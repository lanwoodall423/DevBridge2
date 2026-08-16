#nullable enable

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using DevBridge2;

namespace DevBridge.Coordinator;

internal static class CoordinatorIpcProtocol
{
    internal const string RequestType = "request";
    internal const string EventType = "event";
    internal const string ResultType = "result";
    // These limits are part of the IPC contract. They are deliberately small
    // compared with the named-pipe capacity so malformed input is rejected
    // before it can reach coordinator state or a process launch path.
    internal const int MaxFrameLength = 256 * 1024;
    internal const int MaxRequestLength = 128 * 1024;
    internal const int MaxCommandLength = 128;
    internal const int MaxArgumentCount = 64;
    internal const int MaxArgumentLength = 4096;
    internal const int MaxEventMessageLength = 16 * 1024;
    internal const int MaxBufferedEventCount = 1024;
    internal const int MaxBufferedEventOutputLength = 128 * 1024;
    internal const int MaxOutputPayloadLength = 192 * 1024;
    internal const int MaxRequestIdLength = 128;
    internal const int MaxAgentLength = 256;
    internal const int MaxPathLength = 32768;
    internal const int MaxOpaqueIdLength = 256;

    internal static int Version => DevBridgeSchemaVersions.CoordinatorProtocolMajor;

    internal static string NewRequestId() => Guid.NewGuid().ToString("N");

    internal static bool IsValidRequestId(string requestId) =>
        !string.IsNullOrWhiteSpace(requestId) && requestId.Length <= MaxRequestIdLength &&
        requestId.All(value => !char.IsControl(value));

    internal static CoordinatorIpcFrame Event(string requestId, string message) => new()
    {
        ProtocolVersion = Version,
        RequestId = requestId,
        Type = EventType,
        Message = BoundEventMessage(message)
    };

    internal static CoordinatorIpcFrame Result(string requestId, int exitCode, object? payload,
        CoordinatorBuildIdentity? buildIdentity, CoordinatorBuildIdentity? publishedBuild = null,
        bool? buildMatchesPublished = null) => new()
    {
        ProtocolVersion = Version,
        RequestId = requestId,
        Type = ResultType,
        Payload = BoundedPayload(payload, ref exitCode, buildIdentity, publishedBuild, buildMatchesPublished),
        ExitCode = exitCode,
        CoordinatorBuild = buildIdentity,
        PublishedCoordinatorBuild = publishedBuild,
        CoordinatorBuildMatchesPublished = buildMatchesPublished
    };

    internal static string? ReadFrameLine(StreamReader reader) =>
        ReadFrameLineAsync(reader, CancellationToken.None).GetAwaiter().GetResult();

