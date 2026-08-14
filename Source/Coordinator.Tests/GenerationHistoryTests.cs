using System.Text.Json;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestGenerationHistoryPinsEvidence()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 0,
            Phase = BridgePhase.LOADING,
            LaunchId = "launch-history",
            LaunchGeneration = 1,
            TargetGeneration = 1,
            LaunchRequestKey = "request-history",
            ProcessId = 101,
            ProcessStartUtcTicks = 1001,
            LaunchStartedUtc = ClockStart,
            ProfileMode = ModProfile.LegacyMode,
            LaunchProfileMode = "explicit-human-legacy",
            LaunchProfileInstalled = true,
            LaunchProfileFingerprint = "profile-history",
            RuntimeProfile = new PersistedProfileSnapshot
            {
                Mode = ModProfile.LegacyMode,
                RequestedProjects = new List<string> { "zeta", "alpha" },
                ResolvedProjectPackageIds = new List<string> { "pkg.zeta", "pkg.alpha" },
                ResolvedMods = new List<string> { "mod.zeta", "mod.alpha", "mod.shared" },
                ProfileFingerprint = "profile-history",
                BaselineFingerprint = null,
                RimBridgeMode = RimBridgeMode.Off
            },
            FrozenRegistrations = new List<ProjectIntentSnapshot>
            {
                new() { Id = "registration-1", Owner = "agent", SessionId = "session-1",
                    RequestedProjects = new List<string> { "zeta", "alpha" } }
            },
            Leases = new List<TestLease>
            {
                new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 0,
                    StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
            }
        });
        fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
        fixture.WriteReadiness("launch-history", 1, 101);

        BridgeRequest ensure = Request("wait-ready", "holder", 77);
        List<string> acceptedOutput = new();
        int accepted = fixture.State.Execute(ensure, acceptedOutput.Add, () => true);
        Assert(accepted == 0, "matching readiness must accept the generation before history is inspected: " +
            string.Join(" | ", acceptedOutput));

        string manifestPath = Path.Combine(fixture.Root, "Runtime", "generations", "1.json");
        string historyPath = Path.Combine(fixture.Root, "Runtime", "generation-history.json");
        Assert(File.Exists(manifestPath) && File.Exists(historyPath),
            "READY must persist both the immutable manifest and history envelope");
        byte[] manifestBefore = File.ReadAllBytes(manifestPath);
        byte[] historyBefore = File.ReadAllBytes(historyPath);
        GenerationManifest manifest = JsonSerializer.Deserialize<GenerationManifest>(
            File.ReadAllText(manifestPath), Program.JsonOptions);
        Assert(manifest.Profile.ResolvedMods.SequenceEqual(new[] { "mod.zeta", "mod.alpha", "mod.shared" }),
            "the manifest must pin the exact resolved mod order");
        Assert(manifest.Profile.Registrations.Count == 1 &&
               manifest.Profile.Registrations[0].Id == "registration-1",
            "the manifest must pin frozen registration evidence");

        JsonCommandResponse first = ExecuteHistory(fixture, out string firstJson);
        JsonCommandResponse second = ExecuteHistory(fixture, out string secondJson);
        Assert(first.GenerationHistory?.CurrentGeneration == 1 &&
               first.GenerationHistory.LastKnownGoodGeneration == 1 &&
               first.GenerationHistory.Current?.Manifest != null,
            "history must expose the accepted current and last-known-good generation");
        Assert(firstJson == secondJson, "repeated history JSON must be deterministic");

        BridgeRequest doctorRequest = Request("doctor");
        int doctorResult = fixture.State.Execute(doctorRequest, _ => { }, () => true);
        JsonCommandResponse doctor = fixture.State.CreateJsonResponse(doctorRequest, doctorResult,
            Array.Empty<string>());
        Assert(doctor.GenerationHistory?.CurrentGeneration == 1,
            "doctor must expose accepted-generation history");

        File.WriteAllText(Path.Combine(fixture.Root, "ModsConfig.xml"),
            "<activeMods><li>secret-token=never-persist</li></activeMods>");
        JsonCommandResponse afterConfig = ExecuteHistory(fixture, out string afterConfigJson);
        Assert(afterConfig.GenerationHistory?.Current?.Manifest?.Profile?.ResolvedMods.SequenceEqual(
                   new[] { "mod.zeta", "mod.alpha", "mod.shared" }) == true,
            "historical resolved order must remain unchanged after config changes");
        Assert(File.ReadAllBytes(manifestPath).SequenceEqual(manifestBefore) &&
               File.ReadAllBytes(historyPath).SequenceEqual(historyBefore),
            "history and manifest bytes must remain immutable across later reads and config changes");
        Assert(!afterConfigJson.Contains("secret-token=never-persist", StringComparison.Ordinal) &&
               !afterConfigJson.Contains("never-persist", StringComparison.Ordinal),
            "history JSON must not expose secret-shaped config values");

        fixture.State = fixture.Reload();
        JsonCommandResponse afterReload = ExecuteHistory(fixture, out _);
        Assert(afterReload.GenerationHistory?.Records.Count == 1 &&
               afterReload.GenerationHistory.CurrentGeneration == 1,
            "history must reload without duplicating the accepted record");
    }

    private static void TestGenerationHistoryLastGoodAfterStop()
    {
        using Fixture fixture = Fixture.LoadingWithLease();
        AcceptHistoryGeneration(fixture);
        int stop = fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
        Assert(stop == 0, "normal termination must stop the accepted process cleanly");

        JsonCommandResponse history = ExecuteHistory(fixture, out _);
        GenerationHistoryEntry current = history.GenerationHistory?.Current;
        Assert(history.GenerationHistory?.LastKnownGoodGeneration == 1 &&
               current?.Record?.Status == "STOPPED" && current.Manifest != null,
            "normal termination must retain the accepted generation as last-known-good");
    }

    private static void TestGenerationHistoryFailedLaunch()
    {
        using Fixture fixture = Fixture.LoadingWithLease();
        int result = fixture.State.Execute(Request("wait-ready", "holder", 77),
            _ => { }, () => true);
        JsonCommandResponse history = ExecuteHistory(fixture, out _);
        Assert(result != 0 && history.GenerationHistory?.LastKnownGoodGeneration == null &&
               history.GenerationHistory.CurrentGeneration == 0,
            "a pre-READY failure must not replace or create last-known-good");
        GenerationHistoryRecord record = history.GenerationHistory.Records.Single(value => value.Generation == 1);
        Assert(record.Status == "FAILED" && record.ManifestPath == null &&
               !File.Exists(Path.Combine(fixture.Root, "Runtime", "generations", "1.json")),
            "failed generations must retain semantic failure evidence without an accepted manifest");
    }

    private static void TestGenerationHistoryCorruption()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        string path = Path.Combine(fixture.Root, "Runtime", "generation-history.json");
        File.WriteAllText(path, "{ not valid history }");
        byte[] before = File.ReadAllBytes(path);
        BridgeRequest request = Request("history");
        List<string> output = new();
        int result = fixture.State.Execute(request, output.Add, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(request, result, output);
        Assert(result == 4 && response.GenerationHistory?.Corrupt == true &&
               response.ErrorCode == "GENERATION_HISTORY_CORRUPT",
            "corrupt history must fail closed with a stable integrity error");
        Assert(File.ReadAllBytes(path).SequenceEqual(before),
            "corrupt history must not be rewritten while being diagnosed");

        BridgeRequest doctor = Request("doctor");
        int doctorResult = fixture.State.Execute(doctor, _ => { }, () => true);
        JsonCommandResponse doctorResponse = fixture.State.CreateJsonResponse(doctor, doctorResult,
            Array.Empty<string>());
        Assert(doctorResponse.GenerationHistory?.Corrupt == true &&
               doctorResponse.OperationalState?.HistoryCorrupt == true,
            "doctor must surface durable history corruption");
        Assert(File.ReadAllBytes(path).SequenceEqual(before),
            "doctor must not repair or rewrite corrupt history");
    }

    private static void AcceptHistoryGeneration(Fixture fixture)
    {
        fixture.WriteReadiness("launch-1", 1, 101);
        List<string> output = new();
        int result = fixture.State.Execute(Request("wait-ready", "holder", 77), output.Add, () => true);
        Assert(result == 0, "test fixture generation must reach READY");
    }

    private static JsonCommandResponse ExecuteHistory(Fixture fixture, out string json)
    {
        BridgeRequest request = Request("history");
        List<string> output = new();
        int result = fixture.State.Execute(request, output.Add, () => true);
        JsonCommandResponse response = fixture.State.CreateJsonResponse(request, result, output);
        json = JsonSerializer.Serialize(response, Program.JsonOptions);
        return response;
    }
}
