using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace DevBridge.Coordinator;

internal static class RuntimeIdentityErrorCodes
{
    internal const string CanonicalConfigurationMissing = "RIMWORLD_CANONICAL_CONFIGURATION_MISSING";
    internal const string CanonicalConfigurationInvalid = "RIMWORLD_CANONICAL_CONFIGURATION_INVALID";
    internal const string CanonicalRootMissing = "RIMWORLD_CANONICAL_ROOT_MISSING";
    internal const string ExecutableMissing = "RIMWORLD_EXECUTABLE_MISSING";
    internal const string ExplicitOverrideInvalid = "DEVBRIDGE_EXPLICIT_OVERRIDE_INVALID";
    internal const string RuntimeRootMismatch = "DEVBRIDGE_RUNTIME_ROOT_MISMATCH";
    internal const string RuntimeMissing = "DEVBRIDGE_RUNTIME_MISSING";
}

internal static class RuntimeIdentityResolutionSources
{
    internal const string ExplicitOverride = "explicit-override";
    internal const string CanonicalMachineConfiguration = "canonical-machine-configuration";
    internal const string CanonicalMachineConfigurationRecovery =
        "canonical-machine-configuration-recovery";
    internal const string InstalledLayoutFallback = "installed-layout-fallback";
    internal const string TestOverride = "test-override";
    internal const string Options = "options";
}

internal sealed class RuntimeIdentityDiagnosticContract
{
    [JsonPropertyName("requestedDevBridgeRoot")]
    public string RequestedDevBridgeRoot { get; init; }
    [JsonPropertyName("devBridgeSourceRoot")]
    public string DevBridgeSourceRoot { get; init; }
    [JsonPropertyName("devBridgeRuntimeRoot")]
    public string DevBridgeRuntimeRoot { get; init; }
    [JsonPropertyName("devBridgePinnedWorktreeRoot")]
    public string DevBridgePinnedWorktreeRoot { get; init; }
    [JsonPropertyName("rimWorldRoot")]
    public string RimWorldRoot { get; init; }
    [JsonPropertyName("rimWorldExecutable")]
    public string RimWorldExecutable { get; init; }
    [JsonPropertyName("attemptedExecutable")]
    public string AttemptedExecutable { get; init; }
    [JsonPropertyName("resolutionSource")]
    public string ResolutionSource { get; init; }
    [JsonPropertyName("rimWorldRootExists")]
    public bool RimWorldRootExists { get; init; }
    [JsonPropertyName("rimWorldExecutableExists")]
    public bool RimWorldExecutableExists { get; init; }
    [JsonPropertyName("devBridgeSourceRootExists")]
    public bool DevBridgeSourceRootExists { get; init; }
    [JsonPropertyName("devBridgePinnedWorktreeRootExists")]
    public bool DevBridgePinnedWorktreeRootExists { get; init; }
    [JsonPropertyName("devBridgeRuntimeRootExists")]
    public bool DevBridgeRuntimeRootExists { get; init; }
    [JsonPropertyName("installedRuntimeLayoutValid")]
    public bool InstalledRuntimeLayoutValid { get; init; }
    [JsonPropertyName("runtimeBelongsToRimWorld")]
    public bool RuntimeBelongsToRimWorld { get; init; }
    [JsonPropertyName("sourceOrWorktreeUsedAsRuntime")]
    public bool SourceOrWorktreeUsedAsRuntime { get; init; }
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; init; }
    [JsonPropertyName("error")]
    public string Error { get; init; }
    [JsonPropertyName("nextAction")]
    public string NextAction { get; init; }
}

