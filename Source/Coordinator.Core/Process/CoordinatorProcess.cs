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
    private bool TryAcquireLaunchOwnerLocked(string owner, string requestKey, bool resetBudget)
    {
        bool pending = state.RestartPending || state.Phase == BridgePhase.RESTARTING ||
            state.Phase == BridgePhase.LOADING || state.Phase == BridgePhase.DRAINING;
        if (pending && !string.IsNullOrWhiteSpace(state.LaunchOwner))
        {
            if (string.Equals(state.LaunchOwner, owner, StringComparison.Ordinal) &&
                string.Equals(state.LaunchRequestKey, requestKey, StringComparison.Ordinal))
                return true;
            return false;
        }

        if (resetBudget)
        {
            state.LaunchAttemptCount = 0;
            state.LaunchBudgetRemaining = Math.Max(1, options.MaxLaunchAttempts);
        }

        if (state.LaunchBudgetRemaining <= 0)
        {
            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
            state.ErrorCode = "LAUNCH_BUDGET_EXHAUSTED";
            state.Error = "The finite RimWorld launch budget is exhausted; no further launch was attempted.";
            state.LaunchOwner = null;
            state.LaunchRequestKey = null;
            state.WaitingForBridgeDeadlineUtc = null;
            SaveStateLocked();
            Monitor.PulseAll(gate);
            return false;
        }

        state.LaunchOwner = owner;
        state.LaunchRequestKey = requestKey;
        SaveStateLocked();
        return true;
    }

    private bool StartInitialLaunchLocked(string owner = null)
    {
        if (ShutdownRequested || (launchTask != null && !launchTask.IsCompleted))
            return true;
        if (ExternalMutationBlocksLaunchLocked(null, "Initial launch"))
            return false;

        owner ??= "coordinator@" + runtimeSlotId;
        int target = Math.Max(1, state.Generation + 1);
        ModProfile profile;
        try
        {
            PruneProjectIntentsLocked();
            profile = ResolveAggregateProfile(AggregateAliasesLocked(new RestartArguments()));
            EnsureAggregateBaselineLocked(profile.BaselineFingerprint);
            ModProfileResolver.ValidateResolvedProfile(profile);
        }
        catch (ProfileException exception)
        {
            RecordProfileErrorLocked(exception.Code, exception.Message);
            state.ErrorCode = exception.Code;
            state.Error = exception.Message;
            state.Phase = BridgePhase.ERROR;
            SaveStateLocked();
            return false;
        }

        if (!TryAcquireLaunchOwnerLocked(owner, "initial-" + target, resetBudget: true))
            return false;

        state.LaunchProfileMode = profile.Mode == ModProfile.BaselineMode
            ? "aggregate-minimal-control" : "aggregate-projects";
        SetActiveProfileLocked(profile);
        List<ProjectIntentRegistration> registrations = ActiveProjectIntentsLocked();
        state.TargetGeneration = target;
        state.RestartPending = true;
        state.RestartRequestedUtc = clock.UtcNow;
        state.Phase = BridgePhase.RESTARTING;
        state.Error = null;
        state.ErrorCode = null;
        state.MaintenanceReady = false;
        state.LaunchId = null;
        state.LaunchGeneration = target;
        state.ProcessId = 0;
        state.ProcessStartUtcTicks = 0;
        state.OwnedProcessExecutablePath = null;
        state.LaunchProfileFingerprint = null;
        state.LaunchProfileInstalled = false;
        state.LaunchAttemptStarted = false;
        state.RequiresNewProcess = true;
        state.WaitingForBridgeDeadlineUtc = null;
        DeleteReadinessLocked();
        DeleteQuicktestFailureArtifactLocked();
        FreezeAggregateLocked(profile, registrations, target, owner, "initial-" + target);
        launchTask = Task.Run(() =>
        {
            if (ShutdownRequested)
                return;
            lock (lifecycleGate)
            {
                if (ShutdownRequested)
                    return;
                LaunchGenerationWorker(target, isRestart: false, owner: owner);
            }
        });
        return true;
    }

    private void StartRestartWorkerLocked(int targetGeneration, string owner = null)
    {
        if (ShutdownRequested || (restartTask != null && !restartTask.IsCompleted))
            return;
        if (ExternalMutationBlocksLaunchLocked(null, "Restart"))
            return;

        owner ??= state.LaunchOwner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            FailLaunch("the accepted restart has no durable launch owner; no launch was attempted",
                "LAUNCH_OWNER_MISSING");
            return;
        }
        if (state.LaunchBudgetRemaining <= 0)
        {
            FailLaunch("the finite launch budget is exhausted", "LAUNCH_BUDGET_EXHAUSTED");
            return;
        }
        if (!string.Equals(state.LaunchOwner, owner, StringComparison.Ordinal))
            return;

        restartTask = Task.Run(() => RestartWorker(targetGeneration, owner));
    }

    private void StartMonitorLaunchLocked(int targetGeneration)
    {
        if (ShutdownRequested || (launchTask != null && !launchTask.IsCompleted))
            return;

        launchTask = Task.Run(() => MonitorLaunchWorker(targetGeneration));
    }

    private void RestartWorker(int targetGeneration, string owner)
    {
        try
        {
            int oldProcessId;
            long oldStartTicks;
            bool allowCachedOwnershipProof;
            // Process-control operations remain serialized. The gate is intentionally
            // not taken by status, doctor, wait-ready, or lease cleanup, so those
            // commands remain responsive while this worker waits on a lease.
            lock (lifecycleGate)
            {
                while (true)
                {
                    if (ShutdownRequested)
                        return;
                    lock (gate)
                    {
                        PruneStaleLeasesLocked();
                        if (!state.RestartPending || state.TargetGeneration != targetGeneration)
                            return;
                        if (ExternalMutationBlocksLaunchLocked(null, "Restart"))
                            return;

                        if (launchTask != null && !launchTask.IsCompleted)
                        {
                            Monitor.Wait(gate, 1000);
                            continue;
                        }

                        bool ownedProcessRunning = false;
                        if (state.ProcessId > 0)
                        {
                            bool cachedOwnershipProof = HasCachedOwnedProcessPathProofLocked(
                                state.ProcessId, state.ProcessStartUtcTicks);
                            if (cachedOwnershipProof)
                            {
                                ProcessOwnershipObservation observation =
                                    InspectOwnedProcessForLifecycle(state.ProcessId,
                                        state.ProcessStartUtcTicks, allowCachedPathProof: true);
                                if (observation.Classification is
                                    ProcessOwnershipClassification.IdentityMismatch or
                                    ProcessOwnershipClassification.InspectionUnavailable)
                                    throw ProcessInspection.Failure(observation.Stage);
                                ownedProcessRunning = observation.Classification ==
                                    ProcessOwnershipClassification.OwnedRunning;
                            }
                            else
                            {
                                // Legacy/unmigrated state has no durable static
                                // install proof, so retain the original strict,
                                // bounded full-identity preflight.
                                ownedProcessRunning = IsOwnedProcess(state.ProcessId,
                                    state.ProcessStartUtcTicks);
                            }
                        }
                        if (state.Leases.Count > 0 && ownedProcessRunning)
                        {
                            if (state.Phase != BridgePhase.WAITING_FOR_BRIDGE)
                            {
                                state.Phase = BridgePhase.WAITING_FOR_BRIDGE;
                                SaveStateLocked();
                            }
                            Monitor.Wait(gate, 1000);
                            continue;
                        }

                        if (state.Phase == BridgePhase.WAITING_FOR_BRIDGE)
                        {
                            state.Phase = BridgePhase.DRAINING;
                            SaveStateLocked();
                        }

                        state.Phase = BridgePhase.RESTARTING;
                        state.Error = null;
                        state.ErrorCode = null;
                        state.MaintenanceReady = false;
                        oldProcessId = state.ProcessId;
                        oldStartTicks = state.ProcessStartUtcTicks;
                        allowCachedOwnershipProof = HasCachedOwnedProcessPathProofLocked(
                            oldProcessId, oldStartTicks);
                        DeleteReadinessLocked();
                        DeleteQuicktestFailureArtifactLocked();
                        SaveStateLocked();
                        break;
                    }
                }

                lock (gate)
                {
                    if (!state.RestartPending || state.TargetGeneration != targetGeneration)
                        return;
                }

                ThrowIfShutdownRequested();
                (bool stopped, string stopErrorCode, string stopError) = StopOwnedProcess(
                    oldProcessId, oldStartTicks, allowCachedOwnershipProof);
                ThrowIfShutdownRequested();
                if (!stopped)
                {
                    FailLaunch(stopError, stopErrorCode);
                    return;
                }
                LaunchGenerationWorker(targetGeneration, isRestart: true, owner: owner);
            }
        }
        catch (OperationCanceledException) when (ShutdownRequested)
        {
            // Keep the durable restart/launch intent intact. A later
            // coordinator instance will resume it; shutdown never turns a
            // cancellation into a launch failure or stops RimWorld.
        }
        catch (Exception exception)
        {
            FailLaunch(exception is ProcessInspectionException ? ProcessInspection.Message :
                "restart coordinator failure: " + exception.Message,
                exception is ProcessInspectionException ? ProcessInspection.ErrorCode : "LAUNCH_FAILED");
        }
    }

    private void LaunchGenerationWorker(int targetGeneration, bool isRestart, string owner = null,
        ModProfile isolationProfile = null, string isolationAttemptId = null)
    {
        Interlocked.Increment(ref launchInvocationInProgress);
        string launchId = Guid.NewGuid().ToString("N");
        IManagedProcess process = null;
        bool isolationAttempt = !string.IsNullOrWhiteSpace(isolationAttemptId);
        long launchStarted = 0;
        try
        {
            ThrowIfShutdownRequested();
            owner ??= "coordinator@" + runtimeSlotId;
            lock (gate)
            {
                if (!string.Equals(state.LaunchOwner, owner, StringComparison.Ordinal))
                    return;
                if (ExternalMutationBlocksLaunchLocked(null, "Launch"))
                    return;
                if (isolationAttempt)
                {
                    CrashIsolationIncident incident = state.CrashIsolation;
                    if (incident == null ||
                        !string.Equals(incident.CurrentAttemptId, isolationAttemptId,
                            StringComparison.Ordinal) ||
                        !string.Equals(state.LaunchRequestKey, isolationAttemptId, StringComparison.Ordinal) ||
                        isolationProfile == null ||
                        !string.Equals(incident.CurrentAttemptFingerprint, isolationProfile.ProfileFingerprint,
                            StringComparison.Ordinal) ||
                        incident.CurrentAttemptProfile == null ||
                        !string.Equals(incident.CurrentAttemptProfile.ProfileFingerprint,
                            isolationProfile.ProfileFingerprint, StringComparison.Ordinal) ||
                        !string.Equals(state.LaunchProfileFingerprint,
                            isolationProfile.ProfileFingerprint, StringComparison.Ordinal) ||
                        incident.CurrentAttemptResult != null ||
                        state.LaunchAttemptStarted || state.Phase == BridgePhase.LOADING ||
                        state.IsolationLaunchesRemaining <= 0 ||
                        incident.IsolationLaunchesRemaining <= 0)
                    {
                        return;
                    }
                    int remaining = Math.Min(state.IsolationLaunchesRemaining,
                        incident.IsolationLaunchesRemaining);
                    if (remaining <= 0)
                        return;
                    state.IsolationLaunchesRemaining = remaining - 1;
                    incident.IsolationLaunchesRemaining = remaining - 1;
                }
                else if (state.LaunchBudgetRemaining <= 0)
                {
                    FailLaunch("the finite launch budget is exhausted", "LAUNCH_BUDGET_EXHAUSTED");
                    return;
                }
                state.Phase = BridgePhase.LOADING;
                state.TargetGeneration = targetGeneration;
                state.LaunchId = launchId;
                state.LaunchGeneration = targetGeneration;
                state.LaunchStartedUtc = clock.UtcNow;
                // A failed raw launch must not inherit the previous generation's
                // identity.  Recovery may only attribute a process identity that
                // was durably recorded for this launch intent.
                state.ProcessId = 0;
                state.ProcessStartUtcTicks = 0;
                state.OwnedProcessExecutablePath = null;
                state.Error = null;
                state.ErrorCode = null;
                state.TerminalFailureSchemaVersion = 0;
                state.TerminalFailurePhase = null;
                state.TerminalFailureCode = null;
                state.TerminalFailureDetail = null;
                state.TerminalFailureExceptionType = null;
                state.TerminalFailureExceptionMessage = null;
                state.TerminalFailureDiagnosticDetail = null;
                state.MaintenanceReady = false;
                state.LaunchProfileInstalled = false;
                state.LaunchAttemptStarted = false;
                state.LaunchProfileFingerprint = isolationProfile?.ProfileFingerprint ??
                    (state.ProfileMode == ModProfile.LegacyMode ? null : state.ProfileFingerprint);
                BeginRimBridgeLaunchLocked(launchId, targetGeneration, isolationProfile);
                DeleteReadinessLocked();
                DeleteQuicktestFailureArtifactLocked();
                SaveStateLocked();
            }

            if (!File.Exists(rimWorldExe))
                throw new FileNotFoundException("RimWorld executable was not found", rimWorldExe);

            lock (gate)
            {
                // Check the census before changing ModsConfig. lifecycleGate serializes this
                // launch with coordinator-owned lifecycle operations, so an unmanaged process
                // fails closed without leaving a generated profile behind.
                List<UnmanagedRimWorldProcess> unmanagedProcesses =
                    FindUnmanagedRimWorldProcesses(processIdToExclude: 0, startTicksToExclude: 0);
                if (unmanagedProcesses.Count > 0)
                    throw new InvalidOperationException("an unmanaged RimWorld process is already running (PID " +
                        string.Join(", ", unmanagedProcesses.Select(value => value.ProcessId.ToString())) +
                        "); close it through Steam before retrying");
            }

            RimBridgeLogBoundary rimBridgeBoundary;
            lock (gate)
            {
                // This snapshot only establishes the pre-launch exclusion point. RimWorld
                // may reset Player.log while it initializes; the authoritative forensic
                // boundary is captured later, at the READY transition.
                rimBridgeBoundary = CaptureLogBoundaryLocked(authoritative: false);
                if (!rimBridgeBoundary.Available)
                {
                    state.RimBridge.ErrorCode = RimBridgeIntegrationConstants.EndpointNotFoundCode;
                    state.RimBridge.Error = rimBridgeBoundary.Error;
                    state.RimBridge.LifecycleState = RimBridgeLifecycleState.FAILED;
                }
                SaveStateLocked();
            }
            if (!rimBridgeBoundary.Available && options.RimBridgeMode == RimBridgeMode.Required)
                throw new RimBridgeIntegrationException(RimBridgeIntegrationConstants.EndpointNotFoundCode,
                    rimBridgeBoundary.Error ?? "Player.log boundary could not be captured.");

            ModProfile profile;
            lock (gate)
            {
                // Legacy launches use the user's existing ModsConfig.  A
                // baseline/runtime snapshot is not an implicit legacy profile.
                if (isolationProfile != null)
                    profile = isolationProfile;
                else if (state.ProfileMode == ModProfile.LegacyMode)
                    profile = null;
                else
                {
                    // The accepted profile is authoritative. RuntimeProfile is
                    // a reporting/recovery snapshot and may be stale after a
                    // coordinator crash; never launch a different profile.
                    ModProfile acceptedProfile = ProfileFromStateLocked();
                    ModProfile runtimeProfile = state.RuntimeProfile?.ToModProfile();
                    profile = runtimeProfile != null && acceptedProfile != null &&
                        string.Equals(runtimeProfile.ProfileFingerprint,
                            acceptedProfile.ProfileFingerprint, StringComparison.Ordinal)
                        ? runtimeProfile
                        : acceptedProfile;
                }
            }
            if (profile == null && options.RimBridgeMode == RimBridgeMode.Required)
                throw new RimBridgeIntegrationException("RIMBRIDGE_REQUIRED_PROFILE",
                    "RimBridge mode is required, but the launch is using an unprofiled legacy ModsConfig.");
            lock (gate)
                SetRimBridgeProfileStateLocked(profile);
            if (profile == null)
            {
                EnsureDevBridgeModEnabled();
                lock (gate)
                {
                    // Baseline capture/restore intentionally keeps a control
                    // snapshot for isolation, but it must not be reported as
                    // the profile used by a subsequent ordinary legacy launch.
                    if (!isolationAttempt && state.ProfileMode == ModProfile.LegacyMode &&
                        state.RuntimeProfile != null)
                    {
                        state.RuntimeProfile = null;
                        SaveStateLocked();
                    }
                }
            }
            else
            {
                ApplyProfile(profile, targetGeneration);
                lock (gate)
                {
                    state.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(profile);
                    state.LaunchProfileInstalled = true;
                    SaveStateLocked();
                }
            }

            TestInputSet launchInputs;
            lock (gate)
            {
                IEnumerable<TestInputValue> values = profile?.TestInputs ?? state.RuntimeProfile?.TestInputs ??
                    state.TestInputs;
                string mode = profile?.Mode ?? state.ProfileMode ?? ModProfile.LegacyMode;
                launchInputs = TestGenerationInputs.FromValues(values, mode);
            }

            // A process may have appeared after the pre-write census. Check again at the
            // launch boundary so an external RimWorld start cannot become a duplicate launch.
            ThrowIfShutdownRequested();
            EnsureNoMatchingRimWorldProcess(isRestart);
            lock (gate)
            {
                // Profile application is complete immediately before the only raw launch call.
                if (!isolationAttempt)
                {
                    state.LaunchAttemptCount++;
                    state.LaunchBudgetRemaining--;
                }
                state.LaunchAttemptStarted = true;
                SaveStateLocked();
            }

            InjectFaultForTesting(CoordinatorFaultPoint.AfterStatePersistedBeforeExternalProcessAction);
            launchStarted = Stopwatch.GetTimestamp();
            TraceEvent("process.launch.initiated", detail: isRestart ? "restart" : "initial");
            Dictionary<string, string> launchEnvironment = new()
            {
                ["DEVBRIDGE_ROOT"] = root,
                ["DEVBRIDGE_INSTALLATION_ID"] = state.InstallationId ?? string.Empty,
                ["DEVBRIDGE_RUNTIME_SLOT_ID"] = runtimeSlotId,
                ["DEVBRIDGE_LAUNCH_ID"] = launchId,
                ["DEVBRIDGE_GENERATION"] = targetGeneration.ToString(),
                ["DEVBRIDGE_QUICKTEST_REQUESTED"] = launchInputs.QuicktestEnabled ? "1" : "0",
                ["DEVBRIDGE_QUICKTEST_TIMEOUT_SECONDS"] =
                    launchInputs.QuicktestTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                ["DEVBRIDGE_PROFILE_FINGERPRINT"] = state.LaunchProfileFingerprint ?? string.Empty,
                ["DEVBRIDGE_BASELINE_FINGERPRINT"] = state.BaselineFingerprint ?? string.Empty,
                ["DEVBRIDGE_PROFILE_MODE"] = isolationProfile?.Mode ?? state.ProfileMode ?? string.Empty
            };
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEVBRIDGE_TEST_RIMWORLD_PATH")))
            {
                string signalPath = Path.Combine(root, "Runtime", "fake-rimworld-stop.request");
                launchEnvironment["DEVBRIDGE_TEST_GRACEFUL_STOP_SIGNAL"] = signalPath;
                Environment.SetEnvironmentVariable("DEVBRIDGE_TEST_GRACEFUL_STOP_SIGNAL", signalPath);
            }
            process = processAdapter.Launch(new ProcessLaunchRequest
            {
                FileName = rimWorldExe,
                WorkingDirectory = Path.GetDirectoryName(rimWorldExe) ?? root,
                Arguments = Array.Empty<string>(),
                Environment = launchEnvironment
            });
            if (process == null)
                throw new InvalidOperationException("launch adapter returned no RimWorld process");

            int processId = process.Id;
            long processStartTicks = process.StartIdentity;
            if (processStartTicks <= 0)
                throw new InvalidOperationException("launch adapter did not provide a process-start identity");
            InjectFaultForTesting(CoordinatorFaultPoint.AfterProcessActionBeforeResultingStatePersistence);
            lock (gate)
            {
                state.ProcessId = processId;
                state.ProcessStartUtcTicks = processStartTicks;
                state.RimBridge.ProcessId = processId;
                state.RimBridge.ProcessStartUtcTicks = processStartTicks;
                SaveStateLocked();
                Monitor.PulseAll(gate);
            }
            TraceEvent("process.launch.identity.accepted",
                durationMs: ElapsedMilliseconds(launchStarted), success: true);

            MonitorLaunchUntilReady(process, processId, processStartTicks, launchId, targetGeneration);
        }
        catch (OperationCanceledException) when (ShutdownRequested)
        {
            // LOADING plus the persisted launch identity is intentionally left
            // recoverable. Do not call FailLaunch and do not terminate a process
            // merely because this coordinator instance is exiting.
            lock (gate)
            {
                if (state.Phase == BridgePhase.LOADING && state.ProcessId <= 0)
                {
                    state.Phase = BridgePhase.RESTARTING;
                    SaveStateLocked();
                    Monitor.PulseAll(gate);
                }
            }
        }
        catch (Exception exception)
        {
            string errorCode = LaunchFailureCode(exception, process);
            if (launchStarted > 0)
                TraceEvent("process.launch.failed",
                    durationMs: ElapsedMilliseconds(launchStarted), success: false,
                    errorCode: errorCode, detail: TraceExceptionCategory(exception));
            string detail = DescribeLaunchFailure(exception, process);
            FailLaunch(detail, errorCode);
        }
        finally
        {
            process?.Dispose();
            Interlocked.Decrement(ref launchInvocationInProgress);
        }
    }

    private RimBridgeLogBoundary CaptureLogBoundaryLocked(bool authoritative) =>
        CaptureLogBoundaryLocked(authoritative, out _);

    private RimBridgeLogBoundary CaptureLogBoundaryLocked(bool authoritative, out bool integrityInvalid)
    {
        integrityInvalid = false;
        state.RimBridge ??= RimBridgeIntegrationState.Disabled(options.RimBridgeMode);
        RimBridgeLogBoundary current = RimBridgeLogDiscovery.CaptureBoundary(rimBridgeLogPath, clock.UtcNow);
        if (!authoritative)
        {
            StoreLogBoundaryLocked(current, authoritative: false);
            return current;
        }

        bool provisionalExisted = state.RimBridge.LogExistedAtBoundary;
        RimBridgeLogBoundary provisional = new()
        {
            Path = rimBridgeLogPath,
            Available = true,
            Existed = provisionalExisted,
            Length = Math.Max(0, state.RimBridge.LogBoundaryPosition),
            PrefixLength = state.RimBridge.LogBoundaryPrefixLength > 0
                ? state.RimBridge.LogBoundaryPrefixLength
                : Math.Min(Math.Max(0, state.RimBridge.LogBoundaryPosition), 64 * 1024),
            CreationUtcTicks = state.RimBridge.LogBoundaryCreationUtcTicks,
            PrefixHash = state.RimBridge.LogBoundaryPrefixHash,
            CapturedUtc = state.RimBridge.LogBoundaryTimestampUtc ?? state.LaunchStartedUtc
        };

        bool expectedStartupReset = false;
        if (provisionalExisted)
        {
            if (RimBridgeLogDiscovery.BoundaryChanged(provisional))
            {
                expectedStartupReset = current.Existed &&
                    RimBridgeLogDiscovery.HasRimWorldStartupMarker(rimBridgeLogPath);
                integrityInvalid = !expectedStartupReset;
            }
        }
        else if (current.Existed)
        {
            bool createdDuringLaunch = state.LaunchStartedUtc == default ||
                current.CreationUtcTicks <= 0 ||
                current.CreationUtcTicks > state.LaunchStartedUtc.ToUniversalTime().Ticks;
            expectedStartupReset = createdDuringLaunch ||
                RimBridgeLogDiscovery.HasRimWorldStartupMarker(rimBridgeLogPath);
            integrityInvalid = !expectedStartupReset;
        }

        RimBridgeLogBoundary effective = expectedStartupReset
            ? new RimBridgeLogBoundary
            {
                Path = current.Path,
                Available = current.Available,
                Existed = current.Existed,
                Length = 0,
                PrefixLength = current.PrefixLength,
                CreationUtcTicks = current.CreationUtcTicks,
                PrefixHash = current.PrefixHash,
                CapturedUtc = current.CapturedUtc,
                Error = current.Error
            }
            : integrityInvalid || !provisionalExisted
                ? current
                : new RimBridgeLogBoundary
                {
                    Path = current.Path,
                    Available = current.Available,
                    Existed = provisional.Existed,
                    Length = provisional.Length,
                    PrefixLength = provisional.PrefixLength,
                    CreationUtcTicks = provisional.CreationUtcTicks,
                    PrefixHash = provisional.PrefixHash,
                    CapturedUtc = current.CapturedUtc,
                    Error = current.Error
                };

        StoreLogBoundaryLocked(effective,
            authoritative: !integrityInvalid && effective.Available && effective.Existed);
        if (integrityInvalid)
        {
            state.RimBridge.ErrorCode = RimBridgeIntegrationConstants.PlayerLogBoundaryInvalidCode;
            state.RimBridge.Error = "Player.log changed unexpectedly before the authoritative startup boundary.";
        }
        return effective;
    }

    private void StoreLogBoundaryLocked(RimBridgeLogBoundary boundary, bool authoritative)
    {
        state.RimBridge.LogBoundaryTimestampUtc = boundary.CapturedUtc;
        state.RimBridge.LogBoundaryPosition = boundary.Length;
        state.RimBridge.LogExistedAtBoundary = boundary.Existed;
        state.RimBridge.LogBoundaryAuthoritative = authoritative && boundary.Available;
        state.RimBridge.LogBoundaryPrefixLength = boundary.PrefixLength;
        state.RimBridge.LogBoundaryCreationUtcTicks = boundary.CreationUtcTicks;
        state.RimBridge.LogBoundaryPrefixHash = boundary.PrefixHash;
    }

    private void MonitorLaunchWorker(int targetGeneration)
    {
        try
        {
            ThrowIfShutdownRequested();
            int processId;
            long startTicks;
            string launchId;
            lock (gate)
            {
                processId = state.ProcessId;
                startTicks = state.ProcessStartUtcTicks;
                launchId = state.LaunchId;
            }

            if (processId <= 0 || string.IsNullOrWhiteSpace(launchId))
                throw new InvalidOperationException("persisted launch information is incomplete");

            using IManagedProcess process = processAdapter.Open(processId);
            if (process == null)
            {
                QuicktestFailureRecord failure = TryReadMatchingQuicktestFailure(
                    launchId, targetGeneration, processId, startTicks,
                    state.LaunchStartedUtc.ToUniversalTime());
                if (failure != null)
                {
                    FailLaunch(DescribeQuicktestFailure(failure),
                        QuicktestFailureArtifact.StableFailureCode, failure);
                    return;
                }
                FailLaunch("RimWorld exited before the quicktest map became ready", "PROCESS_EXITED");
                return;
            }
            MonitorLaunchUntilReady(process, processId, startTicks, launchId, targetGeneration);
        }
        catch (OperationCanceledException) when (ShutdownRequested)
        {
            // The persisted LOADING state is recovered by the next coordinator.
        }
        catch (Exception exception)
        {
            FailLaunch(exception is ProcessInspectionException ? ProcessInspection.Message :
                "RimWorld did not report readiness after coordinator recovery: " + exception.Message,
                exception is ProcessInspectionException ? ProcessInspection.ErrorCode : "LAUNCH_FAILED");
        }
    }

    private void MonitorLaunchUntilReady(IManagedProcess process, int processId, long processStartTicks,
        string launchId, int targetGeneration)
    {
        DateTime deadline;
        lock (gate)
            deadline = state.LaunchStartedUtc.ToUniversalTime().Add(options.ReadinessTimeout);
        DateTime? inspectionFailureStartedUtc = null;

        while (!ShutdownRequested && clock.UtcNow < deadline)
        {
            try
            {
                if (process == null || !IsExactProcessIdentity(process, processStartTicks))
                {
                    lock (gate)
                        InvalidateRimBridgeEndpointLocked("The RimWorld process identity changed before bridge readiness.",
                            RimBridgeIntegrationConstants.ProcessMismatchCode);
                    FailLaunch("the RimWorld process identity changed before readiness", "PROCESS_IDENTITY_CHANGED");
                    return;
                }

                DateTime launchStarted = deadline - options.ReadinessTimeout;
                QuicktestFailureRecord failure = TryReadMatchingQuicktestFailure(
                    launchId, targetGeneration, processId, processStartTicks, launchStarted);
                bool readiness = IsReadinessMatch(launchId, processId, targetGeneration, launchStarted);
                string readinessErrorCode = null;
                string readinessError = null;
                lock (gate)
                {
                    if (state.ErrorCode == "READINESS_MALFORMED" ||
                        state.ErrorCode == "READINESS_SCHEMA_INVALID" ||
                        state.ErrorCode == "READINESS_SCHEMA_UNSUPPORTED")
                    {
                        readinessErrorCode = state.ErrorCode;
                        readinessError = state.Error;
                    }
                }
                if (readinessErrorCode != null)
                {
                    FailLaunch(readinessError, readinessErrorCode);
                    return;
                }
                if (failure != null && readiness)
                {
                    FailLaunch("a terminal quicktest failure and a matching readiness signal were both written; the launch is ambiguous",
                        "QUICKTEST_READINESS_CONFLICT", failure);
                    return;
                }

                if (failure != null)
                {
                    FailLaunch(DescribeQuicktestFailure(failure),
                        QuicktestFailureArtifact.StableFailureCode, failure);
                    return;
                }

                if (process.HasExited)
                {
                    lock (gate)
                        InvalidateRimBridgeEndpointLocked("RimWorld exited before bridge readiness.", "PROCESS_EXITED");
                    FailLaunch("RimWorld exited before the quicktest map became ready", "PROCESS_EXITED");
                    return;
                }

                bool bridgeReady;
                string bridgeFailureCode;
                string bridgeFailure;
                lock (gate)
                    bridgeReady = TrySatisfyRimBridgeReadinessLocked(launchId, targetGeneration,
                        processId, processStartTicks, deadline, out bridgeFailureCode, out bridgeFailure);
                if (!bridgeReady && bridgeFailureCode != null)
                {
                    FailLaunch(bridgeFailure, bridgeFailureCode);
                    return;
                }

                if (readiness && bridgeReady)
                {
                    lock (gate)
                    {
                        MarkReadyLocked(launchId, targetGeneration, processId, processStartTicks);
                    }
                    return;
                }

                inspectionFailureStartedUtc = null;
            }
            catch (ProcessInspectionException)
            {
                if (ShutdownRequested)
                    return;
                inspectionFailureStartedUtc ??= clock.UtcNow;
                TimeSpan elapsed = clock.UtcNow - inspectionFailureStartedUtc.Value;
                if (elapsed >= options.ProcessInspectionRetryTimeout)
                    throw;

                TimeSpan retryDelay = options.ProcessInspectionRetryTimeout - elapsed;
                clock.Sleep(retryDelay < TimeSpan.FromSeconds(1) ? retryDelay : TimeSpan.FromSeconds(1));
                continue;
            }

            clock.Sleep(TimeSpan.FromSeconds(1));
        }

        if (ShutdownRequested)
            return;

        if (inspectionFailureStartedUtc.HasValue)
            throw ProcessInspection.Failure();

        string finalBridgeFailureCode;
        string finalBridgeFailure;
        lock (gate)
        {
            TrySatisfyRimBridgeReadinessLocked(launchId, targetGeneration, processId,
                processStartTicks, deadline, out finalBridgeFailureCode, out finalBridgeFailure);
        }
        if (finalBridgeFailureCode != null)
        {
            FailLaunch(finalBridgeFailure, finalBridgeFailureCode);
            return;
        }

        FailLaunch("no matching readiness signal was written within " +
            options.ReadinessTimeout.TotalSeconds.ToString("0") + " seconds", "READINESS_TIMEOUT");
    }

    private bool TrySatisfyRimBridgeReadinessLocked(string launchId, int targetGeneration,
        int processId, long processStartTicks, DateTime deadline, out string terminalCode,
        out string terminalError)
    {
        terminalCode = null;
        terminalError = null;
        state.RimBridge ??= RimBridgeIntegrationState.Disabled(options.RimBridgeMode);
        if (options.RimBridgeMode == RimBridgeMode.Off)
        {
            state.RimBridge.LifecycleState = RimBridgeLifecycleState.DISABLED;
            state.RimBridge.TokenAvailable = false;
            return true;
        }

        if (!string.Equals(state.RimBridge.LaunchId, launchId, StringComparison.Ordinal) ||
            state.RimBridge.Generation != targetGeneration || state.RimBridge.ProcessId != processId ||
            state.RimBridge.ProcessStartUtcTicks != processStartTicks)
        {
            InvalidateRimBridgeEndpointLocked("RimBridge endpoint identity does not match the active launch.",
                RimBridgeIntegrationConstants.ProcessMismatchCode);
            state.RimBridge.LaunchId = launchId;
            state.RimBridge.Generation = targetGeneration;
            state.RimBridge.ProcessId = processId;
            state.RimBridge.ProcessStartUtcTicks = processStartTicks;
            terminalCode = options.RimBridgeMode == RimBridgeMode.Required
                ? RimBridgeIntegrationConstants.ProcessMismatchCode : null;
            terminalError = "RimBridge endpoint identity does not match the active RimWorld process.";
            return options.RimBridgeMode == RimBridgeMode.Optional;
        }

        RimBridgeEndpoint stored = RimBridgeEndpointStore.Load(runtimeRoot);
        if (options.RimBridgeMode == RimBridgeMode.Optional &&
            !state.RimBridge.TokenAvailable &&
            IsRimBridgeProfileResolutionFailure(state.RimBridge.ErrorCode))
        {
            state.RimBridge.LifecycleState = state.RimBridge.ErrorCode == "RIMBRIDGE_NOT_INSTALLED"
                ? RimBridgeLifecycleState.NOT_INSTALLED
                : RimBridgeLifecycleState.FAILED;
            SaveStateLocked();
            return true;
        }

        if (state.RimBridge.LifecycleState == RimBridgeLifecycleState.READY &&
            state.RimBridge.TokenAvailable && stored != null && stored.IsValid &&
            string.Equals(stored.LaunchId, launchId, StringComparison.Ordinal) &&
            stored.Generation == targetGeneration && stored.ProcessId == processId &&
            stored.ProcessStartUtcTicks == processStartTicks &&
            (state.RimBridge.CompanionVerified ||
             state.RimBridge.CompanionErrorCode == RimBridgeIntegrationConstants.CompanionUnavailableCode))
            return true;

        RimBridgeLogBoundary boundary = new()
        {
            Path = rimBridgeLogPath,
            Available = true,
            Existed = state.RimBridge.LogExistedAtBoundary,
            Length = state.RimBridge.LogBoundaryPosition,
            PrefixLength = state.RimBridge.LogBoundaryPrefixLength,
            CreationUtcTicks = state.RimBridge.LogBoundaryCreationUtcTicks,
            PrefixHash = state.RimBridge.LogBoundaryPrefixHash,
            CapturedUtc = state.RimBridge.LogBoundaryTimestampUtc ?? state.LaunchStartedUtc
        };
        RimBridgeLogDiscoveryResult discovery = RimBridgeLogDiscovery.Discover(boundary,
            launchId, targetGeneration, processId, processStartTicks, clock.UtcNow);
        state.RimBridge.DiscoveryTimestampUtc = clock.UtcNow;
        RimBridgeEndpoint endpoint = discovery.Endpoint;
        if (endpoint == null && stored != null && stored.IsValid &&
            string.Equals(stored.LaunchId, launchId, StringComparison.Ordinal) &&
            stored.Generation == targetGeneration && stored.ProcessId == processId &&
            stored.ProcessStartUtcTicks == processStartTicks)
            endpoint = stored;

        if (endpoint != null && endpoint.IsValid)
        {
            bool verified = RimBridgeEndpointVerifier.CanConnect(endpoint,
                TimeSpan.FromMilliseconds(250));
            state.RimBridge.Host = endpoint.Host;
            state.RimBridge.Port = endpoint.Port;
            state.RimBridge.TokenAvailable = true;
            state.RimBridge.LastVerificationTimestampUtc = clock.UtcNow;
            if (verified)
            {
                RimBridgeCompanionVerification companion = RimBridgeCompanionClient.Verify(
                    endpoint, launchId, targetGeneration, processId,
                    TimeSpan.FromMilliseconds(500));
                state.RimBridge.CompanionToolName = RimBridgeIntegrationConstants.CompanionToolName;
                state.RimBridge.CompanionAvailable = companion.Status !=
                    RimBridgeCompanionVerificationStatus.Unavailable;
                state.RimBridge.CompanionVerified = companion.Status ==
                    RimBridgeCompanionVerificationStatus.Match;
                state.RimBridge.CompanionLaunchId = companion.LaunchId;
                state.RimBridge.CompanionGeneration = companion.Generation;
                state.RimBridge.CompanionProcessId = companion.ProcessId;
                state.RimBridge.CompanionVerificationTimestampUtc = clock.UtcNow;
                state.RimBridge.CompanionErrorCode = companion.Code;
                state.RimBridge.CompanionError = companion.Error;
                state.RimBridge.CompanionDiagnosticCode = companion.DiagnosticCode;
                state.RimBridge.CompanionDiagnosticReason = companion.DiagnosticReason;

                if (companion.Status == RimBridgeCompanionVerificationStatus.Mismatch ||
                    companion.Status == RimBridgeCompanionVerificationStatus.Invalid)
                {
                    RimBridgeEndpointStore.Delete(runtimeRoot);
                    state.RimBridge.TokenAvailable = false;
                    state.RimBridge.LifecycleState = RimBridgeLifecycleState.FAILED;
                    state.RimBridge.ErrorCode = companion.Code;
                    state.RimBridge.Error = companion.Error;
                    if (options.RimBridgeMode == RimBridgeMode.Required)
                    {
                        terminalCode = companion.Code;
                        terminalError = companion.Error;
                    }
                    SaveStateLocked();
                    return options.RimBridgeMode == RimBridgeMode.Optional;
                }

                RimBridgeEndpointStore.Save(runtimeRoot, endpoint);
                state.RimBridge.LifecycleState = RimBridgeLifecycleState.READY;
                state.RimBridge.ErrorCode = null;
                state.RimBridge.Error = null;
                SaveStateLocked();
                return true;
            }

            state.RimBridge.LifecycleState = RimBridgeLifecycleState.DISCOVERED;
            state.RimBridge.ErrorCode = RimBridgeIntegrationConstants.EndpointNotFoundCode;
            state.RimBridge.Error = "RimBridgeServer logged an endpoint, but loopback verification did not connect.";
        }
        else
        {
            state.RimBridge.TokenAvailable = false;
            state.RimBridge.LifecycleState = RimBridgeLifecycleState.WAITING;
            state.RimBridge.ErrorCode = discovery.ErrorCode;
            state.RimBridge.Error = discovery.Error;
            if (discovery.BoundaryInvalid)
            {
                state.RimBridge.LifecycleState = RimBridgeLifecycleState.STALE;
                if (options.RimBridgeMode == RimBridgeMode.Required)
                {
                    terminalCode = RimBridgeIntegrationConstants.EndpointNotFoundCode;
                    terminalError = discovery.Error;
                }
            }
        }

        if (discovery.StartupFailed)
        {
            state.RimBridge.LifecycleState = RimBridgeLifecycleState.FAILED;
            state.RimBridge.ErrorCode = RimBridgeIntegrationConstants.StartupFailedCode;
            state.RimBridge.Error = discovery.Error;
            if (options.RimBridgeMode == RimBridgeMode.Required)
            {
                terminalCode = RimBridgeIntegrationConstants.StartupFailedCode;
                terminalError = discovery.Error;
            }
        }

        if (clock.UtcNow >= deadline && options.RimBridgeMode == RimBridgeMode.Required && terminalCode == null)
        {
            terminalCode = discovery.ErrorCode == RimBridgeIntegrationConstants.AuthFailedCode
                ? RimBridgeIntegrationConstants.AuthFailedCode
                : RimBridgeIntegrationConstants.StartupTimeoutCode;
            terminalError = state.RimBridge.Error ??
                "RimBridgeServer did not publish a verified endpoint before the bounded readiness deadline.";
            state.RimBridge.LifecycleState = RimBridgeLifecycleState.FAILED;
            state.RimBridge.ErrorCode = terminalCode;
            state.RimBridge.Error = terminalError;
        }

        SaveStateLocked();
        return options.RimBridgeMode == RimBridgeMode.Optional;
    }

    private static bool IsRimBridgeProfileResolutionFailure(string code) =>
        string.Equals(code, "RIMBRIDGE_NOT_INSTALLED", StringComparison.Ordinal) ||
        string.Equals(code, "RIMBRIDGE_AMBIGUOUS_PACKAGE", StringComparison.Ordinal) ||
        string.Equals(code, "RIMBRIDGE_MALFORMED_METADATA", StringComparison.Ordinal);

    private QuicktestFailureRecord TryReadMatchingQuicktestFailure(
        string launchId, int targetGeneration, int processId, long processStartTicks,
        DateTime launchStartedUtc)
    {
        try
        {
            if (!File.Exists(quicktestFailurePath))
                return null;

            FileInfo file = new(quicktestFailurePath);
            if (file.Length <= 0 || file.Length > 16 * 1024)
                return RejectQuicktestFailureArtifact();

            QuicktestFailureRecord record = JsonSerializer.Deserialize<QuicktestFailureRecord>(
                File.ReadAllText(quicktestFailurePath), CoordinatorSerialization.JsonOptions);
            string rejection = ValidateQuicktestFailure(record, launchId, targetGeneration,
                processId, processStartTicks, launchStartedUtc);
            if (rejection != null)
                return RejectQuicktestFailureArtifact();
            return record;
        }
        catch
        {
            return RejectQuicktestFailureArtifact();
        }
    }

    private QuicktestFailureRecord RejectQuicktestFailureArtifact()
    {
        QuicktestFailureArtifact.TryQuarantine(root, out _);
        return null;
    }

    private string ValidateQuicktestFailure(QuicktestFailureRecord record, string launchId,
        int targetGeneration, int processId, long processStartTicks, DateTime launchStartedUtc)
    {
        if (record == null || record.SchemaVersion != QuicktestFailureArtifact.CurrentSchemaVersion)
            return "schema";
        if (record.LaunchId == null || record.LaunchId.Length == 0 ||
            record.LaunchId.Length > QuicktestFailureArtifact.MaxLaunchIdLength ||
            !string.Equals(record.LaunchId, launchId, StringComparison.Ordinal))
            return "launch";
        if ((record.ProfileFingerprint?.Length ?? 0) > QuicktestFailureArtifact.MaxFingerprintLength ||
            (record.BaselineFingerprint?.Length ?? 0) > QuicktestFailureArtifact.MaxFingerprintLength ||
            (record.ProfileMode?.Length ?? 0) > QuicktestFailureArtifact.MaxProfileModeLength)
            return "profile-bounds";
        if (record.Generation != targetGeneration || record.Generation <= 0 ||
            record.ProcessId != processId || record.ProcessId <= 0 ||
            record.ProcessStartUtcTicks != processStartTicks || processStartTicks <= 0)
            return "identity";

        string expectedFingerprint;
        string expectedBaseline;
        string expectedProfileMode;
        lock (gate)
        {
            expectedFingerprint = state.LaunchProfileFingerprint ??
                (state.ProfileMode == ModProfile.LegacyMode ? null : state.ProfileFingerprint);
            expectedBaseline = state.BaselineFingerprint;
            expectedProfileMode = state.CrashIsolation?.CurrentAttemptProfile?.Mode ?? state.ProfileMode;
        }

        if (!NullableStringEquals(record.ProfileFingerprint, expectedFingerprint) ||
            !NullableStringEquals(record.BaselineFingerprint, expectedBaseline) ||
            !NullableStringEquals(record.ProfileMode, expectedProfileMode))
            return "profile";
        if (record.TimestampUtc == default || record.TimestampUtc.Kind != DateTimeKind.Utc)
            return "timestamp";

        DateTime timestamp = record.TimestampUtc.ToUniversalTime();
        DateTime now = clock.UtcNow.ToUniversalTime();
        if (timestamp < launchStartedUtc.ToUniversalTime().AddSeconds(-2) ||
            timestamp > now.AddSeconds(2))
            return "timestamp";
        if (record.FailurePhase == null || record.FailurePhase.Length == 0 ||
            record.FailurePhase.Length > QuicktestFailureArtifact.MaxPhaseLength ||
            !string.Equals(record.FailureCode, QuicktestFailureArtifact.StableFailureCode,
                StringComparison.Ordinal))
            return "failure";
        if (record.FailureCode.Length > QuicktestFailureArtifact.MaxCodeLength ||
            string.IsNullOrWhiteSpace(record.ExceptionType) ||
            (record.ExceptionType?.Length ?? 0) > QuicktestFailureArtifact.MaxExceptionTypeLength ||
            (record.ExceptionMessage?.Length ?? 0) > QuicktestFailureArtifact.MaxExceptionMessageLength ||
            string.IsNullOrWhiteSpace(record.DiagnosticDetail) ||
            (record.DiagnosticDetail?.Length ?? 0) > QuicktestFailureArtifact.MaxDiagnosticDetailLength)
            return "bounds";
        return null;
    }

    private static bool NullableStringEquals(string left, string right)
    {
        return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
    }

    private static string DescribeQuicktestFailure(QuicktestFailureRecord failure)
    {
        string phase = string.IsNullOrWhiteSpace(failure.FailurePhase) ? "unknown phase" : failure.FailurePhase;
        string type = string.IsNullOrWhiteSpace(failure.ExceptionType) ? "unknown exception" : failure.ExceptionType;
        string message = string.IsNullOrWhiteSpace(failure.ExceptionMessage) ?
            "no exception message" : failure.ExceptionMessage;
        string detail = string.IsNullOrWhiteSpace(failure.DiagnosticDetail) ?
            type + ": " + message : failure.DiagnosticDetail;
        return "terminal quicktest failure during " + phase + ": " +
            QuicktestFailureArtifact.Bounded(detail, QuicktestFailureArtifact.MaxDiagnosticDetailLength);
    }

    private static string LaunchFailureCode(Exception exception, IManagedProcess process)
    {
        if (exception is TimeoutException)
            return "READINESS_TIMEOUT";
        if (exception is ProcessInspectionException)
            return ProcessInspection.ErrorCode;
        if (exception is RimBridgeIntegrationException rimBridgeException)
            return rimBridgeException.Code;
        if (exception is ProfileException profileException)
            return profileException.Code;
        try
        {
            if (process != null && process.HasExited)
                return "PROCESS_EXITED";
        }
        catch
        {
            return ProcessInspection.ErrorCode;
        }
        return "LAUNCH_FAILED";
    }

}
