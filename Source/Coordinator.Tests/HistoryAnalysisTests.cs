using System.Globalization;
using System.Text.Json;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestHistoryDiffSemanticChanges()
    {
        using Fixture fixture = HistoryProfileFixture();
        AcceptHistoryGeneration(fixture);
        GenerationManifest second = AppendReadyGeneration(fixture, 2);

        HistoryDiffResponse response = ExecuteHistoryDiff(fixture, 1, 2, out string json);
        Assert(response.Success && response.SchemaVersion == DevBridgeSchemaVersions.HistoryDiff &&
               response.Contract == DevBridgeSchemaVersions.HistoryDiffContract,
            "history diff must return its versioned success contract");
        Assert(response.Changes.RequestedProjects.Added.Contains("beta") &&
               response.Changes.RequestedProjects.Removed.Contains("zeta"),
            "history diff must identify requested projects added and removed");
        Assert(response.Changes.ResolvedPackageIds.Added.Contains("pkg.beta") &&
               response.Changes.ModLoadOrder.Moved.Any(value => value.PackageId == "mod.shared"),
            "history diff must identify package and load-order changes");
        Assert(response.Changes.TestInputs.Changed &&
               response.Changes.TestInputs.Fields.Any(value => value.Name == TestGenerationInputs.QuicktestVariantName),
            "history diff must compare typed test inputs");
        Assert(response.Changes.ProfileFingerprint.Changed && response.Changes.BaselineFingerprint.Changed &&
               response.Changes.RimBridge.Changed && response.Changes.Components.Changed &&
               response.Changes.Recipe.Changed,
            "history diff must include profile, baseline, RimBridge, component, and recipe changes");
        Assert(response.RuntimeIdentityChanges.LaunchId.Changed && response.RuntimeIdentityChanges.ProcessId.Changed,
            "runtime launch/process identity must be reported separately");
        Assert(!json.Contains("secret=", StringComparison.OrdinalIgnoreCase) && json.Length < 32 * 1024,
            "history diff output must be bounded and secret-safe");

        AppendEquivalentGeneration(fixture, 3);
        HistoryDiffResponse identical = ExecuteHistoryDiff(fixture, 2, 3, out _);
        Assert(identical.Success && !identical.Changes.ProfileFingerprint.Changed &&
               !identical.Changes.RequestedProjects.Changed &&
               !identical.Changes.ResolvedPackageIds.Changed &&
               !identical.Changes.ModLoadOrder.Changed &&
               !identical.Changes.TestInputs.Changed &&
               !identical.Changes.BaselineFingerprint.Changed &&
               !identical.Changes.RimBridge.Changed &&
               !identical.Changes.Components.Changed &&
               !identical.Changes.Recipe.Changed &&
               !identical.Changes.Readiness.Changed &&
               !identical.Changes.Failure.Changed &&
               identical.RuntimeIdentityChanges.LaunchId.Changed,
            "identical semantic generations must produce no changes");
        Assert(second.RecipeContext?.RecipeId == "recipe-two", "test fixture must retain its recipe context");
    }

    private static void TestHistoryDiagnosisUsesNearestGoodEvidence()
    {
        using Fixture fixture = Fixture.LoadingWithLease();
        AcceptHistoryGeneration(fixture);
        AppendReadyGeneration(fixture, 2);
        string evidenceId = AppendFailedGeneration(fixture, 3);

        HistoryDiagnosisResponse response = ExecuteHistoryDiagnose(fixture, 3, out string json);
        Assert(response.Success && response.SchemaVersion == DevBridgeSchemaVersions.HistoryDiagnosis &&
               response.PriorKnownGoodGeneration == 2 && response.Diff?.FromGeneration == 2 &&
               response.Diff.ToGeneration == 3,
            "diagnosis must compare a failed generation with the nearest prior READY generation");
        Assert(response.Failure?.EvidenceId == evidenceId && response.Failure.Fingerprint == "failure-fingerprint-3" &&
               response.Proven.Any(value => value.Contains("generation 2 was READY", StringComparison.Ordinal)),
            "diagnosis must attach normalized failure evidence and proven history facts");
        Assert(response.Unknown.Any(value => value.Contains("does not prove", StringComparison.OrdinalIgnoreCase)),
            "diagnosis must not claim that a changed package caused the failure");
        Assert(!json.Contains("token=", StringComparison.OrdinalIgnoreCase) &&
               !json.Contains("raw exception", StringComparison.OrdinalIgnoreCase),
            "diagnosis must not expose secrets or arbitrary exception text");

        using Fixture noGood = Fixture.LoadingWithLease();
        int failed = noGood.State.Execute(Request("wait-ready", "holder", 77), _ => { }, () => true);
        Assert(failed != 0, "the no-good fixture must create a failed generation");
        HistoryDiagnosisResponse noGoodResponse = ExecuteHistoryDiagnose(noGood, 1, out _);
        Assert(noGoodResponse.Success && noGoodResponse.PriorKnownGoodGeneration == null &&
               noGoodResponse.Unknown.Any(value => value.Contains("no prior READY", StringComparison.OrdinalIgnoreCase)),
            "diagnosis must distinguish the absence of a prior known-good generation");
    }

    private static void TestHistoryAnalysisCorruptionIsMutationFree()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        string path = Path.Combine(fixture.Root, "Runtime", "generation-history.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new GenerationHistoryEnvelope(), Program.JsonOptions));
        byte[] before = File.ReadAllBytes(path);

        HistoryDiffResponse missing = ExecuteHistoryDiff(fixture, 1, 2, out _);
        Assert(!missing.Success && missing.ErrorCode == "GENERATION_NOT_FOUND",
            "missing generations must fail closed with a machine-readable error");
        Assert(File.ReadAllBytes(path).SequenceEqual(before),
            "history analysis must not rewrite corrupt or unsupported history");

        File.WriteAllText(path, "{\"schemaVersion\":999,\"contract\":\"unsupported\"}");
        before = File.ReadAllBytes(path);
        HistoryDiffResponse unsupported = ExecuteHistoryDiff(fixture, 1, 2, out _);
        Assert(!unsupported.Success && unsupported.ErrorCode == "GENERATION_HISTORY_CORRUPT",
            "unsupported history schema must fail closed with a machine-readable error");
        Assert(File.ReadAllBytes(path).SequenceEqual(before),
            "unsupported history must not be rewritten");

        File.WriteAllText(path, "not-json");
        before = File.ReadAllBytes(path);
        HistoryDiagnosisResponse corrupt = ExecuteHistoryDiagnose(fixture, 1, out _);
        Assert(!corrupt.Success && corrupt.ErrorCode == "GENERATION_HISTORY_CORRUPT",
            "corrupt history must produce a stable analysis error");
        Assert(File.ReadAllBytes(path).SequenceEqual(before),
            "diagnosis must preserve corrupt history bytes");

        using Fixture valid = Fixture.LoadingWithLease();
        AcceptHistoryGeneration(valid);
        string manifestPath = Path.Combine(valid.Root, "Runtime", "generations", "1.json");
        string historyPath = Path.Combine(valid.Root, "Runtime", "generation-history.json");
        string statePath = Path.Combine(valid.Root, "Runtime", "state.json");
        string eventsPath = Path.Combine(valid.Root, "Runtime", "coordinator-events.jsonl");
        byte[] manifestBefore = File.ReadAllBytes(manifestPath);
        byte[] historyBefore = File.ReadAllBytes(historyPath);
        byte[] stateBefore = OptionalBytes(statePath);
        byte[] eventsBefore = OptionalBytes(eventsPath);
        ExecuteHistoryDiff(valid, 1, 1, out _);
        ExecuteHistoryDiagnose(valid, 1, out _);
        Assert(File.ReadAllBytes(manifestPath).SequenceEqual(manifestBefore) &&
               File.ReadAllBytes(historyPath).SequenceEqual(historyBefore) &&
               OptionalBytesEqual(statePath, stateBefore) &&
               OptionalBytesEqual(eventsPath, eventsBefore),
            "successful diff and diagnosis must be mutation-free");
    }

    private static void TestHistoryAnalysisBoundsAndCrashIsolation()
    {
        using Fixture fixture = Fixture.LoadingWithLease();
        AcceptHistoryGeneration(fixture);
        AppendReadyGeneration(fixture, 2);
        AppendFailedGeneration(fixture, 3);

        PersistedState state = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        state.CrashIsolation = new CrashIsolationIncident
        {
            OriginalGeneration = 3,
            DiagnosisCode = "MINIMAL_INCOMPATIBLE_PROJECT_SET",
            Diagnosis = new string('d', 2000),
            Diagnoses = Enumerable.Range(0, 100).Select(index => new CrashIsolationDiagnosis
            {
                Code = "PROJECT_OR_REQUIRED_DEPENDENCY_CLOSURE",
                RequestedProjects = Enumerable.Repeat("project-" + index, 100).ToList(),
                ResolvedProjectPackageIds = Enumerable.Repeat("package-" + index, 100).ToList(),
                ProfileFingerprint = "profile-" + index
            }).ToList()
        };
        fixture.WriteState(state);
        fixture.State = fixture.Reload();

        HistoryDiagnosisResponse response = ExecuteHistoryDiagnose(fixture, 3, out string json);
        Assert(response.CrashIsolation?.DiagnosisCode == "MINIMAL_INCOMPATIBLE_PROJECT_SET" &&
               response.CrashIsolation.MinimalIncompatibleSets.Count <= 64 &&
               response.CrashIsolation.Diagnoses.Count <= 64,
            "diagnosis must include only bounded durable crash-isolation evidence (" +
            (response.CrashIsolation?.DiagnosisCode ?? "null") + "," +
            (response.CrashIsolation?.Diagnoses.Count.ToString(CultureInfo.InvariantCulture) ?? "null") + ")");
        Assert(json.Length < 64 * 1024 && !json.Contains("d".PadLeft(1000, 'd'), StringComparison.Ordinal),
            "diagnosis output must bound durable diagnostic strings");
    }

    private static HistoryDiffResponse ExecuteHistoryDiff(Fixture fixture, int from, int to, out string json)
    {
        BridgeRequest request = Request("history", "analysis-agent", 88, "diff",
            from.ToString(CultureInfo.InvariantCulture), to.ToString(CultureInfo.InvariantCulture));
        int result = fixture.State.Execute(request, _ => { }, () => true);
        HistoryDiffResponse response = request.HistoryDiffResult;
        Assert(response != null && response.ExitCode == result,
            "history diff routing must cache a dedicated response and terminal exit code");
        json = JsonSerializer.Serialize(response, Program.JsonOptions);
        return response;
    }

    private static HistoryDiagnosisResponse ExecuteHistoryDiagnose(Fixture fixture, int generation, out string json)
    {
        BridgeRequest request = Request("history", "analysis-agent", 88, "diagnose",
            generation.ToString(CultureInfo.InvariantCulture));
        int result = fixture.State.Execute(request, _ => { }, () => true);
        HistoryDiagnosisResponse response = request.HistoryDiagnosisResult;
        Assert(response != null && response.ExitCode == result,
            "history diagnosis routing must cache a dedicated response and terminal exit code");
        json = JsonSerializer.Serialize(response, Program.JsonOptions);
        return response;
    }

    private static GenerationManifest AppendReadyGeneration(Fixture fixture, int generation)
    {
        string firstPath = Path.Combine(fixture.Root, "Runtime", "generations", "1.json");
        GenerationManifest manifest = JsonSerializer.Deserialize<GenerationManifest>(
            File.ReadAllText(firstPath), Program.JsonOptions);
        manifest.Generation = generation;
        manifest.AcceptedUtc = ClockStart.AddMinutes(generation);
        manifest.Launch.LaunchId = "launch-" + generation.ToString(CultureInfo.InvariantCulture);
        manifest.Launch.LaunchGeneration = generation;
        manifest.Launch.LaunchStartedUtc = manifest.AcceptedUtc;
        manifest.Process.ProcessId = 100 + generation;
        manifest.Process.ProcessStartUtcTicks = 2000 + generation;
        manifest.Readiness.Generation = generation;
        manifest.Readiness.LaunchId = manifest.Launch.LaunchId;
        manifest.Readiness.ProcessId = manifest.Process.ProcessId;
        manifest.Profile.Mode = ModProfile.BaselineMode;
        manifest.Profile.RequestedProjects = new List<string> { "beta", "shared" };
        manifest.Profile.ResolvedProjectPackageIds = new List<string> { "pkg.beta", "pkg.shared" };
        manifest.Profile.ResolvedMods = new List<string> { "mod.beta", "mod.shared", "mod.base" };
        manifest.Profile.TestInputs = new List<TestInputValue>
        {
            new() { Name = TestGenerationInputs.QuicktestName, Value = "false" },
            new() { Name = TestGenerationInputs.QuicktestTimeoutName, Value = "60" },
            new() { Name = TestGenerationInputs.QuicktestVariantName, Value = TestGenerationInputs.DisabledVariant }
        };
        manifest.Profile.ProfileFingerprint = "profile-two";
        manifest.Profile.BaselineFingerprint = "baseline-two";
        manifest.Profile.RimBridgeMode = "required";
        manifest.Profile.RimBridgeVersion = "bridge-two";
        manifest.ModsConfig.Fingerprint = "mods-two";
        manifest.ModsConfig.BaselineFingerprint = "baseline-two";
        manifest.ModsConfig.ResolvedModOrder = manifest.Profile.ResolvedMods.ToList();
        manifest.Readiness.QuicktestRequired = false;
        manifest.Readiness.QuicktestVariant = TestGenerationInputs.DisabledVariant;
        manifest.Readiness.QuicktestTimeoutSeconds = TestGenerationInputs.DefaultQuicktestTimeoutSeconds;
        manifest.Components.CoordinatorVersion = "coordinator-two";
        manifest.RecipeContext = new GenerationRecipeContextEvidence
        {
            RecipeId = "recipe-two",
            ReproductionContextFingerprint = "recipe-context-two",
            ProjectFingerprint = "project-context-two"
        };

        string generationPath = Path.Combine(fixture.Root, "Runtime", "generations",
            generation.ToString(CultureInfo.InvariantCulture) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(generationPath));
        File.WriteAllText(generationPath, JsonSerializer.Serialize(manifest, Program.JsonOptions));
        GenerationHistoryEnvelope history = ReadHistory(fixture);
        history.Records.Add(new GenerationHistoryRecord
        {
            Generation = generation,
            Status = "READY",
            ObservedUtc = manifest.AcceptedUtc,
            AcceptedUtc = manifest.AcceptedUtc,
            ManifestPath = "generations/" + generation.ToString(CultureInfo.InvariantCulture) + ".json",
            TestInputs = manifest.Profile.TestInputs.ToList()
        });
        history.LastKnownGoodGeneration = generation;
        WriteHistory(fixture, history);
        return manifest;
    }

    private static GenerationManifest AppendEquivalentGeneration(Fixture fixture, int generation)
    {
        string sourcePath = Path.Combine(fixture.Root, "Runtime", "generations", "2.json");
        GenerationManifest manifest = JsonSerializer.Deserialize<GenerationManifest>(
            File.ReadAllText(sourcePath), Program.JsonOptions);
        manifest.Generation = generation;
        manifest.AcceptedUtc = ClockStart.AddMinutes(generation);
        manifest.Launch.LaunchId = "launch-" + generation.ToString(CultureInfo.InvariantCulture);
        manifest.Launch.LaunchGeneration = generation;
        manifest.Launch.LaunchStartedUtc = manifest.AcceptedUtc;
        manifest.Process.ProcessId = 100 + generation;
        manifest.Process.ProcessStartUtcTicks = 2000 + generation;
        manifest.Readiness.Generation = generation;
        manifest.Readiness.LaunchId = manifest.Launch.LaunchId;
        manifest.Readiness.ProcessId = manifest.Process.ProcessId;

        string generationPath = Path.Combine(fixture.Root, "Runtime", "generations",
            generation.ToString(CultureInfo.InvariantCulture) + ".json");
        File.WriteAllText(generationPath, JsonSerializer.Serialize(manifest, Program.JsonOptions));
        GenerationHistoryEnvelope history = ReadHistory(fixture);
        history.Records.Add(new GenerationHistoryRecord
        {
            Generation = generation,
            Status = "READY",
            ObservedUtc = manifest.AcceptedUtc,
            AcceptedUtc = manifest.AcceptedUtc,
            ManifestPath = "generations/" + generation.ToString(CultureInfo.InvariantCulture) + ".json",
            TestInputs = manifest.Profile.TestInputs.ToList()
        });
        history.LastKnownGoodGeneration = generation;
        WriteHistory(fixture, history);
        return manifest;
    }

    private static Fixture HistoryProfileFixture()
    {
        Fixture fixture = new(new PersistedState
        {
            Generation = 0,
            Phase = BridgePhase.LOADING,
            LaunchId = "launch-1",
            LaunchGeneration = 1,
            TargetGeneration = 1,
            LaunchRequestKey = "history-analysis",
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
                RimBridgeMode = RimBridgeMode.Off
            },
            Leases = new List<TestLease>
            {
                new() { Id = "T001", Agent = "holder", ClientProcessId = 77,
                    Generation = 0, StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
            }
        });
        fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
        return fixture;
    }

    private static string AppendFailedGeneration(Fixture fixture, int generation)
    {
        FailureEvidenceRecord evidence = new()
        {
            Generation = generation,
            FailureFingerprint = "failure-fingerprint-" + generation.ToString(CultureInfo.InvariantCulture),
            Summary = "bounded quicktest failure",
            ErrorCode = "QUICKTEST_FAILED",
            Phase = "quicktest",
            Component = "component-under-test",
            RecipeId = "recipe-two",
            ReproductionContextFingerprint = "recipe-context-two",
            Detail = "token=should-not-escape"
        };
        string evidenceId = new FailureEvidenceStore(Path.Combine(fixture.Root, "Runtime"),
            () => fixture.Clock.UtcNow).Write(evidence);
        Assert(!string.IsNullOrWhiteSpace(evidenceId), "test failure evidence must be written");

        GenerationHistoryEnvelope history = ReadHistory(fixture);
        history.Records.Add(new GenerationHistoryRecord
        {
            Generation = generation,
            Status = "FAILED",
            ObservedUtc = ClockStart.AddMinutes(generation),
            TerminalFailureCode = "QUICKTEST_FAILED",
            TerminalFailureDetail = "raw exception must not be exposed",
            TestInputs = new List<TestInputValue>
            {
                new() { Name = TestGenerationInputs.QuicktestName, Value = "false" }
            },
            FailureFingerprint = evidence.FailureFingerprint,
            FailureEvidenceId = evidenceId,
            DiagnosisReference = "diagnosis-ref-3"
        });
        history.LastKnownGoodGeneration = 2;
        WriteHistory(fixture, history);
        return evidenceId;
    }

    private static GenerationHistoryEnvelope ReadHistory(Fixture fixture) =>
        JsonSerializer.Deserialize<GenerationHistoryEnvelope>(File.ReadAllText(
            Path.Combine(fixture.Root, "Runtime", "generation-history.json")), Program.JsonOptions);

    private static void WriteHistory(Fixture fixture, GenerationHistoryEnvelope history)
    {
        history.Records = history.Records.OrderBy(value => value.Generation).ToList();
        File.WriteAllText(Path.Combine(fixture.Root, "Runtime", "generation-history.json"),
            JsonSerializer.Serialize(history, Program.JsonOptions));
    }

    private static byte[] OptionalBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static bool OptionalBytesEqual(string path, byte[] expected)
    {
        byte[] actual = OptionalBytes(path);
        return actual == null && expected == null || actual != null && expected != null && actual.SequenceEqual(expected);
    }
}