internal sealed class RuntimeIdentityResolution
{
    internal string RequestedDevBridgeRoot { get; init; }
    internal string DevBridgeSourceRoot { get; init; }
    internal string DevBridgeRuntimeRoot { get; init; }
    internal string DevBridgePinnedWorktreeRoot { get; init; }
    internal string RimWorldRoot { get; init; }
    internal string RimWorldExecutable { get; init; }
    internal string AttemptedExecutable { get; init; }
    internal string ResolutionSource { get; init; }
    internal bool RimWorldRootExists { get; init; }
    internal bool RimWorldExecutableExists { get; init; }
    internal bool DevBridgeSourceRootExists { get; init; }
    internal bool DevBridgePinnedWorktreeRootExists { get; init; }
    internal bool DevBridgeRuntimeRootExists { get; init; }
    internal bool InstalledRuntimeLayoutValid { get; init; }
    internal bool RuntimeBelongsToRimWorld { get; init; }
    internal bool SourceOrWorktreeUsedAsRuntime { get; init; }
    internal string ErrorCode { get; init; }
    internal string Error { get; init; }
    internal string NextAction { get; init; }

    internal bool IsValid => string.IsNullOrWhiteSpace(ErrorCode);

    internal RuntimeIdentityDiagnosticContract ToContract() => new()
    {
        RequestedDevBridgeRoot = RequestedDevBridgeRoot,
        DevBridgeSourceRoot = DevBridgeSourceRoot,
        DevBridgeRuntimeRoot = DevBridgeRuntimeRoot,
        DevBridgePinnedWorktreeRoot = DevBridgePinnedWorktreeRoot,
        RimWorldRoot = RimWorldRoot,
        RimWorldExecutable = RimWorldExecutable,
        AttemptedExecutable = AttemptedExecutable,
        ResolutionSource = ResolutionSource,
        RimWorldRootExists = RimWorldRootExists,
        RimWorldExecutableExists = RimWorldExecutableExists,
        DevBridgeSourceRootExists = DevBridgeSourceRootExists,
        DevBridgePinnedWorktreeRootExists = DevBridgePinnedWorktreeRootExists,
        DevBridgeRuntimeRootExists = DevBridgeRuntimeRootExists,
        InstalledRuntimeLayoutValid = InstalledRuntimeLayoutValid,
        RuntimeBelongsToRimWorld = RuntimeBelongsToRimWorld,
        SourceOrWorktreeUsedAsRuntime = SourceOrWorktreeUsedAsRuntime,
        ErrorCode = ErrorCode,
        Error = Error,
        NextAction = NextAction
    };
}

internal sealed class RuntimeIdentityException : Exception
{
    internal RuntimeIdentityException(RuntimeIdentityResolution resolution)
        : base(resolution?.Error ?? "Runtime identity could not be resolved.")
    {
        Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
    }

    internal RuntimeIdentityResolution Resolution { get; }
}

internal static class RuntimeIdentityResolver
{
    private const string WorkspaceFileName = "workspace.json";
    private const string RuntimeDirectoryName = "Mods";
    private const string DevBridgePackageId = "lan.devbridge2";
    private const string RimWorldExecutableName = "RimWorldWin64.exe";
    private const int MaximumConfigurationBytes = 128 * 1024;

