using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBridge2;

namespace DevBridge.Coordinator;

// This file deliberately contains only an allow-listed, semantic record of a
// generation. It is not a copy of state.json and must never contain endpoint
// credentials, exception objects, or arbitrary diagnostic payloads.
internal sealed class GenerationManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = DevBridgeSchemaVersions.GenerationManifest;

    [JsonPropertyName("contract")]
    public string Contract { get; set; } = DevBridgeSchemaVersions.GenerationManifestContract;

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("acceptedUtc")]
    public DateTime AcceptedUtc { get; set; }

    [JsonPropertyName("launch")]
    public GenerationLaunchEvidence Launch { get; set; } = new();

    [JsonPropertyName("profile")]
    public GenerationProfileEvidence Profile { get; set; } = new();

    [JsonPropertyName("modsConfig")]
    public GenerationModsConfigEvidence ModsConfig { get; set; } = new();

    [JsonPropertyName("process")]
    public GenerationProcessEvidence Process { get; set; } = new();

    [JsonPropertyName("readiness")]
    public GenerationReadinessEvidence Readiness { get; set; } = new();

    [JsonPropertyName("components")]
    public ComponentVersionReport Components { get; set; }

    [JsonPropertyName("failure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationFailureEvidence Failure { get; set; }

    [JsonPropertyName("companion")]
    public GenerationCompanionEvidence Companion { get; set; } = new();

    [JsonPropertyName("recipeContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationRecipeContextEvidence RecipeContext { get; set; }
}

internal sealed class GenerationFailureEvidence
{
    [JsonPropertyName("failureFingerprint")]
    public string FailureFingerprint { get; set; }

    [JsonPropertyName("seenBefore")]
    public bool SeenBefore { get; set; }

    [JsonPropertyName("evidenceId")]
    public string EvidenceId { get; set; }

    [JsonPropertyName("diagnosisReference")]
    public string DiagnosisReference { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }
}

internal sealed class GenerationCompanionEvidence
{
    [JsonPropertyName("lifecycleState")]
    public string LifecycleState { get; set; }

    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }

    [JsonPropertyName("diagnosticCode")]
    public string DiagnosticCode { get; set; }
}

internal sealed class GenerationRecipeContextEvidence
{
    [JsonPropertyName("recipeId")]
    public string RecipeId { get; set; }

    [JsonPropertyName("reproductionContextFingerprint")]
    public string ReproductionContextFingerprint { get; set; }

    [JsonPropertyName("projectFingerprint")]
    public string ProjectFingerprint { get; set; }
}

internal sealed class GenerationLaunchEvidence
{
    [JsonPropertyName("launchId")]
    public string LaunchId { get; set; }

    [JsonPropertyName("launchGeneration")]
    public int LaunchGeneration { get; set; }

    [JsonPropertyName("launchStartedUtc")]
    public DateTime LaunchStartedUtc { get; set; }

    [JsonPropertyName("profileInstalled")]
    public bool ProfileInstalled { get; set; }

    [JsonPropertyName("attemptCount")]
    public int AttemptCount { get; set; }

    [JsonPropertyName("requestKeyPresent")]
    public bool RequestKeyPresent { get; set; }
}

internal sealed class GenerationProfileEvidence
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; }

    [JsonPropertyName("launchMode")]
    public string LaunchMode { get; set; }

    [JsonPropertyName("requestedProjects")]
    public List<string> RequestedProjects { get; set; } = new();

    [JsonPropertyName("resolvedProjectPackageIds")]
    public List<string> ResolvedProjectPackageIds { get; set; } = new();

    [JsonPropertyName("resolvedMods")]
    public List<string> ResolvedMods { get; set; } = new();

    [JsonPropertyName("testInputs")]
    public List<TestInputValue> TestInputs { get; set; } = new();

    [JsonPropertyName("profileFingerprint")]
    public string ProfileFingerprint { get; set; }

    [JsonPropertyName("baselineFingerprint")]
    public string BaselineFingerprint { get; set; }

    [JsonPropertyName("rimBridgeMode")]
    public string RimBridgeMode { get; set; }

    [JsonPropertyName("rimBridgeVersion")]
    public string RimBridgeVersion { get; set; }

    [JsonPropertyName("registrations")]
    public List<GenerationRegistrationEvidence> Registrations { get; set; } = new();
}

internal sealed class GenerationRegistrationEvidence
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("owner")]
    public string Owner { get; set; }

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; }

    [JsonPropertyName("requestedProjects")]
    public List<string> RequestedProjects { get; set; } = new();
}

internal sealed class GenerationModsConfigEvidence
{
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; }

    [JsonPropertyName("baselineFingerprint")]
    public string BaselineFingerprint { get; set; }

    [JsonPropertyName("ownership")]
    public string Ownership { get; set; }

    [JsonPropertyName("resolvedModOrder")]
    public List<string> ResolvedModOrder { get; set; } = new();
}

