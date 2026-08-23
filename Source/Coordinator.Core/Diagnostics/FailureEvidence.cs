using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static class FailureEvidenceLimits
{
    internal const int MaxOccurrences = 64;
    internal const int MaxEvidenceRecords = 64;
    internal const int MaxEvidenceBytes = 16 * 1024;
    internal const int MaxSemanticRecords = 64;
    internal const int MaxTraceRecords = 48;
    internal const int MaxText = 512;
    internal const int MaxStackFrames = 6;
    internal const int MaxRawLogBytes = 256 * 1024;
    internal static readonly TimeSpan EvidenceLifetime = TimeSpan.FromDays(7);
}

internal sealed class FailureFingerprintInput
{
    internal string ErrorCode { get; init; }
    internal string Phase { get; init; }
    internal string ExceptionType { get; init; }
    internal string Message { get; init; }
    internal string Detail { get; init; }
    internal IReadOnlyList<string> StackFrames { get; init; } = Array.Empty<string>();
    internal string Component { get; init; }
    internal string ComponentIdentity { get; init; }
    internal string SourceRevision { get; init; }
    // A caller may bind a recipe reproduction to the source artifact that it
    // just built.  This is intentionally separate from the coordinator's own
    // source revision: the two components can be rebuilt independently.
    internal string SourceFingerprint { get; init; }
    internal string ProjectFingerprint { get; init; }
    internal string RecipeId { get; init; }
    internal IReadOnlyList<TestInputValue> GenerationInputs { get; init; } = Array.Empty<TestInputValue>();
}

internal sealed class NormalizedFailureFingerprint
{
    internal string FailureFingerprint { get; init; }
    internal string ReproductionContextFingerprint { get; init; }
    internal string Summary { get; init; }
    internal string ErrorCode { get; init; }
    internal string Phase { get; init; }
    internal string ExceptionType { get; init; }
    internal string Component { get; init; }
    internal string RecipeId { get; init; }
    internal string ProjectFingerprint { get; init; }
    internal List<string> StackFrames { get; init; } = new();
    internal string CanonicalFailure { get; init; }
    internal string CanonicalContext { get; init; }
}

