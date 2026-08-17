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

    private static void TestMaintenanceLeaseReacquisition()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "fixture stop must succeed");
        fixture.Clock.Advance(TimeSpan.FromHours(2));

        BridgeRequest begin = Request("test", "replacement-holder", 88, "begin");
        int exitCode = fixture.State.Execute(begin, _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(begin, exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.State == "STOPPED" && response.MaintenanceReady,
            "a replacement lease must preserve the confirmed maintenance window");
        Assert(response.ActiveTests == 1 && response.LeaseState == "HELD" &&
            !string.IsNullOrWhiteSpace(response.LeaseId),
            "test begin must return a usable replacement maintenance lease");
        Assert(fixture.Adapter.LaunchCalls == 0,
            "reacquiring a maintenance lease must not launch RimWorld");
    }

    private static void TestMaintenanceLeaseReacquisitionAfterProfileError()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 265,
            Phase = BridgePhase.ERROR,
            ErrorCode = "PROFILE_REQUIRED_MOD_MISSING",
            Error = "The accepted profile is missing required tooling package brrainz.rimbridgeserver.",
            MaintenanceReady = true,
            SessionDirty = true,
            ProcessId = 0,
            ProcessStartUtcTicks = 0
        });

        BridgeRequest begin = Request("test", "replacement-holder", 88, "begin");
        int exitCode = fixture.State.Execute(begin, _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(begin, exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.State == "ERROR" && response.MaintenanceReady,
            "a safe maintenance window must remain lease-reacquirable after persisted profile validation error");
        Assert(response.ActiveTests == 1 && response.LeaseState == "HELD" &&
            !string.IsNullOrWhiteSpace(response.LeaseId),
            "profile-error maintenance recovery must return a usable replacement lease");
        Assert(fixture.Adapter.LaunchCalls == 0,
            "profile-error maintenance recovery must not launch RimWorld");
    }

    private static void TestFailedProcessRecoveryStop()
    {
        using Fixture fixture = Fixture.FailedWithoutLease();
        BridgeRequest begin = Request("test", "recovery-holder", 88, "begin");
        int beginExit = fixture.State.Execute(begin, _ => { }, () => true);
        JsonCommandResponse acquired = fixture.State.CreateJsonResponse(begin, beginExit, Array.Empty<string>());

        Assert(beginExit == 0 && acquired.ActiveTests == 1 && !string.IsNullOrWhiteSpace(acquired.LeaseId),
            "a terminal failed process must return a usable recovery lease");
        Assert(fixture.Adapter.LaunchCalls == 0,
            "acquiring a failure-recovery lease must not launch RimWorld");

        int stopExit = fixture.State.Execute(
            Request("stop", "recovery-holder", 88, acquired.LeaseId), _ => { }, () => true);
        JsonCommandResponse stopped = fixture.State.CreateJsonResponse(
            Request("status", "recovery-holder", 88), stopExit, Array.Empty<string>());
        Assert(stopExit == 0 && stopped.State == "STOPPED" && stopped.MaintenanceReady,
            "the recovery lease must safely stop and confirm the failed process");
        Assert(fixture.Adapter.TerminationRequests == 1 && fixture.Adapter.LaunchCalls == 0,
            "failure recovery must stop exactly once without relaunching");
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

    private static void TestRestartOwnedExitInspectionBoundary()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.Adapter.ReadyOnLaunch = true;
        fixture.Adapter.Current.ReportExitedOnFirstHasExited = true;
        fixture.Adapter.Current.InvalidateInspectionAfterExitObservation = true;

        BridgeRequest restart = Request("restart");
        int exitCode = fixture.State.Execute(restart, _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(restart, exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.State == "READY" && response.Generation == 2 &&
               response.ErrorCode == null && fixture.Adapter.LaunchCalls == 1,
            "an exact owned process that exits before MainModule inspection must not quarantine a restart");
        Assert(fixture.Adapter.TerminationRequests == 0,
            "an already-exited owned process must not receive a second termination request");

        using Fixture ambiguous = Fixture.ReadyWithoutLease();
        ambiguous.Adapter.ReadyOnLaunch = true;
        ambiguous.Adapter.Current.ReportExitedOnFirstHasExited = true;
        ambiguous.Adapter.Current.InvalidateInspectionAfterExitObservation = true;
        ambiguous.Adapter.ExtraMatchingProcess = true;

        BridgeRequest ambiguousRestart = Request("restart");
        int ambiguousExit = ambiguous.State.Execute(ambiguousRestart, _ => { }, () => true);
        JsonCommandResponse ambiguousResponse = ambiguous.State.CreateJsonResponse(
            ambiguousRestart, ambiguousExit, Array.Empty<string>());

        Assert(ambiguousExit != 0 && ambiguous.Adapter.LaunchCalls == 0 &&
               (ambiguousResponse.ErrorCode == "LAUNCH_FAILED" ||
                ambiguousResponse.ErrorCode == ProcessInspection.ErrorCode),
            "a separate matching process must still block the replacement launch fail-closed");
    }

    private static void TestRestartRetriesTransientPreterminationInspection()
    {
        using Fixture transient = Fixture.ReadyWithoutLease();
        transient.Adapter.ReadyOnLaunch = true;
        transient.Adapter.Current.ExecutablePathFailuresRemaining = 1;

        BridgeRequest restart = Request("restart");
        int exitCode = transient.State.Execute(restart, _ => { }, () => true);
        JsonCommandResponse response = transient.State.CreateJsonResponse(restart, exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.State == "READY" && response.Generation == 2 &&
               response.ErrorCode == null && transient.Adapter.LaunchCalls == 1,
            "a transient pre-termination process inspection failure must be retried");

        using Fixture ambiguous = Fixture.ReadyWithoutLease();
        ambiguous.Adapter.ReadyOnLaunch = true;
        ambiguous.Adapter.Current.ThrowOnExecutablePath = true;

        BridgeRequest ambiguousRestart = Request("restart");
        int ambiguousExit = ambiguous.State.Execute(ambiguousRestart, _ => { }, () => true);
        JsonCommandResponse ambiguousResponse = ambiguous.State.CreateJsonResponse(
            ambiguousRestart, ambiguousExit, Array.Empty<string>());

        Assert(ambiguousExit != 0 && ambiguous.Adapter.LaunchCalls == 0 &&
               ambiguousResponse.ErrorCode == ProcessInspection.ErrorCode,
            "persistent pre-termination process inspection uncertainty must fail closed");
    }

    private static void TestRestartPreservesVerifiedLiveOwnership()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.Adapter.ReadyOnLaunch = true;

        BridgeRequest establish = Request("restart");
        int establishExit = fixture.State.Execute(establish, _ => { }, () => true);
        JsonCommandResponse establishResponse = fixture.State.CreateJsonResponse(
            establish, establishExit, Array.Empty<string>());
        Assert(establishExit == 0 && establishResponse.State == "READY" &&
               establishResponse.Generation == 2 && fixture.Adapter.LaunchCalls == 1,
            "the setup generation must reach READY through the normal launch path");

        FakeProcess owned = fixture.Adapter.Current;
        int ownedTerminationRequestsBeforeRestart = owned.TerminationRequests;
        PersistedState durable = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        Assert(durable != null && durable.Phase == BridgePhase.READY && durable.Generation == 2 &&
               durable.ProcessId == owned.Id && durable.ProcessStartUtcTicks == owned.StartIdentity &&
               string.Equals(durable.OwnedProcessExecutablePath, fixture.RimWorldPath,
                   StringComparison.OrdinalIgnoreCase),
            "READY must durably save the exact process identity and executable proof");

        fixture.State = fixture.Reload();
        owned.ThrowOnExecutablePath = true;

        BridgeRequest restart = Request("restart");
        int exitCode = fixture.State.Execute(restart, _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(restart, exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.State == "READY" && response.Generation == 3 &&
               response.ErrorCode == null && fixture.Adapter.LaunchCalls == 2,
            "a rehydrated verified live owned process must survive a non-essential MainModule reinspection failure");
        Assert(fixture.Adapter.TerminationRequests == 2 && owned.HasExited,
            "restart must request termination of the exact previously verified process");
        Assert(owned.TerminationRequests == ownedTerminationRequestsBeforeRestart + 1,
            "the target generation must request termination exactly once for the persisted PID");
        Assert(fixture.Adapter.EnumerationCalls > 0,
            "replacement launch must follow an authoritative absence census");

        List<TraceView> trace = ReadTrace(fixture.Root);
        int restartTraceStart = trace.FindLastIndex(value =>
            value.Event == "command.dispatch.started" && value.Command == "restart");
        Assert(restartTraceStart >= 0, "the target restart trace was not recorded");
        List<TraceView> restartTrace = trace.Skip(restartTraceStart).ToList();
        string[] terminationSequence =
        {
            "termination.identity.pid_match",
            "termination.identity.start_match",
            "termination.path.unavailable",
            "termination.authorization.accepted",
            "process.termination.requested",
            "process.termination.confirmed",
            "post_termination.census.completed",
            "process.launch.initiated"
        };
        int previous = -1;
        foreach (string eventName in terminationSequence)
        {
            int current = IndexOf(restartTrace, eventName);
            Assert(current > previous, eventName + " was missing or out of order for the restart; events=" +
                string.Join(", ", restartTrace
                    .Select(value => value.Event + "[" + value.Detail + "]")));
            previous = current;
        }
        TraceView requestEvent = restartTrace.FirstOrDefault(value =>
            value.Event == "process.termination.requested");
        TraceView censusEvent = restartTrace.FirstOrDefault(value =>
            value.Event == "post_termination.census.completed");
        Assert(requestEvent?.Success == true,
            "termination request trace must prove the request was accepted");
        Assert(censusEvent?.Success == true && censusEvent.Detail == "complete=true;matching=0",
            "replacement launch must be preceded by a complete zero-process census");

        using Fixture contradictory = Fixture.ReadyWithoutLease();
        contradictory.Adapter.ReadyOnLaunch = true;
        BridgeRequest contradictoryStatus = Request("status");
        Assert(contradictory.State.Execute(contradictoryStatus, _ => { }, () => true) == 0,
            "the counter-test must establish an initial ownership proof");
        contradictory.Adapter.Current.ExecutablePathOverride =
            Path.Combine(contradictory.Root, "OtherInstall", "RimWorldWin64.exe");

        BridgeRequest contradictoryRestart = Request("restart");
        int contradictoryExit = contradictory.State.Execute(contradictoryRestart, _ => { }, () => true);
        JsonCommandResponse contradictoryResponse = contradictory.State.CreateJsonResponse(
            contradictoryRestart, contradictoryExit, Array.Empty<string>());

        Assert(contradictoryExit != 0 && contradictory.Adapter.TerminationRequests == 0 &&
               contradictory.Adapter.LaunchCalls == 0 &&
               contradictoryResponse.ErrorCode == ProcessInspection.ErrorCode,
            "a path contradiction must remain an ambiguous ownership failure without termination or launch");
    }

    private static void TestAttachedProcessTerminationBoundary()
    {
        const string signalVariable = "DEVBRIDGE_TEST_GRACEFUL_STOP_SIGNAL";
        string previousSignal = Environment.GetEnvironmentVariable(signalVariable);
        Environment.SetEnvironmentVariable(signalVariable, null);
        System.Diagnostics.Process child = null;
        try
        {
            System.Diagnostics.ProcessStartInfo start = new()
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("ping.exe -n 120 127.0.0.1 > nul");
            child = System.Diagnostics.Process.Start(start);
            Assert(child != null, "the attached-process termination fixture did not start");

            using SystemManagedProcess attached = new(
                System.Diagnostics.Process.GetProcessById(child.Id));
            bool requestResult;
            try
            {
                requestResult = attached.RequestTermination();
            }
            catch (ProcessInspectionException exception)
            {
                throw new InvalidOperationException("attached RequestTermination failed at " +
                    (exception.Stage ?? "unknown"), exception);
            }
            Assert(!requestResult,
                "a headless console child should not claim a graceful window termination request");
            try
            {
                Assert(attached.ForceTerminate(),
                    "the attached process must remain force-terminable after the graceful request boundary");
            }
            catch (ProcessInspectionException exception)
            {
                throw new InvalidOperationException("attached ForceTerminate failed at " +
                    (exception.Stage ?? "unknown"), exception);
            }
            child.WaitForExit(5000);
            Assert(child.HasExited, "the attached process cleanup did not confirm exit");
        }
        finally
        {
            try
            {
                if (child != null && !child.HasExited)
                    child.Kill(entireProcessTree: true);
                child?.WaitForExit(5000);
            }
            catch { }
            child?.Dispose();
            Environment.SetEnvironmentVariable(signalVariable, previousSignal);
        }
    }

    private static void TestLaunchMonitoringRetriesTransientInspection()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        fixture.Adapter.ReadyOnLaunch = true;
        fixture.Adapter.LaunchedProcessExecutablePathFailures = 1;

        int exitCode = fixture.State.Execute(Request("restart"), _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(Request("status"), exitCode,
            Array.Empty<string>());

        Assert(exitCode == 0 && response.State == "READY" && response.ErrorCode == null,
            "bounded transient inspection failures must not quarantine a matching launch; exit=" + exitCode +
            ", state=" + response.State + ", error=" + response.ErrorCode + ", generation=" + response.Generation);
        Assert(fixture.Adapter.LaunchCalls == 1,
            "inspection retries must continue monitoring the original process without relaunching");
    }

    private static void TestInspectionQuarantineAcceptsMatchingReadiness()
    {
        using Fixture fixture = InspectionQuarantineFixture(1001);
        fixture.WriteReadiness("launch-inspection", 2, 101);

        BridgeRequest status = Request("status");
        int exitCode = fixture.State.Execute(status, _ => { }, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(status, exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.State == "READY" && response.ErrorCode == null &&
               response.Generation == 2 && !response.RequiresNewProcess,
            "matching PID, start identity, executable, launch, generation, and readiness must repair stale quarantine");
        Assert(fixture.Adapter.LaunchCalls == 0 && fixture.Adapter.TerminationRequests == 0,
            "quarantine repair must reuse the verified process without control actions");

        using Fixture mismatch = InspectionQuarantineFixture(1002);
        mismatch.WriteReadiness("launch-inspection", 2, 101);
        BridgeRequest mismatchStatus = Request("status");
        int mismatchExit = mismatch.State.Execute(mismatchStatus, _ => { }, () => true);
        JsonCommandResponse mismatchResponse = mismatch.State.CreateJsonResponse(mismatchStatus,
            mismatchExit, Array.Empty<string>());

        Assert(mismatchResponse.State == "ERROR" &&
               mismatchResponse.ErrorCode == ProcessInspection.ErrorCode,
            "late readiness must not repair quarantine when the process start identity differs");
        Assert(mismatch.Adapter.LaunchCalls == 0 && mismatch.Adapter.TerminationRequests == 0,
            "mismatched quarantine recovery must remain fail-closed");

        using Fixture duplicate = InspectionQuarantineFixture(1001);
        duplicate.Adapter.ExtraMatchingProcess = true;
        duplicate.WriteReadiness("launch-inspection", 2, 101);
        BridgeRequest duplicateStatus = Request("status");
        int duplicateExit = duplicate.State.Execute(duplicateStatus, _ => { }, () => true);
        JsonCommandResponse duplicateResponse = duplicate.State.CreateJsonResponse(duplicateStatus,
            duplicateExit, Array.Empty<string>());

        Assert(duplicateResponse.State == "ERROR" &&
               duplicateResponse.ErrorCode == ProcessInspection.ErrorCode,
            "late readiness must not repair quarantine unless a complete census finds exactly one RimWorld process");
        Assert(duplicate.Adapter.LaunchCalls == 0 && duplicate.Adapter.TerminationRequests == 0,
            "ambiguous multi-process recovery must remain fail-closed");
    }

    private static Fixture InspectionQuarantineFixture(long actualStartIdentity)
    {
        Fixture fixture = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.ERROR,
            Error = ProcessInspection.Message,
            ErrorCode = ProcessInspection.ErrorCode,
            LaunchId = "launch-inspection",
            LaunchGeneration = 2,
            TargetGeneration = 2,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            LaunchStartedUtc = ClockStart,
            RequiresNewProcess = true
        });
        fixture.Adapter.Add(new FakeProcess(101, actualStartIdentity, fixture.RimWorldPath));
        return fixture;
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

}
