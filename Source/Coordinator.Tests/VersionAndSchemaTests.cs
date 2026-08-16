using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestComponentVersionsAndSchemas()
    {
        ComponentVersionReport report = ComponentVersions.Current;
        string expectedVersion = report.CoordinatorVersion;
        Assert(report.CliWrapperVersion == null &&
               !string.IsNullOrWhiteSpace(expectedVersion) &&
               report.ModVersion == expectedVersion &&
               report.BridgeToolsVersion == expectedVersion &&
               report.RuntimeStateSchema == "devbridge-runtime-state/v1" &&
               report.ReadinessSchema == "devbridge-readiness/v1" &&
               report.GeneratedModsConfigSchema == "devbridge-generated-mods-config/v1" &&
               report.QuicktestFailureSchema == QuicktestFailureArtifact.CurrentSchemaVersion,
            "component version report must expose the released product and schema versions");

        string about = ReadWorkspaceFile(Path.Combine("About", "About.xml"));
        string changelog = ReadWorkspaceFile("CHANGELOG.md");
        string props = ReadWorkspaceFile(Path.Combine("Source", "Directory.Build.props"));
        string coordinator = ReadWorkspaceFile(Path.Combine("Source", "Coordinator", "DevBridge.Coordinator.csproj"));
        string mod = ReadWorkspaceFile(Path.Combine("Source", "Mod", "DevBridge2.csproj"));
        string bridgeTools = ReadWorkspaceFile(Path.Combine("Source", "BridgeTools", "DevBridge2.BridgeTools.csproj"));
        string handshake = ReadWorkspaceFile(Path.Combine("Source", "Coordinator.Core", "Integrations", "RimBridge", "RimBridgeConnection.cs"));

        Match aboutVersion = Regex.Match(about, "<modVersion>([^<]+)</modVersion>",
            RegexOptions.CultureInvariant);
        Assert(aboutVersion.Success && aboutVersion.Groups[1].Value == expectedVersion &&
               changelog.Contains("## " + expectedVersion, StringComparison.Ordinal) &&
               props.Contains("<DevBridgeProductVersion>" + expectedVersion +
                   "</DevBridgeProductVersion>", StringComparison.Ordinal) &&
               coordinator.Contains("$(DevBridgeProductVersion)", StringComparison.Ordinal) &&
               mod.Contains("$(DevBridgeProductVersion)", StringComparison.Ordinal) &&
               mod.Contains("<GenerateAssemblyInfo>true</GenerateAssemblyInfo>", StringComparison.Ordinal) &&
               bridgeTools.Contains("$(DevBridgeProductVersion)", StringComparison.Ordinal) &&
               bridgeTools.Contains("<GenerateAssemblyInfo>true</GenerateAssemblyInfo>", StringComparison.Ordinal) &&
               !handshake.Contains("1.3." + "0", StringComparison.Ordinal),
            "product metadata and the coordinator handshake must not drift");

        string modSource = ReadWorkspaceFile(Path.Combine("Source", "Mod", "DevBridge2Mod.cs"));
        Assert(modSource.Contains("schemaVersion", StringComparison.Ordinal) &&
               modSource.Contains("DevBridgeSchemaVersions.Readiness", StringComparison.Ordinal),
            "the mod readiness writer must emit the shared readiness schema marker");
    }

    private static void TestPersistedSchemaCompatibility()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 0,
            Phase = BridgePhase.STOPPED
        });
        string statePath = Path.Combine(fixture.Root, "Runtime", "state.json");

        // A pre-schema state is still accepted and upgraded on the next safe save.
        File.WriteAllText(statePath, JsonSerializer.Serialize(new PersistedState
        {
            Generation = 0,
            Phase = BridgePhase.STOPPED
        }, Program.JsonOptions), Encoding.UTF8);
        fixture.State = fixture.Reload();
        PersistedState upgraded = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(statePath), Program.JsonOptions);
        Assert(upgraded != null && upgraded.SchemaVersion == DevBridgeSchemaVersions.RuntimeState,
            "legacy state must be upgraded to the current explicit schema");

        // A newer state is rejected without normalization or overwriting the source artifact.
        const string unsupported = "{\"SchemaVersion\":99,\"Phase\":\"READY\",\"Generation\":7}";
        File.WriteAllText(statePath, unsupported, Encoding.UTF8);
        CoordinatorState blocked = fixture.Reload();
        BridgeRequest request = Request("status");
        request.Json = true;
        List<string> messages = new();
        int exitCode = blocked.Execute(request, messages.Add, () => true);
        JsonCommandResponse response = blocked.CreateJsonResponse(request, exitCode, messages);
        Assert(exitCode == 4 && response.ErrorCode == "PERSISTED_STATE_SCHEMA_UNSUPPORTED" &&
               File.ReadAllText(statePath) == unsupported,
            "unsupported state schema must fail closed with a machine-readable error and preserve the artifact");

        // Generated manifest schema is checked before ownership can be trusted.
        File.WriteAllText(statePath, JsonSerializer.Serialize(new PersistedState
        {
            Generation = 0,
            Phase = BridgePhase.STOPPED
        }, Program.JsonOptions), Encoding.UTF8);
        fixture.State = fixture.Reload();
        string manifestPath = Path.Combine(fixture.Root, "Runtime", "ModsConfig.generated.json");
        File.WriteAllText(manifestPath,
            "{\"SchemaVersion\":99,\"Hash\":\"" + new string('A', 64) + "\",\"Generation\":1}",
            Encoding.UTF8);
        BridgeRequest modsRequest = Request("mods", arguments: new[] { "status" });
        int modsExit = fixture.State.Execute(modsRequest, _ => { }, () => true);
        JsonCommandResponse modsResponse = fixture.State.CreateJsonResponse(
            modsRequest, modsExit, Array.Empty<string>());
        Assert(modsResponse.ErrorCode == "GENERATED_MODS_CONFIG_SCHEMA_UNSUPPORTED" &&
               modsResponse.State == BridgePhase.ERROR.ToString(),
            "unsupported generated-manifest schema must make ModsConfig ownership fail closed");
    }
}