internal sealed class GenerationProcessEvidence
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("processStartUtcTicks")]
    public long ProcessStartUtcTicks { get; set; }

    [JsonPropertyName("executableName")]
    public string ExecutableName { get; set; }
}

internal sealed class GenerationReadinessEvidence
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = "READY";

    [JsonPropertyName("observedUtc")]
    public DateTime ObservedUtc { get; set; }

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("launchId")]
    public string LaunchId { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("quicktestRequired")]
    public bool QuicktestRequired { get; set; } = true;

    [JsonPropertyName("quicktestVariant")]
    public string QuicktestVariant { get; set; }

    [JsonPropertyName("quicktestTimeoutSeconds")]
    public int QuicktestTimeoutSeconds { get; set; }

    [JsonPropertyName("bridgeReadyRequired")]
    public bool BridgeReadyRequired { get; set; }
}

internal sealed class GenerationHistoryRecord
{
    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("observedUtc")]
    public DateTime ObservedUtc { get; set; }

    [JsonPropertyName("acceptedUtc")]
    public DateTime? AcceptedUtc { get; set; }

    [JsonPropertyName("manifestPath")]
    public string ManifestPath { get; set; }

    [JsonPropertyName("terminalFailureCode")]
    public string TerminalFailureCode { get; set; }

    [JsonPropertyName("terminalFailureDetail")]
    public string TerminalFailureDetail { get; set; }

    [JsonPropertyName("testInputs")]
    public List<TestInputValue> TestInputs { get; set; } = new();

    [JsonPropertyName("failureFingerprint")]
    public string FailureFingerprint { get; set; }

    [JsonPropertyName("failureEvidenceId")]
    public string FailureEvidenceId { get; set; }

    [JsonPropertyName("diagnosisReference")]
    public string DiagnosisReference { get; set; }

    [JsonPropertyName("failureSeenBefore")]
    public bool FailureSeenBefore { get; set; }
}

internal sealed class GenerationHistoryEnvelope
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = DevBridgeSchemaVersions.GenerationHistory;

    [JsonPropertyName("contract")]
    public string Contract { get; set; } = DevBridgeSchemaVersions.GenerationHistoryContract;

    [JsonPropertyName("lastKnownGoodGeneration")]
    public int LastKnownGoodGeneration { get; set; }

    [JsonPropertyName("records")]
    public List<GenerationHistoryRecord> Records { get; set; } = new();
}

internal sealed class GenerationHistoryEntry
{
    [JsonPropertyName("record")]
    public GenerationHistoryRecord Record { get; set; }

    [JsonPropertyName("manifest")]
    public GenerationManifest Manifest { get; set; }
}

internal sealed class GenerationProfileComparison
{
    [JsonPropertyName("currentProfileFingerprint")]
    public string CurrentProfileFingerprint { get; set; }

    [JsonPropertyName("lastKnownGoodProfileFingerprint")]
    public string LastKnownGoodProfileFingerprint { get; set; }

    [JsonPropertyName("sameProfile")]
    public bool? SameProfile { get; set; }

    [JsonPropertyName("changedFields")]
    public List<string> ChangedFields { get; set; } = new();
}