internal static class FailureFingerprinting
{
    private static readonly Regex Timestamp = new(
        @"\b(?:\d{4}[-/]\d{1,2}[-/]\d{1,2}[T ]\d{1,2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?|\d{1,2}:\d{2}:\d{2}(?:\.\d+)?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GuidValue = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Address = new(
        @"\b0x[0-9a-f]{6,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TempPath = new(
        @"(?:(?:[A-Za-z]:)?[\\/])?(?:users[\\/][^\\/\s]+[\\/]appdata[\\/]local[\\/]temp|tmp|temp)[\\/][^\s""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Pid = new(
        @"\b(?:pid|process(?:\s+id)?|processid)\s*[:=]?\s*\d+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RequestId = new(
        @"\b(?:request|operation|correlation|incident|launch)\s*id\s*[:=]\s*[A-Za-z0-9._-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LineNumber = new(
        @"\s*:\s*line\s+\d+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static NormalizedFailureFingerprint Create(FailureFingerprintInput input)
    {
        input ??= new FailureFingerprintInput();
        string code = NormalizeToken(input.ErrorCode);
        string phase = NormalizeToken(input.Phase);
        string exceptionType = NormalizeToken(input.ExceptionType);
        string component = NormalizeToken(input.Component);
        string componentIdentity = NormalizeText(input.ComponentIdentity);
        string sourceRevision = NormalizeText(input.SourceRevision);
        string sourceFingerprint = NormalizeText(input.SourceFingerprint);
        string projectFingerprint = NormalizeText(input.ProjectFingerprint);
        string recipeId = NormalizeToken(input.RecipeId);
        string message = NormalizeText(string.IsNullOrWhiteSpace(input.Message)
            ? input.Detail : input.Message);
        List<string> frames = (input.StackFrames ?? Array.Empty<string>())
            .Select(NormalizeText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(FailureEvidenceLimits.MaxStackFrames)
            .ToList();

        string canonicalInputs = string.Join(";", (input.GenerationInputs ?? Array.Empty<TestInputValue>())
            .Where(value => value != null && !string.IsNullOrWhiteSpace(value.Name))
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Value, StringComparer.Ordinal)
            .Select(value => NormalizeToken(value.Name) + "=" + NormalizeText(value.Value)));
        List<string> contextParts = new()
        {
            "component=" + component,
            "componentIdentity=" + componentIdentity,
            "sourceRevision=" + sourceRevision
        };
        // Keep the pre-source-fingerprint context byte-for-byte compatible for
        // callers that do not provide this optional binding.  That preserves
        // the existing repeated-failure guard while allowing a new artifact to
        // be distinguished from an older one.
        if (!string.IsNullOrWhiteSpace(sourceFingerprint))
            contextParts.Add("sourceFingerprint=" + sourceFingerprint);
        contextParts.Add("projectFingerprint=" + projectFingerprint);
        contextParts.Add("recipeId=" + recipeId);
        contextParts.Add("inputs=" + canonicalInputs);
        string context = string.Join("\n", contextParts);
        string failure = string.Join("\n", new[]
        {
            "schema=" + DevBridgeSchemaVersions.FailureFingerprint,
            "code=" + code,
            "phase=" + phase,
            "exceptionType=" + exceptionType,
            "message=" + message,
            "stack=" + string.Join("|", frames),
            "context=" + context
        });
        string contextFingerprint = "ctx-" + Hash(context);
        string fingerprint = "ff-" + Hash(failure);
        string summary = message;
        if (string.IsNullOrWhiteSpace(summary))
            summary = string.IsNullOrWhiteSpace(exceptionType) ? code : exceptionType;

        return new NormalizedFailureFingerprint
        {
            FailureFingerprint = fingerprint,
            ReproductionContextFingerprint = contextFingerprint,
            Summary = Bound(summary),
            ErrorCode = Bound(code),
            Phase = Bound(phase),
            ExceptionType = Bound(exceptionType),
            Component = Bound(component),
            RecipeId = Bound(recipeId),
            ProjectFingerprint = Bound(projectFingerprint),
            StackFrames = frames,
            CanonicalFailure = failure,
            CanonicalContext = context
        };
    }

    internal static bool EquivalentContext(FailureOccurrenceSummary occurrence,
        string recipeId, string projectFingerprint, IReadOnlyList<TestInputValue> inputs,
        string componentIdentity, string sourceRevision, string sourceFingerprint = null)
    {
        if (occurrence == null)
            return false;
        NormalizedFailureFingerprint context = Create(new FailureFingerprintInput
        {
            RecipeId = recipeId,
            ProjectFingerprint = projectFingerprint,
            GenerationInputs = inputs,
            Component = occurrence.Component,
            ComponentIdentity = componentIdentity,
            SourceRevision = sourceRevision,
            SourceFingerprint = sourceFingerprint
        });
        return string.Equals(occurrence.ReproductionContextFingerprint,
            context.ReproductionContextFingerprint, StringComparison.Ordinal);
    }

    internal static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string normalized = DiagnosticRedactor.Text(value);
        normalized = TempPath.Replace(normalized, "<temp-path>");
        normalized = RequestId.Replace(normalized, "<request-id>");
        normalized = Pid.Replace(normalized, "<pid>");
        normalized = GuidValue.Replace(normalized, "<random-id>");
        normalized = Address.Replace(normalized, "<address>");
        normalized = Timestamp.Replace(normalized, "<timestamp>");
        normalized = LineNumber.Replace(normalized, string.Empty);
        normalized = new string(normalized.Where(value =>
            !char.IsControl(value) || value == '\t' || value == '\n').ToArray());
        normalized = Regex.Replace(normalized, @"\s+", " ",
            RegexOptions.CultureInvariant).Trim();
        return Bound(normalized);
    }

