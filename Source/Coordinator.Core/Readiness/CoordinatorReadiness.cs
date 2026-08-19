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
    private int WaitReady(BridgeRequest request, Action<string> emit)
    {
        emit("Agent/session: " + request.Agent);
        bool startInitialLaunch;
        lock (gate)
        {
            SynchronizeLocked();
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

        if (!WaitForReady(emit, requireNoRestart: true))
            return 4;

        lock (gate)
        {
            emit("RimWorld is ready.");
            emit("Generation: " + state.Generation);
            emit("Quicktest map is ready.");
            List<string> missingProjects = MissingProjectsFor(state, request);
            EmitNextCommand(emit, missingProjects.Count == 0
                ? "DevBridge.cmd test begin"
                : "DevBridge.cmd restart");
        }
        return 0;
    }

    private bool WaitForReady(Action<string> emit, bool requireNoRestart, Func<bool> connected = null,
        bool waitForMaintenance = false, Func<bool> budgetAvailable = null)
    {
        DateTime nextProgress = clock.UtcNow;
        bool first = true;
        while (true)
        {
            if (connected != null && !connected())
                return false;
            if (budgetAvailable != null && !budgetAvailable())
                return false;

            lock (gate)
            {
                SynchronizeLocked();
                bool ready = state.Phase == BridgePhase.READY &&
                    (!requireNoRestart || !state.RestartPending);
                if (ready)
                    return true;

                if (state.Phase == BridgePhase.ERROR && !state.RestartPending)
                {
                    emit("RimWorld is in ERROR state: " + state.Error);
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return false;
                }

                if (state.Phase == BridgePhase.STOPPED && state.Generation > 0 && !state.RestartPending)
                {
                    if (state.MaintenanceReady && waitForMaintenance)
                    {
                        emit("RimWorld is stopped for a lease-held maintenance window.");
                        emit("Waiting for the lease holder to run ensure-ready or restart.");
                        EmitKeepWaiting(emit);
                        first = false;
                        nextProgress = clock.UtcNow.Add(ProgressInterval);
                        Monitor.Wait(gate, 1000);
                        continue;
                    }

                    emit("RimWorld is stopped.");
                    EmitNextCommand(emit, "DevBridge.cmd restart");
                    return false;
                }

                if (first || clock.UtcNow >= nextProgress)
                {
                    int target = CurrentTargetGenerationLocked();
                    emit("Waiting for RimWorld generation " + target + "...");
                    emit("State: " + state.Phase + ". Waiting for the quicktest map readiness signal.");
                    EmitKeepWaiting(emit);
                    first = false;
                    nextProgress = clock.UtcNow.Add(ProgressInterval);
                }

                Monitor.Wait(gate, 1000);
            }
        }
    }

    private void EmitRestartWait(Action<string> emit)
    {
        PersistedState snapshot;
        lock (gate)
        {
            PruneStaleLeasesLocked();
            snapshot = CloneStateLocked();
        }

        if (snapshot.Leases.Count == 0)
        {
            emit("Restart is queued and owned by DevBridge.");
            emit("No active tests remain.");
            emit("State: " + snapshot.Phase + ". Waiting for generation " + snapshot.TargetGeneration +
                " quicktest map readiness.");
            EmitKeepWaiting(emit);
            return;
        }

        emit("Restart is queued and owned by DevBridge.");
        emit("Waiting for " + snapshot.Leases.Count + " active test" + (snapshot.Leases.Count == 1 ? "" : "s") + ".");
        EmitLeaseWaitDetails(snapshot, emit);
        EmitKeepWaiting(emit);
    }

    private void EmitLeaseWaitDetails(PersistedState snapshot, Action<string> emit)
    {
        TestLease next = snapshot.Leases
            .OrderBy(value => LeaseExpiresUtc(value))
            .FirstOrDefault();
        if (next == null)
            return;

        DateTime now = clock.UtcNow;
        emit("Next blocking lease can expire at " + FormatUtc(LeaseExpiresUtc(next)) +
            " (retryAfterSeconds=" + RetryAfterSeconds(LeaseExpiresUtc(next), now) + ").");
        foreach (TestLease lease in snapshot.Leases.OrderBy(value => value.StartedUtc))
            emit("  " + lease.Id + " - " + lease.Agent +
                " - lastHeartbeatUtc=" + FormatUtc(LeaseActivityUtc(lease)) +
                " - expiresUtc=" + FormatUtc(LeaseExpiresUtc(lease)) +
                " - retryAfterSeconds=" + RetryAfterSeconds(LeaseExpiresUtc(lease), now));
    }

    private void ReleaseLeaseSilently(string leaseId)
    {
        lock (gate)
        {
            TestLease lease = state.Leases.FirstOrDefault(value =>
                string.Equals(value.Id, leaseId, StringComparison.OrdinalIgnoreCase));
            if (lease == null)
                return;
            if (!TryRestoreViewportForLeaseLocked(lease.Id))
            {
                SaveStateLocked();
                Monitor.PulseAll(gate);
                return;
            }
            state.Leases.Remove(lease);
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }
    }

    private string LaunchOwnerFor(BridgeRequest request)
    {
        string agent = string.IsNullOrWhiteSpace(request?.Agent) ? "unknown-agent" : request.Agent.Trim();
        return agent + "@" + (request?.ClientProcessId ?? 0).ToString();
    }

}
