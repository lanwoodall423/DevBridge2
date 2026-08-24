using System.Globalization;
using System.Text.Json;

namespace DevBridge.Coordinator;

internal sealed partial class CoordinatorState
{
    private DevBridgeIdentityContract BuildIdentityContract(PersistedState snapshot,
        ProcessStatusSnapshot processSnapshot)
    {
        snapshot ??= new PersistedState();
        List<AlternateRootContract> alternateRoots = FindAlternateRoots();
        int expectedPid = snapshot.ProcessId;
        long expectedStart = snapshot.ProcessStartUtcTicks;
        bool expectedPresent = processSnapshot?.OwnedProcessRunning == true;
        string expectedStatus = expectedPid <= 0
            ? "not-expected"
            : expectedPresent ? "present-and-matching" :
            (processSnapshot == null ? "not-observed" : "stale-or-mismatched");
        int retiredRegistrations = (snapshot.ProjectIntents ?? new List<ProjectIntentRegistration>())
            .Count(value => value != null && !string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal));
        int? supersededGeneration = snapshot.RestartPending && snapshot.TargetGeneration > snapshot.Generation
            ? snapshot.Generation : null;

        return new DevBridgeIdentityContract
        {
            AuthoritativeRoot = coordinatorRoot,
            RootSelectionSource = "explicit-cli-root",
            InstallationId = snapshot.InstallationId,
            OwnerId = snapshot.InstallationId,
            RuntimeSlotId = snapshot.RuntimeSlotId,
            Coordinator = new CoordinatorIdentityContract
            {
                InstanceId = coordinatorInstanceId,
                Status = ShutdownRequested ? "stopping" : "running",
                ProcessId = Environment.ProcessId,
                StartedUtc = processStartedUtc,
                PreviousInstanceId = snapshot.PreviousCoordinatorInstanceId,
                PreviousStatus = string.IsNullOrWhiteSpace(snapshot.PreviousCoordinatorInstanceId)
                    ? null : "replaced"
            },
            Runtime = new RuntimeIdentityContract
            {
                Generation = snapshot.Generation,
                TargetGeneration = snapshot.RestartPending && snapshot.TargetGeneration > 0
                    ? snapshot.TargetGeneration : null,
                LaunchGeneration = snapshot.LaunchGeneration,
                LifecycleState = snapshot.Phase.ToString(),
                Transition = LifecycleTransition(snapshot)
            },
            ExpectedRimWorldProcess = new RimWorldIdentityContract
            {
                ProcessId = expectedPid,
                StartIdentity = expectedStart,
                Generation = snapshot.LaunchGeneration > 0 ? snapshot.LaunchGeneration : snapshot.Generation,
                LaunchId = snapshot.LaunchId,
                Present = expectedPresent,
                MatchesExpected = expectedPresent
            },
            CurrentRimWorldProcesses = (processSnapshot?.MatchingProcesses ?? new List<UnmanagedRimWorldProcess>())
                .OrderBy(value => value.ProcessId)
                .Select(value => new RimWorldIdentityContract
                {
                    ProcessId = value.ProcessId,
                    StartIdentity = value.ProcessStartIdentity,
                    Generation = value.ProcessId == expectedPid &&
                        value.ProcessStartIdentity == expectedStart
                        ? snapshot.Generation : 0,
                    LaunchId = value.ProcessId == expectedPid &&
                        value.ProcessStartIdentity == expectedStart ? snapshot.LaunchId : null,
                    Present = true,
                    MatchesExpected = value.ProcessId == expectedPid &&
                        value.ProcessStartIdentity == expectedStart
                }).ToList(),
            Protocol = new ProtocolIdentityContract(),
            StaleState = new StaleStateContract
            {
                ExpectedProcessStatus = expectedStatus,
                RetiredRegistrationCount = retiredRegistrations,
                SupersededGeneration = supersededGeneration,
                CleanupPolicy = "mark-retired; never delete active ownership without an authoritative expiry or identity proof"
            },
            AlternateRoots = alternateRoots
        };
    }

    private static string LifecycleTransition(PersistedState snapshot)
    {
        if (snapshot.RestartPending || snapshot.Phase == BridgePhase.RESTARTING ||
            snapshot.Phase == BridgePhase.DRAINING)
            return "replacing-generation";
        if (snapshot.Phase == BridgePhase.WAITING_FOR_BRIDGE)
            return "waiting-for-bridge";
        if (snapshot.Phase == BridgePhase.LOADING)
            return "loading-new-generation";
        if (snapshot.Phase == BridgePhase.READY)
            return "stable-ready";
        if (snapshot.Phase == BridgePhase.STOPPED)
            return "stopped";
        return snapshot.Phase == BridgePhase.ERROR ? "blocked-error" : "transitioning";
    }

    private List<AlternateRootContract> FindAlternateRoots()
    {
        List<AlternateRootContract> result = new();
        try
        {
            DirectoryInfo parent = Directory.GetParent(coordinatorRoot);
            if (parent == null || !parent.Exists)
                return result;

            foreach (string candidate in Directory.GetDirectories(parent.FullName)
                         .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                         .Take(128))
            {
                if (RuntimeScope.PathsEqual(candidate, coordinatorRoot))
                    continue;
                string candidateStatePath = Path.Combine(candidate, "Runtime", "state.json");
                if (!File.Exists(candidateStatePath))
                    continue;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(candidateStatePath));
                    if (!TryGetString(document.RootElement, "coordinatorRoot", out string persistedRoot) ||
                        !RuntimeScope.PathsEqual(persistedRoot, candidate))
                        continue;
                    TryGetString(document.RootElement, "installationId", out string installationId);
                    result.Add(new AlternateRootContract
                    {
                        Root = RuntimeScope.CanonicalizeRootPath(candidate),
                        InstallationId = installationId,
                        StatePath = candidateStatePath
                    });
                }
                catch
                {
                    // An unreadable alternate is not adopted. The owning root's
                    // own state remains authoritative and the unreadable file is
                    // left for doctor permission/schema diagnostics.
                }
            }
        }
        catch
        {
            // Root discovery is diagnostic only; failure cannot alter ownership.
        }
        return result;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString();
                return true;
            }
        }
        value = null;
        return false;
    }
}
