using System.Globalization;

namespace DevBridge.Coordinator;

internal sealed partial class CoordinatorState
{
    private int ResolveProjectPlan(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        string value = null;
        bool explain = false;
        List<TestInputAssignment> inputAssignments = new();
        for (int index = 1; index < (arguments?.Count ?? 0); index++)
        {
            string argument = arguments[index]?.Trim() ?? string.Empty;
            if (string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase))
            {
                request.Json = true;
                continue;
            }
            if (string.Equals(argument, "--explain", StringComparison.OrdinalIgnoreCase))
            {
                explain = true;
                continue;
            }
            if (string.Equals(argument, "--input", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--input=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = argument.StartsWith("--input=", StringComparison.OrdinalIgnoreCase)
                    ? argument.Substring("--input=".Length)
                    : (++index < arguments.Count ? arguments[index] : null);
                try
                {
                    inputAssignments.Add(TestGenerationInputs.ParseCommandAssignment(raw));
                }
                catch (ProfileException exception)
                {
                    ProjectResolutionResult failure = CreateProjectResolutionFailure(
                        string.IsNullOrWhiteSpace(value) ? new List<string>() : value.Split(',').ToList(),
                        exception.Code, exception.Message);
                    request.ProjectResolutionResult = failure;
                    EmitProjectResolution(failure, explain, emit);
                    return 4;
                }
                continue;
            }
            if (argument.StartsWith("--", StringComparison.Ordinal))
                return ProjectResolveUsage(emit, "unknown project resolve option '" + argument + "'");
            if (value != null)
                return ProjectResolveUsage(emit, "project resolve accepts one comma-separated alias value");
            value = argument;
        }

        if (string.IsNullOrWhiteSpace(value))
            return ProjectResolveUsage(emit, "project resolve requires one or more aliases, or none");

        List<string> requested = string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
            ? new List<string>()
            : value.Split(',', StringSplitOptions.None).Select(item => item.Trim()).ToList();
        IReadOnlyList<string> canonical;
        try
        {
            canonical = ModProfileResolver.CanonicalAliases(requested);
        }
        catch (ProfileException exception)
        {
            ProjectResolutionResult failure = CreateProjectResolutionFailure(requested, exception.Code,
                exception.Message);
            request.ProjectResolutionResult = failure;
            EmitProjectResolution(failure, explain, emit);
            return 4;
        }

        PersistedState snapshot;
        string baselineFingerprint;
        GenerationHistoryView history;
        try
        {
            lock (gate)
            {
                // This is deliberately a read-only snapshot. In particular, do
                // not call SynchronizeLocked, PruneProjectIntentsLocked, or any
                // baseline-adoption helper from the planning command.
                snapshot = CloneStateLocked();
                baselineFingerprint = ReadPlanningBaselineFingerprintLocked();
                history = BuildGenerationHistoryViewLocked(snapshot.Generation);
            }
        }
        catch (ProfileException exception)
        {
            ProjectResolutionResult failure = CreateProjectResolutionFailure(requested, exception.Code,
                exception.Message);
            request.ProjectResolutionResult = failure;
            EmitProjectResolution(failure, explain, emit);
            return 4;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            ProjectResolutionResult failure = CreateProjectResolutionFailure(requested, "PROFILE_BASELINE_MISSING",
                "The planning baseline could not be read; no runtime state was changed.");
            request.ProjectResolutionResult = failure;
            EmitProjectResolution(failure, explain, emit);
            return 4;
        }

        ModProfile profile;
        try
        {
            profile = ModProfileResolver.Resolve(root, baselineFingerprint, canonical,
                options.InstalledModsRoots, options.RimBridgeMode, inputAssignments);
        }
        catch (ProfileException exception)
        {
            ProjectResolutionResult failure = CreateProjectResolutionFailure(requested, exception.Code,
                exception.Message);
            failure.CanonicalProjects = canonical.ToList();
            failure.BaselineFingerprint = baselineFingerprint;
            failure.CurrentGeneration = history.CurrentGeneration;
            failure.CurrentGenerationTrust = CurrentGenerationTrust(history, snapshot);
            failure.NextGenerationConfig = new ConfigurationHealth
            {
                Valid = false,
                ErrorCode = exception.Code,
                Error = SafePlanningText(exception.Message)
            };
            request.ProjectResolutionResult = failure;
            EmitProjectResolution(failure, explain, emit);
            return 4;
        }

        ProjectResolutionResult result = BuildProjectResolutionResult(requested, canonical, profile,
            snapshot, history);
        request.ProjectResolutionResult = result;
        EmitProjectResolution(result, explain, emit);
        return result.Success ? 0 : 4;
    }