    internal static string NormalizeToken(string value) =>
        NormalizeText(value).ToUpperInvariant();

    // A supplied lease is a caller-owned capability.  Refusing to use it for
    // an incompatible generation or autonomous restart means no recipe
    // operation was attempted, so that precondition failure must not poison
    // the repeated-execution guard.  Keep the evidence for diagnosis and
    // classify every other recipe failure normally.
    internal static bool IsRepeatableRecipeFailureCode(string code) =>
        !NormalizeToken(code).StartsWith("RECIPE_SUPPLIED_LEASE_", StringComparison.Ordinal);

    internal static string Bound(string value) =>
        string.IsNullOrEmpty(value) || value.Length <= FailureEvidenceLimits.MaxText
            ? value : value[..FailureEvidenceLimits.MaxText];

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)))
        .ToLowerInvariant();
}

internal sealed class FailureOccurrenceSummary
{
    [JsonPropertyName("failureFingerprint")]
    public string FailureFingerprint { get; set; }

    [JsonPropertyName("seenBefore")]
    public bool SeenBefore { get; set; }

    [JsonPropertyName("firstSeenGeneration")]
    public int FirstSeenGeneration { get; set; }

    [JsonPropertyName("lastSeenGeneration")]
    public int LastSeenGeneration { get; set; }

    [JsonPropertyName("firstSeenUtc")]
    public DateTime FirstSeenUtc { get; set; }

    [JsonPropertyName("lastSeenUtc")]
    public DateTime LastSeenUtc { get; set; }

    [JsonPropertyName("occurrenceCount")]
    public int OccurrenceCount { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }

    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; }

    [JsonPropertyName("phase")]
    public string Phase { get; set; }

    [JsonPropertyName("component")]
    public string Component { get; set; }

    [JsonPropertyName("recipeId")]
    public string RecipeId { get; set; }

    [JsonPropertyName("projectFingerprint")]
    public string ProjectFingerprint { get; set; }

    [JsonPropertyName("reproductionContextFingerprint")]
    public string ReproductionContextFingerprint { get; set; }

    [JsonPropertyName("evidenceId")]
    public string EvidenceId { get; set; }

    [JsonPropertyName("diagnosisReference")]
    public string DiagnosisReference { get; set; }
}

