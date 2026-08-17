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
    private int Stop(BridgeRequest request, Action<string> emit)
    {
        if (request.Arguments.Count < 1 || string.IsNullOrWhiteSpace(request.Arguments[0]))
        {
            emit("Usage: DevBridge.cmd stop <lease-id>");
            return 2;
        }

        string leaseId = request.Arguments[0].Trim().ToUpperInvariant();
        lock (lifecycleGate)
        {
            int processId;
            long processStartIdentity;
            lock (gate)
            {
                SynchronizeLocked();
                if (!TryGetLeaseHolderLocked(leaseId, request, out TestLease lease))
                {
                    emit("Stop denied: lease " + leaseId + " is not held by this agent/session.");
                    EmitNextCommand(emit, "DevBridge.cmd test begin");
                    return 4;
                }

                if (state.MaintenanceReady)
                {
                    MaintenanceValidation validation = RevalidateMaintenanceReadyLocked();
                    if (!validation.Safe)
                    {
                        emit("Stop failed: " + validation.Error);
                        emit("Error code: " + validation.ErrorCode);
                        emit("maintenanceReady=false");
                        EmitNextCommand(emit, "DevBridge.cmd doctor");
                        return 4;
                    }

                    emit("RimWorld is already stopped for maintenance.");
                    emit("gameState=STOPPED maintenanceReady=true leaseState=HELD");
                    EmitNextCommand(emit, "DevBridge.cmd ensure-ready " + lease.Id);
                    return 0;
                }

                bool recoverableFailure = state.Phase == BridgePhase.ERROR &&
                    state.ProcessId > 0 && state.ProcessStartUtcTicks > 0 &&
                    state.ErrorCode != ProcessInspection.ErrorCode;
                if (state.RestartPending || state.Phase == BridgePhase.DRAINING ||
                    state.Phase == BridgePhase.RESTARTING || state.Phase == BridgePhase.LOADING ||
                    (state.Phase != BridgePhase.READY && state.ErrorCode != "READINESS_TIMEOUT" &&
                     !recoverableFailure))
                {
                    emit("Stop denied: RimWorld is not in a stoppable ready or timed-out state.");
                    emit("No launch was attempted.");
                    EmitKeepWaiting(emit);
                    return 4;
                }

                processId = state.ProcessId;
                processStartIdentity = state.ProcessStartUtcTicks;
            }

            (bool success, string errorCode, string error) result = StopForMaintenance(processId, processStartIdentity);
            lock (gate)
            {
                if (!result.success)
                {
                    state.MaintenanceReady = false;
                    state.ErrorCode = result.errorCode;
                    state.Error = "RimWorld was not stopped safely: " + result.error;
                    SaveStateLocked();
                    Monitor.PulseAll(gate);
                    emit("Stop failed: " + result.error);
                    emit("maintenanceReady=false");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }

                bool historyWritten = state.Phase != BridgePhase.READY || state.Generation <= 0 ||
                    TryRecordGenerationOutcomeLocked(state.Generation, "STOPPED", "PROCESS_STOPPED",
                        "The accepted RimWorld process was stopped normally.");
                state.Phase = BridgePhase.STOPPED;
                state.ProcessId = 0;
                state.ProcessStartUtcTicks = 0;
                state.OwnedProcessExecutablePath = null;
                state.MaintenanceReady = true;
                state.SessionDirty = true;
                state.Error = null;
                state.ErrorCode = null;
                state.RestartPending = false;
                state.TargetGeneration = 0;
                state.LaunchOwner = null;
                state.LaunchRequestKey = null;
                state.WaitingForBridgeDeadlineUtc = null;
                InvalidateRimBridgeEndpointLocked("RimWorld was stopped; the bridge endpoint is no longer valid.",
                    "PROCESS_EXITED");
                DeleteReadinessLocked();
                DeleteQuicktestFailureArtifactLocked();
                if (!historyWritten)
                {
                    state.ErrorCode = "GENERATION_HISTORY_CORRUPT";
                    state.Error = "RimWorld stopped, but its accepted-generation history could not be updated.";
                    SaveStateLocked();
                    Monitor.PulseAll(gate);
                    emit("Stop completed, but generation history is corrupt or unavailable.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }
                SaveStateLocked();
                Monitor.PulseAll(gate);
                emit("RimWorld stopped and confirmed absent from the configured installation.");
                emit("gameState=STOPPED maintenanceReady=true leaseState=HELD");
                EmitNextCommand(emit, "DevBridge.cmd ensure-ready " + leaseId);
                return 0;
            }
        }
    }

    private int EnsureReady(BridgeRequest request, Action<string> emit)
    {
        if (request.Arguments.Count < 1 || string.IsNullOrWhiteSpace(request.Arguments[0]))
        {
            emit("Usage: DevBridge.cmd ensure-ready <lease-id>");
            return 2;
        }

        string leaseId = request.Arguments[0].Trim().ToUpperInvariant();
        lock (lifecycleGate)
        {
            int targetGeneration = 0;
            bool shouldLaunch = false;
            lock (gate)
            {
                SynchronizeLocked();
                if (ExternalMutationBlocksLaunchLocked(emit, "Ensure-ready"))
                    return 4;
                if (!TryGetLeaseHolderLocked(leaseId, request, out TestLease lease))
                {
                    emit("Ensure-ready denied: lease " + leaseId + " is not held by this agent/session.");
                    return 4;
                }

                string requestOwner = LaunchOwnerFor(request);
                if (!state.MaintenanceReady &&
                    (state.Phase == BridgePhase.RESTARTING || state.Phase == BridgePhase.LOADING ||
                     state.Phase == BridgePhase.DRAINING || state.RestartPending))
                {
                    if (string.Equals(state.LaunchOwner, requestOwner, StringComparison.Ordinal))
                    {
                        emit("Ensure-ready is already owned by this agent/session.");
                        EmitNextCommand(emit, "DevBridge.cmd wait-ready");
                        return 0;
                    }

                    emit("Ensure-ready denied: another owner is already launching this runtime slot.");
                    emit("No launch was attempted.");
                    return 4;
                }

                if (state.MaintenanceReady)
                {
                    MaintenanceValidation validation = RevalidateMaintenanceReadyLocked();
                    if (!validation.Safe)
                    {
                        emit("Ensure-ready denied: " + validation.Error);
                        emit("Error code: " + validation.ErrorCode);
                        emit("No launch was attempted.");
                        EmitNextCommand(emit, "DevBridge.cmd doctor");
                        return 4;
                    }

                    ModProfile requestedProfile = null;
                    List<ProjectIntentRegistration> registrations = ActiveProjectIntentsLocked();
                    if (registrations.Count > 0)
                    {
                        try
                        {
                            List<string> aliases = CanonicalProjectUnion(
                                registrations.SelectMany(value => value.RequestedProjects));
                            requestedProfile = ResolveAggregateProfile(aliases,
                                (state.TestInputs ?? new List<TestInputValue>()).Select(value =>
                                    new TestInputAssignment { Name = value.Name, Value = value.Value }));
                            ModProfileResolver.ValidateResolvedProfile(requestedProfile);
                            EnsureAggregateBaselineLocked(requestedProfile.BaselineFingerprint);
                        }
                        catch (ProfileException exception)
                        {
                            RecordProfileErrorLocked(exception.Code, exception.Message);
                            emit("Ensure-ready denied: " + exception.Message);
                            emit("Error code: " + exception.Code);
                            emit("No launch was attempted.");
                            return 4;
                        }
                    }

                    targetGeneration = Math.Max(1, state.Generation + 1);
                    if (!TryAcquireLaunchOwnerLocked(requestOwner, "ensure-" + targetGeneration, resetBudget: true))
                    {
                        emit("Ensure-ready denied: the runtime slot launch owner is unavailable.");
                        emit("No launch was attempted.");
                        return 4;
                    }
                    if (requestedProfile != null)
                    {
                        ArchiveCompletedIsolationLocked();
                        SetActiveProfileLocked(requestedProfile);
                        state.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(requestedProfile);
                        state.LaunchProfileFingerprint = null;
                        state.LaunchProfileInstalled = false;
                        state.ProfileErrorCode = null;
                        state.ProfileError = null;
                        state.ProfileConflict = null;
                    }
                    state.TargetGeneration = targetGeneration;
                    state.MaintenanceReady = false;
                    state.Error = null;
                    state.ErrorCode = null;
                    state.Phase = BridgePhase.RESTARTING;
                    DeleteReadinessLocked();
                    DeleteQuicktestFailureArtifactLocked();
                    if (requestedProfile != null)
                        FreezeAggregateLocked(requestedProfile, registrations, targetGeneration,
                            requestOwner, "ensure-" + targetGeneration);
                    else
                        SaveStateLocked();
                    shouldLaunch = true;
                }
                else if (state.Phase == BridgePhase.READY && !state.RestartPending)
                {
                    emit("RimWorld is already ready.");
                    EmitNextCommand(emit, "DevBridge.cmd test end " + lease.Id);
                    return 0;
                }
                else if (IsLateReadinessRecoverableError(state.ErrorCode))
                {
                    if (TryAcceptLateReadinessLocked())
                    {
                        emit("Late quicktest readiness accepted from the original process.");
                        emit("RimWorld is ready.");
                        EmitNextCommand(emit, "DevBridge.cmd test end " + lease.Id);
                        return 0;
                    }

                    if (state.ErrorCode == ProcessInspection.ErrorCode)
                    {
                        emit(ProcessInspection.Message);
                        emit("No launch was attempted.");
                        EmitNextCommand(emit, "DevBridge.cmd doctor");
                        return 4;
                    }

                    emit("READINESS_TIMEOUT: the original RimWorld process is still not ready.");
                    emit("No launch was attempted.");
                    EmitNextCommand(emit, "DevBridge.cmd stop " + lease.Id);
                    return 4;
                }
                else
                {
                    emit("Ensure-ready denied: no confirmed maintenance window or reusable timed-out process exists.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }
            }

            if (shouldLaunch)
            {
                emit("Maintenance window released by lease holder; launching one new RimWorld process.");
                LaunchGenerationWorker(targetGeneration, isRestart: true, owner: LaunchOwnerFor(request));
            }

            lock (gate)
            {
                if (state.Generation >= targetGeneration && state.Phase == BridgePhase.READY &&
                    !state.RestartPending)
                {
                    emit("RimWorld is ready.");
                    emit("Generation: " + state.Generation);
                    EmitNextCommand(emit, "DevBridge.cmd test end " + leaseId);
                    return 0;
                }

                emit(string.IsNullOrWhiteSpace(state.Error) ?
                    "Ensure-ready did not reach quicktest readiness." : state.Error);
                if (state.ErrorCode == "READINESS_TIMEOUT")
                    emit("READINESS_TIMEOUT");
                EmitNextCommand(emit, "DevBridge.cmd doctor");
                return 4;
            }
        }
    }

    private int Restart(BridgeRequest request, Action<string> emit, Func<bool> budgetAvailable = null)
    {
        RestartArguments restartArguments;
        try
        {
            restartArguments = ParseRestartArguments(request.Arguments);
        }
        catch (ProfileException exception)
        {
            if (exception.Code.StartsWith("TEST_INPUT_", StringComparison.Ordinal))
            {
                request.TestInputErrorCode = exception.Code;
                request.TestInputError = exception.Message;
            }
            else
                RecordProfileError(exception.Code, exception.Message);
            emit("Restart request denied: " + exception.Message);
            emit("Error code: " + exception.Code);
            return 2;
        }

        // Validate project aliases before a duplicate/persisted restart can be
        // treated as already complete.  This keeps a malformed late request
        // from being hidden by the previous generation's status error.
        if (!restartArguments.LegacyProduction && restartArguments.HasProjects &&
            restartArguments.Projects.Count > 0)
        {
            try
            {
                ModProfileResolver.CanonicalAliases(restartArguments.Projects);
            }
            catch (ProfileException exception)
            {
                RecordProfileError(exception.Code, exception.Message);
                emit("Profile request denied: " + exception.Message);
                emit("Error code: " + exception.Code);
                emit("No launch was attempted.");
                return 4;
            }
        }

        try
        {
            TestGenerationInputs.Normalize(restartArguments.TestInputs,
                restartArguments.LegacyProduction ? ModProfile.LegacyMode : ModProfile.ProjectsMode);
        }
        catch (ProfileException exception)
        {
            request.TestInputErrorCode = exception.Code;
            request.TestInputError = exception.Message;
            emit("Test input request denied: " + exception.Message);
            emit("Error code: " + exception.Code);
            emit("No launch was attempted.");
            return 4;
        }

        int targetGeneration;
        int currentGeneration;
        bool alreadyPending;
        bool observedPending;
        int observedTargetGeneration;
        string observedLaunchOwner;
        bool pendingBeforeSynchronize;
        int targetBeforeSynchronize;
        string ownerBeforeSynchronize;
        ModProfile requestedProfile = null;
        IReadOnlyList<string> compatibilityAliases = null;
        lock (gate)
        {
            pendingBeforeSynchronize = state.RestartPending;
            targetBeforeSynchronize = state.TargetGeneration;
            ownerBeforeSynchronize = state.LaunchOwner;
            SynchronizeLocked();
            if (ExternalMutationBlocksLaunchLocked(emit, "Restart"))
                return 4;
            if (IsolationActiveLocked())
            {
                emit("Restart is unavailable while crash isolation is active; the in-flight incident is immutable.");
                emit("Error code: CRASH_ISOLATION_RUNNING");
                emit("Next action: Run DevBridge.cmd status and keep polling.");
                return 4;
            }
            if (restartArguments.LegacyProduction && ActiveProjectIntentsLocked().Count > 0)
            {
                RecordProfileErrorLocked("PROFILE_LEGACY_CONFLICT",
                    "active project registrations require an aggregate launch; release them before explicit human legacy mode.");
                emit("Legacy production restart denied: active project registrations require an aggregate launch.");
                emit("Error code: PROFILE_LEGACY_CONFLICT");
                emit("No launch was attempted.");
                return 4;
            }
            if (state.RestartPending && !string.IsNullOrWhiteSpace(state.LaunchOwner) &&
                !string.Equals(state.LaunchOwner, LaunchOwnerFor(request), StringComparison.Ordinal))
            {
                emit("Restart denied: another owner already controls this runtime slot launch.");
                emit("No launch was attempted.");
                return 4;
            }
            currentGeneration = state.Generation;
            alreadyPending = state.RestartPending;
            targetGeneration = state.TargetGeneration;
            observedPending = pendingBeforeSynchronize || state.RestartPending;
            observedTargetGeneration = pendingBeforeSynchronize
                ? targetBeforeSynchronize : state.TargetGeneration;
            observedLaunchOwner = pendingBeforeSynchronize
                ? ownerBeforeSynchronize : state.LaunchOwner;
        }

        lock (lifecycleGate)
        {
            lock (gate)
            {
                SynchronizeLocked();
                if (ExternalMutationBlocksLaunchLocked(emit, "Restart"))
                    return 4;
                if (state.ErrorCode == ProcessInspection.ErrorCode)
                {
                    emit(ProcessInspection.Message);
                    emit("No launch was attempted.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }

                currentGeneration = state.Generation;
                alreadyPending = state.RestartPending;
                if (!alreadyPending && observedPending && observedTargetGeneration > 0 &&
                    state.Phase == BridgePhase.READY && state.Generation >= observedTargetGeneration &&
                    string.Equals(observedLaunchOwner, LaunchOwnerFor(request), StringComparison.Ordinal))
                {
                    if (!TestInputsMatchAcceptedGenerationLocked(restartArguments.TestInputs))
                    {
                        emit("Restart denied: the completed generation has different frozen test inputs.");
                        emit("Error code: TEST_INPUT_CONFLICT");
                        emit("No launch was attempted.");
                        return 4;
                    }
                    if (!restartArguments.LegacyProduction && restartArguments.HasProjects &&
                        restartArguments.Projects.Count > 0)
                    {
                        ProjectIntentRegistration lateRegistration = EnsureCompatibilityRegistrationLocked(
                            request, ModProfileResolver.CanonicalAliases(restartArguments.Projects));
                        SaveStateLocked();
                        if (lateRegistration != null && !state.FrozenRegistrations.Any(value =>
                                string.Equals(value.Id, lateRegistration.Id, StringComparison.Ordinal)))
                        {
                            emit("Project intent " + lateRegistration.Id +
                                " was registered after the completed generation and is queued for the next generation.");
                            emit("The completed generation/profile evidence is immutable.");
                        }
                    }
                    emit("Restart already completed for generation " + observedTargetGeneration + ".");
                    emit("The duplicate request did not launch another RimWorld process.");
                    return 0;
                }
                if (alreadyPending)
                {
                    targetGeneration = state.TargetGeneration;
                    if (restartArguments.LegacyProduction && state.LaunchProfileMode != "explicit-human-legacy")
                    {
                        emit("Restart denied: the frozen generation is an aggregate profile and cannot be replaced by legacy production mode.");
                        emit("Error code: PROFILE_CONFLICT");
                        emit("No launch was attempted.");
                        return 4;
                    }
                }

                if (state.MaintenanceReady && !alreadyPending)
                {
                    if (string.IsNullOrWhiteSpace(restartArguments.LeaseId) ||
                        !TryGetLeaseHolderLocked(restartArguments.LeaseId, request, out TestLease maintenanceLease))
                    {
                        emit("Restart denied: a lease-holder token is required while maintenanceReady=true.");
                        emit("No launch was attempted.");
                        return 4;
                    }

                    MaintenanceValidation validation = RevalidateMaintenanceReadyLocked();
                    if (!validation.Safe)
                    {
                        emit("Restart denied: " + validation.Error);
                        emit("Error code: " + validation.ErrorCode);
                        emit("No launch was attempted.");
                        EmitNextCommand(emit, "DevBridge.cmd doctor");
                        return 4;
                    }
                    state.Leases.Remove(maintenanceLease);
                    state.MaintenanceReady = false;
                    state.SessionDirty = true;
                }
                else if (!alreadyPending && state.Phase == BridgePhase.STOPPED && state.SessionDirty &&
                         (state.ErrorCode == ProcessInspection.ErrorCode || state.ErrorCode == "MAINTENANCE_PROCESS_PRESENT"))
                {
                    emit("Restart denied: the maintenance window is not safe to leave without a fresh process check.");
                    emit("No launch was attempted.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }

                if (alreadyPending)
                {
                    if (!TestInputsMatchFrozenGenerationLocked(restartArguments.TestInputs))
                    {
                        emit("Restart denied: the pending generation already has incompatible test inputs.");
                        emit("Error code: TEST_INPUT_CONFLICT");
                        emit("No launch was attempted.");
                        return 4;
                    }
                    if (!restartArguments.LegacyProduction && restartArguments.HasProjects &&
                        restartArguments.Projects.Count > 0)
                    {
                        IReadOnlyList<string> lateAliases;
                        try
                        {
                            lateAliases = ModProfileResolver.CanonicalAliases(restartArguments.Projects);
                        }
                        catch (ProfileException exception)
                        {
                            RecordProfileErrorLocked(exception.Code, exception.Message);
                            emit("Profile request denied: " + exception.Message);
                            emit("Error code: " + exception.Code);
                            emit("No launch was attempted.");
                            return 4;
                        }
                        ProjectIntentRegistration lateRegistration = EnsureCompatibilityRegistrationLocked(
                            request, lateAliases);
                        SaveStateLocked();
                        if (lateRegistration != null && !state.FrozenRegistrations.Any(value =>
                                string.Equals(value.Id, lateRegistration.Id, StringComparison.Ordinal)))
                        {
                            emit("Project intent " + lateRegistration.Id +
                                " was registered after the frozen generation and is queued for the next generation.");
                            emit("The current frozen registration/profile evidence is immutable.");
                        }
                        emit("The current restart remains owned by its original request; no replacement launch was started.");
                        return 0;
                    }
                    emit("Restart already accepted for generation " + currentGeneration + " -> " + targetGeneration + ".");
                }
                else
                {
                    try
                    {
                        if (restartArguments.LegacyProduction)
                        {
                            if (options.RimBridgeMode != RimBridgeMode.Off)
                                throw new ProfileException("RIMBRIDGE_LEGACY_CONFLICT",
                                    "explicit legacy production mode cannot be used while RimBridge mode is " +
                                    RimBridgeModes.Text(options.RimBridgeMode) + "; use the aggregate profile path.");
                            if (ActiveProjectIntentsLocked().Count > 0)
                                throw new ProfileException("PROFILE_LEGACY_CONFLICT",
                                    "active project registrations must be released before explicit human legacy production mode.");
                        }
                        else
                        {
                            List<string> aliases = AggregateAliasesLocked(restartArguments);
                            requestedProfile = ResolveAggregateProfile(aliases, restartArguments.TestInputs);
                            ModProfileResolver.ValidateResolvedProfile(requestedProfile);
                            EnsureAggregateBaselineLocked(requestedProfile.BaselineFingerprint);
                            if (restartArguments.HasProjects && restartArguments.Projects.Count > 0)
                                compatibilityAliases = CanonicalProjectUnion(restartArguments.Projects);
                        }
                    }
                    catch (ProfileException exception)
                    {
                        RecordProfileErrorLocked(exception.Code, exception.Message);
                        emit("Profile request denied: " + exception.Message);
                        emit("Error code: " + exception.Code);
                        emit("No launch was attempted.");
                        return 4;
                    }

                    targetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
                    string requestKey = "restart-" + targetGeneration;
                    if (!TryAcquireLaunchOwnerLocked(LaunchOwnerFor(request), requestKey, resetBudget: true))
                    {
                        emit("Restart denied: another owner already controls this runtime slot launch.");
                        emit("No launch was attempted.");
                        return 4;
                    }
                    if (!restartArguments.LegacyProduction && compatibilityAliases != null)
                        EnsureCompatibilityRegistrationLocked(request, compatibilityAliases);
                    ArchiveCompletedIsolationLocked();
                    if (restartArguments.LegacyProduction)
                    {
                        ClearActiveProfileLocked();
                        state.RuntimeProfile = null;
                        state.FrozenRegistrations = new List<ProjectIntentSnapshot>();
                        state.FrozenRequestedProjects = new List<string>();
                        state.FrozenResolvedProjectPackageIds = new List<string>();
                        state.FrozenResolvedMods = new List<string>();
                        state.FrozenTestInputs = new List<TestInputValue>();
                        state.FrozenProfileFingerprint = null;
                        state.FrozenBaselineFingerprint = null;
                        state.FrozenTargetGeneration = targetGeneration;
                        state.FrozenLaunchOwner = LaunchOwnerFor(request);
                        state.FrozenLaunchRequestKey = requestKey;
                        state.AggregateFreezePending = false;
                        state.AggregateFrozenUtc = clock.UtcNow;
                    }
                    else
                    {
                        SetActiveProfileLocked(requestedProfile);
                    }
                    state.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(
                        restartArguments.LegacyProduction ? null : requestedProfile);
                    state.LaunchProfileFingerprint = null;
                    state.LaunchProfileInstalled = false;
                    state.ProfileErrorCode = null;
                    state.ProfileError = null;
                    state.ProfileConflict = null;
                    state.TargetGeneration = targetGeneration;
                    state.RestartPending = true;
                    state.RestartRequestedUtc = clock.UtcNow;
                    state.WaitingForBridgeDeadlineUtc = null;
                    state.RequiresNewProcess = true;
                    state.Error = null;
                    state.ErrorCode = null;
                    state.Phase = BridgePhase.DRAINING;
                    InvalidateRimBridgeEndpointLocked("A restart was accepted; the previous bridge endpoint is stale.",
                        "RIMBRIDGE_GENERATION_CHANGED");
                    DeleteReadinessLocked();
                    DeleteQuicktestFailureArtifactLocked();
                    if (restartArguments.LegacyProduction)
                    {
                        state.AggregateGenerations ??= new List<AggregateGenerationEvidence>();
                        state.AggregateGenerations.Add(new AggregateGenerationEvidence
                        {
                            Generation = targetGeneration,
                            FrozenUtc = clock.UtcNow,
                            LaunchOwner = LaunchOwnerFor(request),
                            LaunchRequestKey = requestKey,
                            ProfileMode = "explicit-human-legacy"
                        });
                        while (state.AggregateGenerations.Count > 16)
                            state.AggregateGenerations.RemoveAt(0);
                        SaveStateLocked();
                    }
                    else
                    {
                        List<ProjectIntentRegistration> registrations = ActiveProjectIntentsLocked();
                        FreezeAggregateLocked(requestedProfile, registrations, targetGeneration,
                            LaunchOwnerFor(request), requestKey);
                    }
                    StartRestartWorkerLocked(targetGeneration, LaunchOwnerFor(request));
                    Monitor.PulseAll(gate);
                }
            }
        }

        if (!alreadyPending)
            emit("Restart accepted for generation " + currentGeneration + " -> " + targetGeneration + ".");
        emit("Agent/session: " + request.Agent);
        emit("DevBridge now owns this restart.");
        lock (gate)
            EmitProfile(state, emit);
        emit("If this command is interrupted or times out, do not request another restart.");
        EmitNextCommand(emit, "DevBridge.cmd wait-ready");

        EmitRestartWait(emit);
        while (true)
        {
            if (budgetAvailable != null && !budgetAvailable())
            {
                emit("Restart remains accepted, but the caller recipe budget is exhausted.");
                EmitNextCommand(emit, "DevBridge.cmd agent wait-event --until ready");
                return 5;
            }
            lock (gate)
            {
                SynchronizeLocked();
                if (state.Generation >= targetGeneration && state.Phase == BridgePhase.READY && !state.RestartPending)
                {
                    emit(string.Empty);
                    emit("RimWorld restarted successfully.");
                    emit("Generation: " + state.Generation);
                    emit("Quicktest map is ready.");
                    List<string> missingProjects = MissingProjectsFor(state, request);
                    EmitNextCommand(emit, missingProjects.Count == 0
                        ? "DevBridge.cmd test begin"
                        : "DevBridge.cmd restart");
                    return 0;
                }

                if (state.Phase == BridgePhase.ERROR && !state.RestartPending)
                {
                    emit("Restart failed: " + state.Error);
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }
            }

            WaitForStateChange(ProgressInterval);
            EmitRestartWait(emit);
        }
    }

    private bool TestInputsMatchFrozenGenerationLocked(IEnumerable<TestInputAssignment> assignments)
    {
        try
        {
            string mode = string.Equals(state.LaunchProfileMode, "explicit-human-legacy",
                StringComparison.OrdinalIgnoreCase) ? ModProfile.LegacyMode : ModProfile.ProjectsMode;
            TestInputSet requested = TestGenerationInputs.Normalize(assignments, mode);
            return TestGenerationInputs.AreEquivalent(requested.Values, state.FrozenTestInputs);
        }
        catch (ProfileException)
        {
            return false;
        }
    }

    private bool TestInputsMatchAcceptedGenerationLocked(IEnumerable<TestInputAssignment> assignments)
    {
        try
        {
            GenerationHistoryView history = BuildGenerationHistoryViewLocked(state.Generation);
            IEnumerable<TestInputValue> accepted = history.Current?.Manifest?.Profile?.TestInputs ??
                state.TestInputs ?? new List<TestInputValue>();
            TestInputSet requested = TestGenerationInputs.Normalize(assignments, ModProfile.ProjectsMode);
            return !history.Corrupt && TestGenerationInputs.AreEquivalent(requested.Values, accepted);
        }
        catch (ProfileException)
        {
            return false;
        }
    }

}