    private ProjectResolutionResult BuildProjectResolutionResult(IReadOnlyList<string> requested,
        IReadOnlyList<string> canonical, ModProfile profile, PersistedState snapshot,
        GenerationHistoryView history)
    {
        ProjectResolutionResult result = new()
        {
            Success = true,
            RequestedProjects = (canonical ?? Array.Empty<string>()).ToList(),
            CanonicalProjects = (canonical ?? Array.Empty<string>()).ToList(),
            RequestedPackageIds = profile.ResolvedProjectPackageIds.ToList(),
            ResolvedMods = profile.ResolvedMods.ToList(),
            ResolvedProjectPackageIds = profile.ResolvedProjectPackageIds.ToList(),
            TestInputs = TestGenerationInputs.CloneValues(profile.TestInputs),
            DependencyEdges = profile.ResolutionEdges.Select(value => new ProfileResolutionEdge
            {
                FromPackageId = value.FromPackageId,
                ToPackageId = value.ToPackageId,
                Kind = value.Kind
            }).ToList(),
            Provenance = profile.Provenance.Select(value => new ProjectResolutionMod
            {
                PackageId = value.PackageId,
                Reasons = value.Reasons.Select(reason => new ProjectResolutionReason
                {
                    Category = reason.Category,
                    RelatedPackageId = reason.RelatedPackageId,
                    Detail = reason.Detail
                }).ToList()
            }).ToList(),
            ProfileFingerprint = profile.ProfileFingerprint,
            BaselineFingerprint = profile.BaselineFingerprint,
            CurrentGeneration = history.CurrentGeneration,
            CurrentGenerationTrust = CurrentGenerationTrust(history, snapshot),
            NextGenerationConfig = new ConfigurationHealth { Valid = true },
            NextActions = new List<string> { "DevBridge.cmd project register " + string.Join(",", canonical) }
        };

        if (history.Corrupt && snapshot.Generation > 0)
        {
            result.Success = false;
            result.ErrorCode = history.ErrorCode ?? "GENERATION_HISTORY_CORRUPT";
            result.Errors.Add(new ProjectResolutionIssue
            {
                Code = result.ErrorCode,
                Message = "The pinned current-generation history is corrupt; planning did not mutate runtime state."
            });
            result.NextGenerationConfig = new ConfigurationHealth
            {
                Valid = false,
                ErrorCode = result.ErrorCode,
                Error = "Pinned current-generation evidence is unavailable."
            };
            result.NextActions = new List<string> { "DevBridge.cmd doctor --json", "DevBridge.cmd history --json" };
            return result;
        }

        if (history.Current?.Manifest != null)
        {
            result.Comparison = ComparePlanningProfile(profile, history.Current);
            result.WouldDifferFromCurrent = result.Comparison.WouldDifferFromCurrent;
            result.WouldRequireRestart = result.Comparison.WouldRequireRestart;
            result.NextActions = result.WouldRequireRestart
                ? new List<string>
                {
                    "DevBridge.cmd project register " + string.Join(",", canonical),
                    "DevBridge.cmd restart"
                }
                : new List<string> { "DevBridge.cmd status --json" };
        }
        else if (snapshot.Generation > 0)
        {
            result.Warnings.Add("The running generation has no trusted pinned manifest; comparison is unavailable.");
        }

        return result;
    }