    internal static RuntimeIdentityResolution Resolve(string requestedDevBridgeRoot)
    {
        if (!TryCanonicalPath(requestedDevBridgeRoot, out string requestedRoot))
            return Failure(null, null, null, null, null, null, null,
                RuntimeIdentityErrorCodes.ExplicitOverrideInvalid,
                "The requested DevBridge root is invalid.");

        string sourceRoot = FirstPath(
            Environment.GetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT"),
            Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT"),
            LooksLikeSourceRoot(requestedRoot) ? requestedRoot : null);
        string pinnedRoot = FirstPath(
            Environment.GetEnvironmentVariable("DEVBRIDGE_PINNED_WORKTREE_ROOT"),
            Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_PINNED_ROOT"));
        sourceRoot = CanonicalOrNull(sourceRoot);
        pinnedRoot = CanonicalOrNull(pinnedRoot);

        string explicitRuntimeRoot = Environment.GetEnvironmentVariable("DEVBRIDGE_RUNTIME_ROOT");
        string explicitRimWorldRoot = Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
        string explicitExecutable = Environment.GetEnvironmentVariable("RIMWORLD_EXECUTABLE");
        ConfigurationValues configuration = LoadConfiguration(requestedRoot);
        if (configuration.Invalid)
            return Failure(requestedRoot, sourceRoot, pinnedRoot, null, null, null, null,
                RuntimeIdentityErrorCodes.CanonicalConfigurationInvalid,
                "The canonical workspace configuration is invalid.");

        bool testOverride = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("DEVBRIDGE_TEST_RIMWORLD_PATH"));
        string rimWorldRoot = null;
        string executable = null;
        string resolutionSource = null;
        if (!string.IsNullOrWhiteSpace(explicitRimWorldRoot) ||
            !string.IsNullOrWhiteSpace(explicitExecutable))
        {
            resolutionSource = RuntimeIdentityResolutionSources.ExplicitOverride;
            bool rootSpecified = !string.IsNullOrWhiteSpace(explicitRimWorldRoot);
            bool executableSpecified = !string.IsNullOrWhiteSpace(explicitExecutable);
            bool rootValid = !rootSpecified || TryCanonicalPath(explicitRimWorldRoot, out rimWorldRoot);
            bool executableValid = !executableSpecified || TryCanonicalPath(explicitExecutable, out executable);
            if (!rootValid || !executableValid)
                return Failure(requestedRoot, sourceRoot, pinnedRoot, null, rimWorldRoot, executable,
                    resolutionSource, RuntimeIdentityErrorCodes.ExplicitOverrideInvalid,
                    "The explicit RimWorld root or executable override is invalid.");
            if (!rootSpecified)
                rimWorldRoot = Path.GetDirectoryName(executable);
            executable = !executableSpecified
                ? Path.Combine(rimWorldRoot, RimWorldExecutableName)
                : executable;
        }
        else if (testOverride)
        {
            resolutionSource = RuntimeIdentityResolutionSources.TestOverride;
            executable = CanonicalPath(Environment.GetEnvironmentVariable(
                "DEVBRIDGE_TEST_RIMWORLD_PATH"));
            rimWorldRoot = Path.GetDirectoryName(executable);
        }
        else if (configuration.IsPresent)
        {
            resolutionSource = RuntimeIdentityResolutionSources.CanonicalMachineConfiguration;
            if (!TryCanonicalPath(ResolveConfiguredPath(configuration.RimWorldRoot,
                    configuration.WorkspaceRoot), out rimWorldRoot))
                return Failure(requestedRoot, sourceRoot, pinnedRoot, null, null, null,
                    resolutionSource, RuntimeIdentityErrorCodes.CanonicalConfigurationInvalid,
                    "The canonical workspace configuration has no valid RimWorld root.");
            executable = ResolveConfiguredExecutable(rimWorldRoot,
                configuration.RimWorldExecutable, configuration.WorkspaceRoot);
        }
        else if (TryResolveInstalledLayout(requestedRoot, out rimWorldRoot, out executable))
        {
            resolutionSource = RuntimeIdentityResolutionSources.InstalledLayoutFallback;
        }
        else
        {
            return Failure(requestedRoot, sourceRoot, pinnedRoot, null, null, null, null,
                RuntimeIdentityErrorCodes.CanonicalConfigurationMissing,
                "No canonical RimWorld installation is configured and the requested DevBridge root is not an installed layout.");
        }

        bool rimWorldExists = Directory.Exists(rimWorldRoot);
        bool executableExists = File.Exists(executable);
        if (!rimWorldExists)
            return Failure(requestedRoot, sourceRoot, pinnedRoot, null, rimWorldRoot, executable,
                resolutionSource, RuntimeIdentityErrorCodes.CanonicalRootMissing,
                "The canonical RimWorld root does not exist.");
        if (!executableExists)
            return Failure(requestedRoot, sourceRoot, pinnedRoot, null, rimWorldRoot, executable,
                resolutionSource, RuntimeIdentityErrorCodes.ExecutableMissing,
                "The canonical RimWorld executable is absent.");

        bool explicitGameOverride = !string.IsNullOrWhiteSpace(explicitRimWorldRoot) ||
            !string.IsNullOrWhiteSpace(explicitExecutable);
        string configuredRuntimeRoot = testOverride
            ? requestedRoot
            : FirstPath(
                explicitRuntimeRoot,
                explicitGameOverride ? null :
                    ResolveConfiguredPath(configuration.DevBridgeRuntimeRoot, configuration.WorkspaceRoot),
                Path.Combine(rimWorldRoot, RuntimeDirectoryName, "DevBridge2"));
        if (!TryCanonicalPath(configuredRuntimeRoot, out string runtimeRoot))
            return Failure(requestedRoot, sourceRoot, pinnedRoot, null, rimWorldRoot, executable,
                resolutionSource, RuntimeIdentityErrorCodes.ExplicitOverrideInvalid,
                "The configured DevBridge runtime root is invalid.");

        sourceRoot = CanonicalOrNull(FirstPath(configuration.DevBridgeSourceRoot, sourceRoot));
        pinnedRoot = CanonicalOrNull(FirstPath(configuration.DevBridgePinnedWorktreeRoot, pinnedRoot));
        bool sourceExists = Directory.Exists(sourceRoot);
        bool pinnedExists = Directory.Exists(pinnedRoot);
        bool sourceOrWorktree = PathsEqual(requestedRoot, sourceRoot) ||
            PathsEqual(requestedRoot, pinnedRoot) || LooksLikeSourceRoot(requestedRoot);
        bool runtimeBelongs = IsInstalledRuntimeFor(rimWorldRoot, runtimeRoot);
        bool runtimeExists = Directory.Exists(runtimeRoot);
        bool runtimePathMatches = PathsEqual(
            Path.Combine(rimWorldRoot, RuntimeDirectoryName, "DevBridge2"),
            runtimeRoot);
        bool layoutValid = runtimeBelongs && !testOverride &&
            File.Exists(Path.Combine(runtimeRoot, "DevBridge.cmd")) &&
            File.Exists(Path.Combine(runtimeRoot, "Coordinator", "DevBridge.Coordinator.exe"));

        if (!testOverride && !runtimePathMatches)
            return Failure(requestedRoot, sourceRoot, pinnedRoot, runtimeRoot, rimWorldRoot, executable,
                resolutionSource, RuntimeIdentityErrorCodes.RuntimeRootMismatch,
                "The configured DevBridge runtime root is not the installed runtime for the canonical RimWorld installation.");
        if (resolutionSource == RuntimeIdentityResolutionSources.CanonicalMachineConfiguration &&
            !PathsEqual(requestedRoot, runtimeRoot) && sourceOrWorktree)
            resolutionSource = RuntimeIdentityResolutionSources.CanonicalMachineConfigurationRecovery;

        if (!testOverride && !layoutValid)
            return Failure(requestedRoot, sourceRoot, pinnedRoot, runtimeRoot, rimWorldRoot, executable,
                resolutionSource, RuntimeIdentityErrorCodes.RuntimeMissing,
                "The installed DevBridge runtime is missing or does not have the expected installed layout.");

        return Success(requestedRoot, sourceRoot, pinnedRoot, runtimeRoot, rimWorldRoot, executable,
            resolutionSource, rimWorldExists, executableExists, sourceExists, pinnedExists,
            runtimeExists, layoutValid, runtimeBelongs, false);
    }

