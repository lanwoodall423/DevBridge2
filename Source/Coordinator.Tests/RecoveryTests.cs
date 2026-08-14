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
    private static void TestCrashIsolationSingleProject()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "single-project isolation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunchPredicate = () =>
        {
            List<string> active = ActiveMods(setup.Fixture.Root);
            return !active.Contains("lan.horticulture.novelseeds", StringComparer.OrdinalIgnoreCase);
        };

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.State == "READY",
            "single-project startup failure must recover to a ready control profile");
        Assert(response.ProfileMode == ModProfile.ProjectsMode &&
               response.RequestedProjects.SequenceEqual(new[] { "horticulture" }),
            "the immutable accepted project request must remain visible after recovery");
        Assert(response.CrashIsolation?.Status == "COMPLETED" &&
               response.CrashIsolation.DiagnosisCode == "PROJECT_OR_REQUIRED_DEPENDENCY_CLOSURE",
            "the incident must diagnose the project or its required dependency closure");
        Assert(response.CrashIsolation.Diagnoses.Count == 1 &&
               response.CrashIsolation.Diagnoses[0].ResolvedProjectPackageIds.SequenceEqual(
                   new[] { "lan.horticulture.novelseeds" }),
            "the single failing root must be persisted in the diagnosis");
        ModProfile diagnosedProfile = ModProfileResolver.Resolve(setup.Fixture.Root,
            response.BaselineFingerprint, response.CrashIsolation.Diagnoses[0].RequestedProjects,
            setup.Fixture.InstalledModsRoots);
        Assert(response.CrashIsolation.Diagnoses[0].ProfileFingerprint == diagnosedProfile.ProfileFingerprint,
            "diagnosis fingerprint must identify the exact valid candidate profile that failed");
        Assert(!response.CrashIsolation.OriginalDiagnosticMetadata.ContainsKey("lastFailureUtc"),
            "candidate failures must not mutate the immutable original diagnostic metadata");
        Assert(ActiveMods(setup.Fixture.Root).SequenceEqual(
                   ModProfileResolver.AlwaysOnPackageIds, StringComparer.OrdinalIgnoreCase),
            "successful isolation must leave the durable control profile installed");
        Assert(response.RuntimeProfileFingerprint != response.ProfileFingerprint,
            "runtime profile diagnostics must distinguish the restored control from the failing request");
    }

    private static void TestCrashIsolationProcessExit()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "process-exit isolation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunchPredicate = () =>
            !ActiveMods(setup.Fixture.Root).Contains("lan.horticulture.novelseeds",
                StringComparer.OrdinalIgnoreCase);
        setup.Fixture.Adapter.ExitOnLaunchPredicate = () =>
            ActiveMods(setup.Fixture.Root).Contains("lan.horticulture.novelseeds",
                StringComparer.OrdinalIgnoreCase);

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.CrashIsolation?.Status == "COMPLETED" &&
               response.CrashIsolation.OriginalFailureCode == "PROCESS_EXITED",
            "an observed process exit must be isolated and retained as original evidence");
        Assert(ActiveMods(setup.Fixture.Root).SequenceEqual(
                   ModProfileResolver.AlwaysOnPackageIds, StringComparer.OrdinalIgnoreCase),
            "process-exit isolation must restore the control profile");
    }

    private static void TestCrashIsolationControlFailure()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "control-failure isolation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunchPredicate = () => false;

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());

        Assert(exitCode != 0 && response.State == "ERROR" &&
               response.CrashIsolation?.Status == "ENVIRONMENTAL_FAILURE" &&
               response.CrashIsolation.DiagnosisCode == "ENVIRONMENTAL_BASELINE_FAILURE",
            "control failure must be classified as environmental");
        Assert(response.CrashIsolation.Diagnoses.Count == 0 &&
               response.ProfileMode == ModProfile.ProjectsMode &&
               response.RequestedProjects.SequenceEqual(new[] { "horticulture" }),
            "control failure must not attribute or discard the accepted project request");
        Assert(!response.CrashIsolation.OriginalProfileFingerprint.Equals(
                   response.CrashIsolation.OriginalLastKnownGoodFingerprint,
                   StringComparison.Ordinal),
             "the incident must retain distinct failing and control fingerprints");
    }

    private static void TestCrashIsolationRejectsStaleIdentity()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "stale-identity isolation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "stale-identity isolation: initial profile launch must succeed");

        setup.Fixture.Adapter.ReadyOnLaunch = false;
        setup.Fixture.Adapter.ThrowOnLaunch = true;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "aquaculture"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());

        Assert(exitCode != 0 && response.CrashIsolation == null &&
               response.ErrorCode == "LAUNCH_FAILED" && setup.Fixture.Adapter.LaunchCalls == 2,
            "a raw launch failure after the old process was drained must not attribute the stale PID to the new profile");
    }

    private static void TestCrashIsolationUnsafeCandidate()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "unsafe-candidate isolation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunchPredicate = () =>
        {
            List<string> active = ActiveMods(setup.Fixture.Root);
            return !(active.Contains("lan.horticulture.novelseeds", StringComparer.OrdinalIgnoreCase) &&
                     active.Contains("lan.aquaculture.fishing", StringComparer.OrdinalIgnoreCase));
        };
        setup.Fixture.Adapter.ExitOnLaunchPredicate = () =>
        {
            List<string> active = ActiveMods(setup.Fixture.Root);
            return active.Contains("lan.horticulture.novelseeds", StringComparer.OrdinalIgnoreCase) &&
                   active.Contains("lan.aquaculture.fishing", StringComparer.OrdinalIgnoreCase);
        };
        setup.Fixture.Adapter.ThrowOnLaunchedProcessHasExitedPredicate = () =>
            setup.Fixture.Adapter.LaunchCalls >= 4;

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture,aquaculture"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());

        Assert(exitCode != 0 && response.CrashIsolation?.Status == "ENVIRONMENTAL_FAILURE" &&
               response.CrashIsolation.Diagnoses.Count == 0 &&
               (response.CrashIsolation.DiagnosisCode == ProcessInspection.ErrorCode ||
                response.CrashIsolation.DiagnosisCode.Contains("QUARANTINED", StringComparison.Ordinal)) &&
               response.CrashIsolation.Diagnosis.Contains("quarantined", StringComparison.OrdinalIgnoreCase),
            "inspection/identity uncertainty during a candidate must be environmental and must not blame a project");
    }

    private static void TestCrashIsolationBudgetDoesNotReplenish()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "budget recovery: baseline capture must succeed");
        PersistedState persisted = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        persisted.Phase = BridgePhase.ISOLATING;
        persisted.IsolationLaunchesRemaining = 0;
        persisted.CrashIsolation = new CrashIsolationIncident
        {
            IncidentId = "budget-recovery-incident",
            Status = "RUNNING",
            Stage = "CONTROL",
            OriginalBaselineFingerprint = persisted.BaselineFingerprint,
            IsolationLaunchesRemaining = 7
        };
        setup.Fixture.WriteState(persisted);
        setup.Fixture.State = setup.Fixture.Reload();

        PersistedState normalized = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        Assert(normalized.IsolationLaunchesRemaining == 0 &&
               normalized.CrashIsolation.IsolationLaunchesRemaining == 0,
            "reload must reconcile an exhausted isolation budget to zero rather than replenish it");

        setup.Fixture.State.StartRecoveryWork();
        Assert(SpinWait.SpinUntil(() => setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>()).CrashIsolation?.Status == "ENVIRONMENTAL_FAILURE",
            TimeSpan.FromSeconds(3)) && setup.Fixture.Adapter.LaunchCalls == 0,
            "an exhausted recovered incident must terminate without a replacement launch");
    }

    private static void TestLegacyLaunchIgnoresBaselineRuntimeProfile()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "legacy-after-baseline: baseline capture must succeed");
        string custom = "<ModsConfigData><activeMods>\n" +
            "  <li>lan.devbridge2</li>\n  <li>user.custom.mod</li>\n" +
            "</activeMods></ModsConfigData>";
        File.WriteAllText(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"), custom,
            new UTF8Encoding(false));
        setup.Fixture.Adapter.ReadyOnLaunch = true;

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "human", 404, "--legacy-production"), _ => { }, () => true);
        Assert(exitCode == 0 && ActiveMods(setup.Fixture.Root).SequenceEqual(
                   new[] { "lan.devbridge2", "user.custom.mod" }, StringComparer.OrdinalIgnoreCase),
            "an explicit human legacy launch after baseline capture must preserve the user's current ModsConfig");
    }

    private static void TestCrashIsolationPrepareFailure()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "prepare-failure isolation: baseline capture must succeed");
        JsonCommandResponse baseline = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        ModProfile accepted = ModProfileResolver.Resolve(setup.Fixture.Root, baseline.BaselineFingerprint,
            new[] { "horticulture" }, setup.Fixture.InstalledModsRoots);
        ModProfile control = ModProfileResolver.CreateBaselineProfile(baseline.BaselineFingerprint);
        string attemptId = "prepare-failure-attempt";
        PersistedState persisted = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        persisted.Phase = BridgePhase.ISOLATING;
        persisted.RestartPending = true;
        persisted.TargetGeneration = 1;
        persisted.LaunchOwner = "isolation@" + RuntimeScope.ForRoot(setup.Fixture.Root);
        persisted.LaunchRequestKey = attemptId;
        persisted.ProfileMode = accepted.Mode;
        persisted.RequestedProjects = accepted.RequestedProjects.ToList();
        persisted.ResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList();
        persisted.ResolvedMods = accepted.ResolvedMods.ToList();
        persisted.ProfileFingerprint = accepted.ProfileFingerprint;
        persisted.CrashIsolation = new CrashIsolationIncident
        {
            IncidentId = "prepare-failure-incident",
            Status = "RUNNING",
            Stage = "CONTROL",
            OriginalProfileMode = accepted.Mode,
            OriginalRequestedProjects = accepted.RequestedProjects.ToList(),
            OriginalResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList(),
            OriginalResolvedMods = accepted.ResolvedMods.ToList(),
            OriginalProfileFingerprint = accepted.ProfileFingerprint,
            OriginalBaselineFingerprint = accepted.BaselineFingerprint,
            CurrentAttemptId = attemptId,
            CurrentAttemptKind = "CONTROL",
            CurrentAttemptFingerprint = control.ProfileFingerprint,
            CurrentAttemptProfile = PersistedProfileSnapshot.FromModProfile(control),
            IsolationLaunchesRemaining = 2
        };
        persisted.IsolationLaunchesRemaining = 2;
        setup.Fixture.WriteState(persisted);
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.Adapter.EnumerationIncomplete = true;
        setup.Fixture.State.StartRecoveryWork();

        Assert(SpinWait.SpinUntil(() => setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>()).CrashIsolation?.Status == "ENVIRONMENTAL_FAILURE",
            TimeSpan.FromSeconds(3)), "prepare failure did not become terminal");
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(response.CrashIsolation.Diagnoses.Count == 0 &&
               response.CrashIsolation.Attempts.Count == 1 &&
               response.CrashIsolation.Attempts[0].Result == "UNSAFE" &&
               setup.Fixture.Adapter.LaunchCalls == 0,
            "a failed isolation preparation must be durably quarantined without retrying or attributing a candidate");
    }

    private static void TestCrashIsolationUnsafeResultRecovery()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "unsafe-result recovery: baseline capture must succeed");
        JsonCommandResponse baseline = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        ModProfile accepted = ModProfileResolver.Resolve(setup.Fixture.Root, baseline.BaselineFingerprint,
            new[] { "horticulture" }, setup.Fixture.InstalledModsRoots);
        ModProfile control = ModProfileResolver.CreateBaselineProfile(baseline.BaselineFingerprint);
        PersistedState persisted = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        string attemptId = "unsafe-result-attempt";
        persisted.Generation = 0;
        persisted.Phase = BridgePhase.RESTARTING;
        persisted.RestartPending = true;
        persisted.TargetGeneration = 1;
        persisted.LaunchOwner = "isolation@" + RuntimeScope.ForRoot(setup.Fixture.Root);
        persisted.LaunchRequestKey = attemptId;
        persisted.ProfileMode = accepted.Mode;
        persisted.RequestedProjects = accepted.RequestedProjects.ToList();
        persisted.ResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList();
        persisted.ResolvedMods = accepted.ResolvedMods.ToList();
        persisted.ProfileFingerprint = accepted.ProfileFingerprint;
        persisted.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(control);
        persisted.LastKnownGoodProfile = PersistedProfileSnapshot.FromModProfile(control);
        persisted.IsolationLaunchesRemaining = 2;
        persisted.CrashIsolation = new CrashIsolationIncident
        {
            IncidentId = "unsafe-result-incident",
            Status = "RUNNING",
            Stage = "CONTROL",
            OriginalProfileMode = accepted.Mode,
            OriginalRequestedProjects = accepted.RequestedProjects.ToList(),
            OriginalResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList(),
            OriginalResolvedMods = accepted.ResolvedMods.ToList(),
            OriginalProfileFingerprint = accepted.ProfileFingerprint,
            OriginalBaselineFingerprint = accepted.BaselineFingerprint,
            CurrentAttemptId = attemptId,
            CurrentAttemptKind = "CONTROL",
            CurrentAttemptFingerprint = control.ProfileFingerprint,
            CurrentAttemptProfile = PersistedProfileSnapshot.FromModProfile(control),
            CurrentAttemptResult = "UNSAFE",
            CurrentAttemptFailureCode = "ISOLATION_PREPARE_FAILED",
            CurrentAttemptFailureDetail = "persisted preparation failure",
            IsolationLaunchesRemaining = 2
        };
        setup.Fixture.WriteState(persisted);
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.State.StartRecoveryWork();

        Assert(SpinWait.SpinUntil(() => setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>()).CrashIsolation?.Status == "ENVIRONMENTAL_FAILURE",
            TimeSpan.FromSeconds(3)), "persisted unsafe result did not become terminal");
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(response.ErrorCode == "ISOLATION_PREPARE_FAILED" &&
               response.CrashIsolation.Diagnoses.Count == 0 &&
               response.CrashIsolation.Attempts.Count == 1 &&
               response.CrashIsolation.Attempts[0].Result == "UNSAFE" &&
               setup.Fixture.Adapter.LaunchCalls == 0,
            "a persisted unsafe result must resume fail-closed recovery without relaunching or attributing a project");
    }

    private static void TestCrashIsolationProfileMismatch()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "profile-mismatch recovery: baseline capture must succeed");
        JsonCommandResponse baseline = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        ModProfile accepted = ModProfileResolver.Resolve(setup.Fixture.Root, baseline.BaselineFingerprint,
            new[] { "horticulture" }, setup.Fixture.InstalledModsRoots);
        ModProfile control = ModProfileResolver.CreateBaselineProfile(baseline.BaselineFingerprint);
        string attemptId = "profile-mismatch-attempt";
        PersistedState persisted = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        persisted.Phase = BridgePhase.LOADING;
        persisted.RestartPending = true;
        persisted.TargetGeneration = 1;
        persisted.LaunchOwner = "isolation@" + RuntimeScope.ForRoot(setup.Fixture.Root);
        persisted.LaunchRequestKey = attemptId;
        persisted.ProfileMode = accepted.Mode;
        persisted.RequestedProjects = accepted.RequestedProjects.ToList();
        persisted.ResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList();
        persisted.ResolvedMods = accepted.ResolvedMods.ToList();
        persisted.ProfileFingerprint = accepted.ProfileFingerprint;
        persisted.ProcessId = 101;
        persisted.ProcessStartUtcTicks = 1001;
        persisted.LaunchProfileFingerprint = "different-candidate-fingerprint";
        persisted.LaunchProfileInstalled = true;
        persisted.LaunchAttemptStarted = true;
        persisted.IsolationLaunchesRemaining = 2;
        persisted.CrashIsolation = new CrashIsolationIncident
        {
            IncidentId = "profile-mismatch-incident",
            Status = "RUNNING",
            Stage = "FINAL_CONTROL",
            OriginalProfileMode = accepted.Mode,
            OriginalRequestedProjects = accepted.RequestedProjects.ToList(),
            OriginalResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList(),
            OriginalResolvedMods = accepted.ResolvedMods.ToList(),
            OriginalProfileFingerprint = accepted.ProfileFingerprint,
            OriginalBaselineFingerprint = accepted.BaselineFingerprint,
            CurrentAttemptId = attemptId,
            CurrentAttemptKind = "FINAL_CONTROL",
            CurrentAttemptFingerprint = control.ProfileFingerprint,
            CurrentAttemptProfile = PersistedProfileSnapshot.FromModProfile(control),
            IsolationLaunchesRemaining = 2
        };
        setup.Fixture.WriteState(persisted);
        setup.Fixture.Adapter.Add(new FakeProcess(101, 1001, setup.Fixture.RimWorldPath));
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.State.StartRecoveryWork();

        Assert(SpinWait.SpinUntil(() => setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>()).CrashIsolation?.Status == "ENVIRONMENTAL_FAILURE",
            TimeSpan.FromSeconds(3)), "profile mismatch did not become terminal");
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(response.CrashIsolation.DiagnosisCode == "CRASH_ISOLATION_RECOVERY_QUARANTINED" &&
               setup.Fixture.Adapter.LaunchCalls == 0 &&
               setup.Fixture.Adapter.TerminationRequests == 0,
            "a recovered candidate/profile mismatch must not duplicate-launch or stop an unidentified process");
    }

    private static void TestCrashIsolationStatusAction()
    {
        using Fixture fixture = new(new PersistedState
        {
            Phase = BridgePhase.ISOLATING,
            CrashIsolation = new CrashIsolationIncident
            {
                IncidentId = "status-incident",
                Status = "RUNNING",
                Stage = "CONTROL",
                IsolationLaunchesRemaining = 1
            },
            IsolationLaunchesRemaining = 1
        });
        JsonCommandResponse response = fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(response.NextAction.Contains("Crash isolation is running", StringComparison.Ordinal) &&
               response.NextAction.Contains("Do not retry", StringComparison.Ordinal),
            "status JSON must direct agents to wait for active isolation rather than retrying");
    }

    private static void TestCrashIsolationRecovery()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "isolation recovery: baseline capture must succeed");
        JsonCommandResponse baselineResponse = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        ModProfile accepted = ModProfileResolver.Resolve(setup.Fixture.Root,
            baselineResponse.BaselineFingerprint, new[] { "horticulture" },
            setup.Fixture.InstalledModsRoots);
        ModProfile control = ModProfileResolver.CreateBaselineProfile(baselineResponse.BaselineFingerprint);
        string attemptId = "iso-recovered-final-control";
        CrashIsolationIncident incident = new()
        {
            IncidentId = "iso-recovered-incident",
            Status = "RUNNING",
            Stage = "FINAL_CONTROL",
            OriginalProfileMode = accepted.Mode,
            OriginalRequestedProjects = accepted.RequestedProjects.ToList(),
            OriginalResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList(),
            OriginalResolvedMods = accepted.ResolvedMods.ToList(),
            OriginalProfileFingerprint = accepted.ProfileFingerprint,
            OriginalBaselineFingerprint = accepted.BaselineFingerprint,
            OriginalLastKnownGoodFingerprint = control.ProfileFingerprint,
            OriginalGeneration = 1,
            OriginalLaunchId = "failed-original",
            OriginalProcessId = 77,
            OriginalProcessStartUtcTicks = 7700,
            OriginalFailureUtc = ClockStart,
            OriginalFailurePhase = "LOADING",
            OriginalFailureCode = "READINESS_TIMEOUT",
            OriginalFailureDetail = "persisted recovery fixture",
            CurrentAttemptId = attemptId,
            CurrentAttemptFingerprint = control.ProfileFingerprint,
            CurrentAttemptKind = "FINAL_CONTROL",
            CurrentAttemptProfile = PersistedProfileSnapshot.FromModProfile(control),
            CurrentAttemptProjects = new List<string>(),
            CurrentAttemptProfileInstalled = true,
            IsolationLaunchesRemaining = 10
        };
        PersistedState recovered = new()
        {
            Generation = 1,
            Phase = BridgePhase.LOADING,
            LaunchId = "recovered-final",
            LaunchGeneration = 2,
            TargetGeneration = 2,
            RestartPending = true,
            LaunchOwner = "isolation@" + RuntimeScope.ForRoot(setup.Fixture.Root),
            LaunchRequestKey = attemptId,
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            LaunchStartedUtc = ClockStart,
            ProfileMode = accepted.Mode,
            RequestedProjects = accepted.RequestedProjects.ToList(),
            ResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList(),
            ResolvedMods = accepted.ResolvedMods.ToList(),
            ProfileFingerprint = accepted.ProfileFingerprint,
            BaselineFingerprint = accepted.BaselineFingerprint,
            LastKnownGoodProfile = PersistedProfileSnapshot.FromModProfile(control),
            RuntimeProfile = PersistedProfileSnapshot.FromModProfile(control),
            CrashIsolation = incident,
            LaunchProfileFingerprint = control.ProfileFingerprint,
            LaunchProfileInstalled = true,
            LaunchAttemptStarted = true,
            IsolationLaunchesRemaining = 10
        };
        setup.Fixture.Adapter.Add(new FakeProcess(101, 1001, setup.Fixture.RimWorldPath));
        setup.Fixture.WriteState(recovered);
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.WriteReadiness("recovered-final", 2, 101);
        setup.Fixture.State.StartRecoveryWork();

        Assert(SpinWait.SpinUntil(() =>
        {
            JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
                Request("status"), 0, Array.Empty<string>());
            return response.State == "READY" && response.CrashIsolation?.Status == "COMPLETED";
        }, TimeSpan.FromSeconds(3)), "durable in-flight isolation did not resume");
        JsonCommandResponse final = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(final.CrashIsolation.OriginalProfileFingerprint == accepted.ProfileFingerprint &&
               final.CrashIsolation.OriginalFailureCode == "READINESS_TIMEOUT" &&
               setup.Fixture.Adapter.LaunchCalls == 0,
            "recovery must retain original evidence and avoid a duplicate final-control launch");
    }

    private static void TestCrashIsolationTerminalAttemptRecovery()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "terminal-attempt recovery: baseline capture must succeed");
        JsonCommandResponse baseline = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        ModProfile accepted = ModProfileResolver.Resolve(setup.Fixture.Root, baseline.BaselineFingerprint,
            new[] { "horticulture" }, setup.Fixture.InstalledModsRoots);
        ModProfile control = ModProfileResolver.CreateBaselineProfile(baseline.BaselineFingerprint);
        PersistedState recovered = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        recovered.Generation = 1;
        recovered.Phase = BridgePhase.ISOLATING;
        recovered.RestartPending = true;
        recovered.TargetGeneration = 2;
        recovered.LaunchOwner = "isolation@" + RuntimeScope.ForRoot(setup.Fixture.Root);
        recovered.LaunchRequestKey = "terminal-attempt";
        recovered.LaunchId = "terminal-launch";
        recovered.LaunchGeneration = 2;
        recovered.LaunchStartedUtc = ClockStart;
        recovered.ProcessId = 101;
        recovered.ProcessStartUtcTicks = 1001;
        recovered.ProfileMode = accepted.Mode;
        recovered.RequestedProjects = accepted.RequestedProjects.ToList();
        recovered.ResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList();
        recovered.ResolvedMods = accepted.ResolvedMods.ToList();
        recovered.ProfileFingerprint = accepted.ProfileFingerprint;
        recovered.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(control);
        recovered.LastKnownGoodProfile = PersistedProfileSnapshot.FromModProfile(control);
        recovered.LaunchProfileFingerprint = control.ProfileFingerprint;
        recovered.LaunchProfileInstalled = true;
        recovered.LaunchAttemptStarted = true;
        recovered.IsolationLaunchesRemaining = 3;
        recovered.CrashIsolation = new CrashIsolationIncident
        {
            IncidentId = "terminal-attempt-incident",
            Status = "RUNNING",
            Stage = "FINAL_CONTROL",
            OriginalProfileMode = accepted.Mode,
            OriginalRequestedProjects = accepted.RequestedProjects.ToList(),
            OriginalResolvedProjectPackageIds = accepted.ResolvedProjectPackageIds.ToList(),
            OriginalResolvedMods = accepted.ResolvedMods.ToList(),
            OriginalProfileFingerprint = accepted.ProfileFingerprint,
            OriginalBaselineFingerprint = accepted.BaselineFingerprint,
            CurrentAttemptId = "terminal-attempt",
            CurrentAttemptKind = "FINAL_CONTROL",
            CurrentAttemptFingerprint = control.ProfileFingerprint,
            CurrentAttemptProfile = PersistedProfileSnapshot.FromModProfile(control),
            CurrentAttemptResult = "PASS",
            CurrentAttemptProfileInstalled = true,
            IsolationLaunchesRemaining = 3
        };
        setup.Fixture.Adapter.Add(new FakeProcess(101, 1001, setup.Fixture.RimWorldPath));
        // Model the side effect that the ready FINAL_CONTROL attempt already
        // installed before the coordinator crashed. Recovery must retain this
        // profile without stopping or launching another process.
        File.WriteAllText(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"),
            "<ModsConfigData><activeMods>" +
            string.Join(string.Empty, ModProfileResolver.AlwaysOnPackageIds.Select(value =>
                "<li>" + value + "</li>")) +
            "</activeMods></ModsConfigData>");
        setup.Fixture.WriteState(recovered);
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.State.StartRecoveryWork();

        Assert(SpinWait.SpinUntil(() => setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>()).CrashIsolation?.Status == "COMPLETED",
            TimeSpan.FromSeconds(3)), "persisted terminal attempt did not complete");
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(setup.Fixture.Adapter.LaunchCalls == 0 && setup.Fixture.Adapter.TerminationRequests == 0 &&
               response.State == "READY" && ActiveMods(setup.Fixture.Root).SequenceEqual(
                   ModProfileResolver.AlwaysOnPackageIds, StringComparer.OrdinalIgnoreCase),
            "a persisted terminal attempt must resume exactly once without relaunching or stopping the control process");
    }

    private static void TestCrashIsolationSafeRemainder()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "safe-remainder isolation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunchPredicate = () =>
            !ActiveMods(setup.Fixture.Root).Contains("lan.horticulture.novelseeds",
                StringComparer.OrdinalIgnoreCase);

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture,wildlife"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());
        ModProfile safeRemainder = ModProfileResolver.Resolve(setup.Fixture.Root,
            response.BaselineFingerprint, new[] { "wildlife" }, setup.Fixture.InstalledModsRoots);

        Assert(exitCode == 0 && response.State == "READY" &&
               response.CrashIsolation?.Status == "COMPLETED" &&
               response.CrashIsolation.Diagnoses.Count == 1 &&
               response.CrashIsolation.Diagnoses[0].RequestedProjects.SequenceEqual(
                   new[] { "horticulture" }) &&
               response.RuntimeProfileFingerprint == safeRemainder.ProfileFingerprint &&
               ActiveMods(setup.Fixture.Root).SequenceEqual(safeRemainder.ResolvedMods,
                   StringComparer.OrdinalIgnoreCase),
            "isolation must restore the maximal passing remainder instead of discarding unrelated healthy projects");
    }

    private static void TestCrashIsolationIntermittent()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "intermittent isolation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunchPredicate = () => setup.Fixture.Adapter.LaunchCalls > 1;

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.CrashIsolation?.Status == "COMPLETED" &&
               response.CrashIsolation.DiagnosisCode == "INTERMITTENT_PROFILE_FAILURE" &&
               response.CrashIsolation.Diagnoses.Count == 0 &&
               response.CrashIsolation.OriginalRequestedProjects.SequenceEqual(new[] { "horticulture" }) &&
               response.CrashIsolation.OriginalProfileFingerprint == response.ProfileFingerprint,
            "a failure that does not reproduce must remain explicitly intermittent and unattributed");
    }

    private static void TestCrashIsolationMinimalSet()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "minimal-set isolation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunchPredicate = () =>
        {
            List<string> active = ActiveMods(setup.Fixture.Root);
            return !(active.Contains("lan.horticulture.novelseeds", StringComparer.OrdinalIgnoreCase) &&
                     active.Contains("lan.aquaculture.fishing", StringComparer.OrdinalIgnoreCase));
        };

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture,aquaculture"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.CrashIsolation?.Status == "COMPLETED",
            "minimal-set isolation must complete");
        Assert(response.CrashIsolation.DiagnosisCode == "MINIMAL_INCOMPATIBLE_PROJECT_SET" &&
               response.CrashIsolation.Diagnoses.Count == 1 &&
               response.CrashIsolation.Diagnoses[0].ResolvedProjectPackageIds.SequenceEqual(
                   new[] { "lan.aquaculture.fishing", "lan.horticulture.novelseeds" }),
            "isolation must report the smallest failing combination rather than a last-enabled mod");
        Assert(ActiveMods(setup.Fixture.Root).SequenceEqual(
                   ModProfileResolver.AlwaysOnPackageIds, StringComparer.OrdinalIgnoreCase),
            "minimal-set isolation must restore the control profile");
    }

    private static void TestCrashIsolationMultipleSets()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "multiple-set isolation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunchPredicate = () =>
        {
            List<string> active = ActiveMods(setup.Fixture.Root);
            return !active.Contains("lan.aquaculture.fishing", StringComparer.OrdinalIgnoreCase) &&
                   !active.Contains("lan.wildlife", StringComparer.OrdinalIgnoreCase);
        };

        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "aquaculture,wildlife"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());

        Assert(exitCode == 0 && response.CrashIsolation?.Status == "COMPLETED",
            "multiple-set isolation must complete");
        Assert(response.CrashIsolation.DiagnosisCode == "MULTIPLE_INDEPENDENT_FAILING_PROJECT_SETS" &&
               response.CrashIsolation.Diagnoses.Count == 2,
            "independent failing roots must be reported separately");
        Assert(response.CrashIsolation.Diagnoses.SelectMany(value => value.ResolvedProjectPackageIds)
                   .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                   .SequenceEqual(new[] { "lan.aquaculture.fishing", "lan.wildlife" },
                       StringComparer.OrdinalIgnoreCase),
            "multiple diagnoses must contain both independent roots");
        Assert(ActiveMods(setup.Fixture.Root).SequenceEqual(
                   ModProfileResolver.AlwaysOnPackageIds, StringComparer.OrdinalIgnoreCase),
            "multiple-set isolation must restore the control profile");
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

}
