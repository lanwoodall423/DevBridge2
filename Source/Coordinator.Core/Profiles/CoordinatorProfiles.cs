using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DevBridge2;

namespace DevBridge.Coordinator;

internal sealed partial class CoordinatorState
{
    private RestartArguments ParseRestartArguments(IReadOnlyList<string> arguments)
    {
        string leaseId = null;
        string projectValue = null;
        bool hasProjects = false;
        bool legacyProduction = false;
        List<TestInputAssignment> testInputs = new();
        for (int index = 0; index < (arguments?.Count ?? 0); index++)
        {
            string argument = arguments[index]?.Trim() ?? string.Empty;
            if (string.Equals(argument, "--legacy-production", StringComparison.OrdinalIgnoreCase))
            {
                if (legacyProduction)
                    throw new ProfileException("PROFILE_INVALID_REQUEST", "restart accepts only one --legacy-production option.");
                legacyProduction = true;
                continue;
            }
            if (string.Equals(argument, "--projects", StringComparison.OrdinalIgnoreCase))
            {
                if (hasProjects || index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[++index]))
                    throw new ProfileException("PROFILE_INVALID_REQUEST", "restart --projects requires one value.");
                projectValue = arguments[index].Trim();
                hasProjects = true;
                continue;
            }
            if (argument.StartsWith("--projects=", StringComparison.OrdinalIgnoreCase))
            {
                if (hasProjects)
                    throw new ProfileException("PROFILE_INVALID_REQUEST", "restart accepts only one --projects option.");
                projectValue = argument.Substring("--projects=".Length).Trim();
                if (projectValue.Length == 0)
                    throw new ProfileException("PROFILE_INVALID_REQUEST", "restart --projects requires one value.");
                hasProjects = true;
                continue;
            }
            if (string.Equals(argument, "--input", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--input=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = argument.StartsWith("--input=", StringComparison.OrdinalIgnoreCase)
                    ? argument.Substring("--input=".Length)
                    : (++index < arguments.Count ? arguments[index] : null);
                testInputs.Add(TestGenerationInputs.ParseCommandAssignment(raw));
                continue;
            }
            if (argument.StartsWith("--", StringComparison.Ordinal))
                throw new ProfileException("PROFILE_INVALID_REQUEST", "Unknown restart option '" + argument + "'.");
            if (string.IsNullOrWhiteSpace(leaseId))
                leaseId = argument;
            else
                throw new ProfileException("PROFILE_INVALID_REQUEST", "restart accepts at most one lease ID.");
        }

        if (legacyProduction && hasProjects)
            throw new ProfileException("PROFILE_INVALID_REQUEST", "--legacy-production cannot be combined with --projects.");
        if (!hasProjects)
            return new RestartArguments { LeaseId = leaseId, LegacyProduction = legacyProduction,
                TestInputs = testInputs };
        if (string.Equals(projectValue, "none", StringComparison.OrdinalIgnoreCase))
            return new RestartArguments { LeaseId = leaseId, HasProjects = true, TestInputs = testInputs };
        string[] parts = projectValue.Split(',', StringSplitOptions.None);
        if (parts.Length == 0 || parts.Any(value => string.IsNullOrWhiteSpace(value)))
            throw new ProfileException("PROFILE_INVALID_REQUEST", "restart --projects requires none or one or more aliases.");
        List<string> aliases = parts.Select(value => value.Trim()).ToList();
        return new RestartArguments { LeaseId = leaseId, HasProjects = true, Projects = aliases,
            TestInputs = testInputs };
    }

    private ModProfile ResolveRequestedProfile(IReadOnlyList<string> aliases,
        IEnumerable<TestInputAssignment> testInputAssignments = null)
    {
        string baselineFingerprint;
        lock (gate)
            baselineFingerprint = ReadBaselineFingerprintLocked();
        return ModProfileResolver.Resolve(root, baselineFingerprint, aliases, options.InstalledModsRoots,
            options.RimBridgeMode, testInputAssignments);
    }

