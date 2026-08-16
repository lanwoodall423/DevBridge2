using System.Text.Json.Serialization;

namespace DevBridge.Coordinator;

internal sealed class ProfileResolutionEdge
{
    [JsonPropertyName("fromPackageId")]
    public string FromPackageId { get; set; }

    [JsonPropertyName("toPackageId")]
    public string ToPackageId { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; }
}

internal sealed class ProfileModProvenance
{
    internal string PackageId { get; init; }
    internal List<ProfileModReason> Reasons { get; init; } = new();
}

internal sealed class ProfileModReason
{
    internal string Category { get; init; }
    internal string RelatedPackageId { get; init; }
    internal string Detail { get; init; }
}

internal sealed class ProjectResolutionIssue
{
    [JsonPropertyName("code")]
    public string Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }
}

internal sealed class ProjectResolutionMod
{
    [JsonPropertyName("packageId")]
    public string PackageId { get; set; }

    [JsonPropertyName("reasons")]
    public List<ProjectResolutionReason> Reasons { get; set; } = new();
}

internal sealed class ProjectResolutionReason
{
    [JsonPropertyName("category")]
    public string Category { get; set; }

    [JsonPropertyName("relatedPackageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string RelatedPackageId { get; set; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Detail { get; set; }
}

internal sealed class ProjectResolutionComparison
{
    [JsonPropertyName("comparedGeneration")]
    public int ComparedGeneration { get; set; }

    [JsonPropertyName("packagesAdded")]
    public List<string> PackagesAdded { get; set; } = new();

    [JsonPropertyName("packagesRemoved")]
    public List<string> PackagesRemoved { get; set; } = new();

    [JsonPropertyName("orderChanged")]
    public bool OrderChanged { get; set; }

    [JsonPropertyName("projectIntentChanged")]
    public bool ProjectIntentChanged { get; set; }

    [JsonPropertyName("fingerprintChanged")]
    public bool FingerprintChanged { get; set; }

    [JsonPropertyName("testInputsChanged")]
    public bool TestInputsChanged { get; set; }

    [JsonPropertyName("wouldDifferFromCurrent")]
    public bool WouldDifferFromCurrent { get; set; }

    [JsonPropertyName("wouldRequireRestart")]
    public bool WouldRequireRestart { get; set; }
}

internal sealed class ConfigurationHealth
{
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; set; }
}

internal sealed class ProjectResolutionResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("requestedProjects")]
    public List<string> RequestedProjects { get; set; } = new();

    [JsonPropertyName("canonicalProjects")]
    public List<string> CanonicalProjects { get; set; } = new();

    [JsonPropertyName("requestedPackageIds")]
    public List<string> RequestedPackageIds { get; set; } = new();

    [JsonPropertyName("resolvedMods")]
    public List<string> ResolvedMods { get; set; } = new();

    [JsonPropertyName("resolvedProjectPackageIds")]
    public List<string> ResolvedProjectPackageIds { get; set; } = new();

    [JsonPropertyName("testInputs")]
    public List<TestInputValue> TestInputs { get; set; } = new();

    [JsonPropertyName("dependencyEdges")]
    public List<ProfileResolutionEdge> DependencyEdges { get; set; } = new();

    [JsonPropertyName("provenance")]
    public List<ProjectResolutionMod> Provenance { get; set; } = new();

    [JsonPropertyName("profileFingerprint")]
    public string ProfileFingerprint { get; set; }

    [JsonPropertyName("baselineFingerprint")]
    public string BaselineFingerprint { get; set; }

    [JsonPropertyName("currentGeneration")]
    public int CurrentGeneration { get; set; }

    [JsonPropertyName("wouldDifferFromCurrent")]
    public bool WouldDifferFromCurrent { get; set; }

    [JsonPropertyName("wouldRequireRestart")]
    public bool WouldRequireRestart { get; set; }

    [JsonPropertyName("comparison")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectResolutionComparison Comparison { get; set; }

    [JsonPropertyName("currentGenerationTrust")]
    public string CurrentGenerationTrust { get; set; } = "UNKNOWN";

    [JsonPropertyName("nextGenerationConfig")]
    public ConfigurationHealth NextGenerationConfig { get; set; } = new() { Valid = false };

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("errors")]
    public List<ProjectResolutionIssue> Errors { get; set; } = new();

    [JsonPropertyName("nextActions")]
    public List<string> NextActions { get; set; } = new();

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; set; }
}
