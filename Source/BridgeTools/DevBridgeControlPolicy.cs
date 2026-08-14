using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace DevBridge2.BridgeTools;

public static class DevBridgeControlPolicy
{
    public const string SchemaVersion = "devbridge-control-policy/v1";

    public static DevBridgeControlPolicyPayload Read()
    {
        return Read(null, null);
    }

    public static DevBridgeControlPolicyPayload Read(
        IDictionary<string, string> environment, string statePath)
    {
        IDictionary<string, string> values = environment ?? ReadProcessEnvironment();
        string root = Get(values, "DEVBRIDGE_ROOT");
        string effectiveStatePath = statePath;
        if (string.IsNullOrWhiteSpace(effectiveStatePath) && !string.IsNullOrWhiteSpace(root))
            effectiveStatePath = Path.Combine(root, "Runtime", "state.json");

        PersistedPolicySnapshot state = ReadState(effectiveStatePath);
        if (state == null)
            return Failure("DEVBRIDGE_POLICY_UNAVAILABLE",
                "DevBridge control policy state is not available.");

        PersistedPolicySnapshotPolicy policy = state.RimBridgePolicy ??
            PersistedPolicySnapshotPolicy.CreateDefault();
        string authority = string.IsNullOrWhiteSpace(state.ModsConfigMutationAuthority)
            ? policy.ModsConfigMutationAuthority
            : state.ModsConfigMutationAuthority;

        return new DevBridgeControlPolicyPayload
        {
            Success = true,
            Available = true,
            SchemaVersion = SchemaVersion,
            ReadOnly = true,
            LifecycleOwner = policy.LifecycleOwner ?? "devbridge",
            ModsConfigOwner = policy.ModsConfigOwner ?? "devbridge",
            GenerationOwner = policy.GenerationOwner ?? "devbridge",
            CurrentGeneration = policy.CurrentGeneration != 0 ? policy.CurrentGeneration : state.Generation,
            GenerationOwned = policy.GenerationOwned,
            ProfileFrozen = policy.ProfileFrozen,
            ModsConfigMutationAuthority = authority ?? "NOT_GENERATION_OWNED",
            BlockedOperations = policy.BlockedOperations ?? Defaults.BlockedOperations(),
            OperationCategories = CompleteOperationCategories(policy.OperationCategories),
            ExternalMutation = state.ExternalModsConfigMutation == null ? null :
                new DevBridgeControlPolicyEvidence
                {
                    Generation = state.ExternalModsConfigMutation.Generation,
                    LaunchId = state.ExternalModsConfigMutation.LaunchId,
                    ExpectedFingerprint = state.ExternalModsConfigMutation.ExpectedFingerprint,
                    ObservedFingerprint = state.ExternalModsConfigMutation.ObservedFingerprint,
                    ExpectedProfileFingerprint = state.ExternalModsConfigMutation.ExpectedProfileFingerprint,
                    DetectedUtc = state.ExternalModsConfigMutation.DetectedUtc,
                    Reason = state.ExternalModsConfigMutation.Reason
                }
        };
    }

    private static DevBridgeControlPolicyPayload Failure(string code, string error)
    {
        return new DevBridgeControlPolicyPayload
        {
            Success = false,
            Available = false,
            SchemaVersion = SchemaVersion,
            ReadOnly = true,
            ErrorCode = code,
            Error = error
        };
    }

