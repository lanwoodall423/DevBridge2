using System.Net.Sockets;
using System.Text.Json;

namespace DevBridge.Coordinator;

internal sealed class RimBridgeClient : IRimBridgeClient
{
    public RimBridgeWireResult ListTools(RimBridgeEndpoint endpoint, string expectedLaunchId,
        TimeSpan timeout)
    {
        return Execute(endpoint, expectedLaunchId, timeout, connection =>
            connection.Request("tools/list", new Dictionary<string, object>()),
            "tools/list");
    }

    public RimBridgeWireResult CallTool(RimBridgeEndpoint endpoint, string expectedLaunchId,
        string toolName, JsonElement arguments, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(toolName) || toolName.Contains(' '))
            return Failure("RIMBRIDGE_INVALID_TOOL_NAME", "RimBridge tool names must be non-empty canonical names.");

        return Execute(endpoint, expectedLaunchId, timeout, connection =>
            connection.Request("tools/call", new Dictionary<string, object>
            {
                ["name"] = toolName,
                ["arguments"] = arguments.ValueKind == JsonValueKind.Undefined
                    ? JsonDocument.Parse("{}").RootElement.Clone()
                    : arguments
            }), "tools/call");
    }

    private static RimBridgeWireResult Execute(RimBridgeEndpoint endpoint, string expectedLaunchId,
        TimeSpan timeout, Func<RimBridgeConnection, JsonDocument> request, string method)
    {
        try
        {
            using RimBridgeConnection connection = RimBridgeConnection.Open(endpoint, expectedLaunchId, timeout);
            using JsonDocument response = request(connection);
            JsonElement root = response.RootElement;
            if (root.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                return ProtocolError(error, method, root, endpoint?.Token);
            }

            if (!root.TryGetProperty("result", out JsonElement result))
                return Failure("RIMBRIDGE_PROTOCOL_ERROR",
                    "RimBridge returned no result for " + method + ".", root);

            return new RimBridgeWireResult
            {
                Success = true,
                Payload = result.Clone(),
                RawResponse = root.Clone()
            };
        }
        catch (RimBridgeProtocolException exception)
        {
            return new RimBridgeWireResult
            {
                ErrorCode = exception.Code,
                Error = RimBridgeRouteSecurity.RedactText(exception.Message, endpoint?.Token),
                AuthenticationFailure = exception.AuthenticationFailure
            };
        }
        catch (TimeoutException)
        {
            return new RimBridgeWireResult
            {
                ErrorCode = "RIMBRIDGE_CALL_TIMEOUT",
                Error = "RimBridge did not complete the request within the bounded timeout.",
                Timeout = true
            };
        }
        catch (SocketException)
        {
            return Failure("RIMBRIDGE_ENDPOINT_UNAVAILABLE",
                "RimBridge did not accept the routed connection.");
        }
        catch (IOException)
        {
            return Failure("RIMBRIDGE_PROTOCOL_ERROR",
                "RimBridge closed the routed connection before completing the request.");
        }
        catch (JsonException)
        {
            return Failure("RIMBRIDGE_PROTOCOL_ERROR", "RimBridge returned invalid protocol JSON.");
        }
        catch
        {
            return Failure("RIMBRIDGE_PROTOCOL_ERROR", "RimBridge routed call failed unexpectedly.");
        }
    }

    private static RimBridgeWireResult ProtocolError(JsonElement error, string method,
        JsonElement root, string token)
    {
        int code = error.TryGetProperty("code", out JsonElement codeValue) &&
                   codeValue.TryGetInt32(out int parsed) ? parsed : 0;
        string message = error.TryGetProperty("message", out JsonElement messageValue) &&
                         messageValue.ValueKind == JsonValueKind.String
            ? messageValue.GetString()
            : "RimBridge rejected " + method + ".";
        if (code == -31000)
            return new RimBridgeWireResult
            {
                ErrorCode = "RIMBRIDGE_AUTH_FAILED",
                Error = "RimBridge rejected the bridge credentials.",
                AuthenticationFailure = true,
                RawResponse = root.Clone()
            };
        if (code == -32601)
            return new RimBridgeWireResult
            {
                ErrorCode = method == "tools/call" ? "RIMBRIDGE_TOOL_NOT_FOUND" : "RIMBRIDGE_PROTOCOL_ERROR",
                Error = method == "tools/call" ? "RimBridge did not find the requested tool." :
                    RimBridgeRouteSecurity.RedactText(message, token),
                RawResponse = root.Clone()
            };
        if (code == -32602)
            return new RimBridgeWireResult
            {
                ErrorCode = "RIMBRIDGE_INVALID_ARGUMENTS",
                Error = "RimBridge rejected the tool arguments.",
                RawResponse = root.Clone()
            };
        return new RimBridgeWireResult
        {
            ErrorCode = "RIMBRIDGE_PROTOCOL_ERROR",
            Error = RimBridgeRouteSecurity.RedactText(
                "RimBridge rejected the routed request: " + message, token),
            RawResponse = root.Clone()
        };
    }

    private static RimBridgeWireResult Failure(string code, string error, JsonElement root = default) => new()
    {
        ErrorCode = code,
        Error = error,
        RawResponse = root.ValueKind == JsonValueKind.Undefined ? null : root.Clone()
    };
}