internal sealed class FailureEvidenceRecord
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = DevBridgeSchemaVersions.Evidence;

    [JsonPropertyName("contract")]
    public string Contract { get; set; } = DevBridgeSchemaVersions.Evidence;

    [JsonPropertyName("evidenceId")]
    public string EvidenceId { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("failureFingerprint")]
    public string FailureFingerprint { get; set; }

    [JsonPropertyName("seenBefore")]
    public bool SeenBefore { get; set; }

    [JsonPropertyName("occurrenceCount")]
    public int OccurrenceCount { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; }

    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; }

    [JsonPropertyName("phase")]
    public string Phase { get; set; }

    [JsonPropertyName("exceptionType")]
    public string ExceptionType { get; set; }

    [JsonPropertyName("component")]
    public string Component { get; set; }

    [JsonPropertyName("recipeId")]
    public string RecipeId { get; set; }

    [JsonPropertyName("projectFingerprint")]
    public string ProjectFingerprint { get; set; }

    [JsonPropertyName("reproductionContextFingerprint")]
    public string ReproductionContextFingerprint { get; set; }

    [JsonPropertyName("diagnosisReference")]
    public string DiagnosisReference { get; set; }

    [JsonPropertyName("detail")]
    public string Detail { get; set; }

    [JsonPropertyName("stackFrames")]
    public List<string> StackFrames { get; set; } = new();

    [JsonPropertyName("playerLogSegmentReference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string PlayerLogSegmentReference { get; set; }
}

internal sealed class EvidenceLookupResult
{
    internal FailureEvidenceRecord Record { get; init; }
    internal string ErrorCode { get; init; }
    internal string Error { get; init; }
    internal bool Found => Record != null;
}

internal sealed class FailureEvidenceStore
{
    private static readonly Regex EvidenceId = new(
        @"^ev-[0-9a-f]{24}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string directory;
    private readonly Func<DateTime> utcNow;

    internal FailureEvidenceStore(string runtimeRoot, Func<DateTime> utcNow = null)
    {
        directory = Path.Combine(runtimeRoot ?? string.Empty, "evidence");
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal string Write(FailureEvidenceRecord record)
    {
        if (record == null || string.IsNullOrWhiteSpace(record.FailureFingerprint))
            return null;
        try
        {
            Directory.CreateDirectory(directory);
            string seed = string.Join("|", record.FailureFingerprint,
                record.Generation.ToString(CultureInfo.InvariantCulture),
                record.OccurrenceCount.ToString(CultureInfo.InvariantCulture),
                record.Component ?? string.Empty);
            record.EvidenceId = "ev-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..24];
            record.SchemaVersion = DevBridgeSchemaVersions.Evidence;
            record.Contract = DevBridgeSchemaVersions.Evidence;
            record.CreatedUtc = record.CreatedUtc == default ? utcNow().ToUniversalTime() : record.CreatedUtc.ToUniversalTime();
            string json = JsonSerializer.Serialize(record, CoordinatorSerialization.JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > FailureEvidenceLimits.MaxEvidenceBytes)
                return null;
            string path = Path.Combine(directory, record.EvidenceId + ".json");
            if (!File.Exists(path))
            {
                string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            Prune();
            return record.EvidenceId;
        }
        catch
        {
            return null;
        }
    }

    internal EvidenceLookupResult Read(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !EvidenceId.IsMatch(id))
            return new EvidenceLookupResult { ErrorCode = "EVIDENCE_ID_INVALID", Error = "Evidence IDs use the bounded ev-xxxxxxxxxxxxxxxxxxxxxxxx format." };
        string path = Path.Combine(directory, id + ".json");
        if (!File.Exists(path))
            return new EvidenceLookupResult { ErrorCode = "EVIDENCE_NOT_FOUND", Error = "The requested evidence is missing or has expired." };
        try
        {
            string json = File.ReadAllText(path);
            if (Encoding.UTF8.GetByteCount(json) > FailureEvidenceLimits.MaxEvidenceBytes)
                return new EvidenceLookupResult { ErrorCode = "EVIDENCE_INVALID", Error = "The evidence record exceeds its bounded size." };
            FailureEvidenceRecord record = JsonSerializer.Deserialize<FailureEvidenceRecord>(json,
                CoordinatorSerialization.JsonOptions);
            if (record == null || !string.Equals(record.EvidenceId, id, StringComparison.Ordinal) ||
                !string.Equals(record.Contract, DevBridgeSchemaVersions.Evidence, StringComparison.Ordinal))
                return new EvidenceLookupResult { ErrorCode = "EVIDENCE_INVALID", Error = "The evidence record failed its schema contract." };
            if (record.CreatedUtc != default && record.CreatedUtc.ToUniversalTime() <
                utcNow().ToUniversalTime() - FailureEvidenceLimits.EvidenceLifetime)
                return new EvidenceLookupResult { ErrorCode = "EVIDENCE_EXPIRED", Error = "The bounded evidence record has expired." };
            return new EvidenceLookupResult { Record = record };
        }
        catch
        {
            return new EvidenceLookupResult { ErrorCode = "EVIDENCE_INVALID", Error = "The evidence record could not be read." };
        }
    }

    internal bool UpdateDiagnosis(string id, string diagnosisReference)
    {
        if (string.IsNullOrWhiteSpace(id) || !EvidenceId.IsMatch(id))
            return false;
        try
        {
            EvidenceLookupResult lookup = Read(id);
            if (!lookup.Found)
                return false;
            lookup.Record.DiagnosisReference = FailureFingerprinting.Bound(diagnosisReference);
            string path = Path.Combine(directory, id + ".json");
            string json = JsonSerializer.Serialize(lookup.Record, CoordinatorSerialization.JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > FailureEvidenceLimits.MaxEvidenceBytes)
                return false;
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Prune()
    {
        FileInfo[] files = new DirectoryInfo(directory).GetFiles("ev-*.json")
            .Where(value => EvidenceId.IsMatch(Path.GetFileNameWithoutExtension(value.Name)))
            .OrderByDescending(value => ReadCreatedUtc(value.FullName))
            .ThenByDescending(value => value.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (FileInfo file in files.Skip(FailureEvidenceLimits.MaxEvidenceRecords))
        {
            try { file.Delete(); } catch { }
        }
    }

    private DateTime ReadCreatedUtc(string path)
    {
        try
        {
            FailureEvidenceRecord record = JsonSerializer.Deserialize<FailureEvidenceRecord>(
                File.ReadAllText(path), CoordinatorSerialization.JsonOptions);
            return record?.CreatedUtc ?? File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return File.GetLastWriteTimeUtc(path);
        }
    }
}

internal sealed class SemanticLogRecord
{
    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("sinceLaunch")]
    public bool SinceLaunch { get; set; } = true;

    [JsonPropertyName("severity")]
    public string Severity { get; set; }

    [JsonPropertyName("component")]
    public string Component { get; set; }

    [JsonPropertyName("fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Fingerprint { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }

    [JsonPropertyName("stackFrames")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> StackFrames { get; set; }

    [JsonPropertyName("occurrenceCount")]
    public int OccurrenceCount { get; set; } = 1;
}

internal sealed class SemanticLogParseResult
{
    internal List<SemanticLogRecord> Records { get; init; } = new();
    internal bool Truncated { get; init; }
    internal int RawBytes { get; init; }
}

internal static class SemanticLogParser
{
    private static readonly Regex StackFrame = new(
        @"^\s*at\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Error = new(
        @"\b(?:error|exception|failed|failure|fatal|critical|stack trace)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Warning = new(
        @"\b(?:warn|warning)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Debug = new(
        @"\bdebug\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Component = new(
        @"^\s*\[(?<component>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static SemanticLogParseResult Parse(string text, int generation,
        int maximum = FailureEvidenceLimits.MaxSemanticRecords)
    {
        text ??= string.Empty;
        Dictionary<string, SemanticLogRecord> distinct = new(StringComparer.Ordinal);
        bool truncated = false;
        int sequence = 0;
        SemanticLogRecord current = null;
        foreach (string rawLine in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string line = FailureFingerprinting.Bound(rawLine?.TrimEnd() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (StackFrame.IsMatch(line) && current != null)
            {
                current.StackFrames ??= new List<string>();
                string frame = FailureFingerprinting.NormalizeText(line);
                if (current.StackFrames.Count < FailureEvidenceLimits.MaxStackFrames &&
                    !current.StackFrames.Contains(frame, StringComparer.Ordinal))
                    current.StackFrames.Add(frame);
                continue;
            }

            string severity = Error.IsMatch(line) ? "ERROR" :
                Warning.IsMatch(line) ? "WARN" : Debug.IsMatch(line) ? "DEBUG" : "INFO";
            Match componentMatch = Component.Match(line);
            string component = componentMatch.Success ? componentMatch.Groups["component"].Value :
                line.IndexOf("RimBridge", StringComparison.OrdinalIgnoreCase) >= 0 ? "RimBridge" : "RimWorld";
            if (component.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("warn", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("info", StringComparison.OrdinalIgnoreCase) ||
                component.Equals("debug", StringComparison.OrdinalIgnoreCase))
            {
                Match secondComponent = Regex.Match(line, @"^\s*\[[^\]]+\]\s*\[(?<component>[^\]]+)\]",
                    RegexOptions.CultureInvariant);
                if (secondComponent.Success)
                    component = secondComponent.Groups["component"].Value;
            }
            component = FailureFingerprinting.Bound(FailureFingerprinting.NormalizeToken(component));
            string message = FailureFingerprinting.NormalizeText(line);
            string fingerprint = null;
            if (severity == "ERROR")
            {
                fingerprint = FailureFingerprinting.Create(new FailureFingerprintInput
                {
                    ErrorCode = "PLAYER_LOG_ERROR",
                    Phase = "PLAYER_LOG",
                    Component = component,
                    Message = message
                }).FailureFingerprint;
            }
            string key = string.Join("|", severity, component, fingerprint ?? string.Empty, message);
            if (distinct.TryGetValue(key, out SemanticLogRecord prior))
            {
                prior.OccurrenceCount++;
                current = prior;
                continue;
            }
            if (distinct.Count >= Math.Clamp(maximum, 1, FailureEvidenceLimits.MaxSemanticRecords))
            {
                truncated = true;
                break;
            }
            current = new SemanticLogRecord
            {
                Sequence = ++sequence,
                Generation = generation,
                Severity = severity,
                Component = component,
                Fingerprint = fingerprint,
                Message = message,
                StackFrames = severity == "ERROR" ? new List<string>() : null
            };
            distinct[key] = current;
        }
        return new SemanticLogParseResult
        {
            Records = distinct.Values.ToList(),
            Truncated = truncated,
            RawBytes = Encoding.UTF8.GetByteCount(text)
        };
    }
}

internal static class CoordinatorTraceReader
{
    internal static List<CoordinatorTraceEvent> Read(string runtimeRoot,
        int maximum = FailureEvidenceLimits.MaxTraceRecords)
    {
        List<CoordinatorTraceEvent> result = new();
        string path = Path.Combine(runtimeRoot ?? string.Empty, CoordinatorTrace.FileName);
        IEnumerable<string> paths = Enumerable.Range(1, CoordinatorTrace.DefaultMaxRetainedFiles)
            .Select(index => path + "." + index.ToString(CultureInfo.InvariantCulture))
            .Append(path);
        foreach (string candidate in paths)
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                foreach (string line in File.ReadLines(candidate).Take(512))
                {
                    try
                    {
                        CoordinatorTraceEvent entry = JsonSerializer.Deserialize<CoordinatorTraceEvent>(
                            line, CoordinatorSerialization.JsonOptions);
                        if (entry != null)
                            result.Add(entry);
                    }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result.OrderBy(value => value.TimestampUtc).TakeLast(
            Math.Clamp(maximum, 1, FailureEvidenceLimits.MaxTraceRecords)).ToList();
    }
}

internal abstract class ForensicResponse
{
    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ExitCode { get; set; }
}

internal sealed class LogsQueryResponse : ForensicResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.LogsQuery;

    [JsonPropertyName("contract")]
    public string Contract { get; init; } = DevBridgeSchemaVersions.LogsQuery;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("generation")]
    public int Generation { get; init; }

    [JsonPropertyName("sinceLaunch")]
    public bool SinceLaunch { get; init; }

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("rawBytes")]
    public int RawBytes { get; init; }

    [JsonPropertyName("semanticBytes")]
    public int SemanticBytes { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("records")]
    public List<SemanticLogRecord> Records { get; init; } = new();

    [JsonPropertyName("trace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CoordinatorTraceEvent> Trace { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; init; }
}

internal sealed class EvidenceShowResponse : ForensicResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.Evidence;

    [JsonPropertyName("contract")]
    public string Contract { get; init; } = DevBridgeSchemaVersions.Evidence;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FailureEvidenceRecord Evidence { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; init; }
}