    private ModProfile CreateBaselineProfileForMode(string baselineFingerprint,
        IEnumerable<TestInputAssignment> testInputAssignments = null)
    {
        return ModProfileResolver.CreateBaselineProfile(baselineFingerprint, options.RimBridgeMode,
            root, options.InstalledModsRoots, testInputAssignments);
    }

    private ModProfile ResolveAggregateProfile(IReadOnlyList<string> aliases,
        IEnumerable<TestInputAssignment> testInputAssignments = null)
    {
        string baselineFingerprint;
        lock (gate)
        {
            baselineFingerprint = ReadBaselineFingerprintLocked();
            if (string.IsNullOrWhiteSpace(baselineFingerprint))
            {
                if (!File.Exists(modsConfigPath))
                    throw new ProfileException("PROFILE_MODS_CONFIG_MISSING",
                        "ModsConfig.xml was not found at " + modsConfigPath + ".");
                try
                {
                    baselineFingerprint = HashBytes(File.ReadAllBytes(modsConfigPath));
                }
                catch (Exception exception)
                {
                    throw new ProfileException("PROFILE_BASELINE_MISSING",
                        "The current ModsConfig.xml could not be read as the first aggregate baseline: " + exception.Message);
                }
            }
        }

        return ModProfileResolver.Resolve(root, baselineFingerprint,
            CanonicalProjectUnion(aliases), options.InstalledModsRoots, options.RimBridgeMode,
            testInputAssignments);
    }

    private List<string> AggregateAliasesLocked(RestartArguments arguments)
    {
        IEnumerable<string> aliases = ActiveProjectIntentsLocked().SelectMany(value => value.RequestedProjects);
        if (arguments.HasProjects && arguments.Projects.Count > 0)
            aliases = aliases.Concat(arguments.Projects);
        return CanonicalProjectUnion(aliases);
    }

    private ProjectIntentRegistration EnsureCompatibilityRegistrationLocked(BridgeRequest request,
        IReadOnlyList<string> aliases)
    {
        if (aliases == null || aliases.Count == 0)
            return null;
        string owner = StableProjectOwner(request);
        string session = StableProjectSession(request);
        ProjectIntentRegistration existing = state.ProjectIntents.FirstOrDefault(value =>
            string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal) &&
            string.Equals(value.Owner, owner, StringComparison.Ordinal) &&
            string.Equals(value.SessionId, session, StringComparison.Ordinal) &&
            SequenceEqualAliases(value.RequestedProjects, aliases));
        if (existing != null)
        {
            TouchProjectIntentLocked(existing);
            return existing;
        }

