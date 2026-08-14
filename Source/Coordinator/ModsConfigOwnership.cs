using System.Text.Json.Serialization;

namespace DevBridge.Coordinator;

internal static class ModsConfigMutationAuthorityValues
{
    internal const string ControlledFrozen = "CONTROLLED_FROZEN";
    internal const string DevBridgeTransition = "DEVBRIDGE_TRANSITION";
    internal const string ExternalMutated = "EXTERNAL_MUTATED";
    internal const string NotGenerationOwned = "NOT_GENERATION_OWNED";
}

internal sealed class ModsConfigMutationEvidence
{
    public int Generation { get; set; }
    public string LaunchId { get; set; }
    public string ExpectedFingerprint { get; set; }
    public string ObservedFingerprint { get; set; }
    public string ExpectedProfileFingerprint { get; set; }
    public DateTime DetectedUtc { get; set; }
    public string Reason { get; set; }
}

internal sealed class RimBridgePolicyState
{
    public string LifecycleOwner { get; set; } = "devbridge";
    public string ModsConfigOwner { get; set; } = "devbridge";
    public string GenerationOwner { get; set; } = "devbridge";
    public int CurrentGeneration { get; set; }
    public bool GenerationOwned { get; set; }
    public bool ProfileFrozen { get; set; }
    public string ModsConfigMutationAuthority { get; set; } =
        ModsConfigMutationAuthorityValues.NotGenerationOwned;
    public List<string> BlockedOperations { get; set; } = DefaultBlockedOperations();
    public Dictionary<string, string> OperationCategories { get; set; } = DefaultOperationCategories();

    internal static RimBridgePolicyState CreateDefault() => new();

    internal RimBridgePolicyState Clone() => new()
    {
        LifecycleOwner = LifecycleOwner,
        ModsConfigOwner = ModsConfigOwner,
        GenerationOwner = GenerationOwner,
        CurrentGeneration = CurrentGeneration,
        GenerationOwned = GenerationOwned,
        ProfileFrozen = ProfileFrozen,
        ModsConfigMutationAuthority = ModsConfigMutationAuthority,
        BlockedOperations = (BlockedOperations ?? new List<string>()).ToList(),
        OperationCategories = new Dictionary<string, string>(
            OperationCategories ?? new Dictionary<string, string>(), StringComparer.Ordinal)
    };

    internal static List<string> DefaultBlockedOperations() => new()
    {
        "rimworld/set_mod_enabled",
        "rimworld/reorder_mod"
    };

    internal static Dictionary<string, string> DefaultOperationCategories() => new(StringComparer.Ordinal)
    {
        ["rimworld/set_mod_enabled"] = "profile-mutation",
        ["rimworld/reorder_mod"] = "profile-mutation",
        ["rimworld/start"] = "lifecycle-mutation",
        ["rimworld/restart"] = "lifecycle-mutation",
        ["rimworld/stop"] = "lifecycle-mutation"
    };
}
