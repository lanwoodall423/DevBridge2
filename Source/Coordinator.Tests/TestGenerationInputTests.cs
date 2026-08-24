using System.Text.Json;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestTypedProjectResolveInputs()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        string statePath = Path.Combine(setup.Fixture.Root, "Runtime", "state.json");
        string modsPath = Path.Combine(setup.Fixture.Root, "ModsConfig.xml");
        byte[] stateBefore = File.ReadAllBytes(statePath);
        byte[] modsBefore = File.ReadAllBytes(modsPath);

        JsonCommandResponse response = ExecuteInputResolve(setup.Fixture, "horticulture",
            "quicktest=true", "quicktestTimeoutSeconds=45", "quicktestVariant=builtin-dev");
        ProjectResolutionResult plan = response.ProjectResolution;
        Assert(response.Success && plan != null && plan.TestInputs.Count == 3,
            "declared boolean, integer, and enum inputs must resolve into a structured plan");
        Assert(plan.TestInputs.Any(value => value.Name == "quicktest" && value.Value == "true") &&
               plan.TestInputs.Any(value => value.Name == "quicktestTimeoutSeconds" && value.Value == "45") &&
               plan.TestInputs.Any(value => value.Name == "quicktestVariant" && value.Value == "builtin-dev"),
            "inputs must be normalized to their declared canonical values");
        Assert(File.ReadAllBytes(statePath).SequenceEqual(stateBefore) &&
               File.ReadAllBytes(modsPath).SequenceEqual(modsBefore) &&
               setup.Fixture.Adapter.LaunchCalls == 0,
            "typed resolve must not mutate state or ModsConfig or launch a process");

        JsonCommandResponse equivalent = ExecuteInputResolve(setup.Fixture, "HORTICULTURE",
            "quicktest=TRUE", "quicktestTimeoutSeconds=45", "quicktestVariant=BUILTIN-DEV");
        Assert(JsonSerializer.Serialize(plan, Program.JsonOptions) ==
               JsonSerializer.Serialize(equivalent.ProjectResolution, Program.JsonOptions) &&
               plan.ProfileFingerprint == equivalent.ProjectResolution.ProfileFingerprint,
            "equivalent normalized inputs must have deterministic JSON and fingerprints");

        JsonCommandResponse changed = ExecuteInputResolve(setup.Fixture, "horticulture",
            "quicktest=false", "quicktestVariant=disabled");
        Assert(changed.Success && changed.ProjectResolution.ProfileFingerprint != plan.ProfileFingerprint &&
               changed.ProjectResolution.TestInputs.Any(value => value.Name == "quicktest" && value.Value == "false"),
            "a semantic test-input change must change the prospective profile fingerprint: success=" +
            changed.Success + " code=" + changed.ErrorCode + " first=" + plan.ProfileFingerprint +
            " changed=" + changed.ProjectResolution?.ProfileFingerprint + " inputs=" +
            string.Join(",", changed.ProjectResolution?.TestInputs?.Select(value => value.Name + "=" + value.Value) ??
                Enumerable.Empty<string>()));
    }

    private static void TestTypedProjectResolveInputFailures()
    {
        using ProfileSetup unknown = ProfileSetup.Create();
        AssertInputFailure(unknown.Fixture, "quicktestTypo=true", "TEST_INPUT_UNKNOWN");

        using ProfileSetup invalidType = ProfileSetup.Create();
        AssertInputFailure(invalidType.Fixture, "quicktest=maybe", "TEST_INPUT_INVALID_TYPE");

        using ProfileSetup outOfRange = ProfileSetup.Create();
        AssertInputFailure(outOfRange.Fixture, "quicktestTimeoutSeconds=4", "TEST_INPUT_OUT_OF_RANGE");

        using ProfileSetup invalidEnum = ProfileSetup.Create();
        AssertInputFailure(invalidEnum.Fixture, "quicktestVariant=arbitrary-shell", "TEST_INPUT_VALUE_NOT_ALLOWED");

        using ProfileSetup unsupported = ProfileSetup.Create();
        AssertInputFailure(unsupported.Fixture, "quicktest=false", "TEST_INPUT_NOT_SUPPORTED_FOR_PROFILE",
            useLegacyRestart: true);
    }

    private static void TestTypedInputsBindToGenerationHistoryAndStayFrozen()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int restart = setup.Fixture.State.Execute(Request("restart", "agent", 1,
            "--projects", "horticulture", "--input", "quicktestTimeoutSeconds=45"), _ => { }, () => true);
        Assert(restart == 0, "a validated typed input must be accepted by the real restart workflow");
        Assert(setup.Fixture.Adapter.LastLaunchArguments.Count == 0 &&
               setup.Fixture.Adapter.LastLaunchEnvironment.ContainsKey("DEVBRIDGE_QUICKTEST_REQUESTED") &&
               setup.Fixture.Adapter.LastLaunchEnvironment.ContainsKey("DEVBRIDGE_QUICKTEST_TIMEOUT_SECONDS") &&
               setup.Fixture.Adapter.LastLaunchEnvironment["DEVBRIDGE_QUICKTEST_TIMEOUT_SECONDS"] == "45" &&
               setup.Fixture.Adapter.LastLaunchEnvironment.Keys.All(IsKnownLaunchEnvironmentKey),
            "launches must use only fixed DevBridge environment keys and no raw argv");

        string manifestPath = Path.Combine(setup.Fixture.Root, "Runtime", "generations", "1.json");
        GenerationManifest manifest = JsonSerializer.Deserialize<GenerationManifest>(
            File.ReadAllText(manifestPath), Program.JsonOptions);
        Assert(manifest.Profile.TestInputs.Any(value => value.Name == "quicktestTimeoutSeconds" && value.Value == "45") &&
               manifest.Readiness.QuicktestRequired && manifest.Readiness.QuicktestTimeoutSeconds == 45,
            "immutable manifests must retain normalized inputs and runtime evidence");

        JsonCommandResponse laterPlan = ExecuteInputResolve(setup.Fixture, "horticulture",
            "quicktestTimeoutSeconds=30");
        Assert(laterPlan.ProjectResolution.Comparison != null &&
               laterPlan.ProjectResolution.Comparison.TestInputsChanged &&
               laterPlan.ProjectResolution.WouldRequireRestart,
            "a changed input must compare against the pinned manifest and require a new generation");
        GenerationManifest unchanged = JsonSerializer.Deserialize<GenerationManifest>(
            File.ReadAllText(manifestPath), Program.JsonOptions);
        Assert(unchanged.Profile.TestInputs.Any(value => value.Name == "quicktestTimeoutSeconds" && value.Value == "45"),
            "a later plan must not modify frozen generation inputs");

        PersistedState pending = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        pending.RestartPending = true;
        pending.TargetGeneration = 2;
        pending.Phase = BridgePhase.DRAINING;
        pending.LaunchOwner = "agent@1";
        pending.LaunchRequestKey = "restart-2";
        setup.Fixture.WriteState(pending);
        setup.Fixture.State = setup.Fixture.Reload();
        int conflict = setup.Fixture.State.Execute(Request("restart", "agent", 1,
            "--projects", "horticulture", "--input", "quicktestTimeoutSeconds=30"), _ => { }, () => true);
        PersistedState pendingAfter = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        Assert(conflict != 0 && pendingAfter.FrozenTestInputs.Any(value =>
                   value.Name == "quicktestTimeoutSeconds" && value.Value == "45"),
            "incompatible pending generation input requests must fail deterministically without replacing frozen inputs: result=" +
            conflict + " frozen=" + string.Join(",", pendingAfter.FrozenTestInputs.Select(value => value.Name + "=" + value.Value)));

        string historyJson = File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "generation-history.json"));
        Assert(historyJson.Contains("quicktestTimeoutSeconds", StringComparison.Ordinal) &&
               !historyJson.Contains("DEVBRIDGE_ROOT=", StringComparison.Ordinal) &&
               !historyJson.Contains("secret", StringComparison.OrdinalIgnoreCase),
            "history must retain normalized inputs without arbitrary environment or secret data");
    }

    private static void TestTypedInputFailuresAreMutationFree()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        string statePath = Path.Combine(setup.Fixture.Root, "Runtime", "state.json");
        string modsPath = Path.Combine(setup.Fixture.Root, "ModsConfig.xml");
        byte[] stateBefore = File.ReadAllBytes(statePath);
        byte[] modsBefore = File.ReadAllBytes(modsPath);
        int result = setup.Fixture.State.Execute(Request("restart", "agent", 1,
            "--projects", "horticulture", "--input", "DEVBRIDGE_ROOT=C:\\escape"), _ => { }, () => true);
        Assert(result != 0 && setup.Fixture.Adapter.LaunchCalls == 0 &&
               File.ReadAllBytes(statePath).SequenceEqual(stateBefore) &&
               File.ReadAllBytes(modsPath).SequenceEqual(modsBefore),
            "unknown input names must fail before any durable or runtime mutation: result=" + result +
            " launches=" + setup.Fixture.Adapter.LaunchCalls + " stateEqual=" +
            File.ReadAllBytes(statePath).SequenceEqual(stateBefore) + " modsEqual=" +
            File.ReadAllBytes(modsPath).SequenceEqual(modsBefore));
    }

    private static JsonCommandResponse ExecuteInputResolve(Fixture fixture, string aliases,
        params string[] inputValues)
    {
        List<string> arguments = new() { "resolve", aliases };
        foreach (string input in inputValues)
        {
            arguments.Add("--input");
            arguments.Add(input);
        }
        arguments.Add("--json");
        return Execute(fixture, Request("project", "agent", 1, arguments.ToArray()));
    }

    private static void AssertInputFailure(Fixture fixture, string input, string expectedCode,
        bool useLegacyRestart = false)
    {
        JsonCommandResponse response;
        if (useLegacyRestart)
        {
            BridgeRequest request = Request("restart", "agent", 1, "--legacy-production", "--input", input);
            response = Execute(fixture, request);
        }
        else
            response = ExecuteInputResolve(fixture, "horticulture", input);
        Assert(!response.Success && response.ErrorCode == expectedCode,
            "invalid test input must return " + expectedCode + " as a machine-readable error");
    }

    private static bool IsKnownLaunchEnvironmentKey(string key) => key switch
    {
        "DEVBRIDGE_ROOT" => true,
        "DEVBRIDGE_INSTALLATION_ID" => true,
        "DEVBRIDGE_RUNTIME_SLOT_ID" => true,
        "DEVBRIDGE_LAUNCH_ID" => true,
        "DEVBRIDGE_GENERATION" => true,
        "DEVBRIDGE_QUICKTEST_REQUESTED" => true,
        "DEVBRIDGE_QUICKTEST_TIMEOUT_SECONDS" => true,
        "DEVBRIDGE_PROFILE_FINGERPRINT" => true,
        "DEVBRIDGE_BASELINE_FINGERPRINT" => true,
        "DEVBRIDGE_PROFILE_MODE" => true,
        _ => false
    };
}
