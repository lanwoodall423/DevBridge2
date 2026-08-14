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
        Assert(mod.Contains("built-in Dev Quicktest callback queued; no UI button click was performed", StringComparison.Ordinal) &&
            !mod.Contains("button activation queued", StringComparison.Ordinal),
            "quicktest logging must describe callback queuing without implying a UI click");
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

    private static void TestQuicktestFailureArtifactContract()
    {
        using Fixture fixture = new(new PersistedState { Generation = 1, Phase = BridgePhase.STOPPED });
        QuicktestFailureRecord record = new()
        {
            SchemaVersion = QuicktestFailureArtifact.CurrentSchemaVersion,
            LaunchId = new string('l', 300),
            Generation = 2,
            ProcessId = 123,
            ProcessStartUtcTicks = 456,
            ProfileFingerprint = new string('p', 300),
            BaselineFingerprint = new string('b', 300),
            ProfileMode = new string('m', 100),
            TimestampUtc = fixture.Clock.UtcNow,
            FailurePhase = new string('f', 300),
            FailureCode = QuicktestFailureArtifact.StableFailureCode,
            ExceptionType = new string('t', 400),
            ExceptionMessage = new string('e', 800),
            DiagnosticDetail = new string('d', 4000)
        };

        Assert(QuicktestFailureArtifact.TryWrite(fixture.Root, record, out string error) &&
               string.IsNullOrWhiteSpace(error), "failure artifact must be written atomically");
        string path = QuicktestFailureArtifact.PathFor(fixture.Root);
        QuicktestFailureRecord bounded = JsonSerializer.Deserialize<QuicktestFailureRecord>(
            File.ReadAllText(path), Program.JsonOptions);
        Assert(bounded.LaunchId.Length == QuicktestFailureArtifact.MaxLaunchIdLength &&
               bounded.DiagnosticDetail.Length == QuicktestFailureArtifact.MaxDiagnosticDetailLength,
            "failure diagnostics must be bounded before persistence");

        record.LaunchId = "replacement";
        Assert(QuicktestFailureArtifact.TryWrite(fixture.Root, record, out error),
            "a second generation-specific artifact write must replace the first atomically");
        QuicktestFailureRecord replaced = JsonSerializer.Deserialize<QuicktestFailureRecord>(
            File.ReadAllText(path), Program.JsonOptions);
        Assert(replaced.LaunchId == "replacement" &&
               !Directory.GetFiles(Path.Combine(fixture.Root, "Runtime"), "quicktest-failure.json.tmp-*").Any(),
            "only the complete replacement artifact may remain visible");
        QuicktestFailureArtifact.Invalidate(fixture.Root);
        Assert(!File.Exists(path), "launch cleanup must invalidate the prior failure artifact");
    }

    private static void TestQuicktestFailureIsolation()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "quicktest failure: baseline capture must succeed");
        PersistedState dirty = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        dirty.SessionDirty = true;
        setup.Fixture.WriteState(dirty);
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.Adapter.ReadyOnLaunchPredicate = () => setup.Fixture.Adapter.LaunchCalls > 1;
        setup.Fixture.Adapter.QuicktestFailureOnLaunch = (request, process) =>
            setup.Fixture.Adapter.LaunchCalls == 1 ? MatchingFailure(setup.Fixture, request, process) : null;

        DateTime started = DateTime.UtcNow;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "wildlife"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());

        Assert((DateTime.UtcNow - started) < TimeSpan.FromSeconds(10),
            "a durable callback failure must be detected without the readiness timeout");
        Assert(exitCode == 0 && response.State == "READY" &&
               response.CrashIsolation?.Status == "COMPLETED" &&
               response.CrashIsolation.OriginalFailureCode == QuicktestFailureArtifact.StableFailureCode &&
               response.CrashIsolation.OriginalFailurePhase == "Root_Play.SetupForQuickTestPlay" &&
               response.CrashIsolation.OriginalFailureExceptionType == "System.NullReferenceException" &&
               response.CrashIsolation.OriginalFailureDiagnosticDetail.Contains("world generation", StringComparison.Ordinal) &&
               setup.Fixture.Adapter.TerminationRequests > 0,
            "matching quicktest failure must preserve immutable evidence and stop the exact failed process for isolation: " +
            JsonSerializer.Serialize(response, Program.JsonOptions) +
            "; launches=" + setup.Fixture.Adapter.LaunchCalls +
            "; terminations=" + setup.Fixture.Adapter.TerminationRequests);
        Assert(response.SessionDirty == false,
            "a verified fresh accepted project launch must clear historical session dirtiness after readiness");
    }

    private static void TestQuicktestFailureRejectsInvalidRecords()
    {
        Action<string, Action<QuicktestFailureRecord>, string> runCase = (name, mutate, raw) =>
        {
            using ProfileSetup setup = ProfileSetup.Create();
            Assert(setup.CaptureBaseline(), name + ": baseline capture must succeed");
            setup.Fixture.Adapter.QuicktestFailureOnLaunch = (request, process) =>
            {
                QuicktestFailureRecord record = MatchingFailure(setup.Fixture, request, process);
                mutate?.Invoke(record);
                return record;
            };
            setup.Fixture.Adapter.RawQuicktestFailureJsonOnLaunch = raw;
            int exitCode = setup.Fixture.State.Execute(
                Request("restart", "agent", 1, "--projects", "none"), _ => { }, () => true);
            JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
                Request("status"), exitCode, Array.Empty<string>());
            string runtime = Path.Combine(setup.Fixture.Root, "Runtime");
            Assert(exitCode != 0 && response.ErrorCode == "READINESS_TIMEOUT" &&
                   response.CrashIsolation == null && setup.Fixture.Adapter.LaunchCalls == 1 &&
                   setup.Fixture.Adapter.TerminationRequests == 0 &&
                   Directory.GetFiles(runtime, "quicktest-failure.rejected-*.json").Length == 1,
                name + ": invalid evidence must not trigger attribution, stopping, or a replacement launch");
        };

        runCase("wrong PID", record => record.ProcessId++, null);
        runCase("wrong launch ID", record => record.LaunchId = "stale-launch", null);
        runCase("wrong generation", record => record.Generation++, null);
        runCase("wrong start identity", record => record.ProcessStartUtcTicks++, null);
        runCase("wrong fingerprint", record => record.ProfileFingerprint = "wrong-profile", null);
        runCase("stale timestamp", record => record.TimestampUtc = ClockStart.AddMinutes(-10), null);
        runCase("future timestamp", record => record.TimestampUtc = ClockStart.AddMinutes(10), null);
        runCase("malformed JSON", null, "{not-json");
    }

    private static void TestQuicktestFailureReadinessConflict()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "readiness conflict: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        setup.Fixture.Adapter.QuicktestFailureOnLaunch = (request, process) =>
            MatchingFailure(setup.Fixture, request, process);

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "none"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());
        Assert(exitCode != 0 && response.ErrorCode == "QUICKTEST_READINESS_CONFLICT" &&
               response.CrashIsolation == null &&
               response.TerminalFailureCode == QuicktestFailureArtifact.StableFailureCode &&
               response.TerminalFailurePhase == "Root_Play.SetupForQuickTestPlay" &&
               response.TerminalFailureDetail.Contains("ambiguous", StringComparison.OrdinalIgnoreCase),
            "matching success and terminal failure artifacts must fail closed as an environmental conflict");
    }

    private static QuicktestFailureRecord MatchingFailure(Fixture fixture,
        ProcessLaunchRequest request, FakeProcess process)
    {
        IReadOnlyDictionary<string, string> environment = request.Environment;
        return new QuicktestFailureRecord
        {
            SchemaVersion = QuicktestFailureArtifact.CurrentSchemaVersion,
            LaunchId = environment["DEVBRIDGE_LAUNCH_ID"],
            Generation = int.Parse(environment["DEVBRIDGE_GENERATION"]),
            ProcessId = process.Id,
            ProcessStartUtcTicks = process.StartIdentity,
            ProfileFingerprint = environment["DEVBRIDGE_PROFILE_FINGERPRINT"],
            BaselineFingerprint = environment["DEVBRIDGE_BASELINE_FINGERPRINT"],
            ProfileMode = environment["DEVBRIDGE_PROFILE_MODE"],
            TimestampUtc = fixture.Clock.UtcNow,
            FailurePhase = "Root_Play.SetupForQuickTestPlay",
            FailureCode = QuicktestFailureArtifact.StableFailureCode,
            ExceptionType = "System.NullReferenceException",
            ExceptionMessage = "world generation object was null",
            DiagnosticDetail = "NullReferenceException: world generation failed before playable readiness."
        };
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

}
