using System.Reflection;
using System.Text.Json.Serialization;

using DevBridge2;

namespace DevBridge.Coordinator;

internal class DevBridgeBuildIdentity
{
    [JsonPropertyName("productVersion")]
    public string ProductVersion { get; init; }

    [JsonPropertyName("informationalVersion")]
    public string InformationalVersion { get; init; }

    [JsonPropertyName("sourceRevision")]
    public string SourceRevision { get; init; }

    [JsonPropertyName("revisionKnown")]
    public bool RevisionKnown { get; init; }

    [JsonPropertyName("dirty")]
    public bool Dirty { get; init; }

    [JsonPropertyName("buildConfiguration")]
    public string BuildConfiguration { get; init; }

    internal static DevBridgeBuildIdentity FromAssembly(Assembly assembly)
    {
        if (assembly == null)
            throw new ArgumentNullException(nameof(assembly));

        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string configuration = assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        string fallbackProductVersion = assembly.GetName().Version?.ToString(3);
        return FromInformationalVersion(informationalVersion, configuration, fallbackProductVersion);
    }

    internal static DevBridgeBuildIdentity FromAssemblyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            // Load from bytes so status/doctor never keep a deployed assembly
            // file open while an operator is replacing the published build.
            return FromAssembly(Assembly.Load(File.ReadAllBytes(Path.GetFullPath(path))));
        }
        catch
        {
            return null;
        }
    }

    internal static DevBridgeBuildIdentity FromInformationalVersion(string informationalVersion,
        string buildConfiguration, string fallbackProductVersion = null)
    {
        string normalized = informationalVersion?.Trim();
        int separator = normalized?.IndexOf('+') ?? -1;
        string productVersion = separator > 0 ? normalized.Substring(0, separator) : normalized;
        string revision = separator >= 0 && separator + 1 < normalized.Length
            ? normalized.Substring(separator + 1)
            : "unknown";
        bool dirty = revision.EndsWith(".dirty", StringComparison.OrdinalIgnoreCase);
        if (dirty)
            revision = revision.Substring(0, revision.Length - ".dirty".Length);

        if (string.IsNullOrWhiteSpace(productVersion))
            productVersion = string.IsNullOrWhiteSpace(fallbackProductVersion)
                ? "unknown"
                : fallbackProductVersion;
        if (string.IsNullOrWhiteSpace(revision))
            revision = "unknown";

        return new DevBridgeBuildIdentity
        {
            ProductVersion = productVersion,
            InformationalVersion = string.IsNullOrWhiteSpace(normalized)
                ? productVersion
                : normalized,
            SourceRevision = revision,
            RevisionKnown = !string.Equals(revision, "unknown", StringComparison.OrdinalIgnoreCase),
            Dirty = dirty,
            BuildConfiguration = string.IsNullOrWhiteSpace(buildConfiguration) ? "unknown" : buildConfiguration
        };
    }
}

internal sealed class CoordinatorBuildIdentity : DevBridgeBuildIdentity
{
    [JsonPropertyName("processStartedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ProcessStartedUtc { get; init; }

    [JsonPropertyName("coordinatorProtocolVersion")]
    public int CoordinatorProtocolVersion { get; init; }

    [JsonPropertyName("protocolContract")]
    public string ProtocolContract { get; init; }

    internal static CoordinatorBuildIdentity Current(DateTime? processStartedUtc = null) =>
        FromAssembly(typeof(CoordinatorState).Assembly, processStartedUtc);

    internal static CoordinatorBuildIdentity FromAssembly(Assembly assembly, DateTime? processStartedUtc = null)
    {
        DevBridgeBuildIdentity metadata = DevBridgeBuildIdentity.FromAssembly(assembly);
        ReadProtocolMetadata(assembly, out int protocolVersion, out string protocolContract);
        return FromMetadata(metadata, processStartedUtc, protocolVersion, protocolContract);
    }

    internal static new CoordinatorBuildIdentity FromAssemblyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            return FromAssembly(Assembly.Load(File.ReadAllBytes(Path.GetFullPath(path))));
        }
        catch
        {
            return null;
        }
    }

    internal static CoordinatorBuildIdentity FromInformationalVersion(string informationalVersion,
        string buildConfiguration, DateTime? processStartedUtc = null, string fallbackProductVersion = null)
    {
        DevBridgeBuildIdentity metadata = DevBridgeBuildIdentity.FromInformationalVersion(
            informationalVersion, buildConfiguration, fallbackProductVersion);
        return FromMetadata(metadata, processStartedUtc,
            DevBridgeSchemaVersions.CoordinatorProtocolMajor,
            DevBridgeSchemaVersions.CoordinatorProtocolContract);
    }

    private static CoordinatorBuildIdentity FromMetadata(DevBridgeBuildIdentity metadata,
        DateTime? processStartedUtc, int protocolVersion, string protocolContract)
    {
        return new CoordinatorBuildIdentity
        {
            ProductVersion = metadata.ProductVersion,
            InformationalVersion = metadata.InformationalVersion,
            SourceRevision = metadata.SourceRevision,
            RevisionKnown = metadata.RevisionKnown,
            Dirty = metadata.Dirty,
            BuildConfiguration = metadata.BuildConfiguration,
            ProcessStartedUtc = processStartedUtc,
            CoordinatorProtocolVersion = protocolVersion,
            ProtocolContract = protocolContract
        };
    }

    private static void ReadProtocolMetadata(Assembly assembly, out int protocolVersion,
        out string protocolContract)
    {
        protocolVersion = DevBridgeSchemaVersions.CoordinatorProtocolMajor;
        protocolContract = DevBridgeSchemaVersions.CoordinatorProtocolContract;
        try
        {
            Type schema = assembly.GetType("DevBridge2.DevBridgeSchemaVersions", throwOnError: false);
            object version = schema?.GetField("CoordinatorProtocolMajor",
                BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue();
            object contract = schema?.GetField("CoordinatorProtocolContract",
                BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue();
            if (version is int value)
                protocolVersion = value;
            if (contract is string valueText && !string.IsNullOrWhiteSpace(valueText))
                protocolContract = valueText;
        }
        catch
        {
            // Assemblies from a pre-v2 deployment may not contain these fields;
            // retain the current compatibility boundary as the safe fallback.
        }
    }

    internal CoordinatorBuildIdentity WithProcessStart(DateTime processStartedUtc) => new()
    {
        ProductVersion = ProductVersion,
        InformationalVersion = InformationalVersion,
        SourceRevision = SourceRevision,
        RevisionKnown = RevisionKnown,
        Dirty = Dirty,
        BuildConfiguration = BuildConfiguration,
        ProcessStartedUtc = processStartedUtc,
        CoordinatorProtocolVersion = CoordinatorProtocolVersion,
        ProtocolContract = ProtocolContract
    };
}
