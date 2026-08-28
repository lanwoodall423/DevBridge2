using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static class DoctorSeverities
{
    internal const string Info = "INFO";
    internal const string Warning = "WARNING";
    internal const string Error = "ERROR";
}

internal static class DiagnosticResponseLimits
{
    internal const int MaxSampleCount = 16;
    internal const int MaxFindingCount = 96;
    internal const int MaxDiagnosticStringLength = 4096;
}

internal sealed class DiagnosticCollectionSummary
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }
}

internal sealed class DiagnosticPayloadMetadata
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; }

    [JsonPropertyName("configuredLimitBytes")]
    public int ConfiguredLimitBytes { get; set; }

    [JsonPropertyName("estimatedSerializedBytes")]
    public long? EstimatedSerializedBytes { get; set; }

    [JsonPropertyName("summarized")]
    public bool Summarized { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("fallback")]
    public bool Fallback { get; set; }

    [JsonPropertyName("collections")]
    public SortedDictionary<string, DiagnosticCollectionSummary> Collections { get; set; } =
        new(StringComparer.Ordinal);
}

internal sealed class DoctorNextAction
{
    [JsonPropertyName("command")]
    public string Command { get; set; }

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();

    [JsonPropertyName("reason")]
    public string Reason { get; set; }

    [JsonPropertyName("requiresLeaseId")]
    public bool RequiresLeaseId { get; set; }

    internal string StableKey() => Command + "|" + string.Join("\u001f", Arguments ?? new List<string>()) +
        "|" + RequiresLeaseId.ToString();

    internal string DisplayCommand() => Command +
        ((Arguments ?? new List<string>()).Count == 0 ? string.Empty : " " + string.Join(" ", Arguments));
}

internal sealed class DoctorFinding
{
    [JsonPropertyName("severity")]
    public string Severity { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }

    [JsonPropertyName("component")]
    public string Component { get; set; }

    [JsonPropertyName("details")]
    public SortedDictionary<string, string> Details { get; set; } =
        new(StringComparer.Ordinal);

    [JsonPropertyName("nextActions")]
    public List<DoctorNextAction> NextActions { get; set; } = new();

    internal string StableKey() => Component + "|" + Severity + "|" + Code + "|" + Message;
}

internal sealed class DoctorOperationalState
{
    [JsonPropertyName("phase")]
    public string Phase { get; set; }

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("launchIdPresent")]
    public bool LaunchIdPresent { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("processRunning")]
    public bool ProcessRunning { get; set; }

    [JsonPropertyName("maintenanceReady")]
    public bool MaintenanceReady { get; set; }

    [JsonPropertyName("restartQueued")]
    public bool RestartQueued { get; set; }

    [JsonPropertyName("targetGeneration")]
    public int TargetGeneration { get; set; }

    [JsonPropertyName("activeLeaseCount")]
    public int ActiveLeaseCount { get; set; }

    [JsonPropertyName("expiredLeaseCount")]
    public int ExpiredLeaseCount { get; set; }

    [JsonPropertyName("profileFingerprint")]
    public string ProfileFingerprint { get; set; }

    [JsonPropertyName("frozenProfileFingerprint")]
    public string FrozenProfileFingerprint { get; set; }

    [JsonPropertyName("modsConfigOwnership")]
    public string ModsConfigOwnership { get; set; }

    [JsonPropertyName("modsConfigSafeToWrite")]
    public bool ModsConfigSafeToWrite { get; set; }

    [JsonPropertyName("readinessState")]
    public string ReadinessState { get; set; }

    [JsonPropertyName("crashIsolationStatus")]
    public string CrashIsolationStatus { get; set; }

    [JsonPropertyName("mutationCommandsAllowed")]
    public bool MutationCommandsAllowed { get; set; }

    [JsonPropertyName("queuedProjectIntentCount")]
    public int QueuedProjectIntentCount { get; set; }

    [JsonPropertyName("currentAcceptedGeneration")]
    public int CurrentAcceptedGeneration { get; set; }

    [JsonPropertyName("previousAcceptedGeneration")]
    public int? PreviousAcceptedGeneration { get; set; }

    [JsonPropertyName("lastKnownGoodGeneration")]
    public int? LastKnownGoodGeneration { get; set; }

    [JsonPropertyName("currentProfileMatchesLastKnownGood")]
    public bool? CurrentProfileMatchesLastKnownGood { get; set; }

    [JsonPropertyName("terminalFailureCode")]
    public string TerminalFailureCode { get; set; }

    [JsonPropertyName("terminalFailureDetail")]
    public string TerminalFailureDetail { get; set; }

    [JsonPropertyName("historyCorrupt")]
    public bool HistoryCorrupt { get; set; }

    [JsonPropertyName("currentGenerationTrust")]
    public string CurrentGenerationTrust { get; set; }

    [JsonPropertyName("nextGenerationConfig")]
    public ConfigurationHealth NextGenerationConfig { get; set; }
}

