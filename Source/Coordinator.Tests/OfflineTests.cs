using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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
        Run("doctor clears a stale inspection quarantine after a zero-process census", TestDoctorRecoversInspectionQuarantine);
        Run("doctor keeps inspection quarantine when the census is not conclusively empty", TestDoctorRecoveryFailsClosed);
        Run("maintenance claims are freshly re-enumerated", TestMaintenanceRevalidation);
        Run("uncertain maintenance operations make no adapter calls", TestMaintenanceInspectionNoLaunch);
        Run("status uses one authoritative process snapshot", TestStatusSnapshotConsistency);
        Run("duplicate launch requests have one slot owner", TestDuplicateLaunchOwnership);
        Run("fifty duplicate restart requests have one launch", TestDuplicateRestartOwnership);
        Run("competing restart owners cannot overwrite provenance", TestCompetingRestartOwners);
        Run("lease-blocked restart waits durably and resumes", TestDurableLeaseWait);
        Run("connected lease sessions heartbeat active tests", TestConnectedLeaseSession);
        Run("stopped lease sessions expire without an orphan heartbeat", TestStoppedLeaseSessionExpires);
        Run("lease heartbeats and stable-agent authorization", TestLeaseHeartbeatAndAuthorization);
        Run("orphaned leases expire without blocking a restart", TestOrphanLeaseExpiry);
        Run("shared leases block restart until the final lease ends", TestMultipleSharedLeases);
        Run("lease JSON reports exact expiration and retry timing", TestLeaseJsonTiming);
        Run("missing process relaunches despite an active lease", TestMissingProcessRelaunchWithLease);
        Run("legacy lease-wait expiry recovers automatically", TestLegacyLeaseWaitRecovery);
        Run("recovery launch budget is finite", TestFiniteRecovery);
        Run("crash recovery never duplicates an ambiguous launch", TestCrashRecoveryNoDuplicateLaunch);
        Run("root and runtime slot bindings are authoritative", TestRuntimeScopeBinding);
        Run("ticket routing preserves its durable slot", TestTicketRouting);
        Run("goal wake and MCP scope metadata is preserved", TestScopeMetadata);
        Run("quicktest activation is ordered and bounded", TestQuicktestActivation);
        Run("quicktest request only records pending intent", TestQuicktestRequestRegistration);
        Run("quicktest pre-menu readiness cannot activate", TestQuicktestPreMainMenu);
        Run("quicktest activation uses one UI-thread boundary", TestQuicktestUiThreadBoundary);
        Run("quicktest duplicate ticks produce one activation", TestQuicktestSingleActivation);
        Run("quicktest callback preserves built-in order", TestQuicktestCallbackOrder);
        Run("quicktest old lifecycle failure is prevented", TestQuicktestLifecycleGuard);
        Run("quicktest activation failure clears pending state", TestQuicktestActivationFailure);
        Run("quicktest callback bursts cannot consume the elapsed-time wait", TestQuicktestCallbackBurst);
        Run("quicktest readiness expiry is terminal", TestQuicktestReadinessExpiry);
        Run("quicktest source boundary and lifecycle predicates are structural", TestQuicktestStructuralBoundary);
        Run("quicktest path has no fallback activation mechanism", TestQuicktestNoFallback);
        Run("coordinator-root argument forms are accepted", TestCoordinatorRootArgumentForms);
        Run("unprofiled launch preserves the existing mod list", TestUnprofiledLaunchPreservesMods);
        Run("baseline profile excludes managed projects and load-them-last", TestBaselineProfile);
        Run("profile dependency closure is ordered, deduplicated, and case-insensitive", TestProfileDependencyClosure);
        Run("profile config write waits for leases and process shutdown", TestProfileWriteWaitsForDrain);
        Run("profile writes fail closed on config and process races", TestProfileWritePreconditions);
        Run("generated config ownership survives lost state", TestGeneratedOwnershipSurvivesLostState);
        Run("invalid profiles fail before mutation or launch", TestInvalidProfilesFailClosed);
        Run("accepted profile survives coordinator recovery and conflicts", TestProfileRecoveryAndConflict);
        Run("recovery launches the frozen accepted profile without re-resolving metadata", TestFrozenProfileRecovery);
        Run("corrupt persisted profiles quarantine recovery", TestCorruptPersistedProfileQuarantine);
        Run("baseline restore is byte-for-byte and rejects external edits", TestBaselineRestoreSafety);
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

        using Fixture newCliProcess = Fixture.ReadyWithLease();
        int sameAgent = newCliProcess.State.Execute(Request("stop", "holder", 78, "T001"), _ => { }, () => true);
        Assert(sameAgent == 0, "the lease holder must be able to use a later CLI process");

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

    private static void TestQuicktestActivation()
    {
        bool mainMenu = false;
        int activationCalls = 0;
        QuicktestActivationController failure = new(true, () => mainMenu, () =>
        {
            activationCalls++;
            throw new NullReferenceException("simulated Root_Play lifecycle failure");
        }, () => 0, 1000);
        Assert(failure.Tick(true) == QuicktestActivationResult.WaitingForMainMenu && activationCalls == 0 &&
            failure.Pending,
            "Quicktest must not activate before genuine main-menu readiness");
        mainMenu = true;
        Assert(failure.Tick(true) == QuicktestActivationResult.Failed && failure.TerminalFailure &&
            !failure.Pending && activationCalls == 1,
            "observed built-in activation failure must be bounded and terminal");
        Assert(failure.Tick(true) == QuicktestActivationResult.Failed && activationCalls == 1,
            "terminal Quicktest failure must not retry or launch");

        int successfulCalls = 0;
        QuicktestActivationController success = new(true, () => mainMenu, () => successfulCalls++, () => 0, 1000);
        Assert(success.Tick(true) == QuicktestActivationResult.Requested && success.MainMenuReady &&
            success.ActivationRequested && !success.Pending && successfulCalls == 1,
            "built-in button activation must follow genuine main-menu readiness");
    }

    private static void TestQuicktestRequestRegistration()
    {
        int readinessCalls = 0;
        int activationCalls = 0;
        QuicktestActivationController controller = new(true, () =>
        {
            readinessCalls++;
            return true;
        }, () => activationCalls++, () => 0, 1000);

        Assert(controller.Pending && !controller.MainMenuReady && activationCalls == 0,
            "registration must only leave a pending activation intent");
        Assert(controller.Tick(false) == QuicktestActivationResult.WaitingForMainMenu &&
            readinessCalls == 0 && activationCalls == 0,
            "the request handler must not inspect or activate from outside the UI boundary");
    }

    private static void TestQuicktestPreMainMenu()
    {
        int activationCalls = 0;
        QuicktestActivationController controller = new(true, () => false, () => activationCalls++, () => 0, 1000);

        Assert(controller.Tick(true) == QuicktestActivationResult.WaitingForMainMenu &&
            controller.Pending && activationCalls == 0,
            "pre-main-menu readiness must defer activation");
    }

    private static void TestQuicktestUiThreadBoundary()
    {
        bool ready = true;
        int activationCalls = 0;
        QuicktestActivationController controller = new(true, () => ready, () => activationCalls++, () => 0, 1000);

        Assert(controller.Tick(false) == QuicktestActivationResult.WaitingForMainMenu && activationCalls == 0,
            "a ready-looking request must not activate off the modeled game/UI thread");
        Assert(controller.Tick(true) == QuicktestActivationResult.Requested && activationCalls == 1,
            "the same request must activate once it reaches the game/UI-thread boundary");
    }

    private static void TestQuicktestSingleActivation()
    {
        int activationCalls = 0;
        QuicktestActivationController controller = new(true, () => true, () => activationCalls++, () => 0, 1000);

        Assert(controller.Tick(true) == QuicktestActivationResult.Requested, "first UI tick must queue activation");
        Assert(controller.Tick(true) == QuicktestActivationResult.Requested, "duplicate UI tick must be harmless");
        Assert(controller.Tick(true) == QuicktestActivationResult.Requested && activationCalls == 1,
            "duplicate ticks or callbacks must not activate twice");
    }

    private static void TestQuicktestCallbackOrder()
    {
        List<string> operations = new();
        QuicktestActivationController controller = new(true, () => true, () =>
        {
            operations.Add("QueueLongEvent:GeneratingMap");
            operations.Add("Root_Play.SetupForQuickTestPlay");
            operations.Add("PageUtility.InitGameStart");
        }, () => 0, 1000);

        Assert(controller.Tick(true) == QuicktestActivationResult.Requested,
            "verified adapter model must queue successfully");
        Assert(operations.SequenceEqual(new[]
        {
            "QueueLongEvent:GeneratingMap",
            "Root_Play.SetupForQuickTestPlay",
            "PageUtility.InitGameStart"
        }), "verified built-in callback order must be preserved");
    }

    private static void TestQuicktestLifecycleGuard()
    {
        bool initialized = false;
        AssertThrows<NullReferenceException>(() =>
        {
            if (!initialized)
                throw new NullReferenceException("simulated Root_Play lifecycle failure");
        }, "the former direct path must reproduce the invalid lifecycle failure");

        int fakeLaunches = 0;
        QuicktestActivationController corrected = new(true, () => initialized, () => fakeLaunches++, () => 0, 1000);
        Assert(corrected.Tick(true) == QuicktestActivationResult.WaitingForMainMenu && fakeLaunches == 0,
            "the corrected path must not enter the invalid lifecycle");
        initialized = true;
        Assert(corrected.Tick(true) == QuicktestActivationResult.Requested && fakeLaunches == 1,
            "the corrected path may activate only after lifecycle readiness");
    }

    private static void TestQuicktestActivationFailure()
    {
        int fakeLaunches = 0;
        int restartRequests = 0;
        QuicktestActivationController controller = new(true, () => true, () =>
        {
            throw new InvalidOperationException("simulated queued activation failure");
        }, () => 0, 1000);

        Assert(controller.Tick(true) == QuicktestActivationResult.Failed && controller.TerminalFailure &&
            !controller.Pending && fakeLaunches == 0 && restartRequests == 0,
            "activation failure must be terminal, clear pending state, and launch nothing");
        Assert(controller.Tick(true) == QuicktestActivationResult.Failed && fakeLaunches == 0 &&
            restartRequests == 0, "terminal activation failure must not retry or request restart");

        QuicktestActivationController queued = new(true, () => true, () => { }, () => 0, 1000);
        Assert(queued.Tick(true) == QuicktestActivationResult.Requested && queued.ActivationRequested,
            "a queued adapter request must be marked consumed");
        queued.ReportActivationFailure(new InvalidOperationException("simulated deferred callback failure"));
        Assert(queued.TerminalFailure && !queued.Pending && !queued.ActivationRequested,
            "deferred queue failure must become terminal and clear the consumed request");
    }

    private static void TestQuicktestCallbackBurst()
    {
        long elapsedMilliseconds = 0;
        bool mainMenuReady = false;
        int activationCalls = 0;
        QuicktestActivationController controller = new(true, () => mainMenuReady, () => activationCalls++,
            () => elapsedMilliseconds, 1000);

        for (int index = 0; index < 1000; index++)
            Assert(controller.Tick(true) == QuicktestActivationResult.WaitingForMainMenu,
                "callback frequency must not consume an elapsed-time activation window");

        Assert(controller.Pending && !controller.TerminalFailure && activationCalls == 0,
            "a same-instant callback burst must leave Quicktest pending");
        mainMenuReady = true;
        Assert(controller.Tick(true) == QuicktestActivationResult.Requested && activationCalls == 1,
            "Quicktest must still activate when the menu becomes ready after a callback burst");
    }

    private static void TestQuicktestReadinessExpiry()
    {
        int fakeLaunches = 0;
        int restartRequests = 0;
        long elapsedMilliseconds = 0;
        QuicktestActivationController controller = new(true, () => false, () => fakeLaunches++,
            () => elapsedMilliseconds, 1000);

        Assert(controller.Tick(true) == QuicktestActivationResult.WaitingForMainMenu,
            "first invalid-readiness tick must remain bounded and pending");
        elapsedMilliseconds = 999;
        Assert(controller.Tick(true) == QuicktestActivationResult.WaitingForMainMenu && controller.Pending,
            "readiness must remain pending before the elapsed-time deadline");
        elapsedMilliseconds = 1000;
        Assert(controller.Tick(true) == QuicktestActivationResult.Failed && controller.TerminalFailure &&
            !controller.Pending && fakeLaunches == 0 && restartRequests == 0,
            "readiness expiry must become terminal with zero launches and restart requests");
    }

    private static void TestQuicktestStructuralBoundary()
    {
        string mod = ReadWorkspaceFile(Path.Combine("Source", "Mod", "DevBridge2Mod.cs"));
        string adapter = ReadWorkspaceFile(Path.Combine("Source", "Mod", "DevBridgeQuicktestMenuAdapter.cs"));

        Assert(!mod.Contains("Root_Play.SetupForQuickTestPlay", StringComparison.Ordinal) &&
            !mod.Contains("PageUtility.InitGameStart", StringComparison.Ordinal),
            "DevBridge2Mod request handler must not directly reference the leaf or setup method");
        Assert(adapter.Contains("LongEventHandler.QueueLongEvent", StringComparison.Ordinal) &&
            adapter.Contains("\"GeneratingMap\"", StringComparison.Ordinal) &&
            adapter.Contains("GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap", StringComparison.Ordinal),
            "the adapter must retain the built-in queued long-event boundary");

        int setup = adapter.IndexOf("Root_Play.SetupForQuickTestPlay", StringComparison.Ordinal);
        int init = adapter.IndexOf("PageUtility.InitGameStart", StringComparison.Ordinal);
        Assert(setup >= 0 && init > setup, "the adapter must preserve SetupForQuickTestPlay before InitGameStart");
        foreach (string predicate in new[]
        {
            "UnityData.IsInMainThread", "GenScene.InEntryScene", "Current.ProgramState",
            "Current.Root", "Current.Root_Entry", "Find.UIRoot", "Find.WindowStack",
            "Current.Game", "WorldRendererUtility.WorldSelected", "Prefs.DevMode",
            "LongEventHandler.AnyEventNowOrWaiting", "LongEventHandler.ShouldWaitForEvent"
        })
        {
            Assert(adapter.Contains(predicate, StringComparison.Ordinal),
                "verified main-menu lifecycle predicate is missing: " + predicate);
        }

        Assert(mod.Contains("DevBridgeQuicktestActivationDriver", StringComparison.Ordinal) &&
            mod.Contains("private void Update()", StringComparison.Ordinal) &&
            mod.Contains("DevBridgeQuicktestActivation.Tick()", StringComparison.Ordinal),
            "Quicktest readiness must be driven by a persistent per-frame UI component");
        Assert(!mod.Contains("ExecuteWhenFinished(TryActivate)", StringComparison.Ordinal),
            "Quicktest readiness must not retry through long-event completion callbacks");
        Assert(!adapter.Contains("WindowLayer.Dialog", StringComparison.Ordinal) &&
            !adapter.Contains("UIMenuBackgroundManager.background", StringComparison.Ordinal),
            "initialized entry lifecycle must not be rejected by visual menu overlays");
    }

    private static void TestQuicktestNoFallback()
    {
        string mod = ReadWorkspaceFile(Path.Combine("Source", "Mod", "DevBridge2Mod.cs"));
        string adapter = ReadWorkspaceFile(Path.Combine("Source", "Mod", "DevBridgeQuicktestMenuAdapter.cs"));
        string quicktestSource = mod + Environment.NewLine + adapter;
        foreach (string forbidden in new[]
        {
            "GetCommandLineArgs", "--quicktest", "Input.GetMouseButton", "Event.current",
            "SaveGame", ".rws", "Process.Start", "MapGenerator", "MousePosition"
        })
        {
            Assert(!quicktestSource.Contains(forbidden, StringComparison.Ordinal),
                "Quicktest path must not contain fallback mechanism: " + forbidden);
        }
    }

    private static void TestDoctorRecoversInspectionQuarantine()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 193,
            Phase = BridgePhase.ERROR,
            Error = ProcessInspection.Message,
            ErrorCode = ProcessInspection.ErrorCode,
            LaunchId = "stale-launch",
            LaunchGeneration = 194,
            TargetGeneration = 194,
            ProcessId = 26844,
            ProcessStartUtcTicks = 639221499641606101,
            LaunchStartedUtc = ClockStart,
            RequiresNewProcess = true
        });

        BridgeRequest doctorRequest = Request("doctor");
        List<string> output = new();
        int doctorExit = fixture.State.Execute(doctorRequest, output.Add, () => true);
        JsonCommandResponse recovered = fixture.State.CreateJsonResponse(doctorRequest, doctorExit, output);

        Assert(doctorExit == 0 && recovered.State == "STOPPED" && recovered.ErrorCode == null,
            "a complete zero-process census must recover the stale inspection quarantine to STOPPED");
        Assert(recovered.RimWorldPid == 0 && recovered.RimWorldProcessStartIdentity == 0 &&
            recovered.RequiresNewProcess && !recovered.RestartPending,
            "recovery must clear the stale process identity and require a new explicit launch");
        Assert(fixture.Adapter.EnumerationCalls == 1 && fixture.Adapter.TerminationRequests == 0 &&
            fixture.Adapter.LaunchCalls == 0,
            "doctor recovery must use one census and make zero termination or launch calls");
        Assert(output.Any(value => value.Contains("zero-process census", StringComparison.Ordinal)) &&
            output.Any(value => value.Contains("DevBridge.cmd restart", StringComparison.Ordinal)),
            "doctor must report the recovery and direct the operator to an explicit restart");

        fixture.State = fixture.Reload();
        JsonCommandResponse persisted = fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>());
        Assert(persisted.State == "STOPPED" && persisted.RimWorldPid == 0 && persisted.ErrorCode == null,
            "the recovered stopped state must be durable");

        fixture.Adapter.ReadyOnLaunch = true;
        List<string> restartOutput = new();
        int restartExit = fixture.State.Execute(Request("restart"), restartOutput.Add, () => true);
        Assert(restartExit == 0 && fixture.Adapter.LaunchCalls == 1,
            "only the later explicit restart may launch the replacement generation (exit " + restartExit +
            ", launches " + fixture.Adapter.LaunchCalls + ", output: " + string.Join(" | ", restartOutput) + ")");
    }

    private static void TestDoctorRecoveryFailsClosed()
    {
        using (Fixture incomplete = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.ERROR,
            Error = ProcessInspection.Message,
            ErrorCode = ProcessInspection.ErrorCode,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            RequiresNewProcess = true
        }))
        {
            incomplete.Adapter.EnumerationIncomplete = true;
            int exitCode = incomplete.State.Execute(Request("doctor"), _ => { }, () => true);
            JsonCommandResponse response = incomplete.State.CreateJsonResponse(Request("status"), exitCode,
                Array.Empty<string>());
            Assert(response.State == "ERROR" && response.ErrorCode == ProcessInspection.ErrorCode &&
                response.RimWorldPid == 101,
                "an incomplete census must preserve the quarantine and stale identity for diagnosis");
            Assert(incomplete.Adapter.TerminationRequests == 0 && incomplete.Adapter.LaunchCalls == 0,
                "an incomplete census must make zero process-control calls");
        }

        using (Fixture present = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.ERROR,
            Error = ProcessInspection.Message,
            ErrorCode = ProcessInspection.ErrorCode,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            RequiresNewProcess = true
        }))
        {
            present.Adapter.Add(new FakeProcess(999, 9999, present.RimWorldPath));
            int exitCode = present.State.Execute(Request("doctor"), _ => { }, () => true);
            JsonCommandResponse response = present.State.CreateJsonResponse(Request("status"), exitCode,
                Array.Empty<string>());
            Assert(response.State == "ERROR" && response.ErrorCode == ProcessInspection.ErrorCode,
                "a matching RimWorld process must preserve the inspection quarantine");
            Assert(present.Adapter.TerminationRequests == 0 && present.Adapter.LaunchCalls == 0,
                "doctor must never control or launch a process while deciding recovery");
        }
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo directory = new(Environment.CurrentDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new InvalidOperationException("workspace file not found: " + relativePath);
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
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

    private static void TestUnprofiledLaunchPreservesMods()
    {
        using Fixture fixture = new(new PersistedState { Generation = 0, Phase = BridgePhase.STOPPED });
        string original = "<ModsConfigData>\r\n  <activeMods>\r\n    <li>lan.devbridge2</li>\r\n    <li>user.custom.mod</li>\r\n  </activeMods>\r\n  <customSetting>keep-me</customSetting>\r\n</ModsConfigData>";
        File.WriteAllText(Path.Combine(fixture.Root, "ModsConfig.xml"), original, new UTF8Encoding(false));
        fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = fixture.State.Execute(Request("restart", "agent", 1), _ => { }, () => true);
        Assert(exitCode == 0, "unprofiled restart must still launch successfully");
        Assert(File.ReadAllText(Path.Combine(fixture.Root, "ModsConfig.xml")) == original,
            "restart without --projects must not rewrite the user mod list");
        Assert(fixture.State.CreateJsonResponse(Request("status"), 0, Array.Empty<string>()).ProfileMode == "legacy",
            "unprofiled launch must remain in legacy profile mode");
    }

    private static void TestBaselineProfile()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "none"), _ => { }, () => true);
        Assert(exitCode == 0, "baseline profile restart must succeed");
        List<string> active = ActiveMods(setup.Fixture.Root);
        Assert(active.SequenceEqual(ModProfileResolver.AlwaysOnPackageIds, StringComparer.OrdinalIgnoreCase),
            "baseline profile must contain exactly the always-on mods in stable order");
        Assert(!active.Any(value => value.Contains("loadthemlast", StringComparison.OrdinalIgnoreCase)),
            "baseline profile must never inject Load Them Last");
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(response.ProfileMode == ModProfile.BaselineMode && response.RequestedProjects.Count == 0,
            "JSON must report the explicit baseline profile");
        Assert(response.ResolvedMods.SequenceEqual(active, StringComparer.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(response.ProfileFingerprint) &&
               !string.IsNullOrWhiteSpace(response.BaselineFingerprint),
            "JSON must report the exact resolved baseline profile and fingerprints");
    }

    private static void TestProfileDependencyClosure()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture,aquaculture"), _ => { }, () => true);
        Assert(exitCode == 0, "project profile restart must succeed");
        List<string> active = ActiveMods(setup.Fixture.Root);
        List<string> lower = active.Select(value => value.ToLowerInvariant()).ToList();
        string[] expected =
        {
            "oskarpotocki.vanillafactionsexpanded.core",
            "vanillaexpanded.vcef",
            "ferny.replacelib",
            "ferny.progressionagriculture",
            "lan.aquaculture.fishing",
            "lan.horticulture.novelseeds"
        };
        foreach (string packageId in expected)
            Assert(lower.Contains(packageId), "dependency closure is missing " + packageId);
        Assert(lower.Distinct(StringComparer.OrdinalIgnoreCase).Count() == lower.Count,
            "shared dependencies must be deduplicated");
        Assert(IndexOf(lower, "oskarpotocki.vanillafactionsexpanded.core") < IndexOf(lower, "vanillaexpanded.vcef") &&
               IndexOf(lower, "vanillaexpanded.vcef") < IndexOf(lower, "ferny.replacelib") &&
               IndexOf(lower, "ferny.replacelib") < IndexOf(lower, "ferny.progressionagriculture"),
            "dependencies must precede their dependents");
        Assert(IndexOf(lower, "lan.aquaculture.fishing") < IndexOf(lower, "lan.horticulture.novelseeds"),
            "loadBefore/loadAfter constraints must be honored");
        Assert(!lower.Contains(ModProfileResolver.ForbiddenPackageId),
            "Load Them Last must never be included");
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(response.ProfileMode == ModProfile.ProjectsMode &&
               response.RequestedProjects.SequenceEqual(new[] { "aquaculture", "horticulture" }),
            "JSON must report canonical requested project aliases");
        Assert(response.ResolvedProjectPackageIds.Count == 2 && response.ResolvedMods.Count == active.Count,
            "JSON must expose both resolved roots and the complete ordered profile");

        ModProfile first = ModProfileResolver.Resolve(setup.Fixture.Root, response.BaselineFingerprint,
            new[] { "HORTICULTURE", "aquaculture" }, setup.Fixture.InstalledModsRoots);
        ModProfile second = ModProfileResolver.Resolve(setup.Fixture.Root, response.BaselineFingerprint,
            new[] { "aquaculture", "horticulture" }, setup.Fixture.InstalledModsRoots);
        Assert(first.ProfileFingerprint == second.ProfileFingerprint &&
               first.ResolvedMods.SequenceEqual(second.ResolvedMods, StringComparer.OrdinalIgnoreCase),
            "equivalent alias casing/order must produce one deterministic profile fingerprint and order");
    }

    private static void TestProfileWriteWaitsForDrain()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        byte[] baseline = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        PersistedState initial = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        initial.Generation = 1;
        initial.Phase = BridgePhase.READY;
        initial.LaunchId = "launch-ready";
        initial.LaunchGeneration = 1;
        initial.ProcessId = 101;
        initial.ProcessStartUtcTicks = 1001;
        initial.LaunchStartedUtc = ClockStart;
        initial.Leases = new List<TestLease> { setup.Fixture.Lease("T001", "holder", 77, ClockStart) };
        setup.Fixture.WriteState(initial);
        FakeProcess oldProcess = new(101, 1001, setup.Fixture.RimWorldPath);
        setup.Fixture.Adapter.Add(oldProcess);
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.Adapter.ReadyOnLaunch = true;

        Task<int> restart = Task.Run(() => setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true));
        Assert(SpinWait.SpinUntil(() =>
        {
            JsonCommandResponse status = setup.Fixture.State.CreateJsonResponse(
                Request("status"), 0, Array.Empty<string>());
            return status.RestartPending && setup.Fixture.Adapter.LaunchCalls == 0;
        }, TimeSpan.FromSeconds(2)), "profile restart must wait while the lease is active");
        Assert(File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(baseline) &&
               !oldProcess.HasExited,
            "profile config must not change while the old process and blocking lease remain");

        Assert(setup.Fixture.State.Execute(Request("test", "holder", 77, "end", "T001"), _ => { }, () => true) == 0,
            "lease holder must be able to release the blocking lease");
        Assert(restart.Wait(TimeSpan.FromSeconds(10)) && restart.Result == 0,
            "profile restart must resume exactly once after the lease drains");
        Assert(oldProcess.HasExited && setup.Fixture.Adapter.LaunchCalls == 1 &&
               ActiveMods(setup.Fixture.Root).Contains("lan.horticulture.novelseeds", StringComparer.OrdinalIgnoreCase),
            "profile config must be written after owned-process shutdown and before the replacement launch");
    }

    private static void TestProfileWritePreconditions()
    {
        using (ProfileSetup capture = ProfileSetup.Create())
        {
            capture.Fixture.BeforeModsConfigWrite = () => File.WriteAllText(
                Path.Combine(capture.Fixture.Root, "ModsConfig.xml"), "<capture-race />", new UTF8Encoding(false));
            capture.Fixture.State = capture.Fixture.Reload();
            int exitCode = capture.Fixture.State.Execute(
                Request("mods", "agent", 1, "capture-baseline"), _ => { }, () => true);
            Assert(exitCode != 0 && !File.Exists(Path.Combine(capture.Fixture.Root, "Runtime", "ModsConfig.baseline.xml")) &&
                   File.ReadAllText(Path.Combine(capture.Fixture.Root, "ModsConfig.xml")) == "<capture-race />",
                "a concurrent edit must not be captured as the durable baseline");
        }

        using (ProfileSetup edited = ProfileSetup.Create())
        {
            Assert(edited.CaptureBaseline(), "external-edit race: baseline capture must succeed");
            edited.Fixture.Adapter.ReadyOnLaunch = true;
            edited.Fixture.BeforeModsConfigWrite = () => File.WriteAllText(
                Path.Combine(edited.Fixture.Root, "ModsConfig.xml"), "<user-edit />", new UTF8Encoding(false));
            edited.Fixture.State = edited.Fixture.Reload();
            int exitCode = edited.Fixture.State.Execute(
                Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
            JsonCommandResponse response = edited.Fixture.State.CreateJsonResponse(
                Request("status"), exitCode, Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "MODS_CONFIG_EXTERNAL_EDIT" &&
                   edited.Fixture.Adapter.LaunchCalls == 0 && File.ReadAllText(
                       Path.Combine(edited.Fixture.Root, "ModsConfig.xml")) == "<user-edit />",
                "a concurrent ModsConfig edit must be detected before the profile replaces it or launches");
        }

        using (ProfileSetup process = ProfileSetup.Create())
        {
            Assert(process.CaptureBaseline(), "process race: baseline capture must succeed");
            byte[] baseline = File.ReadAllBytes(Path.Combine(process.Fixture.Root, "ModsConfig.xml"));
            process.Fixture.Adapter.ReadyOnLaunch = true;
            process.Fixture.BeforeModsConfigWrite = () => process.Fixture.Adapter.Add(
                new FakeProcess(999, 9999, process.Fixture.RimWorldPath));
            process.Fixture.State = process.Fixture.Reload();
            int exitCode = process.Fixture.State.Execute(
                Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
            JsonCommandResponse response = process.Fixture.State.CreateJsonResponse(
                Request("status"), exitCode, Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "MODS_CONFIG_PROCESS_RUNNING" &&
                   process.Fixture.Adapter.LaunchCalls == 0 && File.ReadAllBytes(
                       Path.Combine(process.Fixture.Root, "ModsConfig.xml")).SequenceEqual(baseline),
                "a process appearing before the config write must prevent both mutation and launch");
        }

        using (Fixture legacy = new(new PersistedState { Generation = 0, Phase = BridgePhase.STOPPED }))
        {
            File.WriteAllText(Path.Combine(legacy.Root, "ModsConfig.xml"),
                "<ModsConfigData><activeMods><li>user.custom.mod</li></activeMods></ModsConfigData>",
                new UTF8Encoding(false));
            legacy.Adapter.ReadyOnLaunch = true;
            legacy.BeforeModsConfigWrite = () => File.WriteAllText(
                Path.Combine(legacy.Root, "ModsConfig.xml"), "<user-edit />", new UTF8Encoding(false));
            legacy.State = legacy.Reload();
            int exitCode = legacy.State.Execute(Request("restart", "agent", 1), _ => { }, () => true);
            JsonCommandResponse response = legacy.State.CreateJsonResponse(
                Request("status"), exitCode, Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "MODS_CONFIG_EXTERNAL_EDIT" &&
                   legacy.Adapter.LaunchCalls == 0 && File.ReadAllText(
                       Path.Combine(legacy.Root, "ModsConfig.xml")) == "<user-edit />",
                "legacy DevBridge activation must also reject a concurrent config edit");
        }
    }

    private static void TestGeneratedOwnershipSurvivesLostState()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "lost-state ownership: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "lost-state ownership: reduced profile launch must succeed");

        byte[] generated = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        setup.Fixture.Adapter.Current.ForceTerminate();
        File.Delete(Path.Combine(setup.Fixture.Root, "Runtime", "state.json"));
        setup.Fixture.State = setup.Fixture.Reload();
        int exitCode = setup.Fixture.State.Execute(
            Request("mods", "agent", 1, "capture-baseline"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());
        Assert(exitCode != 0 && response.ErrorCode == "PROFILE_BASELINE_GENERATED" &&
               File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(generated) &&
               File.Exists(Path.Combine(setup.Fixture.Root, "Runtime", "ModsConfig.generated.json")),
            "generated reduced output must remain identifiable even when state.json is lost");
    }

    private static void TestInvalidProfilesFailClosed()
    {
        AssertInvalidProfile("missing dependency", setup =>
        {
            Directory.Delete(Path.Combine(setup.MetadataRoot, "progression"), true);
        }, "PROFILE_MISSING_PACKAGE");

        AssertInvalidProfile("ambiguous package", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "duplicate-replacelib", "FERNY.ReplaceLib", "");
        }, "PROFILE_AMBIGUOUS_PACKAGE");

        AssertInvalidProfile("malformed dependency metadata", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture",
                "lan.horticulture.novelseeds", "<modDependencies>unparseable dependency text</modDependencies>");
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("dependency cycle", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture",
                "lan.horticulture.novelseeds", "<modDependencies><li>ferny.progressionagriculture</li></modDependencies>");
            WriteInstalledMetadata(setup.MetadataRoot, "progression",
                "ferny.progressionagriculture", "<modDependencies><li>lan.horticulture.novelseeds</li></modDependencies>");
        }, "PROFILE_DEPENDENCY_CYCLE");
    }

    private static void AssertInvalidProfile(string name, Action<ProfileSetup> mutate, string expectedCode)
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), name + ": baseline capture must succeed");
        byte[] before = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        mutate(setup);
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), exitCode, Array.Empty<string>());
        Assert(exitCode != 0 && response.ErrorCode == expectedCode,
            name + " must fail with " + expectedCode + " (actual " + response.ErrorCode + ")");
        Assert(setup.Fixture.Adapter.LaunchCalls == 0 &&
               File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(before),
            name + " must fail before launch or ModsConfig mutation");
        Assert(!setup.Fixture.State.CreateJsonResponse(Request("status"), exitCode, Array.Empty<string>()).RestartPending,
            name + " must not leave a pending restart");
    }

    private static void TestProfileRecoveryAndConflict()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        Assert(exitCode == 0, "profile restart must complete before recovery check");
        string fingerprint = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>()).ProfileFingerprint;
        setup.Fixture.State = setup.Fixture.Reload();
        JsonCommandResponse recovered = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(recovered.ProfileFingerprint == fingerprint && recovered.ResolvedMods.Count > 0,
            "accepted profile and fingerprint must survive coordinator recovery");

        // A conflicting request is rejected from the durable pending record before the
        // lifecycle worker can acquire its process-control gate.
        PersistedState pending = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        pending.RestartPending = true;
        pending.TargetGeneration = pending.Generation + 1;
        pending.LaunchOwner = "agent@1";
        pending.LaunchRequestKey = "restart-" + pending.TargetGeneration;
        pending.Phase = BridgePhase.DRAINING;
        setup.Fixture.WriteState(pending);
        setup.Fixture.State = setup.Fixture.Reload();
        int conflict = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "aquaculture"), _ => { }, () => true);
        JsonCommandResponse conflictResponse = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), conflict, Array.Empty<string>());
        Assert(conflict != 0 && conflictResponse.ErrorCode == "PROFILE_CONFLICT",
            "a conflicting project request must not replace a pending profile");
        Assert(conflictResponse.ProfileFingerprint == fingerprint,
            "the accepted pending profile fingerprint must remain unchanged after conflict");

        int legacyConflict = setup.Fixture.State.Execute(
            Request("restart", "agent", 1), _ => { }, () => true);
        JsonCommandResponse legacyConflictResponse = setup.Fixture.State.CreateJsonResponse(
            Request("status"), legacyConflict, Array.Empty<string>());
        Assert(legacyConflict != 0 && legacyConflictResponse.ErrorCode == "PROFILE_CONFLICT" &&
               legacyConflictResponse.ProfileFingerprint == fingerprint,
            "an unprofiled restart must not be treated as a duplicate of an accepted reduced profile");
    }

    private static void TestCorruptPersistedProfileQuarantine()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "corrupt profile: baseline capture must succeed");
        PersistedState baseline = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        baseline.ProfileMode = ModProfile.ProjectsMode;
        baseline.RequestedProjects = new List<string> { "horticulture" };
        baseline.ResolvedProjectPackageIds = new List<string> { "lan.horticulture.novelseeds" };
        baseline.ResolvedMods = ModProfileResolver.AlwaysOnPackageIds.ToList();
        baseline.ResolvedMods.Add("lan.horticulture.novelseeds");
        baseline.ProfileFingerprint = "not-a-fingerprint";
        baseline.RestartPending = true;
        baseline.TargetGeneration = 1;
        baseline.Phase = BridgePhase.DRAINING;
        baseline.LaunchOwner = "agent@1";
        baseline.LaunchRequestKey = "restart-1";
        setup.Fixture.WriteState(baseline);
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.State.StartRecoveryWork();
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(response.ErrorCode == "PROFILE_FINGERPRINT_MISMATCH" && !response.RestartPending &&
               setup.Fixture.Adapter.LaunchCalls == 0,
            "corrupt accepted profile state must quarantine recovery without silently falling back or launching");
    }

    private static void TestFrozenProfileRecovery()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "frozen recovery: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "frozen recovery: initial profile launch must succeed");

        PersistedState pending = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        int targetGeneration = pending.Generation + 1;
        pending.RestartPending = true;
        pending.TargetGeneration = targetGeneration;
        pending.Phase = BridgePhase.RESTARTING;
        pending.LaunchOwner = "agent@1";
        pending.LaunchRequestKey = "restart-" + targetGeneration;
        pending.ProcessId = 0;
        pending.ProcessStartUtcTicks = 0;
        pending.LaunchId = null;
        pending.LaunchGeneration = targetGeneration;
        pending.RestartRequestedUtc = ClockStart;
        pending.RequiresNewProcess = true;
        pending.Error = null;
        pending.ErrorCode = null;
        setup.Fixture.Adapter.Current.ForceTerminate();
        setup.Fixture.WriteState(pending);
        Directory.Delete(setup.MetadataRoot, true);
        setup.Fixture.State = setup.Fixture.Reload();
        int launchesBeforeRecovery = setup.Fixture.Adapter.LaunchCalls;
        setup.Fixture.State.StartRecoveryWork();
        Assert(SpinWait.SpinUntil(() =>
        {
            JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
                Request("status"), 0, Array.Empty<string>());
            return response.Generation == targetGeneration && response.State == "READY" &&
                   !response.RestartPending;
        }, TimeSpan.FromSeconds(10)),
            "recovery must complete using the accepted profile even when installed metadata is gone");
        JsonCommandResponse recovered = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(setup.Fixture.Adapter.LaunchCalls == launchesBeforeRecovery + 1 &&
               recovered.ProfileMode == ModProfile.ProjectsMode &&
               recovered.RequestedProjects.SequenceEqual(new[] { "horticulture" }) &&
               ActiveMods(setup.Fixture.Root).SequenceEqual(recovered.ResolvedMods, StringComparer.OrdinalIgnoreCase),
            "recovery must preserve the frozen profile roots, order, and exactly-once launch");
    }

    private static void TestBaselineRestoreSafety()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        byte[] original = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "profile launch must succeed before restore");
        setup.Fixture.Adapter.Current.ForceTerminate();
        int recapture = setup.Fixture.State.Execute(
            Request("mods", "agent", 1, "capture-baseline"), _ => { }, () => true);
        JsonCommandResponse recaptureResponse = setup.Fixture.State.CreateJsonResponse(
            Request("status"), recapture, Array.Empty<string>());
            Assert(recapture != 0 && recaptureResponse.ErrorCode == "PROFILE_BASELINE_GENERATED",
            "a generated reduced profile must never be silently recaptured as the user baseline");
        int restored = setup.Fixture.State.Execute(Request("mods", "agent", 1, "restore-baseline"), _ => { }, () => true);
        Assert(restored == 0 && File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(original),
            "atomic restore must reproduce the captured bytes exactly");

        File.WriteAllText(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"), "<user-edit />", new UTF8Encoding(false));
        int refused = setup.Fixture.State.Execute(Request("mods", "agent", 1, "restore-baseline"), _ => { }, () => true);
        Assert(refused != 0 && File.ReadAllText(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")) == "<user-edit />",
            "unexpected external edits must never be overwritten by restore");
    }

    private static int IndexOf(IReadOnlyList<string> values, string value) =>
        values.ToList().IndexOf(value);

    private static List<string> ActiveMods(string root)
    {
        XDocument document = XDocument.Load(Path.Combine(root, "ModsConfig.xml"));
        XElement active = document.Descendants().Single(value =>
            string.Equals(value.Name.LocalName, "activeMods", StringComparison.OrdinalIgnoreCase));
        return active.Elements().Where(value => string.Equals(value.Name.LocalName, "li", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Value.Trim()).ToList();
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

    private static void WriteInstalledMetadata(string metadataRoot, string directoryName, string packageId,
        string dependencySection, string loadBefore = "", string loadAfter = "")
    {
        string directory = Path.Combine(metadataRoot, directoryName, "About");
        Directory.CreateDirectory(directory);
        string dependencies = dependencySection?.Trim() ?? string.Empty;
        if (dependencies.Length > 0 && !dependencies.StartsWith("<", StringComparison.Ordinal))
        {
            dependencies = "<modDependencies>" + string.Join(string.Empty,
                dependencies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => "<li>" + value + "</li>")) + "</modDependencies>";
        }
        string before = string.IsNullOrWhiteSpace(loadBefore) ? string.Empty :
            "<loadBefore><li>" + loadBefore + "</li></loadBefore>";
        string after = string.IsNullOrWhiteSpace(loadAfter) ? string.Empty :
            "<loadAfter><li>" + loadAfter + "</li></loadAfter>";
        File.WriteAllText(Path.Combine(directory, "About.xml"),
            "<ModMetaData><packageId>" + packageId + "</packageId>" + dependencies + before + after + "</ModMetaData>");
    }

    private sealed class ProfileSetup : IDisposable
    {
        internal readonly Fixture Fixture;
        internal readonly string MetadataRoot;

        private ProfileSetup(Fixture fixture, string metadataRoot)
        {
            Fixture = fixture;
            MetadataRoot = metadataRoot;
        }

        internal static ProfileSetup Create()
        {
            Fixture fixture = new(new PersistedState { Generation = 0, Phase = BridgePhase.STOPPED });
            string metadataRoot = Path.Combine(fixture.Root, "InstalledMods");
            Directory.CreateDirectory(metadataRoot);
            fixture.InstalledModsRoots = new[] { metadataRoot };
            WriteAllMetadata(metadataRoot);
            File.WriteAllText(Path.Combine(fixture.Root, "ModsConfig.xml"),
                "<ModsConfigData>\r\n  <activeMods>\r\n" +
                string.Join("\r\n", new[]
                {
                    "    <li>lan.devbridge2</li>",
                    "    <li>lan.horticulture.novelseeds</li>",
                    "    <li>lan.aquaculture.fishing</li>",
                    "    <li>ferny.loadthemlast</li>"
                }) + "\r\n  </activeMods>\r\n</ModsConfigData>", new UTF8Encoding(false));
            fixture.State = fixture.Reload();
            return new ProfileSetup(fixture, metadataRoot);
        }

        internal bool CaptureBaseline()
        {
            return Fixture.State.Execute(Request("mods", "agent", 1, "capture-baseline"), _ => { }, () => true) == 0;
        }

        public void Dispose() => Fixture.Dispose();

        private static void WriteAllMetadata(string metadataRoot)
        {
            foreach (string packageId in ModProfileResolver.AlwaysOnPackageIds)
                WriteInstalledMetadata(metadataRoot, packageId, packageId, "");

            WriteInstalledMetadata(metadataRoot, "deferred-reality", "lan.deferredreality.framework", "");
            WriteInstalledMetadata(metadataRoot, "insight-canvas", "lan.insightcanvas", "");
            WriteInstalledMetadata(metadataRoot, "knowledge-framework", "lan.knowledgeframework", "");
            WriteInstalledMetadata(metadataRoot, "frontier", "lan.frontier", "");
            WriteInstalledMetadata(metadataRoot, "aquaculture", "lan.aquaculture.fishing",
                "FERNY.ReplaceLib", "ferny.progressionagriculture", "");
            WriteInstalledMetadata(metadataRoot, "horticulture", "lan.horticulture.novelseeds",
                "ferny.progressionagriculture", "", "lan.aquaculture.fishing");
            WriteInstalledMetadata(metadataRoot, "wildlife", "lan.wildlife", "");
            WriteInstalledMetadata(metadataRoot, "progression", "ferny.progressionagriculture", "ferny.replacelib");
            WriteInstalledMetadata(metadataRoot, "replacelib", "FERNY.ReplaceLib", "vanillaexpanded.vcef");
            WriteInstalledMetadata(metadataRoot, "vcef", "vanillaexpanded.vcef",
                "oskarpotocki.vanillafactionsexpanded.core");
            WriteInstalledMetadata(metadataRoot, "vfe-core", "oskarpotocki.vanillafactionsexpanded.core", "");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        internal readonly string RimWorldPath;
        internal readonly FakeClock Clock;
        internal readonly FakeProcessAdapter Adapter;
        internal CoordinatorState State;
        internal IReadOnlyList<string> InstalledModsRoots { get; set; }
        internal Action BeforeModsConfigWrite { get; set; }

        internal Fixture(PersistedState initial)
        {
            Root = Path.Combine(Path.GetTempPath(), "DevBridge2-offline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "Runtime"));
            Directory.CreateDirectory(Path.Combine(Root, "About"));
            Directory.CreateDirectory(Path.Combine(Root, "1.6", "Assemblies"));
            RimWorldPath = Path.Combine(Root, "RimWorldWin64.exe");
            File.WriteAllText(RimWorldPath, "offline-test-executable");
            File.WriteAllText(Path.Combine(Root, "About", "About.xml"), "<ModMetaData />");
            File.WriteAllText(Path.Combine(Root, "1.6", "Assemblies", "DevBridge2.dll"), "offline-test-assembly");
            File.WriteAllText(Path.Combine(Root, "ModsConfig.xml"), "<activeMods><li>lan.devbridge2</li></activeMods>");
            Clock = new FakeClock(ClockStart);
            Adapter = new FakeProcessAdapter(RimWorldPath, Root, Clock);
            WriteState(initial);
            State = Reload();
        }

        internal TestLease Lease(DateTime started) => Lease("T001", "holder", 77, started);

        internal TestLease Lease(string id, string agent, int pid, DateTime started) => new()
        {
            Id = id, Agent = agent, ClientProcessId = pid, Generation = 1,
            StartedUtc = started, LastHeartbeatUtc = started
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
                    new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 0,
                        StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
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
                Leases = new List<TestLease> { new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1,
                    StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart } }
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture ReadyWithLeases()
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
                Leases = new List<TestLease>
                {
                    new() { Id = "T001", Agent = "holder-a", ClientProcessId = 77, Generation = 1,
                        StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart },
                    new() { Id = "T002", Agent = "holder-b", ClientProcessId = 78, Generation = 1,
                        StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
                }
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
                Leases = new List<TestLease> { new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1,
                    StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart } }
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
                ModsConfigPath = Path.Combine(Root, "ModsConfig.xml"),
                InstalledModsRoots = InstalledModsRoots,
                BeforeModsConfigWrite = BeforeModsConfigWrite
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
