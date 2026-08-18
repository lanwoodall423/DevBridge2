using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestRimBridgeProtocolCompatibilityContract()
    {
        using JsonDocument contract = JsonDocument.Parse(
            ReadWorkspaceFile("RimBridgeProtocolCompatibility.json"));
        JsonElement root = contract.RootElement;
        JsonElement gabp = root.GetProperty("gabp");
        JsonElement server = root.GetProperty("rimBridgeServer");
        JsonElement bridgeTools = root.GetProperty("bridgeTools");

        JsonElement testedVersions = server.GetProperty("testedVersions");
        Assert(testedVersions.ValueKind == JsonValueKind.Array &&
               testedVersions.EnumerateArray().All(IsValidTestedVersionRecord) &&
               root.GetProperty("contractVersion").GetInt32() == 1 &&
               gabp.GetProperty("major").GetInt32() == RimBridgeProtocolContract.GabpMajor &&
               gabp.GetProperty("envelopeVersion").GetString() ==
                   RimBridgeProtocolContract.EnvelopeVersion &&
               bridgeTools.GetProperty("sdkPackage").GetString() == "RimBridgeServer.Sdk" &&
               bridgeTools.GetProperty("sdkPackageVersion").GetString() ==
                   RimBridgeProtocolContract.BridgeToolsSdkPackageVersion &&
               bridgeTools.GetProperty("compileTested").GetBoolean() &&
               !bridgeTools.GetProperty("runtimeAssemblyBundled").GetBoolean() &&
               root.GetProperty("companion").GetProperty("optional").GetBoolean() ==
                   RimBridgeProtocolContract.CompanionIsOptional,
            "the machine-readable GABP compatibility contract drifted from the typed boundary");

        string project = ReadWorkspaceFile(Path.Combine("Source", "BridgeTools",
            "DevBridge2.BridgeTools.csproj"));
        Match sdkVersion = Regex.Match(project,
            "<PackageReference\\s+Include=\"RimBridgeServer\\.Sdk\"\\s+Version=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert(sdkVersion.Success && sdkVersion.Groups[1].Value ==
                   RimBridgeProtocolContract.BridgeToolsSdkPackageVersion,
            "BridgeTools SDK package version must be updated together with compatibility metadata");
    }

    private static bool IsValidTestedVersionRecord(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        string[] required =
        [
            "rimWorldVersion",
            "rimBridgeServerVersion",
            "rimBridgeServerSdkVersion",
            "devBridge2Commit",
            "verifiedAtUtc",
            "result"
        ];
        return required.All(name =>
            value.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString())) &&
            string.Equals(value.GetProperty("result").GetString(), "pass",
                StringComparison.OrdinalIgnoreCase);
    }

    private static void TestRimBridgeProtocolTypedFixtures()
    {
        string helloId = "hello-fixture";
        string coordinatorVersion = ComponentVersions.CoordinatorHandshakeVersion();
        GabpRequestEnvelope helloRequest = RimBridgeProtocolContract.Request(
            "session/hello", helloId, RimBridgeProtocolContract.SessionHello(
                "fixture-token", coordinatorVersion, "RimWorld", "launch-fixture"));
        using JsonDocument helloJson = JsonDocument.Parse(JsonSerializer.Serialize(
            helloRequest, Program.JsonOptions));
        JsonElement helloParams = helloJson.RootElement.GetProperty("params");
        JsonElement clientInfo = helloParams.GetProperty("clientInfo");
        Assert(helloParams.GetProperty("token").GetString() == "fixture-token" &&
               clientInfo.GetProperty("name").GetString() == "DevBridge2.Coordinator" &&
               clientInfo.GetProperty("version").GetString() == coordinatorVersion,
            "session/hello must serialize the typed GabpClientInfo shape");

        GabpRequestEnvelope optionalHello = RimBridgeProtocolContract.Request(
            "session/hello", "optional-hello", RimBridgeProtocolContract.SessionHello(
                "fixture-token", coordinatorVersion, "RimWorld", "launch-fixture",
                includeClientInfo: false));
        using JsonDocument optionalJson = JsonDocument.Parse(JsonSerializer.Serialize(
            optionalHello, Program.JsonOptions));
        Assert(!optionalJson.RootElement.GetProperty("params").TryGetProperty("clientInfo",
                out _), "optional session/hello clientInfo must be omittable");

        GabpRequestEnvelope listRequest = RimBridgeProtocolContract.Request(
            "tools/list", "list-fixture", new GabpToolsListParams());
        using JsonDocument listJson = JsonDocument.Parse(JsonSerializer.Serialize(
            listRequest, Program.JsonOptions));
        Assert(listJson.RootElement.GetProperty("params").ValueKind == JsonValueKind.Object &&
               !listJson.RootElement.GetProperty("params").EnumerateObject().Any(),
            "tools/list must use an explicit empty typed parameter object");

        using JsonDocument arguments = JsonDocument.Parse("{\"value\":7}");
        GabpRequestEnvelope callRequest = RimBridgeProtocolContract.Request(
            "tools/call", "call-fixture", RimBridgeProtocolContract.ToolsCall(
                "rimworld/get_game_state", arguments.RootElement));
        using JsonDocument callJson = JsonDocument.Parse(JsonSerializer.Serialize(
            callRequest, Program.JsonOptions));
        JsonElement callParams = callJson.RootElement.GetProperty("params");
        Assert(callParams.GetProperty("name").GetString() == "rimworld/get_game_state" &&
               callParams.GetProperty("arguments").GetProperty("value").GetInt32() == 7,
            "tools/call must use the typed name and JSON-object arguments shape");

        using JsonDocument responseJson = JsonDocument.Parse(
            "{\"v\":\"gabp/1\",\"type\":\"response\",\"id\":\"call-fixture\",\"result\":{\"ok\":true}}" );
        GabpResponseEnvelope response = RimBridgeProtocolContract.ParseResponse(
            responseJson.RootElement, "call-fixture");
        Assert(response.Result.HasValue && response.Result.Value.GetProperty("ok").GetBoolean(),
            "typed response envelope must retain a successful result");

        using JsonDocument nullableErrorResponseJson = JsonDocument.Parse(
            "{\"v\":\"gabp/1\",\"type\":\"response\",\"id\":\"call-fixture\",\"result\":{\"ok\":true},\"error\":null}");
        GabpResponseEnvelope nullableErrorResponse = RimBridgeProtocolContract.ParseResponse(
            nullableErrorResponseJson.RootElement, "call-fixture");
        Assert(nullableErrorResponse.Result.HasValue && nullableErrorResponse.Error == null,
            "a successful response with a nullable error field must remain valid");

        try
        {
            using JsonDocument invalid = JsonDocument.Parse(
                "{\"v\":\"gabp/1\",\"type\":\"response\",\"id\":\"call-fixture\"}");
            RimBridgeProtocolContract.ParseResponse(invalid.RootElement, "call-fixture");
            throw new InvalidOperationException("invalid response was accepted");
        }
        catch (RimBridgeProtocolException exception)
        {
            Assert(exception.Code == RimBridgeProtocolContract.InvalidResponseCode,
                "invalid typed response must have a stable protocol error code");
        }
    }

    private static void TestRimBridgeProtocolWireFixtures()
    {
        RimBridgeWireResult list = RunWireCase((stream, helloId) =>
        {
            WriteTestFrame(stream, new
            {
                v = RimBridgeProtocolContract.EnvelopeVersion,
                type = RimBridgeProtocolContract.ResponseType,
                id = helloId,
                result = new { agentId = "fixture-agent" }
            });
            using JsonDocument request = JsonDocument.Parse(ReadTestFrame(stream));
            Assert(request.RootElement.GetProperty("method").GetString() == "tools/list",
                "tools/list fixture must send the canonical method");
            string id = request.RootElement.GetProperty("id").GetString();
            WriteTestFrame(stream, new
            {
                v = RimBridgeProtocolContract.EnvelopeVersion,
                type = RimBridgeProtocolContract.ResponseType,
                id,
                result = new
                {
                    tools = new[] { new
                    {
                        name = "rimworld/get_game_state",
                        description = "fixture",
                        inputSchema = new { type = "object" },
                        outputSchema = new { type = "object" }
                    } }
                }
            });
        }, TimeSpan.FromSeconds(2));
        Assert(list.Success && list.Payload.HasValue &&
               list.Payload.Value.GetProperty("tools").ValueKind == JsonValueKind.Array,
            "tools/list must accept the canonical tools array result");

        RimBridgeWireResult call = RunWireCase((stream, helloId) =>
        {
            WriteTestFrame(stream, new
            {
                v = RimBridgeProtocolContract.EnvelopeVersion,
                type = RimBridgeProtocolContract.ResponseType,
                id = helloId,
                result = new { agentId = "fixture-agent" }
            });
            using JsonDocument request = JsonDocument.Parse(ReadTestFrame(stream));
            Assert(request.RootElement.GetProperty("method").GetString() == "tools/call" &&
                   request.RootElement.GetProperty("params").GetProperty("name").GetString() ==
                       "rimworld/get_game_state" &&
                   request.RootElement.GetProperty("params").GetProperty("arguments").ValueKind ==
                       JsonValueKind.Object,
                "tools/call fixture must send the canonical request shape");
            string id = request.RootElement.GetProperty("id").GetString();
            WriteTestFrame(stream, new
            {
                v = RimBridgeProtocolContract.EnvelopeVersion,
                type = RimBridgeProtocolContract.ResponseType,
                id,
                result = new { state = "ready" }
            });
        }, TimeSpan.FromSeconds(2), call: true);
        Assert(call.Success && call.Payload.HasValue &&
               call.Payload.Value.GetProperty("state").GetString() == "ready",
            "tools/call must accept the canonical result envelope");

        RimBridgeWireResult invalid = RunWireCase((stream, helloId) =>
        {
            WriteTestFrame(stream, new
            {
                v = RimBridgeProtocolContract.EnvelopeVersion,
                type = RimBridgeProtocolContract.ResponseType,
                id = helloId
            });
        }, TimeSpan.FromSeconds(2));
        Assert(invalid.ErrorCode == RimBridgeProtocolContract.InvalidResponseCode,
            "invalid GABP responses must be rejected before route success");

        RimBridgeWireResult wrongVersion = RunWireCase((stream, helloId) =>
        {
            WriteTestFrame(stream, new
            {
                v = "gabp/99", type = RimBridgeProtocolContract.ResponseType,
                id = helloId, result = new { agentId = "wrong-version" }
            });
        }, TimeSpan.FromSeconds(2));
        Assert(wrongVersion.ErrorCode == RimBridgeProtocolContract.ProtocolVersionUnsupportedCode,
            "unsupported GABP envelope versions must fail immediately");

        RimBridgeWireResult wrongId = RunWireCase((stream, helloId) =>
        {
            WriteTestFrame(stream, new
            {
                v = RimBridgeProtocolContract.EnvelopeVersion,
                type = RimBridgeProtocolContract.ResponseType,
                id = "not-the-request-id",
                result = new { agentId = "wrong-id" }
            });
        }, TimeSpan.FromSeconds(2));
        Assert(wrongId.ErrorCode == RimBridgeProtocolContract.ResponseIdMismatchCode,
            "a response for another request must not be silently ignored");

        RimBridgeWireResult invalidList = RunWireCase((stream, helloId) =>
        {
            WriteTestFrame(stream, new
            {
                v = RimBridgeProtocolContract.EnvelopeVersion,
                type = RimBridgeProtocolContract.ResponseType,
                id = helloId,
                result = new { agentId = "fixture-agent" }
            });
            string listId = FrameId(ReadTestFrame(stream));
            WriteTestFrame(stream, new
            {
                v = RimBridgeProtocolContract.EnvelopeVersion,
                type = RimBridgeProtocolContract.ResponseType,
                id = listId,
                result = new { notTools = true }
            });
        }, TimeSpan.FromSeconds(2));
        Assert(invalidList.ErrorCode == "RIMBRIDGE_PROTOCOL_ERROR",
            "tools/list must reject a result without the required tools array");
    }

    private static void TestRimBridgeProtocolFramingFailures()
    {
        string duplicateLength = "Content-Length: 2\r\nContent-Length: 2\r\n" +
            "Content-Type: application/json\r\n\r\n{}";
        string missingLength = "Content-Type: application/json\r\n\r\n{}";
        string oversizedHeader = new string('X', RimBridgeProtocolContract.MaxHeaderBytes + 1);
        string oversizedBody = "Content-Length: " +
            (RimBridgeProtocolContract.MaxMessageBytes + 1) +
            "\r\nContent-Type: application/json\r\n\r\n";
        string invalidJson = "Content-Length: 8\r\nContent-Type: application/json\r\n\r\nnot-json";

        foreach (string frame in new[] { duplicateLength, missingLength, oversizedHeader,
                     oversizedBody, invalidJson })
        {
            RimBridgeWireResult result = RunRawResponseCase(frame);
            Assert(result.ErrorCode == "RIMBRIDGE_PROTOCOL_ERROR",
                "malformed or oversized GABP framing must map to a bounded protocol error");
        }
    }

    private static RimBridgeWireResult RunRawResponseCase(string rawFrame)
    {
        return RunWireCase((stream, helloId) =>
        {
            WriteTestFrame(stream, new
            {
                v = RimBridgeProtocolContract.EnvelopeVersion,
                type = RimBridgeProtocolContract.ResponseType,
                id = helloId,
                result = new { agentId = "fixture-agent" }
            });
            ReadTestFrame(stream);
            byte[] bytes = Encoding.ASCII.GetBytes(rawFrame);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }, TimeSpan.FromSeconds(2));
    }

    private static void TestRimBridgeProtocolCompanionFailures()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception serverFailure = null;
        Task server = Task.Run(() =>
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                using NetworkStream stream = client.GetStream();
                string helloId = FrameId(ReadTestFrame(stream));
                WriteTestFrame(stream, new
                {
                    v = RimBridgeProtocolContract.EnvelopeVersion,
                    type = RimBridgeProtocolContract.ResponseType,
                    id = helloId,
                    error = new { code = RimBridgeProtocolContract.AuthenticationFailed,
                        message = "companion-secret-must-not-escape" }
                });
            }
            catch (Exception exception)
            {
                serverFailure = exception;
            }
        });

        try
        {
            RimBridgeCompanionVerification result = RimBridgeCompanionClient.Verify(
                new RimBridgeEndpoint
                {
                    Host = "127.0.0.1",
                    Port = port,
                    Token = "companion-secret",
                    LaunchId = "wire-launch",
                    Generation = 1,
                    ProcessId = 101,
                    ProcessStartUtcTicks = 1001,
                    DiscoveredUtc = ClockStart
                }, "wire-launch", 1, 101, TimeSpan.FromSeconds(2));
            Assert(result.Status == RimBridgeCompanionVerificationStatus.Invalid &&
                   result.Code == RimBridgeIntegrationConstants.AuthFailedCode &&
                   !result.Error.Contains("secret", StringComparison.OrdinalIgnoreCase),
                "companion authentication failures must be bounded and secret-safe");
        }
        finally
        {
            try { server.Wait(TimeSpan.FromSeconds(2)); } catch { }
            listener.Stop();
            if (serverFailure != null)
                throw new InvalidOperationException("companion authentication fixture failed",
                    serverFailure);
        }
    }
}
