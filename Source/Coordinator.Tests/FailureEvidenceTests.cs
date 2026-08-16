using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestFailureFingerprintNormalization()
    {
        FailureFingerprintInput first = new()
        {
            ErrorCode = "LOAD_FAILED",
            Phase = "Loading",
            ExceptionType = "System.InvalidOperationException",
            Message = "failure at 2026-08-16T12:34:56Z requestId=req-1 pid=101 " +
                "addr=0xABCDEF token=super-secret C:\\Users\\Lan\\AppData\\Local\\Temp\\one\\x.log",
            StackFrames = new[] { "at Loader.Run: line 12" },
            Component = "Coordinator",
            ComponentIdentity = "1.2.4",
            SourceRevision = "revision-a",
            ProjectFingerprint = "profile-a",
            RecipeId = "recipe-a",
            GenerationInputs = new[] { new TestInputValue { Name = "quicktest", Value = "true" } }
        };
        FailureFingerprintInput sameFailure = new()
        {
            ErrorCode = first.ErrorCode,
            Phase = first.Phase,
            ExceptionType = first.ExceptionType,
            Message = "failure at 2026-08-17T22:01:03Z requestId=req-2 pid=909 " +
                "addr=0x123456 token=another-secret C:\\Users\\Other\\AppData\\Local\\Temp\\two\\x.log",
            StackFrames = new[] { "at Loader.Run: line 99" },
            Component = first.Component,
            ComponentIdentity = first.ComponentIdentity,
            SourceRevision = first.SourceRevision,
            ProjectFingerprint = first.ProjectFingerprint,
            RecipeId = first.RecipeId,
            GenerationInputs = new[] { new TestInputValue { Name = "quicktest", Value = "true" } }
        };
        NormalizedFailureFingerprint normalized = FailureFingerprinting.Create(first);
        NormalizedFailureFingerprint equivalent = FailureFingerprinting.Create(sameFailure);
        Assert(normalized.FailureFingerprint == equivalent.FailureFingerprint &&
               normalized.ReproductionContextFingerprint == equivalent.ReproductionContextFingerprint,
            "timestamps, IDs, PIDs, addresses, paths, secrets, and line numbers must not change a fingerprint");
        Assert(!normalized.CanonicalFailure.Contains("super-secret", StringComparison.Ordinal) &&
               !normalized.CanonicalFailure.Contains("req-1", StringComparison.Ordinal) &&
               !normalized.CanonicalFailure.Contains("101", StringComparison.Ordinal),
            "normalized failure material must not retain secret-shaped or request-specific values");

        FailureFingerprintInput changedStack = new()
        {
            ErrorCode = first.ErrorCode, Phase = first.Phase, ExceptionType = first.ExceptionType,
            Message = "stable failure", StackFrames = new[] { "at Different.Loader.Run" },
            Component = first.Component, ComponentIdentity = first.ComponentIdentity,
            SourceRevision = first.SourceRevision, ProjectFingerprint = first.ProjectFingerprint,
            RecipeId = first.RecipeId, GenerationInputs = first.GenerationInputs
        };
        FailureFingerprintInput changedContext = new()
        {
            ErrorCode = first.ErrorCode, Phase = first.Phase, ExceptionType = first.ExceptionType,
            Message = "stable failure", StackFrames = first.StackFrames,
            Component = first.Component, ComponentIdentity = first.ComponentIdentity,
            SourceRevision = "revision-b", ProjectFingerprint = first.ProjectFingerprint,
            RecipeId = first.RecipeId, GenerationInputs = first.GenerationInputs
        };
        FailureFingerprintInput stable = new()
        {
            ErrorCode = first.ErrorCode, Phase = first.Phase, ExceptionType = first.ExceptionType,
            Message = "stable failure", StackFrames = first.StackFrames,
            Component = first.Component, ComponentIdentity = first.ComponentIdentity,
            SourceRevision = first.SourceRevision, ProjectFingerprint = first.ProjectFingerprint,
            RecipeId = first.RecipeId, GenerationInputs = first.GenerationInputs
        };
        Assert(FailureFingerprinting.Create(changedStack).FailureFingerprint !=
                   FailureFingerprinting.Create(stable).FailureFingerprint &&
               FailureFingerprinting.Create(changedContext).ReproductionContextFingerprint !=
                   normalized.ReproductionContextFingerprint,
            "meaningful stack and source-context changes must remain distinguishable");
    }

    private static void TestFailureOccurrenceDeduplication()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        FailureFingerprintInput input = new()
        {
            ErrorCode = "LOAD_FAILED", Phase = "LOADING", Message = "same failure",
            Component = "coordinator", ProjectFingerprint = "profile-a"
        };
        FailureOccurrenceSummary first = fixture.State.RecordFailureOccurrenceLocked(input, 1,
            "LOADING", "first detail");
        FailureOccurrenceSummary second = fixture.State.RecordFailureOccurrenceLocked(input, 2,
            "LOADING", "second detail");
        Assert(first.FailureFingerprint == second.FailureFingerprint &&
               second.SeenBefore && second.OccurrenceCount == 2 &&
               second.FirstSeenGeneration == 1 && second.LastSeenGeneration == 2 &&
               !string.IsNullOrWhiteSpace(second.EvidenceId),
            "equivalent failures must produce one bounded occurrence summary with lazy evidence pointers");
    }

    private static void TestRepeatedRecipeFailureEquivalence()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        List<TestInputValue> inputs = new()
        {
            new TestInputValue { Name = "quicktest", Value = "true" }
        };
        string fingerprint = fixture.State.RecordRecipeFailure("recipe-a", "LOAD_FAILED",
            "same recipe failure", 1, "profile-a", inputs);
        FailureOccurrenceSummary equivalent = fixture.State.FindEquivalentRecipeFailureLocked(
            "recipe-a", "profile-a", inputs, 1);
        FailureOccurrenceSummary changedProfile = fixture.State.FindEquivalentRecipeFailureLocked(
            "recipe-a", "profile-b", inputs, 1);
        FailureOccurrenceSummary changedInputs = fixture.State.FindEquivalentRecipeFailureLocked(
            "recipe-a", "profile-a", new[]
            {
                new TestInputValue { Name = "quicktest", Value = "false" }
            }, 1);
        Assert(equivalent?.FailureFingerprint == fingerprint && changedProfile == null &&
               changedInputs == null,
            "repeated-failure short-circuiting must require the same recipe, profile, and typed inputs");
    }

    private static void TestSemanticLogsAreBoundedAndCompact()
    {
        string repeated = "[RimBridge] ERROR failure requestId=req-1 token=secret at 2026-08-16T12:34:56Z\n" +
            "   at RimBridge.Server.Run: line 42\n";
        string raw = string.Concat(Enumerable.Repeat(repeated, 100)) + "[Coordinator] warning completed\n";
        SemanticLogParseResult parsed = SemanticLogParser.Parse(raw, 7);
        string semantic = JsonSerializer.Serialize(parsed.Records, Program.JsonOptions);
        Assert(parsed.Records.Count <= FailureEvidenceLimits.MaxSemanticRecords &&
               parsed.Records.Count == 2 && parsed.Records[0].OccurrenceCount == 100 &&
               parsed.Records[0].StackFrames.Count <= FailureEvidenceLimits.MaxStackFrames &&
               Encoding.UTF8.GetByteCount(semantic) < Encoding.UTF8.GetByteCount(raw) &&
               !semantic.Contains("secret", StringComparison.OrdinalIgnoreCase),
            "semantic logs must deduplicate, bound stacks, redact, and materially reduce noisy raw output");
    }

    private static void TestEvidenceLookupBoundsAndExpiry()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevBridge2-evidence-test-" + Guid.NewGuid().ToString("N"));
        DateTime now = ClockStart;
        try
        {
            FailureEvidenceStore store = new(root, () => now);
            string lastId = null;
            for (int index = 0; index < FailureEvidenceLimits.MaxEvidenceRecords + 8; index++)
            {
                lastId = store.Write(new FailureEvidenceRecord
                {
                    Generation = index + 1,
                    FailureFingerprint = "ff-" + index.ToString("x"),
                    Summary = "bounded",
                    Detail = new string('x', 256)
                });
            }
            string directory = Path.Combine(root, "evidence");
            Assert(Directory.GetFiles(directory, "ev-*.json").Length <= FailureEvidenceLimits.MaxEvidenceRecords &&
                   store.Read(lastId).Found,
                "evidence retention and lookup must remain bounded");
            EvidenceLookupResult invalid = store.Read("ev-invalid");
            EvidenceLookupResult missing = store.Read("ev-000000000000000000000000");
            Assert(invalid.ErrorCode == "EVIDENCE_ID_INVALID" && missing.ErrorCode == "EVIDENCE_NOT_FOUND",
                "invalid and missing evidence IDs must have deterministic errors");

            FailureEvidenceRecord expired = new()
            {
                EvidenceId = "ev-aaaaaaaaaaaaaaaaaaaaaaaa",
                CreatedUtc = now - FailureEvidenceLimits.EvidenceLifetime - TimeSpan.FromMinutes(1),
                FailureFingerprint = "ff-expired"
            };
            File.WriteAllText(Path.Combine(directory, expired.EvidenceId + ".json"),
                JsonSerializer.Serialize(expired, Program.JsonOptions));
            Assert(store.Read(expired.EvidenceId).ErrorCode == "EVIDENCE_EXPIRED",
                "expired evidence must not be returned as live evidence");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestForensicCommandsAndDiagnosisReference()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        string playerLog = Path.Combine(fixture.Root, "Player.log");
        string prefix = "old pre-launch output\n";
        File.WriteAllText(playerLog, prefix, new UTF8Encoding(false));
        fixture.PlayerLogPath = playerLog;
        fixture.State = fixture.Reload();
        PersistedState persisted = ReadState(fixture.Root);
        persisted.RimBridge ??= new RimBridgeIntegrationState();
        persisted.RimBridge.LogExistedAtBoundary = true;
        persisted.RimBridge.LogBoundaryPosition = Encoding.UTF8.GetByteCount(prefix);
        persisted.RimBridge.LogBoundaryPrefixHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(prefix)));
        fixture.WriteState(persisted);
        File.AppendAllText(playerLog,
            "[RimBridge] ERROR launch failed\n   at RimBridge.Run: line 4\n" +
            "[Coordinator] warning retrying\n", new UTF8Encoding(false));
        fixture.State = fixture.Reload();

        BridgeRequest logs = Request("logs", "forensic-agent", 22, "query", "--since-launch",
            "--limit", "4", "--json");
        logs.Json = true;
        int logsExit = fixture.State.Execute(logs, _ => { }, () => true);
        LogsQueryResponse logResponse = fixture.State.CreateForensicJsonResponse(logs, logsExit) as LogsQueryResponse;
        Assert(logsExit == 0 && logResponse?.Available == true && logResponse.Records.Count == 2 &&
               logResponse.Records.All(value => value.SinceLaunch) &&
               logResponse.Records.Any(value => value.Severity == "ERROR") &&
               logResponse.Records.All(value => !value.Message.Contains("old pre-launch", StringComparison.Ordinal)),
            "logs query must use the persisted launch boundary and return bounded semantic records");

        persisted = ReadState(fixture.Root);
        persisted.CrashIsolation = new CrashIsolationIncident { IncidentId = "incident-phase4" };
        fixture.WriteState(persisted);
        fixture.State = fixture.Reload();
        FailureOccurrenceSummary occurrence = fixture.State.RecordFailureOccurrenceLocked(
            new FailureFingerprintInput
            {
                ErrorCode = "LOAD_FAILED", Phase = "LOADING", Message = "failure",
                Component = "coordinator", ProjectFingerprint = "profile-a"
            }, 1, "LOADING", "detail");
        fixture.State.AttachLatestFailureDiagnosisReferenceLocked();
        BridgeRequest evidence = Request("evidence", "forensic-agent", 22, "show",
            occurrence.EvidenceId, "--json");
        evidence.Json = true;
        int evidenceExit = fixture.State.Execute(evidence, _ => { }, () => true);
        EvidenceShowResponse evidenceResponse = fixture.State.CreateForensicJsonResponse(evidence,
            evidenceExit) as EvidenceShowResponse;
        Assert(evidenceExit == 0 && evidenceResponse?.Success == true &&
               evidenceResponse.Evidence.DiagnosisReference.Contains("incident-phase4", StringComparison.Ordinal),
            "evidence show must lazily expose the bounded crash-isolation diagnosis reference");
    }

    private static PersistedState ReadState(string root)
    {
        return JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(root, "Runtime", "state.json")), Program.JsonOptions);
    }
}
