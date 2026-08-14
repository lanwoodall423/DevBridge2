namespace DevBridge2;

/// <summary>
/// Version markers for durable files shared by the coordinator and the mod.
/// Missing markers are treated as the supported legacy format; newer markers
/// are never interpreted as an older format.
/// </summary>
public static class DevBridgeSchemaVersions
{
    public const int RuntimeState = 1;
    public const int Readiness = 1;
    public const int GeneratedModsConfig = 1;
    public const int Doctor = 1;
    public const int GenerationManifest = 1;
    public const int GenerationHistory = 1;

    public const string RuntimeStateContract = "devbridge-runtime-state/v1";
    public const string ReadinessContract = "devbridge-readiness/v1";
    public const string GeneratedModsConfigContract = "devbridge-generated-mods-config/v1";
    public const string DoctorContract = "devbridge-doctor/v1";
    public const string GenerationManifestContract = "devbridge-generation-manifest/v1";
    public const string GenerationHistoryContract = "devbridge-generation-history/v1";
}
