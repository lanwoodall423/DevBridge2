#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBridge.Coordinator;

/// <summary>
/// The deliberately small client-side compatibility boundary for the
/// RimBridgeServer GABP endpoint. Keep this contract aligned with the
/// machine-readable RimBridgeProtocolCompatibility.json file.
/// </summary>
internal static class RimBridgeProtocolContract
{
    internal const string EnvelopeVersion = "gabp/1";
    internal const string RequestType = "request";
    internal const string ResponseType = "response";
    internal const string EventType = "event";
    internal const int GabpMajor = 1;
    internal const int BridgeToolsSdkMajor = 2;
    internal const string BridgeToolsSdkPackageVersion = "2.0.0";
    internal const string TestedRimBridgeServerVersions = "none; live smoke verification is local/release-only";
    internal const bool CompanionIsOptional = true;
    internal const int MaxHeaderBytes = 8192;
    internal const int MaxMessageBytes = 16 * 1024 * 1024;
    internal const int MaxCompanionMessageBytes = 1024 * 1024;

    internal const int AuthenticationFailed = -31000;
    internal const int MethodNotFound = -32601;
    internal const int InvalidParams = -32602;
    internal const int ToolNotFound = -31002;

    internal const string ProtocolVersionUnsupportedCode =
        "RIMBRIDGE_PROTOCOL_VERSION_UNSUPPORTED";
    internal const string ResponseIdMismatchCode = "RIMBRIDGE_RESPONSE_ID_MISMATCH";
    internal const string InvalidResponseCode = "RIMBRIDGE_INVALID_RESPONSE";

    internal static GabpRequestEnvelope Request(string method, string id, object? parameters) => new()
    {
        Version = EnvelopeVersion,
        Type = RequestType,
        Id = id,
        Method = method,
        Parameters = parameters ?? new GabpEmptyParameters()
    };

    internal static GabpSessionHelloParams SessionHello(string token, string bridgeVersion,
        string platform, string launchId, bool includeClientInfo = true,
        string clientName = "DevBridge2.Coordinator") => new()
    {
        Token = token,
        BridgeVersion = bridgeVersion,
        Platform = platform,
        LaunchId = launchId,
        ClientInfo = includeClientInfo ? new GabpClientInfo
        {
            Name = clientName,
            Version = bridgeVersion
        } : null
    };

    internal static GabpToolsCallParams ToolsCall(string name, JsonElement arguments) => new()
    {
        Name = name,
        Arguments = arguments.ValueKind == JsonValueKind.Undefined ? EmptyObject() : arguments.Clone()
    };

    private static JsonElement EmptyObject()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    internal static GabpResponseEnvelope ParseResponse(JsonElement root, string expectedId)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new RimBridgeProtocolException(InvalidResponseCode,
                "RimBridge returned a non-object response envelope.");

        string? version = ReadString(root, "v");
        if (!string.Equals(version, EnvelopeVersion, StringComparison.Ordinal))
            throw new RimBridgeProtocolException(ProtocolVersionUnsupportedCode,
                "RimBridge returned an unsupported GABP protocol version.");

        if (!string.Equals(ReadString(root, "type"), ResponseType, StringComparison.Ordinal))
            throw new RimBridgeProtocolException(InvalidResponseCode,
                "RimBridge returned a non-response GABP envelope.");

        string? responseId = ReadString(root, "id");
        if (string.IsNullOrWhiteSpace(responseId) ||
            !string.Equals(responseId, expectedId, StringComparison.Ordinal))
            throw new RimBridgeProtocolException(ResponseIdMismatchCode,
                "RimBridge returned a response for a different request.");

        bool hasResult = root.TryGetProperty("result", out _);
        bool hasErrorProperty = root.TryGetProperty("error", out JsonElement error);
        if (hasErrorProperty && error.ValueKind != JsonValueKind.Object)
            throw new RimBridgeProtocolException(InvalidResponseCode,
                "RimBridge returned a non-object error envelope.");
        bool hasError = hasErrorProperty;
        if (hasResult == hasError)
            throw new RimBridgeProtocolException(InvalidResponseCode,
                "RimBridge response must contain exactly one result or error.");

        GabpResponseEnvelope? response;
        try
        {
            response = JsonSerializer.Deserialize<GabpResponseEnvelope>(
                root.GetRawText(), CoordinatorSerialization.JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new RimBridgeProtocolException(InvalidResponseCode,
                "RimBridge returned an invalid response envelope: " + exception.Message);
        }

        if (response == null)
            throw new RimBridgeProtocolException(InvalidResponseCode,
                "RimBridge returned an empty response envelope.");
        response.RawResponse = root.Clone();
        return response;
    }

    internal static bool IsEvent(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        string.Equals(ReadString(root, "type"), EventType, StringComparison.Ordinal);

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed class GabpEmptyParameters
{
}

internal sealed class GabpRequestEnvelope
{
    [JsonPropertyName("v")]
    public string? Version { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public object? Parameters { get; set; }
}

internal sealed class GabpResponseEnvelope
{
    [JsonPropertyName("v")]
    public string? Version { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public GabpErrorEnvelope? Error { get; set; }

    [JsonIgnore]
    public JsonElement RawResponse { get; set; }
}

internal sealed class GabpErrorEnvelope
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

internal sealed class GabpClientInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

internal sealed class GabpSessionHelloParams
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("bridgeVersion")]
    public string? BridgeVersion { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("launchId")]
    public string? LaunchId { get; set; }

    [JsonPropertyName("clientInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GabpClientInfo? ClientInfo { get; set; }
}

internal sealed class GabpToolsListParams
{
}

internal sealed class GabpToolsCallParams
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }
}
