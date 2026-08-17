using System.Globalization;
using System.Text.Json.Serialization;
using DevBridge2;

namespace DevBridge.Coordinator;

internal sealed class HistoryDiffResponse
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = DevBridgeSchemaVersions.HistoryDiff;

    [JsonPropertyName("contract")]
    public string Contract { get; init; } = DevBridgeSchemaVersions.HistoryDiffContract;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("command")]
    public string Command { get; init; } = "history diff";

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; init; }

    [JsonPropertyName("fromGeneration")]
    public int FromGeneration { get; init; }

    [JsonPropertyName("toGeneration")]
    public int ToGeneration { get; init; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryGenerationSummary From { get; init; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryGenerationSummary To { get; init; }

    [JsonPropertyName("changes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistorySemanticChanges Changes { get; init; }

    [JsonPropertyName("runtimeIdentityChanges")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryRuntimeIdentityChanges RuntimeIdentityChanges { get; init; }
}

internal sealed class HistoryDiagnosisResponse
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = DevBridgeSchemaVersions.HistoryDiagnosis;

    [JsonPropertyName("contract")]
    public string Contract { get; init; } = DevBridgeSchemaVersions.HistoryDiagnosisContract;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("command")]
    public string Command { get; init; } = "history diagnose";

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; init; }

    [JsonPropertyName("generation")]
    public int Generation { get; init; }

    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryGenerationSummary Target { get; init; }

    [JsonPropertyName("priorKnownGoodGeneration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PriorKnownGoodGeneration { get; init; }

    [JsonPropertyName("diff")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryDiffBody Diff { get; init; }

    [JsonPropertyName("failure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryFailureEvidence Failure { get; init; }

    [JsonPropertyName("recipe")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryRecipeContext Recipe { get; init; }

    [JsonPropertyName("crashIsolation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryCrashIsolationEvidence CrashIsolation { get; init; }

    [JsonPropertyName("proven")]
    public List<string> Proven { get; init; } = new();

    [JsonPropertyName("correlated")]
    public List<string> Correlated { get; init; } = new();

    [JsonPropertyName("unknown")]
    public List<string> Unknown { get; init; } = new();
}

internal sealed class HistoryDiffBody
{
    [JsonPropertyName("fromGeneration")]
    public int FromGeneration { get; init; }

    [JsonPropertyName("toGeneration")]
    public int ToGeneration { get; init; }

    [JsonPropertyName("from")]
    public HistoryGenerationSummary From { get; init; }

    [JsonPropertyName("to")]
    public HistoryGenerationSummary To { get; init; }

    [JsonPropertyName("changes")]
    public HistorySemanticChanges Changes { get; init; }

    [JsonPropertyName("runtimeIdentityChanges")]
    public HistoryRuntimeIdentityChanges RuntimeIdentityChanges { get; init; }
}

internal sealed class HistoryGenerationSummary
{
    [JsonPropertyName("generation")]
    public int Generation { get; init; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; }

    [JsonPropertyName("acceptedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? AcceptedUtc { get; init; }

    [JsonPropertyName("manifestAvailable")]
    public bool ManifestAvailable { get; init; }

    [JsonPropertyName("failureCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string FailureCode { get; init; }

    [JsonPropertyName("failureFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string FailureFingerprint { get; init; }
}

internal sealed class HistorySemanticChanges
{
    [JsonPropertyName("requestedProjects")]
    public HistoryListDelta RequestedProjects { get; init; } = new();

    [JsonPropertyName("resolvedPackageIds")]
    public HistoryListDelta ResolvedPackageIds { get; init; } = new();

    [JsonPropertyName("modLoadOrder")]
    public HistoryOrderDelta ModLoadOrder { get; init; } = new();

    [JsonPropertyName("testInputs")]
    public HistoryInputDelta TestInputs { get; init; } = new();

    [JsonPropertyName("profileFingerprint")]
    public HistoryValueDelta ProfileFingerprint { get; init; } = new();

    [JsonPropertyName("baselineFingerprint")]
    public HistoryValueDelta BaselineFingerprint { get; init; } = new();

    [JsonPropertyName("rimBridge")]
    public HistoryNamedValueDelta RimBridge { get; init; } = new();

    [JsonPropertyName("components")]
    public HistoryNamedValueDelta Components { get; init; } = new();

    [JsonPropertyName("recipe")]
    public HistoryNamedValueDelta Recipe { get; init; } = new();

    [JsonPropertyName("readiness")]
    public HistoryNamedValueDelta Readiness { get; init; } = new();

    [JsonPropertyName("failure")]
    public HistoryFailureDelta Failure { get; init; } = new();
}

internal sealed class HistoryRuntimeIdentityChanges
{
    [JsonPropertyName("launchId")]
    public HistoryValueDelta LaunchId { get; init; } = new();

    [JsonPropertyName("processId")]
    public HistoryValueDelta ProcessId { get; init; } = new();

    [JsonPropertyName("processStartUtc")]
    public HistoryValueDelta ProcessStartUtc { get; init; } = new();
}

internal class HistoryValueDelta
{
    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string From { get; init; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string To { get; init; }
}

internal sealed class HistoryNamedValueDelta : HistoryValueDelta
{
    [JsonPropertyName("fields")]
    public List<HistoryValueDeltaField> Fields { get; init; } = new();
}

internal sealed class HistoryValueDeltaField
{
    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string From { get; init; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string To { get; init; }
}

internal class HistoryListDelta
{
    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("added")]
    public List<string> Added { get; init; } = new();

    [JsonPropertyName("removed")]
    public List<string> Removed { get; init; } = new();
}

internal sealed class HistoryOrderDelta : HistoryListDelta
{
    [JsonPropertyName("moved")]
    public List<HistoryMovedMod> Moved { get; init; } = new();
}

internal sealed class HistoryMovedMod
{
    [JsonPropertyName("packageId")]
    public string PackageId { get; init; }

    [JsonPropertyName("fromIndex")]
    public int FromIndex { get; init; }

    [JsonPropertyName("toIndex")]
    public int ToIndex { get; init; }
}

internal sealed class HistoryInputDelta
{
    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("fields")]
    public List<HistoryInputFieldDelta> Fields { get; init; } = new();
}

internal sealed class HistoryInputFieldDelta
{
    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string From { get; init; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string To { get; init; }
}

internal sealed class HistoryFailureDelta
{
    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryFailureEvidence From { get; init; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryFailureEvidence To { get; init; }
}

internal sealed class HistoryFailureEvidence
{
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Code { get; init; }

    [JsonPropertyName("fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Fingerprint { get; init; }

    [JsonPropertyName("evidenceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string EvidenceId { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Summary { get; init; }

    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Phase { get; init; }

    [JsonPropertyName("component")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Component { get; init; }

    [JsonPropertyName("diagnosisReference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string DiagnosisReference { get; init; }
}

internal sealed class HistoryRecipeContext
{
    [JsonPropertyName("recipeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string RecipeId { get; init; }

    [JsonPropertyName("contextFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ContextFingerprint { get; init; }
}

internal sealed class HistoryCrashIsolationEvidence
{
    [JsonPropertyName("diagnosisCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string DiagnosisCode { get; init; }

    [JsonPropertyName("diagnosis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Diagnosis { get; init; }

    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Stage { get; init; }

    [JsonPropertyName("diagnoses")]
    public List<HistoryCrashDiagnosis> Diagnoses { get; init; } = new();

    [JsonPropertyName("attempts")]
    public List<HistoryCrashAttempt> Attempts { get; init; } = new();

    [JsonPropertyName("minimalIncompatibleSets")]
    public List<List<string>> MinimalIncompatibleSets { get; init; } = new();
}

internal sealed class HistoryCrashDiagnosis
{
    [JsonPropertyName("requestedProjects")]
    public List<string> RequestedProjects { get; init; } = new();

    [JsonPropertyName("resolvedPackageIds")]
    public List<string> ResolvedPackageIds { get; init; } = new();

    [JsonPropertyName("profileFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ProfileFingerprint { get; init; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Code { get; init; }
}

internal sealed class HistoryCrashAttempt
{
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Kind { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Result { get; init; }

    [JsonPropertyName("failureCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string FailureCode { get; init; }

    [JsonPropertyName("profileFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ProfileFingerprint { get; init; }

    [JsonPropertyName("requestedProjects")]
    public List<string> RequestedProjects { get; init; } = new();

    [JsonPropertyName("resolvedPackageIds")]
    public List<string> ResolvedPackageIds { get; init; } = new();
}

internal sealed class HistoryAnalysisData
{
    public GenerationHistoryRecord Record { get; init; }
    public GenerationManifest Manifest { get; init; }
    public FailureEvidenceRecord Evidence { get; init; }
    public HistoryGenerationSummary Summary { get; init; }
}

internal sealed class HistoryAnalysisResult
{
    public HistoryAnalysisData Data { get; init; }
    public string ErrorCode { get; init; }
    public string Error { get; init; }
    public bool Success => Data != null && ErrorCode == null;
}

internal sealed partial class CoordinatorState
{
    private const int HistoryAnalysisMaxList = 64;
    private const int HistoryAnalysisMaxConclusions = 16;

    internal int ExecuteHistoryDiff(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        request.HistoryDiffResult = null;
        if (arguments == null || arguments.Count != 2 ||
            !int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out int from) ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int to) ||
            from <= 0 || to <= 0 || from >= to)
        {
            request.HistoryDiffResult = HistoryDiffFailure(0, 0, "GENERATION_ORDER_INVALID",
                "history diff requires positive from/to generations with from < to");
            emit?.Invoke("history diff: invalid generation ordering");
            return 4;
        }

        lock (gate)
        {
            HistoryAnalysisResult left = LoadHistoryAnalysisLocked(from);
            if (!left.Success)
            {
                request.HistoryDiffResult = HistoryDiffFailure(from, to, left.ErrorCode, left.Error);
                emit?.Invoke("history diff: " + left.ErrorCode);
                return 4;
            }

            HistoryAnalysisResult right = LoadHistoryAnalysisLocked(to);
            if (!right.Success)
            {
                request.HistoryDiffResult = HistoryDiffFailure(from, to, right.ErrorCode, right.Error);
                emit?.Invoke("history diff: " + right.ErrorCode);
                return 4;
            }

            HistoryDiffBody body = BuildHistoryDiffBody(left.Data, right.Data);
            request.HistoryDiffResult = new HistoryDiffResponse
            {
                Success = true,
                ExitCode = 0,
                FromGeneration = from,
                ToGeneration = to,
                From = body.From,
                To = body.To,
                Changes = body.Changes,
                RuntimeIdentityChanges = body.RuntimeIdentityChanges
            };
            EmitHistoryDiff(body, emit);
            return 0;
        }
    }

    internal int ExecuteHistoryDiagnose(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        request.HistoryDiagnosisResult = null;
        if (arguments == null || arguments.Count != 1 ||
            !int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out int generation) ||
            generation <= 0)
        {
            request.HistoryDiagnosisResult = HistoryDiagnosisFailure(0, "GENERATION_INVALID",
                "history diagnose requires one positive generation");
            emit?.Invoke("history diagnose: invalid generation");
            return 4;
        }

        lock (gate)
        {
            HistoryAnalysisResult target = LoadHistoryAnalysisLocked(generation);
            if (!target.Success)
            {
                request.HistoryDiagnosisResult = HistoryDiagnosisFailure(generation, target.ErrorCode, target.Error);
                emit?.Invoke("history diagnose: " + target.ErrorCode);
                return 4;
            }

            GenerationHistoryEnvelope envelope;
            if (!TryLoadGenerationHistoryLocked(out envelope, out string historyErrorCode, out string historyError))
            {
                request.HistoryDiagnosisResult = HistoryDiagnosisFailure(generation, historyErrorCode, historyError);
                emit?.Invoke("history diagnose: " + historyErrorCode);
                return 4;
            }

            HistoryAnalysisData prior = null;
            foreach (GenerationHistoryRecord record in envelope.Records
                .Where(value => value != null && value.Generation < generation)
                .OrderByDescending(value => value.Generation)
                .Take(HistoryAnalysisMaxList))
            {
                if (!string.Equals(record.Status, "READY", StringComparison.OrdinalIgnoreCase))
                    continue;
                HistoryAnalysisResult candidate = LoadHistoryAnalysisLocked(record.Generation);
                if (!candidate.Success)
                {
                    request.HistoryDiagnosisResult = HistoryDiagnosisFailure(generation,
                        candidate.ErrorCode, candidate.Error);
                    emit?.Invoke("history diagnose: " + candidate.ErrorCode);
                    return 4;
                }
                prior = candidate.Data;
                break;
            }

            HistoryDiffBody diff = prior == null ? null : BuildHistoryDiffBody(prior, target.Data);
            HistoryFailureEvidence failure = BuildFailureEvidence(target.Data);
            HistoryRecipeContext recipe = BuildRecipeContext(target.Data);
            HistoryCrashIsolationEvidence crash = BuildCrashIsolationEvidence(generation);
            HistoryDiagnosisResponse response = new()
            {
                Success = true,
                ExitCode = 0,
                Generation = generation,
                Target = target.Data.Summary,
                PriorKnownGoodGeneration = prior?.Record.Generation,
                Diff = diff,
                Failure = failure,
                Recipe = recipe,
                CrashIsolation = crash
            };
            AddDiagnosisConclusions(response, target.Data, prior, diff, failure, crash);
            request.HistoryDiagnosisResult = response;
            EmitHistoryDiagnosis(response, emit);
            return 0;
        }
    }

    private HistoryAnalysisResult LoadHistoryAnalysisLocked(int generation)
    {
        if (!TryLoadGenerationHistoryLocked(out GenerationHistoryEnvelope envelope,
            out string historyErrorCode, out string historyError))
            return new HistoryAnalysisResult { ErrorCode = historyErrorCode, Error = historyError };

        GenerationHistoryRecord record = envelope.Records?.FirstOrDefault(value =>
            value != null && value.Generation == generation);
        if (record == null)
            return new HistoryAnalysisResult
            {
                ErrorCode = "GENERATION_NOT_FOUND",
                Error = "generation " + generation.ToString(CultureInfo.InvariantCulture) + " is not present in immutable history"
            };

        GenerationHistoryView view = new();
        GenerationHistoryEntry entry = ReadHistoryEntryLocked(record, view);
        if (view.Corrupt)
            return new HistoryAnalysisResult
            {
                ErrorCode = view.ErrorCode ?? "GENERATION_MANIFEST_CORRUPT",
                Error = view.Error ?? "generation manifest is corrupt"
            };

        GenerationManifest manifest = entry?.Manifest;
        if (manifest != null && !IsValidGenerationManifest(manifest, generation))
            return new HistoryAnalysisResult
            {
                ErrorCode = "GENERATION_MANIFEST_CORRUPT",
                Error = "generation manifest failed immutable evidence validation"
            };

        string evidenceId = FirstNonEmpty(record.FailureEvidenceId, manifest?.Failure?.EvidenceId);
        FailureEvidenceRecord evidence = null;
        if (!string.IsNullOrWhiteSpace(evidenceId))
        {
            FailureEvidenceStore store = new(runtimeRoot, () => clock.UtcNow);
            EvidenceLookupResult evidenceLookup = store.Read(evidenceId);
            evidence = evidenceLookup.Record;
            if (evidence == null)
                return new HistoryAnalysisResult
                {
                    ErrorCode = evidenceLookup.ErrorCode ?? "EVIDENCE_NOT_FOUND",
                    Error = evidenceLookup.Error ?? "referenced failure evidence is unavailable"
                };
        }

        return new HistoryAnalysisResult
        {
            Data = new HistoryAnalysisData
            {
                Record = record,
                Manifest = manifest,
                Evidence = evidence,
                Summary = BuildSummary(record, manifest, evidence)
            }
        };
    }

    private static HistoryGenerationSummary BuildSummary(GenerationHistoryRecord record,
        GenerationManifest manifest, FailureEvidenceRecord evidence)
    {
        string failureCode = FirstNonEmpty(record?.TerminalFailureCode, evidence?.ErrorCode);
        string fingerprint = FirstNonEmpty(record?.FailureFingerprint, manifest?.Failure?.FailureFingerprint,
            evidence?.FailureFingerprint);
        return new HistoryGenerationSummary
        {
            Generation = record?.Generation ?? manifest?.Generation ?? 0,
            Outcome = Bound(record?.Status ?? (manifest == null ? "UNKNOWN" : "READY"), 32),
            AcceptedUtc = manifest?.AcceptedUtc,
            ManifestAvailable = manifest != null,
            FailureCode = Bound(failureCode, 96),
            FailureFingerprint = Bound(fingerprint, 128)
        };
    }

    private static HistoryDiffBody BuildHistoryDiffBody(HistoryAnalysisData from, HistoryAnalysisData to)
    {
        HistorySnapshot left = new(from);
        HistorySnapshot right = new(to);
        return new HistoryDiffBody
        {
            FromGeneration = from.Record.Generation,
            ToGeneration = to.Record.Generation,
            From = from.Summary,
            To = to.Summary,
            Changes = new HistorySemanticChanges
            {
                RequestedProjects = ListDelta(left.ProfileAvailable || right.ProfileAvailable, left.RequestedProjects, right.RequestedProjects),
                ResolvedPackageIds = ListDelta(left.ProfileAvailable || right.ProfileAvailable, left.ResolvedPackageIds, right.ResolvedPackageIds),
                ModLoadOrder = OrderDelta(left.ProfileAvailable || right.ProfileAvailable, left.ModOrder, right.ModOrder),
                TestInputs = InputDelta(left.InputsAvailable || right.InputsAvailable, left.Inputs, right.Inputs),
                ProfileFingerprint = ValueDelta(left.ProfileAvailable || right.ProfileAvailable, left.ProfileFingerprint, right.ProfileFingerprint),
                BaselineFingerprint = ValueDelta(left.ProfileAvailable || right.ProfileAvailable, left.BaselineFingerprint, right.BaselineFingerprint),
                RimBridge = NamedDelta(left.ProfileAvailable || right.ProfileAvailable, new Dictionary<string, string>
                {
                    ["mode"] = left.RimBridgeMode,
                    ["version"] = left.RimBridgeVersion
                }, new Dictionary<string, string>
                {
                    ["mode"] = right.RimBridgeMode,
                    ["version"] = right.RimBridgeVersion
                }),
                Components = ComponentDelta(left, right),
                Recipe = NamedDelta(left.RecipeAvailable || right.RecipeAvailable, new Dictionary<string, string>
                {
                    ["recipeId"] = left.RecipeId,
                    ["contextFingerprint"] = left.RecipeContextFingerprint
                }, new Dictionary<string, string>
                {
                    ["recipeId"] = right.RecipeId,
                    ["contextFingerprint"] = right.RecipeContextFingerprint
                }),
                Readiness = NamedDelta(left.ReadinessAvailable || right.ReadinessAvailable, new Dictionary<string, string>
                {
                    ["result"] = left.ReadinessResult,
                    ["quicktestRequired"] = left.QuicktestRequired,
                    ["quicktestVariant"] = left.QuicktestVariant,
                    ["quicktestTimeoutSeconds"] = left.QuicktestTimeoutSeconds,
                    ["bridgeReadyRequired"] = left.BridgeReadyRequired,
                    ["quicktestFailureCode"] = left.QuicktestFailureCode
                }, new Dictionary<string, string>
                {
                    ["result"] = right.ReadinessResult,
                    ["quicktestRequired"] = right.QuicktestRequired,
                    ["quicktestVariant"] = right.QuicktestVariant,
                    ["quicktestTimeoutSeconds"] = right.QuicktestTimeoutSeconds,
                    ["bridgeReadyRequired"] = right.BridgeReadyRequired,
                    ["quicktestFailureCode"] = right.QuicktestFailureCode
                }),
                Failure = FailureDelta(left.Failure, right.Failure)
            },
            RuntimeIdentityChanges = new HistoryRuntimeIdentityChanges
            {
                LaunchId = ValueDelta(left.RuntimeAvailable || right.RuntimeAvailable, left.LaunchId, right.LaunchId),
                ProcessId = ValueDelta(left.RuntimeAvailable || right.RuntimeAvailable, left.ProcessId, right.ProcessId),
                ProcessStartUtc = ValueDelta(left.RuntimeAvailable || right.RuntimeAvailable, left.ProcessStartUtc, right.ProcessStartUtc)
            }
        };
    }

    private static HistoryListDelta ListDelta(bool available, IEnumerable<string> from, IEnumerable<string> to)
    {
        List<string> left = BoundedDistinct(from);
        List<string> right = BoundedDistinct(to);
        return new HistoryListDelta
        {
            Available = available,
            Changed = available && !left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase),
            Added = right.Except(left, StringComparer.OrdinalIgnoreCase).Take(HistoryAnalysisMaxList).ToList(),
            Removed = left.Except(right, StringComparer.OrdinalIgnoreCase).Take(HistoryAnalysisMaxList).ToList()
        };
    }

    private static HistoryOrderDelta OrderDelta(bool available, IReadOnlyList<string> from, IReadOnlyList<string> to)
    {
        HistoryListDelta baseDelta = ListDelta(available, from, to);
        List<HistoryMovedMod> moved = new();
        if (available)
        {
            List<string> leftValues = BoundedDistinct(from);
            List<string> rightValues = BoundedDistinct(to);
            Dictionary<string, int> left = leftValues.Select((value, index) => new { value, index })
                .ToDictionary(value => value.value, value => value.index, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < rightValues.Count && moved.Count < HistoryAnalysisMaxList; index++)
            {
                string packageId = rightValues[index];
                if (!left.TryGetValue(packageId, out int oldIndex) || oldIndex == index)
                    continue;
                moved.Add(new HistoryMovedMod { PackageId = Bound(packageId, 128), FromIndex = oldIndex, ToIndex = index });
            }
        }
        return new HistoryOrderDelta
        {
            Available = baseDelta.Available,
            Changed = baseDelta.Changed || moved.Count > 0,
            Added = baseDelta.Added,
            Removed = baseDelta.Removed,
            Moved = moved
        };
    }

    private static HistoryInputDelta InputDelta(bool available, IReadOnlyDictionary<string, string> from,
        IReadOnlyDictionary<string, string> to)
    {
        List<HistoryInputFieldDelta> fields = new();
        foreach (string name in from.Keys.Concat(to.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(HistoryAnalysisMaxList))
        {
            from.TryGetValue(name, out string left);
            to.TryGetValue(name, out string right);
            if (!string.Equals(left, right, StringComparison.Ordinal))
                fields.Add(new HistoryInputFieldDelta { Name = Bound(name, 64), From = Bound(left, 128), To = Bound(right, 128) });
        }
        return new HistoryInputDelta { Available = available, Changed = available && fields.Count > 0, Fields = fields };
    }

    private static HistoryValueDelta ValueDelta(bool available, string from, string to) => new()
    {
        Available = available,
        Changed = available && !string.Equals(from, to, StringComparison.Ordinal),
        From = Bound(from, 256),
        To = Bound(to, 256)
    };

    private static HistoryNamedValueDelta NamedDelta(bool available, IReadOnlyDictionary<string, string> from,
        IReadOnlyDictionary<string, string> to)
    {
        List<HistoryValueDeltaField> fields = new();
        foreach (string name in from.Keys.Concat(to.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(HistoryAnalysisMaxList))
        {
            from.TryGetValue(name, out string left);
            to.TryGetValue(name, out string right);
            if (!string.Equals(left, right, StringComparison.Ordinal))
                fields.Add(new HistoryValueDeltaField { Name = Bound(name, 64), From = Bound(left, 256), To = Bound(right, 256) });
        }
        return new HistoryNamedValueDelta
        {
            Available = available,
            Changed = available && fields.Count > 0,
            From = null,
            To = null,
            Fields = fields
        };
    }

    private static HistoryNamedValueDelta ComponentDelta(HistorySnapshot left, HistorySnapshot right)
    {
        return NamedDelta(left.ComponentsAvailable || right.ComponentsAvailable,
            left.Components, right.Components);
    }

    private static HistoryFailureDelta FailureDelta(HistoryFailureEvidence from, HistoryFailureEvidence to)
    {
        bool available = from != null || to != null;
        bool changed = !FailureEquals(from, to);
        return new HistoryFailureDelta { Available = available, Changed = available && changed, From = from, To = to };
    }

    private static bool FailureEquals(HistoryFailureEvidence left, HistoryFailureEvidence right) =>
        string.Equals(left?.Code, right?.Code, StringComparison.Ordinal) &&
        string.Equals(left?.Fingerprint, right?.Fingerprint, StringComparison.Ordinal) &&
        string.Equals(left?.EvidenceId, right?.EvidenceId, StringComparison.Ordinal) &&
        string.Equals(left?.DiagnosisReference, right?.DiagnosisReference, StringComparison.Ordinal);

    private static HistoryFailureEvidence BuildFailureEvidence(HistoryAnalysisData data)
    {
        string evidenceId = FirstNonEmpty(data.Record?.FailureEvidenceId, data.Manifest?.Failure?.EvidenceId,
            data.Evidence?.EvidenceId);
        string fingerprint = FirstNonEmpty(data.Record?.FailureFingerprint, data.Manifest?.Failure?.FailureFingerprint,
            data.Evidence?.FailureFingerprint);
        string code = FirstNonEmpty(data.Record?.TerminalFailureCode,
            data.Evidence?.ErrorCode);
        if (string.IsNullOrWhiteSpace(evidenceId) && string.IsNullOrWhiteSpace(fingerprint) && string.IsNullOrWhiteSpace(code) &&
            data.Evidence == null && data.Manifest?.Failure == null)
            return null;
        return new HistoryFailureEvidence
        {
            Code = Bound(code, 96),
            Fingerprint = Bound(fingerprint, 128),
            EvidenceId = Bound(evidenceId, 96),
            Summary = Bound(FirstNonEmpty(data.Manifest?.Failure?.Summary, data.Evidence?.Summary), 512),
            Phase = Bound(data.Evidence?.Phase, 96),
            Component = Bound(data.Evidence?.Component, 96),
            DiagnosisReference = Bound(FirstNonEmpty(data.Manifest?.Failure?.DiagnosisReference,
                data.Evidence?.DiagnosisReference), 128)
        };
    }

    private static HistoryRecipeContext BuildRecipeContext(HistoryAnalysisData data)
    {
        string recipeId = FirstNonEmpty(data.Manifest?.RecipeContext?.RecipeId, data.Evidence?.RecipeId);
        string fingerprint = FirstNonEmpty(data.Manifest?.RecipeContext?.ReproductionContextFingerprint,
            data.Evidence?.ReproductionContextFingerprint);
        if (string.IsNullOrWhiteSpace(recipeId) && string.IsNullOrWhiteSpace(fingerprint))
            return null;
        return new HistoryRecipeContext { RecipeId = Bound(recipeId, 128), ContextFingerprint = Bound(fingerprint, 128) };
    }

    private HistoryCrashIsolationEvidence BuildCrashIsolationEvidence(int generation)
    {
        IEnumerable<CrashIsolationIncident> incidents = (state.CrashIsolationHistory ?? new List<CrashIsolationIncident>())
            .Concat(state.CrashIsolation == null ? Enumerable.Empty<CrashIsolationIncident>() : new[] { state.CrashIsolation })
            .Where(value => value != null && value.OriginalGeneration == generation)
            .Take(HistoryAnalysisMaxList);
        CrashIsolationIncident incident = incidents.LastOrDefault();
        if (incident == null)
            return null;

        HistoryCrashIsolationEvidence result = new()
        {
            DiagnosisCode = Bound(incident.DiagnosisCode, 128),
            Diagnosis = Bound(incident.Diagnosis, 512),
            Stage = Bound(incident.Stage, 96)
        };
        foreach (CrashIsolationDiagnosis diagnosis in (incident.Diagnoses ?? new List<CrashIsolationDiagnosis>())
            .Where(value => value != null).Take(HistoryAnalysisMaxList))
        {
            result.Diagnoses.Add(new HistoryCrashDiagnosis
            {
                RequestedProjects = BoundedStrings(diagnosis.RequestedProjects),
                ResolvedPackageIds = BoundedStrings(diagnosis.ResolvedProjectPackageIds),
                ProfileFingerprint = Bound(diagnosis.ProfileFingerprint, 128),
                Code = Bound(diagnosis.Code, 128)
            });
        }

        foreach (CrashIsolationAttempt attempt in (incident.Attempts ?? new List<CrashIsolationAttempt>())
            .Where(value => value != null).Take(HistoryAnalysisMaxList))
        {
            result.Attempts.Add(new HistoryCrashAttempt
            {
                Kind = Bound(attempt.Kind, 96),
                Result = Bound(attempt.Result, 96),
                FailureCode = Bound(attempt.FailureCode, 96),
                ProfileFingerprint = Bound(attempt.ProfileFingerprint, 128),
                RequestedProjects = BoundedStrings(attempt.RequestedProjects),
                ResolvedPackageIds = BoundedStrings(attempt.ResolvedProjectPackageIds)
            });
        }

        foreach (CrashIsolationDiagnosis diagnosis in (incident.Diagnoses ?? new List<CrashIsolationDiagnosis>())
            .Where(value => value != null))
        {
            if (result.MinimalIncompatibleSets.Count >= HistoryAnalysisMaxList)
                break;
            List<string> projects = BoundedStrings(diagnosis.RequestedProjects);
            if (projects.Count > 0)
                result.MinimalIncompatibleSets.Add(projects);
        }
        return result;
    }

    private static void AddDiagnosisConclusions(HistoryDiagnosisResponse response, HistoryAnalysisData target,
        HistoryAnalysisData prior, HistoryDiffBody diff, HistoryFailureEvidence failure,
        HistoryCrashIsolationEvidence crash)
    {
        AddConclusion(response.Proven, "generation " + target.Record.Generation.ToString(CultureInfo.InvariantCulture) +
            " outcome is " + target.Summary.Outcome);
        if (failure != null)
            AddConclusion(response.Proven, "generation " + target.Record.Generation.ToString(CultureInfo.InvariantCulture) +
                " failed " + (failure.Code ?? "with an unclassified failure") +
                (failure.Fingerprint == null ? string.Empty : " with fingerprint " + failure.Fingerprint));
        if (prior != null)
        {
            AddConclusion(response.Proven, "generation " + prior.Record.Generation.ToString(CultureInfo.InvariantCulture) + " was READY");
            foreach (string item in diff.Changes.RequestedProjects.Added.Take(HistoryAnalysisMaxConclusions))
                AddConclusion(response.Proven, "project " + item + " was added");
            foreach (string item in diff.Changes.ResolvedPackageIds.Added.Take(HistoryAnalysisMaxConclusions))
                AddConclusion(response.Proven, "package " + item + " was added");
            foreach (HistoryInputFieldDelta item in diff.Changes.TestInputs.Fields.Take(HistoryAnalysisMaxConclusions))
                AddConclusion(response.Proven, item.Name + " changed from " + (item.From ?? "<missing>") +
                    " to " + (item.To ?? "<missing>"));
        }
        else
            AddConclusion(response.Unknown, "no prior READY generation was available");

        if (failure?.Component != null)
            AddConclusion(response.Correlated, "failure evidence names component " + failure.Component);
        if (crash?.DiagnosisCode != null)
            AddConclusion(response.Correlated, "durable crash-isolation evidence has diagnosis " + crash.DiagnosisCode);
        if (prior != null && diff.Changes.ResolvedPackageIds.Changed)
            AddConclusion(response.Unknown, "history evidence does not prove that a changed package caused the failure");
        if (response.Proven.Count == 0)
            AddConclusion(response.Unknown, "available immutable evidence does not establish a causal explanation");
    }

    private static void AddConclusion(List<string> list, string value)
    {
        if (list.Count < HistoryAnalysisMaxConclusions && !string.IsNullOrWhiteSpace(value))
            list.Add(Bound(value, 512));
    }

    private static HistoryDiffResponse HistoryDiffFailure(int from, int to, string errorCode, string error) => new()
    {
        Success = false,
        ExitCode = 4,
        FromGeneration = from,
        ToGeneration = to,
        ErrorCode = Bound(errorCode ?? "HISTORY_ANALYSIS_FAILED", 96),
        Error = Bound(error ?? "history analysis failed", 512)
    };

    private static HistoryDiagnosisResponse HistoryDiagnosisFailure(int generation, string errorCode, string error) => new()
    {
        Success = false,
        ExitCode = 4,
        Generation = generation,
        ErrorCode = Bound(errorCode ?? "HISTORY_ANALYSIS_FAILED", 96),
        Error = Bound(error ?? "history analysis failed", 512)
    };

    private static void EmitHistoryDiff(HistoryDiffBody body, Action<string> emit)
    {
        if (emit == null)
            return;
        emit("history diff " + body.FromGeneration.ToString(CultureInfo.InvariantCulture) + " -> " +
            body.ToGeneration.ToString(CultureInfo.InvariantCulture));
        if (!body.Changes.RequestedProjects.Changed && !body.Changes.ResolvedPackageIds.Changed &&
            !body.Changes.ModLoadOrder.Changed && !body.Changes.TestInputs.Changed &&
            !body.Changes.ProfileFingerprint.Changed && !body.Changes.BaselineFingerprint.Changed &&
            !body.Changes.RimBridge.Changed && !body.Changes.Components.Changed &&
            !body.Changes.Recipe.Changed && !body.Changes.Readiness.Changed && !body.Changes.Failure.Changed)
            emit("  no semantic changes");
        else
            emit("  semantic changes detected");
        if (body.RuntimeIdentityChanges.LaunchId.Changed || body.RuntimeIdentityChanges.ProcessId.Changed)
            emit("  runtime identity changed (launch/process identity is separate from semantic changes)");
    }

    private static void EmitHistoryDiagnosis(HistoryDiagnosisResponse response, Action<string> emit)
    {
        if (emit == null)
            return;
        emit("history diagnose " + response.Generation.ToString(CultureInfo.InvariantCulture));
        foreach (string value in response.Proven.Take(HistoryAnalysisMaxConclusions))
            emit("PROVEN: " + value);
        foreach (string value in response.Correlated.Take(HistoryAnalysisMaxConclusions))
            emit("CORRELATED: " + value);
        foreach (string value in response.Unknown.Take(HistoryAnalysisMaxConclusions))
            emit("UNKNOWN: " + value);
    }

    private sealed class HistorySnapshot
    {
        internal HistorySnapshot(HistoryAnalysisData data)
        {
            GenerationManifest manifest = data.Manifest;
            GenerationHistoryRecord record = data.Record;
            GenerationProfileEvidence profile = manifest?.Profile;
            ProfileAvailable = profile != null;
            RequestedProjects = profile?.RequestedProjects ?? new List<string>();
            ResolvedPackageIds = profile?.ResolvedProjectPackageIds ?? new List<string>();
            ModOrder = manifest?.ModsConfig?.ResolvedModOrder ?? profile?.ResolvedMods ?? new List<string>();
            InputsAvailable = profile != null || record?.TestInputs != null;
            Inputs = (profile?.TestInputs ?? record?.TestInputs ?? new List<TestInputValue>())
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.Name))
                .GroupBy(value => Bound(value.Name, 64), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group => group.First())
                .Take(HistoryAnalysisMaxList)
                .ToDictionary(value => Bound(value.Name, 64), value => Bound(value.Value, 128), StringComparer.OrdinalIgnoreCase);
            ProfileFingerprint = FirstNonEmpty(profile?.ProfileFingerprint);
            BaselineFingerprint = FirstNonEmpty(profile?.BaselineFingerprint, manifest?.ModsConfig?.BaselineFingerprint);
            RimBridgeMode = profile?.RimBridgeMode;
            RimBridgeVersion = profile?.RimBridgeVersion;
            RuntimeAvailable = manifest?.Process != null || manifest?.Launch != null;
            LaunchId = manifest?.Launch?.LaunchId;
            ProcessId = manifest?.Process?.ProcessId.ToString(CultureInfo.InvariantCulture);
            ProcessStartUtc = manifest?.Process == null || manifest.Process.ProcessStartUtcTicks <= 0
                ? null
                : new DateTime(manifest.Process.ProcessStartUtcTicks, DateTimeKind.Utc)
                    .ToString("O", CultureInfo.InvariantCulture);
            ReadinessAvailable = manifest?.Readiness != null;
            ReadinessResult = manifest?.Readiness?.Result;
            QuicktestRequired = manifest?.Readiness == null ? null : manifest.Readiness.QuicktestRequired.ToString();
            QuicktestVariant = manifest?.Readiness?.QuicktestVariant;
            QuicktestTimeoutSeconds = manifest?.Readiness == null ? null : manifest.Readiness.QuicktestTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            BridgeReadyRequired = manifest?.Readiness == null ? null : manifest.Readiness.BridgeReadyRequired.ToString();
            QuicktestFailureCode = null;
            ComponentsAvailable = manifest?.Components != null;
            Components = ComponentFields(manifest?.Components);
            RecipeAvailable = manifest?.RecipeContext != null || data.Evidence?.RecipeId != null || data.Evidence?.ReproductionContextFingerprint != null;
            RecipeId = FirstNonEmpty(manifest?.RecipeContext?.RecipeId, data.Evidence?.RecipeId);
            RecipeContextFingerprint = FirstNonEmpty(manifest?.RecipeContext?.ReproductionContextFingerprint, data.Evidence?.ReproductionContextFingerprint);
            Failure = BuildFailureEvidence(data);
        }

        internal bool ProfileAvailable { get; }
        internal bool InputsAvailable { get; }
        internal bool RuntimeAvailable { get; }
        internal bool ReadinessAvailable { get; }
        internal bool ComponentsAvailable { get; }
        internal bool RecipeAvailable { get; }
        internal List<string> RequestedProjects { get; }
        internal List<string> ResolvedPackageIds { get; }
        internal List<string> ModOrder { get; }
        internal Dictionary<string, string> Inputs { get; }
        internal string ProfileFingerprint { get; }
        internal string BaselineFingerprint { get; }
        internal string RimBridgeMode { get; }
        internal string RimBridgeVersion { get; }
        internal string LaunchId { get; }
        internal string ProcessId { get; }
        internal string ProcessStartUtc { get; }
        internal string ReadinessResult { get; }
        internal string QuicktestRequired { get; }
        internal string QuicktestVariant { get; }
        internal string QuicktestTimeoutSeconds { get; }
        internal string BridgeReadyRequired { get; }
        internal string QuicktestFailureCode { get; }
        internal Dictionary<string, string> Components { get; }
        internal string RecipeId { get; }
        internal string RecipeContextFingerprint { get; }
        internal HistoryFailureEvidence Failure { get; }
    }

    private static Dictionary<string, string> ComponentFields(ComponentVersionReport report)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (report == null)
            return result;
        AddComponent(result, "cliWrapperVersion", report.CliWrapperVersion);
        AddComponent(result, "coordinatorVersion", report.CoordinatorVersion);
        AddComponent(result, "modVersion", report.ModVersion);
        AddComponent(result, "bridgeToolsVersion", report.BridgeToolsVersion);
        AddComponent(result, "runtimeStateSchema", report.RuntimeStateSchema);
        AddComponent(result, "readinessSchema", report.ReadinessSchema);
        AddComponent(result, "generationManifestSchema", report.GenerationManifestSchema);
        AddComponent(result, "generationHistorySchema", report.GenerationHistorySchema);
        AddComponent(result, "quicktestFailureSchema", report.QuicktestFailureSchema.ToString(CultureInfo.InvariantCulture));
        AddComponent(result, "coordinatorProtocolMajor", report.CoordinatorProtocolMajor.ToString(CultureInfo.InvariantCulture));
        AddComponent(result, "modProtocolMajor", report.ModProtocolMajor.ToString(CultureInfo.InvariantCulture));
        AddBuild(result, "coordinatorBuild", report.CoordinatorBuild);
        AddBuild(result, "publishedCoordinatorBuild", report.PublishedCoordinatorBuild);
        AddBuild(result, "modBuild", report.ModBuild);
        AddBuild(result, "bridgeToolsBuild", report.BridgeToolsBuild);
        return result;
    }

    private static void AddBuild(Dictionary<string, string> target, string name, DevBridgeBuildIdentity build)
    {
        if (build == null)
            return;
        AddComponent(target, name + ".productVersion", build.ProductVersion);
        AddComponent(target, name + ".informationalVersion", build.InformationalVersion);
        AddComponent(target, name + ".sourceRevision", build.SourceRevision);
        AddComponent(target, name + ".revisionKnown", build.RevisionKnown.ToString(CultureInfo.InvariantCulture));
        AddComponent(target, name + ".dirty", build.Dirty.ToString(CultureInfo.InvariantCulture));
        AddComponent(target, name + ".buildConfiguration", build.BuildConfiguration);
    }

    private static void AddComponent(Dictionary<string, string> target, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target[name] = Bound(value, 256);
    }

    private static List<string> BoundedDistinct(IEnumerable<string> values) => (values ?? Enumerable.Empty<string>())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => Bound(value.Trim(), 128))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(HistoryAnalysisMaxList)
        .ToList();

    private static List<string> BoundedStrings(IEnumerable<string> values) => BoundedDistinct(values);

    private static string FirstNonEmpty(params string[] values) => values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string Bound(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string redacted = DiagnosticRedactor.Text(value.Trim());
        return string.IsNullOrWhiteSpace(redacted)
            ? null
            : (redacted.Length <= max ? redacted : redacted.Substring(0, max));
    }
}
