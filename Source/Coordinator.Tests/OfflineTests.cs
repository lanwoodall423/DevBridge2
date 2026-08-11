using System.Text.Json;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static class OfflineTests
{
    private static readonly DateTime ClockStart = new(2026, 8, 11, 16, 0, 0, DateTimeKind.Utc);
    private static int failures;

    private static int Main()
    {
        Run("readiness timeout retains process and accepts late same-process readiness", TestReadinessTimeoutContract);
        Run("stop authorization and identity checks", TestStopAuthorization);
        Run("stop requires confirmed exit and fails closed", TestStopFailsClosed);
        Run("stop succeeds, retains lease, marks dirty, and makes no launches", TestSuccessfulStop);
        Run("maintenance state has no background launch and expiry leaves it stopped", TestMaintenanceNoLaunch);
        Run("other test holders queue during maintenance", TestMaintenanceQueue);
        Run("stop serializes ensure-ready and restart", TestStopSerialization);
        Run("ensure-ready launches exactly once after maintenance", TestEnsureReadyLaunch);
        Run("restart retains immediate launch behavior", TestImmediateRestart);
        Run("duplicate stop is idempotent", TestDuplicateStop);
        Run("process inspection uncertainty fails closed", TestInspectionFailsClosed);
        Run("maintenance claims are freshly re-enumerated", TestMaintenanceRevalidation);
        Run("uncertain maintenance operations make no adapter calls", TestMaintenanceInspectionNoLaunch);
        Run("status uses one authoritative process snapshot", TestStatusSnapshotConsistency);
        Run("duplicate launch requests have one slot owner", TestDuplicateLaunchOwnership);
        Run("fifty duplicate restart requests have one launch", TestDuplicateRestartOwnership);
        Run("competing restart owners cannot overwrite provenance", TestCompetingRestartOwners);
        Run("recovery budget and waiting deadline are finite", TestFiniteRecovery);
        Run("crash recovery never duplicates an ambiguous launch", TestCrashRecoveryNoDuplicateLaunch);
        Run("root and runtime slot bindings are authoritative", TestRuntimeScopeBinding);
        Run("ticket routing preserves its durable slot", TestTicketRouting);
        Run("goal wake and MCP scope metadata is preserved", TestScopeMetadata);
        Run("quicktest activation is ordered and bounded", TestQuicktestActivation);
        Run("coordinator-root argument forms are accepted", TestCoordinatorRootArgumentForms);
        Run("wrapper propagates native exit codes", DevBridgeWrapperTests.Run);

        Console.WriteLine(failures == 0 ? "OFFLINE TESTS PASS" : "OFFLINE TESTS FAIL: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine("FAIL " + name + ": " + exception.Message);
        }
    }

    private static void TestReadinessTimeoutContract()
    {
        using Fixture fixture = Fixture.LoadingWithLease();
        BridgeRequest wait = Request("wait-ready", "waiter", 88);
        List<string> output = new();
        int exitCode = fixture.State.Execute(wait, output.Add, () => true);
        JsonCommandResponse timedOut = fixture.State.CreateJsonResponse(wait, exitCode, output);

        Assert(exitCode != 0, "the original wait must fail");
        Assert(output.Any(line => line.Contains("READINESS_TIMEOUT", StringComparison.Ordinal)),
            "the original wait must report READINESS_TIMEOUT");
        Assert(timedOut.ErrorCode == "READINESS_TIMEOUT", "JSON state must expose READINESS_TIMEOUT");
        Assert(fixture.Adapter.LaunchCalls == 0, "timeout must make zero replacement launch calls");
        Assert(timedOut.RimWorldPid == 101 && timedOut.RimWorldProcessStartIdentity == 1001,
            "timeout must retain the exact PID/start identity");
        Assert(timedOut.LaunchGeneration == 1, "timeout must retain launch generation");

        fixture.Adapter.Replace(101, 1002);
        fixture.WriteReadiness("launch-1", 1, 101);
        int rejected = fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true);
        Assert(rejected != 0, "different process start identity must be rejected");
        Assert(fixture.Adapter.LaunchCalls == 0, "rejected readiness must not launch");

        fixture.Adapter.Replace(101, 1001);
        int accepted = fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), output.Add, () => true);
        Assert(accepted == 0, "late readiness from the original process must be accepted");
        Assert(fixture.Adapter.LaunchCalls == 0, "late readiness acceptance must not launch");

        int reused = fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), output.Add, () => true);
        Assert(reused == 0, "next ensure-ready must reuse the ready process");
        Assert(fixture.Adapter.LaunchCalls == 0, "next ensure-ready must make zero launch calls");
    }

    private static void TestStopAuthorization()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        int missing = fixture.State.Execute(Request("stop", "holder", 77, "MISSING"), _ => { }, () => true);
        Assert(missing != 0 && fixture.Adapter.TerminationRequests == 0, "missing token must not stop");

        int nonHolder = fixture.State.Execute(Request("stop", "other", 78, "T001"), _ => { }, () => true);
        Assert(nonHolder != 0 && fixture.Adapter.TerminationRequests == 0, "non-holder must not stop");

        fixture.State = fixture.ReloadWithLease(ClockStart.AddHours(-2));
        int expired = fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        Assert(expired != 0 && fixture.Adapter.TerminationRequests == 0, "expired token must not stop");
    }

    private static void TestStopFailsClosed()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Adapter.Current.WaitExits = false;
        int exitCode = fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "holder", 77), exitCode, Array.Empty<string>());
        Assert(exitCode != 0 && response.ErrorCode == "STOP_FAILED", "unconfirmed exit must fail structurally");
        Assert(!response.MaintenanceReady, "failed stop must not claim maintenance safety");
        Assert(fixture.Adapter.LaunchCalls == 0, "failed stop must not launch");

        using Fixture ambiguous = Fixture.ReadyWithLease();
        ambiguous.Adapter.ExtraMatchingProcess = true;
        int ambiguousExit = ambiguous.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse ambiguousResponse = ambiguous.State.CreateJsonResponse(Request("status", "holder", 77), ambiguousExit, Array.Empty<string>());
        Assert(ambiguousExit != 0 && !ambiguousResponse.MaintenanceReady,
            "ambiguous post-stop enumeration must fail closed");

        using Fixture pidZero = Fixture.ReadyWithLease();
        pidZero.State = pidZero.ReloadWithLease(ClockStart);
        pidZero.WriteState(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.STOPPED,
            MaintenanceReady = false,
            ProcessId = 0,
            ProcessStartUtcTicks = 0,
            Leases = new List<TestLease> { pidZero.Lease(ClockStart) }
        });
        pidZero.State = pidZero.Reload();
        int pidZeroExit = pidZero.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse pidZeroResponse = pidZero.State.CreateJsonResponse(Request("status", "holder", 77), pidZeroExit, Array.Empty<string>());
        Assert(pidZeroExit != 0 && !pidZeroResponse.MaintenanceReady,
            "PID zero alone must never establish maintenanceReady");
    }

    private static void TestSuccessfulStop()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        int exitCode = fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "holder", 77), exitCode, Array.Empty<string>());
        Assert(exitCode == 0 && response.State == "STOPPED", "stop must return stopped");
        Assert(response.MaintenanceReady && response.LeaseState == "HELD", "stop must retain lease and safety state");
        Assert(response.SessionDirty, "stop must mark the session dirty");
        Assert(fixture.Adapter.LaunchCalls == 0, "stop must make zero launch calls");
        Assert(fixture.Adapter.TerminationRequests == 1 && fixture.Adapter.Current.HasExited,
            "stop must request and confirm exact process exit");
    }

    private static void TestMaintenanceNoLaunch()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "fixture stop must succeed");
        int launchCount = fixture.Adapter.LaunchCalls;
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        fixture.State.Execute(Request("status", "other", 88), _ => { }, () => true);
        fixture.State.Execute(Request("wait-ready", "other", 88), _ => { }, () => true);
        Assert(fixture.Adapter.LaunchCalls == launchCount, "maintenance state must not background-launch");
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "other", 88), 0, Array.Empty<string>());
        Assert(response.State == "STOPPED" && response.MaintenanceReady && response.SessionDirty && response.ActiveTests == 0,
            "lease expiry must leave the game stopped and dirty without launching");
    }

    private static void TestStopSerialization()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Adapter.BlockWaitForExit = true;
        Task<int> stop = Task.Run(() => fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true));
        Assert(fixture.Adapter.TerminationRequested.Wait(TimeSpan.FromSeconds(2)), "stop did not reach termination");

        Task<int> restart = Task.Run(() => fixture.State.Execute(Request("restart"), _ => { }, () => true));
        Thread.Sleep(100);
        Task<int> ensure = Task.Run(() => fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true));
        Assert(!ensure.IsCompleted && !restart.IsCompleted && fixture.Adapter.LaunchCalls == 0,
            "ensure-ready/restart must not launch during stop");

        fixture.Adapter.ReleaseWait.Set();
        Assert(stop.Wait(TimeSpan.FromSeconds(2)), "stop did not complete");
        Assert(ensure.Wait(TimeSpan.FromSeconds(2)), "ensure-ready did not complete after stop");
        Assert(restart.Wait(TimeSpan.FromSeconds(2)), "restart did not complete after stop");
        Assert(fixture.Adapter.LaunchCalls == 1, "only explicit ensure-ready may launch after maintenance");
    }

    private static void TestMaintenanceQueue()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "fixture stop must succeed");

        Task<int> queued = Task.Run(() => fixture.State.Execute(
            Request("test", "other", 88, "begin"), _ => { }, () => true));
        Thread.Sleep(100);
        Assert(!queued.IsCompleted && fixture.Adapter.LaunchCalls == 0,
            "other test holders must wait without launching during maintenance");

        fixture.Adapter.ReadyOnLaunch = true;
        Assert(fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "lease holder must be able to release maintenance");
        Assert(queued.Wait(TimeSpan.FromSeconds(2)) && queued.Result == 0,
            "queued test holder must acquire after ensure-ready");
    }

    private static void TestEnsureReadyLaunch()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        fixture.Adapter.ReadyOnLaunch = true;
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "fixture stop must succeed");
        int exitCode = fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "holder", 77), exitCode, Array.Empty<string>());
        Assert(exitCode == 0 && response.State == "READY", "ensure-ready must reach ready");
        Assert(fixture.Adapter.LaunchCalls == 1, "ensure-ready must perform exactly one launch");
        Assert(fixture.Adapter.LastLaunchArguments.Count == 0 &&
            fixture.Adapter.LastLaunchEnvironment.TryGetValue("DEVBRIDGE_QUICKTEST_REQUESTED", out string requested) &&
            requested == "1", "launch must use normal startup with built-in quicktest activation");
        Assert(response.ActiveTests == 1 && response.LeaseState == "HELD", "maintenance lease must survive ensure-ready");
    }

    private static void TestImmediateRestart()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = fixture.State.Execute(Request("restart"), _ => { }, () => true);
        Assert(exitCode == 0, "existing restart must still complete");
        Assert(fixture.Adapter.LaunchCalls == 1, "restart must make one launch call");
        Assert(fixture.Adapter.LastLaunchArguments.Count == 0, "restart must not use command-line quicktest");
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status"), exitCode, Array.Empty<string>());
        Assert(response.State == "READY" && response.Generation == 2, "restart must produce the next ready generation");
    }

    private static void TestDuplicateStop()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "first stop must succeed");
        int terminations = fixture.Adapter.TerminationRequests;
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "duplicate stop must be idempotent");
        Assert(fixture.Adapter.TerminationRequests == terminations && fixture.Adapter.LaunchCalls == 0,
            "duplicate stop must not terminate or launch again");
    }

    private static void TestInspectionFailsClosed()
    {
        foreach (Action<FakeProcess> configure in new Action<FakeProcess>[]
        {
            process => process.ThrowOnExecutablePath = true,
            process => process.ThrowOnHasExited = true,
            process => process.ThrowOnStartIdentity = true
        })
        {
            using Fixture fixture = Fixture.MaintenanceWithLease();
            FakeProcess candidate = new(501, 5001, fixture.RimWorldPath);
            fixture.Adapter.Add(candidate);
            configure(candidate);

            int exitCode = fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
            JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status", "holder", 77), exitCode,
                Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == ProcessInspection.ErrorCode,
                "inspection uncertainty must be structured as ambiguous process state");
            Assert(!response.MaintenanceReady, "inspection uncertainty must not be copy-safe");
            Assert(fixture.Adapter.TerminationRequests == 0 && fixture.Adapter.LaunchCalls == 0,
                "inspection uncertainty must make zero termination and launch calls");
        }
    }

    private static void TestMaintenanceRevalidation()
    {
        using Fixture fixture = Fixture.MaintenanceWithLease();
        int beforeStop = fixture.Adapter.EnumerationCalls;
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "clean duplicate stop must remain idempotent");
        Assert(fixture.Adapter.EnumerationCalls == beforeStop + 1,
            "duplicate stop must freshly enumerate the installation");

        int beforeStatus = fixture.Adapter.EnumerationCalls;
        List<string> cleanOutput = new();
        BridgeRequest statusRequest = Request("status", "holder", 77);
        int cleanExit = fixture.State.Execute(statusRequest, cleanOutput.Add, () => true);
        JsonCommandResponse clean = fixture.State.CreateJsonResponse(statusRequest, cleanExit, cleanOutput);
        Assert(fixture.Adapter.EnumerationCalls == beforeStatus + 1,
            "status must freshly enumerate before reporting maintenanceReady=true");
        Assert(cleanExit == 0 && clean.MaintenanceReady &&
            !cleanOutput.Any(value => value.Contains("WARNING", StringComparison.Ordinal)),
            "clean status must preserve maintenanceReady without a warning");

        fixture.Adapter.ExtraMatchingProcess = true;
        int beforeAppeared = fixture.Adapter.EnumerationCalls;
        List<string> appearedOutput = new();
        int appearedExit = fixture.State.Execute(statusRequest, appearedOutput.Add, () => true);
        JsonCommandResponse appeared = fixture.State.CreateJsonResponse(statusRequest, appearedExit, appearedOutput);
        Assert(fixture.Adapter.EnumerationCalls == beforeAppeared + 1,
            "status must use one authoritative enumeration");
        Assert(!appeared.MaintenanceReady && appeared.ErrorCode == "MAINTENANCE_PROCESS_PRESENT",
            "a process appearing after persistence must invalidate maintenanceReady");
        Assert(appearedOutput.Any(value => value.Contains("unmanaged RimWorld process", StringComparison.Ordinal)) &&
            !appearedOutput.Any(value => value.Contains("confirmed safe", StringComparison.OrdinalIgnoreCase)),
            "status must not pair a process warning with a positive safety claim");

        fixture.State = fixture.Reload();
        JsonCommandResponse persisted = fixture.State.CreateJsonResponse(statusRequest, appearedExit, appearedOutput);
        Assert(!persisted.MaintenanceReady,
            "status invalidation must be persisted before the response");
        Assert(fixture.Adapter.TerminationRequests == 0 && fixture.Adapter.LaunchCalls == 0,
            "revalidation must not terminate or launch a newly discovered process");
    }

    private static void TestStatusSnapshotConsistency()
    {
        using Fixture fixture = Fixture.MaintenanceWithLease();
        fixture.Adapter.AddExtraMatchingProcessOnSecondEnumeration = true;
        BridgeRequest request = Request("status", "holder", 77);
        List<string> output = new();
        int exitCode = fixture.State.Execute(request, output.Add, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(request, exitCode, output);

        Assert(exitCode == 0 && response.MaintenanceReady,
            "a clean authoritative snapshot must report maintenanceReady");
        Assert(fixture.Adapter.EnumerationCalls == 1,
            "status must not perform a second independent enumeration");
        Assert(fixture.Adapter.TerminationRequests == 0 && fixture.Adapter.LaunchCalls == 0,
            "status snapshotting must make zero termination and launch calls");
    }

    private static void TestMaintenanceInspectionNoLaunch()
    {
        using (Fixture statusFixture = Fixture.MaintenanceWithLease())
        {
            statusFixture.Adapter.EnumerationIncomplete = true;
            int statusExit = statusFixture.State.Execute(Request("status", "holder", 77), _ => { }, () => true);
            JsonCommandResponse status = statusFixture.State.CreateJsonResponse(Request("status", "holder", 77), statusExit,
                Array.Empty<string>());
            Assert(statusExit == 0 && !status.MaintenanceReady &&
                status.ErrorCode == ProcessInspection.ErrorCode,
                "status must report persisted maintenance state as non-copy-safe when re-enumeration is uncertain");
            Assert(statusFixture.Adapter.TerminationRequests == 0 && statusFixture.Adapter.LaunchCalls == 0,
                "uncertain status reconciliation must make zero termination and launch calls");
        }

        using (Fixture ensureFixture = Fixture.MaintenanceWithLease())
        {
            ensureFixture.Adapter.EnumerationIncomplete = true;
            int ensure = ensureFixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true);
            Assert(ensure != 0 && ensureFixture.Adapter.TerminationRequests == 0 &&
                ensureFixture.Adapter.LaunchCalls == 0,
                "uncertain ensure-ready must make zero termination and launch calls");
        }

        using (Fixture restartFixture = Fixture.MaintenanceWithLease())
        {
            restartFixture.Adapter.EnumerationIncomplete = true;
            int restart = restartFixture.State.Execute(Request("restart", "holder", 77, "T001"), _ => { }, () => true);
            Assert(restart != 0 && restartFixture.Adapter.TerminationRequests == 0 &&
                restartFixture.Adapter.LaunchCalls == 0,
                "uncertain restart must make zero termination and launch calls");
        }
    }

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
        using Fixture expired = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.DRAINING,
            TargetGeneration = 2,
            RestartPending = true,
            WaitingForBridgeDeadlineUtc = ClockStart.AddSeconds(-1),
            LaunchBudgetRemaining = 2,
            Leases = new List<TestLease> { new() { Id = "T001", Agent = "holder", ClientProcessId = 77,
                Generation = 1, StartedUtc = ClockStart } }
        });
        expired.State.StartRecoveryWork();
        JsonCommandResponse expiredResponse = expired.State.CreateJsonResponse(Request("status"), 0,
            Array.Empty<string>());
        Assert(expiredResponse.ErrorCode == "WAITING_FOR_BRIDGE_EXPIRED" &&
            expired.Adapter.LaunchCalls == 0, "expired WAITING_FOR_BRIDGE must become terminal without launch");

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

    private static void TestQuicktestActivation()
    {
        bool mainMenu = false;
        int activationCalls = 0;
        QuicktestActivationController failure = new(true, () => mainMenu, () =>
        {
            activationCalls++;
            throw new NullReferenceException("simulated Root_Play lifecycle failure");
        }, 3);
        Assert(failure.Tick() == QuicktestActivationResult.WaitingForMainMenu && activationCalls == 0,
            "Quicktest must not activate before genuine main-menu readiness");
        mainMenu = true;
        Assert(failure.Tick() == QuicktestActivationResult.Failed && failure.TerminalFailure && activationCalls == 1,
            "observed built-in activation failure must be bounded and terminal");
        Assert(failure.Tick() == QuicktestActivationResult.Failed && activationCalls == 1,
            "terminal Quicktest failure must not retry or launch");

        int successfulCalls = 0;
        QuicktestActivationController success = new(true, () => mainMenu, () => successfulCalls++, 3);
        Assert(success.Tick() == QuicktestActivationResult.Requested && success.MainMenuReady &&
            success.ActivationRequested && successfulCalls == 1,
            "built-in button activation must follow genuine main-menu readiness");
    }

    private static void TestCoordinatorRootArgumentForms()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevBridge2-argument-" + Guid.NewGuid().ToString("N"));
        ParsedArguments separated = ParsedArguments.Parse(new[] { "--coordinator-root", root, "--json", "status" });
        ParsedArguments equals = ParsedArguments.Parse(new[] { "--coordinator-root=" + root, "--json", "status" });
        Assert(separated.Command.SequenceEqual(new[] { "--json", "status" }) &&
            equals.Command.SequenceEqual(new[] { "--json", "status" }),
            "both coordinator-root forms must preserve command forwarding");
    }

    private static BridgeRequest Request(string command, string agent = "agent", int pid = 1, params string[] arguments)
    {
        return new BridgeRequest
        {
            Command = command,
            Agent = agent,
            ClientProcessId = pid,
            Arguments = arguments?.ToList() ?? new List<string>()
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        internal readonly string RimWorldPath;
        internal readonly FakeClock Clock;
        internal readonly FakeProcessAdapter Adapter;
        internal CoordinatorState State;

        internal Fixture(PersistedState initial)
        {
            Root = Path.Combine(Path.GetTempPath(), "DevBridge2-offline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "Runtime"));
            RimWorldPath = Path.Combine(Root, "RimWorldWin64.exe");
            File.WriteAllText(RimWorldPath, "offline-test-executable");
            File.WriteAllText(Path.Combine(Root, "ModsConfig.xml"), "<activeMods><li>lan.devbridge2</li></activeMods>");
            Clock = new FakeClock(ClockStart);
            Adapter = new FakeProcessAdapter(RimWorldPath, Root, Clock);
            WriteState(initial);
            State = Reload();
        }

        internal TestLease Lease(DateTime started) => new()
        {
            Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1, StartedUtc = started
        };

        internal static Fixture LoadingWithLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 0,
                Phase = BridgePhase.LOADING,
                LaunchId = "launch-1",
                LaunchGeneration = 1,
                TargetGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease>
                {
                    new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 0, StartedUtc = ClockStart }
                }
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture ReadyWithLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease> { new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1, StartedUtc = ClockStart } }
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture ReadyWithoutLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture MaintenanceWithLease()
        {
            return new Fixture(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.STOPPED,
                MaintenanceReady = true,
                SessionDirty = true,
                ProcessId = 0,
                ProcessStartUtcTicks = 0,
                Leases = new List<TestLease> { new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1, StartedUtc = ClockStart } }
            });
        }

        internal CoordinatorState ReloadWithLease(DateTime started)
        {
            WriteState(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease> { Lease(started) }
            });
            Adapter.Replace(101, 1001);
            return Reload();
        }

        internal void WriteReadiness(string launchId, int generation, int processId)
        {
            File.WriteAllText(Path.Combine(Root, "Runtime", "readiness.json"), JsonSerializer.Serialize(new ReadinessRecord
            {
                LaunchId = launchId,
                Generation = generation,
                ProcessId = processId,
                TimestampUtc = Clock.UtcNow
            }, Program.JsonOptions));
        }

        internal void WriteState(PersistedState value)
        {
            File.WriteAllText(Path.Combine(Root, "Runtime", "state.json"), JsonSerializer.Serialize(value, Program.JsonOptions));
        }

        internal CoordinatorState Reload()
        {
            return new CoordinatorState(Root, new CoordinatorOptions
            {
                ReadinessTimeout = TimeSpan.FromSeconds(3),
                ProcessExitTimeout = TimeSpan.FromSeconds(1),
                ProcessAdapter = Adapter,
                Clock = Clock,
                RimWorldExecutablePath = RimWorldPath,
                ModsConfigPath = Path.Combine(Root, "ModsConfig.xml")
            });
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch { }
        }
    }

    private sealed class FakeClock : ICoordinatorClock
    {
        private DateTime now;
        internal FakeClock(DateTime start) => now = start;
        public DateTime UtcNow => now;
        public void Sleep(TimeSpan duration) => now = now.Add(duration);
        internal void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class FakeProcessAdapter : IProcessAdapter
    {
        private readonly string executablePath;
        private readonly string root;
        private readonly FakeClock clock;
        private readonly Dictionary<int, FakeProcess> processes = new();
        private int nextPid = 200;
        private long nextStart = 2000;

        internal int LaunchCalls { get; private set; }
        internal IReadOnlyList<string> LastLaunchArguments { get; private set; } = Array.Empty<string>();
        internal IReadOnlyDictionary<string, string> LastLaunchEnvironment { get; private set; } =
            new Dictionary<string, string>();
        internal int TerminationRequests => processes.Values.Sum(value => value.TerminationRequests);
        internal FakeProcess Current => processes.Values.OrderByDescending(value => value.Id).First();
        internal bool ReadyOnLaunch { get; set; }
        internal bool ExtraMatchingProcess { get; set; }
        internal bool AddExtraMatchingProcessOnSecondEnumeration { get; set; }
        internal bool EnumerationIncomplete { get; set; }
        internal int EnumerationCalls { get; private set; }
        internal bool BlockWaitForExit
        {
            get => Current.BlockWait;
            set
            {
                Current.BlockWait = value;
                Current.WaitSignal = ReleaseWait;
            }
        }
        internal ManualResetEventSlim TerminationRequested { get; } = new(false);
        internal ManualResetEventSlim ReleaseWait { get; } = new(false);

        internal FakeProcessAdapter(string executablePath, string root, FakeClock clock)
        {
            this.executablePath = executablePath;
            this.root = root;
            this.clock = clock;
        }

        internal void Add(FakeProcess process)
        {
            process.TerminationSignal = TerminationRequested;
            processes[process.Id] = process;
        }

        internal void Replace(int id, long startIdentity)
        {
            Add(new FakeProcess(id, startIdentity, executablePath));
        }

        public IManagedProcess Open(int processId)
        {
            processes.TryGetValue(processId, out FakeProcess process);
            return process;
        }

        public ProcessEnumeration EnumerateRimWorld(string configuredPath)
        {
            EnumerationCalls++;
            if (EnumerationIncomplete)
                return new ProcessEnumeration { Complete = false, Error = "simulated inspection failure" };
            if (ExtraMatchingProcess || (AddExtraMatchingProcessOnSecondEnumeration && EnumerationCalls >= 2))
                Add(new FakeProcess(999, 9999, executablePath));

            try
            {
                return new ProcessEnumeration
                {
                    Complete = true,
                    Processes = processes.Values.Where(value => !value.HasExited &&
                        string.Equals(value.ExecutablePath, configuredPath, StringComparison.OrdinalIgnoreCase) &&
                        value.StartIdentity > 0).Cast<IManagedProcess>().ToList()
                };
            }
            catch
            {
                return new ProcessEnumeration { Complete = false, Error = "simulated inspection failure" };
            }
        }

        public IManagedProcess Launch(ProcessLaunchRequest request)
        {
            LaunchCalls++;
            LastLaunchArguments = request.Arguments?.ToArray() ?? Array.Empty<string>();
            LastLaunchEnvironment = new Dictionary<string, string>(request.Environment ??
                new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            FakeProcess process = new(nextPid++, nextStart++, executablePath)
            {
                WaitExits = true
            };
            process.TerminationSignal = TerminationRequested;
            processes[process.Id] = process;
            if (ReadyOnLaunch)
            {
                File.WriteAllText(Path.Combine(root, "Runtime", "readiness.json"), JsonSerializer.Serialize(new ReadinessRecord
                {
                    LaunchId = request.Environment["DEVBRIDGE_LAUNCH_ID"],
                    Generation = int.Parse(request.Environment["DEVBRIDGE_GENERATION"]),
                    ProcessId = process.Id,
                    TimestampUtc = clock.UtcNow
                }, Program.JsonOptions));
            }
            return process;
        }
    }

    private sealed class FakeProcess : IManagedProcess
    {
        internal ManualResetEventSlim WaitSignal { get; set; } = new(true);
        internal int TerminationRequests { get; private set; }
        internal bool WaitExits { get; set; } = true;
        internal bool BlockWait { get; set; }
        internal ManualResetEventSlim TerminationSignal { get; set; }
        private bool exited;

        internal FakeProcess(int id, long startIdentity, string executablePath)
        {
            Id = id;
            this.startIdentity = startIdentity;
            this.executablePath = executablePath;
        }

        public int Id { get; }
        internal bool ThrowOnStartIdentity { get; set; }
        internal bool ThrowOnExecutablePath { get; set; }
        internal bool ThrowOnHasExited { get; set; }
        public long StartIdentity => ThrowOnStartIdentity ? throw new InvalidOperationException("start identity unavailable") : startIdentity;
        public string ExecutablePath => ThrowOnExecutablePath ? throw new InvalidOperationException("path unavailable") : executablePath;
        public bool HasExited => ThrowOnHasExited ? throw new InvalidOperationException("exit state unavailable") : exited;

        private readonly long startIdentity;
        private readonly string executablePath;

        public bool RequestTermination()
        {
            TerminationRequests++;
            TerminationSignal?.Set();
            return true;
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            if (BlockWait)
                WaitSignal.Wait(timeout);
            if (WaitExits)
                exited = true;
            return exited;
        }

        public bool ForceTerminate()
        {
            exited = true;
            return true;
        }

        public void Dispose() { }
    }
}
