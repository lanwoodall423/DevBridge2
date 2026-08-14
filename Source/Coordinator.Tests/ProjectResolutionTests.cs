using System.Text.Json;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestProjectResolveIsPureAndMatchesAcceptedClosure()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        string statePath = Path.Combine(setup.Fixture.Root, "Runtime", "state.json");
        string modsPath = Path.Combine(setup.Fixture.Root, "ModsConfig.xml");
        byte[] stateBefore = File.ReadAllBytes(statePath);
        byte[] modsBefore = File.ReadAllBytes(modsPath);
        int launchesBefore = setup.Fixture.Adapter.LaunchCalls;

        JsonCommandResponse plan = ExecuteProjectResolve(setup.Fixture, "horticulture");
        Assert(plan.Success && plan.ProjectResolution != null,
            "project resolve must return a successful structured plan");
        Assert(plan.ProjectResolution.ResolvedMods.Count > 0 &&
               plan.ProjectResolution.Provenance.Any(value => value.PackageId == "lan.horticulture.novelseeds" &&
                   value.Reasons.Any(reason => reason.Category == "PROJECT_ROOT")),
            "the plan must expose the exact closure and project-root provenance");
        Assert(File.ReadAllBytes(statePath).SequenceEqual(stateBefore) &&
               File.ReadAllBytes(modsPath).SequenceEqual(modsBefore) &&
               setup.Fixture.Adapter.LaunchCalls == launchesBefore &&
               setup.Fixture.State.Execute(Request("project", "agent", 1, "status"), _ => { }, () => true) == 0,
            "project resolve must not write state or ModsConfig, launch a process, or create registration state");

        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int restart = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        Assert(restart == 0, "the same resolver input must be accepted by a real generation");
        JsonCommandResponse status = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(plan.ProjectResolution.ResolvedMods.SequenceEqual(status.FrozenResolvedMods,
                   StringComparer.OrdinalIgnoreCase),
            "dry-run order must match the exact order frozen by the accepted generation");
    }

    private static void TestProjectResolveIsDeterministicAndComparesPinnedGeneration()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        JsonCommandResponse first = ExecuteProjectResolve(setup.Fixture, "horticulture");
        JsonCommandResponse second = ExecuteProjectResolve(setup.Fixture, "HORTICULTURE");
        string firstJson = JsonSerializer.Serialize(first.ProjectResolution, Program.JsonOptions);
        string secondJson = JsonSerializer.Serialize(second.ProjectResolution, Program.JsonOptions);
        Assert(firstJson == secondJson && first.ProjectResolution.ProfileFingerprint == second.ProjectResolution.ProfileFingerprint,
            "equivalent dry-run aliases must produce deterministic JSON and fingerprints");

        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(Request("restart", "agent", 1, "--projects", "horticulture"),
                   _ => { }, () => true) == 0, "comparison requires an accepted generation");
        JsonCommandResponse same = ExecuteProjectResolve(setup.Fixture, "horticulture");
        JsonCommandResponse changed = ExecuteProjectResolve(setup.Fixture, "aquaculture");
        Assert(same.ProjectResolution.CurrentGeneration == 1 &&
               same.ProjectResolution.CurrentGenerationTrust == "VALID" &&
               same.ProjectResolution.Comparison != null &&
               !same.ProjectResolution.WouldDifferFromCurrent &&
               !same.ProjectResolution.WouldRequireRestart,
            "matching plans must compare against the pinned manifest as an exact match");
        Assert(changed.ProjectResolution.WouldDifferFromCurrent &&
               changed.ProjectResolution.WouldRequireRestart &&
               changed.ProjectResolution.Comparison.PackagesAdded.Count > 0,
            "a changed plan must report package and restart differences from the pinned generation");
    }

    private static void TestProjectResolveFailuresAreMachineReadableAndMutationFree()
    {
        using ProfileSetup unknown = ProfileSetup.Create();
        AssertResolutionFailure(unknown.Fixture, "not-a-managed-project", "PROFILE_UNKNOWN_PROJECT");

        using ProfileSetup missing = ProfileSetup.Create();
        string missingState = Path.Combine(missing.Fixture.Root, "Runtime", "state.json");
        byte[] missingBefore = File.ReadAllBytes(missingState);
        Directory.Delete(Path.Combine(missing.MetadataRoot, "progression"), true);
        AssertResolutionFailure(missing.Fixture, "horticulture", "PROFILE_MISSING_PACKAGE");
        Assert(File.ReadAllBytes(missingState).SequenceEqual(missingBefore) &&
               missing.Fixture.Adapter.LaunchCalls == 0,
            "missing dependency resolution must not mutate durable state or launch");

        using ProfileSetup cycle = ProfileSetup.Create();
        WriteInstalledMetadata(cycle.MetadataRoot, "horticulture", "lan.horticulture.novelseeds",
            "lan.horticulture.novelseeds");
        AssertResolutionFailure(cycle.Fixture, "horticulture", "PROFILE_DEPENDENCY_CYCLE");
    }

    private static void TestFutureConfigurationInvalidPreservesCurrentGeneration()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        BridgeRequest registration = Request("project", "agent", 1, "register", "horticulture");
        Assert(setup.Fixture.State.Execute(registration, _ => { }, () => true) == 0,
            "future-configuration test requires an active project intent");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(Request("restart", "agent", 1), _ => { }, () => true) == 0,
            "future-configuration test requires a valid accepted generation");
        int launchCalls = setup.Fixture.Adapter.LaunchCalls;
        Directory.Delete(Path.Combine(setup.MetadataRoot, "progression"), true);

        JsonCommandResponse status = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(status.Success && status.Generation == 1 && status.CurrentGenerationTrust == "VALID" &&
               status.NextGenerationConfig != null && !status.NextGenerationConfig.Valid &&
               status.NextGenerationConfig.ErrorCode == "PROFILE_MISSING_PACKAGE",
            "status must distinguish a valid current generation from invalid future configuration");

        BridgeRequest doctorRequest = Request("doctor");
        int doctorExit = setup.Fixture.State.Execute(doctorRequest, _ => { }, () => true);
        JsonCommandResponse doctor = setup.Fixture.State.CreateJsonResponse(
            doctorRequest, doctorExit, Array.Empty<string>());
        Assert(doctor.Healthy == true && doctor.CurrentGenerationTrust == "VALID" &&
               doctor.NextGenerationConfig != null && !doctor.NextGenerationConfig.Valid &&
               doctor.Findings.Any(value => value.Code == "FUTURE_CONFIGURATION_INVALID"),
            "Doctor must report invalid future configuration without invalidating the current generation");

        JsonCommandResponse lease = Execute(setup.Fixture, Request("test", "agent", 1, "begin"));
        Assert(lease.LeaseId != null, "current generation must remain lease-manageable");
        int stop = setup.Fixture.State.Execute(Request("stop", "agent", 1, lease.LeaseId), _ => { }, () => true);
        Assert(stop == 0, "current generation must remain safely stoppable");
        Assert(setup.Fixture.State.Execute(Request("restart", "agent", 1), _ => { }, () => true) != 0 &&
               setup.Fixture.Adapter.LaunchCalls == launchCalls,
            "invalid future configuration must block a new launch without replacing the current state");

        WriteInstalledMetadata(setup.MetadataRoot, "progression", "ferny.progressionagriculture",
            "ferny.replacelib");
        JsonCommandResponse fixedStatus = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(fixedStatus.NextGenerationConfig != null && fixedStatus.NextGenerationConfig.Valid,
            "fixing future metadata must clear the prospective configuration error on the next audit");
    }

    private static JsonCommandResponse ExecuteProjectResolve(Fixture fixture, string aliases)
    {
        return Execute(fixture, Request("project", "agent", 1, "resolve", aliases, "--json"));
    }

    private static void AssertResolutionFailure(Fixture fixture, string aliases, string expectedCode)
    {
        JsonCommandResponse response = ExecuteProjectResolve(fixture, aliases);
        Assert(!response.Success && response.ProjectResolution != null &&
               response.ProjectResolution.ErrorCode == expectedCode &&
               response.ProjectResolution.Errors.Any(value => value.Code == expectedCode),
            "project resolve must expose machine-readable " + expectedCode + "");
    }

    private static JsonCommandResponse Execute(Fixture fixture, BridgeRequest request)
    {
        List<string> messages = new();
        int exitCode = fixture.State.Execute(request, messages.Add, () => true);
        return fixture.State.CreateJsonResponse(request, exitCode, messages);
    }
}
