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
    internal int Execute(BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        request ??= new BridgeRequest();
        bool pureHistoryAnalysis = IsPureHistoryAnalysisRequest(request);
        using IDisposable traceScope = pureHistoryAnalysis ? null : BeginTraceRequest(request);
        if (!pureHistoryAnalysis)
            TraceCommandStarted(request);
        int exitCode = -1;
        try
        {
            exitCode = ExecuteCore(request, emit, connected);
            return exitCode;
        }
        finally
        {
            if (!pureHistoryAnalysis)
                TraceCommandCompleted(request, exitCode >= 0 ? exitCode : null);
        }
    }

    private static bool IsPureHistoryAnalysisRequest(BridgeRequest request)
    {
        if (!string.Equals(request?.Command, "history", StringComparison.OrdinalIgnoreCase))
            return false;
        string operation = request.Arguments?.FirstOrDefault();
        return string.Equals(operation, "diff", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, "diagnose", StringComparison.OrdinalIgnoreCase);
    }

    private int ExecuteCore(BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        request.Arguments ??= new List<string>();
        string command = (request.Command ?? string.Empty).Trim().ToLowerInvariant();
        if (persistedStateLoadBlocked && command != "doctor" && command != "history" &&
            command != "coordinator" &&
            command != "agent" &&
            command != "logs" && command != "evidence" &&
            !IsProjectResolveCommand(request) &&
            !IsPureRecipeCommand(request))
        {
            string errorCode;
            string error;
            lock (gate)
            {
                errorCode = state.ErrorCode ?? "PERSISTED_STATE_UNAVAILABLE";
                error = state.Error ?? "The persisted coordinator state is unavailable.";
            }
            emit(errorCode + ": " + error);
            return 4;
        }
        if (!TryResolveScope(request, emit))
            return 4;

        List<string> arguments = request.Arguments ?? new List<string>();

        return command switch
        {
            "agent" => Agent(arguments, request, emit, connected),
            "status" => Status(request, emit),
            "bridge" => Bridge(arguments, request, emit),
            "rimbridge" => Bridge(arguments, request, emit),
            "mods" => Mods(arguments, emit),
            "doctor" => Doctor(request, emit),
            "history" => History(arguments, request, emit),
            "logs" => Logs(arguments, request, emit),
            "evidence" => Evidence(arguments, request, emit),
            "game" => Game(arguments, request, emit, connected),
            "environment" => ViewportEnvironmentCommand(arguments, request, emit, connected),
            "wait-ready" => WaitReady(request, emit),
            "restart" => Restart(request, emit),
            "stop" => Stop(request, emit),
            "ensure-ready" => EnsureReady(request, emit),
            "coordinator" => CoordinatorControl(arguments, emit),
            "project" or "projects" or "intent" => ProjectIntent(arguments, request, emit),
            "test" => Test(arguments, request, emit, connected),
            "help" => Help(emit),
            _ => Unknown(command, emit)
        };
    }

    private int CoordinatorControl(IReadOnlyList<string> arguments, Action<string> emit)
    {
        if (arguments.Count == 1 &&
            string.Equals(arguments[0], "shutdown", StringComparison.OrdinalIgnoreCase))
        {
            emit("Coordinator shutdown accepted. Durable state and the RimWorld process are unchanged.");
            emit("The next command will lazily start the current coordinator binary and environment.");
            return 0;
        }

        if (arguments.Count == 2 &&
            string.Equals(arguments[0], "recover-process", StringComparison.OrdinalIgnoreCase))
            return RecoverProcessOwnership(arguments[1], emit);

        emit("Usage: DevBridge.cmd coordinator shutdown | coordinator recover-process <source-state-path>");
        return 2;
    }

    private int RecoverProcessOwnership(string sourceStatePath, Action<string> emit)
    {
        if (string.IsNullOrWhiteSpace(sourceStatePath))
        {
            emit("Process recovery denied: source state path is required.");
            emit("Error code: PROCESS_RECOVERY_EVIDENCE_MISSING");
            return 4;
        }

        PersistedState evidence;
        string fullPath;
        string sourceRoot;
        try
        {
            fullPath = Path.GetFullPath(sourceStatePath);
            sourceRoot = Directory.GetParent(Directory.GetParent(fullPath)?.FullName ?? string.Empty)?.FullName;
            if (!File.Exists(fullPath))
            {
                emit("Process recovery denied: source state evidence is absent.");
                emit("Error code: PROCESS_RECOVERY_EVIDENCE_MISSING");
                return 4;
            }

            evidence = JsonSerializer.Deserialize<PersistedState>(
                File.ReadAllText(fullPath), CoordinatorSerialization.JsonOptions);
        }
        catch
        {
            emit("Process recovery denied: source state evidence is unreadable.");
            emit("Error code: PROCESS_RECOVERY_EVIDENCE_INVALID");
            return 4;
        }

        if (evidence == null || evidence.SchemaVersion > DevBridgeSchemaVersions.RuntimeState ||
            string.IsNullOrWhiteSpace(sourceRoot) ||
            !string.Equals(Path.GetFileName(fullPath), "state.json", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(fullPath) ?? string.Empty),
                "Runtime", StringComparison.OrdinalIgnoreCase) ||
            !RuntimeScope.PathsEqual(evidence.CoordinatorRoot, sourceRoot) ||
            RuntimeScope.PathsEqual(evidence.CoordinatorRoot, coordinatorRoot) ||
            evidence.ProcessId <= 0 || evidence.ProcessStartUtcTicks <= 0 ||
            string.IsNullOrWhiteSpace(evidence.OwnedProcessExecutablePath) ||
            !RuntimeScope.PathsEqual(evidence.OwnedProcessExecutablePath, rimWorldExe))
        {
            emit("Process recovery denied: source state does not contain a matching durable owner identity.");
            emit("Error code: PROCESS_RECOVERY_EVIDENCE_MISMATCH");
            return 4;
        }

        ProcessStatusSnapshot census;
        try
        {
            census = EnumerateStatusProcessesLocked();
        }
        catch (ProcessInspectionException)
        {
            emit("Process recovery denied: the current RimWorld process census was incomplete.");
            emit("Error code: " + ProcessInspection.ErrorCode);
            return 4;
        }

        if (census.MatchingProcessCount != 1 ||
            !census.MatchingProcesses.Any(value => value.ProcessId == evidence.ProcessId &&
                value.ProcessStartIdentity == evidence.ProcessStartUtcTicks))
        {
            emit("Process recovery denied: current process identity did not match source evidence.");
            emit("Error code: PROCESS_RECOVERY_IDENTITY_MISMATCH");
            return 4;
        }

        lock (lifecycleGate)
        {
            lock (gate)
            {
                SynchronizeLocked();
                if (state.ProcessId > 0 || state.MaintenanceReady || state.Leases.Count != 0 ||
                    state.RestartPending || state.Phase != BridgePhase.ERROR)
                {
                    emit("Process recovery denied: the installed runtime is not at a quiescent error boundary.");
                    emit("Error code: PROCESS_RECOVERY_BOUNDARY_UNSAFE");
                    return 4;
                }

                state.ProcessId = evidence.ProcessId;
                state.ProcessStartUtcTicks = evidence.ProcessStartUtcTicks;
                state.OwnedProcessExecutablePath = rimWorldExe;
                state.LaunchId = evidence.LaunchId;
                state.LaunchGeneration = evidence.LaunchGeneration > 0
                    ? evidence.LaunchGeneration : evidence.Generation;
                state.Generation = Math.Max(state.Generation, evidence.Generation);
                state.ErrorCode = "PROFILE_EXTERNAL_MUTATION";
                state.Error = "Recovered a previously coordinator-owned RimWorld process from exact source state evidence.";
                state.Phase = BridgePhase.ERROR;
                state.MaintenanceReady = false;
                state.RequiresNewProcess = true;
                state.SessionDirty = true;
                SaveStateLocked();
                Monitor.PulseAll(gate);
            }
        }

        emit("Process ownership recovered from exact source state evidence.");
        emit("PID: " + evidence.ProcessId);
        emit("Start identity: " + evidence.ProcessStartUtcTicks);
        emit("No RimWorld process was launched or terminated.");
        EmitNextCommand(emit, "DevBridge.cmd test begin");
        return 0;
    }

    private bool TryResolveScope(BridgeRequest request, Action<string> emit)
    {
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(request.TicketId))
            {
                ScopeTicket ticket = state.ScopeTickets.FirstOrDefault(value =>
                    string.Equals(value.Id, request.TicketId.Trim(), StringComparison.Ordinal));
                if (ticket == null)
                {
                    emit("Scope denied: the ticket is not bound to an authoritative runtime slot.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return false;
                }

                if ((!string.IsNullOrWhiteSpace(request.CoordinatorRoot) &&
                     !RuntimeScope.PathsEqual(request.CoordinatorRoot, ticket.CoordinatorRoot)) ||
                    (!string.IsNullOrWhiteSpace(request.RuntimeSlotId) &&
                     !string.Equals(request.RuntimeSlotId, ticket.RuntimeSlotId, StringComparison.Ordinal)))
                {
                    emit("Scope denied: the ticket scope conflicts with the requested runtime slot.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return false;
                }

                request.RuntimeSlotId = ticket.RuntimeSlotId;
                request.CoordinatorRoot = ticket.CoordinatorRoot;
            }

            if (string.IsNullOrWhiteSpace(request.CoordinatorRoot))
                request.CoordinatorRoot = coordinatorRoot;
            if (string.IsNullOrWhiteSpace(request.RuntimeSlotId))
                request.RuntimeSlotId = runtimeSlotId;

            if (!RuntimeScope.PathsEqual(request.CoordinatorRoot, coordinatorRoot) ||
                !string.Equals(request.RuntimeSlotId, runtimeSlotId, StringComparison.Ordinal))
            {
                emit("Scope denied: runtime slot and coordinator root do not match this coordinator.");
                EmitNextCommand(emit, "DevBridge.cmd doctor");
                return false;
            }

            return true;
        }
    }

    private int Test(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        if (arguments.Count == 0)
        {
            emit("Usage: DevBridge.cmd test begin | test session | test renew <lease-id> | test end <lease-id> | test recipe list|show|plan|run");
            return 2;
        }

        return arguments[0].Trim().ToLowerInvariant() switch
        {
            "begin" => BeginLease(request, emit, connected),
            "session" => SessionLease(request, emit, connected),
            "renew" => RenewLease(request, arguments, emit),
            "end" => EndLease(request, arguments, emit),
            "recipe" => TestRecipe(arguments, request, emit, connected),
            _ => Unknown("test " + arguments[0], emit)
        };
    }

    private int Mods(IReadOnlyList<string> arguments, Action<string> emit)
    {
        if (arguments.Count == 0)
        {
            emit("Usage: DevBridge.cmd mods status | mods capture-baseline | mods restore-baseline");
            return 2;
        }

        return arguments[0].Trim().ToLowerInvariant() switch
        {
            "status" when arguments.Count == 1 => ModsStatus(emit),
            "capture-baseline" when arguments.Count == 1 => CaptureBaseline(emit),
            "restore-baseline" when arguments.Count == 1 => RestoreBaseline(emit),
            _ => Unknown("mods " + string.Join(" ", arguments), emit)
        };
    }

    private int ModsStatus(Action<string> emit)
    {
        PersistedState snapshot;
        lock (gate)
        {
            DetectExternalModsConfigMutationLocked();
            snapshot = CloneStateLocked();
            snapshot.BaselineFingerprint = ReadBaselineFingerprintLocked() ?? snapshot.BaselineFingerprint;
            snapshot.ModsConfigOwnership = CurrentModsConfigOwnershipLocked();
            RefreshRimBridgePolicyStateLocked();
            snapshot.RimBridgePolicy = state.RimBridgePolicy?.Clone();
        }

        emit("DevBridge2 mod profiles");
        EmitProfile(snapshot, emit);
        if (!string.IsNullOrWhiteSpace(snapshot.ProfileError))
            emit("Profile error: " + snapshot.ProfileErrorCode + " - " + snapshot.ProfileError);
        if (!string.IsNullOrWhiteSpace(snapshot.ProfileConflict))
            emit("Profile conflict: " + snapshot.ProfileConflict);
        return 0;
    }

    private int CaptureBaseline(Action<string> emit)
    {
        lock (lifecycleGate)
        {
            lock (gate)
            {
                if (!CanChangeModsConfigLocked(emit))
                    return 4;
                if (!File.Exists(modsConfigPath))
                {
                    emit("Baseline capture failed: ModsConfig.xml was not found at " + modsConfigPath + ".");
                    return 4;
                }

                byte[] contents = File.ReadAllBytes(modsConfigPath);
                string fingerprint = HashBytes(contents);
                string ownership = CurrentModsConfigOwnershipLocked(contents, fingerprint);
                if (ownership == "DEVBRIDGE_GENERATED" || ownership == "DEVBRIDGE_PENDING")
                {
                    RecordProfileErrorLocked("PROFILE_BASELINE_GENERATED",
                        "The current ModsConfig.xml was generated by DevBridge; edit it intentionally, then capture the changed file.");
                    emit("Baseline capture refused: the current ModsConfig.xml is DevBridge-generated.");
                    emit("Error code: PROFILE_BASELINE_GENERATED");
                    return 4;
                }

                options.BeforeModsConfigWrite?.Invoke();
                byte[] latest;
                try
                {
                    latest = File.ReadAllBytes(modsConfigPath);
                }
                catch
                {
                    RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                        "ModsConfig.xml changed or disappeared while preparing the baseline capture.");
                    emit("Baseline capture refused: ModsConfig.xml changed while the capture was preparing.");
                    emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                    return 4;
                }
                if (!string.Equals(HashBytes(latest), fingerprint, StringComparison.Ordinal))
                {
                    RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                        "ModsConfig.xml changed while preparing the baseline capture.");
                    emit("Baseline capture refused: an unexpected edit would be captured as the baseline.");
                    emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                    return 4;
                }
                try
                {
                    EnsureNoMatchingRimWorldProcess();
                }
                catch (ProfileException exception)
                {
                    RecordProfileErrorLocked(exception.Code, exception.Message);
                    emit("Baseline capture refused: " + exception.Message);
                    emit("Error code: " + exception.Code);
                    return 4;
                }
                catch (ProcessInspectionException)
                {
                    RecordProfileErrorLocked(ProcessInspection.ErrorCode, ProcessInspection.Message);
                    emit("Baseline capture refused: " + ProcessInspection.Message);
                    emit("Error code: " + ProcessInspection.ErrorCode);
                    return 4;
                }

                BeginModsConfigTransitionLocked(allowExternalMutationReconciliation: true);
                try
                {
                    AtomicWriteFile(baselinePath, contents);
                }
                catch
                {
                    AbortModsConfigTransitionLocked();
                    throw;
                }
                ClearGeneratedModsConfigManifestLocked();
                state.BaselineFingerprint = fingerprint;
                state.ModsConfigOwnership = "BASELINE";
                state.ModsConfigGeneratedHash = null;
                state.ModsConfigGeneratedProfileFingerprint = null;
                state.ModsConfigGeneratedGeneration = 0;
                ClearActiveProfileLocked();
                state.LastKnownGoodProfile = PersistedProfileSnapshot.FromModProfile(
                    CreateBaselineProfileForMode(fingerprint));
                state.RuntimeProfile = state.LastKnownGoodProfile;
                state.CrashIsolation = null;
                state.LaunchProfileFingerprint = null;
                state.LaunchProfileInstalled = false;
                state.LaunchAttemptStarted = false;
                state.ProfileErrorCode = null;
                state.ProfileError = null;
                state.ProfileConflict = null;
                ClearExternalModsConfigMutationLocked();
                state.Phase = BridgePhase.STOPPED;
                state.RequiresNewProcess = true;
                SaveStateLocked();
                emit("Captured the user ModsConfig baseline byte-for-byte.");
                emit("Baseline fingerprint: " + fingerprint);
                emit("Next action: register project intent if needed, then run DevBridge.cmd restart and verify status --json before testing.");
                return 0;
            }
        }
    }

    private int ProjectIntent(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        if (arguments.Count == 0)
        {
            emit("Usage: DevBridge.cmd project resolve <alias[,alias...]> [--explain] [--json] | project register <alias[,alias...]> | project status | project renew <registration-id> | project release <registration-id>");
            return 2;
        }

        string operation = arguments[0]?.Trim().ToLowerInvariant();
        return operation switch
        {
            "resolve" => ResolveProjectPlan(arguments, request, emit),
            "register" => RegisterProjectIntent(arguments, request, emit),
            "status" => ProjectIntentStatus(emit),
            "renew" or "heartbeat" => RenewProjectIntent(arguments, request, emit),
            "release" or "end" => ReleaseProjectIntent(arguments, request, emit),
            _ => Unknown("project " + string.Join(" ", arguments), emit)
        };
    }

    private int RegisterProjectIntent(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        string value = null;
        string requestedId = null;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index]?.Trim() ?? string.Empty;
            if (argument.StartsWith("--id=", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(requestedId))
                    return ProjectIntentUsage(emit, "project register accepts only one --id option");
                requestedId = argument.Substring("--id=".Length).Trim();
                continue;
            }
            if (string.Equals(argument, "--id", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[++index]))
                    return ProjectIntentUsage(emit, "project register --id requires a stable registration ID");
                if (!string.IsNullOrWhiteSpace(requestedId))
                    return ProjectIntentUsage(emit, "project register accepts only one --id option");
                requestedId = arguments[index].Trim();
                continue;
            }
            if (argument.StartsWith("--", StringComparison.Ordinal))
                return ProjectIntentUsage(emit, "unknown project registration option '" + argument + "'");
            if (value != null)
                return ProjectIntentUsage(emit, "project register accepts one comma-separated alias value");
            value = argument;
        }

        if (string.IsNullOrWhiteSpace(value))
            return ProjectIntentUsage(emit, "project register requires one or more managed project aliases");

        IReadOnlyList<string> aliases;
        try
        {
            aliases = ModProfileResolver.CanonicalAliases(value.Split(',', StringSplitOptions.None));
            if (aliases.Count == 0)
                throw new ProfileException("PROJECT_INTENT_INVALID", "a project intent must contain at least one managed project alias");
        }
        catch (ProfileException exception)
        {
            RecordProfileError(exception.Code, exception.Message);
            emit("Project registration denied: " + exception.Message);
            emit("Error code: " + exception.Code);
            return 2;
        }

        lock (gate)
        {
            SynchronizeLocked();
            if (IsolationActiveLocked())
                return ProjectIntentIsolationDenied(emit);

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
                SaveStateLocked();
                EmitProjectRegistration(existing, emit, "Project intent renewed: ");
                return 0;
            }

            string id = string.IsNullOrWhiteSpace(requestedId) ?
                NewProjectIntentIdLocked(owner, session, aliases) : requestedId.Trim();
            if (id.Length < 4 || id.Length > 128 || id.Any(char.IsWhiteSpace))
                return ProjectIntentUsage(emit, "registration ID must be 4-128 non-whitespace characters");
            ProjectIntentRegistration byId = state.ProjectIntents.FirstOrDefault(value =>
                string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase));
            if (byId != null && (!string.Equals(byId.Owner, owner, StringComparison.Ordinal) ||
                                 !string.Equals(byId.SessionId, session, StringComparison.Ordinal)))
            {
                emit("Project registration denied: registration ID " + id + " belongs to another owner/session.");
                emit("Error code: PROJECT_INTENT_OWNERSHIP");
                return 4;
            }
            if (byId != null && !string.Equals(byId.Status, "ACTIVE", StringComparison.Ordinal))
            {
                emit("Project registration denied: registration ID " + id +
                    " is terminal and cannot be reused; choose a new stable ID.");
                emit("Error code: PROJECT_INTENT_ID_REUSED");
                return 4;
            }

            DateTime now = clock.UtcNow;
            ProjectIntentRegistration registration = byId ?? new ProjectIntentRegistration { Id = id };
            registration.Owner = owner;
            registration.SessionId = session;
            registration.ClientProcessId = request.ClientProcessId;
            registration.RequestedProjects = aliases.ToList();
            registration.CreatedUtc = registration.CreatedUtc == default ? now : registration.CreatedUtc;
            registration.LastHeartbeatUtc = now;
            registration.ExpiresUtc = now.Add(options.ProjectIntentDuration);
            registration.Status = "ACTIVE";
            registration.ReleasedUtc = null;
            registration.ReleaseReason = null;
            if (byId == null)
                state.ProjectIntents.Add(registration);
            SaveStateLocked();
            Monitor.PulseAll(gate);
            EmitProjectRegistration(registration, emit, "Project intent registered: ");
            emit("Aggregate-first policy: existing project registrations are combined; do not wait for an exclusive profile unless isolating a failure or honoring a known incompatibility.");
            if (state.Leases.Count > 0)
                emit("Active tests delay the replacement launch or your test start; they do not block project registration.");
            if (state.RestartPending && !state.FrozenRegistrations.Any(value =>
                    string.Equals(value.Id, registration.Id, StringComparison.Ordinal)))
            {
                emit("This registration is queued for the next generation; the frozen generation is immutable.");
            }
            emit("Next action: DevBridge.cmd restart, then verify project inclusion with DevBridge.cmd status --json.");
            return 0;
        }
    }

    private int ProjectIntentStatus(Action<string> emit)
    {
        lock (gate)
        {
            SynchronizeLocked();
            EmitAggregateIntentStatusLocked(CloneStateLocked(), null, emit);
        }
        emit("Next action: Run DevBridge.cmd restart, then verify the frozen generation and included registrations with DevBridge.cmd status --json before testing.");
        return 0;
    }

    private int RenewProjectIntent(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        if (arguments.Count != 2 || string.IsNullOrWhiteSpace(arguments[1]))
            return ProjectIntentUsage(emit, "project renew requires one registration ID");
        lock (gate)
        {
            SynchronizeLocked();
            if (IsolationActiveLocked())
                return ProjectIntentIsolationDenied(emit);
            ProjectIntentRegistration registration = FindProjectIntentLocked(arguments[1]);
            if (!ProjectIntentOwnedBy(registration, request))
            {
                emit("Project intent renewal denied: registration is not owned by this agent/session or has expired.");
                emit("Error code: PROJECT_INTENT_OWNERSHIP");
                return 4;
            }
            TouchProjectIntentLocked(registration);
            SaveStateLocked();
            EmitProjectRegistration(registration, emit, "Project intent renewed: ");
            return 0;
        }
    }

    private int ReleaseProjectIntent(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        if (arguments.Count != 2 || string.IsNullOrWhiteSpace(arguments[1]))
            return ProjectIntentUsage(emit, "project release requires one registration ID");
        lock (gate)
        {
            SynchronizeLocked();
            if (IsolationActiveLocked())
                return ProjectIntentIsolationDenied(emit);
            ProjectIntentRegistration registration = FindProjectIntentLocked(arguments[1]);
            if (registration == null)
            {
                emit("Project intent " + arguments[1] + " was already released or expired.");
                return 0;
            }
            if (!ProjectIntentOwnedBy(registration, request))
            {
                emit("Project intent release denied: registration is not owned by this agent/session.");
                emit("Error code: PROJECT_INTENT_OWNERSHIP");
                return 4;
            }
            if (string.Equals(registration.Status, "ACTIVE", StringComparison.Ordinal))
            {
                registration.Status = "RELEASED";
                registration.ReleasedUtc = clock.UtcNow;
                registration.ReleaseReason = "explicit release";
                SaveStateLocked();
                Monitor.PulseAll(gate);
            }
            emit("Project intent released: " + registration.Id);
            emit("The release affects future generations only; frozen generation evidence is unchanged.");
            emit("Next action: Run DevBridge.cmd status --json; restart when the next aggregate generation should omit this registration.");
            return 0;
        }
    }

    private static int ProjectIntentUsage(Action<string> emit, string detail)
    {
        emit("Project registration denied: " + detail + ".");
        emit("Usage: DevBridge.cmd project register <alias[,alias...]> [--id <registration-id>]");
        emit("Error code: PROJECT_INTENT_INVALID");
        return 2;
    }

    private int ProjectIntentIsolationDenied(Action<string> emit)
    {
        emit("Project intent change denied while crash isolation is active; the in-flight incident is immutable.");
        emit("Error code: CRASH_ISOLATION_RUNNING");
        emit("Next action: Run DevBridge.cmd status and keep polling. Do not restart, edit ModsConfig.xml, or mutate registrations.");
        return 4;
    }

    private void EmitProjectRegistration(ProjectIntentRegistration registration, Action<string> emit, string prefix)
    {
        emit(prefix + registration.Id);
        emit("Owner/session: " + registration.Owner + "/" + registration.SessionId);
        emit("Projects: " + string.Join(", ", registration.RequestedProjects));
        emit("Status: " + registration.Status + " expiresUtc=" + FormatUtc(registration.ExpiresUtc));
    }

    private static string StableProjectOwner(BridgeRequest request) =>
        string.IsNullOrWhiteSpace(request?.Agent) ? "unknown-agent" : request.Agent.Trim();

    private static string StableProjectSession(BridgeRequest request) =>
        string.IsNullOrWhiteSpace(request?.SessionId) ? StableProjectOwner(request) : request.SessionId.Trim();

    private static bool SequenceEqualAliases(IEnumerable<string> left, IEnumerable<string> right) =>
        (left ?? Array.Empty<string>()).SequenceEqual(right ?? Array.Empty<string>(), StringComparer.Ordinal);

    private ProjectIntentRegistration FindProjectIntentLocked(string id) => state.ProjectIntents.FirstOrDefault(value =>
        string.Equals(value?.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool ProjectIntentOwnedBy(ProjectIntentRegistration registration, BridgeRequest request)
    {
        if (registration == null || !string.Equals(registration.Status, "ACTIVE", StringComparison.Ordinal))
            return false;
        string owner = StableProjectOwner(request);
        string session = StableProjectSession(request);
        return string.Equals(registration.Owner, owner, StringComparison.Ordinal) &&
            string.Equals(registration.SessionId, session, StringComparison.Ordinal);
    }

    private void TouchProjectIntentLocked(ProjectIntentRegistration registration)
    {
        DateTime now = clock.UtcNow;
        registration.LastHeartbeatUtc = now;
        registration.ExpiresUtc = now.Add(options.ProjectIntentDuration);
        registration.Status = "ACTIVE";
    }

    private string NewProjectIntentIdLocked(string owner, string session, IReadOnlyList<string> aliases)
    {
        string seed = owner + "\n" + session + "\n" + string.Join(",", aliases ?? Array.Empty<string>()) +
            "\n" + clock.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        // This ID is content-derived and therefore intentionally deterministic
        // for the same owner/session/project/time seed. The truncated hash is
        // widened to 96 bits so distinct registrations do not share a short
        // 64-bit namespace.
        string id = "pi-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..24];
        int suffix = 1;
        string candidate = id;
        while (state.ProjectIntents.Any(value => string.Equals(value.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = id + "-" + suffix++.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private void EmitAggregateIntentStatusLocked(PersistedState snapshot, BridgeRequest request, Action<string> emit)
    {
        emit("Project intent registrations:");
        foreach (ProjectIntentRegistration registration in (snapshot.ProjectIntents ?? new List<ProjectIntentRegistration>())
                     .OrderBy(value => value.Status == "ACTIVE" ? 0 : 1).ThenBy(value => value.Id, StringComparer.Ordinal))
            emit("  " + registration.Id + " owner=" + registration.Owner + " session=" + registration.SessionId +
                " status=" + registration.Status + " projects=" + string.Join(",", registration.RequestedProjects) +
                " expiresUtc=" + FormatUtc(registration.ExpiresUtc));
        emit("Frozen generation: " + (snapshot.FrozenTargetGeneration > 0 ? snapshot.FrozenTargetGeneration.ToString(CultureInfo.InvariantCulture) : "none"));
        emit("Frozen launch owner/request: " + (snapshot.FrozenLaunchOwner ?? "none") + "/" +
            (snapshot.FrozenLaunchRequestKey ?? "none"));
        emit("Frozen projects: " + (snapshot.FrozenRequestedProjects.Count == 0 ? "none" : string.Join(", ", snapshot.FrozenRequestedProjects)));
        emit("Frozen package order: " + (snapshot.FrozenResolvedMods.Count == 0 ? "none" :
            string.Join(" -> ", snapshot.FrozenResolvedMods)));
        emit("Frozen profile/baseline fingerprints: " + (snapshot.FrozenProfileFingerprint ?? "none") + "/" +
            (snapshot.FrozenBaselineFingerprint ?? "none"));
        emit("Frozen registrations: " + (snapshot.FrozenRegistrations.Count == 0 ? "none" :
            string.Join(", ", snapshot.FrozenRegistrations.OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => value.Id + "=" + value.Owner + "/" + value.SessionId))));
        List<ProjectIntentRegistration> queued = ActiveProjectIntentsLocked(snapshot)
            .Where(value => snapshot.FrozenRegistrations.All(frozen => !string.Equals(frozen.Id, value.Id, StringComparison.Ordinal)))
            .ToList();
        emit("Queued next-generation registrations: " + (queued.Count == 0 ? "none" :
            string.Join(", ", queued.OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => value.Id + "=" + value.Owner + "/" + value.SessionId))));
        emit("Queued next-generation projects: " + (queued.Count == 0 ? "none" :
            string.Join(", ", CanonicalProjectUnion(queued.SelectMany(value => value.RequestedProjects)))));
    }

    private static List<ProjectIntentRegistration> ActiveProjectIntentsLocked(PersistedState snapshot) =>
        (snapshot.ProjectIntents ?? new List<ProjectIntentRegistration>()).Where(value =>
            value != null && string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal)).ToList();

    private void PruneProjectIntentsLocked()
    {
        state.ProjectIntents ??= new List<ProjectIntentRegistration>();
        bool changed = false;
        DateTime now = clock.UtcNow;
        foreach (ProjectIntentRegistration registration in state.ProjectIntents)
        {
            if (registration == null || !string.Equals(registration.Status, "ACTIVE", StringComparison.Ordinal))
                continue;
            if (registration.ExpiresUtc == default)
                registration.ExpiresUtc = (registration.LastHeartbeatUtc == default ? now : registration.LastHeartbeatUtc)
                    .Add(options.ProjectIntentDuration);
            if (registration.ExpiresUtc <= now)
            {
                registration.Status = "EXPIRED";
                registration.ReleasedUtc = now;
                registration.ReleaseReason = "owner heartbeat expired";
                changed = true;
            }
        }
        if (changed)
        {
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }
    }

    private static List<string> CanonicalProjectUnion(IEnumerable<string> aliases)
    {
        HashSet<string> distinct = new(StringComparer.OrdinalIgnoreCase);
        foreach (string alias in aliases ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(alias))
                distinct.Add(alias.Trim());
        return ModProfileResolver.CanonicalAliases(distinct).ToList();
    }

    private List<ProjectIntentRegistration> ActiveProjectIntentsLocked()
    {
        return ActiveProjectIntentsLocked(state).OrderBy(value => value.Id, StringComparer.Ordinal).ToList();
    }

    private static ProjectIntentSnapshot SnapshotProjectIntent(ProjectIntentRegistration registration) => new()
    {
        Id = registration.Id,
        Owner = registration.Owner,
        SessionId = registration.SessionId,
        RequestedProjects = (registration.RequestedProjects ?? new List<string>()).ToList()
    };

    private int RestoreBaseline(Action<string> emit)
    {
        lock (lifecycleGate)
        {
            lock (gate)
            {
                if (!CanChangeModsConfigLocked(emit))
                    return 4;
                if (!File.Exists(baselinePath))
                {
                    emit("Baseline restore failed: no captured baseline exists.");
                    emit("Next action: DevBridge.cmd mods capture-baseline");
                    return 4;
                }

                byte[] baseline = File.ReadAllBytes(baselinePath);
                string baselineFingerprint = HashBytes(baseline);
                if (!File.Exists(modsConfigPath))
                {
                    emit("Baseline restore failed: ModsConfig.xml was not found at " + modsConfigPath + ".");
                    return 4;
                }

                byte[] current = File.ReadAllBytes(modsConfigPath);
                string currentFingerprint = HashBytes(current);
                string ownership = CurrentModsConfigOwnershipLocked(current, currentFingerprint);
                if (currentFingerprint != baselineFingerprint && ownership != "DEVBRIDGE_GENERATED" &&
                    ownership != ModsConfigMutationAuthorityValues.ExternalMutated &&
                    ownership != "DEVBRIDGE_PENDING")
                {
                    RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                        "ModsConfig.xml differs from the captured baseline and is not a known DevBridge-generated file.");
                    emit("Baseline restore refused: an unexpected user edit would be overwritten.");
                    emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                    emit("Capture the intentional edit as the new baseline, or restore it manually before retrying.");
                    return 4;
                }

                if (currentFingerprint != baselineFingerprint)
                {
                    options.BeforeModsConfigWrite?.Invoke();
                    byte[] latest;
                    try
                    {
                        latest = File.ReadAllBytes(modsConfigPath);
                    }
                    catch
                    {
                        RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                            "ModsConfig.xml changed or disappeared while preparing the baseline restore.");
                        emit("Baseline restore refused: ModsConfig.xml changed while the restore was preparing.");
                        emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                        return 4;
                    }

                    if (!string.Equals(HashBytes(latest), currentFingerprint, StringComparison.Ordinal))
                    {
                        RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                            "ModsConfig.xml changed while preparing the baseline restore.");
                        emit("Baseline restore refused: an unexpected edit would be overwritten.");
                        emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                        return 4;
                    }

                    try
                    {
                        EnsureNoMatchingRimWorldProcess();
                    }
                    catch (ProfileException exception)
                    {
                        RecordProfileErrorLocked(exception.Code, exception.Message);
                        emit("Baseline restore refused: " + exception.Message);
                        emit("Error code: " + exception.Code);
                        return 4;
                    }
                    catch (ProcessInspectionException)
                    {
                        RecordProfileErrorLocked(ProcessInspection.ErrorCode, ProcessInspection.Message);
                        emit("Baseline restore refused: " + ProcessInspection.Message);
                        emit("Error code: " + ProcessInspection.ErrorCode);
                        return 4;
                    }

                    BeginModsConfigTransitionLocked(allowExternalMutationReconciliation: true);
                    try
                    {
                        AtomicWriteFile(modsConfigPath, baseline);
                    }
                    catch
                    {
                        AbortModsConfigTransitionLocked();
                        throw;
                    }
                }
                ClearGeneratedModsConfigManifestLocked();
                state.BaselineFingerprint = baselineFingerprint;
                state.ModsConfigOwnership = "BASELINE";
                state.ModsConfigGeneratedHash = null;
                state.ModsConfigGeneratedProfileFingerprint = null;
                state.ModsConfigGeneratedGeneration = 0;
                ClearActiveProfileLocked();
                state.LastKnownGoodProfile = PersistedProfileSnapshot.FromModProfile(
                    CreateBaselineProfileForMode(baselineFingerprint));
                state.RuntimeProfile = state.LastKnownGoodProfile;
                state.CrashIsolation = null;
                state.LaunchProfileFingerprint = null;
                state.LaunchProfileInstalled = false;
                state.LaunchAttemptStarted = false;
                state.ProfileErrorCode = null;
                state.ProfileError = null;
                state.ProfileConflict = null;
                ClearExternalModsConfigMutationLocked();
                state.Phase = BridgePhase.STOPPED;
                state.RequiresNewProcess = true;
                SaveStateLocked();
                emit(currentFingerprint == baselineFingerprint
                    ? "ModsConfig.xml already matches the captured baseline."
                    : "Restored ModsConfig.xml atomically from the captured byte-for-byte baseline.");
                emit("Baseline fingerprint: " + baselineFingerprint);
                emit("Restoration occurs only while no RimWorld process, lease, or pending restart is active.");
                return 0;
            }
        }
    }

}