    internal static async Task<string?> ReadFrameLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] character = new char[1];
        StringBuilder line = new();
        while (true)
        {
            int count = await reader.ReadAsync(character.AsMemory(), cancellationToken);
            if (count == 0)
                return line.Length == 0 ? null : line.ToString();

            if (character[0] == '\n')
                return line.ToString().TrimEnd('\r');

            line.Append(character[0]);
            if (line.Length > MaxFrameLength)
                throw new CoordinatorIpcException("FRAME_TOO_LARGE",
                    "IPC frame exceeded the maximum length.");
        }
    }

    internal static bool TrySerializeFrame(CoordinatorIpcFrame frame, out string? line,
        out string? errorCode)
    {
        line = null;
        errorCode = null;
        try
        {
            line = JsonSerializer.Serialize(frame, Program.JsonOptions);
        }
        catch (JsonException)
        {
            errorCode = "OUTPUT_SERIALIZATION_FAILED";
            return false;
        }

        if (line.Length > MaxFrameLength || Encoding.UTF8.GetByteCount(line) > MaxFrameLength)
        {
            line = null;
            errorCode = "OUTPUT_TOO_LARGE";
            return false;
        }
        return true;
    }

    internal static bool TryDeserializeRequest(string line, out BridgeRequest? request,
        out string? errorCode, out string? error)
    {
        request = null;
        errorCode = "MALFORMED_REQUEST";
        error = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            error = "IPC request frame was empty.";
            return false;
        }
        if (line.Length > MaxRequestLength)
        {
            errorCode = "REQUEST_TOO_LARGE";
            error = "IPC request exceeded the maximum request length.";
            return false;
        }

        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(line, Program.JsonOptions);
        }
        catch (JsonException exception)
        {
            error = "IPC request frame was not valid JSON: " + exception.Message;
            return false;
        }
        catch (NotSupportedException exception)
        {
            error = "IPC request frame used an unsupported JSON shape: " + exception.Message;
            return false;
        }

        if (request == null)
        {
            error = "IPC request frame decoded to null.";
            return false;
        }
        return true;
    }

    internal static bool TryValidateRequest(BridgeRequest? request, out string? errorCode, out string? error)
    {
        errorCode = null;
        error = null;
        if (request == null)
        {
            errorCode = "MALFORMED_REQUEST";
            error = "IPC request frame was null.";
            return false;
        }

        if (request.ProtocolVersion != Version)
        {
            errorCode = "INCOMPATIBLE_PROTOCOL";
            error = "Incompatible coordinator IPC protocol. Client requested v" +
                request.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "; supported v" + Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
            return false;
        }
        if (!string.Equals(request.Type, RequestType, StringComparison.Ordinal))
        {
            errorCode = "MALFORMED_REQUEST";
            error = "IPC request type must be 'request'.";
            return false;
        }
        if (!IsValidRequestId(request.RequestId))
        {
            errorCode = "MALFORMED_REQUEST";
            error = "IPC requestId is required, bounded, and must not contain control characters.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            errorCode = "MALFORMED_REQUEST";
            error = "IPC request command is required.";
            return false;
        }
        if (request.Command.Length > MaxCommandLength)
        {
            errorCode = "COMMAND_TOO_LONG";
            error = "IPC command exceeded the maximum command length.";
            return false;
        }

        if (request.Arguments != null && request.Arguments.Count > MaxArgumentCount)
        {
            errorCode = "ARGUMENT_COUNT_EXCEEDED";
            error = "IPC request exceeded the maximum argument count.";
            return false;
        }
        if (request.Arguments?.Any(value => value == null) == true)
        {
            errorCode = "MALFORMED_REQUEST";
            error = "IPC arguments must be strings.";
            return false;
        }
        if (request.Arguments?.Any(value => value.Length > MaxArgumentLength) == true)
        {
            errorCode = "ARGUMENT_TOO_LONG";
            error = "IPC argument exceeded the maximum argument length.";
            return false;
        }
        if (!Bounded(request.Agent, MaxAgentLength) || !Bounded(request.CoordinatorRoot, MaxPathLength))
        {
            errorCode = "REQUEST_METADATA_TOO_LARGE";
            error = "IPC request metadata exceeded its maximum length.";
            return false;
        }
        if (!Bounded(request.RuntimeSlotId, MaxOpaqueIdLength) ||
            !Bounded(request.TicketId, MaxOpaqueIdLength) ||
            !Bounded(request.GoalId, MaxOpaqueIdLength) ||
            !Bounded(request.WakeId, MaxOpaqueIdLength) ||
            !Bounded(request.McpRequestId, MaxOpaqueIdLength) ||
            !Bounded(request.SessionId, MaxOpaqueIdLength))
        {
            errorCode = "REQUEST_METADATA_TOO_LARGE";
            error = "IPC request identity metadata exceeded its maximum length.";
            return false;
        }
        return true;
    }

    internal static bool TryValidateResponse(CoordinatorIpcFrame? frame, string requestId,
        bool terminalSeen, out string? error)
    {
        error = null;
        if (frame == null)
        {
            error = "coordinator returned a null IPC frame";
            return false;
        }
        if (frame.ProtocolVersion != Version)
        {
            error = "coordinator returned unsupported IPC protocol version " + frame.ProtocolVersion +
                "; expected " + Version;
            return false;
        }
        if (!string.Equals(frame.RequestId, requestId, StringComparison.Ordinal))
        {
            error = "coordinator IPC response requestId did not match the request";
            return false;
        }
        if (terminalSeen)
        {
            error = "coordinator returned a duplicate terminal result";
            return false;
        }
        if (string.Equals(frame.Type, EventType, StringComparison.Ordinal))
        {
            if (frame.Message == null)
            {
                error = "coordinator event frame did not contain a message";
                return false;
            }
            if (frame.Message.Length > MaxEventMessageLength)
            {
                error = "coordinator event exceeded the maximum message length";
                return false;
            }
            return true;
        }
        if (string.Equals(frame.Type, ResultType, StringComparison.Ordinal))
        {
            if (!frame.ExitCode.HasValue)
            {
                error = "coordinator result frame did not contain an exitCode";
                return false;
            }
            if (frame.Payload.HasValue && frame.Payload.Value.GetRawText().Length > MaxOutputPayloadLength)
            {
                error = "coordinator result exceeded the maximum payload length";
                return false;
            }
            return true;
        }

        error = "coordinator returned unknown IPC frame type '" + (frame.Type ?? "") + "'";
        return false;
    }

    internal static JsonCommandResponse ProtocolFailure(string command, string errorCode, string error,
        CoordinatorBuildIdentity? buildIdentity, CoordinatorBuildIdentity? publishedBuild = null,
        bool? buildMatchesPublished = null)
    {
        return new JsonCommandResponse
        {
            Success = false,
            Command = command ?? "protocol",
            ExitCode = 2,
            State = BridgePhase.ERROR.ToString(),
            ErrorCode = errorCode,
            Error = error,
            NextAction = "Update the client and coordinator together; supported coordinator IPC protocol is v" +
                Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
            CoordinatorBuild = buildIdentity,
            PublishedCoordinatorBuild = publishedBuild,
            CoordinatorBuildMatchesPublished = buildMatchesPublished
        };
    }

    private static bool Bounded(string? value, int maximum) => value == null || value.Length <= maximum;

    internal static string BoundEventMessage(string message)
    {
        string value = message ?? string.Empty;
        if (value.Length <= MaxEventMessageLength)
            return value;
        const string suffix = "\n[output truncated by coordinator IPC limit]";
        return value[..(MaxEventMessageLength - suffix.Length)] + suffix;
    }

    private static JsonElement? BoundedPayload(object? payload, ref int exitCode,
        CoordinatorBuildIdentity? buildIdentity, CoordinatorBuildIdentity? publishedBuild,
        bool? buildMatchesPublished)
    {
        if (payload == null)
            return null;

        JsonElement result = JsonSerializer.SerializeToElement(payload, Program.JsonOptions);
        if (result.GetRawText().Length <= MaxOutputPayloadLength)
            return result;

        exitCode = 2;
        return JsonSerializer.SerializeToElement(
            ProtocolFailure("protocol", "OUTPUT_TOO_LARGE",
                "The coordinator result exceeded the maximum payload length.", buildIdentity,
                publishedBuild, buildMatchesPublished), Program.JsonOptions);
    }
}

internal sealed class CoordinatorIpcFrame
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; set; }

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; set; }

    [JsonPropertyName("coordinatorBuild")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoordinatorBuildIdentity? CoordinatorBuild { get; set; }

    [JsonPropertyName("publishedCoordinatorBuild")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoordinatorBuildIdentity? PublishedCoordinatorBuild { get; set; }

    [JsonPropertyName("coordinatorBuildMatchesPublished")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CoordinatorBuildMatchesPublished { get; set; }
}

internal sealed class CoordinatorIpcException : IOException
{
    internal CoordinatorIpcException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    internal string ErrorCode { get; }
}
