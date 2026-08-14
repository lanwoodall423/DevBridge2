using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml.Linq;

namespace DevBridge2.BridgeTools;

public static class DevBridgeGenerationContext
{
    public const string SchemaVersion = "devbridge-generation-context/v1";
    public const string IntegrationSchemaVersion = "rimbridge-integration/v1";

    public static DevBridgeGenerationContextPayload Read()
    {
        return Read(null, null, null, null);
    }

    public static DevBridgeGenerationContextPayload Read(
        IDictionary<string, string> environment,
        string statePath,
        int? processIdOverride,
        long? processStartIdentityOverride)
    {
        IDictionary<string, string> values = environment ?? ReadProcessEnvironment();
        string root = Get(values, "DEVBRIDGE_ROOT");
        string effectiveStatePath = statePath;
        if (string.IsNullOrWhiteSpace(effectiveStatePath) && !string.IsNullOrWhiteSpace(root))
            effectiveStatePath = Path.Combine(root, "Runtime", "state.json");

        PersistedStateSnapshot state = ReadState(effectiveStatePath);
        List<string> missing = new();

        string launchId = PreferEnvironment(values, "DEVBRIDGE_LAUNCH_ID", state?.LaunchId);
        if (string.IsNullOrWhiteSpace(launchId))
            missing.Add("launchId");

        string generationText = Get(values, "DEVBRIDGE_GENERATION");
        int? generation;
        if (!string.IsNullOrWhiteSpace(generationText))
        {
            if (!int.TryParse(generationText, out int parsedGeneration) || parsedGeneration < 0)
                return Failure("DEVBRIDGE_GENERATION_INVALID", "DEVBRIDGE_GENERATION is not a non-negative integer.");
            generation = parsedGeneration;
        }
        else
        {
            generation = state?.TargetGeneration > 0 ? state.TargetGeneration : state?.Generation;
            if (!generation.HasValue)
                missing.Add("generation");
        }

        int? processId = processIdOverride ?? TryCurrentProcessId();
        if (!processId.HasValue || processId.Value <= 0)
            missing.Add("processId");

        long? processStartIdentity = processStartIdentityOverride ?? TryCurrentProcessStartIdentity();
        string profileFingerprint = PreferEnvironment(values, "DEVBRIDGE_PROFILE_FINGERPRINT",
            state?.LaunchProfileFingerprint ?? state?.ProfileFingerprint);
        string baselineFingerprint = PreferEnvironment(values, "DEVBRIDGE_BASELINE_FINGERPRINT",
            state?.BaselineFingerprint);
        string profileMode = PreferEnvironment(values, "DEVBRIDGE_PROFILE_MODE",
            state?.ProfileMode ?? state?.LaunchProfileMode);
        string modVersion = PreferEnvironment(values, "DEVBRIDGE_MOD_VERSION",
            ReadModVersion(root));

        if (missing.Count > 0)
            return Failure("DEVBRIDGE_CONTEXT_INCOMPLETE",
                "DevBridge launch identity is incomplete: " + string.Join(", ", missing) + ".");

        return new DevBridgeGenerationContextPayload
        {
            Success = true,
            Available = true,
            SchemaVersion = SchemaVersion,
            LaunchId = launchId,
            Generation = generation,
            ProfileFingerprint = profileFingerprint,
            BaselineFingerprint = baselineFingerprint,
            ProfileMode = profileMode,
            ProcessId = processId,
            ProcessStartUtcTicks = processStartIdentity,
            DevBridge2ModVersion = modVersion,
            RimBridgeIntegrationSchemaVersion = IntegrationSchemaVersion
        };
    }

    private static DevBridgeGenerationContextPayload Failure(string code, string message)
    {
        return new DevBridgeGenerationContextPayload
        {
            Success = false,
            Available = false,
            SchemaVersion = SchemaVersion,
            RimBridgeIntegrationSchemaVersion = IntegrationSchemaVersion,
            ErrorCode = code,
            Error = message
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

    private static string PreferEnvironment(IDictionary<string, string> values, string name, string fallback)
    {
        string value = Get(values, name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int? TryCurrentProcessId()
    {
        try
        {
            return Process.GetCurrentProcess().Id;
        }
        catch
        {
            return null;
        }
    }

    private static long? TryCurrentProcessStartIdentity()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadModVersion(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;
        try
        {
            string path = Path.Combine(root, "About", "About.xml");
            if (!File.Exists(path))
                return null;
            XDocument document = XDocument.Load(path);
            return document.Root?.Elements()
                .FirstOrDefault(value => string.Equals(value.Name.LocalName, "modVersion",
                    StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static PersistedStateSnapshot ReadState(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            DataContractJsonSerializer serializer = new(typeof(PersistedStateSnapshot));
            using FileStream stream = File.OpenRead(path);
            return serializer.ReadObject(stream) as PersistedStateSnapshot;
        }
        catch
        {
            return null;
        }
    }

    [DataContract]
    private sealed class PersistedStateSnapshot
    {
        [DataMember(Name = "LaunchId")] public string LaunchId { get; set; }
        [DataMember(Name = "Generation")] public int Generation { get; set; }
        [DataMember(Name = "TargetGeneration")] public int TargetGeneration { get; set; }
        [DataMember(Name = "ProcessId")] public int ProcessId { get; set; }
        [DataMember(Name = "ProcessStartUtcTicks")] public long ProcessStartUtcTicks { get; set; }
        [DataMember(Name = "ProfileMode")] public string ProfileMode { get; set; }
        [DataMember(Name = "LaunchProfileMode")] public string LaunchProfileMode { get; set; }
        [DataMember(Name = "ProfileFingerprint")] public string ProfileFingerprint { get; set; }
        [DataMember(Name = "LaunchProfileFingerprint")] public string LaunchProfileFingerprint { get; set; }
        [DataMember(Name = "BaselineFingerprint")] public string BaselineFingerprint { get; set; }
    }
}

[DataContract]
public sealed class DevBridgeGenerationContextPayload
{
    [DataMember(Name = "success")]
    public bool Success { get; set; }
    [DataMember(Name = "available")]
    public bool Available { get; set; }
    [DataMember(Name = "schemaVersion")]
    public string SchemaVersion { get; set; }
    [DataMember(Name = "launchId")]
    public string LaunchId { get; set; }
    [DataMember(Name = "generation")]
    public int? Generation { get; set; }
    [DataMember(Name = "profileFingerprint")]
    public string ProfileFingerprint { get; set; }
    [DataMember(Name = "baselineFingerprint")]
    public string BaselineFingerprint { get; set; }
    [DataMember(Name = "profileMode")]
    public string ProfileMode { get; set; }
    [DataMember(Name = "processId")]
    public int? ProcessId { get; set; }
    [DataMember(Name = "processStartUtcTicks")]
    public long? ProcessStartUtcTicks { get; set; }
    [DataMember(Name = "devBridge2ModVersion")]
    public string DevBridge2ModVersion { get; set; }
    [DataMember(Name = "rimBridgeIntegrationSchemaVersion")]
    public string RimBridgeIntegrationSchemaVersion { get; set; }
    [DataMember(Name = "errorCode")]
    public string ErrorCode { get; set; }
    [DataMember(Name = "error")]
    public string Error { get; set; }
}
