using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DevBridge2;
using DevBridge2.BridgeTools;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestDuplicateLaunchOwnership()
    {
        using Fixture fixture = Fixture.MaintenanceWithLease();
        fixture.Adapter.ReadyOnLaunch = true;

        Task<int>[] requests = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => fixture.State.Execute(
                Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true)))
            .ToArray();
        Task.WaitAll(requests);

        Assert(requests.All(value => value.Result == 0), "same-owner duplicate ensure requests must be idempotent");
        Assert(fixture.Adapter.LaunchCalls == 1, "fifty duplicate requests must have exactly one launch attempt");
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "holder", 77), 0,
            Array.Empty<string>());
        Assert(response.LaunchOwner == null && response.ActiveTests == 1,
            "completed launch ownership must not leave an orphan owner or lease");
    }

    private static void TestFiniteRecovery()
    {
        using Fixture exhausted = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.DRAINING,
            TargetGeneration = 2,
            RestartPending = true,
            LaunchOwner = "recovery-owner@1",
            LaunchRequestKey = "restart-2",
            LaunchBudgetRemaining = 0
        });
        exhausted.State.StartRecoveryWork();
        JsonCommandResponse exhaustedResponse = exhausted.State.CreateJsonResponse(Request("status"), 0,
            Array.Empty<string>());
        Assert(exhaustedResponse.ErrorCode == "LAUNCH_BUDGET_EXHAUSTED" &&
            exhausted.Adapter.LaunchCalls == 0, "exhausted launch budget must prevent recovery launch");
    }

    private static void TestDurableLeaseWait()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Adapter.ReadyOnLaunch = true;
        Task<int> restart = Task.Run(() => fixture.State.Execute(
            Request("restart", "restart-agent", 90), _ => { }, () => true));

        Assert(SpinWait.SpinUntil(() => fixture.State.CreateJsonResponse(Request("status"), 0,
                Array.Empty<string>()).State == "WAITING_FOR_BRIDGE", TimeSpan.FromSeconds(2)),
            "restart must enter a durable lease wait while the owned process is running");

        Task<int> status = Task.Run(() => fixture.State.Execute(Request("status", "diagnostic", 91), _ => { }, () => true));
        bool statusCompleted = status.Wait(TimeSpan.FromSeconds(2));
        Task<int> doctor = Task.Run(() => fixture.State.Execute(Request("doctor", "diagnostic", 91), _ => { }, () => true));
        bool doctorCompleted = doctor.Wait(TimeSpan.FromSeconds(2));
        ConcurrentQueue<string> waitReadyOutput = new();
        Task<int> waitReady = Task.Run(() => fixture.State.Execute(
            Request("wait-ready", "diagnostic", 91), waitReadyOutput.Enqueue, () => true));
        bool waitReadyStarted = SpinWait.SpinUntil(
            () => waitReadyOutput.Any(value => value.StartsWith("Waiting for RimWorld generation", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        JsonCommandResponse waiting = fixture.State.CreateJsonResponse(Request("status"), 0,
            Array.Empty<string>());
        Assert(waiting.State == "WAITING_FOR_BRIDGE" && waiting.RestartPending &&
            waiting.ErrorCode == null && fixture.Adapter.LaunchCalls == 0,
            "lease wait must not become a terminal timeout");
        Assert(waiting.RestartQueued, "waiting JSON must identify the queued restart");
        Assert(waiting.NextLeaseExpirationUtc == ClockStart.AddMinutes(2), "waiting JSON must identify the next lease expiration");
        Assert(waiting.RetryAfterSeconds == 60, "waiting JSON must identify the numeric retry timing");
        Assert(waiting.NextAction.Contains("queued", StringComparison.OrdinalIgnoreCase) &&
            waiting.NextAction.Contains("expire", StringComparison.OrdinalIgnoreCase),
            "waiting JSON next action must explain queued ownership and expiration");

        Assert(fixture.State.Execute(Request("test", "holder", 77, "end", "T001"), _ => { }, () => true) == 0,
            "lease holder must be able to release the queued restart");
        Assert(restart.Wait(TimeSpan.FromSeconds(2)) && restart.Result == 0 && fixture.Adapter.LaunchCalls == 1,
            "queued restart must resume exactly once after the lease is released");
        Assert(statusCompleted && doctorCompleted && waitReadyStarted,
            "status, doctor, and wait-ready must remain callable while restart waits on a lease");
        Assert(waitReady.Wait(TimeSpan.FromSeconds(2)) && waitReady.Result == 0,
            "wait-ready must complete after the queued restart becomes ready");
    }

    private static void TestConnectedLeaseSession()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        List<string> output = new();
        DateTime ownerStopsUtc = ClockStart.AddMinutes(5);
        int result = fixture.State.Execute(Request("test", "session-owner", 501, "session"), output.Add,
            () => fixture.Clock.UtcNow < ownerStopsUtc);

        Assert(result == 0, "a connected lease session must end cleanly when its owner disconnects");
        Assert(output.Any(line => line.StartsWith("Test lease heartbeat:", StringComparison.Ordinal)),
            "a connected lease session must emit regular heartbeat progress");

        JsonCommandResponse active = fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        Assert(active.ActiveTests == 1, "regular session heartbeats must keep a long-running test alive");
        Assert(active.Leases[0].LastHeartbeatUtc == ClockStart.AddMinutes(4).AddSeconds(30),
            "the connected session must heartbeat on the configured cadence");
    }

    private static void TestStoppedLeaseSessionExpires()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        DateTime ownerStopsUtc = ClockStart.AddSeconds(45);
        int result = fixture.State.Execute(Request("test", "crashed-owner", 502, "session"), _ => { },
            () => fixture.Clock.UtcNow < ownerStopsUtc);
        Assert(result == 0, "a crashed or cancelled session must stop without a terminal coordinator error");

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        JsonCommandResponse expired = fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        Assert(expired.ActiveTests == 0,
            "once the session owner stops, the lease must expire within the bounded interval");
    }

    private static void TestLeaseHeartbeatAndAuthorization()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Clock.Advance(TimeSpan.FromSeconds(90));
        int wrongAgentRenew = fixture.State.Execute(Request("test", "other", 78, "renew", "T001"), _ => { }, () => true);
        int wrongAgentEnd = fixture.State.Execute(Request("test", "other", 78, "end", "T001"), _ => { }, () => true);
        Assert(wrongAgentRenew != 0, "another agent must not renew a test lease");
        Assert(wrongAgentEnd != 0, "another agent must not end a test lease");

        int renewed = fixture.State.Execute(Request("test", "holder", 78, "renew", "T001"), _ => { }, () => true);
        Assert(renewed == 0, "an active lease must be renewable by its stable agent identity");

        fixture.Clock.Advance(TimeSpan.FromSeconds(119));
        JsonCommandResponse active = fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        Assert(active.ActiveTests == 1, "a renewed lease must survive its previous expiration time");

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        JsonCommandResponse expired = fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        Assert(expired.ActiveTests == 0, "a lease with no further heartbeat must eventually expire");
    }

    private static void TestOrphanLeaseExpiry()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Adapter.ReadyOnLaunch = true;
        Task<int> restart = Task.Run(() => fixture.State.Execute(
            Request("restart", "restart-agent", 90), _ => { }, () => true));

        Assert(SpinWait.SpinUntil(() => fixture.State.CreateJsonResponse(Request("status"), 0,
                Array.Empty<string>()).State == "WAITING_FOR_BRIDGE", TimeSpan.FromSeconds(2)),
            "restart must wait on the initial orphaned lease");
        fixture.Clock.Advance(TimeSpan.FromSeconds(119));
        JsonCommandResponse stillBlocked = fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        Assert(stillBlocked.State == "WAITING_FOR_BRIDGE" && stillBlocked.ActiveTests == 1 &&
            fixture.Adapter.LaunchCalls == 0, "an unexpired lease must still block the owned process restart");
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        // The production coordinator prunes expired leases when it observes
        // ordinary status traffic and pulses the restart worker.  Drive that
        // same bounded wake-up explicitly so this test does not depend on a
        // one-second scheduler timeout under a loaded CI runner.
        fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());

        Assert(restart.Wait(TimeSpan.FromSeconds(2)) && restart.Result == 0 && fixture.Adapter.LaunchCalls == 1,
            "an abandoned lease must release the queued restart within the bounded interval");
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        Assert(response.State == "READY" && response.ActiveTests == 0,
            "the expired orphan lease must be removed before the replacement is ready");
    }

    private static void TestMultipleSharedLeases()
    {
        using Fixture fixture = Fixture.ReadyWithLeases();
        fixture.Adapter.ReadyOnLaunch = true;
        Task<int> restart = Task.Run(() => fixture.State.Execute(
            Request("restart", "restart-agent", 90), _ => { }, () => true));

        Assert(SpinWait.SpinUntil(() => fixture.State.CreateJsonResponse(Request("status"), 0,
                Array.Empty<string>()).State == "WAITING_FOR_BRIDGE", TimeSpan.FromSeconds(2)),
            "shared leases must block restart while the owned process is running");
        fixture.State.Execute(Request("test", "holder-a", 77, "end", "T001"), _ => { }, () => true);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert(fixture.Adapter.LaunchCalls == 0,
            "ending one shared lease must not release a restart blocked by another lease");

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        Assert(restart.Wait(TimeSpan.FromSeconds(2)) && restart.Result == 0 && fixture.Adapter.LaunchCalls == 1,
            "the queued restart must resume once after the final shared lease expires");
        Assert(fixture.State.Execute(Request("status"), _ => { }, () => true) == 0,
            "status must remain responsive while shared lease contention drains");
    }

    private static void TestLeaseJsonTiming()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        JsonCommandResponse initial = fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        JsonLeaseInfo lease = initial.Leases.Single();
        DateTime expectedExpiry = ClockStart.AddMinutes(2);
        Assert(lease.LastHeartbeatUtc == ClockStart && lease.ExpiresUtc == expectedExpiry &&
            lease.RetryAfterSeconds == 120, "lease JSON must expose exact fake-clock heartbeat and retry timing");
        Assert(initial.NextLeaseExpirationUtc == expectedExpiry && initial.RetryAfterSeconds == 120,
            "top-level JSON must expose exact next lease expiration and retry timing");
        string serialized = JsonSerializer.Serialize(initial, Program.JsonOptions);
        Assert(!serialized.Contains("staleIn", StringComparison.Ordinal),
            "machine-readable lease JSON must not require parsing the staleIn display string");

        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        JsonCommandResponse later = fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        Assert(later.Leases.Single().RetryAfterSeconds == 89,
            "lease retry timing must remain numeric and exact after fake-clock advancement");
    }

    private static void TestMissingProcessRelaunchWithLease()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.STOPPED,
            ErrorCode = "PROCESS_EXITED",
            Error = "The coordinator-owned RimWorld process is no longer running.",
            RequiresNewProcess = true,
            Leases = new List<TestLease> { new() { Id = "T001", Agent = "holder", ClientProcessId = 77,
                Generation = 1, StartedUtc = ClockStart } }
        });
        fixture.Adapter.ReadyOnLaunch = true;

        List<string> output = new();
        int exitCode = fixture.State.Execute(Request("restart", "restart-agent", 90), output.Add, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "holder", 77), exitCode,
            Array.Empty<string>());
        Assert(exitCode == 0 && response.State == "READY" && response.Generation == 2 &&
            response.ActiveTests == 1 && fixture.Adapter.LaunchCalls == 1 &&
            fixture.Adapter.TerminationRequests == 0,
            "an absent process must relaunch once without discarding or waiting on the active lease (exit " +
            exitCode + ", state " + response.State + ", generation " + response.Generation + ", tests " +
            response.ActiveTests + ", launches " + fixture.Adapter.LaunchCalls + ", terminations " +
            fixture.Adapter.TerminationRequests + ", output: " + string.Join(" | ", output) + ")");
    }

    private static void TestLegacyLeaseWaitRecovery()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 202,
            Phase = BridgePhase.ERROR,
            ErrorCode = "WAITING_FOR_BRIDGE_EXPIRED",
            Error = "The durable WAITING_FOR_BRIDGE deadline expired; no launch was attempted.",
            ProcessId = 34208,
            ProcessStartUtcTicks = 639221723214541368,
            RestartPending = false,
            LaunchAttemptCount = 0,
            LaunchBudgetRemaining = 2,
            RequiresNewProcess = true,
            Leases = new List<TestLease> { new() { Id = "9F8D", Agent = "agent-4D8C", ClientProcessId = 19852,
                Generation = 202, StartedUtc = ClockStart } }
        });
        fixture.Adapter.ReadyOnLaunch = true;
        fixture.State.StartRecoveryWork();

        Assert(SpinWait.SpinUntil(() => fixture.State.CreateJsonResponse(Request("status"), 0,
                Array.Empty<string>()).State == "READY", TimeSpan.FromSeconds(2)),
            "legacy terminal lease wait must autonomously resume");
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status"), 0,
            Array.Empty<string>());
        Assert(response.Generation == 203 && response.ActiveTests == 1 && response.ErrorCode == null &&
            fixture.Adapter.LaunchCalls == 1 && fixture.Adapter.TerminationRequests == 0,
            "legacy recovery must launch generation 203 exactly once and preserve the lease");
    }

    private static void TestDuplicateRestartOwnership()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.Adapter.ReadyOnLaunch = true;
        fixture.Adapter.BlockWaitForExit = true;
        BridgeRequest request = Request("restart", "restart-agent", 90);
        Task<int> first = Task.Factory.StartNew(() => fixture.State.Execute(request, _ => { }, () => true),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert(fixture.Adapter.TerminationRequested.Wait(TimeSpan.FromSeconds(10)),
            "restart did not reach the identity-checked stop");

        Task<int>[] duplicates = Enumerable.Range(0, 49)
            .Select(_ => Task.Factory.StartNew(() => fixture.State.Execute(Request("restart", "restart-agent", 90), _ => { }, () => true),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        fixture.Adapter.ReleaseWait.Set();
        Assert(first.Wait(TimeSpan.FromSeconds(10)), "primary restart did not finish");
        Assert(Task.WaitAll(duplicates, TimeSpan.FromSeconds(10)), "duplicate restarts did not finish");
        Assert(first.Result == 0 && duplicates.All(value => value.Result == 0),
            "same-owner duplicate restarts must be idempotent");
        Assert(fixture.Adapter.LaunchCalls == 1, "fifty duplicate restarts must have exactly one launch attempt");
    }

    private static void TestCompetingRestartOwners()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.Adapter.ReadyOnLaunch = true;
        fixture.Adapter.BlockWaitForExit = true;
        Task<int> primary = Task.Factory.StartNew(() => fixture.State.Execute(Request("restart", "owner-a", 90), _ => { }, () => true),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert(fixture.Adapter.TerminationRequested.Wait(TimeSpan.FromSeconds(10)),
            "primary owner did not acquire the restart slot");
        Task<int> competing = Task.Factory.StartNew(
            () => fixture.State.Execute(Request("restart", "owner-b", 91), _ => { }, () => true),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Thread.Sleep(100);
        PersistedState pending = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        Assert(pending?.LaunchOwner == "owner-a@90", "competing owner overwrote launch provenance");
        fixture.Adapter.ReleaseWait.Set();
        Assert(primary.Wait(TimeSpan.FromSeconds(10)) && primary.Result == 0, "primary restart did not finish");
        Assert(competing.Wait(TimeSpan.FromSeconds(10)) && competing.Result == 4,
            "a competing owner must be rejected while the slot is pending");
        Assert(fixture.Adapter.LaunchCalls == 1, "competing owner must not create a second launch");
    }

    private static void TestCrashRecoveryNoDuplicateLaunch()
    {
        using Fixture ambiguous = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.LOADING,
            TargetGeneration = 2,
            RestartPending = true,
            LaunchOwner = "owner-a@90",
            LaunchRequestKey = "restart-2",
            LaunchBudgetRemaining = 2,
            ProcessId = 0,
            ProcessStartUtcTicks = 0
        });
        ambiguous.State.StartRecoveryWork();
        JsonCommandResponse response = ambiguous.State.CreateJsonResponse(Request("status"), 0,
            Array.Empty<string>());
        Assert(response.ErrorCode == "LAUNCH_RECOVERY_AMBIGUOUS" && ambiguous.Adapter.LaunchCalls == 0,
            "reconnect without an exact process identity must fail closed without relaunching");

        using Fixture monitored = Fixture.LoadingWithLease();
        monitored.State.StartRecoveryWork();
        Assert(monitored.Adapter.LaunchCalls == 0, "recovery monitoring must not invoke the launcher");
    }

    private static void TestScopeMetadata()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        BridgeRequest request = Request("status", "scope-agent", 91);
        request.GoalId = "goal-7";
        request.WakeId = "wake-8";
        request.McpRequestId = "mcp-9";
        JsonCommandResponse response = fixture.State.CreateJsonResponse(request, 0, Array.Empty<string>());
        Assert(response.GoalId == "goal-7" && response.WakeId == "wake-8" && response.McpRequestId == "mcp-9",
            "scope metadata was not preserved through the coordinator response");
    }

    private static void TestRuntimeScopeBinding()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevBridge2-scope-" + Guid.NewGuid().ToString("N"));
        string other = Path.Combine(root, "other");
        ParsedArguments separated = ParsedArguments.Parse(new[] { "--root", root, "--coordinator-root", root, "status" });
        ParsedArguments equals = ParsedArguments.Parse(new[] { "--coordinator-root=" + root, "status" });
        Assert(RuntimeScope.PathsEqual(separated.Root, root) && RuntimeScope.PathsEqual(separated.CoordinatorRoot, root),
            "separated coordinator-root form must bind to root");
        Assert(RuntimeScope.PathsEqual(equals.Root, root) && RuntimeScope.PathsEqual(equals.CoordinatorRoot, root),
            "equals coordinator-root form must bind to root");

        bool mismatchRejected = false;
        try { ParsedArguments.Parse(new[] { "--root", root, "--coordinator-root", other, "status" }); }
        catch (ArgumentException) { mismatchRejected = true; }
        Assert(mismatchRejected, "mismatched command-line roots must be rejected");

        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.WriteState(new PersistedState
        {
            CoordinatorRoot = other,
            RuntimeSlotId = "slot-other",
            Generation = 1,
            Phase = BridgePhase.READY
        });
        bool persistedMismatchRejected = false;
        try { fixture.Reload(); }
        catch (InvalidOperationException) { persistedMismatchRejected = true; }
        Assert(persistedMismatchRejected, "a persisted root mismatch must be rejected even if the legacy path is absent");
    }

    private static void TestTicketRouting()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.WriteState(new PersistedState
        {
            CoordinatorRoot = fixture.Root,
            RuntimeSlotId = RuntimeScope.ForRoot(fixture.Root),
            Generation = 1,
            Phase = BridgePhase.READY,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            ScopeTickets = new List<ScopeTicket>
            {
                new() { Id = "ticket-1", CoordinatorRoot = fixture.Root,
                    RuntimeSlotId = RuntimeScope.ForRoot(fixture.Root) }
            }
        });
        ParsedArguments ticketArguments = ParsedArguments.Parse(new[]
            { "--root", fixture.Root, "--ticket", "ticket-1", "status" });
        ParsedArguments ticketEqualsArguments = ParsedArguments.Parse(new[]
            { "--root=" + fixture.Root, "--ticket=ticket-1", "status" });
        Assert(ticketArguments.TicketId == "ticket-1" && ticketArguments.RuntimeSlotId == null &&
            ticketEqualsArguments.TicketId == "ticket-1" && ticketEqualsArguments.RuntimeSlotId == null,
            "ticket-only CLI requests must preserve the ticket without inventing a root-derived slot");
        Assert(RuntimeScope.ResolveTicketSlot(fixture.Root, "ticket-1") == RuntimeScope.ForRoot(fixture.Root),
            "ticket-only startup must resolve the persisted slot before connecting");
        Assert(PipeNames.ForSlot(fixture.Root, "slot-a") != PipeNames.ForSlot(fixture.Root, "slot-b"),
            "different runtime slots must have distinct coordinator pipe endpoints");
        fixture.State = fixture.Reload();
        BridgeRequest request = Request("status", "ticket-agent", 88);
        request.TicketId = "ticket-1";
        int exitCode = fixture.State.Execute(request, _ => { }, () => true);
        Assert(exitCode == 0, "ticket-only routing must resolve its durable authoritative slot");
        Assert(fixture.Adapter.LaunchCalls == 0 && fixture.Adapter.TerminationRequests == 0,
            "ticket routing must not create lifecycle side effects");

        BridgeRequest conflicting = Request("status", "ticket-agent", 88);
        conflicting.TicketId = "ticket-1";
        conflicting.CoordinatorRoot = "C:\\wrong-root";
        conflicting.RuntimeSlotId = "slot-wrong";
        Assert(fixture.State.Execute(conflicting, _ => { }, () => true) == 4,
            "ticket scope conflicts must be rejected rather than silently rewritten");
    }

}