    private static IDictionary<string, string> ReadProcessEnvironment()
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                values[key] = value;
        }
        return values;
    }

    private static string Get(IDictionary<string, string> values, string name)
    {
        if (values == null)
            return null;
        foreach (KeyValuePair<string, string> entry in values)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                return entry.Value?.Trim();
        }
        return null;
    }

    private static PersistedPolicySnapshot ReadState(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            DataContractJsonSerializer serializer = new(typeof(PersistedPolicySnapshot));
            using FileStream stream = File.OpenRead(path);
            return serializer.ReadObject(stream) as PersistedPolicySnapshot;
        }
        catch
        {
            return null;
        }
    }

    private static class Defaults
    {
        internal static List<string> BlockedOperations() => new()
        {
            "rimworld/set_mod_enabled",
            "rimworld/reorder_mod"
        };

        internal static Dictionary<string, string> OperationCategories() => new()
        {
            ["rimworld/set_mod_enabled"] = "profile-mutation",
            ["rimworld/reorder_mod"] = "profile-mutation",
            ["rimworld/start"] = "lifecycle-mutation",
            ["rimworld/restart"] = "lifecycle-mutation",
            ["rimworld/stop"] = "lifecycle-mutation"
        };
    }

    private static Dictionary<string, string> CompleteOperationCategories(
        IDictionary<string, string> persisted)
    {
        Dictionary<string, string> categories = Defaults.OperationCategories();
        if (persisted != null)
        {
            foreach (KeyValuePair<string, string> entry in persisted)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                    categories[entry.Key] = entry.Value;
            }
        }
        return categories;
    }

    [DataContract]
    private sealed class PersistedPolicySnapshot
    {
        [DataMember(Name = "Generation")] public int Generation { get; set; }
        [DataMember(Name = "ModsConfigMutationAuthority")] public string ModsConfigMutationAuthority { get; set; }
        [DataMember(Name = "ExternalModsConfigMutation")] public PersistedPolicyEvidence ExternalModsConfigMutation { get; set; }
        [DataMember(Name = "RimBridgePolicy")] public PersistedPolicySnapshotPolicy RimBridgePolicy { get; set; }
    }

    [DataContract]
    private sealed class PersistedPolicySnapshotPolicy
    {
        [DataMember(Name = "LifecycleOwner")] public string LifecycleOwner { get; set; }
        [DataMember(Name = "ModsConfigOwner")] public string ModsConfigOwner { get; set; }
        [DataMember(Name = "GenerationOwner")] public string GenerationOwner { get; set; }
        [DataMember(Name = "CurrentGeneration")] public int CurrentGeneration { get; set; }
        [DataMember(Name = "GenerationOwned")] public bool GenerationOwned { get; set; }
        [DataMember(Name = "ProfileFrozen")] public bool ProfileFrozen { get; set; }
        [DataMember(Name = "ModsConfigMutationAuthority")] public string ModsConfigMutationAuthority { get; set; }
        [DataMember(Name = "BlockedOperations")] public List<string> BlockedOperations { get; set; }
        [DataMember(Name = "OperationCategories")] public Dictionary<string, string> OperationCategories { get; set; }

        internal static PersistedPolicySnapshotPolicy CreateDefault() => new()
        {
            LifecycleOwner = "devbridge",
            ModsConfigOwner = "devbridge",
            GenerationOwner = "devbridge",
            BlockedOperations = Defaults.BlockedOperations(),
            OperationCategories = Defaults.OperationCategories()
        };
    }

    [DataContract]
    private sealed class PersistedPolicyEvidence
    {
        [DataMember(Name = "Generation")] public int Generation { get; set; }
        [DataMember(Name = "LaunchId")] public string LaunchId { get; set; }
        [DataMember(Name = "ExpectedFingerprint")] public string ExpectedFingerprint { get; set; }
        [DataMember(Name = "ObservedFingerprint")] public string ObservedFingerprint { get; set; }
        [DataMember(Name = "ExpectedProfileFingerprint")] public string ExpectedProfileFingerprint { get; set; }
        [DataMember(Name = "DetectedUtc")] public DateTime DetectedUtc { get; set; }
        [DataMember(Name = "Reason")] public string Reason { get; set; }
    }
}

[DataContract]
public sealed class DevBridgeControlPolicyPayload
{
    [DataMember(Name = "success")] public bool Success { get; set; }
    [DataMember(Name = "available")] public bool Available { get; set; }
    [DataMember(Name = "schemaVersion")] public string SchemaVersion { get; set; }
    [DataMember(Name = "readOnly")] public bool ReadOnly { get; set; }
    [DataMember(Name = "lifecycleOwner")] public string LifecycleOwner { get; set; }
    [DataMember(Name = "modsConfigOwner")] public string ModsConfigOwner { get; set; }
    [DataMember(Name = "generationOwner")] public string GenerationOwner { get; set; }
    [DataMember(Name = "currentGeneration")] public int CurrentGeneration { get; set; }
    [DataMember(Name = "generationOwned")] public bool GenerationOwned { get; set; }
    [DataMember(Name = "profileFrozen")] public bool ProfileFrozen { get; set; }
    [DataMember(Name = "modsConfigMutationAuthority")] public string ModsConfigMutationAuthority { get; set; }
    [DataMember(Name = "blockedOperations")] public List<string> BlockedOperations { get; set; } = new();
    [DataMember(Name = "operationCategories")] public Dictionary<string, string> OperationCategories { get; set; } = new();
    [DataMember(Name = "externalMutation")] public DevBridgeControlPolicyEvidence ExternalMutation { get; set; }
    [DataMember(Name = "errorCode")] public string ErrorCode { get; set; }
    [DataMember(Name = "error")] public string Error { get; set; }
}

[DataContract]
public sealed class DevBridgeControlPolicyEvidence
{
    [DataMember(Name = "generation")] public int Generation { get; set; }
    [DataMember(Name = "launchId")] public string LaunchId { get; set; }
    [DataMember(Name = "expectedFingerprint")] public string ExpectedFingerprint { get; set; }
    [DataMember(Name = "observedFingerprint")] public string ObservedFingerprint { get; set; }
    [DataMember(Name = "expectedProfileFingerprint")] public string ExpectedProfileFingerprint { get; set; }
    [DataMember(Name = "detectedUtc")] public DateTime DetectedUtc { get; set; }
    [DataMember(Name = "reason")] public string Reason { get; set; }
}