internal sealed class GenerationHistoryView
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = DevBridgeSchemaVersions.GenerationHistory;

    [JsonPropertyName("contract")]
    public string Contract { get; set; } = DevBridgeSchemaVersions.GenerationHistoryContract;

    [JsonPropertyName("corrupt")]
    public bool Corrupt { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; set; }

    [JsonPropertyName("currentGeneration")]
    public int CurrentGeneration { get; set; }

    [JsonPropertyName("previousGeneration")]
    public int? PreviousGeneration { get; set; }

    [JsonPropertyName("lastKnownGoodGeneration")]
    public int? LastKnownGoodGeneration { get; set; }

    [JsonPropertyName("current")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationHistoryEntry Current { get; set; }

    [JsonPropertyName("previous")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationHistoryEntry Previous { get; set; }

    [JsonPropertyName("lastKnownGood")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationHistoryEntry LastKnownGood { get; set; }

    [JsonPropertyName("selected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationHistoryEntry Selected { get; set; }

    [JsonPropertyName("profileComparison")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationProfileComparison ProfileComparison { get; set; }

    [JsonPropertyName("records")]
    public List<GenerationHistoryRecord> Records { get; set; } = new();
}

internal sealed partial class CoordinatorState
{
    private const string HistoryReadyStatus = "READY";
    private const string HistoryFailedStatus = "FAILED";
    private const string HistoryStoppedStatus = "STOPPED";

    private bool TryRecordGenerationOutcomeLocked(int generation, string status,
        string terminalFailureCode = null, string terminalFailureDetail = null,
        FailureFingerprintInput failureInput = null)
    {
        if (generation <= 0)
            return true;

        if (!TryLoadGenerationHistoryLocked(out GenerationHistoryEnvelope envelope,
                out string loadErrorCode, out string loadError))
            return false;

        DateTime now = clock.UtcNow;
        List<TestInputValue> semanticInputs = TestGenerationInputs.CloneValues(
            state.RuntimeProfile?.TestInputs ?? state.FrozenTestInputs ?? state.TestInputs);
        GenerationHistoryRecord record = envelope.Records.FirstOrDefault(value =>
            value.Generation == generation);
        bool changed = false;

        if (string.Equals(status, HistoryReadyStatus, StringComparison.Ordinal))
        {
            string manifestPath = Path.Combine(generationsRoot, generation + ".json");
            if (!TryGetOrCreateImmutableManifestLocked(manifestPath, generation, out GenerationManifest manifest))
                return false;

            if (record == null)
            {
                record = new GenerationHistoryRecord
                {
                    Generation = generation,
                    Status = HistoryReadyStatus,
                    ObservedUtc = now,
                    AcceptedUtc = manifest.AcceptedUtc,
                    ManifestPath = RelativeManifestPath(generation),
                    TestInputs = TestGenerationInputs.CloneValues(manifest.Profile?.TestInputs)
                };
                envelope.Records.Add(record);
                changed = true;
            }
            else
            {
                if ((!string.IsNullOrWhiteSpace(record.ManifestPath) &&
                     !string.Equals(record.ManifestPath, RelativeManifestPath(generation),
                        StringComparison.Ordinal)) ||
                    (record.AcceptedUtc.HasValue && record.AcceptedUtc != manifest.AcceptedUtc))
                    return false;
                if (!string.Equals(record.ManifestPath, RelativeManifestPath(generation),
                        StringComparison.Ordinal) || record.AcceptedUtc != manifest.AcceptedUtc)
                {
                    record.ManifestPath = RelativeManifestPath(generation);
                    record.AcceptedUtc = manifest.AcceptedUtc;
                    record.TestInputs = TestGenerationInputs.CloneValues(manifest.Profile?.TestInputs);
                    changed = true;
                }
                if (!string.Equals(record.Status, HistoryReadyStatus, StringComparison.Ordinal))
                {
                    record.Status = HistoryReadyStatus;
                    record.ObservedUtc = now;
                    record.TerminalFailureCode = null;
                    record.TerminalFailureDetail = null;
                    changed = true;
                }
            }

            if (envelope.LastKnownGoodGeneration < generation)
            {
                envelope.LastKnownGoodGeneration = generation;
                changed = true;
            }
        }
        else
        {
            string safeCode = SafeHistoryText(terminalFailureCode);
            string safeDetail = SafeHistoryText(terminalFailureDetail);
            FailureOccurrenceSummary occurrence = null;
            if (!string.IsNullOrWhiteSpace(safeCode) &&
                !string.Equals(safeCode, "PROCESS_STOPPED", StringComparison.Ordinal))
            {
                occurrence = RecordFailureOccurrenceLocked(
                    failureInput ?? BuildFailureInputLocked(safeCode, status, safeDetail),
                    generation, status, safeDetail);
            }
            if (record == null)
            {
                record = new GenerationHistoryRecord
                {
                    Generation = generation,
                    Status = string.IsNullOrWhiteSpace(status) ? HistoryFailedStatus : status,
                    ObservedUtc = now,
                    TerminalFailureCode = safeCode,
                    TerminalFailureDetail = safeDetail,
                    TestInputs = semanticInputs,
                    FailureFingerprint = occurrence?.FailureFingerprint,
                    FailureEvidenceId = occurrence?.EvidenceId,
                    DiagnosisReference = occurrence?.DiagnosisReference,
                    FailureSeenBefore = occurrence?.SeenBefore ?? false
                };
                envelope.Records.Add(record);
                changed = true;
            }
            else
            {
                string effectiveStatus = string.IsNullOrWhiteSpace(status) ? HistoryFailedStatus : status;
                if (!string.Equals(record.Status, effectiveStatus, StringComparison.Ordinal) ||
                    !string.Equals(record.TerminalFailureCode, safeCode, StringComparison.Ordinal) ||
                    !string.Equals(record.TerminalFailureDetail, safeDetail, StringComparison.Ordinal))
                {
                    record.Status = effectiveStatus;
                    record.ObservedUtc = now;
                    record.TerminalFailureCode = safeCode;
                    record.TerminalFailureDetail = safeDetail;
                    record.TestInputs = semanticInputs;
                    if (occurrence != null)
                    {
                        record.FailureFingerprint = occurrence.FailureFingerprint;
                        record.FailureEvidenceId = occurrence.EvidenceId;
                        record.DiagnosisReference = occurrence.DiagnosisReference;
                        record.FailureSeenBefore = occurrence.SeenBefore;
                    }
                    changed = true;
                }
                else if (occurrence != null &&
                    (!string.Equals(record.FailureFingerprint, occurrence.FailureFingerprint, StringComparison.Ordinal) ||
                     !string.Equals(record.FailureEvidenceId, occurrence.EvidenceId, StringComparison.Ordinal)))
                {
                    record.FailureFingerprint = occurrence.FailureFingerprint;
                    record.FailureEvidenceId = occurrence.EvidenceId;
                    record.DiagnosisReference = occurrence.DiagnosisReference;
                    record.FailureSeenBefore = occurrence.SeenBefore;
                    changed = true;
                }
            }
        }

        envelope.Records = envelope.Records.OrderBy(value => value.Generation).ToList();
        if (changed)
            return TryWriteGenerationHistoryLocked(envelope);
        return true;
    }

    private void RefreshLatestFailureReferencesInHistoryLocked(int generation)
    {
        if (generation <= 0 || string.IsNullOrWhiteSpace(state.LatestFailureFingerprint))
            return;
        if (!TryLoadGenerationHistoryLocked(out GenerationHistoryEnvelope envelope,
                out _, out _))
            return;
        GenerationHistoryRecord record = envelope.Records.FirstOrDefault(value =>
            value.Generation == generation);
        if (record == null || !string.Equals(record.FailureFingerprint,
                state.LatestFailureFingerprint, StringComparison.Ordinal))
            return;

        bool changed = !string.Equals(record.FailureEvidenceId, state.LatestFailureEvidenceId,
                StringComparison.Ordinal) ||
            !string.Equals(record.DiagnosisReference, state.LatestFailureDiagnosisReference,
                StringComparison.Ordinal) ||
            record.FailureSeenBefore != state.LatestFailureSeenBefore;
        if (!changed)
            return;
        record.FailureEvidenceId = SafeHistoryText(state.LatestFailureEvidenceId);
        record.DiagnosisReference = SafeHistoryText(state.LatestFailureDiagnosisReference);
        record.FailureSeenBefore = state.LatestFailureSeenBefore;
        TryWriteGenerationHistoryLocked(envelope);
    }

    private GenerationManifest BuildGenerationManifestLocked(int generation, DateTime acceptedUtc)
    {
        PersistedProfileSnapshot profile = state.RuntimeProfile;
        if (profile == null && state.LaunchProfileInstalled && state.LaunchProfileFingerprint != null)
            profile = PersistedProfileSnapshot.FromModProfile(ProfileFromStateLocked());
        profile ??= new PersistedProfileSnapshot
        {
            Mode = state.ProfileMode,
            RequestedProjects = SafeHistoryList(state.FrozenRequestedProjects ?? state.RequestedProjects),
            ResolvedProjectPackageIds = SafeHistoryList(state.FrozenResolvedProjectPackageIds ?? state.ResolvedProjectPackageIds),
            ResolvedMods = SafeHistoryList(state.FrozenResolvedMods ?? state.ResolvedMods),
            TestInputs = TestGenerationInputs.CloneValues(state.FrozenTestInputs ?? state.TestInputs),
            ProfileFingerprint = state.FrozenProfileFingerprint ?? state.ProfileFingerprint,
            BaselineFingerprint = state.FrozenBaselineFingerprint ?? state.BaselineFingerprint,
            RimBridgeMode = state.RimBridgeMode,
            RimBridgeVersion = state.RimBridgeVersion
        };

        List<GenerationRegistrationEvidence> registrations = (state.FrozenRegistrations ?? new List<ProjectIntentSnapshot>())
            .OrderBy(value => value.Id ?? string.Empty, StringComparer.Ordinal)
            .Select(value => new GenerationRegistrationEvidence
            {
                Id = SafeHistoryText(value.Id),
                Owner = SafeHistoryText(value.Owner),
                SessionId = SafeHistoryText(value.SessionId),
                RequestedProjects = SafeHistoryList(value.RequestedProjects)
            }).ToList();
        List<string> resolvedMods = (profile.ResolvedMods ?? new List<string>()).ToList();
        string modsFingerprint = null;
        try
        {
            if (File.Exists(modsConfigPath))
                modsFingerprint = HashBytes(File.ReadAllBytes(modsConfigPath));
        }
        catch (IOException)
        {
            modsFingerprint = state.ModsConfigGeneratedHash;
        }
        catch (UnauthorizedAccessException)
        {
            modsFingerprint = state.ModsConfigGeneratedHash;
        }

        return new GenerationManifest
        {
            Generation = generation,
            AcceptedUtc = acceptedUtc,
            Launch = new GenerationLaunchEvidence
            {
                LaunchId = SafeHistoryText(state.LaunchId),
                LaunchGeneration = state.LaunchGeneration,
                LaunchStartedUtc = state.LaunchStartedUtc,
                ProfileInstalled = state.LaunchProfileInstalled,
                AttemptCount = state.LaunchAttemptCount,
                RequestKeyPresent = !string.IsNullOrWhiteSpace(state.LaunchRequestKey)
            },
            Profile = new GenerationProfileEvidence
            {
                Mode = SafeHistoryText(profile.Mode),
                LaunchMode = SafeHistoryText(state.LaunchProfileMode),
                RequestedProjects = SafeHistoryList(profile.RequestedProjects),
                ResolvedProjectPackageIds = SafeHistoryList(profile.ResolvedProjectPackageIds),
                ResolvedMods = SafeHistoryList(resolvedMods),
                TestInputs = TestGenerationInputs.CloneValues(profile.TestInputs),
                ProfileFingerprint = SafeHistoryText(profile.ProfileFingerprint ?? state.FrozenProfileFingerprint ?? state.ProfileFingerprint),
                BaselineFingerprint = SafeHistoryText(profile.BaselineFingerprint ?? state.FrozenBaselineFingerprint ?? state.BaselineFingerprint),
                RimBridgeMode = RimBridgeModes.Text(profile.RimBridgeMode),
                RimBridgeVersion = SafeHistoryText(profile.RimBridgeVersion),
                Registrations = registrations
            },
            ModsConfig = new GenerationModsConfigEvidence
            {
                Fingerprint = modsFingerprint,
                BaselineFingerprint = SafeHistoryText(state.BaselineFingerprint),
                Ownership = SafeHistoryText(state.ModsConfigOwnership),
                ResolvedModOrder = SafeHistoryList(resolvedMods)
            },
            Process = new GenerationProcessEvidence
            {
                ProcessId = state.ProcessId,
                ProcessStartUtcTicks = state.ProcessStartUtcTicks,
                ExecutableName = SafeHistoryText(Path.GetFileName(rimWorldExe))
            },
            Readiness = new GenerationReadinessEvidence
            {
                Result = HistoryReadyStatus,
                ObservedUtc = acceptedUtc,
                Generation = generation,
                LaunchId = SafeHistoryText(state.LaunchId),
                ProcessId = state.ProcessId,
                QuicktestRequired = TestGenerationInputs.IsQuicktestEnabled(profile.TestInputs),
                QuicktestVariant = TestGenerationInputs.FromValues(profile.TestInputs, profile.Mode).QuicktestVariant,
                QuicktestTimeoutSeconds = TestGenerationInputs.FromValues(profile.TestInputs, profile.Mode).QuicktestTimeoutSeconds,
                BridgeReadyRequired = profile.RimBridgeMode == RimBridgeMode.Required
            },
            Components = ComponentVersions.Current,
            Failure = state.LatestFailureGeneration == generation &&
                !string.IsNullOrWhiteSpace(state.LatestFailureFingerprint)
                ? new GenerationFailureEvidence
                {
                    FailureFingerprint = SafeHistoryText(state.LatestFailureFingerprint),
                    SeenBefore = state.LatestFailureSeenBefore,
                    EvidenceId = SafeHistoryText(state.LatestFailureEvidenceId),
                    DiagnosisReference = SafeHistoryText(state.LatestFailureDiagnosisReference),
                    Summary = SafeHistoryText(state.LatestFailureSummary)
                } : null,
            Companion = new GenerationCompanionEvidence
            {
                LifecycleState = SafeHistoryText(state.RimBridge?.LifecycleState.ToString()),
                Available = state.RimBridge != null && state.RimBridge.LifecycleState != RimBridgeLifecycleState.DISABLED,
                Verified = state.RimBridge?.CompanionVerified == true,
                DiagnosticCode = SafeHistoryText(state.RimBridge == null ? null : RimBridgeCompanionDiagnostics.Code(state.RimBridge))
            },
            RecipeContext = !string.IsNullOrWhiteSpace(state.LatestFailureRecipeId) &&
                state.LatestFailureGeneration == generation
                ? new GenerationRecipeContextEvidence
                {
                    RecipeId = SafeHistoryText(state.LatestFailureRecipeId),
                    ReproductionContextFingerprint = SafeHistoryText(state.LatestFailureContextFingerprint),
                    ProjectFingerprint = SafeHistoryText(profile.ProfileFingerprint)
                } : null
        };
    }

    private bool TryGetOrCreateImmutableManifestLocked(string path, int generation,
        out GenerationManifest manifest)
    {
        manifest = null;
        try
        {
            Directory.CreateDirectory(generationsRoot);
            if (File.Exists(path))
            {
                string serialized = File.ReadAllText(path);
                RequireSchemaContract(serialized, DevBridgeSchemaVersions.GenerationManifest,
                    DevBridgeSchemaVersions.GenerationManifestContract, requireRecords: false);
                manifest = JsonSerializer.Deserialize<GenerationManifest>(serialized, CoordinatorSerialization.JsonOptions);
                if (!IsValidGenerationManifest(manifest, generation))
                    return false;
                return true;
            }
            manifest = BuildGenerationManifestLocked(generation, clock.UtcNow);
            long manifestWriteStarted = Stopwatch.GetTimestamp();
            TraceEvent("history.manifest.write.started");
            try
            {
                InjectFaultForTesting(CoordinatorFaultPoint.DuringHistoryManifestPersistence);
                AtomicCreateGenerationFile(path,
                    JsonSerializer.Serialize(manifest, CoordinatorSerialization.JsonOptions),
                    beforeReplacement: () => InjectFaultForTesting(
                        CoordinatorFaultPoint.AfterHistoryTempFileWriteBeforeAtomicReplacement),
                    afterReplacement: () => InjectFaultForTesting(
                        CoordinatorFaultPoint.AfterHistoryDurableReplacement));
                TraceEvent("history.manifest.write.completed",
                    durationMs: ElapsedMilliseconds(manifestWriteStarted), success: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another coordinator instance may have won the create race.
                // Read its immutable value; never replace an accepted manifest.
                TraceEvent("history.manifest.write.completed",
                    durationMs: ElapsedMilliseconds(manifestWriteStarted), success: true,
                    detail: "existing-manifest-won-race");
                string serialized = File.ReadAllText(path);
                RequireSchemaContract(serialized, DevBridgeSchemaVersions.GenerationManifest,
                    DevBridgeSchemaVersions.GenerationManifestContract, requireRecords: false);
                manifest = JsonSerializer.Deserialize<GenerationManifest>(serialized, CoordinatorSerialization.JsonOptions);
                if (!IsValidGenerationManifest(manifest, generation))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                           exception is JsonException || exception is InvalidDataException ||
                                           exception is NotSupportedException)
        {
            TraceEvent("history.manifest.write.failed", success: false,
                errorCode: TraceExceptionCategory(exception));
            return false;
        }
    }

    private static bool IsValidGenerationManifest(GenerationManifest manifest, int generation)
    {
        if (manifest == null || manifest.SchemaVersion != DevBridgeSchemaVersions.GenerationManifest ||
            !string.Equals(manifest.Contract, DevBridgeSchemaVersions.GenerationManifestContract,
                StringComparison.Ordinal) || manifest.Generation != generation ||
            manifest.AcceptedUtc == default || manifest.Launch == null || manifest.Profile == null ||
            manifest.Profile.RequestedProjects == null || manifest.Profile.ResolvedProjectPackageIds == null ||
            manifest.Profile.ResolvedMods == null || manifest.Profile.Registrations == null ||
            manifest.ModsConfig == null || manifest.ModsConfig.ResolvedModOrder == null ||
            manifest.Process == null || manifest.Readiness == null ||
            manifest.Readiness.Generation != generation ||
            manifest.Readiness.ProcessId != manifest.Process.ProcessId ||
            !string.Equals(manifest.Readiness.LaunchId, manifest.Launch.LaunchId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Readiness.Result, HistoryReadyStatus, StringComparison.Ordinal) ||
            manifest.Components == null)
            return false;

        try
        {
            TestInputSet inputs = TestGenerationInputs.FromValues(manifest.Profile.TestInputs,
                manifest.Profile.Mode);
            return manifest.Readiness.QuicktestRequired == inputs.QuicktestEnabled &&
                string.Equals(manifest.Readiness.QuicktestVariant, inputs.QuicktestVariant,
                    StringComparison.Ordinal) &&
                manifest.Readiness.QuicktestTimeoutSeconds == inputs.QuicktestTimeoutSeconds;
        }
        catch (ProfileException)
        {
            return false;
        }
    }

    private bool TryLoadGenerationHistoryLocked(out GenerationHistoryEnvelope envelope,
        out string errorCode, out string error)
    {
        envelope = null;
        errorCode = null;
        error = null;
        try
        {
            if (!File.Exists(generationHistoryPath))
            {
                envelope = new GenerationHistoryEnvelope();
                return true;
            }

            string serialized = File.ReadAllText(generationHistoryPath);
            RequireSchemaContract(serialized, DevBridgeSchemaVersions.GenerationHistory,
                DevBridgeSchemaVersions.GenerationHistoryContract, requireRecords: true);
            envelope = JsonSerializer.Deserialize<GenerationHistoryEnvelope>(serialized, CoordinatorSerialization.JsonOptions);
            if (envelope == null || envelope.SchemaVersion != DevBridgeSchemaVersions.GenerationHistory ||
                !string.Equals(envelope.Contract, DevBridgeSchemaVersions.GenerationHistoryContract,
                    StringComparison.Ordinal))
                throw new InvalidDataException("generation history schema is unsupported");
            envelope.Records ??= new List<GenerationHistoryRecord>();
            if (envelope.Records.Any(value => value == null || value.Generation <= 0) ||
                envelope.Records.GroupBy(value => value.Generation).Any(group => group.Count() != 1))
                throw new InvalidDataException("generation history contains duplicate or invalid records");
            foreach (GenerationHistoryRecord record in envelope.Records)
            {
                if (!string.IsNullOrWhiteSpace(record.ManifestPath) &&
                    !string.Equals(record.ManifestPath, RelativeManifestPath(record.Generation),
                        StringComparison.Ordinal))
                    throw new InvalidDataException("generation history contains an invalid manifest path");
                if ((string.Equals(record.Status, HistoryReadyStatus, StringComparison.Ordinal) ||
                     !string.IsNullOrWhiteSpace(record.ManifestPath)) &&
                    (string.IsNullOrWhiteSpace(record.ManifestPath) || !record.AcceptedUtc.HasValue))
                    throw new InvalidDataException("generation history contains an incomplete accepted record");
            }
            int lastKnownGoodGeneration = envelope.LastKnownGoodGeneration;
            if (lastKnownGoodGeneration < 0 ||
                (lastKnownGoodGeneration > 0 &&
                 !envelope.Records.Any(value => value.Generation == lastKnownGoodGeneration &&
                     !string.IsNullOrWhiteSpace(value.ManifestPath))))
                throw new InvalidDataException("generation history has an invalid last-known-good pointer");
            return true;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                           exception is JsonException || exception is InvalidDataException ||
                                           exception is NotSupportedException)
        {
            errorCode = "GENERATION_HISTORY_CORRUPT";
            error = "Runtime/generation-history.json is malformed, unsupported, or inconsistent; it was not rewritten.";
            return false;
        }
    }

    private bool TryWriteGenerationHistoryLocked(GenerationHistoryEnvelope envelope)
    {
        long started = Stopwatch.GetTimestamp();
        TraceEvent("history.write.started");
        try
        {
            InjectFaultForTesting(CoordinatorFaultPoint.DuringHistoryManifestPersistence);
            AtomicWriteGenerationFile(generationHistoryPath,
                JsonSerializer.Serialize(envelope, CoordinatorSerialization.JsonOptions),
                beforeReplacement: () => InjectFaultForTesting(
                    CoordinatorFaultPoint.AfterHistoryTempFileWriteBeforeAtomicReplacement),
                afterReplacement: () => InjectFaultForTesting(
                    CoordinatorFaultPoint.AfterHistoryDurableReplacement));
            TraceEvent("history.write.completed", durationMs: ElapsedMilliseconds(started),
                success: true);
            return true;
        }
        catch (IOException exception)
        {
            TraceEvent("history.write.failed", durationMs: ElapsedMilliseconds(started),
                success: false, errorCode: TraceExceptionCategory(exception));
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            TraceEvent("history.write.failed", durationMs: ElapsedMilliseconds(started),
                success: false, errorCode: TraceExceptionCategory(exception));
            return false;
        }
    }

    private GenerationHistoryView BuildGenerationHistoryViewLocked(int currentGeneration,
        int? selectedGeneration = null)
    {
        GenerationHistoryView view = new() { CurrentGeneration = currentGeneration };
        if (!TryLoadGenerationHistoryLocked(out GenerationHistoryEnvelope envelope,
                out string errorCode, out string error))
        {
            view.Corrupt = true;
            view.ErrorCode = errorCode;
            view.Error = error;
            return view;
        }

        List<GenerationHistoryRecord> records = envelope.Records.OrderBy(value => value.Generation).ToList();
        view.Records = records;
        List<int> acceptedGenerations = records
            .Where(value => !string.IsNullOrWhiteSpace(value.ManifestPath))
            .Select(value => value.Generation)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        int acceptedCurrent = acceptedGenerations.LastOrDefault();
        view.CurrentGeneration = acceptedCurrent;
        int? previous = acceptedGenerations.Count > 1
            ? acceptedGenerations[^2]
            : null;
        view.PreviousGeneration = previous;
        view.LastKnownGoodGeneration = envelope.LastKnownGoodGeneration > 0
            ? envelope.LastKnownGoodGeneration : null;
        if (acceptedCurrent > 0)
            view.Current = ReadHistoryEntryLocked(records.FirstOrDefault(value => value.Generation == acceptedCurrent), view);
        if (previous.HasValue)
            view.Previous = ReadHistoryEntryLocked(records.FirstOrDefault(value => value.Generation == previous.Value), view);
        if (view.LastKnownGoodGeneration.HasValue)
            view.LastKnownGood = ReadHistoryEntryLocked(records.FirstOrDefault(value =>
                value.Generation == view.LastKnownGoodGeneration.Value), view);
        if (view.Corrupt)
            return view;

        view.ProfileComparison = BuildProfileComparison(view.Current?.Manifest, view.LastKnownGood?.Manifest);
        if (selectedGeneration.HasValue)
        {
            GenerationHistoryRecord selected = records.FirstOrDefault(value =>
                value.Generation == selectedGeneration.Value);
            view.Selected = ReadHistoryEntryLocked(selected, view);
        }
        return view;
    }

    private GenerationHistoryEntry ReadHistoryEntryLocked(GenerationHistoryRecord record,
        GenerationHistoryView view)
    {
        if (record == null)
            return null;
        GenerationHistoryEntry entry = new() { Record = record };
        if (string.IsNullOrWhiteSpace(record.ManifestPath))
            return entry;
        string path = Path.Combine(generationsRoot, record.Generation + ".json");
        try
        {
            string serialized = File.ReadAllText(path);
            RequireSchemaContract(serialized, DevBridgeSchemaVersions.GenerationManifest,
                DevBridgeSchemaVersions.GenerationManifestContract, requireRecords: false);
            GenerationManifest manifest = JsonSerializer.Deserialize<GenerationManifest>(serialized, CoordinatorSerialization.JsonOptions);
            if (manifest == null || manifest.Generation != record.Generation ||
                manifest.SchemaVersion != DevBridgeSchemaVersions.GenerationManifest ||
                !string.Equals(manifest.Contract, DevBridgeSchemaVersions.GenerationManifestContract,
                    StringComparison.Ordinal))
                throw new InvalidDataException("generation manifest is inconsistent");
            entry.Manifest = manifest;
            return entry;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                           exception is JsonException || exception is InvalidDataException ||
                                           exception is NotSupportedException)
        {
            view.Corrupt = true;
            view.ErrorCode = "GENERATION_MANIFEST_CORRUPT";
            view.Error = "An accepted generation manifest is missing, malformed, or inconsistent; history was not rewritten.";
            return entry;
        }
    }

    private static GenerationProfileComparison BuildProfileComparison(GenerationManifest current,
        GenerationManifest lastKnownGood)
    {
        if (current?.Profile == null || lastKnownGood?.Profile == null)
            return null;
        List<string> changed = new();
        if (!string.Equals(current.Profile.Mode, lastKnownGood.Profile.Mode, StringComparison.Ordinal)) changed.Add("mode");
        if (!string.Equals(current.Profile.LaunchMode, lastKnownGood.Profile.LaunchMode, StringComparison.Ordinal)) changed.Add("launchMode");
        if (!(current.Profile.RequestedProjects ?? new List<string>()).SequenceEqual(
                lastKnownGood.Profile.RequestedProjects ?? new List<string>(), StringComparer.Ordinal)) changed.Add("requestedProjects");
        if (!(current.Profile.ResolvedProjectPackageIds ?? new List<string>()).SequenceEqual(
                lastKnownGood.Profile.ResolvedProjectPackageIds ?? new List<string>(), StringComparer.Ordinal)) changed.Add("resolvedProjectPackageIds");
        if (!(current.Profile.ResolvedMods ?? new List<string>()).SequenceEqual(
                lastKnownGood.Profile.ResolvedMods ?? new List<string>(), StringComparer.Ordinal)) changed.Add("resolvedMods");
        if (!string.Equals(current.Profile.BaselineFingerprint, lastKnownGood.Profile.BaselineFingerprint, StringComparison.Ordinal)) changed.Add("baselineFingerprint");
        if (!string.Equals(current.Profile.RimBridgeMode, lastKnownGood.Profile.RimBridgeMode, StringComparison.Ordinal)) changed.Add("rimBridgeMode");
        return new GenerationProfileComparison
        {
            CurrentProfileFingerprint = current.Profile.ProfileFingerprint,
            LastKnownGoodProfileFingerprint = lastKnownGood.Profile.ProfileFingerprint,
            SameProfile = changed.Count == 0,
            ChangedFields = changed
        };
    }

    private static string RelativeManifestPath(int generation) => "generations/" + generation + ".json";

    private static void RequireSchemaContract(string serialized, int schemaVersion,
        string contract, bool requireRecords)
    {
        using JsonDocument document = JsonDocument.Parse(serialized);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out int actualSchemaVersion) ||
            actualSchemaVersion != schemaVersion ||
            !document.RootElement.TryGetProperty("contract", out JsonElement actualContract) ||
            actualContract.ValueKind != JsonValueKind.String ||
            !string.Equals(actualContract.GetString(), contract, StringComparison.Ordinal) ||
            (requireRecords &&
             (!document.RootElement.TryGetProperty("records", out JsonElement records) ||
              records.ValueKind != JsonValueKind.Array)))
            throw new InvalidDataException("generation history artifact is missing its required schema contract");
    }

    private static string SafeHistoryText(string value)
    {
        string safe = DiagnosticRedactor.Text(value);
        if (string.IsNullOrWhiteSpace(safe))
            return safe;
        return safe.Length <= 512 ? safe : safe.Substring(0, 512);
    }

    private static List<string> SafeHistoryList(IEnumerable<string> values) =>
        (values ?? Enumerable.Empty<string>()).Select(SafeHistoryText).ToList();

    private void AtomicWriteGenerationFile(string path, string contents,
        Action beforeReplacement = null, Action afterReplacement = null)
    {
        string directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(true);
            }
            beforeReplacement?.Invoke();
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporary, path, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporary, path, true);
                }
                catch (IOException)
                {
                    File.Move(temporary, path, true);
                }
            }
            else
                File.Move(temporary, path);
            afterReplacement?.Invoke();
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    private void AtomicCreateGenerationFile(string path, string contents,
        Action beforeReplacement = null, Action afterReplacement = null)
    {
        string directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(true);
            }
            // Deliberately do not use overwrite semantics here. A manifest is
            // an accepted-generation boundary and is write-once by contract.
            beforeReplacement?.Invoke();
            File.Move(temporary, path);
            afterReplacement?.Invoke();
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }
}