        DateTime now = clock.UtcNow;
        ProjectIntentRegistration registration = new()
        {
            Id = NewProjectIntentIdLocked(owner, session, aliases),
            Owner = owner,
            SessionId = session,
            ClientProcessId = request.ClientProcessId,
            RequestedProjects = aliases.ToList(),
            CreatedUtc = now,
            LastHeartbeatUtc = now,
            ExpiresUtc = now.Add(options.ProjectIntentDuration),
            Status = "ACTIVE"
        };
        state.ProjectIntents.Add(registration);
        return registration;
    }

    private void EnsureAggregateBaselineLocked(string expectedFingerprint)
    {
        string sidecarFingerprint = ReadBaselineFingerprintLocked();
        if (!string.IsNullOrWhiteSpace(sidecarFingerprint))
        {
            if (!string.Equals(sidecarFingerprint, expectedFingerprint, StringComparison.Ordinal))
                throw new ProfileException("PROFILE_BASELINE_CHANGED",
                    "The durable aggregate baseline changed while the profile was resolving.");
            state.BaselineFingerprint = sidecarFingerprint;
            return;
        }

        if (!File.Exists(modsConfigPath))
            throw new ProfileException("PROFILE_MODS_CONFIG_MISSING",
                "ModsConfig.xml was not found at " + modsConfigPath + ".");
        byte[] current;
        try { current = File.ReadAllBytes(modsConfigPath); }
        catch (Exception exception)
        {
            throw new ProfileException("PROFILE_BASELINE_MISSING",
                "The current ModsConfig.xml could not be read as the aggregate baseline: " + exception.Message);
        }
        if (!string.Equals(HashBytes(current), expectedFingerprint, StringComparison.Ordinal))
            throw new ProfileException("PROFILE_BASELINE_CHANGED",
                "ModsConfig.xml changed while the aggregate profile was resolving; no profile was installed.");

        // The first aggregate launch adopts the exact pre-DevBridge bytes as a
        // durable safety baseline. It does not write ModsConfig.xml and is done
        // only after metadata resolution succeeded.
        AtomicWriteFile(baselinePath, current);
        state.BaselineFingerprint = expectedFingerprint;
        state.LastKnownGoodProfile ??= PersistedProfileSnapshot.FromModProfile(
            CreateBaselineProfileForMode(expectedFingerprint));
    }

    private void FreezeAggregateLocked(ModProfile profile, IReadOnlyList<ProjectIntentRegistration> registrations,
        int targetGeneration, string owner, string requestKey)
    {
        ModProfileResolver.ValidateResolvedProfile(profile);
        List<ProjectIntentSnapshot> frozenRegistrations = (registrations ?? Array.Empty<ProjectIntentRegistration>())
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .Select(SnapshotProjectIntent).ToList();
        state.FrozenRegistrations = frozenRegistrations;
        state.FrozenRequestedProjects = profile.RequestedProjects.ToList();
        state.FrozenResolvedProjectPackageIds = profile.ResolvedProjectPackageIds.ToList();
        state.FrozenResolvedMods = profile.ResolvedMods.ToList();
        state.FrozenTestInputs = TestGenerationInputs.CloneValues(profile.TestInputs);
        state.FrozenProfileFingerprint = profile.ProfileFingerprint;
        state.FrozenBaselineFingerprint = profile.BaselineFingerprint;
        state.FrozenTargetGeneration = targetGeneration;
        state.FrozenLaunchOwner = owner;
        state.FrozenLaunchRequestKey = requestKey;
        state.AggregateFreezePending = true;
        state.AggregateFreezeRequestedUtc = clock.UtcNow;
        state.AggregateFrozenUtc = clock.UtcNow;
        state.AggregateGenerations ??= new List<AggregateGenerationEvidence>();
        state.AggregateGenerations.Add(new AggregateGenerationEvidence
        {
            Generation = targetGeneration,
            FrozenUtc = clock.UtcNow,
            LaunchOwner = owner,
            LaunchRequestKey = requestKey,
            ProfileMode = state.LaunchProfileMode,
            Registrations = frozenRegistrations.Select(value => new ProjectIntentSnapshot
            {
                Id = value.Id,
                Owner = value.Owner,
                SessionId = value.SessionId,
                RequestedProjects = value.RequestedProjects.ToList()
            }).ToList(),
            RequestedProjects = profile.RequestedProjects.ToList(),
            ResolvedProjectPackageIds = profile.ResolvedProjectPackageIds.ToList(),
            ResolvedMods = profile.ResolvedMods.ToList(),
            TestInputs = TestGenerationInputs.CloneValues(profile.TestInputs),
            ProfileFingerprint = profile.ProfileFingerprint,
            BaselineFingerprint = profile.BaselineFingerprint
        });
        while (state.AggregateGenerations.Count > 16)
            state.AggregateGenerations.RemoveAt(0);
        SaveStateLocked();
        InjectFaultForTesting(CoordinatorFaultPoint.DuringProjectAggregateFreeze);
    }

    private bool ProfileRequestMatchesLocked(RestartArguments arguments, ModProfile requestedProfile)
    {
        if (arguments.HasProjects)
        {
            try
            {
                IReadOnlyList<string> canonical = ModProfileResolver.CanonicalAliases(arguments.Projects);
                if (canonical.Count == 0)
                    return state.ProfileMode == ModProfile.BaselineMode &&
                        (state.RequestedProjects?.Count ?? 0) == 0;
                return state.ProfileMode == ModProfile.ProjectsMode &&
                    (state.RequestedProjects ?? new List<string>()).SequenceEqual(canonical, StringComparer.Ordinal);
            }
            catch (ProfileException)
            {
                return false;
            }
        }
        return state.ProfileMode == ModProfile.LegacyMode;
    }

}
