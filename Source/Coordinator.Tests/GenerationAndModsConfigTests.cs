using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DevBridge2;
using DevBridge2.BridgeTools;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestBaselineProfile()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "none"), _ => { }, () => true);
        Assert(exitCode == 0, "baseline profile restart must succeed");
        List<string> active = ActiveMods(setup.Fixture.Root);
        Assert(active.SequenceEqual(ModProfileResolver.AlwaysOnPackageIds, StringComparer.OrdinalIgnoreCase),
            "baseline profile must contain exactly the always-on mods in stable order");
        Assert(!active.Any(value => value.Contains("loadthemlast", StringComparison.OrdinalIgnoreCase)),
            "baseline profile must never inject Load Them Last");
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(response.ProfileMode == ModProfile.BaselineMode && response.RequestedProjects.Count == 0,
            "JSON must report the explicit baseline profile");
        Assert(response.ResolvedMods.SequenceEqual(active, StringComparer.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(response.ProfileFingerprint) &&
               !string.IsNullOrWhiteSpace(response.BaselineFingerprint),
            "JSON must report the exact resolved baseline profile and fingerprints");
    }

    private static void TestProfileDependencyClosure()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        WriteInstalledMetadata(setup.MetadataRoot, "ludeon.rimworld.ideology", "Ludeon.RimWorld.Ideology", "");
        WriteInstalledMetadata(setup.MetadataRoot, "horticulture", "Lan.Horticulture.NovelSeeds",
            "ferny.progressionagriculture", "", "lan.aquaculture.fishing");
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture,aquaculture"), _ => { }, () => true);
        Assert(exitCode == 0, "project profile restart must succeed");
        List<string> active = ActiveMods(setup.Fixture.Root);
        List<string> lower = active.Select(value => value.ToLowerInvariant()).ToList();
        string[] expected =
        {
            "oskarpotocki.vanillafactionsexpanded.core",
            "vanillaexpanded.vcef",
            "ferny.replacelib",
            "ferny.progressionagriculture",
            "lan.aquaculture.fishing",
            "lan.horticulture.novelseeds"
        };
        foreach (string packageId in expected)
            Assert(lower.Contains(packageId), "dependency closure is missing " + packageId);
        Assert(active.Contains("ludeon.rimworld.ideology", StringComparer.Ordinal) &&
               active.Contains("lan.horticulture.novelseeds", StringComparer.Ordinal),
            "profile roots must retain canonical requested casing instead of installed metadata casing");
        Assert(lower.Distinct(StringComparer.OrdinalIgnoreCase).Count() == lower.Count,
            "shared dependencies must be deduplicated");
        Assert(IndexOf(lower, "oskarpotocki.vanillafactionsexpanded.core") < IndexOf(lower, "vanillaexpanded.vcef") &&
               IndexOf(lower, "vanillaexpanded.vcef") < IndexOf(lower, "ferny.replacelib") &&
               IndexOf(lower, "ferny.replacelib") < IndexOf(lower, "ferny.progressionagriculture"),
            "dependencies must precede their dependents");
        Assert(IndexOf(lower, "lan.aquaculture.fishing") < IndexOf(lower, "lan.horticulture.novelseeds"),
            "loadBefore/loadAfter constraints must be honored");
        Assert(!lower.Contains(ModProfileResolver.ForbiddenPackageId),
            "Load Them Last must never be included");
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(response.ProfileMode == ModProfile.ProjectsMode &&
               response.RequestedProjects.SequenceEqual(new[] { "aquaculture", "horticulture" }),
            "JSON must report canonical requested project aliases");
        Assert(response.ResolvedProjectPackageIds.Count == 2 && response.ResolvedMods.Count == active.Count,
            "JSON must expose both resolved roots and the complete ordered profile");

        ModProfile first = ModProfileResolver.Resolve(setup.Fixture.Root, response.BaselineFingerprint,
            new[] { "HORTICULTURE", "aquaculture" }, setup.Fixture.InstalledModsRoots);
        ModProfile second = ModProfileResolver.Resolve(setup.Fixture.Root, response.BaselineFingerprint,
            new[] { "aquaculture", "horticulture" }, setup.Fixture.InstalledModsRoots);
        Assert(first.ProfileFingerprint == second.ProfileFingerprint &&
               first.ResolvedMods.SequenceEqual(second.ResolvedMods, StringComparer.OrdinalIgnoreCase),
            "equivalent alias casing/order must produce one deterministic profile fingerprint and order");
    }

    private static void TestPackageDiscoveryUsesOwnPackageId()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        string aboutPath = Path.Combine(setup.MetadataRoot, "astryl.moderndevtools", "About", "About.xml");
        File.WriteAllText(aboutPath,
            "<ModMetaData><modDependencies><li><packageId>brrainz.harmony</packageId></li>" +
            "</modDependencies><packageId>astryl.moderndevtools</packageId></ModMetaData>",
            new UTF8Encoding(false));
        Assert(setup.CaptureBaseline(), "package discovery: baseline capture must succeed");
        JsonCommandResponse baseline = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        ModProfile profile = ModProfileResolver.Resolve(setup.Fixture.Root, baseline.BaselineFingerprint,
            new[] { "aquaculture" }, setup.Fixture.InstalledModsRoots);
        Assert(profile.ResolvedMods.Contains("astryl.moderndevtools", StringComparer.OrdinalIgnoreCase),
            "package discovery must use the direct mod package ID");
        Assert(profile.ResolvedMods.Count(value =>
                string.Equals(value, "brrainz.harmony", StringComparison.OrdinalIgnoreCase)) == 1,
            "a dependency package ID must not create a duplicate installed package candidate");
    }

    private static void TestStructuredDependencyMetadata()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        File.WriteAllText(Path.Combine(setup.MetadataRoot, "ilyvion.loadingprogress", "About", "About.xml"),
            @"<ModMetaData>
  <packageId>ilyvion.loadingprogress</packageId>
  <modDependencies>
    <li>
      <packageId>brrainz.harmony</packageId>
      <displayName>Harmony</displayName>
      <downloadUrl>https://steamcommunity.com/workshop/filedetails/?id=2009463077</downloadUrl>
      <steamWorkshopUrl>steam://url/CommunityFilePage/2009463077</steamWorkshopUrl>
    </li>
  </modDependencies>
  <loadAfter>
    <li>brrainz.harmony</li>
  </loadAfter>
  <loadBefore>
    <li>ludeon.rimworld</li>
  </loadBefore>
</ModMetaData>", new UTF8Encoding(false));
        Assert(setup.CaptureBaseline(), "structured metadata: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "aquaculture"), _ => { }, () => true);
        Assert(exitCode == 0, "structured metadata profile must resolve and launch successfully");

        List<string> active = ActiveMods(setup.Fixture.Root);
        List<string> lower = active.Select(value => value.ToLowerInvariant()).ToList();
        Assert(lower.Count(value => value == "brrainz.harmony") == 1,
            "structured dependency metadata must keep Harmony exactly once");
        Assert(IndexOf(lower, "brrainz.harmony") < IndexOf(lower, "ilyvion.loadingprogress") &&
               IndexOf(lower, "ilyvion.loadingprogress") < IndexOf(lower, "ludeon.rimworld"),
            "loadAfter Harmony and loadBefore RimWorld constraints must remain valid");

        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(active.SequenceEqual(response.ResolvedMods, StringComparer.OrdinalIgnoreCase),
            "the launched active mod order must equal the resolved profile order");
        Assert(response.ResolvedMods.Count(value =>
                   string.Equals(value, "brrainz.harmony", StringComparison.OrdinalIgnoreCase)) == 1,
            "the resolved profile must contain Harmony exactly once");
    }

    private static void TestStructuredDependencyCommentsAndWhitespace()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        File.WriteAllText(Path.Combine(setup.MetadataRoot, "ilyvion.loadingprogress", "About", "About.xml"),
            @"<ModMetaData>
  <!-- metadata comment -->
  <packageId>
    ilyvion.loadingprogress
  </packageId>
  <modDependencies>
    <!-- section comment -->
    <li>
      <!-- entry comment -->
      <packageId>
        brrainz.harmony
      </packageId>
      <!-- descriptive comments are harmless -->
      <displayName>
        Harmony
      </displayName>
    </li>
  </modDependencies>
  <loadAfter>
    <!-- load-order comment -->
    <li>
      brrainz.harmony
    </li>
  </loadAfter>
  <loadBefore>
    <li>
      ludeon.rimworld
    </li>
  </loadBefore>
</ModMetaData>", new UTF8Encoding(false));
        Assert(setup.CaptureBaseline(), "commented metadata: baseline capture must succeed");
        JsonCommandResponse baseline = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        ModProfile profile = ModProfileResolver.Resolve(setup.Fixture.Root, baseline.BaselineFingerprint,
            new[] { "aquaculture" }, setup.Fixture.InstalledModsRoots);
        List<string> lower = profile.ResolvedMods.Select(value => value.ToLowerInvariant()).ToList();
        Assert(lower.Count(value => value == "brrainz.harmony") == 1 &&
               IndexOf(lower, "brrainz.harmony") < IndexOf(lower, "ilyvion.loadingprogress") &&
               IndexOf(lower, "ilyvion.loadingprogress") < IndexOf(lower, "ludeon.rimworld"),
            "comments and formatting whitespace must not change dependency or load-order resolution");
    }

    private static void TestProfileWriteWaitsForDrain()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        byte[] baseline = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        PersistedState initial = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        initial.Generation = 1;
        initial.Phase = BridgePhase.READY;
        initial.LaunchId = "launch-ready";
        initial.LaunchGeneration = 1;
        initial.ProcessId = 101;
        initial.ProcessStartUtcTicks = 1001;
        initial.LaunchStartedUtc = ClockStart;
        initial.Leases = new List<TestLease> { setup.Fixture.Lease("T001", "holder", 77, ClockStart) };
        setup.Fixture.WriteState(initial);
        FakeProcess oldProcess = new(101, 1001, setup.Fixture.RimWorldPath);
        setup.Fixture.Adapter.Add(oldProcess);
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.Adapter.ReadyOnLaunch = true;

        Task<int> restart = Task.Run(() => setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true));
        Assert(SpinWait.SpinUntil(() =>
        {
            JsonCommandResponse status = setup.Fixture.State.CreateJsonResponse(
                Request("status"), 0, Array.Empty<string>());
            return status.RestartPending && setup.Fixture.Adapter.LaunchCalls == 0;
        }, TimeSpan.FromSeconds(2)), "profile restart must wait while the lease is active");
        Assert(File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(baseline) &&
               !oldProcess.HasExited,
            "profile config must not change while the old process and blocking lease remain");

        Assert(setup.Fixture.State.Execute(Request("test", "holder", 77, "end", "T001"), _ => { }, () => true) == 0,
            "lease holder must be able to release the blocking lease");
        Assert(restart.Wait(TimeSpan.FromSeconds(10)) && restart.Result == 0,
            "profile restart must resume exactly once after the lease drains");
        Assert(oldProcess.HasExited && setup.Fixture.Adapter.LaunchCalls == 1 &&
               ActiveMods(setup.Fixture.Root).Contains("lan.horticulture.novelseeds", StringComparer.OrdinalIgnoreCase),
            "profile config must be written after owned-process shutdown and before the replacement launch");
    }

    private static void TestProfileWritePreconditions()
    {
        using (ProfileSetup capture = ProfileSetup.Create())
        {
            capture.Fixture.BeforeModsConfigWrite = () => File.WriteAllText(
                Path.Combine(capture.Fixture.Root, "ModsConfig.xml"), "<capture-race />", new UTF8Encoding(false));
            capture.Fixture.State = capture.Fixture.Reload();
            int exitCode = capture.Fixture.State.Execute(
                Request("mods", "agent", 1, "capture-baseline"), _ => { }, () => true);
            Assert(exitCode != 0 && !File.Exists(Path.Combine(capture.Fixture.Root, "Runtime", "ModsConfig.baseline.xml")) &&
                   File.ReadAllText(Path.Combine(capture.Fixture.Root, "ModsConfig.xml")) == "<capture-race />",
                "a concurrent edit must not be captured as the durable baseline");
        }

        using (ProfileSetup edited = ProfileSetup.Create())
        {
            Assert(edited.CaptureBaseline(), "external-edit race: baseline capture must succeed");
            edited.Fixture.Adapter.ReadyOnLaunch = true;
            edited.Fixture.BeforeModsConfigWrite = () => File.WriteAllText(
                Path.Combine(edited.Fixture.Root, "ModsConfig.xml"), "<user-edit />", new UTF8Encoding(false));
            edited.Fixture.State = edited.Fixture.Reload();
            int exitCode = edited.Fixture.State.Execute(
                Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
            JsonCommandResponse response = edited.Fixture.State.CreateJsonResponse(
                Request("status"), exitCode, Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "MODS_CONFIG_EXTERNAL_EDIT" &&
                   edited.Fixture.Adapter.LaunchCalls == 0 && File.ReadAllText(
                       Path.Combine(edited.Fixture.Root, "ModsConfig.xml")) == "<user-edit />",
                "a concurrent ModsConfig edit must be detected before the profile replaces it or launches");
        }

        using (ProfileSetup process = ProfileSetup.Create())
        {
            Assert(process.CaptureBaseline(), "process race: baseline capture must succeed");
            byte[] baseline = File.ReadAllBytes(Path.Combine(process.Fixture.Root, "ModsConfig.xml"));
            process.Fixture.Adapter.ReadyOnLaunch = true;
            process.Fixture.BeforeModsConfigWrite = () => process.Fixture.Adapter.Add(
                new FakeProcess(999, 9999, process.Fixture.RimWorldPath));
            process.Fixture.State = process.Fixture.Reload();
            int exitCode = process.Fixture.State.Execute(
                Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
            JsonCommandResponse response = process.Fixture.State.CreateJsonResponse(
                Request("status"), exitCode, Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "MODS_CONFIG_PROCESS_RUNNING" &&
                   process.Fixture.Adapter.LaunchCalls == 0 && File.ReadAllBytes(
                       Path.Combine(process.Fixture.Root, "ModsConfig.xml")).SequenceEqual(baseline),
                "a process appearing before the config write must prevent both mutation and launch");
        }

        using (Fixture legacy = new(new PersistedState { Generation = 0, Phase = BridgePhase.STOPPED }))
        {
            File.WriteAllText(Path.Combine(legacy.Root, "ModsConfig.xml"),
                "<ModsConfigData><activeMods><li>user.custom.mod</li></activeMods></ModsConfigData>",
                new UTF8Encoding(false));
            legacy.Adapter.ReadyOnLaunch = true;
            legacy.BeforeModsConfigWrite = () => File.WriteAllText(
                Path.Combine(legacy.Root, "ModsConfig.xml"), "<user-edit />", new UTF8Encoding(false));
            legacy.State = legacy.Reload();
            int exitCode = legacy.State.Execute(Request("restart", "agent", 1), _ => { }, () => true);
            JsonCommandResponse response = legacy.State.CreateJsonResponse(
                Request("status"), exitCode, Array.Empty<string>());
            Assert(exitCode != 0 && response.ErrorCode == "MODS_CONFIG_EXTERNAL_EDIT" &&
                   legacy.Adapter.LaunchCalls == 0 && File.ReadAllText(
                       Path.Combine(legacy.Root, "ModsConfig.xml")) == "<user-edit />",
                "legacy DevBridge activation must also reject a concurrent config edit");
        }
    }

    private static void TestGeneratedOwnershipSurvivesLostState()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "lost-state ownership: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "lost-state ownership: reduced profile launch must succeed");

        byte[] generated = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        setup.Fixture.Adapter.Current.ForceTerminate();
        File.Delete(Path.Combine(setup.Fixture.Root, "Runtime", "state.json"));
        setup.Fixture.State = setup.Fixture.Reload();
        int exitCode = setup.Fixture.State.Execute(
            Request("mods", "agent", 1, "capture-baseline"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());
        Assert(exitCode != 0 && response.ErrorCode == "PROFILE_BASELINE_GENERATED" &&
               File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(generated) &&
               File.Exists(Path.Combine(setup.Fixture.Root, "Runtime", "ModsConfig.generated.json")),
            "generated reduced output must remain identifiable even when state.json is lost");
    }

    private static void TestAuthorizedModsConfigOwnership()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "authorized ownership: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "authorized ownership: profile launch must succeed");

        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(response.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ControlledFrozen &&
               response.ExternalModsConfigMutation == null &&
               response.RimBridgePolicy != null && response.RimBridgePolicy.ProfileFrozen &&
               response.RimBridgePolicy.GenerationOwned,
            "DevBridge's authorized write must end in controlled/frozen ownership without external evidence");
    }

    private static void TestExternalModsConfigMutation()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "external mutation: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "external mutation: accepted profile launch must succeed");

        JsonCommandResponse accepted = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        PersistedState acceptedState = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        string acceptedProfile = accepted.ProfileFingerprint;
        string configPath = Path.Combine(setup.Fixture.Root, "ModsConfig.xml");
        File.AppendAllText(configPath, "\n<!-- external mutation -->\n", new UTF8Encoding(false));

        JsonCommandResponse detected = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        ModsConfigMutationEvidence evidence = detected.ExternalModsConfigMutation;
        Assert(detected.ErrorCode == "PROFILE_EXTERNAL_MUTATION" &&
               detected.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ExternalMutated &&
               evidence != null && evidence.Generation == acceptedState.Generation &&
               evidence.LaunchId == acceptedState.LaunchId &&
               evidence.ExpectedFingerprint == acceptedState.ModsConfigGeneratedHash &&
               evidence.ObservedFingerprint != evidence.ExpectedFingerprint &&
               evidence.ExpectedProfileFingerprint == acceptedProfile &&
               evidence.DetectedUtc != default &&
               detected.ProfileFingerprint == acceptedProfile &&
               detected.RimBridgePolicy != null && !detected.RimBridgePolicy.GenerationOwned &&
               !detected.RimBridgePolicy.ProfileFrozen,
            "external mutation must record bounded evidence and preserve the accepted profile metadata");
        Assert(detected.Error.Contains("reconciliation", StringComparison.OrdinalIgnoreCase) &&
               detected.Error.Contains("not", StringComparison.OrdinalIgnoreCase),
            "external mutation diagnostics must direct maintenance/profile reconciliation");
    }

    private static void TestExternalMutationNoRestartLoop()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "restart guard: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "restart guard: accepted profile launch must succeed");
        File.AppendAllText(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"), "\n<!-- drift -->\n",
            new UTF8Encoding(false));

        JsonCommandResponse detected = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        int launches = setup.Fixture.Adapter.LaunchCalls;
        int terminations = setup.Fixture.Adapter.TerminationRequests;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        setup.Fixture.State.StartRecoveryWork();
        JsonCommandResponse blocked = setup.Fixture.State.CreateJsonResponse(
            Request("status"), exitCode, Array.Empty<string>());
        Assert(detected.ErrorCode == "PROFILE_EXTERNAL_MUTATION" && exitCode != 0 &&
               blocked.ErrorCode == "PROFILE_EXTERNAL_MUTATION" &&
               setup.Fixture.Adapter.LaunchCalls == launches &&
               setup.Fixture.Adapter.TerminationRequests == terminations,
            "external evidence must block restart/recovery without repeatedly stopping or launching RimWorld");
    }

    private static void TestControlPolicyCompanion()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "policy companion: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "policy companion: accepted profile launch must succeed");

        string statePath = Path.Combine(setup.Fixture.Root, "Runtime", "state.json");
        string before = File.ReadAllText(statePath);
        DevBridgeControlPolicyPayload policy = DevBridgeControlPolicy.Read(null, statePath);
        string serialized = JsonSerializer.Serialize(policy, Program.JsonOptions);
        string after = File.ReadAllText(statePath);
        Assert(policy.Success && policy.Available && policy.ReadOnly &&
               policy.LifecycleOwner == "devbridge" && policy.ModsConfigOwner == "devbridge" &&
               policy.GenerationOwner == "devbridge" && policy.CurrentGeneration > 0 &&
               policy.GenerationOwned && policy.ProfileFrozen &&
               policy.BlockedOperations.Contains("rimworld/set_mod_enabled") &&
               policy.BlockedOperations.Contains("rimworld/reorder_mod") &&
               policy.OperationCategories["rimworld/reorder_mod"] == "profile-mutation" &&
               !serialized.Contains("Token", StringComparison.OrdinalIgnoreCase) && before == after,
             "control policy must be read-only, machine-readable, and expose the conflicting RimBridge operations");
    }

    private static void TestInvalidProfilesFailClosed()
    {
        AssertInvalidProfile("missing dependency", setup =>
        {
            Directory.Delete(Path.Combine(setup.MetadataRoot, "progression"), true);
        }, "PROFILE_MISSING_PACKAGE");

        AssertInvalidProfile("ambiguous package", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "duplicate-replacelib", "FERNY.ReplaceLib", "");
        }, "PROFILE_AMBIGUOUS_PACKAGE");

        AssertInvalidProfile("malformed dependency metadata", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture",
                "lan.horticulture.novelseeds", "<modDependencies>unparseable dependency text</modDependencies>");
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("duplicate direct package IDs", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture", "lan.horticulture.novelseeds",
                "<modDependencies><li><packageId>ferny.progressionagriculture</packageId>" +
                "<packageId>ferny.replacelib</packageId></li></modDependencies>");
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("missing direct package ID", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture", "lan.horticulture.novelseeds",
                "<modDependencies><li><displayName>Harmony</displayName></li></modDependencies>");
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("nested-only package ID", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture", "lan.horticulture.novelseeds",
                "<modDependencies><li><displayName><packageId>ferny.progressionagriculture</packageId>" +
                "</displayName></li></modDependencies>");
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("empty dependency package ID", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture", "lan.horticulture.novelseeds",
                "<modDependencies><li><packageId> </packageId></li></modDependencies>");
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("malformed dependency package ID", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture", "lan.horticulture.novelseeds",
                "<modDependencies><li><packageId>ferny.progressionagriculture!</packageId></li></modDependencies>");
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("ambiguous structured mixed text", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture", "lan.horticulture.novelseeds",
                "<modDependencies><li>unexpected text<packageId>ferny.progressionagriculture</packageId>" +
                "</li></modDependencies>");
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("malformed non-li dependency entry", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture", "lan.horticulture.novelseeds",
                "<modDependencies><displayName>Harmony</displayName></modDependencies>");
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("malformed XML metadata", setup =>
        {
            File.WriteAllText(Path.Combine(setup.MetadataRoot, "horticulture", "About", "About.xml"),
                "<ModMetaData><packageId>lan.horticulture.novelseeds</packageId>" +
                "<modDependencies><li>ferny.progressionagriculture</modDependencies></ModMetaData>",
                new UTF8Encoding(false));
        }, "PROFILE_MALFORMED_METADATA");

        AssertInvalidProfile("dependency cycle", setup =>
        {
            WriteInstalledMetadata(setup.MetadataRoot, "horticulture",
                "lan.horticulture.novelseeds", "<modDependencies><li>ferny.progressionagriculture</li></modDependencies>");
            WriteInstalledMetadata(setup.MetadataRoot, "progression",
                "ferny.progressionagriculture", "<modDependencies><li>lan.horticulture.novelseeds</li></modDependencies>");
        }, "PROFILE_DEPENDENCY_CYCLE");
    }

    private static void AssertInvalidProfile(string name, Action<ProfileSetup> mutate, string expectedCode)
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), name + ": baseline capture must succeed");
        byte[] before = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        FakeProcess existingProcess = PrepareReadyProcess(setup);
        int modsConfigWrites = 0;
        setup.Fixture.BeforeModsConfigWrite = () => modsConfigWrites++;
        setup.Fixture.State = setup.Fixture.Reload();
        mutate(setup);
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), exitCode, Array.Empty<string>());
        Assert(exitCode != 0 && response.ErrorCode == expectedCode,
            name + " must fail with " + expectedCode + " (actual " + response.ErrorCode + ")");
        Assert(setup.Fixture.Adapter.LaunchCalls == 0 &&
               setup.Fixture.Adapter.TerminationRequests == 0 && !existingProcess.HasExited &&
               modsConfigWrites == 0 &&
               File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(before),
            name + " must fail before process stop, launch, or ModsConfig mutation");
        Assert(!setup.Fixture.State.CreateJsonResponse(Request("status"), exitCode, Array.Empty<string>()).RestartPending,
            name + " must not leave a pending restart");
    }

    private static FakeProcess PrepareReadyProcess(ProfileSetup setup)
    {
        PersistedState ready = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        ready.Generation = 1;
        ready.Phase = BridgePhase.READY;
        ready.LaunchId = "launch-ready";
        ready.LaunchGeneration = 1;
        ready.ProcessId = 101;
        ready.ProcessStartUtcTicks = 1001;
        ready.LaunchStartedUtc = ClockStart;
        ready.RestartPending = false;
        ready.TargetGeneration = 0;
        ready.RequiresNewProcess = false;
        setup.Fixture.WriteState(ready);
        FakeProcess process = new(101, 1001, setup.Fixture.RimWorldPath);
        setup.Fixture.Adapter.Add(process);
        setup.Fixture.State = setup.Fixture.Reload();
        return process;
    }

    private static void TestProfileRecoveryAndConflict()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        int exitCode = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true);
        Assert(exitCode == 0, "profile restart must complete before recovery check");
        string fingerprint = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>()).ProfileFingerprint;
        setup.Fixture.State = setup.Fixture.Reload();
        JsonCommandResponse recovered = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(recovered.ProfileFingerprint == fingerprint && recovered.ResolvedMods.Count > 0,
            "accepted profile and fingerprint must survive coordinator recovery");

        // A conflicting request is rejected from the durable pending record before the
        // lifecycle worker can acquire its process-control gate.
        PersistedState pending = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        pending.RestartPending = true;
        pending.TargetGeneration = pending.Generation + 1;
        pending.LaunchOwner = "agent@1";
        pending.LaunchRequestKey = "restart-" + pending.TargetGeneration;
        pending.Phase = BridgePhase.DRAINING;
        setup.Fixture.WriteState(pending);
        setup.Fixture.State = setup.Fixture.Reload();
        int queued = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "aquaculture"), _ => { }, () => true);
        JsonCommandResponse conflictResponse = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), queued, Array.Empty<string>());
        Assert(queued == 0 && conflictResponse.QueuedProjectIntents.Any(value =>
                   value.RequestedProjects.SequenceEqual(new[] { "aquaculture" })),
            "a late project request must queue without replacing a pending profile");
        Assert(conflictResponse.ProfileFingerprint == fingerprint,
            "the accepted pending profile fingerprint must remain unchanged after conflict");

        int legacyConflict = setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--legacy-production"), _ => { }, () => true);
        JsonCommandResponse legacyConflictResponse = setup.Fixture.State.CreateJsonResponse(
            Request("status"), legacyConflict, Array.Empty<string>());
        Assert(legacyConflict != 0 && legacyConflictResponse.ErrorCode == "PROFILE_LEGACY_CONFLICT" &&
               legacyConflictResponse.ProfileFingerprint == fingerprint,
            "explicit legacy production must not replace an accepted aggregate profile or its frozen evidence");
    }

    private static void TestCorruptPersistedProfileQuarantine()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "corrupt profile: baseline capture must succeed");
        PersistedState baseline = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        baseline.ProfileMode = ModProfile.ProjectsMode;
        baseline.RequestedProjects = new List<string> { "horticulture" };
        baseline.ResolvedProjectPackageIds = new List<string> { "lan.horticulture.novelseeds" };
        baseline.ResolvedMods = ModProfileResolver.AlwaysOnPackageIds.ToList();
        baseline.ResolvedMods.Add("lan.horticulture.novelseeds");
        baseline.ProfileFingerprint = "not-a-fingerprint";
        baseline.RestartPending = true;
        baseline.TargetGeneration = 1;
        baseline.Phase = BridgePhase.DRAINING;
        baseline.LaunchOwner = "agent@1";
        baseline.LaunchRequestKey = "restart-1";
        setup.Fixture.WriteState(baseline);
        setup.Fixture.State = setup.Fixture.Reload();
        setup.Fixture.State.StartRecoveryWork();
        JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
            Request("status"), 0, Array.Empty<string>());
        Assert(response.ErrorCode == "PROFILE_FINGERPRINT_MISMATCH" && !response.RestartPending &&
               setup.Fixture.Adapter.LaunchCalls == 0,
            "corrupt accepted profile state must quarantine recovery without silently falling back or launching");
    }

    private static void TestFrozenProfileRecovery()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "frozen recovery: baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "frozen recovery: initial profile launch must succeed");

        PersistedState pending = JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(setup.Fixture.Root, "Runtime", "state.json")), Program.JsonOptions);
        int targetGeneration = pending.Generation + 1;
        pending.RestartPending = true;
        pending.TargetGeneration = targetGeneration;
        pending.Phase = BridgePhase.RESTARTING;
        pending.LaunchOwner = "agent@1";
        pending.LaunchRequestKey = "restart-" + targetGeneration;
        pending.ProcessId = 0;
        pending.ProcessStartUtcTicks = 0;
        pending.LaunchId = null;
        pending.LaunchGeneration = targetGeneration;
        pending.RestartRequestedUtc = ClockStart;
        pending.RequiresNewProcess = true;
        // Simulate a crash after a stale runtime snapshot was persisted. The
        // accepted profile must remain the launch authority during recovery.
        pending.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(
            ModProfileResolver.CreateBaselineProfile(pending.BaselineFingerprint));
        pending.Error = null;
        pending.ErrorCode = null;
        setup.Fixture.Adapter.Current.ForceTerminate();
        setup.Fixture.WriteState(pending);
        Directory.Delete(setup.MetadataRoot, true);
        setup.Fixture.State = setup.Fixture.Reload();
        int launchesBeforeRecovery = setup.Fixture.Adapter.LaunchCalls;
        setup.Fixture.State.StartRecoveryWork();
        Assert(SpinWait.SpinUntil(() =>
        {
            JsonCommandResponse response = setup.Fixture.State.CreateJsonResponse(
                Request("status"), 0, Array.Empty<string>());
            return response.Generation == targetGeneration && response.State == "READY" &&
                   !response.RestartPending;
        }, TimeSpan.FromSeconds(10)),
            "recovery must complete using the accepted profile even when installed metadata is gone");
        JsonCommandResponse recovered = setup.Fixture.State.CreateJsonResponse(
            Request("mods", "agent", 1, "status"), 0, Array.Empty<string>());
        Assert(setup.Fixture.Adapter.LaunchCalls == launchesBeforeRecovery + 1 &&
               recovered.ProfileMode == ModProfile.ProjectsMode &&
               recovered.RequestedProjects.SequenceEqual(new[] { "horticulture" }) &&
               ActiveMods(setup.Fixture.Root).SequenceEqual(recovered.ResolvedMods, StringComparer.OrdinalIgnoreCase),
            "recovery must preserve the frozen profile roots, order, and exactly-once launch");
    }

    private static void TestBaselineRestoreSafety()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        byte[] original = File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"));
        Assert(setup.CaptureBaseline(), "baseline capture must succeed");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(
            Request("restart", "agent", 1, "--projects", "horticulture"), _ => { }, () => true) == 0,
            "profile launch must succeed before restore");
        setup.Fixture.Adapter.Current.ForceTerminate();
        int recapture = setup.Fixture.State.Execute(
            Request("mods", "agent", 1, "capture-baseline"), _ => { }, () => true);
        JsonCommandResponse recaptureResponse = setup.Fixture.State.CreateJsonResponse(
            Request("status"), recapture, Array.Empty<string>());
            Assert(recapture != 0 && recaptureResponse.ErrorCode == "PROFILE_BASELINE_GENERATED",
            "a generated reduced profile must never be silently recaptured as the user baseline");
        int restored = setup.Fixture.State.Execute(Request("mods", "agent", 1, "restore-baseline"), _ => { }, () => true);
        Assert(restored == 0 && File.ReadAllBytes(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")).SequenceEqual(original),
            "atomic restore must reproduce the captured bytes exactly");

        File.WriteAllText(Path.Combine(setup.Fixture.Root, "ModsConfig.xml"), "<user-edit />", new UTF8Encoding(false));
        int refused = setup.Fixture.State.Execute(Request("mods", "agent", 1, "restore-baseline"), _ => { }, () => true);
        Assert(refused != 0 && File.ReadAllText(Path.Combine(setup.Fixture.Root, "ModsConfig.xml")) == "<user-edit />",
            "unexpected external edits must never be overwritten by restore");
    }

}
