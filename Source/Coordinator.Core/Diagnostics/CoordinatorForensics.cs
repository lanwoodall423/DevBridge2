using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevBridge2;

namespace DevBridge.Coordinator;

internal sealed partial class CoordinatorState
{
    internal bool IsForensicCommand(BridgeRequest request) =>
        string.Equals(request?.Command, "logs", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(request?.Command, "evidence", StringComparison.OrdinalIgnoreCase);

    private int Logs(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        LogsQueryResponse response = BuildLogsQuery(arguments, request);
        request.LogsQueryResponse = response;
        if (!response.Success)
        {
            emit(response.ErrorCode + ": " + response.Error);
            return response.ExitCode == 0 ? 4 : response.ExitCode;
        }
        emit("Semantic Player.log records: " + response.Records.Count +
            " (rawBytes=" + response.RawBytes + ", semanticBytes=" + response.SemanticBytes + ").");
        if (response.Truncated)
            emit("Player.log evidence was bounded; additional records were omitted.");
        foreach (SemanticLogRecord record in response.Records)
            emit(record.Severity + " [" + record.Component + "] " + record.Message);
        if (response.Trace != null)
            emit("Coordinator trace records: " + response.Trace.Count + ".");
        return 0;
    }

    private int Evidence(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        EvidenceShowResponse response = BuildEvidenceShow(arguments);
        request.EvidenceShowResponse = response;
        if (!response.Success)
        {
            emit(response.ErrorCode + ": " + response.Error);
            return response.ExitCode == 0 ? 4 : response.ExitCode;
        }
        emit("Evidence " + response.Evidence.EvidenceId + " loaded for failure " +
            response.Evidence.FailureFingerprint + ".");
        emit("Summary: " + response.Evidence.Summary);
        emit("Generation: " + response.Evidence.Generation + "; occurrence=" +
            response.Evidence.OccurrenceCount);
        return 0;
    }

    private LogsQueryResponse BuildLogsQuery(IReadOnlyList<string> arguments, BridgeRequest request)
    {
        if (arguments == null || arguments.Count == 0 ||
            !string.Equals(arguments[0], "query", StringComparison.OrdinalIgnoreCase))
            return LogsFailure(2, "LOGS_QUERY_USAGE",
                "Usage: DevBridge.cmd logs query [--generation <n>] [--since-launch] [--severity <level>] [--fingerprint <id>] [--component <name>] [--limit <n>] [--trace] --json");

        int? generation = null;
        int limit = FailureEvidenceLimits.MaxSemanticRecords;
        bool sinceLaunch = false;
        bool trace = false;
        string severity = null;
        string fingerprint = null;
        string component = null;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < arguments.Count; index++)
        {
            string option = arguments[index]?.Trim() ?? string.Empty;
            if (string.Equals(option, "--json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (option is "--since-launch" or "--trace")
            {
                if (!seen.Add(option))
                    return LogsFailure(2, "LOGS_QUERY_USAGE", "A logs query flag may be declared only once.");
                if (option == "--since-launch")
                    sinceLaunch = true;
                else
                    trace = true;
                continue;
            }
            if (option is "--generation" or "--severity" or "--fingerprint" or "--component" or "--limit")
            {
                if (!seen.Add(option) || ++index >= arguments.Count)
                    return LogsFailure(2, "LOGS_QUERY_USAGE", "Each logs query option requires one bounded value.");
                string value = arguments[index]?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                    return LogsFailure(2, "LOGS_QUERY_USAGE", "Each logs query option requires one bounded value.");
                switch (option)
                {
                    case "--generation":
                        if (!int.TryParse(value, out int parsedGeneration) || parsedGeneration < 1)
                            return LogsFailure(2, "LOGS_QUERY_USAGE", "generation must be a positive integer.");
                        generation = parsedGeneration;
                        break;
                    case "--limit":
                        if (!int.TryParse(value, out int parsedLimit) || parsedLimit < 1 ||
                            parsedLimit > FailureEvidenceLimits.MaxSemanticRecords)
                            return LogsFailure(2, "LOGS_QUERY_USAGE", "limit must be between 1 and 64.");
                        limit = parsedLimit;
                        break;
                    case "--severity":
                        severity = value.ToUpperInvariant();
                        if (severity is not ("ERROR" or "WARN" or "INFO" or "DEBUG"))
                            return LogsFailure(2, "LOGS_QUERY_USAGE", "severity must be ERROR, WARN, INFO, or DEBUG.");
                        break;
                    case "--fingerprint":
                        fingerprint = FailureFingerprinting.Bound(value);
                        break;
                    case "--component":
                        component = FailureFingerprinting.NormalizeToken(value);
                        break;
                }
                continue;
            }
            return LogsFailure(2, "LOGS_QUERY_USAGE", "Unknown logs query option.");
        }

        PersistedState snapshot;
        lock (gate)
            snapshot = CloneStateLocked();
        int currentGeneration = snapshot.RimBridge?.Generation > 0
            ? snapshot.RimBridge.Generation : snapshot.Generation;
        if (generation.HasValue && generation.Value != currentGeneration)
        {
            return new LogsQueryResponse
            {
                ExitCode = 0,
                Success = true,
                Generation = generation.Value,
                SinceLaunch = sinceLaunch,
                Available = false,
                Records = new List<SemanticLogRecord>(),
                RawBytes = 0,
                SemanticBytes = 0
            };
        }

        LogSegment segment = ReadLaunchBoundedLog(snapshot);
        if (!segment.Available)
        {
            return new LogsQueryResponse
            {
                ExitCode = 0,
                Success = true,
                Generation = currentGeneration,
                SinceLaunch = sinceLaunch,
                Available = false,
                ErrorCode = segment.ErrorCode,
                Error = segment.Error,
                Records = new List<SemanticLogRecord>()
            };
        }

        SemanticLogParseResult parsed = SemanticLogParser.Parse(segment.Text, currentGeneration,
            FailureEvidenceLimits.MaxSemanticRecords);
        List<SemanticLogRecord> records = parsed.Records.Where(value =>
            (!sinceLaunch || value.SinceLaunch) &&
            (severity == null || string.Equals(value.Severity, severity, StringComparison.Ordinal)) &&
            (fingerprint == null || string.Equals(value.Fingerprint, fingerprint, StringComparison.Ordinal)) &&
            (component == null || string.Equals(value.Component, component, StringComparison.Ordinal))).Take(limit).ToList();
        // Report the compact semantic payload, not the JSON response envelope.
        // This makes the raw-vs-semantic reduction measurable even when a
        // small query's response metadata is larger than its source log.
        int semanticBytes = records.Sum(record => Encoding.UTF8.GetByteCount(string.Join("|",
            record.Severity ?? string.Empty,
            record.Component ?? string.Empty,
            record.Fingerprint ?? string.Empty,
            record.Message ?? string.Empty,
            record.StackFrames == null ? string.Empty : string.Join("|", record.StackFrames))));
        return new LogsQueryResponse
        {
            ExitCode = 0,
            Success = true,
            Generation = currentGeneration,
            SinceLaunch = sinceLaunch,
            Available = true,
            RawBytes = segment.RawBytes,
            SemanticBytes = semanticBytes,
            Truncated = segment.Truncated || parsed.Truncated || parsed.Records.Count > records.Count,
            Records = records,
            Trace = trace ? CoordinatorTraceReader.Read(runtimeRoot) : null
        };
    }

    private EvidenceShowResponse BuildEvidenceShow(IReadOnlyList<string> arguments)
    {
        if (arguments == null || arguments.Count < 2 ||
            !string.Equals(arguments[0], "show", StringComparison.OrdinalIgnoreCase))
            return EvidenceFailure(2, "EVIDENCE_USAGE",
                "Usage: DevBridge.cmd evidence show <id> --json");
        string id = arguments[1];
        if (arguments.Skip(2).Any(value => !string.Equals(value, "--json", StringComparison.OrdinalIgnoreCase)))
            return EvidenceFailure(2, "EVIDENCE_USAGE", "evidence show accepts only one evidence ID and --json.");
        EvidenceLookupResult lookup = new FailureEvidenceStore(runtimeRoot, () => clock.UtcNow).Read(id);
        if (!lookup.Found)
            return EvidenceFailure(4, lookup.ErrorCode, lookup.Error);
        return new EvidenceShowResponse
        {
            ExitCode = 0,
            Success = true,
            Evidence = lookup.Record
        };
    }

    private static LogsQueryResponse LogsFailure(int exitCode, string code, string error) => new()
    {
        ExitCode = exitCode,
        Success = false,
        ErrorCode = code,
        Error = error,
        Records = new List<SemanticLogRecord>()
    };

    private static EvidenceShowResponse EvidenceFailure(int exitCode, string code, string error) => new()
    {
        ExitCode = exitCode,
        Success = false,
        ErrorCode = code,
        Error = error
    };

    private LogSegment ReadLaunchBoundedLog(PersistedState snapshot)
    {
        string path = rimBridgeLogPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new LogSegment { ErrorCode = "PLAYER_LOG_UNAVAILABLE", Error = "Player.log is not available." };
        RimBridgeIntegrationState bridge = snapshot.RimBridge ?? new RimBridgeIntegrationState();
        try
        {
            FileInfo info = new(path);
            long offset = Math.Max(0, bridge.LogBoundaryPosition);
            if (!bridge.LogBoundaryAuthoritative)
                return new LogSegment
                {
                    ErrorCode = RimBridgeIntegrationConstants.PlayerLogBoundaryInvalidCode,
                    Error = "Player.log has no authoritative post-startup boundary; stale output was excluded."
                };
            if (!bridge.LogExistedAtBoundary)
                return new LogSegment
                {
                    ErrorCode = RimBridgeIntegrationConstants.PlayerLogBoundaryInvalidCode,
                    Error = "Player.log was not present when the authoritative boundary was captured."
                };
            if (info.Length < offset)
                return new LogSegment
                {
                    ErrorCode = RimBridgeIntegrationConstants.PlayerLogBoundaryInvalidCode,
                    Error = "Player.log was shortened after the launch boundary."
                };
            if (bridge.LogBoundaryCreationUtcTicks > 0 && info.CreationTimeUtc.Ticks > 0 &&
                info.CreationTimeUtc.Ticks != bridge.LogBoundaryCreationUtcTicks)
                return new LogSegment
                {
                    ErrorCode = RimBridgeIntegrationConstants.PlayerLogBoundaryInvalidCode,
                    Error = "Player.log was replaced after the launch boundary; stale output was excluded."
                };
            long prefixLength = bridge.LogBoundaryPrefixLength > 0
                ? bridge.LogBoundaryPrefixLength
                : offset;
            if (prefixLength > info.Length)
                return new LogSegment
                {
                    ErrorCode = RimBridgeIntegrationConstants.PlayerLogBoundaryInvalidCode,
                    Error = "Player.log was shortened after the launch boundary."
                };
            if (prefixLength > 0 && string.IsNullOrWhiteSpace(bridge.LogBoundaryPrefixHash))
                return new LogSegment
                {
                    ErrorCode = RimBridgeIntegrationConstants.PlayerLogBoundaryInvalidCode,
                    Error = "Player.log boundary integrity metadata is incomplete."
                };
            if (prefixLength > 0 && !string.Equals(ReadPrefixHash(path, prefixLength), bridge.LogBoundaryPrefixHash,
                    StringComparison.Ordinal))
                return new LogSegment
                {
                    ErrorCode = RimBridgeIntegrationConstants.PlayerLogBoundaryInvalidCode,
                    Error = "Player.log changed after the launch boundary; stale output was excluded."
                };
            if (offset > info.Length)
                offset = info.Length;
            int count = (int)Math.Min(FailureEvidenceLimits.MaxRawLogBytes, info.Length - offset);
            byte[] bytes = new byte[count];
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < bytes.Length)
            {
                int received = stream.Read(bytes, read, bytes.Length - read);
                if (received <= 0)
                    break;
                read += received;
            }
            return new LogSegment
            {
                Available = true,
                Text = Encoding.UTF8.GetString(bytes, 0, read),
                RawBytes = read,
                Truncated = info.Length - offset > FailureEvidenceLimits.MaxRawLogBytes
            };
        }
        catch (IOException)
        {
            return new LogSegment { ErrorCode = "PLAYER_LOG_UNAVAILABLE", Error = "Player.log could not be read." };
        }
        catch (UnauthorizedAccessException)
        {
            return new LogSegment { ErrorCode = "PLAYER_LOG_UNAVAILABLE", Error = "Player.log access was denied." };
        }
    }

    private static string ReadPrefixHash(string path, long length)
    {
        long bounded = Math.Min(Math.Max(0, length), 64 * 1024);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[(int)bounded];
        int read = 0;
        while (read < bytes.Length)
        {
            int received = stream.Read(bytes, read, bytes.Length - read);
            if (received <= 0)
                break;
            read += received;
        }
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, read)));
    }

    private sealed class LogSegment
    {
        internal bool Available { get; init; }
        internal string Text { get; init; }
        internal int RawBytes { get; init; }
        internal bool Truncated { get; init; }
        internal string ErrorCode { get; init; }
        internal string Error { get; init; }
    }

    internal FailureOccurrenceSummary RecordFailureOccurrenceLocked(FailureFingerprintInput input,
        int generation, string phase, string detail)
    {
        NormalizedFailureFingerprint normalized = FailureFingerprinting.Create(input);
        state.FailureOccurrences ??= new List<FailureOccurrenceSummary>();
        FailureOccurrenceSummary previous = state.FailureOccurrences.FirstOrDefault(value =>
            string.Equals(value?.FailureFingerprint, normalized.FailureFingerprint, StringComparison.Ordinal));
        DateTime now = clock.UtcNow.ToUniversalTime();
        FailureOccurrenceSummary occurrence = previous ?? new FailureOccurrenceSummary
        {
            FailureFingerprint = normalized.FailureFingerprint,
            FirstSeenGeneration = Math.Max(0, generation),
            FirstSeenUtc = now,
            OccurrenceCount = 0
        };
        occurrence.SeenBefore = previous != null;
        occurrence.LastSeenGeneration = Math.Max(0, generation);
        occurrence.LastSeenUtc = now;
        occurrence.OccurrenceCount = Math.Min(int.MaxValue, occurrence.OccurrenceCount + 1);
        occurrence.Summary = normalized.Summary;
        occurrence.ErrorCode = normalized.ErrorCode;
        occurrence.Phase = FailureFingerprinting.Bound(phase ?? normalized.Phase);
        occurrence.Component = normalized.Component;
        occurrence.RecipeId = normalized.RecipeId;
        occurrence.ProjectFingerprint = normalized.ProjectFingerprint;
        occurrence.ReproductionContextFingerprint = normalized.ReproductionContextFingerprint;
        occurrence.DiagnosisReference = FailureFingerprinting.Bound(
            input is null ? null : CurrentDiagnosisReferenceLocked());

        FailureEvidenceRecord evidence = new()
        {
            Generation = Math.Max(0, generation),
            FailureFingerprint = normalized.FailureFingerprint,
            SeenBefore = occurrence.SeenBefore,
            OccurrenceCount = occurrence.OccurrenceCount,
            Summary = normalized.Summary,
            ErrorCode = normalized.ErrorCode,
            Phase = normalized.Phase,
            ExceptionType = normalized.ExceptionType,
            Component = normalized.Component,
            RecipeId = normalized.RecipeId,
            ProjectFingerprint = normalized.ProjectFingerprint,
            ReproductionContextFingerprint = normalized.ReproductionContextFingerprint,
            DiagnosisReference = occurrence.DiagnosisReference,
            Detail = FailureFingerprinting.Bound(detail ?? input?.Detail),
            StackFrames = normalized.StackFrames
        };
        occurrence.EvidenceId = new FailureEvidenceStore(runtimeRoot, () => clock.UtcNow).Write(evidence);
        if (previous == null)
            state.FailureOccurrences.Add(occurrence);
        state.FailureOccurrences = state.FailureOccurrences
            .Where(value => value != null)
            .OrderByDescending(value => value.LastSeenUtc)
            .ThenByDescending(value => value.LastSeenGeneration)
            .Take(FailureEvidenceLimits.MaxOccurrences)
            .ToList();
        state.LatestFailureFingerprint = occurrence.FailureFingerprint;
        state.LatestFailureSeenBefore = occurrence.SeenBefore;
        state.LatestFailureGeneration = occurrence.LastSeenGeneration;
        state.LatestFailureSummary = occurrence.Summary;
        state.LatestFailureEvidenceId = occurrence.EvidenceId;
        state.LatestFailureDiagnosisReference = occurrence.DiagnosisReference;
        state.LatestFailureContextFingerprint = occurrence.ReproductionContextFingerprint;
        state.LatestFailureRecipeId = occurrence.RecipeId;
        state.LatestFailureComponent = occurrence.Component;
        return occurrence;
    }

    internal FailureFingerprintInput BuildFailureInputLocked(string errorCode, string phase,
        string detail, QuicktestFailureRecord failure = null, string recipeId = null,
        string component = null, IReadOnlyList<TestInputValue> inputs = null,
        string projectFingerprint = null, string sourceFingerprint = null)
    {
        return new FailureFingerprintInput
        {
            ErrorCode = errorCode,
            Phase = phase,
            ExceptionType = failure?.ExceptionType,
            Message = failure?.ExceptionMessage ?? detail,
            Detail = detail ?? failure?.DiagnosticDetail,
            Component = component ?? "coordinator",
            ComponentIdentity = RunningBuildIdentity?.InformationalVersion,
            SourceRevision = RunningBuildIdentity?.SourceRevision,
            SourceFingerprint = sourceFingerprint,
            ProjectFingerprint = projectFingerprint ?? state.LaunchProfileFingerprint ??
                state.ProfileFingerprint ?? state.FrozenProfileFingerprint,
            RecipeId = recipeId,
            GenerationInputs = TestGenerationInputs.CloneValues(inputs ?? state.RuntimeProfile?.TestInputs ??
                state.FrozenTestInputs ?? state.TestInputs)
        };
    }

    private string CurrentDiagnosisReferenceLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null || string.IsNullOrWhiteSpace(incident.IncidentId))
            return null;
        return "Runtime/state.json#crashIsolation/" + FailureFingerprinting.Bound(incident.IncidentId);
    }

    internal FailureOccurrenceSummary FindEquivalentRecipeFailureLocked(string recipeId,
        string projectFingerprint, IReadOnlyList<TestInputValue> inputs, int maxCount,
        string sourceFingerprint = null)
    {
        if (maxCount <= 0)
            return null;
        return (state.FailureOccurrences ?? new List<FailureOccurrenceSummary>())
            .Where(value => value != null && value.OccurrenceCount >= maxCount &&
                string.Equals(value.RecipeId, FailureFingerprinting.NormalizeToken(recipeId), StringComparison.Ordinal) &&
                IsRepeatableRecipeFailureOccurrenceLocked(value))
            .FirstOrDefault(value => FailureFingerprinting.EquivalentContext(value, recipeId,
                projectFingerprint, inputs, RunningBuildIdentity?.InformationalVersion,
                RunningBuildIdentity?.SourceRevision, sourceFingerprint));
    }

    internal int RetireEquivalentRecipeFailuresLocked(
        string recipeId,
        string projectFingerprint,
        IReadOnlyList<TestInputValue> inputs,
        string sourceFingerprint = null)
    {
        // A successful run is a positive observation for the recipe itself.
        // Retire its active guard entries across prior contexts; their
        // evidence remains durable for historical diagnosis.
        FailureOccurrenceSummary[] retired = (state.FailureOccurrences ??
                new List<FailureOccurrenceSummary>())
            .Where(value => value != null &&
                string.Equals(
                    value.RecipeId,
                    FailureFingerprinting.NormalizeToken(recipeId),
                    StringComparison.Ordinal))
            .ToArray();
        if (retired.Length == 0)
        {
            return 0;
        }

        HashSet<string> retiredFingerprints = retired
            .Select(static value => value.FailureFingerprint)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        state.FailureOccurrences = (state.FailureOccurrences ??
                new List<FailureOccurrenceSummary>())
            .Where(value => value is null ||
                !retiredFingerprints.Contains(value.FailureFingerprint))
            .ToList();
        if (!string.IsNullOrWhiteSpace(state.LatestFailureFingerprint) &&
            retiredFingerprints.Contains(state.LatestFailureFingerprint))
        {
            state.LatestFailureFingerprint = null;
            state.LatestFailureSeenBefore = false;
            state.LatestFailureGeneration = 0;
            state.LatestFailureSummary = null;
            state.LatestFailureEvidenceId = null;
            state.LatestFailureDiagnosisReference = null;
            state.LatestFailureContextFingerprint = null;
            state.LatestFailureRecipeId = null;
            state.LatestFailureComponent = null;
        }
        SaveStateLocked();
        return retired.Length;
    }


    private bool IsRepeatableRecipeFailureOccurrenceLocked(FailureOccurrenceSummary occurrence)
    {
        if (occurrence == null)
            return false;
        if (!string.IsNullOrWhiteSpace(occurrence.ErrorCode))
            return FailureFingerprinting.IsRepeatableRecipeFailureCode(occurrence.ErrorCode);

        // Older state summaries did not persist ErrorCode, but their evidence
        // records do.  Recover the classification without rewriting or
        // deleting durable evidence.  If the evidence is unavailable or
        // malformed, fail closed and retain the repeated-failure protection.
        if (!string.IsNullOrWhiteSpace(occurrence.EvidenceId))
        {
            EvidenceLookupResult evidence = new FailureEvidenceStore(runtimeRoot,
                () => clock.UtcNow).Read(occurrence.EvidenceId);
            if (evidence.Found && !string.IsNullOrWhiteSpace(evidence.Record.ErrorCode))
                return FailureFingerprinting.IsRepeatableRecipeFailureCode(evidence.Record.ErrorCode);
        }
        return true;
    }

    internal string RecordRecipeFailure(string recipeId, string code, string error,
        int generation, string projectFingerprint, IReadOnlyList<TestInputValue> inputs,
        string sourceFingerprint = null)
    {
        lock (gate)
        {
            FailureOccurrenceSummary occurrence = RecordFailureOccurrenceLocked(
                BuildFailureInputLocked(code, "RECIPE", error, recipeId: recipeId,
                    component: "recipe", inputs: inputs, projectFingerprint: projectFingerprint,
                    sourceFingerprint: sourceFingerprint),
                generation, "RECIPE", error);
            SaveStateLocked();
            return occurrence?.FailureFingerprint ?? code;
        }
    }

    internal void AttachLatestFailureDiagnosisReferenceLocked()
    {
        string reference = CurrentDiagnosisReferenceLocked();
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(state.LatestFailureFingerprint))
            return;
        FailureOccurrenceSummary occurrence = state.FailureOccurrences?.FirstOrDefault(value =>
            string.Equals(value?.FailureFingerprint, state.LatestFailureFingerprint,
                StringComparison.Ordinal));
        if (occurrence == null)
            return;
        occurrence.DiagnosisReference = FailureFingerprinting.Bound(reference);
        state.LatestFailureDiagnosisReference = occurrence.DiagnosisReference;
        new FailureEvidenceStore(runtimeRoot, () => clock.UtcNow).UpdateDiagnosis(
            occurrence.EvidenceId, occurrence.DiagnosisReference);
        RefreshLatestFailureReferencesInHistoryLocked(occurrence.LastSeenGeneration);
    }

    internal ForensicResponse CreateForensicJsonResponse(BridgeRequest request, int exitCode)
    {
        if (string.Equals(request?.Command, "logs", StringComparison.OrdinalIgnoreCase))
        {
            LogsQueryResponse response = request.LogsQueryResponse ?? LogsFailure(exitCode,
                "LOGS_QUERY_RESPONSE_MISSING", "The logs query did not produce its dedicated response.");
            if (response.ExitCode == 0 && exitCode != 0)
                response.ExitCode = exitCode;
            return response;
        }
        EvidenceShowResponse evidence = request?.EvidenceShowResponse ?? EvidenceFailure(exitCode,
            "EVIDENCE_RESPONSE_MISSING", "The evidence command did not produce its dedicated response.");
        if (evidence.ExitCode == 0 && exitCode != 0)
            evidence.ExitCode = exitCode;
        return evidence;
    }
}
