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
    private int Help(Action<string> emit)
    {
        emit("DevBridge commands:");
        emit("  DevBridge.cmd status");
        emit("  DevBridge.cmd bridge status");
        emit("  DevBridge.cmd bridge policy  (read-only RimBridge ownership policy)");
        emit("  DevBridge.cmd bridge endpoint  (explicit credential-bearing command; ordinary status never shows the token)");
        emit("  DevBridge.cmd bridge tools  (lease-bound read-only RimBridge discovery)");
        emit("  DevBridge.cmd bridge call <tool-name> [JSON arguments] [--lease <lease-id>]  (lease-bound routed call)");
        emit("  DevBridge.cmd game inspect|action|wait|advance|save|load|errors  (lease-safe semantic game primitives)");
        emit("  DevBridge.cmd project resolve <alias[,alias...]> [--explain]  (pure no-mutation planning)");
        emit("  DevBridge.cmd project register <alias[,alias...]> [--id <stable-registration-id>]");
        emit("  DevBridge.cmd project status");
        emit("  DevBridge.cmd project renew <registration-id>");
        emit("  DevBridge.cmd project release <registration-id>");
        emit("  DevBridge.cmd mods status");
        emit("  DevBridge.cmd mods capture-baseline");
        emit("  DevBridge.cmd mods restore-baseline");
        emit("  DevBridge.cmd test begin");
        emit("  DevBridge.cmd test session");
        emit("  DevBridge.cmd test renew <lease-id>");
        emit("  DevBridge.cmd test end <lease-id>");
        emit("  DevBridge.cmd stop <lease-id>");
        emit("  DevBridge.cmd coordinator shutdown  (refresh the coordinator only; RimWorld and durable state remain unchanged)");
        emit("  DevBridge.cmd ensure-ready <lease-id>");
        emit("  DevBridge.cmd restart [--projects none|alias[,alias...]]");
        emit("  DevBridge.cmd restart --legacy-production  (explicit human production compatibility; never an automatic fallback)");
        emit("  DevBridge.cmd wait-ready");
        emit("  DevBridge.cmd history");
        emit("  DevBridge.cmd history show <generation>");
        emit("  DevBridge.cmd history last-good");
        emit("  DevBridge.cmd logs query [--generation <n>] [--since-launch] [--severity <level>] [--fingerprint <id>] [--component <name>] [--limit <n>] [--trace] --json");
        emit("  DevBridge.cmd evidence show <id> --json");
        emit("  DevBridge.cmd doctor");
        emit("  DevBridge.cmd agent capabilities|snapshot|delta|wait-event --json");
        emit("  pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\\live-stack-smoke.ps1 -Json  (self-hosted real RimWorld gate)");
        emit("Append --json to a non-session command for one machine-readable result.");
        emit("Plan first: project resolve <alias[,alias...]> --json; inspect the exact closure/fingerprint, then register and restart.");
        emit("Register project intent before testing: project register <alias[,alias...]>; renew it while active and release it when finished.");
        emit("Aggregate-first: register immediately even when other project intents or tests are active; do not wait for an exclusive profile unless isolating a failure or honoring a known incompatibility.");
        emit("Restart or await: restart, then verify status --json reports the frozen registration, project union, ordered closure, and fingerprints before test begin.");
        emit("During crash isolation, poll status only; do not restart, edit ModsConfig.xml, or mutate registrations.");
        emit("test session is a connected streaming lease owner; keep it attached to the test owner.");
        return 0;
    }

    private static int Unknown(string command, Action<string> emit)
    {
        emit("Unknown DevBridge command: " + command);
        emit("Use: status, bridge status/policy/endpoint/tools/call, game inspect/action/wait/advance/save/load/errors, project resolve/register/status/renew/release, mods status/capture-baseline/restore-baseline, test begin/session/renew/end, stop <lease-id>, ensure-ready <lease-id>, restart [--projects ...|--legacy-production], wait-ready, history [show <generation>|last-good], logs query, evidence show, doctor, agent capabilities|snapshot|delta|wait-event");
        EmitNextCommand(emit, "DevBridge.cmd help");
        return 2;
    }

    private static void EmitNextCommand(Action<string> emit, string command)
    {
        emit("Next action: Run:");
        emit(command);
    }

    private static void EmitKeepWaiting(Action<string> emit)
    {
        emit("Next action: Keep waiting. DevBridge owns the accepted restart; reconnect with DevBridge.cmd wait-ready. Do not launch, kill, restart, or end your task because of lease contention.");
    }

    private int Status(BridgeRequest request, Action<string> emit)
    {
        PersistedState snapshot;
        ProcessStatusSnapshot processSnapshot = new();
        bool processInspectionAmbiguous = false;
        lock (gate)
        {
            SynchronizeLocked();
            processInspectionAmbiguous = state.ErrorCode == ProcessInspection.ErrorCode;
            try
            {
                processSnapshot = EnumerateStatusProcessesLocked();
                if (state.MaintenanceReady && processSnapshot.MatchingProcessCount > 0)
                    MarkMaintenanceProcessPresentLocked();
            }
            catch (ProcessInspectionException)
            {
                processInspectionAmbiguous = true;
                MarkProcessInspectionAmbiguousLocked();
            }
            if (processInspectionAmbiguous && state.MaintenanceReady)
                MarkProcessInspectionAmbiguousLocked();

            RefreshRimBridgePolicyStateLocked();
            snapshot = CloneStateLocked();
            snapshot.BaselineFingerprint = ReadBaselineFingerprintLocked() ?? snapshot.BaselineFingerprint;
            snapshot.ModsConfigOwnership = CurrentModsConfigOwnershipLocked();
        }

        emit("DevBridge2 status");
        emit("Agent/session: " + request.Agent);
        emit("State: " + snapshot.Phase);
        string heldLease = snapshot.Leases.FirstOrDefault(value =>
            string.Equals(value.Agent, request.Agent, StringComparison.Ordinal))?.Id;
        emit("gameState=" + snapshot.Phase + " maintenanceReady=" + snapshot.MaintenanceReady.ToString().ToLowerInvariant() +
            " leaseState=" + (heldLease == null ? "QUEUED" : "HELD"));
        emit("Generation: " + snapshot.Generation);
        emit("RimWorld: " + (processSnapshot.OwnedProcessRunning ? "running" : "not running") +
            (snapshot.ProcessId > 0 ? " (PID " + snapshot.ProcessId + ")" : string.Empty));
        if (processInspectionAmbiguous)
            emit("WARNING: RimWorld process inspection is ambiguous; no process-control or launch action was taken.");
        if (processSnapshot.UnmanagedProcesses.Count > 0)
        {
            emit("WARNING: unmanaged RimWorld process(es) detected: " +
                 string.Join(", ", processSnapshot.UnmanagedProcesses.Select(value => value.ProcessId.ToString())));
            emit("Close the unmanaged process through Steam before the next DevBridge restart.");
        }
        emit("Launch ID: " + (string.IsNullOrWhiteSpace(snapshot.LaunchId) ? "none" : snapshot.LaunchId));
        EmitRimBridgeStatus(snapshot.RimBridge, emit);
        EmitRimBridgePolicyStatus(snapshot.RimBridgePolicy, snapshot.ExternalModsConfigMutation, emit);
        emit("Active tests: " + snapshot.Leases.Count);
        emit("Session dirty: " + snapshot.SessionDirty);
        snapshot.BaselineFingerprint = ReadBaselineFingerprintLocked() ?? snapshot.BaselineFingerprint;
        snapshot.ModsConfigOwnership = CurrentModsConfigOwnershipLocked();
        EmitProfile(snapshot, emit);
        EmitAggregateIntentStatusLocked(snapshot, request, emit);
        foreach (TestLease lease in snapshot.Leases.OrderBy(value => value.StartedUtc))
            emit("  " + lease.Id + " - " + lease.Agent + " - age " + FormatAge(lease.StartedUtc) +
                " - lastHeartbeatUtc=" + FormatUtc(LeaseActivityUtc(lease)) +
                " - expiresUtc=" + FormatUtc(LeaseExpiresUtc(lease)) +
                " - retryAfterSeconds=" + RetryAfterSeconds(LeaseExpiresUtc(lease), clock.UtcNow));

        if (snapshot.RestartPending)
        {
            emit("Restart is queued and owned by DevBridge.");
            emit("Restart: pending for generation " + snapshot.TargetGeneration +
                (snapshot.RestartRequestedUtc.HasValue ? " (requested " + FormatAge(snapshot.RestartRequestedUtc.Value) + " ago)" : string.Empty));
            if (snapshot.Leases.Count > 0)
                EmitLeaseWaitDetails(snapshot, emit);
            emit("New test requests are waiting for the new generation.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            emit("Error: " + snapshot.Error);
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorCode))
            emit("Error code: " + snapshot.ErrorCode);
        if (!string.IsNullOrWhiteSpace(snapshot.ProfileError))
            emit("Profile error: " + snapshot.ProfileErrorCode + " - " + snapshot.ProfileError);
        if (!string.IsNullOrWhiteSpace(snapshot.ProfileConflict))
            emit("Profile conflict: " + snapshot.ProfileConflict);

        if (snapshot.MaintenanceReady)
        {
            TestLease holder = snapshot.Leases.FirstOrDefault();
            emit("Maintenance window is confirmed safe for assembly replacement.");
            if (holder != null)
                EmitNextCommand(emit, "DevBridge.cmd ensure-ready " + holder.Id);
            else
                EmitKeepWaiting(emit);
        }
        else if (snapshot.Phase == BridgePhase.READY && !snapshot.RestartPending)
        {
            List<string> missingProjects = MissingProjectsFor(snapshot, request);
            if (missingProjects.Count > 0)
            {
                emit("Your active project intents are not in the READY profile: " +
                    string.Join(", ", missingProjects) + ".");
                EmitNextCommand(emit, "DevBridge.cmd restart");
            }
            else
            {
                emit("Test leases are shared; multiple agents may test this generation concurrently.");
                EmitNextCommand(emit, "DevBridge.cmd test begin");
            }
        }
        else if (snapshot.ExternalModsConfigMutation != null ||
                 snapshot.ErrorCode == "PROFILE_EXTERNAL_MUTATION")
        {
            emit("Next action: Run DevBridge.cmd mods status, then perform explicit baseline/profile reconciliation before restarting.");
        }
        else if (snapshot.Phase == BridgePhase.ERROR ||
                 snapshot.ErrorCode == ProcessInspection.ErrorCode ||
                 snapshot.ErrorCode == "MAINTENANCE_PROCESS_PRESENT")
            EmitNextCommand(emit, "DevBridge.cmd doctor");
        else if (snapshot.Phase == BridgePhase.STOPPED && snapshot.Generation > 0 && !snapshot.RestartPending)
            EmitNextCommand(emit, "DevBridge.cmd restart");
        else if (snapshot.RestartPending || snapshot.Phase == BridgePhase.DRAINING ||
                 snapshot.Phase == BridgePhase.RESTARTING || snapshot.Phase == BridgePhase.LOADING)
            EmitKeepWaiting(emit);
        else
            EmitNextCommand(emit, "DevBridge.cmd wait-ready");

        return 0;
    }

    private int Doctor(BridgeRequest request, Action<string> emit)
    {
        DoctorAuditReport report = RunDoctorAudit(request);
        request.DoctorAudit = report;
        emit("DevBridge2 doctor");
        emit("Health: " + (report.Healthy ? "HEALTHY" : "UNHEALTHY"));
        foreach (IGrouping<string, DoctorFinding> group in report.Findings
                     .GroupBy(value => value.Component, StringComparer.Ordinal)
                     .OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            emit(group.Key + ":");
            foreach (DoctorFinding finding in group.OrderBy(value => value.StableKey(), StringComparer.Ordinal))
            {
                emit("  " + finding.Severity + " " + finding.Code + ": " + finding.Message);
                if (finding.NextActions.Count > 0)
                    emit("    next: " + finding.NextActions[0].DisplayCommand());
            }
        }
        if (report.NextActions.Count > 0)
            emit("Safe next actions:");
        foreach (DoctorNextAction action in report.NextActions)
            emit("  " + action.DisplayCommand() + (action.RequiresLeaseId ? " (requires lease)" : ""));
        return report.Healthy ? 0 : 1;
    }

}