internal sealed class DoctorAuditReport
{
    [JsonIgnore]
    internal bool Healthy => Findings.All(value => !string.Equals(value.Severity,
        DoctorSeverities.Error, StringComparison.Ordinal));

    [JsonIgnore]
    internal DoctorFinding FirstError => Findings.FirstOrDefault(value =>
        string.Equals(value.Severity, DoctorSeverities.Error, StringComparison.Ordinal));

    internal int SchemaVersion { get; set; } = DevBridgeSchemaVersions.Doctor;
    internal List<DoctorFinding> Findings { get; } = new();
    internal ComponentVersionReport Components { get; set; }
    internal DoctorOperationalState OperationalState { get; set; }
    internal DevBridgeIdentityContract Identity { get; set; }
    internal GenerationHistoryView GenerationHistory { get; set; }
    internal ConfigurationHealth NextGenerationConfig { get; set; }
    internal List<DoctorNextAction> NextActions { get; } = new();
    internal int FindingsTotalCount { get; private set; }
    internal bool FindingsTruncated { get; private set; }

    internal void AddFinding(string severity, string code, string message, string component,
        IDictionary<string, string> details = null)
    {
        DoctorFinding finding = new()
        {
            Severity = severity,
            Code = code,
            Message = DiagnosticRedactor.Text(message),
            Component = component,
            Details = new SortedDictionary<string, string>(StringComparer.Ordinal)
        };
        foreach (KeyValuePair<string, string> detail in details ??
                 new Dictionary<string, string>(StringComparer.Ordinal))
            finding.Details[detail.Key] = DiagnosticRedactor.Text(detail.Value);

        finding.NextActions = severity == DoctorSeverities.Info &&
            !string.Equals(code, "PROCESS_QUARANTINE_CLEARED", StringComparison.Ordinal)
            ? new List<DoctorNextAction>()
            : RecoveryGuidance.For(code, finding.Message);
        Findings.Add(finding);
    }

    internal void Complete()
    {
        Findings.Sort((left, right) => string.Compare(left.StableKey(), right.StableKey(),
            StringComparison.Ordinal));
        FindingsTotalCount = Findings.Count;
        FindingsTruncated = Findings.Count > DiagnosticResponseLimits.MaxFindingCount;
        if (FindingsTruncated)
        {
            DoctorFinding firstError = FirstError;
            List<DoctorFinding> retained = Findings.Take(DiagnosticResponseLimits.MaxFindingCount).ToList();
            if (firstError != null && !retained.Contains(firstError))
                retained[retained.Count - 1] = firstError;
            retained.Sort((left, right) => string.Compare(left.StableKey(), right.StableKey(),
                StringComparison.Ordinal));
            Findings.Clear();
            Findings.AddRange(retained);
        }

        NextActions.Clear();
        foreach (DoctorNextAction action in Findings.SelectMany(value => value.NextActions)
                     .Concat(RecoveryGuidance.For(FirstError?.Code, FirstError?.Message))
                     .GroupBy(value => value.StableKey(), StringComparer.Ordinal)
                     .Select(value => value.First())
                     .OrderBy(value => value.StableKey(), StringComparer.Ordinal)
                     .Take(DiagnosticResponseLimits.MaxSampleCount))
            NextActions.Add(action);
    }
}

internal static class RecoveryGuidance
{
    internal static List<DoctorNextAction> For(string errorCode, string reason)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            return new List<DoctorNextAction>();

        string safeReason = DiagnosticRedactor.Text(reason);
        List<DoctorNextAction> actions = new();
        void Add(string[] arguments, bool requiresLeaseId = false) => actions.Add(new DoctorNextAction
        {
            Command = "DevBridge.cmd",
            Arguments = arguments.ToList(),
            Reason = safeReason,
            RequiresLeaseId = requiresLeaseId
        });

