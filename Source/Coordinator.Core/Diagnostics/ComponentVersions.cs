using System.Text.Json.Serialization;

using DevBridge2;

namespace DevBridge.Coordinator;

internal sealed class ComponentVersionReport
{
    [JsonPropertyName("cliWrapperVersion")]
    public string CliWrapperVersion { get; set; }
    [JsonPropertyName("coordinatorVersion")]
    public string CoordinatorVersion { get; set; }
    [JsonPropertyName("coordinatorBuild")]
    public CoordinatorBuildIdentity CoordinatorBuild { get; set; }
    [JsonPropertyName("publishedCoordinatorBuild")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CoordinatorBuildIdentity PublishedCoordinatorBuild { get; set; }
    [JsonPropertyName("modVersion")]
    public string ModVersion { get; set; }
    [JsonPropertyName("modBuild")]
    public DevBridgeBuildIdentity ModBuild { get; set; }
    [JsonPropertyName("bridgeToolsVersion")]
    public string BridgeToolsVersion { get; set; }
    [JsonPropertyName("bridgeToolsBuild")]
    public DevBridgeBuildIdentity BridgeToolsBuild { get; set; }
    [JsonPropertyName("bridgeToolsPath")]
    public string BridgeToolsPath { get; set; }
    [JsonPropertyName("runtimeStateSchema")]
    public string RuntimeStateSchema { get; set; }
    [JsonPropertyName("readinessSchema")]
    public string ReadinessSchema { get; set; }
    [JsonPropertyName("generatedModsConfigSchema")]
    public string GeneratedModsConfigSchema { get; set; }
    [JsonPropertyName("generationManifestSchema")]
    public string GenerationManifestSchema { get; set; }
    [JsonPropertyName("generationHistorySchema")]
    public string GenerationHistorySchema { get; set; }
    [JsonPropertyName("quicktestFailureSchema")]
    public int QuicktestFailureSchema { get; set; }
    [JsonPropertyName("coordinatorProtocolMajor")]
    public int CoordinatorProtocolMajor { get; set; }
    [JsonPropertyName("modProtocolMajor")]
    public int ModProtocolMajor { get; set; }
    [JsonPropertyName("protocolCompatible")]
    public bool ProtocolCompatible { get; set; }
}

internal static class ComponentVersions
{
    internal static ComponentVersionReport Current => new()
    {
        // The wrapper is a script and has no independently versioned binary.
        CliWrapperVersion = null,
        CoordinatorVersion = CoordinatorBuildIdentity.Current().ProductVersion,
        CoordinatorBuild = CoordinatorBuildIdentity.Current(),
        ModVersion = ProductVersion(),
        ModBuild = null,
        BridgeToolsVersion = ProductVersion(),
        BridgeToolsBuild = null,
        RuntimeStateSchema = DevBridgeSchemaVersions.RuntimeStateContract,
        ReadinessSchema = DevBridgeSchemaVersions.ReadinessContract,
        GeneratedModsConfigSchema = DevBridgeSchemaVersions.GeneratedModsConfigContract,
        GenerationManifestSchema = DevBridgeSchemaVersions.GenerationManifestContract,
        GenerationHistorySchema = DevBridgeSchemaVersions.GenerationHistoryContract,
        QuicktestFailureSchema = QuicktestFailureArtifact.CurrentSchemaVersion,
        CoordinatorProtocolMajor = DevBridgeSchemaVersions.CoordinatorProtocolMajor,
        ModProtocolMajor = 1,
        ProtocolCompatible = true
    };

    internal static string CoordinatorHandshakeVersion() =>
        "DevBridge2.Coordinator/" + ProductVersion();

    internal static string BridgeToolsHandshakeVersion() =>
        "DevBridge2.BridgeTools/" + ProductVersion();

    private static string ProductVersion()
    {
        return CoordinatorBuildIdentity.Current().ProductVersion;
    }
}
