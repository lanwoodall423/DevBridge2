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
    private int BeginLease(BridgeRequest request, Action<string> emit, Func<bool> connected,
        Action<TestLease> acquired = null, Func<bool> budgetAvailable = null)
    {
        emit("Agent/session: " + request.Agent);
        bool startInitialLaunch;
        lock (gate)
        {
            SynchronizeLocked();
            bool recoverableFailedProcess = state.Phase == BridgePhase.ERROR &&
                state.ProcessId > 0 && state.ProcessStartUtcTicks > 0 &&
                state.ErrorCode != ProcessInspection.ErrorCode;
            if (state.Phase == BridgePhase.ERROR && !recoverableFailedProcess &&
                !IsConfirmedMaintenanceWindowLocked())
            {
                emit("RimWorld is in ERROR state: " + state.Error);
                EmitNextCommand(emit, "DevBridge.cmd doctor");
                return 4;
            }
            startInitialLaunch = state.Phase == BridgePhase.STOPPED &&
                state.Generation == 0 && !state.RestartPending;
        }

        if (startInitialLaunch)
        {
            lock (lifecycleGate)
            {
                lock (gate)
                {
                    SynchronizeLocked();
                    if (state.Phase == BridgePhase.STOPPED && state.Generation == 0 && !state.RestartPending)
                    {
                        emit("No ready RimWorld generation is running.");
                        emit("DevBridge is launching RimWorld normally, then requesting built-in Dev Quicktest.");
                        StartInitialLaunchLocked(LaunchOwnerFor(request));
                    }
                }
            }
        }

        TestLease maintenanceLease = null;
        lock (lifecycleGate)
        {
            lock (gate)
            {
                SynchronizeLocked();
                PruneStaleLeasesLocked();
                bool stoppedMaintenance = IsConfirmedMaintenanceWindowLocked();
                bool failedProcess = state.Phase == BridgePhase.ERROR && !state.MaintenanceReady &&
                    state.ProcessId > 0 && state.ProcessStartUtcTicks > 0 &&
                    state.ErrorCode != ProcessInspection.ErrorCode;
                if ((stoppedMaintenance || failedProcess) && state.Leases.Count == 0)
                {
                    if (stoppedMaintenance)
                    {
                        MaintenanceValidation validation = RevalidateMaintenanceReadyLocked();
                        if (!validation.Safe)
                        {
                            emit("Test begin denied: " + validation.Error);
                            emit("Error code: " + validation.ErrorCode);
                            EmitNextCommand(emit, "DevBridge.cmd doctor");
                            return 4;
                        }
                    }

                    if (!connected())
                        return 4;

                    maintenanceLease = new TestLease
                    {
                        Id = NewLeaseIdLocked(),
                        Agent = string.IsNullOrWhiteSpace(request.Agent) ? "unknown-agent" : request.Agent,
                        ClientProcessId = request.ClientProcessId,
                        Generation = state.Generation,
                        StartedUtc = clock.UtcNow,
                        LastHeartbeatUtc = clock.UtcNow
                    };
                    state.Leases.Add(maintenanceLease);
                    acquired?.Invoke(maintenanceLease);
                    SaveStateLocked();
                    Monitor.PulseAll(gate);
                }
            }
        }

        if (maintenanceLease != null)
        {
            emit(string.Empty);
            if (!connected())
            {
                ReleaseLeaseSilently(maintenanceLease.Id);
                return 4;
            }
            emit(state.MaintenanceReady ? "Maintenance test lease acquired: " + maintenanceLease.Id :
                "Failure-recovery test lease acquired: " + maintenanceLease.Id);
            emit("Generation: " + maintenanceLease.Generation);
            if (state.MaintenanceReady)
            {
                emit("RimWorld remains safely stopped; no launch was attempted.");
                EmitNextCommand(emit, "DevBridge.cmd ensure-ready " + maintenanceLease.Id);
            }
            else
            {
                emit("The failed RimWorld process remains owned by DevBridge; no launch was attempted.");
                EmitNextCommand(emit, "DevBridge.cmd stop " + maintenanceLease.Id);
            }
            return 0;
        }

        if (!WaitForReady(emit, requireNoRestart: true, connected: connected, waitForMaintenance: true,
                budgetAvailable: budgetAvailable))
            return 4;

        if (!connected())
            return 4;

        lock (gate)
        {
            SynchronizeLocked();
            if (!TestProfileIncludesRegisteredProjectsLocked(request, emit))
                return 4;
        }

        TestLease lease;
        while (true)
        {
            lock (gate)
            {
                SynchronizeLocked();
                if (state.Phase == BridgePhase.ERROR)
                {
                    emit("RimWorld is in ERROR state: " + state.Error);
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }

                if (state.Phase == BridgePhase.READY && !state.RestartPending)
                {
                    if (!connected())
                        return 4;
                    lease = new TestLease
                    {
                        Id = NewLeaseIdLocked(),
                        Agent = string.IsNullOrWhiteSpace(request.Agent) ? "unknown-agent" : request.Agent,
                        ClientProcessId = request.ClientProcessId,
                        Generation = state.Generation,
                        StartedUtc = clock.UtcNow,
                        LastHeartbeatUtc = clock.UtcNow
                    };
                    state.Leases.Add(lease);
                    acquired?.Invoke(lease);
                    SaveStateLocked();
                    Monitor.PulseAll(gate);
                    break;
                }
            }

            emit("Restart is in progress. Waiting for generation " + CurrentTargetGeneration() + "...");
            EmitKeepWaiting(emit);
            WaitForStateChange();
        }

        emit(string.Empty);
        if (!connected())
        {
            ReleaseLeaseSilently(lease.Id);
            return 4;
        }
        emit("Test lease acquired: " + lease.Id);
        if (!connected())
        {
            ReleaseLeaseSilently(lease.Id);
            return 4;
        }
        emit("Generation: " + lease.Generation);
        emit(string.Empty);
        emit("Next action: Test your mod; this lease expires two minutes after its last heartbeat. Renew it before expiresUtc; for automatic renewal, start long-running work with test session, then run:");
        emit("DevBridge.cmd test end " + lease.Id);
        if (!connected())
        {
            ReleaseLeaseSilently(lease.Id);
            return 4;
        }
        return 0;
    }

    private bool TestProfileIncludesRegisteredProjectsLocked(BridgeRequest request, Action<string> emit)
    {
        List<ProjectIntentRegistration> owned = ActiveProjectIntentsLocked()
            .Where(value => ProjectIntentOwnedBy(value, request))
            .ToList();
        List<string> requested = CanonicalProjectUnion(owned.SelectMany(value => value.RequestedProjects));
        if (requested.Count == 0)
            return true;

        List<string> included = CanonicalProjectUnion(state.RequestedProjects ?? new List<string>());
        List<string> missing = requested.Where(value => !included.Contains(value, StringComparer.Ordinal)).ToList();
        if (missing.Count == 0 && state.ProfileMode != ModProfile.LegacyMode &&
            state.LaunchProfileMode != "explicit-human-legacy")
            return true;

        RecordProfileErrorLocked("PROJECT_PROFILE_MISSING",
            "the READY profile does not include every project registered by this agent/session: " +
            string.Join(", ", missing.Count == 0 ? requested : missing));
        emit("Test begin denied: the READY profile does not include every project registered by this agent/session.");
        emit("Missing projects: " + string.Join(", ", missing.Count == 0 ? requested : missing));
        emit("Error code: PROJECT_PROFILE_MISSING");
        emit("Next action: Run DevBridge.cmd status --json, then DevBridge.cmd restart to request the aggregate profile; begin only after includedProjects contains every registered alias.");
        return false;
    }

    private int SessionLease(BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        if (request.Json)
        {
            emit("Usage: DevBridge.cmd test session (streaming command; omit --json)");
            return 2;
        }

        TestLease lease = null;
        int result = BeginLease(request, emit, connected, acquired: value => lease = value);
        if (result != 0 || lease == null)
            return result;

        emit("Connected lease session is active for " + lease.Id + ".");
        emit("DevBridge will heartbeat this lease every " +
            options.LeaseHeartbeatInterval.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
            " seconds while this command remains connected.");
        emit("Keep this session attached to the test owner; cancellation or disconnect stops heartbeats.");
        return RunLeaseSession(request, lease, emit, connected);
    }

    private int RunLeaseSession(BridgeRequest request, TestLease lease, Action<string> emit,
        Func<bool> connected)
    {
        DateTime nextHeartbeatUtc = clock.UtcNow.Add(options.LeaseHeartbeatInterval);
        DateTime nextProgressUtc = clock.UtcNow;

        while (connected())
        {
            bool heartbeat = false;
            bool missing = false;
            string progress = null;
            DateTime now = clock.UtcNow;
            lock (gate)
            {
                PruneStaleLeasesLocked();
                TestLease current = state.Leases.FirstOrDefault(value =>
                    string.Equals(value.Id, lease.Id, StringComparison.OrdinalIgnoreCase));
                if (current == null || !string.Equals(current.Agent, request.Agent, StringComparison.Ordinal))
                {
                    missing = true;
                }
                else
                {
                    if (now >= nextHeartbeatUtc)
                    {
                        current.LastHeartbeatUtc = now;
                        SaveStateLocked();
                        Monitor.PulseAll(gate);
                        heartbeat = true;
                        nextHeartbeatUtc = now.Add(options.LeaseHeartbeatInterval);
                    }

                    if (now >= nextProgressUtc)
                    {
                        progress = "Lease session active: " + current.Id +
                            " expiresUtc=" + FormatUtc(LeaseExpiresUtc(current)) +
                            " retryAfterSeconds=" + RetryAfterSeconds(LeaseExpiresUtc(current), now);
                        nextProgressUtc = now.Add(options.LeaseProgressInterval);
                    }
                }
            }

            if (missing)
            {
                emit("Lease session ended; DevBridge will not renew " + lease.Id + ".");
                return 0;
            }

            if (heartbeat)
                emit("Test lease heartbeat: " + lease.Id);
            if (progress != null)
                emit(progress);

            if (!connected())
                break;

            now = clock.UtcNow;
            TimeSpan delay = options.LeaseSessionPollInterval;
            TimeSpan untilHeartbeat = nextHeartbeatUtc - now;
            TimeSpan untilProgress = nextProgressUtc - now;
            if (untilHeartbeat < delay)
                delay = untilHeartbeat;
            if (untilProgress < delay)
                delay = untilProgress;
            if (delay <= TimeSpan.Zero)
                continue;
            clock.Sleep(delay);
        }

        return 0;
    }

    private int RenewLease(BridgeRequest request, IReadOnlyList<string> arguments, Action<string> emit)
    {
        if (arguments.Count < 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            emit("Usage: DevBridge.cmd test renew <lease-id>");
            return 2;
        }

        string leaseId = arguments[1].Trim().ToUpperInvariant();
        lock (gate)
        {
            PruneStaleLeasesLocked();
            if (!TryGetLeaseHolderLocked(leaseId, request, out TestLease lease))
            {
                emit("Test lease renewal denied: lease " + leaseId +
                    " is not held by this agent or has expired.");
                return 4;
            }

            lease.LastHeartbeatUtc = clock.UtcNow;
            SaveStateLocked();
            Monitor.PulseAll(gate);
            emit("Test lease renewed: " + lease.Id);
            emit("Next action: Continue testing; renew the lease before expiresUtc, or keep a connected test session.");
            return 0;
        }
    }

    private int EndLease(BridgeRequest request, IReadOnlyList<string> arguments, Action<string> emit)
    {
        if (arguments.Count < 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            emit("Usage: DevBridge.cmd test end <lease-id>");
            return 2;
        }

        string leaseId = arguments[1].Trim().ToUpperInvariant();
        lock (gate)
        {
            PruneStaleLeasesLocked();
            TestLease lease = state.Leases.FirstOrDefault(value =>
                string.Equals(value.Id, leaseId, StringComparison.OrdinalIgnoreCase));
            if (lease == null)
            {
                emit("Test lease " + leaseId + " was already released or expired.");
                return 0;
            }

            if (!string.Equals(lease.Agent, request.Agent, StringComparison.Ordinal))
            {
                emit("Test lease release denied: lease " + leaseId +
                    " is not held by this stable agent identity.");
                return 4;
            }

            if (!TryRestoreViewportForLeaseLocked(lease.Id))
            {
                SaveStateLocked();
                emit("Test lease release denied: the viewport transaction could not be restored safely.");
                emit("Error code: " + (state.ViewportEnvironment?.RestorationErrorCode ??
                    "VIEWPORT_RESTORE_FAILED"));
                emit("Next action: Restore the viewport transaction explicitly before releasing the lease.");
                return 4;
            }

            state.Leases.Remove(lease);
            SaveStateLocked();
            Monitor.PulseAll(gate);
            emit("Test lease released: " + leaseId);
            if (state.RestartPending && state.Leases.Count == 0)
            {
                emit("No active tests remain. DevBridge will continue the pending restart automatically.");
                EmitKeepWaiting(emit);
            }
            else
                emit("Next action: Continue your workflow; run DevBridge.cmd restart only after a change requiring a fresh process.");
            return 0;
        }
    }

}