        switch (errorCode)
        {
            case "READINESS_TIMEOUT":
                Add(new[] { "status", "--json" });
                Add(new[] { "doctor", "--json" });
                Add(new[] { "ensure-ready", "<lease-id>", "--json" }, true);
                break;
            case "PROCESS_INSPECTION_AMBIGUOUS":
            case "PROCESS_MULTIPLE_MATCHING":
            case "PROCESS_OWNERSHIP_AMBIGUOUS":
            case "MAINTENANCE_PROCESS_PRESENT":
                Add(new[] { "doctor", "--json" });
                Add(new[] { "status", "--json" });
                break;
            case "PROCESS_QUARANTINE_CLEARED":
                Add(new[] { "status", "--json" });
                Add(new[] { "restart", "--json" });
                break;
            case "PROFILE_CONFLICT":
            case "PROFILE_INVALID_REQUEST":
            case "PROFILE_MISSING_PROJECT":
            case "PROFILE_PROJECT_METADATA_INVALID":
            case "FUTURE_CONFIGURATION_INVALID":
            case "FUTURE_CONFIGURATION_DIFFERS":
            case "PROFILE_UNKNOWN_PROJECT":
            case "PROFILE_MISSING_PACKAGE":
            case "PROFILE_AMBIGUOUS_PACKAGE":
            case "PROFILE_DEPENDENCY_CYCLE":
            case "PROFILE_LOAD_ORDER_CYCLE":
            case "PROFILE_MALFORMED_METADATA":
                Add(new[] { "project", "status", "--json" });
                Add(new[] { "project", "resolve", "<alias[,alias...]>", "--json" });
                Add(new[] { "doctor", "--json" });
                break;
            case "QUEUED_PROJECT_INTENT":
                Add(new[] { "project", "status", "--json" });
                Add(new[] { "restart", "--json" });
                break;
            case "PROFILE_EXTERNAL_MUTATION":
            case "MODSCONFIG_OWNERSHIP_UNKNOWN":
                Add(new[] { "mods", "status", "--json" });
                Add(new[] { "doctor", "--json" });
                break;
            case "CRASH_ISOLATION_ACTIVE":
            case "CRASH_ISOLATION_QUARANTINED":
                Add(new[] { "status", "--json" });
                Add(new[] { "doctor", "--json" });
                break;
            case "PERSISTED_STATE_SCHEMA_UNSUPPORTED":
            case "PERSISTED_STATE_SCHEMA_INVALID":
            case "PERSISTED_STATE_MALFORMED":
            case "READINESS_SCHEMA_UNSUPPORTED":
            case "READINESS_SCHEMA_INVALID":
            case "GENERATED_MODS_CONFIG_SCHEMA_UNSUPPORTED":
            case "GENERATED_MODS_CONFIG_SCHEMA_INVALID":
            case "GENERATED_MODS_CONFIG_MALFORMED":
            case "GENERATED_MODS_CONFIG_READ_FAILED":
            case "GENERATION_HISTORY_CORRUPT":
            case "GENERATION_MANIFEST_CORRUPT":
                Add(new[] { "doctor", "--json" });
                Add(new[] { "history", "--json" });
                break;
            case "LEASE_EXPIRED":
            case "LEASE_NOT_FOUND":
            case "LEASE_REQUIRED":
            case "MAINTENANCE_LEASE_REQUIRED":
            case "MAINTENANCE_NOT_READY":
                Add(new[] { "status", "--json" });
                Add(new[] { "doctor", "--json" });
                break;
            case "RESTART_QUEUED":
                Add(new[] { "wait-ready", "--json" });
                Add(new[] { "status", "--json" });
                break;
            case "COORDINATOR_VERSION_MISMATCH":
            case "MOD_PROTOCOL_TOO_OLD":
            case "MOD_PROTOCOL_TOO_NEW":
            case "RUNTIME_SCHEMA_UNSUPPORTED":
                Add(new[] { "doctor", "--json" });
                break;
            case "FILE_PERMISSION_DENIED":
            case "FILE_ACCESS_FAILED":
                Add(new[] { "doctor", "--json" });
                Add(new[] { "status", "--json" });
                break;
            default:
                if (errorCode.StartsWith("RIMBRIDGE_", StringComparison.Ordinal))
                {
                    Add(new[] { "bridge", "status", "--json" });
                    Add(new[] { "doctor", "--json" });
                }
                else if (errorCode.StartsWith("PROCESS_", StringComparison.Ordinal) ||
                         errorCode.StartsWith("READINESS_", StringComparison.Ordinal))
                {
                    Add(new[] { "status", "--json" });
                    Add(new[] { "doctor", "--json" });
                }
                else if (errorCode.StartsWith("PROFILE_", StringComparison.Ordinal))
                {
                    Add(new[] { "project", "status", "--json" });
                    Add(new[] { "doctor", "--json" });
                }
                else
                {
                    Add(new[] { "doctor", "--json" });
                    Add(new[] { "status", "--json" });
                }
                break;
        }

        return actions;
    }
}

internal static class DiagnosticRedactor
{
    private static readonly Regex SecretAssignment = new(
        @"(?<name>token|password|secret|credential|api[-_]?key|authorization|access[-_]?token)\s*(?<separator>[:=])\s*(?<value>""[^""]*""|'[^']*'|[^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BearerValue = new(
        @"(?<prefix>\bBearer\s+)(?<value>[^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Text(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        string redacted = SecretAssignment.Replace(value, match =>
            match.Groups["name"].Value + match.Groups["separator"].Value + "<redacted>");
        return BearerValue.Replace(redacted, match => match.Groups["prefix"].Value + "<redacted>");
    }

    internal static string Json(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
                WriteJsonElement(document.RootElement, writer);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return Text(value);
        }
    }

    private static void WriteJsonElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveProperty(property.Name))
                        writer.WriteStringValue("<redacted>");
                    else
                        WriteJsonElement(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteJsonElement(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Text(element.GetString()));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveProperty(string name) =>
        string.Equals(name, "token", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "password", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "secret", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "credential", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "apiKey", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "accessToken", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "authorization", StringComparison.OrdinalIgnoreCase);
}
