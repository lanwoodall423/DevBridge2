namespace DevBridge.Coordinator;

internal static class RuntimeIdentityTests
{
    internal static void InstalledRuntimeRootResolvesCanonicalRimWorld()
    {
        using IdentityFixture fixture = new();
        RuntimeIdentityResolution result = fixture.Resolve(fixture.RuntimeRoot);
        Assert(result.IsValid, result.ErrorCode);
        AssertEqual(fixture.GameRoot, result.RimWorldRoot);
        AssertEqual(fixture.Executable, result.RimWorldExecutable);
        AssertEqual(fixture.RuntimeRoot, result.DevBridgeRuntimeRoot);
        AssertEqual(RuntimeIdentityResolutionSources.CanonicalMachineConfiguration,
            result.ResolutionSource);
        Assert(result.InstalledRuntimeLayoutValid && result.RuntimeBelongsToRimWorld,
            "installed runtime must be validated against the configured game root");
    }
    internal static void ExistingRuntimeWithMissingCoordinatorIsIncomplete()
    {
        using IdentityFixture fixture = new();
        File.Delete(Path.Combine(fixture.RuntimeRoot, "Coordinator", "DevBridge.Coordinator.exe"));
        RuntimeIdentityResolution result = fixture.Resolve(fixture.RuntimeRoot);
        AssertEqual(RuntimeIdentityErrorCodes.RuntimeIncomplete, result.ErrorCode);
        Assert(result.DevBridgeRuntimeRootExists, "an incomplete runtime must remain distinguishable from a missing root");
    }
    internal static void ProductionModsConfigPathUsesCanonicalUserData()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Directory.GetParent(localAppData)?.FullName ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData");
        string expected = Path.Combine(appData, "LocalLow", "Ludeon Studios",
            "RimWorld by Ludeon Studios", "Config", "ModsConfig.xml");
        AssertEqual(expected, CoordinatorOptions.DefaultModsConfigPath());
    }



    internal static void SourceCheckoutCannotRedefineRimWorld()
    {
        using IdentityFixture fixture = new();
        RuntimeIdentityResolution result = fixture.Resolve(fixture.SourceRoot);
        Assert(result.IsValid, result.ErrorCode);
        AssertEqual(fixture.GameRoot, result.RimWorldRoot);
        AssertEqual(fixture.RuntimeRoot, result.DevBridgeRuntimeRoot);
        Assert(!result.SourceOrWorktreeUsedAsRuntime,
            "source checkout must not be selected as the live runtime root");
    }

    internal static void PinnedWorktreeCannotRedefineRimWorld()
    {
        using IdentityFixture fixture = new();
        RuntimeIdentityResolution result = fixture.Resolve(fixture.PinnedRoot);
        Assert(result.IsValid, result.ErrorCode);
        AssertEqual(fixture.GameRoot, result.RimWorldRoot);
        AssertEqual(fixture.RuntimeRoot, result.DevBridgeRuntimeRoot);
        Assert(!result.SourceOrWorktreeUsedAsRuntime,
            "pinned worktree must not be selected as the live runtime root");
    }

    internal static void MachineConfigurationWorksWithoutRimWorldEnvironment()
    {
        using IdentityFixture fixture = new();
        RuntimeIdentityResolution result = fixture.Resolve(fixture.SourceRoot);
        Assert(result.IsValid, result.ErrorCode);
        AssertEqual(RuntimeIdentityResolutionSources.CanonicalMachineConfigurationRecovery,
            result.ResolutionSource);
    }

    internal static void SourceRuntimeConfusionHasPreciseClassification()
    {
        using IdentityFixture fixture = new(runtimeRootOverride: fixturePath =>
            Path.Combine(fixturePath, "not-an-installed-runtime"));
        RuntimeIdentityResolution result = fixture.Resolve(fixture.SourceRoot);
        AssertEqual(RuntimeIdentityErrorCodes.RuntimeRootMismatch, result.ErrorCode);
        AssertEqual(fixture.GameRoot, result.RimWorldRoot);
        Assert(result.RimWorldExecutableExists, "the canonical executable must still be reported present");
    }

    internal static void MissingExecutableIsNotWrongPathDerivation()
    {
        using IdentityFixture fixture = new(createExecutable: false);
        RuntimeIdentityResolution result = fixture.Resolve(fixture.SourceRoot);
        AssertEqual(RuntimeIdentityErrorCodes.ExecutableMissing, result.ErrorCode);
        AssertEqual(fixture.GameRoot, result.RimWorldRoot);
        Assert(!result.RimWorldExecutableExists, "missing canonical executable must be reported absent");
    }

    internal static void ExplicitValidOverrideWins()
    {
        using IdentityFixture fixture = new();
        string alternateRoot = Path.Combine(fixture.Root, "alternate-game");
        string alternateRuntime = Path.Combine(alternateRoot, "Mods", "DevBridge2");
        Directory.CreateDirectory(Path.Combine(alternateRuntime, "About"));
        Directory.CreateDirectory(Path.Combine(alternateRuntime, "Coordinator"));
        File.WriteAllText(Path.Combine(alternateRuntime, "DevBridge.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(alternateRuntime, "Coordinator", "DevBridge.Coordinator.exe"), string.Empty);
        File.WriteAllText(Path.Combine(alternateRuntime, "About", "About.xml"),
            "<ModMetaData><packageId>lan.devbridge2</packageId></ModMetaData>");
        Directory.CreateDirectory(alternateRoot);
        string alternateExecutable = Path.Combine(alternateRoot, "RimWorldWin64.exe");
        File.WriteAllText(alternateExecutable, string.Empty);
        RuntimeIdentityResolution result = fixture.Resolve(fixture.SourceRoot,
            new Dictionary<string, string?>
            {
                ["RIMWORLD_ROOT"] = alternateRoot,
                ["DEVBRIDGE_RUNTIME_ROOT"] = alternateRuntime
            });
        Assert(result.IsValid, result.ErrorCode);
        AssertEqual(Path.GetFullPath(alternateRoot), result.RimWorldRoot);
        AssertEqual(Path.GetFullPath(alternateExecutable), result.RimWorldExecutable);
        AssertEqual(RuntimeIdentityResolutionSources.ExplicitOverride, result.ResolutionSource);
    }

    internal static void InvalidExplicitOverrideDoesNotFallBack()
    {
        using IdentityFixture fixture = new();
        string invalidRoot = Path.Combine(fixture.Root, "missing-game");
        RuntimeIdentityResolution result = fixture.Resolve(fixture.SourceRoot,
            new Dictionary<string, string?> { ["RIMWORLD_ROOT"] = invalidRoot });
        AssertEqual(RuntimeIdentityErrorCodes.CanonicalRootMissing, result.ErrorCode);
        AssertEqual(Path.GetFullPath(invalidRoot), result.RimWorldRoot);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual(string expected, string actual)
    {
        if (!string.Equals(Path.GetFullPath(expected), Path.GetFullPath(actual),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private sealed class IdentityFixture : IDisposable
    {
        private readonly Dictionary<string, string?> originalEnvironment = new(StringComparer.Ordinal);
        private readonly Func<string, string>? runtimeRootOverride;

        internal IdentityFixture(bool createExecutable = true, Func<string, string>? runtimeRootOverride = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "devbridge-identity-" + Guid.NewGuid().ToString("N"));
            this.runtimeRootOverride = runtimeRootOverride;
            GameRoot = Path.Combine(Root, "RimWorld");
            SourceRoot = Path.Combine(Root, "Repos", "DevBridge2");
            PinnedRoot = Path.Combine(Root, ".rimdev", "pinned-worktrees", "DevBridge2", "pinned");
            RuntimeRoot = Path.Combine(GameRoot, "Mods", "DevBridge2");
            Directory.CreateDirectory(GameRoot);
            Directory.CreateDirectory(Path.Combine(SourceRoot, "Source"));
            Directory.CreateDirectory(PinnedRoot);
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, "About"));
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, "Coordinator"));
            File.WriteAllText(Path.Combine(SourceRoot, "DevBridge.cmd"), string.Empty);
            File.WriteAllText(Path.Combine(PinnedRoot, "DevBridge.cmd"), string.Empty);
            File.WriteAllText(Path.Combine(RuntimeRoot, "DevBridge.cmd"), string.Empty);
            File.WriteAllText(Path.Combine(RuntimeRoot, "Coordinator", "DevBridge.Coordinator.exe"), string.Empty);
            File.WriteAllText(Path.Combine(RuntimeRoot, "About", "About.xml"),
                "<ModMetaData><packageId>lan.devbridge2</packageId></ModMetaData>");
            Executable = Path.Combine(GameRoot, "RimWorldWin64.exe");
            if (createExecutable)
                File.WriteAllText(Executable, string.Empty);
            string configPath = Path.Combine(Root, ".rimdev", "workspace.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, $$"""
                {
                  "schemaVersion": "rimdev-workspace/v1",
                  "rimWorldRoot": "{{GameRoot.Replace("\\", "\\\\")}}",
                  "devBridgeRuntimeRoot": "{{RuntimeRoot.Replace("\\", "\\\\")}}",
                  "devBridgeSourceRoot": "{{SourceRoot.Replace("\\", "\\\\")}}",
                  "devBridgePinnedWorktreeRoot": "{{PinnedRoot.Replace("\\", "\\\\")}}",
                  "repositories": []
                }
                """);
            SetEnvironment("RIMDEV_WORKSPACE_CONFIG", configPath);
            SetEnvironment("RIMWORLD_ROOT", null);
            SetEnvironment("RIMWORLD_EXECUTABLE", null);
            SetEnvironment("DEVBRIDGE_RUNTIME_ROOT", runtimeRootOverride?.Invoke(Root));
            SetEnvironment("DEVBRIDGE_SOURCE_ROOT", SourceRoot);
            SetEnvironment("DEVBRIDGE_PINNED_WORKTREE_ROOT", PinnedRoot);
            SetEnvironment("RIMTEST_DEVBRIDGE_ROOT", null);
            SetEnvironment("RIMTEST_DEVBRIDGE_PINNED_ROOT", null);
        }

        internal string Root { get; }
        internal string GameRoot { get; }
        internal string Executable { get; }
        internal string SourceRoot { get; }
        internal string PinnedRoot { get; }
        internal string RuntimeRoot { get; }

        internal RuntimeIdentityResolution Resolve(string requestedRoot,
            IReadOnlyDictionary<string, string?>? overrides = null)
        {
            if (overrides is not null)
                foreach (KeyValuePair<string, string?> pair in overrides)
                    SetEnvironment(pair.Key, pair.Value);
            return RuntimeIdentityResolver.Resolve(requestedRoot);
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> pair in originalEnvironment)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            try { Directory.Delete(Root, true); }
            catch { }
        }

        private void SetEnvironment(string name, string? value)
        {
            if (!originalEnvironment.ContainsKey(name))
                originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
