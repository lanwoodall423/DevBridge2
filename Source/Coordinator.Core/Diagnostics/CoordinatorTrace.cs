using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBridge.Coordinator;

// This is deliberately a best-effort diagnostic sink. Trace failures must not
// alter the coordinator's fail-closed lifecycle or persistence behavior.
internal sealed class CoordinatorTrace
{
    internal const string FileName = "coordinator-events.jsonl";
    internal const int DefaultMaxFileBytes = 512 * 1024;
    internal const int DefaultMaxRetainedFiles = 3;

    private readonly string runtimeRoot;
    private readonly string filePath;
    private readonly int maxFileBytes;
    private readonly int maxRetainedFiles;
    private readonly Func<DateTime> utcNow;
    private readonly object gate = new();
    private bool disabled;

    internal CoordinatorTrace(string runtimeRoot,
        int maxFileBytes = DefaultMaxFileBytes,
        int maxRetainedFiles = DefaultMaxRetainedFiles,
        Func<DateTime> utcNow = null)
    {
        this.runtimeRoot = runtimeRoot ?? throw new ArgumentNullException(nameof(runtimeRoot));
        filePath = Path.Combine(runtimeRoot, FileName);
        this.maxFileBytes = Math.Max(256, maxFileBytes);
        this.maxRetainedFiles = Math.Max(1, maxRetainedFiles);
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal string FilePath => filePath;
    internal bool DisabledForTesting => disabled;

    internal void Record(CoordinatorTraceEvent entry)
    {
        if (entry == null || Volatile.Read(ref disabled))
            return;

        lock (gate)
        {
            if (disabled)
                return;

            try
            {
                CoordinatorTraceEvent safe = Sanitize(entry);
                byte[] bytes = Serialize(safe);
                if (bytes == null)
                    return;

                Directory.CreateDirectory(runtimeRoot);
                RotateIfNeeded(bytes.Length);
                using FileStream stream = new(filePath, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            catch (Exception)
            {
                // Never fall back to an unbounded alternate log or throw from
                // a lifecycle/persistence path because diagnostics failed.
                disabled = true;
            }
        }
    }

    private byte[] Serialize(CoordinatorTraceEvent value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,
            CoordinatorSerialization.JsonOptions) + Environment.NewLine);
        if (bytes.Length <= maxFileBytes)
            return bytes;

        // A caller cannot make a diagnostic record exceed the configured file
        // bound, even when tests use a deliberately small rotation size.
        value.Detail = null;
        value.BuildIdentity = null;
        value.Command = null;
        value.OperationId = null;
        bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,
            CoordinatorSerialization.JsonOptions) + Environment.NewLine);
        return bytes.Length <= maxFileBytes ? bytes : null;
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(filePath))
            return;

        long currentLength = new FileInfo(filePath).Length;
        if (currentLength == 0 || currentLength + incomingBytes <= maxFileBytes)
            return;

        string oldest = RotatedPath(maxRetainedFiles);
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (int index = maxRetainedFiles - 1; index >= 1; index--)
        {
            string source = RotatedPath(index);
            if (File.Exists(source))
                File.Move(source, RotatedPath(index + 1), true);
        }

        File.Move(filePath, RotatedPath(1), true);
    }

    private string RotatedPath(int index) => filePath + "." +
        index.ToString(CultureInfo.InvariantCulture);

    private static CoordinatorTraceEvent Sanitize(CoordinatorTraceEvent source)
    {
        return new CoordinatorTraceEvent
        {
            TimestampUtc = (source.TimestampUtc == default ? DateTime.UtcNow : source.TimestampUtc)
                .ToUniversalTime(),
            Event = Safe(source.Event, 96),
            RequestId = Safe(source.RequestId, 128),
            OperationId = Safe(source.OperationId, 128),
            Command = Safe(source.Command, 128),
            RuntimeSlotId = Safe(source.RuntimeSlotId, 256),
            Generation = source.Generation,
            Phase = Safe(source.Phase, 32),
            DurationMs = source.DurationMs.HasValue
                ? Math.Max(0, source.DurationMs.Value) : null,
            Success = source.Success,
            ErrorCode = Safe(source.ErrorCode, 96),
            Detail = Safe(source.Detail, 512),
            Category = Safe(source.Category, 96),
            ProtocolVersion = source.ProtocolVersion,
            BuildIdentity = SafeBuild(source.BuildIdentity)
        };
    }

    private static CoordinatorBuildIdentity SafeBuild(CoordinatorBuildIdentity value)
    {
        if (value == null)
            return null;

        return new CoordinatorBuildIdentity
        {
            ProductVersion = Safe(value.ProductVersion, 64),
            InformationalVersion = Safe(value.InformationalVersion, 128),
            SourceRevision = Safe(value.SourceRevision, 128),
            RevisionKnown = value.RevisionKnown,
            Dirty = value.Dirty,
            BuildConfiguration = Safe(value.BuildConfiguration, 32),
            ProcessStartedUtc = value.ProcessStartedUtc,
            CoordinatorProtocolVersion = value.CoordinatorProtocolVersion,
            ProtocolContract = Safe(value.ProtocolContract, 96)
        };
    }

    private static string Safe(string value, int maximum)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        string redacted;
        string trimmed = value.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) ||
            trimmed.StartsWith("[", StringComparison.Ordinal))
            redacted = DiagnosticRedactor.Json(value);
        else
            redacted = DiagnosticRedactor.Text(value);

        redacted = new string((redacted ?? string.Empty).Where(character =>
            !char.IsControl(character) || character == '\t').ToArray());
        return redacted.Length <= maximum ? redacted : redacted[..maximum];
    }
}

internal sealed class CoordinatorTraceEvent
{
    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; }

    [JsonPropertyName("event")]
    public string Event { get; set; }

    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string RequestId { get; set; }

    [JsonPropertyName("operationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string OperationId { get; set; }

    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Command { get; set; }

    [JsonPropertyName("runtimeSlotId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string RuntimeSlotId { get; set; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; set; }

    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Phase { get; set; }

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; set; }

    [JsonPropertyName("success")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Success { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; set; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Detail { get; set; }

    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Category { get; set; }

    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProtocolVersion { get; set; }

    [JsonPropertyName("buildIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoordinatorBuildIdentity BuildIdentity { get; set; }
}