    private static ProjectResolutionComparison ComparePlanningProfile(ModProfile profile,
        GenerationHistoryEntry current)
    {
        GenerationProfileEvidence pinned = current?.Manifest?.Profile;
        List<string> pinnedMods = pinned?.ResolvedMods ?? new List<string>();
        List<string> plannedMods = profile.ResolvedMods ?? new List<string>();
        List<string> added = plannedMods.Except(pinnedMods, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        List<string> removed = pinnedMods.Except(plannedMods, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        bool orderChanged = !plannedMods.SequenceEqual(pinnedMods, StringComparer.OrdinalIgnoreCase);
        bool projectIntentChanged = !(profile.RequestedProjects ?? new List<string>()).SequenceEqual(
            pinned?.RequestedProjects ?? new List<string>(), StringComparer.Ordinal);
        bool fingerprintChanged = !string.Equals(profile.ProfileFingerprint,
            pinned?.ProfileFingerprint, StringComparison.Ordinal);
        bool testInputsChanged = !TestGenerationInputs.AreEquivalent(profile.TestInputs, pinned?.TestInputs);
        bool differs = added.Count > 0 || removed.Count > 0 || orderChanged || projectIntentChanged ||
            fingerprintChanged || testInputsChanged;
        return new ProjectResolutionComparison
        {
            ComparedGeneration = current.Record.Generation,
            PackagesAdded = added,
            PackagesRemoved = removed,
            OrderChanged = orderChanged,
            ProjectIntentChanged = projectIntentChanged,
            FingerprintChanged = fingerprintChanged,
            TestInputsChanged = testInputsChanged,
            WouldDifferFromCurrent = differs,
            WouldRequireRestart = differs
        };
    }

    private string ReadPlanningBaselineFingerprintLocked()
    {
        string sidecarFingerprint = ReadBaselineFingerprintLocked();
        if (!string.IsNullOrWhiteSpace(sidecarFingerprint))
            return sidecarFingerprint;
        if (!File.Exists(modsConfigPath))
            throw new ProfileException("PROFILE_MODS_CONFIG_MISSING",
                "ModsConfig.xml was not found at the coordinator runtime path.");
        try
        {
            return HashBytes(File.ReadAllBytes(modsConfigPath));
        }
        catch (Exception exception)
        {
            throw new ProfileException("PROFILE_BASELINE_MISSING",
                "The current ModsConfig.xml could not be read as a planning baseline: " +
                SafePlanningText(exception.Message));
        }
    }

    private ConfigurationHealth EvaluateFutureConfigurationLocked(PersistedState snapshot)
    {
        try
        {
            List<string> aliases = CanonicalProjectUnion(ActiveProjectIntentsLocked(snapshot)
                .SelectMany(value => value.RequestedProjects));
            string baseline = ReadPlanningBaselineFingerprintLocked();
            ModProfileResolver.Resolve(root, baseline, aliases, options.InstalledModsRoots, options.RimBridgeMode,
                (snapshot.TestInputs ?? new List<TestInputValue>()).Select(value =>
                    new TestInputAssignment { Name = value.Name, Value = value.Value }));
            return new ConfigurationHealth { Valid = true };
        }
        catch (ProfileException exception)
        {
            return new ConfigurationHealth
            {
                Valid = false,
                ErrorCode = exception.Code,
                Error = SafePlanningText(exception.Message)
            };
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            return new ConfigurationHealth
            {
                Valid = false,
                ErrorCode = "PROFILE_BASELINE_MISSING",
                Error = "The planning baseline could not be read."
            };
        }
    }

    private static string CurrentGenerationTrust(GenerationHistoryView history, PersistedState snapshot)
    {
        if (history?.Corrupt == true)
            return "CORRUPT";
        if (history?.Current?.Manifest != null)
            return "VALID";
        return snapshot?.Generation > 0 ? "UNKNOWN" : "NONE";
    }

    private static ProjectResolutionResult CreateProjectResolutionFailure(IReadOnlyList<string> requested,
        string code, string message)
    {
        string safeCode = string.IsNullOrWhiteSpace(code) ? "PROFILE_RESOLUTION_FAILED" : code;
        string safeMessage = SafePlanningText(message);
        return new ProjectResolutionResult
        {
            Success = false,
            RequestedProjects = (requested ?? Array.Empty<string>()).ToList(),
            ErrorCode = safeCode,
            Errors = new List<ProjectResolutionIssue>
            {
                new() { Code = safeCode, Message = safeMessage }
            },
            NextGenerationConfig = new ConfigurationHealth
            {
                Valid = false,
                ErrorCode = safeCode,
                Error = safeMessage
            },
            NextActions = new List<string> { "DevBridge.cmd doctor --json" }
        };
    }

    private static int ProjectResolveUsage(Action<string> emit, string detail)
    {
        emit("Project resolution denied: " + detail + ".");
        emit("Usage: DevBridge.cmd project resolve <alias[,alias...]> [--explain] [--json]");
        emit("Error code: PROJECT_RESOLVE_INVALID");
        return 2;
    }

    private static string SafePlanningText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Project profile resolution failed.";
        string safe = DiagnosticRedactor.Text(value.Trim());
        return safe.Length <= 512 ? safe : safe.Substring(0, 512);
    }

    private static void EmitProjectResolution(ProjectResolutionResult result, bool explain, Action<string> emit)
    {
        if (result == null)
            return;
        emit(result.Success ? "Project resolution plan is valid." :
            "Project resolution plan is invalid: " + (result.ErrorCode ?? "PROFILE_RESOLUTION_FAILED") + ".");
        emit("Canonical projects: " + (result.CanonicalProjects.Count == 0 ? "none" :
            string.Join(", ", result.CanonicalProjects)));
        emit("Resolved mod order: " + (result.ResolvedMods.Count == 0 ? "none" :
            string.Join(" -> ", result.ResolvedMods)));
        emit("Profile fingerprint: " + (result.ProfileFingerprint ?? "none"));
        emit("Current pinned generation: " + result.CurrentGeneration.ToString(CultureInfo.InvariantCulture) +
            " trust=" + result.CurrentGenerationTrust);
        emit("Would differ from current: " + result.WouldDifferFromCurrent +
            "; would require restart: " + result.WouldRequireRestart);
        if (explain && result.Provenance.Count > 0)
        {
            emit("Provenance:");
            foreach (ProjectResolutionMod mod in result.Provenance)
            {
                emit("  " + mod.PackageId);
                foreach (ProjectResolutionReason reason in mod.Reasons)
                    emit("    " + reason.Category + (string.IsNullOrWhiteSpace(reason.RelatedPackageId)
                        ? string.Empty : " -> " + reason.RelatedPackageId));
            }
        }
        foreach (ProjectResolutionIssue error in result.Errors)
            emit("Error: " + error.Code + " - " + error.Message);
        foreach (string warning in result.Warnings)
            emit("Warning: " + warning);
        foreach (string nextAction in result.NextActions)
            emit("Next action: " + nextAction);
    }

    private static bool IsProjectResolveCommand(BridgeRequest request)
    {
        if (request == null || !string.Equals(request.Command?.Trim(), "project", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Command?.Trim(), "projects", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Command?.Trim(), "intent", StringComparison.OrdinalIgnoreCase))
            return false;
        return request.Arguments != null && request.Arguments.Count > 0 &&
            string.Equals(request.Arguments[0]?.Trim(), "resolve", StringComparison.OrdinalIgnoreCase);
    }
}
