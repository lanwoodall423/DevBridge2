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
    private static bool IsTerminalIsolationStatus(string status) =>
        string.Equals(status, "COMPLETED", StringComparison.Ordinal) ||
        string.Equals(status, "ENVIRONMENTAL_FAILURE", StringComparison.Ordinal) ||
        string.Equals(status, "INCONCLUSIVE", StringComparison.Ordinal);

    private bool IsolationActiveLocked() => state.CrashIsolation != null &&
        !IsTerminalIsolationStatus(state.CrashIsolation.Status);

    private bool IsolationLaunchStateMatchesLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null || string.IsNullOrWhiteSpace(incident.CurrentAttemptId) ||
            !string.Equals(state.ProfileFingerprint, incident.OriginalProfileFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(state.BaselineFingerprint, incident.OriginalBaselineFingerprint,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(state.LaunchId) ||
            state.LaunchGeneration <= 0 || state.LaunchGeneration != state.TargetGeneration ||
            !state.RestartPending ||
            !string.Equals(state.LaunchOwner, "isolation@" + runtimeSlotId, StringComparison.Ordinal) ||
            !string.Equals(state.LaunchRequestKey, incident.CurrentAttemptId, StringComparison.Ordinal) ||
            !string.Equals(state.LaunchProfileFingerprint, incident.CurrentAttemptFingerprint,
                StringComparison.Ordinal) || incident.CurrentAttemptProfile == null ||
            !string.Equals(incident.CurrentAttemptProfile.ProfileFingerprint,
                incident.CurrentAttemptFingerprint, StringComparison.Ordinal) ||
            !state.LaunchProfileInstalled || !state.LaunchAttemptStarted)
            return false;
        try
        {
            ModProfileResolver.ValidateResolvedProfile(incident.CurrentAttemptProfile.ToModProfile());
            return true;
        }
        catch (ProfileException)
        {
            return false;
        }
    }

    private void ResumePersistedIsolationResultLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null)
            return;
        if (string.Equals(incident.CurrentAttemptResult, "UNSAFE", StringComparison.Ordinal))
        {
            FinalizeIsolationEnvironmentalLocked(
                incident.CurrentAttemptFailureCode ?? "ISOLATION_UNSAFE_RESULT",
                incident.CurrentAttemptFailureDetail ??
                "the persisted isolation attempt did not produce safe profile-failure evidence");
        }
        else if (IsolationLaunchStateMatchesLocked())
            StartIsolationWorkerLocked();
        else
            FinalizeIsolationEnvironmentalLocked("ISOLATION_PROFILE_MISMATCH",
                "the persisted terminal isolation attempt does not match its durable launch intent; no replacement launch was attempted");
    }

    private void ArchiveCompletedIsolationLocked()
    {
        if (state.CrashIsolation == null || IsolationActiveLocked())
            return;
        state.CrashIsolationHistory ??= new List<CrashIsolationIncident>();
        state.CrashIsolationHistory.Add(state.CrashIsolation);
        while (state.CrashIsolationHistory.Count > 8)
            state.CrashIsolationHistory.RemoveAt(0);
        state.CrashIsolation = null;
    }

    private bool IsEligibleForCrashIsolationLocked(string errorCode)
    {
        if (!IsIsolationEvidenceFailure(errorCode))
            return false;
        if (state.ProfileMode != ModProfile.ProjectsMode ||
            string.IsNullOrWhiteSpace(state.ProfileFingerprint) ||
            !state.LaunchProfileInstalled || !state.LaunchAttemptStarted ||
            !string.Equals(state.LaunchProfileFingerprint, state.ProfileFingerprint,
                StringComparison.Ordinal) ||
            state.ProcessId <= 0 || state.ProcessStartUtcTicks <= 0 ||
            state.CrashIsolation != null || state.Leases.Count != 0 ||
            state.MaintenanceReady)
            return false;

        // SessionDirty describes the preceding stopped/maintenance session. It
        // is not evidence against the freshly installed, accepted project
        // launch when all of the authoritative launch-boundary checks below
        // have succeeded. Active maintenance remains rejected above.
        if (state.SessionDirty && !IsVerifiedFreshProjectLaunchLocked())
            return false;

        if (errorCode == ProcessInspection.ErrorCode ||
            errorCode == "PROCESS_IDENTITY_CHANGED" ||
            errorCode == "LAUNCH_RECOVERY_AMBIGUOUS" ||
            errorCode == "ISOLATION_RECOVERY_AMBIGUOUS" ||
            errorCode == "LAUNCH_OWNER_MISSING" ||
            errorCode == "LAUNCH_BUDGET_EXHAUSTED" ||
            errorCode == "MAINTENANCE_PROCESS_PRESENT" ||
            errorCode == "PROFILE_CONFLICT" ||
            errorCode == "PROFILE_RESTART_PENDING" ||
            errorCode.StartsWith("PROFILE_", StringComparison.Ordinal) ||
            errorCode.StartsWith("MODS_CONFIG_", StringComparison.Ordinal))
            return false;

        // A failed ownership/lease/maintenance check is not evidence about the
        // managed profile, even when a previous write happened in this generation.
        if (errorCode.Contains("LEASE", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("MAINTENANCE", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("OWNERSHIP", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("PROCESS_INSPECTION", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool IsIsolationEvidenceFailure(string errorCode) =>
        string.Equals(errorCode, "PROCESS_EXITED", StringComparison.Ordinal) ||
        string.Equals(errorCode, "READINESS_TIMEOUT", StringComparison.Ordinal) ||
        string.Equals(errorCode, QuicktestFailureArtifact.StableFailureCode, StringComparison.Ordinal);

    private bool IsVerifiedFreshProjectLaunchLocked()
    {
        if (state.ProfileMode != ModProfile.ProjectsMode ||
            string.IsNullOrWhiteSpace(state.ProfileFingerprint) ||
            !state.LaunchProfileInstalled || !state.LaunchAttemptStarted ||
            !string.Equals(state.LaunchProfileFingerprint, state.ProfileFingerprint,
                StringComparison.Ordinal) || string.IsNullOrWhiteSpace(state.LaunchId) ||
            state.LaunchGeneration <= 0 || state.ProcessId <= 0 ||
            state.ProcessStartUtcTicks <= 0 || state.ModsConfigGeneratedGeneration != state.LaunchGeneration ||
            !string.Equals(state.ModsConfigGeneratedProfileFingerprint, state.ProfileFingerprint,
                StringComparison.Ordinal))
            return false;

        return string.Equals(CurrentModsConfigOwnershipLocked(), "DEVBRIDGE_GENERATED",
            StringComparison.Ordinal);
    }

    private void BeginCrashIsolationLocked(string detail, string errorCode,
        QuicktestFailureRecord failure = null)
    {
        ModProfile accepted = ProfileFromStateLocked();
        if (accepted == null)
            return;

        List<string> projects = (accepted.ResolvedProjectPackageIds ?? new List<string>())
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();
        CrashIsolationIncident incident = new()
        {
            IncidentId = DeterministicIsolationId("incident", accepted.ProfileFingerprint),
            Status = "RUNNING",
            Stage = "CONTROL",
            OriginalProfileMode = accepted.Mode,
            OriginalRequestedProjects = (accepted.RequestedProjects ?? new List<string>()).ToList(),
            OriginalResolvedProjectPackageIds = (accepted.ResolvedProjectPackageIds ?? new List<string>()).ToList(),
            OriginalResolvedMods = (accepted.ResolvedMods ?? new List<string>()).ToList(),
            OriginalProfileFingerprint = accepted.ProfileFingerprint,
            OriginalBaselineFingerprint = accepted.BaselineFingerprint,
            OriginalLastKnownGoodFingerprint = state.LastKnownGoodProfile?.ProfileFingerprint ??
                accepted.BaselineFingerprint,
            OriginalGeneration = state.LaunchGeneration > 0 ? state.LaunchGeneration : state.Generation,
            OriginalLaunchId = state.LaunchId,
            OriginalProcessId = state.ProcessId,
            OriginalProcessStartUtcTicks = state.ProcessStartUtcTicks,
            OriginalFailureUtc = clock.UtcNow,
            OriginalFailurePhase = failure?.FailurePhase ?? state.Phase.ToString(),
            OriginalFailureCode = errorCode,
            OriginalFailureDetail = detail,
            OriginalFailureSchemaVersion = failure?.SchemaVersion ?? 0,
            OriginalFailureExceptionType = failure?.ExceptionType,
            OriginalFailureExceptionMessage = failure?.ExceptionMessage,
            OriginalFailureDiagnosticDetail = failure?.DiagnosticDetail,
            OriginalProcessExitObserved = errorCode == "PROCESS_EXITED",
            OriginalExitInformation = detail,
            SearchPoolProjects = projects,
            DeltaCurrentProjects = projects.ToList(),
            DeltaGranularity = Math.Min(2, Math.Max(1, projects.Count)),
            IsolationLaunchesRemaining = Math.Max(1, options.IsolationMaxAttempts),
            OriginalRegistrations = (state.FrozenRegistrations ?? new List<ProjectIntentSnapshot>())
                .Select(value => new ProjectIntentSnapshot
                {
                    Id = value.Id,
                    Owner = value.Owner,
                    SessionId = value.SessionId,
                    RequestedProjects = (value.RequestedProjects ?? new List<string>()).ToList()
                }).ToList(),
            ProjectRequesters = BuildProjectRequesterMap(state.FrozenRegistrations)
        };
        incident.OriginalDiagnosticMetadata["acceptedAtUtc"] =
            state.RestartRequestedUtc?.ToUniversalTime().ToString("O") ?? string.Empty;
        incident.OriginalDiagnosticMetadata["modsConfigGeneratedHash"] =
            state.ModsConfigGeneratedHash ?? string.Empty;
        incident.OriginalDiagnosticMetadata["modsConfigGeneratedGeneration"] =
            state.ModsConfigGeneratedGeneration.ToString(CultureInfo.InvariantCulture);

        state.CrashIsolation = incident;
        state.Phase = BridgePhase.ISOLATING;
        state.RestartPending = true;
        state.TargetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
        state.LaunchOwner = "isolation@" + runtimeSlotId;
        state.LaunchRequestKey = "isolation-control-" + incident.IncidentId;
        state.IsolationLaunchesRemaining = incident.IsolationLaunchesRemaining;
        state.WaitingForBridgeDeadlineUtc = null;
        state.ErrorCode = "CRASH_ISOLATION_RUNNING";
        state.Error = "The accepted project profile failed during startup; deterministic crash isolation is running.";
        state.ProfileErrorCode = null;
        state.ProfileError = null;
        SaveStateLocked();
        InjectFaultForTesting(CoordinatorFaultPoint.DuringCrashIsolationAttemptPersistence);
        StartIsolationWorkerLocked();
    }

    private static Dictionary<string, List<ProjectIntentRequester>> BuildProjectRequesterMap(
        IEnumerable<ProjectIntentSnapshot> registrations)
    {
        Dictionary<string, List<ProjectIntentRequester>> result =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectIntentSnapshot registration in registrations ?? Array.Empty<ProjectIntentSnapshot>())
        {
            foreach (string alias in registration.RequestedProjects ?? new List<string>())
            {
                if (!result.TryGetValue(alias, out List<ProjectIntentRequester> requesters))
                {
                    requesters = new List<ProjectIntentRequester>();
                    result[alias] = requesters;
                }
                if (!requesters.Any(value => string.Equals(value.RegistrationId, registration.Id,
                        StringComparison.Ordinal)))
                {
                    requesters.Add(new ProjectIntentRequester
                    {
                        RegistrationId = registration.Id,
                        Owner = registration.Owner,
                        SessionId = registration.SessionId
                    });
                }
            }
        }
        foreach (List<ProjectIntentRequester> requesters in result.Values)
            requesters.Sort((left, right) => StringComparer.Ordinal.Compare(left.RegistrationId, right.RegistrationId));
        return result;
    }

    private void StartIsolationWorkerLocked()
    {
        if (ShutdownRequested || !IsolationActiveLocked() ||
            (isolationTask != null && !isolationTask.IsCompleted))
            return;
        isolationTask = Task.Run(IsolationWorker);
    }

    private void QueueIsolationContinuationLocked()
    {
        if (IsolationActiveLocked() && (isolationTask == null || isolationTask.IsCompleted))
            StartIsolationWorkerLocked();
    }

    private static string DeterministicIsolationId(string kind, string fingerprint)
    {
        string input = (kind ?? string.Empty) + "\n" + (fingerprint ?? string.Empty);
        using SHA256 sha = SHA256.Create();
        return "iso-" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static List<string> StableProjectOrder(IEnumerable<string> projects) =>
        (projects ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();

    private ModProfile BuildIsolationProfileLocked(IReadOnlyList<string> projectPackageIds)
    {
        List<string> aliases = new();
        for (int index = 0; index < state.CrashIsolation.OriginalResolvedProjectPackageIds.Count; index++)
        {
            string packageId = state.CrashIsolation.OriginalResolvedProjectPackageIds[index];
            if (projectPackageIds.Any(value => string.Equals(value, packageId, StringComparison.OrdinalIgnoreCase)))
                aliases.Add(state.CrashIsolation.OriginalRequestedProjects[index]);
        }
        return ModProfileResolver.Resolve(coordinatorRoot, state.CrashIsolation.OriginalBaselineFingerprint,
            aliases, options.InstalledModsRoots);
    }

    private CrashIsolationAttempt FindIsolationAttemptLocked(string attemptId)
    {
        return state.CrashIsolation?.Attempts?.FirstOrDefault(value =>
            string.Equals(value.AttemptId, attemptId, StringComparison.Ordinal));
    }

    private void SetCurrentIsolationAttemptLocked(string kind, ModProfile profile,
        IReadOnlyList<string> projects)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        string attemptId = kind.StartsWith("MINIMIZE_", StringComparison.Ordinal)
            ? DeterministicIsolationId("candidate", profile.ProfileFingerprint)
            : DeterministicIsolationId(kind, profile.ProfileFingerprint);
        CrashIsolationAttempt previous = FindIsolationAttemptLocked(attemptId);
        incident.CurrentAttemptId = attemptId;
        incident.CurrentAttemptKind = kind;
        incident.CurrentAttemptFingerprint = profile.ProfileFingerprint;
        incident.CurrentAttemptProfile = PersistedProfileSnapshot.FromModProfile(profile);
        incident.CurrentAttemptProjects = StableProjectOrder(projects);
        incident.CurrentAttemptProfileInstalled = false;
        incident.CurrentAttemptFailurePhase = null;
        incident.CurrentAttemptFailureCode = null;
        incident.CurrentAttemptFailureDetail = null;
        incident.CurrentAttemptResult = previous?.Result;
        if (previous != null)
        {
            incident.CurrentAttemptFailurePhase = previous.FailurePhase;
            incident.CurrentAttemptFailureCode = previous.FailureCode;
            incident.CurrentAttemptFailureDetail = previous.FailureDetail;
            incident.CurrentAttemptProfileInstalled = previous.ProfileInstalled;
        }
        state.TargetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
        state.LaunchOwner = "isolation@" + runtimeSlotId;
        state.LaunchRequestKey = attemptId;
        state.RestartPending = true;
        state.Phase = BridgePhase.ISOLATING;
        SaveStateLocked();
        InjectFaultForTesting(CoordinatorFaultPoint.DuringCrashIsolationAttemptPersistence);
    }

    private List<CrashIsolationSelection> PartitionCandidatesLocked(List<string> current,
        int granularity, bool complements)
    {
        current = StableProjectOrder(current);
        int count = Math.Min(Math.Max(2, granularity), current.Count);
        List<CrashIsolationSelection> result = new();
        for (int part = 0; part < count; part++)
        {
            List<string> selected = current.Where((_, index) => index % count == part).ToList();
            List<string> candidate = complements
                ? current.Where(value => !selected.Contains(value, StringComparer.OrdinalIgnoreCase)).ToList()
                : selected;
            if (candidate.Count == 0 || candidate.Count == current.Count)
                continue;
            result.Add(new CrashIsolationSelection { Projects = StableProjectOrder(candidate) });
        }
        return result;
    }

    private void StartIsolationRoundLocked(bool complements, bool persist = true)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        List<string> current = StableProjectOrder(incident.DeltaCurrentProjects);
        if (current.Count <= 1)
        {
            CompleteMinimalSetLocked(current, persist);
            return;
        }
        int n = Math.Min(current.Count, Math.Max(2, incident.DeltaGranularity));
        incident.PendingKind = complements ? "REMOVE" : "DIRECT";
        incident.PendingCandidates = PartitionCandidatesLocked(current, n, complements);
        incident.PendingCandidateIndex = 0;
        if (incident.PendingCandidates.Count == 0)
            CompleteMinimalSetLocked(current, persist);
        else if (persist)
            SaveStateLocked();
    }

    private void CompleteMinimalSetLocked(IReadOnlyList<string> projects, bool persist = true)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        List<string> minimal = StableProjectOrder(projects);
        ModProfile minimalProfile = BuildIsolationProfileLocked(minimal);
        CrashIsolationDiagnosis diagnosis = new()
        {
            Code = minimal.Count == 1 ? "PROJECT_OR_REQUIRED_DEPENDENCY_CLOSURE" :
                "MINIMAL_INCOMPATIBLE_PROJECT_SET",
            Message = minimal.Count == 1
                ? "Project " + minimal[0] + " or its required dependency closure causes the startup failure."
                : "The minimal incompatible project set is: " + string.Join(", ", minimal) + ".",
            ResolvedProjectPackageIds = minimal.ToList(),
            RequestedProjects = RequestedAliasesForPackagesLocked(minimal),
            ProfileFingerprint = minimalProfile.ProfileFingerprint
        };
        incident.Diagnoses.Add(diagnosis);
        if (incident.Diagnoses.Count == 1)
        {
            incident.DiagnosisCode = diagnosis.Code;
            incident.Diagnosis = diagnosis.Message;
        }
        else
        {
            incident.DiagnosisCode = "MULTIPLE_INDEPENDENT_FAILING_PROJECT_SETS";
            incident.Diagnosis = "Multiple independent failing project sets were isolated: " +
                string.Join("; ", incident.Diagnoses.Select(value =>
                    "[" + string.Join(", ", value.ResolvedProjectPackageIds) + "]")) + ".";
        }
        incident.SearchPoolProjects = incident.SearchPoolProjects
            .Where(value => !minimal.Contains(value, StringComparer.OrdinalIgnoreCase)).ToList();
        incident.DeltaCurrentProjects = incident.SearchPoolProjects.ToList();
        incident.PendingCandidates = new List<CrashIsolationSelection>();
        incident.PendingCandidateIndex = 0;
        incident.PendingKind = null;
        if (incident.SearchPoolProjects.Count == 0)
            incident.Stage = "FINAL_CONTROL";
        else
        {
            incident.Stage = "VERIFY_REMAINDER";
            incident.DeltaGranularity = Math.Min(2, incident.SearchPoolProjects.Count);
        }
        if (persist)
            SaveStateLocked();
    }

    private List<string> RequestedAliasesForPackagesLocked(IEnumerable<string> packages)
    {
        HashSet<string> wanted = new(packages ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        List<string> aliases = new();
        for (int index = 0; index < state.CrashIsolation.OriginalResolvedProjectPackageIds.Count; index++)
        {
            if (wanted.Contains(state.CrashIsolation.OriginalResolvedProjectPackageIds[index]))
                aliases.Add(state.CrashIsolation.OriginalRequestedProjects[index]);
        }
        return aliases.OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    private bool TryPlanIsolationAttemptLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null || incident.CurrentAttemptId != null || IsTerminalIsolationStatus(incident.Status))
            return false;

        if (incident.Stage != "CONTROL" && incident.Stage != "REPRODUCE" &&
            incident.Stage != "VERIFY_REMAINDER" && incident.Stage != "MINIMIZE" &&
            incident.Stage != "FINAL_CONTROL" && incident.Stage != "FINAL_BASELINE_CONTROL")
        {
            FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_STATE_INVALID",
                "the durable isolation incident contains an unknown search phase; no project was attributed");
            return false;
        }
        if (incident.Stage == "MINIMIZE" && incident.PendingKind != null &&
            incident.PendingKind != "DIRECT" && incident.PendingKind != "REMOVE")
        {
            FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_STATE_INVALID",
                "the durable isolation incident contains an unknown candidate partition kind; no project was attributed");
            return false;
        }

        while (true)
        {
            ModProfile profile;
            string kind;
            List<string> projects;
            if (incident.Stage == "CONTROL")
            {
                profile = state.LastKnownGoodProfile?.ToModProfile() ??
                    CreateBaselineProfileForMode(incident.OriginalBaselineFingerprint);
                kind = "CONTROL";
                projects = Array.Empty<string>().ToList();
            }
            else if (incident.Stage == "REPRODUCE")
            {
                profile = new PersistedProfileSnapshot
                {
                    Mode = incident.OriginalProfileMode,
                    RequestedProjects = incident.OriginalRequestedProjects.ToList(),
                    ResolvedProjectPackageIds = incident.OriginalResolvedProjectPackageIds.ToList(),
                    ResolvedMods = incident.OriginalResolvedMods.ToList(),
                    ProfileFingerprint = incident.OriginalProfileFingerprint,
                    BaselineFingerprint = incident.OriginalBaselineFingerprint
                }.ToModProfile();
                kind = "REPRODUCE";
                projects = incident.OriginalResolvedProjectPackageIds.ToList();
            }
            else if (incident.Stage == "VERIFY_REMAINDER")
            {
                profile = BuildIsolationProfileLocked(incident.DeltaCurrentProjects);
                kind = "VERIFY_REMAINDER";
                projects = incident.DeltaCurrentProjects.ToList();
            }
            else if (incident.Stage == "FINAL_CONTROL")
            {
                profile = incident.SafeRemainderProfile?.ToModProfile() ??
                    state.LastKnownGoodProfile?.ToModProfile() ??
                    CreateBaselineProfileForMode(incident.OriginalBaselineFingerprint);
                kind = "FINAL_CONTROL";
                projects = incident.SafeRemainderProfile?.ResolvedProjectPackageIds?.ToList() ??
                    Array.Empty<string>().ToList();
            }
            else if (incident.Stage == "FINAL_BASELINE_CONTROL")
            {
                profile = CreateBaselineProfileForMode(incident.OriginalBaselineFingerprint);
                kind = "FINAL_BASELINE_CONTROL";
                projects = Array.Empty<string>().ToList();
            }
            else if (incident.Stage == "MINIMIZE")
            {
                if (incident.PendingCandidates == null ||
                    incident.PendingCandidateIndex >= incident.PendingCandidates.Count)
                {
                    StartIsolationRoundLocked(incident.PendingKind != "DIRECT");
                    if (incident.Stage != "MINIMIZE" || incident.CurrentAttemptId != null)
                        return false;
                    continue;
                }
                projects = StableProjectOrder(incident.PendingCandidates[incident.PendingCandidateIndex].Projects);
                profile = BuildIsolationProfileLocked(projects);
                kind = "MINIMIZE_" + incident.PendingKind;
            }
            else
                return false;

            ModProfileResolver.ValidateResolvedProfile(profile);
            SetCurrentIsolationAttemptLocked(kind, profile, projects);
            return true;
        }
    }

    private bool StopIsolationProcess(out string errorCode, out string error)
    {
        int processId;
        long startTicks;
        lock (gate)
        {
            processId = state.ProcessId;
            startTicks = state.ProcessStartUtcTicks;
        }

        ThrowIfShutdownRequested();
        (bool stopped, string stopCode, string stopError) = StopOwnedProcess(processId, startTicks);
        ThrowIfShutdownRequested();
        if (!stopped)
        {
            errorCode = stopCode;
            error = stopError;
            return false;
        }
        try
        {
            if (FindUnmanagedRimWorldProcesses(0, 0).Count != 0)
            {
                errorCode = "MAINTENANCE_PROCESS_PRESENT";
                error = "a RimWorld process remained after the isolated attempt was stopped";
                return false;
            }
        }
        catch (ProcessInspectionException)
        {
            errorCode = ProcessInspection.ErrorCode;
            error = ProcessInspection.Message;
            return false;
        }
        errorCode = null;
        error = null;
        return true;
    }

    private bool PrepareIsolationAttempt(ModProfile profile, string attemptId, int targetGeneration,
        out string errorCode, out string error)
    {
        if (!StopIsolationProcess(out errorCode, out error))
            return false;

        lock (gate)
        {
            CrashIsolationIncident incident = state.CrashIsolation;
            if (incident == null || !string.Equals(incident.CurrentAttemptId, attemptId, StringComparison.Ordinal))
            {
                errorCode = "CRASH_ISOLATION_STATE_CHANGED";
                error = "the durable isolation attempt changed before launch";
                return false;
            }
            state.TargetGeneration = Math.Max(state.Generation + 1, targetGeneration);
            state.Phase = BridgePhase.RESTARTING;
            state.RestartPending = true;
            state.LaunchOwner = "isolation@" + runtimeSlotId;
            state.LaunchRequestKey = attemptId;
            state.LaunchId = null;
            state.ProcessId = 0;
            state.ProcessStartUtcTicks = 0;
            state.OwnedProcessExecutablePath = null;
            state.LaunchProfileFingerprint = profile.ProfileFingerprint;
            state.LaunchProfileInstalled = false;
            state.LaunchAttemptStarted = false;
            state.RequiresNewProcess = true;
            state.Error = null;
            state.ErrorCode = null;
            DeleteReadinessLocked();
            DeleteQuicktestFailureArtifactLocked();
            SaveStateLocked();
        }
        errorCode = null;
        error = null;
        return true;
    }

    private void StoreCurrentIsolationAttemptLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        CrashIsolationAttempt attempt = FindIsolationAttemptLocked(incident.CurrentAttemptId);
        if (attempt == null)
        {
            attempt = new CrashIsolationAttempt { AttemptId = incident.CurrentAttemptId };
            incident.Attempts.Add(attempt);
        }
        attempt.Kind = incident.CurrentAttemptKind;
        attempt.ProfileFingerprint = incident.CurrentAttemptFingerprint;
        attempt.RequestedProjects = incident.CurrentAttemptProfile?.RequestedProjects?.ToList() ?? new List<string>();
        attempt.ResolvedProjectPackageIds = incident.CurrentAttemptProfile?.ResolvedProjectPackageIds?.ToList() ?? new List<string>();
        attempt.Result = incident.CurrentAttemptResult;
        attempt.Generation = state.LaunchGeneration;
        attempt.ProcessId = state.ProcessId;
        attempt.ProcessStartUtcTicks = state.ProcessStartUtcTicks;
        attempt.CompletedUtc = clock.UtcNow;
        attempt.ProfileInstalled = incident.CurrentAttemptProfileInstalled;
        attempt.ProcessExitObserved = incident.OriginalProcessExitObserved ||
            incident.CurrentAttemptFailureCode == "PROCESS_EXITED";
        attempt.FailurePhase = incident.CurrentAttemptFailurePhase;
        attempt.FailureCode = incident.CurrentAttemptFailureCode;
        attempt.FailureDetail = incident.CurrentAttemptFailureDetail;
        if (attempt.StartedUtc == default)
            attempt.StartedUtc = attempt.CompletedUtc;
    }

    private void ClearCurrentIsolationAttemptLocked(bool persist = true)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        incident.CurrentAttemptId = null;
        incident.CurrentAttemptFingerprint = null;
        incident.CurrentAttemptKind = null;
        incident.CurrentAttemptProfile = null;
        incident.CurrentAttemptProjects = new List<string>();
        incident.CurrentAttemptResult = null;
        incident.CurrentAttemptFailurePhase = null;
        incident.CurrentAttemptFailureCode = null;
        incident.CurrentAttemptFailureDetail = null;
        incident.CurrentAttemptProfileInstalled = false;
        state.ProcessId = 0;
        state.ProcessStartUtcTicks = 0;
        state.OwnedProcessExecutablePath = null;
        state.LaunchId = null;
        state.LaunchProfileFingerprint = null;
        state.LaunchProfileInstalled = false;
        state.LaunchAttemptStarted = false;
        state.Phase = BridgePhase.ISOLATING;
        if (persist)
            SaveStateLocked();
    }

    private void IsolationWorker()
    {
        try
        {
            lock (lifecycleGate)
            {
                while (true)
                {
                    ThrowIfShutdownRequested();
                    ModProfile profile = null;
                    string attemptId = null;
                    string kind = null;
                    int targetGeneration = 0;
                    bool consume = false;
                    bool recoveryAmbiguous = false;

                    lock (gate)
                    {
                        if (!IsolationActiveLocked())
                            return;

                        CrashIsolationIncident incident = state.CrashIsolation;
                        if (incident.CurrentAttemptId == null)
                        {
                            if (state.IsolationLaunchesRemaining <= 0)
                            {
                                FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_BUDGET_EXHAUSTED",
                                    "The deterministic isolation launch budget was exhausted before a conclusive diagnosis; no opted-in project was blamed.");
                                return;
                            }
                            if (!TryPlanIsolationAttemptLocked())
                            {
                                if (!IsolationActiveLocked())
                                    return;
                                continue;
                            }
                        }

                        incident = state.CrashIsolation;
                        attemptId = incident.CurrentAttemptId;
                        kind = incident.CurrentAttemptKind;
                        targetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
                        profile = incident.CurrentAttemptProfile?.ToModProfile();
                        if (incident.CurrentAttemptResult != null)
                            consume = true;
                        else if (state.Phase == BridgePhase.LOADING)
                        {
                            // A recovery monitor owns an in-flight attempt. It will
                            // queue this worker after recording PASS/FAIL.
                            if (state.ProcessId > 0)
                                return;
                            incident.CurrentAttemptResult = "UNSAFE";
                            incident.CurrentAttemptFailurePhase = "LOADING";
                            incident.CurrentAttemptFailureCode = "ISOLATION_RECOVERY_AMBIGUOUS";
                            incident.CurrentAttemptFailureDetail =
                                "the coordinator restarted after isolation launch intent was persisted but before a verified process identity was recorded";
                            consume = true;
                            recoveryAmbiguous = true;
                        }
                    }

                    if (consume)
                    {
                        bool retainFinalControl;
                        lock (gate)
                        {
                            retainFinalControl = (string.Equals(kind, "FINAL_CONTROL", StringComparison.Ordinal) ||
                                string.Equals(kind, "FINAL_BASELINE_CONTROL", StringComparison.Ordinal)) &&
                                string.Equals(state.CrashIsolation?.CurrentAttemptResult, "PASS", StringComparison.Ordinal);
                        }
                        if (!retainFinalControl)
                        {
                            ThrowIfShutdownRequested();
                            if (!StopIsolationProcess(out string stopCode, out string stopError))
                            {
                                lock (gate)
                                    FinalizeIsolationEnvironmentalLocked(stopCode ?? "ISOLATION_STOP_FAILED",
                                        stopError ?? "the isolated RimWorld process could not be drained safely");
                                return;
                            }
                        }

                        lock (gate)
                        {
                            if (!IsolationActiveLocked())
                                return;
                            CrashIsolationIncident incident = state.CrashIsolation;
                            StoreCurrentIsolationAttemptLocked();
                            if (recoveryAmbiguous)
                            {
                                FinalizeIsolationEnvironmentalLocked("ISOLATION_RECOVERY_AMBIGUOUS",
                                    incident.CurrentAttemptFailureDetail);
                                return;
                            }
                            if (retainFinalControl)
                            {
                                FinalizeIsolationCompletedLocked();
                                return;
                            }
                            AdvanceIsolationAfterAttemptLocked(persist: false);
                            if (!IsolationActiveLocked())
                                return;
                            ClearCurrentIsolationAttemptLocked(persist: false);
                            SaveStateLocked();
                        }
                        continue;
                    }

                    if (profile == null)
                    {
                        lock (gate)
                            FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_PROFILE_MISSING",
                                "the durable isolation candidate profile was missing");
                        return;
                    }

                    ThrowIfShutdownRequested();
                    if (!PrepareIsolationAttempt(profile, attemptId, targetGeneration,
                            out string prepareErrorCode, out string prepareError))
                    {
                        lock (gate)
                        {
                            CrashIsolationIncident incident = state.CrashIsolation;
                            if (incident != null && string.Equals(incident.CurrentAttemptId, attemptId,
                                    StringComparison.Ordinal))
                            {
                                incident.CurrentAttemptResult = "UNSAFE";
                                incident.CurrentAttemptFailurePhase = "PREPARE";
                                incident.CurrentAttemptFailureCode = prepareErrorCode ?? "ISOLATION_PREPARE_FAILED";
                                incident.CurrentAttemptFailureDetail = prepareError;
                                incident.CurrentAttemptProfileInstalled = false;
                                StoreCurrentIsolationAttemptLocked();
                                FinalizeIsolationEnvironmentalLocked(
                                    prepareErrorCode ?? "ISOLATION_PREPARE_FAILED", prepareError);
                                return;
                            }
                        }
                        continue;
                    }

                    ThrowIfShutdownRequested();
                    LaunchGenerationWorker(targetGeneration, isRestart: true,
                        owner: "isolation@" + runtimeSlotId, isolationProfile: profile,
                        isolationAttemptId: attemptId);
                }
            }
        }
        catch (OperationCanceledException) when (ShutdownRequested)
        {
            // Leave the persisted isolation attempt recoverable. Shutdown is
            // not an environmental isolation failure and never kills RimWorld
            // merely to refresh the coordinator binary.
        }
        catch (ProfileException exception)
        {
            lock (gate)
                FinalizeIsolationEnvironmentalLocked(exception.Code, exception.Message);
        }
        catch (ProcessInspectionException)
        {
            lock (gate)
                FinalizeIsolationEnvironmentalLocked(ProcessInspection.ErrorCode, ProcessInspection.Message);
        }
        catch (Exception exception)
        {
            lock (gate)
                FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_FAILED", exception.Message);
        }
    }

    private void AdvanceIsolationAfterAttemptLocked(bool persist = true)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        string stage = incident.Stage;
        string result = incident.CurrentAttemptResult;
        string kind = incident.CurrentAttemptKind;

        if (!string.Equals(result, "PASS", StringComparison.Ordinal) &&
            !string.Equals(result, "FAIL", StringComparison.Ordinal))
        {
            FinalizeIsolationEnvironmentalLocked("ISOLATION_UNSAFE_RESULT",
                incident.CurrentAttemptFailureDetail ??
                "the isolation attempt did not produce safe profile-failure evidence");
            return;
        }

        if (stage == "CONTROL")
        {
            if (!string.Equals(result, "PASS", StringComparison.Ordinal))
            {
                FinalizeIsolationEnvironmentalLocked("ENVIRONMENTAL_BASELINE_FAILURE",
                    "The durable baseline/last-known-good control profile also failed before readiness; no opted-in project was blamed.");
                return;
            }
            incident.Stage = "REPRODUCE";
            if (persist)
                SaveStateLocked();
            return;
        }

        if (stage == "REPRODUCE")
        {
            if (string.Equals(result, "PASS", StringComparison.Ordinal))
            {
                incident.DiagnosisCode = "INTERMITTENT_PROFILE_FAILURE";
                incident.Diagnosis = "The accepted project profile passed when reproduced after the control profile; the original startup failure is intermittent/nondeterministic, so no project was attributed.";
                incident.Diagnoses.Clear();
                incident.Stage = "FINAL_CONTROL";
            }
            else
            {
                incident.SearchPoolKnownFail = true;
                incident.DeltaCurrentProjects = StableProjectOrder(incident.SearchPoolProjects);
                incident.DeltaGranularity = Math.Min(2, Math.Max(1, incident.DeltaCurrentProjects.Count));
                incident.PendingCandidates = new List<CrashIsolationSelection>();
                incident.PendingCandidateIndex = 0;
                incident.PendingKind = null;
                incident.Stage = "MINIMIZE";
            }
            if (persist)
                SaveStateLocked();
            return;
        }

        if (stage == "VERIFY_REMAINDER")
        {
            if (string.Equals(result, "FAIL", StringComparison.Ordinal))
            {
                incident.SearchPoolKnownFail = true;
                incident.DeltaCurrentProjects = StableProjectOrder(incident.DeltaCurrentProjects);
                incident.DeltaGranularity = Math.Min(2, Math.Max(1, incident.DeltaCurrentProjects.Count));
                incident.PendingCandidates = new List<CrashIsolationSelection>();
                incident.PendingCandidateIndex = 0;
                incident.PendingKind = null;
                incident.Stage = "MINIMIZE";
            }
            else
            {
                // This remainder has passed after the diagnosed roots were
                // removed. Preserve it durably so unrelated requested roots
                // can remain enabled in the recovered runtime.
                incident.SafeRemainderProfile = incident.CurrentAttemptProfile == null
                    ? null
                    : PersistedProfileSnapshot.FromModProfile(incident.CurrentAttemptProfile.ToModProfile());
                incident.Stage = "FINAL_CONTROL";
            }
            if (persist)
                SaveStateLocked();
            return;
        }

        if (stage == "MINIMIZE")
        {
            List<string> current = StableProjectOrder(incident.DeltaCurrentProjects);
            if (incident.PendingCandidateIndex < incident.PendingCandidates.Count)
                incident.PendingCandidateIndex++;

            if (string.Equals(result, "FAIL", StringComparison.Ordinal))
            {
                incident.DeltaCurrentProjects = StableProjectOrder(incident.CurrentAttemptProjects);
                incident.DeltaGranularity = Math.Max(2,
                    Math.Min(incident.DeltaCurrentProjects.Count, incident.DeltaGranularity - 1));
                incident.PendingCandidates = new List<CrashIsolationSelection>();
                incident.PendingCandidateIndex = 0;
                incident.PendingKind = null;
            }
            else if (incident.PendingCandidateIndex >= incident.PendingCandidates.Count)
            {
                int n = Math.Min(current.Count, Math.Max(2, incident.DeltaGranularity));
                if (incident.PendingKind == "REMOVE" && n < current.Count)
                {
                    StartIsolationRoundLocked(complements: false, persist: persist);
                }
                else if (incident.PendingKind == "DIRECT" && n < current.Count)
                {
                    incident.DeltaGranularity = Math.Min(current.Count, n * 2);
                    StartIsolationRoundLocked(complements: true, persist: persist);
                }
                else
                    CompleteMinimalSetLocked(current, persist);
            }
            if (persist)
                SaveStateLocked();
            return;
        }

        if (stage == "FINAL_CONTROL")
        {
            if (string.Equals(result, "FAIL", StringComparison.Ordinal) &&
                !incident.FinalControlBaselineAttempted)
            {
                ModProfile baseline = CreateBaselineProfileForMode(
                    incident.OriginalBaselineFingerprint);
                bool finalProfileWasBaseline = incident.CurrentAttemptProfile != null &&
                    string.Equals(incident.CurrentAttemptProfile.ProfileFingerprint,
                        baseline.ProfileFingerprint, StringComparison.Ordinal);
                if (!finalProfileWasBaseline)
                {
                    incident.SafeRemainderProfile = null;
                    incident.FinalControlBaselineAttempted = true;
                    incident.Stage = "FINAL_BASELINE_CONTROL";
                    if (persist)
                        SaveStateLocked();
                    return;
                }
            }
            FinalizeIsolationEnvironmentalLocked("ENVIRONMENTAL_BASELINE_FAILURE",
                "The known-good control profile failed while restoring after isolation; no opted-in project was blamed.");
            return;
        }

        if (stage == "FINAL_BASELINE_CONTROL")
        {
            FinalizeIsolationEnvironmentalLocked("ENVIRONMENTAL_BASELINE_FAILURE",
                "The durable baseline control profile failed while restoring after isolation; no opted-in project was blamed.");
            return;
        }

        FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_FAILED",
            "the isolation state machine reached an unknown phase");
    }

    private void FinalizeIsolationEnvironmentalLocked(string code, string detail)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null)
            return;
        // A terminal environmental result is deliberately non-attributive.
        // Do not leave earlier candidate diagnoses visible beside it.
        incident.Diagnoses ??= new List<CrashIsolationDiagnosis>();
        incident.Diagnoses.Clear();
        if (incident.CurrentAttemptId != null)
        {
            if (incident.CurrentAttemptResult == null)
            {
                incident.CurrentAttemptResult = "UNSAFE";
                incident.CurrentAttemptFailurePhase = state.Phase.ToString();
                incident.CurrentAttemptFailureCode = code;
                incident.CurrentAttemptFailureDetail = detail;
            }
            StoreCurrentIsolationAttemptLocked();
        }

        string finalCode = code;
        string finalDetail = detail;
        bool noRimWorldProcess = false;
        bool censusComplete = false;
        try
        {
            noRimWorldProcess = FindUnmanagedRimWorldProcesses(0, 0).Count == 0;
            censusComplete = true;
        }
        catch (ProcessInspectionException)
        {
            finalCode = ProcessInspection.ErrorCode;
            finalDetail = ProcessInspection.Message + " Isolation was quarantined without changing ModsConfig.xml.";
        }

        if (noRimWorldProcess)
        {
            try
            {
                ModProfile control = state.LastKnownGoodProfile?.ToModProfile() ??
                    CreateBaselineProfileForMode(incident.OriginalBaselineFingerprint);
                string ownership = CurrentModsConfigOwnershipLocked();
                bool generatedOwnership = ownership == "DEVBRIDGE_GENERATED" || ownership == "DEVBRIDGE_PENDING";
                string generatedProfileFingerprint = state.ModsConfigGeneratedProfileFingerprint;
                if (string.IsNullOrWhiteSpace(generatedProfileFingerprint))
                    generatedProfileFingerprint = ReadGeneratedModsConfigManifestLocked(out _)?.ProfileFingerprint;

                if (state.ProfileMode == ModProfile.ProjectsMode && ownership != "BASELINE" &&
                    !generatedOwnership)
                {
                    throw new ProfileException("MODS_CONFIG_EXTERNAL_EDIT",
                        "ModsConfig.xml ownership is " + ownership + "; the candidate profile was not overwritten.");
                }

                string installedProfileFingerprint = ownership == "BASELINE"
                    ? incident.OriginalBaselineFingerprint
                    : generatedProfileFingerprint;
                if (state.ProfileMode == ModProfile.ProjectsMode &&
                    (ownership == "BASELINE" || generatedOwnership) &&
                    !string.Equals(installedProfileFingerprint, control.ProfileFingerprint,
                        StringComparison.Ordinal))
                {
                    ApplyProfile(control, Math.Max(state.Generation + 1, state.TargetGeneration));
                }
                if (state.ProfileMode == ModProfile.ProjectsMode &&
                    (ownership == "BASELINE" || generatedOwnership))
                    state.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(control);
            }
            catch (ProfileException exception)
            {
                finalCode = "CRASH_ISOLATION_RECOVERY_UNSAFE";
                finalDetail = exception.Message + " ModsConfig.xml was left unchanged.";
                state.SessionDirty = true;
            }
            catch (ProcessInspectionException)
            {
                finalCode = ProcessInspection.ErrorCode;
                finalDetail = ProcessInspection.Message + " ModsConfig.xml was left unchanged.";
                state.SessionDirty = true;
            }
            catch (Exception exception)
            {
                finalCode = "CRASH_ISOLATION_RECOVERY_UNSAFE";
                finalDetail = "DevBridge could not safely restore the control profile: " +
                    exception.Message + " ModsConfig.xml may require manual verification.";
                state.SessionDirty = true;
            }
        }
        else if (censusComplete)
        {
            finalCode = "CRASH_ISOLATION_RECOVERY_QUARANTINED";
            finalDetail = detail + " A RimWorld process is still present or could not be safely identified; no process was stopped and ModsConfig.xml was not changed.";
            state.SessionDirty = true;
        }
        else
            state.SessionDirty = true;

        incident.Status = "ENVIRONMENTAL_FAILURE";
        incident.Stage = "TERMINAL";
        incident.DiagnosisCode = finalCode;
        incident.Diagnosis = finalDetail;
        state.Phase = BridgePhase.ERROR;
        state.RestartPending = false;
        state.TargetGeneration = 0;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.WaitingForBridgeDeadlineUtc = null;
        if (noRimWorldProcess)
        {
            state.ProcessId = 0;
            state.ProcessStartUtcTicks = 0;
            state.OwnedProcessExecutablePath = null;
            state.LaunchId = null;
            state.LaunchProfileFingerprint = null;
            state.LaunchProfileInstalled = false;
            state.LaunchAttemptStarted = false;
        }
        state.IsolationLaunchesRemaining = 0;
        incident.IsolationLaunchesRemaining = 0;
        state.ErrorCode = finalCode;
        state.Error = finalDetail;
        state.ProfileErrorCode = finalCode;
        state.ProfileError = finalDetail;
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void FinalizeIsolationCompletedLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null)
            return;
        // The profile that passed FINAL_CONTROL is the maximal safe remainder
        // (or the baseline fallback). Promote that exact snapshot to the new
        // durable control; retaining the pre-isolation baseline here would
        // silently discard unrelated healthy projects.
        PersistedProfileSnapshot restoredProfile = incident.SafeRemainderProfile ??
            state.RuntimeProfile ?? state.LastKnownGoodProfile;
        if (restoredProfile != null)
        {
            state.LastKnownGoodProfile = restoredProfile;
            state.RuntimeProfile = restoredProfile;
        }
        incident.Status = "COMPLETED";
        incident.Stage = "TERMINAL";
        if (string.IsNullOrWhiteSpace(incident.DiagnosisCode))
        {
            incident.DiagnosisCode = "NO_DETERMINISTIC_PROJECT_FAILURE";
            incident.Diagnosis = "The accepted project profile could not be reduced to a deterministic failing project set.";
        }
        state.Phase = BridgePhase.READY;
        state.Generation = state.TargetGeneration;
        state.RestartPending = false;
        state.RestartRequestedUtc = null;
        state.AggregateFreezePending = false;
        state.TargetGeneration = 0;
        state.LastLaunchOwner = state.LaunchOwner;
        state.LastLaunchRequestKey = state.LaunchRequestKey;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.RequiresNewProcess = false;
        state.MaintenanceReady = false;
        state.LaunchProfileFingerprint = null;
        state.LaunchProfileInstalled = false;
        state.LaunchAttemptStarted = false;
        state.IsolationLaunchesRemaining = 0;
        incident.IsolationLaunchesRemaining = 0;
        state.Error = null;
        state.ErrorCode = "CRASH_ISOLATION_COMPLETE";
        state.ProfileErrorCode = incident.DiagnosisCode;
        state.ProfileError = incident.Diagnosis;
        incident.CurrentAttemptId = null;
        incident.CurrentAttemptFingerprint = null;
        incident.CurrentAttemptKind = null;
        incident.CurrentAttemptProfile = null;
        incident.CurrentAttemptProjects = new List<string>();
        incident.CurrentAttemptResult = null;
        incident.CurrentAttemptFailurePhase = null;
        incident.CurrentAttemptFailureCode = null;
        incident.CurrentAttemptFailureDetail = null;
        incident.CurrentAttemptProfileInstalled = false;
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

}
