using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBridge.Coordinator;

internal sealed class RimBridgeWireResult
{
    internal bool Success { get; init; }
    internal string ErrorCode { get; init; }
    internal string Error { get; init; }
    internal bool AuthenticationFailure { get; init; }
    internal bool Timeout { get; init; }
    internal JsonElement? Payload { get; init; }
    internal JsonElement? RawResponse { get; init; }
}

internal sealed class RimBridgeRouteResult
{
    internal string Operation { get; init; }
    internal string ToolName { get; init; }
    internal string OperationId { get; init; }
    internal string WorkflowId { get; init; }
    internal bool Success { get; init; }
    internal string ErrorCode { get; init; }
    internal string Error { get; init; }
    internal DateTime InvocationTimestampUtc { get; init; }
    internal string LaunchId { get; init; }
    internal int Generation { get; init; }
    internal string ProfileFingerprint { get; init; }
    internal int ProcessId { get; init; }
    internal string EndpointHost { get; init; }
    internal int EndpointPort { get; init; }
    internal JsonElement? Payload { get; init; }
    internal JsonElement? OpaqueEvidence { get; init; }

    internal JsonRimBridgeRoute ToJson() => new()
    {
        Operation = Operation,
        ToolName = ToolName,
        OperationId = OperationId,
        WorkflowId = WorkflowId,
        Success = Success,
        ErrorCode = ErrorCode,
        Error = Error,
        Result = Payload,
        OpaqueEvidence = OpaqueEvidence,
        Provenance = new JsonRimBridgeProvenance
        {
            WorkflowId = WorkflowId,
            Generation = Generation,
            LaunchId = LaunchId,
            ProfileFingerprint = ProfileFingerprint,
            ProcessId = ProcessId,
            EndpointHost = EndpointHost,
            EndpointPort = EndpointPort,
            ToolName = ToolName,
            InvocationTimestampUtc = InvocationTimestampUtc
        }
    };
}

internal sealed class JsonRimBridgeRoute
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; }

    [JsonPropertyName("toolName")]
    public string ToolName { get; set; }

    [JsonPropertyName("operationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string OperationId { get; set; }

    [JsonPropertyName("workflowId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string WorkflowId { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("opaqueEvidence")]
    public JsonElement? OpaqueEvidence { get; set; }

    [JsonPropertyName("provenance")]
    public JsonRimBridgeProvenance Provenance { get; set; }
}

internal sealed class JsonRimBridgeProvenance
{
    [JsonPropertyName("workflowId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string WorkflowId { get; set; }

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("launchId")]
    public string LaunchId { get; set; }

    [JsonPropertyName("profileFingerprint")]
    public string ProfileFingerprint { get; set; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("endpointHost")]
    public string EndpointHost { get; set; }

    [JsonPropertyName("endpointPort")]
    public int EndpointPort { get; set; }

    [JsonPropertyName("toolName")]
    public string ToolName { get; set; }

    [JsonPropertyName("invocationTimestampUtc")]
    public DateTime InvocationTimestampUtc { get; set; }
}

internal sealed class RimBridgeRouteRequest
{
    internal string LeaseId { get; init; }
    internal string ToolName { get; init; }
    internal JsonElement Arguments { get; init; }
}

internal sealed class RimBridgeRouteContext
{
    internal RimBridgeEndpoint Endpoint { get; init; }
    internal string OperationId { get; init; }
    internal string WorkflowId { get; init; }
    internal string LaunchId { get; init; }
    internal int Generation { get; init; }
    internal int ProcessId { get; init; }
    internal string ProfileFingerprint { get; init; }
    internal string LeaseId { get; init; }
    internal string Category { get; init; }
}

internal static class RimBridgeRouteSecurity
{
    internal static string RedactText(string value, string secret)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(secret))
            return value;
        return value.Replace(secret, "[redacted]", StringComparison.Ordinal);
    }

    internal static JsonElement? Redact(JsonElement? value, string secret)
    {
        if (!value.HasValue || value.Value.ValueKind == JsonValueKind.Undefined)
            return null;
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            Write(writer, value.Value, secret ?? string.Empty);
        }
        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, string secret)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name.Contains("token", StringComparison.OrdinalIgnoreCase))
                        writer.WriteStringValue("[redacted]");
                    else
                        Write(writer, property.Value, secret);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                    Write(writer, item, secret);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(value.GetString(), secret));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
