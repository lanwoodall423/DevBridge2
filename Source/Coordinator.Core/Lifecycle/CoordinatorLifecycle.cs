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
    private void FailLaunch(string detail, string errorCode = "LAUNCH_FAILED",
        QuicktestFailureRecord failure = null)
    {
        lock (gate)
        {
            string failurePhase = state.Phase.ToString();
            int historyGeneration = state.LaunchGeneration > 0 ? state.LaunchGeneration :
                (state.TargetGeneration > 0 ? state.TargetGeneration : state.Generation);
            FailureFingerprintInput failureInput = BuildFailureInputLocked(errorCode, failurePhase,
                detail, failure);
            if (state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.DevBridgeTransition &&
                state.ExternalModsConfigMutation == null)
            {
                // Profile preparation can fail after the durable transition
                // marker is written but before AtomicWriteFile runs. Clear the
                // marker and restore the previous generated evidence so a
                // failed launch cannot make the next observation look like an
                // external edit.
                AbortModsConfigTransitionLocked();
            }
            if (failure != null)
            {
                state.TerminalFailureSchemaVersion = failure.SchemaVersion;
                state.TerminalFailurePhase = failure.FailurePhase;
                state.TerminalFailureCode = failure.FailureCode;
                state.TerminalFailureDetail = detail;
                state.TerminalFailureExceptionType = failure.ExceptionType;
                state.TerminalFailureExceptionMessage = failure.ExceptionMessage;
                state.TerminalFailureDiagnosticDetail = failure.DiagnosticDetail;
            }
            TryRecordGenerationOutcomeLocked(historyGeneration, "FAILED", errorCode, detail, failureInput);
            if (IsolationActiveLocked() && state.CrashIsolation.CurrentAttemptId == null &&
                string.Equals(state.CrashIsolation.OriginalLaunchId, state.LaunchId, StringComparison.Ordinal) &&
                state.CrashIsolation.OriginalGeneration == state.LaunchGeneration &&
                string.Equals(state.CrashIsolation.OriginalFailureCode, errorCode, StringComparison.Ordinal))
            {
                SaveStateLocked();
                return;
            }
            if (IsolationActiveLocked() && state.CrashIsolation.CurrentAttemptId != null)
            {
                CrashIsolationIncident incident = state.CrashIsolation;
                if (!IsIsolationEvidenceFailure(errorCode) ||
                    !state.LaunchProfileInstalled || !state.LaunchAttemptStarted ||
                    !string.Equals(state.LaunchProfileFingerprint,
                        incident.CurrentAttemptFingerprint, StringComparison.Ordinal))
                {
                    FinalizeIsolationEnvironmentalLocked(errorCode, detail);
                    return;
                }

                if (incident.CurrentAttemptResult != null)
                    return;
                incident.CurrentAttemptResult = "FAIL";
                incident.CurrentAttemptFailurePhase = failurePhase;
                incident.CurrentAttemptFailureCode = errorCode;
                incident.CurrentAttemptFailureDetail = detail;
                incident.CurrentAttemptProfileInstalled = state.LaunchProfileInstalled;
                state.Phase = BridgePhase.ISOLATING;
                state.RestartPending = true;
                state.ErrorCode = "CRASH_ISOLATION_ATTEMPT_FAILED";
                state.Error = detail;
                SaveStateLocked();
                QueueIsolationContinuationLocked();
                Monitor.PulseAll(gate);
                return;
            }

            if (IsolationActiveLocked())
            {
                FinalizeIsolationEnvironmentalLocked(errorCode, detail);
                return;
            }

            if (IsEligibleForCrashIsolationLocked(errorCode))
            {
                BeginCrashIsolationLocked(detail, errorCode, failure);
                AttachLatestFailureDiagnosisReferenceLocked();
                SaveStateLocked();
                return;
            }

            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
            state.ErrorCode = errorCode;
            if (errorCode.StartsWith("PROFILE_", StringComparison.Ordinal) ||
                errorCode.StartsWith("MODS_CONFIG_", StringComparison.Ordinal))
            {
                state.ProfileErrorCode = errorCode;
                state.ProfileError = detail;
            }
            state.Error = errorCode == ProcessInspection.ErrorCode ? ProcessInspection.Message :
                errorCode == "READINESS_TIMEOUT" ?
                "READINESS_TIMEOUT: " + detail + ". The original process was retained; no replacement launch was attempted." :
                "RimWorld did not report a playable quicktest map: " + detail +
                ". Inspect Runtime/readiness.json and the RimWorld logs, then run DevBridge.cmd restart.";
            state.LaunchOwner = null;
            state.LaunchRequestKey = null;
            state.WaitingForBridgeDeadlineUtc = null;
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }
    }

    private static string DescribeLaunchFailure(Exception exception, IManagedProcess process)
    {
        if (exception is ProcessInspectionException)
            return ProcessInspection.Message;
        if (exception is RimBridgeIntegrationException)
            return exception.Message;
        if (exception is FileNotFoundException)
            return exception.Message;
        if (process != null)
        {
            try
            {
                if (process.HasExited)
                    return "RimWorld exited before readiness";
            }
            catch
            {
                // Use the original exception below.
            }
        }

        return exception.GetType().Name + ": " + exception.Message;
    }

    private (bool success, string errorCode, string error) StopOwnedProcess(int processId,
        long startTicks, bool allowCachedOwnershipProof = false)
    {
        if (processId <= 0)
            return (true, null, null);

        IManagedProcess process = null;
        bool terminationRequested = false;
        bool terminationAuthorizationAccepted = false;
        try
        {
            DateTime inspectionDeadline = clock.UtcNow.Add(options.ProcessInspectionRetryTimeout);
            while (true)
            {
                try
                {
                    process = processAdapter.Open(processId);
                    if (process == null)
                        return (true, null, null);

                    bool alreadyExited = process.HasExited;
                    // An exited process can no longer expose MainModule.FileName
                    // even though its start identity remains queryable. The
                    // persisted PID/start pair is the exact owner proof for
                    // this already-exited coordinator launch; the next launch
                    // still performs a complete zero-process census.
                    if (alreadyExited)
                    {
                        if (!IsExactExitedProcessIdentity(process, startTicks))
                            return (false, "PROCESS_IDENTITY_CHANGED",
                                "the persisted RimWorld process identity no longer matches");

                        TraceEvent("process.termination.confirmed", detail: "already-exited", success: true);
                        return (true, null, null);
                    }

                    // Prove the live process identity before requesting
                    // termination. If it crosses into exit while this proof is
                    // being read, the bounded observation below can re-open it
                    // and validate the exited start identity safely.
                    bool authorized;
                    try
                    {
                        authorized = IsExactProcessIdentityForTermination(process, processId,
                            startTicks, allowCachedOwnershipProof);
                    }
                    catch (ProcessInspectionException exception)
                    {
                        TraceEvent("termination.authorization.rejected", success: false,
                            errorCode: ProcessInspection.ErrorCode,
                            detail: "identity-inspection;stage=" + (exception.Stage ?? "unknown"));
                        throw;
                    }
                    if (!authorized)
                    {
                        TraceEvent("termination.authorization.rejected", success: false,
                            errorCode: "PROCESS_IDENTITY_CHANGED", detail: "identity-mismatch");
                        return (false, "PROCESS_IDENTITY_CHANGED",
                            "the persisted RimWorld process identity no longer matches");
                    }

                    terminationAuthorizationAccepted = true;
                    TraceEvent("termination.authorization.accepted", success: true,
                        detail: "pid/start identity verified;cached-path-proof=" +
                            allowCachedOwnershipProof.ToString().ToLowerInvariant());

                    bool requested = process.RequestTermination();
                    terminationRequested = true;
                    TraceEvent("process.termination.requested", success: requested,
                        errorCode: requested ? null : "TERMINATION_REQUEST_REJECTED");
                    bool exited = process.WaitForExit(options.ProcessExitTimeout);
                    if (!exited)
                    {
                        TraceEvent("process.termination.force_requested");
                        if (!process.ForceTerminate())
                            return (false, "STOP_FAILED", "process exit was not confirmed");
                    }

                    InjectFaultForTesting(CoordinatorFaultPoint.AfterProcessActionBeforeResultingStatePersistence);
                    TraceEvent("process.termination.confirmed", success: true);
                    return (true, null, null);
                }
                catch (ProcessInspectionException)
                {
                    // Only the pre-termination identity observation may be
                    // retried. Once termination was requested, uncertainty is
                    // terminal and remains fail-closed.
                    if (terminationAuthorizationAccepted || terminationRequested ||
                        clock.UtcNow >= inspectionDeadline)
                    {
                        TraceEvent("process.termination.failed", success: false,
                            errorCode: ProcessInspection.ErrorCode,
                            detail: terminationAuthorizationAccepted
                                ? "termination-dispatch-or-post-request-inspection"
                                : "pre-termination-inspection-timeout");
                        return (false, ProcessInspection.ErrorCode, ProcessInspection.Message);
                    }

                    process?.Dispose();
                    process = null;
                    TimeSpan remaining = inspectionDeadline - clock.UtcNow;
                    clock.Sleep(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
                }
            }
        }
        catch (ProcessInspectionException)
        {
            return (false, ProcessInspection.ErrorCode, ProcessInspection.Message);
        }
        catch
        {
            return (false, "STOP_FAILED", "the verified process could not be stopped safely");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private (bool success, string errorCode, string error) StopForMaintenance(int processId, long startTicks)
    {
        if (processId <= 0 || startTicks <= 0)
            return (false, "STOP_FAILED", "the persisted process PID/start identity is incomplete");

        try
        {
            using IManagedProcess process = processAdapter.Open(processId);
            if (process == null)
                return (false, "STOP_FAILED", "the persisted RimWorld process no longer exists");
            if (!IsOwnedProcess(process, startTicks))
                return (false, "PROCESS_IDENTITY_CHANGED", "the persisted process identity no longer matches");
            if (process.HasExited)
                return (false, "STOP_FAILED", "the verified process was already exited before termination was requested");
            bool requested = process.RequestTermination();
            TraceEvent("process.termination.requested", success: requested,
                errorCode: requested ? null : "TERMINATION_REQUEST_REJECTED");
            if (!requested)
                return (false, "STOP_FAILED", "the verified process rejected the termination request");
            if (!process.WaitForExit(options.ProcessExitTimeout) || !process.HasExited)
                return (false, "STOP_FAILED", "process exit was not confirmed within the configured timeout");

            List<UnmanagedRimWorldProcess> remaining = FindUnmanagedRimWorldProcesses(0, 0);
            if (remaining.Count != 0)
                return (false, "MAINTENANCE_PROCESS_PRESENT", "a matching RimWorld installation process remains");

            InjectFaultForTesting(CoordinatorFaultPoint.AfterProcessActionBeforeResultingStatePersistence);
            TraceEvent("process.termination.confirmed", success: true);
            return (true, null, null);
        }
        catch (ProcessInspectionException)
        {
            return (false, ProcessInspection.ErrorCode, ProcessInspection.Message);
        }
        catch
        {
            return (false, "STOP_FAILED", "RimWorld could not be stopped safely");
        }
    }

    private bool TryGetLeaseHolderLocked(string leaseId, BridgeRequest request, out TestLease lease)
    {
        // CLI commands are separate short-lived processes. The lease ID is the
        // capability and Agent is the durable caller identity; ClientProcessId
        // remains diagnostic metadata rather than a later-command authorization key.
        lease = state.Leases.FirstOrDefault(value =>
            string.Equals(value.Id, leaseId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.Agent, request.Agent, StringComparison.Ordinal));
        return lease != null;
    }

    private void MarkReadyLocked(string launchId, int targetGeneration, int processId, long processStartIdentity)
    {
        if (!string.Equals(state.LaunchId, launchId, StringComparison.Ordinal) ||
            state.LaunchGeneration != targetGeneration || state.ProcessId != processId ||
            state.ProcessStartUtcTicks != processStartIdentity || !IsOwnedProcess(processId, processStartIdentity))
            return;

        if (DetectExternalModsConfigMutationLocked(allowTransition: true,
                generationOverride: targetGeneration))
            return;

        // RimWorld may truncate or recreate Player.log during startup. Capture the
        // forensic boundary only after readiness has established that startup-side
        // initialization is complete; the pre-launch snapshot is provisional.
        RimBridgeLogBoundary authoritativeBoundary = CaptureLogBoundaryLocked(authoritative: true,
            out bool boundaryIntegrityInvalid);
        if (boundaryIntegrityInvalid)
        {
            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
            state.RequiresNewProcess = true;
            state.ErrorCode = RimBridgeIntegrationConstants.PlayerLogBoundaryInvalidCode;
            state.Error = authoritativeBoundary.Error ??
                "Player.log changed unexpectedly before the authoritative startup boundary.";
            state.RimBridge.ErrorCode = state.ErrorCode;
            state.RimBridge.Error = state.Error;
            SaveStateLocked();
            Monitor.PulseAll(gate);
            return;
        }

        // This path is the static install proof that was accepted together
        // with the READY generation. Restart may carry it through a transient
        // MainModule boundary, while fresh PID/start and absence checks remain
        // mandatory for lifecycle control.
        state.OwnedProcessExecutablePath = rimWorldExe;

        if (state.CrashIsolation != null &&
            !string.IsNullOrWhiteSpace(state.CrashIsolation.CurrentAttemptId))
        {
            CrashIsolationIncident incident = state.CrashIsolation;
            bool verifiedIsolationProjectLaunch = IsVerifiedFreshProjectLaunchLocked();
            incident.CurrentAttemptResult = "PASS";
            incident.CurrentAttemptFailurePhase = null;
            incident.CurrentAttemptFailureCode = null;
            incident.CurrentAttemptFailureDetail = null;
            incident.CurrentAttemptProfileInstalled = state.LaunchProfileInstalled;
            state.Generation = targetGeneration;
            state.Phase = BridgePhase.ISOLATING;
            state.Error = null;
            state.ErrorCode = null;
            state.RestartPending = true;
            state.TargetGeneration = targetGeneration;
            state.RequiresNewProcess = true;
            state.MaintenanceReady = false;
            if (verifiedIsolationProjectLaunch)
                state.SessionDirty = false;
            state.ModsConfigMutationAuthority =
                state.ModsConfigGeneratedGeneration == targetGeneration &&
                !string.IsNullOrWhiteSpace(state.ModsConfigGeneratedHash)
                    ? ModsConfigMutationAuthorityValues.ControlledFrozen
                    : ModsConfigMutationAuthorityValues.NotGenerationOwned;
            RefreshRimBridgePolicyStateLocked();
            SaveStateLocked();
            QueueIsolationContinuationLocked();
            Monitor.PulseAll(gate);
            return;
        }

        state.Generation = targetGeneration;
        state.Phase = BridgePhase.READY;
        state.Error = null;
        state.ErrorCode = null;
        state.RestartPending = false;
        state.RestartRequestedUtc = null;
        state.AggregateFreezePending = false;
        state.TargetGeneration = 0;
        state.LastLaunchOwner = state.LaunchOwner;
        state.LastLaunchRequestKey = state.LaunchRequestKey;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.WaitingForBridgeDeadlineUtc = null;
        state.RequiresNewProcess = false;
        state.MaintenanceReady = false;
            state.RuntimeProfile = state.LaunchProfileInstalled && state.LaunchProfileFingerprint != null
                ? (state.RuntimeProfile ?? (state.ProfileMode == ModProfile.LegacyMode ? null :
                PersistedProfileSnapshot.FromModProfile(ProfileFromStateLocked())))
                : state.RuntimeProfile;
        if (!TryRecordGenerationOutcomeLocked(targetGeneration, "READY"))
        {
            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
            state.ErrorCode = "GENERATION_HISTORY_CORRUPT";
            state.Error = "The accepted generation could not be pinned immutably; the generation was not accepted.";
            SaveStateLocked();
            Monitor.PulseAll(gate);
            return;
        }
        bool verifiedFreshProjectLaunch = IsVerifiedFreshProjectLaunchLocked();
        if (state.LaunchProfileInstalled && state.RuntimeProfile != null)
            state.LastKnownGoodProfile = state.RuntimeProfile;
        state.LaunchProfileFingerprint = null;
        state.LaunchProfileInstalled = false;
        state.LaunchAttemptStarted = false;
        if (verifiedFreshProjectLaunch)
            state.SessionDirty = false;
        state.TerminalFailureSchemaVersion = 0;
        state.TerminalFailurePhase = null;
        state.TerminalFailureCode = null;
        state.TerminalFailureDetail = null;
        state.TerminalFailureExceptionType = null;
        state.TerminalFailureExceptionMessage = null;
        state.TerminalFailureDiagnosticDetail = null;
        foreach (TestLease lease in state.Leases)
            lease.Generation = targetGeneration;
        state.ModsConfigMutationAuthority =
            state.ModsConfigGeneratedGeneration == targetGeneration &&
            !string.IsNullOrWhiteSpace(state.ModsConfigGeneratedHash)
                ? ModsConfigMutationAuthorityValues.ControlledFrozen
                : ModsConfigMutationAuthorityValues.NotGenerationOwned;
        RefreshRimBridgePolicyStateLocked();
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private bool IsConfirmedMaintenanceWindowLocked()
    {
        return state.MaintenanceReady && state.ProcessId == 0 &&
            state.ProcessStartUtcTicks == 0 && !state.RestartPending;
    }

    private MaintenanceValidation RevalidateMaintenanceReadyLocked()
    {
        if (!state.MaintenanceReady)
            return new MaintenanceValidation { Safe = false, ErrorCode = state.ErrorCode, Error = state.Error };

        try
        {
            List<UnmanagedRimWorldProcess> remaining = FindUnmanagedRimWorldProcesses(0, 0);
            if (remaining.Count == 0)
                return new MaintenanceValidation { Safe = true };

            MarkMaintenanceProcessPresentLocked();
            return new MaintenanceValidation
            {
                Safe = false,
                ErrorCode = state.ErrorCode,
                Error = state.Error
            };
        }
        catch (ProcessInspectionException)
        {
            MarkProcessInspectionAmbiguousLocked();
            return new MaintenanceValidation
            {
                Safe = false,
                ErrorCode = ProcessInspection.ErrorCode,
                Error = ProcessInspection.Message
            };
        }
    }

    private void MarkMaintenanceProcessPresentLocked()
    {
        state.MaintenanceReady = false;
        state.ErrorCode = "MAINTENANCE_PROCESS_PRESENT";
        state.Error = "A matching RimWorld process is present; assembly replacement is not safe.";
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void MarkProcessInspectionAmbiguousLocked()
    {
        state.MaintenanceReady = false;
        state.ErrorCode = ProcessInspection.ErrorCode;
        state.Error = ProcessInspection.Message;
        if (state.Phase == BridgePhase.READY || state.Phase == BridgePhase.LOADING ||
            state.Phase == BridgePhase.RESTARTING || state.Phase == BridgePhase.DRAINING)
        {
            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
        }
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private bool TryAcceptLateReadinessLocked()
    {
        try
        {
            if (!IsLateReadinessRecoverableError(state.ErrorCode) || state.ProcessId <= 0 ||
                state.ProcessStartUtcTicks <= 0 || state.LaunchGeneration <= 0)
                return false;

            if (state.ErrorCode == ProcessInspection.ErrorCode)
            {
                ProcessStatusSnapshot snapshot = EnumerateStatusProcessesLocked();
                if (!snapshot.OwnedProcessRunning || snapshot.MatchingProcessCount != 1 ||
                    snapshot.UnmanagedProcesses.Count != 0)
                    return false;
            }
            else if (!IsOwnedProcess(state.ProcessId, state.ProcessStartUtcTicks))
            {
                return false;
            }

            DateTime launchStarted = state.LaunchStartedUtc.ToUniversalTime();
            QuicktestFailureRecord failure = TryReadMatchingQuicktestFailure(
                state.LaunchId, state.LaunchGeneration, state.ProcessId,
                state.ProcessStartUtcTicks, launchStarted);
            bool readiness = IsReadinessMatch(state.LaunchId, state.ProcessId,
                state.LaunchGeneration, launchStarted);
            if (failure != null && readiness)
            {
                FailLaunch("a terminal quicktest failure and a matching readiness signal were both written; the launch is ambiguous",
                    "QUICKTEST_READINESS_CONFLICT", failure);
                return false;
            }
            if (failure != null)
            {
                FailLaunch(DescribeQuicktestFailure(failure),
                    QuicktestFailureArtifact.StableFailureCode, failure);
                return false;
            }
            if (!readiness)
                return false;

            MarkReadyLocked(state.LaunchId, state.LaunchGeneration, state.ProcessId, state.ProcessStartUtcTicks);
            return state.Phase == BridgePhase.READY;
        }
        catch (ProcessInspectionException)
        {
            MarkProcessInspectionAmbiguousLocked();
            return false;
        }
    }

    private static bool IsLateReadinessRecoverableError(string errorCode)
    {
        return errorCode == "READINESS_TIMEOUT" || errorCode == ProcessInspection.ErrorCode;
    }

    private void RecoverProcessInspectionQuarantineLocked()
    {
        state.Phase = BridgePhase.STOPPED;
        state.Error = null;
        state.ErrorCode = null;
        state.ProcessId = 0;
        state.ProcessStartUtcTicks = 0;
        state.OwnedProcessExecutablePath = null;
        state.LaunchId = null;
        state.LaunchStartedUtc = default;
        state.RestartPending = false;
        state.RestartRequestedUtc = null;
        state.MaintenanceReady = false;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.WaitingForBridgeDeadlineUtc = null;
        state.RequiresNewProcess = true;
        DeleteReadinessLocked();
        DeleteQuicktestFailureArtifactLocked();
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void SynchronizeLocked(bool reconcileLateReadiness = true)
    {
        if (persistedStateLoadBlocked)
            return;
        PruneStaleLeasesLocked();
        PruneProjectIntentsLocked();

        if (DetectExternalModsConfigMutationLocked())
            return;

        if (IsolationActiveLocked())
        {
            if (state.CrashIsolation?.CurrentAttemptResult != null &&
                (launchTask == null || launchTask.IsCompleted) &&
                (isolationTask == null || isolationTask.IsCompleted))
                ResumePersistedIsolationResultLocked();
            else if (state.Phase == BridgePhase.LOADING && state.ProcessId > 0 &&
                (launchTask == null || launchTask.IsCompleted) &&
                (isolationTask == null || isolationTask.IsCompleted))
            {
                if (IsolationLaunchStateMatchesLocked())
                    StartMonitorLaunchLocked(state.TargetGeneration);
                else
                    FinalizeIsolationEnvironmentalLocked("ISOLATION_PROFILE_MISMATCH",
                        "the persisted isolation launch profile does not match the durable candidate; no replacement launch was attempted");
            }
            else if (state.Phase == BridgePhase.LOADING && state.ProcessId <= 0 &&
                     (launchTask == null || launchTask.IsCompleted) &&
                     (isolationTask == null || isolationTask.IsCompleted))
                FailLaunch("the persisted isolation attempt has no verified process identity; attribution was not attempted",
                    "ISOLATION_RECOVERY_AMBIGUOUS");
            else if (state.Phase != BridgePhase.LOADING)
                StartIsolationWorkerLocked();
            return;
        }

        if (reconcileLateReadiness && state.Phase == BridgePhase.ERROR &&
            IsLateReadinessRecoverableError(state.ErrorCode))
            TryAcceptLateReadinessLocked();

        if (state.Phase == BridgePhase.LOADING && state.ProcessId <= 0 &&
            (launchTask == null || launchTask.IsCompleted) &&
            (restartTask == null || restartTask.IsCompleted) &&
            Volatile.Read(ref launchInvocationInProgress) == 0)
        {
            FailLaunch("the persisted launch has no verified process identity; no replacement launch was attempted",
                "LAUNCH_RECOVERY_AMBIGUOUS");
            return;
        }

        bool owned = false;
        if (state.Phase == BridgePhase.READY && state.ProcessId > 0)
        {
            try
            {
                bool cachedOwnershipProof = HasCachedOwnedProcessPathProofLocked(
                    state.ProcessId, state.ProcessStartUtcTicks);
                if (cachedOwnershipProof)
                {
                    ProcessOwnershipObservation observation = InspectOwnedProcessForLifecycle(
                        state.ProcessId, state.ProcessStartUtcTicks, allowCachedPathProof: true);
                    if (observation.Classification is ProcessOwnershipClassification.IdentityMismatch or
                        ProcessOwnershipClassification.InspectionUnavailable)
                    {
                        MarkProcessInspectionAmbiguousLocked();
                        return;
                    }
                    owned = observation.Classification == ProcessOwnershipClassification.OwnedRunning;
                }
                else
                {
                    // Older state files have no persisted static install
                    // proof. Keep their original strict, bounded identity
                    // probe and establish the proof only after it succeeds.
                    owned = IsOwnedProcess(state.ProcessId, state.ProcessStartUtcTicks);
                    if (owned)
                        state.OwnedProcessExecutablePath = rimWorldExe;
                }
            }
            catch (ProcessInspectionException)
            {
                MarkProcessInspectionAmbiguousLocked();
                return;
            }
        }

        if (state.Phase == BridgePhase.READY && (state.ProcessId <= 0 || !owned))
        {
            InvalidateRimBridgeEndpointLocked("The coordinator-owned RimWorld process is no longer running.",
                "PROCESS_EXITED");
            state.Phase = BridgePhase.STOPPED;
            state.Error = "The coordinator-owned RimWorld process is no longer running.";
                state.ErrorCode = "PROCESS_EXITED";
                state.MaintenanceReady = false;
                TryRecordGenerationOutcomeLocked(state.Generation, "STOPPED", "PROCESS_EXITED",
                "The accepted RimWorld process is no longer running.",
                BuildFailureInputLocked("PROCESS_EXITED", "PROCESS",
                    "The accepted RimWorld process is no longer running."));
            state.ProcessId = 0;
            state.ProcessStartUtcTicks = 0;
            state.OwnedProcessExecutablePath = null;
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }
        else if (state.Phase == BridgePhase.READY && options.RimBridgeMode != RimBridgeMode.Off)
        {
            DateTime bridgeDeadline = (state.LaunchStartedUtc == default ? clock.UtcNow :
                state.LaunchStartedUtc.ToUniversalTime()).Add(options.ReadinessTimeout);
            string bridgeErrorCode;
            string bridgeError;
            bool bridgeReady = TrySatisfyRimBridgeReadinessLocked(state.LaunchId, state.Generation,
                state.ProcessId, state.ProcessStartUtcTicks, bridgeDeadline,
                out bridgeErrorCode, out bridgeError);
            if (!bridgeReady && bridgeErrorCode != null && options.RimBridgeMode == RimBridgeMode.Required)
            {
                state.Phase = BridgePhase.ERROR;
                state.ErrorCode = bridgeErrorCode;
                state.Error = bridgeError;
                SaveStateLocked();
                Monitor.PulseAll(gate);
            }
        }

        if (state.RestartPending)
        {
            if (state.TargetGeneration <= state.Generation)
                state.TargetGeneration = state.Generation + 1;
            if (string.IsNullOrWhiteSpace(state.LaunchOwner))
            {
                FailLaunch("the accepted restart has no durable launch owner; no launch was attempted",
                    "LAUNCH_OWNER_MISSING");
                return;
            }
            if ((state.Phase != BridgePhase.LOADING || launchTask == null || launchTask.IsCompleted) &&
                (restartTask == null || restartTask.IsCompleted))
                StartRestartWorkerLocked(state.TargetGeneration, state.LaunchOwner);
        }
        else if (state.Phase == BridgePhase.LOADING && state.ProcessId > 0 &&
                 (launchTask == null || launchTask.IsCompleted))
        {
            StartMonitorLaunchLocked(state.TargetGeneration);
        }
    }

    private void PruneStaleLeasesLocked()
    {
        DateTime cutoff = clock.UtcNow - options.LeaseDuration;
        int before = state.Leases.Count;
        List<TestLease> stale = state.Leases
            .Where(lease => lease == null || LeaseActivityUtc(lease) <= cutoff)
            .ToList();
        bool restorationBlocked = false;
        List<TestLease> releasable = new();
        foreach (TestLease lease in stale)
        {
            if (lease == null || TryRestoreViewportForLeaseLocked(lease.Id))
                releasable.Add(lease);
            else
                restorationBlocked = true;
        }
        state.Leases.RemoveAll(lease => releasable.Contains(lease));
        if (state.Leases.Count != before || restorationBlocked)
        {
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }
    }

}
