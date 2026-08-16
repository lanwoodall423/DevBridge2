using System.Text.Json;

namespace DevBridge.Coordinator;

internal interface IRimBridgeClient
{
    RimBridgeWireResult ListTools(RimBridgeEndpoint endpoint, string expectedLaunchId,
        TimeSpan timeout);

    RimBridgeWireResult CallTool(RimBridgeEndpoint endpoint, string expectedLaunchId,
        string toolName, JsonElement arguments, TimeSpan timeout);
}
internal interface IRimBridgeGenerationVerifier
{
    RimBridgeCompanionVerification Verify(RimBridgeEndpoint endpoint, string expectedLaunchId,
        int expectedGeneration, int expectedProcessId, TimeSpan timeout);
}

internal sealed class RimBridgeCompanionGenerationVerifier : IRimBridgeGenerationVerifier
{
    public RimBridgeCompanionVerification Verify(RimBridgeEndpoint endpoint, string expectedLaunchId,
        int expectedGeneration, int expectedProcessId, TimeSpan timeout) =>
        RimBridgeCompanionClient.Verify(endpoint, expectedLaunchId, expectedGeneration,
            expectedProcessId, timeout);
}