    internal static RuntimeIdentityResolution ResolveFromOptions(
        string runtimeRoot, string rimWorldRoot, string executable)
    {
        string resolvedExecutable = executable ??
            Path.Combine(rimWorldRoot ?? runtimeRoot, RimWorldExecutableName);
        string resolvedRimWorldRoot = rimWorldRoot ??
            Path.GetDirectoryName(resolvedExecutable) ?? runtimeRoot;
        return Success(
            runtimeRoot, null, null, runtimeRoot, resolvedRimWorldRoot,
            resolvedExecutable,
            RuntimeIdentityResolutionSources.Options,
            Directory.Exists(resolvedRimWorldRoot),
            File.Exists(resolvedExecutable),
            false, false, Directory.Exists(runtimeRoot), false, false, false);
    }

    private static ConfigurationValues LoadConfiguration(string requestedRoot)
    {
        string explicitPath = Environment.GetEnvironmentVariable("RIMDEV_WORKSPACE_CONFIG");
        IEnumerable<string> candidates = !string.IsNullOrWhiteSpace(explicitPath)
            ? new[] { explicitPath }
            : AncestorPaths(requestedRoot)
                .Concat(AncestorPaths(AppContext.BaseDirectory))
                .Select(path => Path.Combine(path, ".rimdev", WorkspaceFileName));

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                if (new FileInfo(candidate).Length > MaximumConfigurationBytes)
                    return ConfigurationValues.InvalidValue;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(candidate));
                JsonElement root = document.RootElement;
                string workspaceRoot = Directory.GetParent(candidate)?.Parent?.FullName;
                return new ConfigurationValues(
                    true,
                    ReadString(root, "rimWorldRoot"),
                    ReadString(root, "rimWorldExecutable"),
                    ReadString(root, "devBridgeRuntimeRoot"),
                    ReadString(root, "devBridgeSourceRoot"),
                    ReadString(root, "devBridgePinnedWorktreeRoot"),
                    workspaceRoot,
                    false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or
                UnauthorizedAccessException or NotSupportedException)
            {
                return ConfigurationValues.InvalidValue;
            }
        }

        return ConfigurationValues.None;
    }

    private static bool TryResolveInstalledLayout(
        string requestedRoot, out string rimWorldRoot, out string executable)
    {
        rimWorldRoot = null;
        executable = null;
        if (!IsInstalledRuntimeCandidate(requestedRoot))
            return false;
        DirectoryInfo? mods = Directory.GetParent(requestedRoot);
        DirectoryInfo? game = mods?.Parent;
        if (mods is null || game is null ||
            !string.Equals(mods.Name, RuntimeDirectoryName, StringComparison.OrdinalIgnoreCase))
            return false;
        rimWorldRoot = game.FullName;
        executable = Path.Combine(rimWorldRoot, RimWorldExecutableName);
        return File.Exists(executable);
    }

    private static bool IsInstalledRuntimeFor(string rimWorldRoot, string runtimeRoot) =>
        PathsEqual(Path.Combine(rimWorldRoot, RuntimeDirectoryName, "DevBridge2"), runtimeRoot) &&
        IsInstalledRuntimeCandidate(runtimeRoot);

    private static bool IsInstalledRuntimeCandidate(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            !string.Equals(Directory.GetParent(root)?.Name, RuntimeDirectoryName,
                StringComparison.OrdinalIgnoreCase))
            return false;
        string aboutPath = Path.Combine(root, "About", "About.xml");
        if (!File.Exists(aboutPath))
            return false;
        try
        {
            XDocument document = XDocument.Load(aboutPath, LoadOptions.None);
            return string.Equals(
                document.Root?.Element("packageId")?.Value?.Trim(),
                DevBridgePackageId,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            XmlException)
        {
            return false;
        }
    }

    private static string ResolveConfiguredExecutable(string root, string value, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Path.Combine(root, RimWorldExecutableName);
        return CanonicalPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(workspaceRoot ?? root, value));
    }

    private static string ResolveConfiguredPath(string value, string workspaceRoot) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.IsPathRooted(value)
            ? value : Path.Combine(workspaceRoot ?? string.Empty, value);

    private static bool LooksLikeSourceRoot(string root) =>
        Directory.Exists(Path.Combine(root, "Source")) &&
        File.Exists(Path.Combine(root, "DevBridge.cmd"));

    private static IEnumerable<string> AncestorPaths(string start)
    {
        DirectoryInfo? current = null;
        try { current = new DirectoryInfo(CanonicalPath(start)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { }
        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string FirstPath(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string CanonicalOrNull(string value) =>
        TryCanonicalPath(value, out string result) ? result : null;

    private static bool TryCanonicalPath(string value, out string result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            result = CanonicalPath(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string CanonicalPath(string value)
    {
        string full = Path.GetFullPath(value);
        string root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(CanonicalPath(left), CanonicalPath(right), StringComparison.OrdinalIgnoreCase);

    private static RuntimeIdentityResolution Success(
        string requestedRoot, string sourceRoot, string pinnedRoot, string runtimeRoot,
        string rimWorldRoot, string executable, string source, bool rimWorldExists,
        bool executableExists, bool sourceExists, bool pinnedExists, bool runtimeExists,
        bool layoutValid, bool runtimeBelongs, bool sourceOrWorktree) => new()
    {
        RequestedDevBridgeRoot = requestedRoot,
        DevBridgeSourceRoot = sourceRoot,
        DevBridgeRuntimeRoot = runtimeRoot,
        DevBridgePinnedWorktreeRoot = pinnedRoot,
        RimWorldRoot = rimWorldRoot,
        RimWorldExecutable = executable,
        AttemptedExecutable = executable,
        ResolutionSource = source,
        RimWorldRootExists = rimWorldExists,
        RimWorldExecutableExists = executableExists,
        DevBridgeSourceRootExists = sourceExists,
        DevBridgePinnedWorktreeRootExists = pinnedExists,
        DevBridgeRuntimeRootExists = runtimeExists,
        InstalledRuntimeLayoutValid = layoutValid,
        RuntimeBelongsToRimWorld = runtimeBelongs,
        SourceOrWorktreeUsedAsRuntime = sourceOrWorktree,
        NextAction = "DevBridge.cmd doctor --json"
    };

    private static RuntimeIdentityResolution Failure(
        string requestedRoot, string sourceRoot, string pinnedRoot, string runtimeRoot,
        string rimWorldRoot, string executable, string source, string code, string error) => new()
    {
        RequestedDevBridgeRoot = requestedRoot,
        DevBridgeSourceRoot = sourceRoot,
        DevBridgeRuntimeRoot = runtimeRoot,
        DevBridgePinnedWorktreeRoot = pinnedRoot,
        RimWorldRoot = rimWorldRoot,
        RimWorldExecutable = executable,
        AttemptedExecutable = executable,
        ResolutionSource = source,
        RimWorldRootExists = !string.IsNullOrWhiteSpace(rimWorldRoot) && Directory.Exists(rimWorldRoot),
        RimWorldExecutableExists = !string.IsNullOrWhiteSpace(executable) && File.Exists(executable),
        DevBridgeSourceRootExists = Directory.Exists(sourceRoot),
        DevBridgePinnedWorktreeRootExists = Directory.Exists(pinnedRoot),
        DevBridgeRuntimeRootExists = Directory.Exists(runtimeRoot),
        InstalledRuntimeLayoutValid = !string.IsNullOrWhiteSpace(runtimeRoot) &&
            IsInstalledRuntimeCandidate(runtimeRoot),
        RuntimeBelongsToRimWorld = !string.IsNullOrWhiteSpace(runtimeRoot) &&
            !string.IsNullOrWhiteSpace(rimWorldRoot) && IsInstalledRuntimeFor(rimWorldRoot, runtimeRoot),
        SourceOrWorktreeUsedAsRuntime = !string.IsNullOrWhiteSpace(runtimeRoot) &&
            (PathsEqual(runtimeRoot, sourceRoot) || PathsEqual(runtimeRoot, pinnedRoot)),
        ErrorCode = code,
        Error = error,
        NextAction = "DevBridge.cmd doctor --json"
    };

    private sealed record ConfigurationValues(
        bool IsPresent,
        string RimWorldRoot,
        string RimWorldExecutable,
        string DevBridgeRuntimeRoot,
        string DevBridgeSourceRoot,
        string DevBridgePinnedWorktreeRoot,
        string WorkspaceRoot,
        bool Invalid)
    {
        internal static ConfigurationValues None =>
            new(false, null, null, null, null, null, null, false);
        internal static ConfigurationValues InvalidValue =>
            new(true, null, null, null, null, null, null, true);
    }
}
