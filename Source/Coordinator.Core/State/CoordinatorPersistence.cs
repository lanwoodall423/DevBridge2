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
    private void DeleteReadinessLocked()
    {
        try
        {
            if (File.Exists(readinessPath))
                File.Delete(readinessPath);
        }
        catch
        {
            // A stale readiness file is ignored unless it matches the new launch ID.
        }
    }

    private void DeleteQuicktestFailureArtifactLocked()
    {
        QuicktestFailureArtifact.Invalidate(root);
    }

    private string NewLeaseIdLocked()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            // Lease IDs are authorization capabilities. Keep the complete
            // 128-bit value durable; short prefixes are not sufficient for
            // equality or ownership checks.
            string id = "lease-" + Guid.NewGuid().ToString("N").ToUpperInvariant();
            if (state.Leases.All(lease => !string.Equals(lease.Id, id, StringComparison.OrdinalIgnoreCase)))
                return id;
        }

        throw new InvalidOperationException("Unable to allocate a unique coordinator lease ID.");
    }

    private int CurrentTargetGeneration()
    {
        lock (gate)
            return CurrentTargetGenerationLocked();
    }

    private int CurrentTargetGenerationLocked()
    {
        return state.RestartPending && state.TargetGeneration > 0 ? state.TargetGeneration :
            Math.Max(1, state.Generation);
    }

    private void WaitForStateChange(TimeSpan? timeout = null)
    {
        lock (gate)
            Monitor.Wait(gate, timeout ?? TimeSpan.FromSeconds(1));
    }

    private PersistedState LoadState()
    {
        if (!File.Exists(statePath))
            return new PersistedState();

        try
        {
            PersistedState loaded = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(statePath), CoordinatorSerialization.JsonOptions);
            if (loaded == null)
                return BlockPersistedState("PERSISTED_STATE_MALFORMED",
                    "Runtime/state.json was empty or did not contain a state object.");
            if (loaded.SchemaVersion < 0)
                return BlockPersistedState("PERSISTED_STATE_SCHEMA_INVALID",
                    "Runtime/state.json contains an invalid schema version: " + loaded.SchemaVersion + ".",
                    loaded.SchemaVersion);
            if (loaded.SchemaVersion > DevBridgeSchemaVersions.RuntimeState)
                return BlockPersistedState("PERSISTED_STATE_SCHEMA_UNSUPPORTED",
                    "Runtime/state.json uses unsupported schema version " + loaded.SchemaVersion + ".",
                    loaded.SchemaVersion);
            return loaded;
        }
        catch
        {
            string backup = statePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(statePath, backup); } catch { }
            return BlockPersistedState("PERSISTED_STATE_MALFORMED",
                "Runtime/state.json was invalid and was moved to " + backup + ".");
        }
    }

    private PersistedState BlockPersistedState(string errorCode, string error, int schemaVersion = 0)
    {
        persistedStateLoadBlocked = true;
        return new PersistedState
        {
            SchemaVersion = schemaVersion,
            CoordinatorRoot = coordinatorRoot,
            RuntimeSlotId = runtimeSlotId,
            Phase = BridgePhase.ERROR,
            ErrorCode = errorCode,
            Error = error
        };
    }

    private void RecordPersistedArtifactErrorLocked(string errorCode, string error)
    {
        if (string.Equals(state.ErrorCode, errorCode, StringComparison.Ordinal) &&
            string.Equals(state.Error, error, StringComparison.Ordinal))
            return;

        state.ErrorCode = errorCode;
        state.Error = error;
        state.Phase = BridgePhase.ERROR;
        state.RestartPending = false;
        state.MaintenanceReady = false;
        state.RequiresNewProcess = true;
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void NormalizeStateLocked()
    {
        if (persistedStateLoadBlocked)
            return;
        bool changed = true;

        if (string.IsNullOrWhiteSpace(state.InstallationId))
        {
            state.InstallationId = Guid.NewGuid().ToString("N");
            changed = true;
        }
        if (!string.Equals(state.CoordinatorInstanceId, coordinatorInstanceId, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(state.CoordinatorInstanceId))
            {
                state.PreviousCoordinatorInstanceId = state.CoordinatorInstanceId;
                state.PreviousCoordinatorProcessId = state.CoordinatorProcessId;
                state.PreviousCoordinatorStartedUtc = state.CoordinatorStartedUtc == default
                    ? null : state.CoordinatorStartedUtc;
            }
            state.CoordinatorInstanceId = coordinatorInstanceId;
            state.CoordinatorProcessId = Environment.ProcessId;
            state.CoordinatorStartedUtc = processStartedUtc;
            changed = true;
        }

        BeginAgentEpochLocked();
        // The epoch is process-scoped. Persist the fresh epoch even when the
        // rest of the legacy state needs no normalization so a crash cannot
        // leave an old cursor silently durable.
        if (state.SchemaVersion != DevBridgeSchemaVersions.RuntimeState)
        {
            state.SchemaVersion = DevBridgeSchemaVersions.RuntimeState;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(state.CoordinatorRoot))
        {
            state.CoordinatorRoot = coordinatorRoot;
            changed = true;
        }
        else if (!RuntimeScope.PathsEqual(state.CoordinatorRoot, coordinatorRoot))
        {
            throw new InvalidOperationException("Persisted coordinator root does not match this coordinator.");
        }

        if (string.IsNullOrWhiteSpace(state.RuntimeSlotId))
        {
            state.RuntimeSlotId = runtimeSlotId;
            changed = true;
        }
        else if (!string.Equals(state.RuntimeSlotId, runtimeSlotId, StringComparison.Ordinal))
        {
            if (RuntimeScope.IsLegacyRuntimeSlot(state.RuntimeSlotId))
                throw new InvalidOperationException(RuntimeScope.LegacyRuntimeSlotGuidance(
                    coordinatorRoot, state.RuntimeSlotId));
            throw new InvalidOperationException("Persisted runtime slot does not match this coordinator root.");
        }

        state.Leases ??= new List<TestLease>();
        state.ScopeTickets ??= new List<ScopeTicket>();
        state.ProjectIntents ??= new List<ProjectIntentRegistration>();
        state.FrozenRegistrations ??= new List<ProjectIntentSnapshot>();
        state.FrozenRequestedProjects ??= new List<string>();
        state.FrozenResolvedProjectPackageIds ??= new List<string>();
        state.FrozenResolvedMods ??= new List<string>();
        state.TestInputs ??= new List<TestInputValue>();
        state.FrozenTestInputs ??= new List<TestInputValue>();
        state.AggregateGenerations ??= new List<AggregateGenerationEvidence>();
        state.CrashIsolationHistory ??= new List<CrashIsolationIncident>();
        state.FailureOccurrences ??= new List<FailureOccurrenceSummary>();
        if (state.FailureOccurrences.Count > FailureEvidenceLimits.MaxOccurrences)
        {
            state.FailureOccurrences = state.FailureOccurrences
                .Where(value => value != null)
                .OrderByDescending(value => value.LastSeenUtc)
                .ThenByDescending(value => value.LastSeenGeneration)
                .Take(FailureEvidenceLimits.MaxOccurrences)
                .ToList();
            changed = true;
        }
        if (state.CrashIsolation != null)
        {
            if (string.IsNullOrWhiteSpace(state.CrashIsolation.Status))
            {
                state.CrashIsolation.Status = "RUNNING";
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(state.CrashIsolation.Stage))
            {
                state.CrashIsolation.Stage = "CONTROL";
                changed = true;
            }
            state.CrashIsolation.Attempts ??= new List<CrashIsolationAttempt>();
            state.CrashIsolation.Diagnoses ??= new List<CrashIsolationDiagnosis>();
            state.CrashIsolation.SearchPoolProjects ??= new List<string>();
            state.CrashIsolation.DeltaCurrentProjects ??= new List<string>();
            state.CrashIsolation.PendingCandidates ??= new List<CrashIsolationSelection>();
            state.CrashIsolation.CurrentAttemptProjects ??= new List<string>();
            state.CrashIsolation.OriginalRequestedProjects ??= new List<string>();
            state.CrashIsolation.OriginalResolvedProjectPackageIds ??= new List<string>();
            state.CrashIsolation.OriginalResolvedMods ??= new List<string>();
            state.CrashIsolation.OriginalDiagnosticMetadata ??= new Dictionary<string, string>();
            state.CrashIsolation.OriginalRegistrations ??= new List<ProjectIntentSnapshot>();
            state.CrashIsolation.ProjectRequesters ??= new Dictionary<string, List<ProjectIntentRequester>>();
            // The two copies are a launch guard and incident evidence. Never
            // replenish either one from the other after a restart: a mismatch
            // fails closed at the lower value and is repaired durably.
            int isolationBudget = Math.Min(Math.Max(0, state.IsolationLaunchesRemaining),
                Math.Max(0, state.CrashIsolation.IsolationLaunchesRemaining));
            if (state.IsolationLaunchesRemaining != isolationBudget)
            {
                state.IsolationLaunchesRemaining = isolationBudget;
                changed = true;
            }
            if (state.CrashIsolation.IsolationLaunchesRemaining != isolationBudget)
            {
                state.CrashIsolation.IsolationLaunchesRemaining = isolationBudget;
                changed = true;
            }
        }
        bool profileFieldsPresent = (state.RequestedProjects?.Count ?? 0) > 0 ||
            (state.ResolvedProjectPackageIds?.Count ?? 0) > 0 ||
            (state.ResolvedMods?.Count ?? 0) > 0 ||
            !string.IsNullOrWhiteSpace(state.ProfileFingerprint);
        string persistedProfileMode = state.ProfileMode;
        if (string.IsNullOrWhiteSpace(persistedProfileMode))
        {
            if (profileFieldsPresent || state.RestartPending)
                QuarantineInvalidProfileLocked(new ProfileException("PROFILE_INVALID_STATE",
                    "Persisted profile mode is missing while profile or restart state is present."));
            else
            {
                state.ProfileMode = ModProfile.LegacyMode;
                changed = true;
            }
        }
        else
            state.ProfileMode = persistedProfileMode.Trim().ToLowerInvariant();

        string expectedLaunchProfileMode = state.ProfileMode == ModProfile.LegacyMode
            ? "explicit-human-legacy"
            : state.ProfileMode == ModProfile.BaselineMode
                ? "aggregate-minimal-control"
                : "aggregate-projects";
        if (!string.Equals(state.LaunchProfileMode, expectedLaunchProfileMode, StringComparison.Ordinal))
        {
            state.LaunchProfileMode = expectedLaunchProfileMode;
            changed = true;
        }

        if (state.ProfileMode != ModProfile.LegacyMode && state.ProfileMode != ModProfile.BaselineMode &&
            state.ProfileMode != ModProfile.ProjectsMode)
        {
            QuarantineInvalidProfileLocked(new ProfileException("PROFILE_INVALID_STATE",
                "Persisted profile mode is invalid: " + persistedProfileMode + "."));
        }
        state.RequestedProjects ??= new List<string>();
        state.ResolvedProjectPackageIds ??= new List<string>();
        state.ResolvedMods ??= new List<string>();
        foreach (ProjectIntentRegistration registration in state.ProjectIntents.Where(value => value != null))
        {
            registration.RequestedProjects ??= new List<string>();
            registration.Owner ??= "unknown-agent";
            registration.SessionId ??= registration.Owner;
            if (registration.CreatedUtc == default)
            {
                registration.CreatedUtc = registration.LastHeartbeatUtc == default ? clock.UtcNow : registration.LastHeartbeatUtc;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(registration.Status))
            {
                registration.Status = "ACTIVE";
                changed = true;
            }
            else
            {
                string normalizedStatus = registration.Status.Trim().ToUpperInvariant();
                if (!string.Equals(registration.Status, normalizedStatus, StringComparison.Ordinal))
                {
                    registration.Status = normalizedStatus;
                    changed = true;
                }
            }
            if (string.Equals(registration.Status, "ACTIVE", StringComparison.Ordinal))
            {
                try
                {
                    IReadOnlyList<string> canonical = ModProfileResolver.CanonicalAliases(registration.RequestedProjects);
                    if (!registration.RequestedProjects.SequenceEqual(canonical, StringComparer.Ordinal))
                    {
                        registration.RequestedProjects = canonical.ToList();
                        changed = true;
                    }
                }
                catch (ProfileException exception)
                {
                    registration.Status = "INVALID";
                    registration.ReleasedUtc = clock.UtcNow;
                    registration.ReleaseReason = exception.Code + ": " + exception.Message;
                    changed = true;
                }
            }
            if (registration.LastHeartbeatUtc == default)
            {
                registration.LastHeartbeatUtc = registration.CreatedUtc == default ? clock.UtcNow : registration.CreatedUtc;
                changed = true;
            }
            if (registration.ExpiresUtc == default)
            {
                registration.ExpiresUtc = registration.LastHeartbeatUtc.Add(options.ProjectIntentDuration);
                changed = true;
            }
        }
        if (state.ProfileMode == ModProfile.LegacyMode &&
            (state.RequestedProjects.Count > 0 || state.ResolvedProjectPackageIds.Count > 0 ||
             state.ResolvedMods.Count > 0 ||
              !string.IsNullOrWhiteSpace(state.ProfileFingerprint)))
        {
            QuarantineInvalidProfileLocked(new ProfileException("PROFILE_INVALID_STATE",
                "Persisted legacy state contains an accepted non-legacy profile."));
        }
        if (string.IsNullOrWhiteSpace(state.BaselineFingerprint))
        {
            string baselineFingerprint = ReadBaselineFingerprintLocked();
            if (!string.IsNullOrWhiteSpace(baselineFingerprint))
            {
                state.BaselineFingerprint = baselineFingerprint;
                changed = true;
            }
        }

        else
        {
            string sidecarFingerprint = ReadBaselineFingerprintLocked();
            if (state.ProfileMode != ModProfile.LegacyMode && string.IsNullOrWhiteSpace(sidecarFingerprint))
            {
                QuarantineInvalidProfileLocked(new ProfileException("PROFILE_BASELINE_MISSING",
                    "The accepted profile has no durable baseline sidecar."));
            }
            else if (!string.IsNullOrWhiteSpace(sidecarFingerprint) &&
                !string.Equals(sidecarFingerprint, state.BaselineFingerprint, StringComparison.Ordinal))
            {
                if (state.ProfileMode != ModProfile.LegacyMode || state.RestartPending)
                    QuarantineInvalidProfileLocked(new ProfileException("PROFILE_BASELINE_CHANGED",
                        "The captured baseline sidecar no longer matches its persisted fingerprint."));
                else
                {
                    // A crash can occur after the durable baseline sidecar is replaced but
                    // before state.json records its fingerprint. With no accepted profile,
                    // the sidecar is the authoritative explicit baseline capture.
                    state.BaselineFingerprint = sidecarFingerprint;
                    changed = true;
                }
            }
        }

        if (state.LastKnownGoodProfile == null && !string.IsNullOrWhiteSpace(state.BaselineFingerprint))
        {
            try
            {
                state.LastKnownGoodProfile = PersistedProfileSnapshot.FromModProfile(
                    CreateBaselineProfileForMode(state.BaselineFingerprint));
                changed = true;
            }
            catch (ProfileException)
            {
                // The normal profile validation below remains authoritative. Do not
                // manufacture a control profile when the durable baseline is invalid.
            }
        }
        if (state.RuntimeProfile == null && state.ProfileMode != ModProfile.LegacyMode)
        {
            try
            {
                state.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(ProfileFromStateLocked());
                changed = true;
            }
            catch
            {
                // Invalid accepted profile state is quarantined below.
            }
        }

        if (state.ProfileMode != ModProfile.LegacyMode && state.ErrorCode != "PROFILE_INVALID_STATE" &&
            state.ErrorCode != "PROFILE_BASELINE_CHANGED")
        {
            try
            {
                ModProfileResolver.ValidateResolvedProfile(ProfileFromStateLocked());
            }
            catch (ProfileException exception)
            {
                QuarantineInvalidProfileLocked(exception);
            }
        }
        foreach (TestLease lease in state.Leases.Where(value => value != null))
        {
            if (lease.LastHeartbeatUtc == default)
            {
                lease.LastHeartbeatUtc = lease.StartedUtc;
                changed = true;
            }
        }
        state.Phase = Enum.IsDefined(state.Phase) ? state.Phase : BridgePhase.STOPPED;
        if (string.Equals(state.ErrorCode, "WAITING_FOR_BRIDGE_EXPIRED", StringComparison.Ordinal) &&
            state.RequiresNewProcess && state.LaunchAttemptCount == 0)
        {
            state.TargetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
            state.RestartPending = true;
            state.RestartRequestedUtc ??= clock.UtcNow;
            state.Phase = BridgePhase.WAITING_FOR_BRIDGE;
            state.Error = null;
            state.ErrorCode = null;
            state.LaunchOwner = "coordinator@" + runtimeSlotId;
            state.LaunchRequestKey = "recovered-wait-" + state.TargetGeneration;
            state.WaitingForBridgeDeadlineUtc = null;
            changed = true;
        }
        else if (state.RestartPending && state.WaitingForBridgeDeadlineUtc.HasValue)
        {
            state.WaitingForBridgeDeadlineUtc = null;
            changed = true;
        }
        if (state.RestartPending && state.TargetGeneration <= state.Generation)
            state.TargetGeneration = state.Generation + 1;
        if (state.Phase == BridgePhase.READY && state.Generation <= 0)
            state.Phase = BridgePhase.STOPPED;
        if (state.LaunchBudgetRemaining < 0)
        {
            state.LaunchBudgetRemaining = 0;
            changed = true;
        }
        if (state.LaunchAttemptCount < 0)
        {
            state.LaunchAttemptCount = 0;
            changed = true;
        }
        if (state.Generation == 0 && state.LaunchAttemptCount == 0 && state.LaunchBudgetRemaining == 0)
        {
            state.LaunchBudgetRemaining = Math.Max(1, options.MaxLaunchAttempts);
            changed = true;
        }
        if (RecoverModsConfigTransitionLocked())
            changed = true;
        if (RefreshRimBridgePolicyStateLocked())
            changed = true;
        if (changed)
            SaveStateLocked();
    }

    private bool RecoverModsConfigTransitionLocked()
    {
        if (state.ModsConfigMutationAuthority != ModsConfigMutationAuthorityValues.DevBridgeTransition ||
            state.ExternalModsConfigMutation != null)
            return false;

        string observed = ReadModsConfigFingerprintLocked();
        if (!string.IsNullOrWhiteSpace(state.ModsConfigGeneratedHash) &&
            string.Equals(observed, state.ModsConfigGeneratedHash, StringComparison.OrdinalIgnoreCase))
        {
            state.ModsConfigOwnership = "DEVBRIDGE_GENERATED";
            state.ModsConfigMutationAuthority = ModsConfigMutationAuthorityValues.ControlledFrozen;
            ClearModsConfigTransitionRecoveryLocked();
            return true;
        }

        // An atomic write either left the source untouched or installed the
        // target. If neither durable fingerprint matches, the file changed
        // outside the transition and must remain untrusted.
        if (!string.IsNullOrWhiteSpace(state.ModsConfigTransitionSourceFingerprint) &&
            string.Equals(observed, state.ModsConfigTransitionSourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            AbortModsConfigTransitionLocked();
            return true;
        }

        RecordExternalModsConfigMutationLocked(
            state.ModsConfigGeneratedGeneration > 0
                ? state.ModsConfigGeneratedGeneration
                : state.Generation,
            observed);
        return true;
    }

    private bool NormalizeRimBridgeStateLocked()
    {
        bool changed = false;
        RimBridgeMode configuredMode = options.RimBridgeMode;
        if (state.RimBridge == null)
        {
            state.RimBridge = RimBridgeIntegrationState.Disabled(configuredMode);
            changed = true;
        }

        state.RimBridge.PackageId = RimBridgeIntegrationConstants.PackageId;
        string expectedMode = RimBridgeModes.Text(configuredMode);
        string persistedMode = state.RimBridge.ConfiguredMode;
        RimBridgeMode parsedPersisted = RimBridgeMode.Off;
        try { parsedPersisted = RimBridgeModes.Parse(persistedMode); }
        catch { changed = true; }

        if (!string.Equals(persistedMode, expectedMode, StringComparison.OrdinalIgnoreCase) ||
            parsedPersisted != configuredMode)
        {
            InvalidateRimBridgeEndpointLocked("RimBridge mode changed; a fresh generation is required.",
                "RIMBRIDGE_MODE_CHANGED");
            state.RimBridge.ConfiguredMode = expectedMode;
            if (state.Phase == BridgePhase.READY || state.RestartPending)
            {
                state.Phase = BridgePhase.ERROR;
                state.ErrorCode = "RIMBRIDGE_MODE_CHANGED";
                state.Error = "RimBridge mode changed; restart is required before readiness can be trusted.";
                state.RequiresNewProcess = true;
            }
            changed = true;
        }
        else if (!string.Equals(state.RimBridge.ConfiguredMode, expectedMode, StringComparison.Ordinal))
        {
            state.RimBridge.ConfiguredMode = expectedMode;
            changed = true;
        }

        if (configuredMode == RimBridgeMode.Off &&
            state.RimBridge.LifecycleState != RimBridgeLifecycleState.DISABLED)
        {
            InvalidateRimBridgeEndpointLocked("RimBridge integration is disabled by configuration.", null);
            changed = true;
        }

        return changed;
    }

    private static RimBridgeMode ParsePersistedRimBridgeMode(RimBridgeMode value) => value;

    private void InvalidateRimBridgeEndpointLocked(string reason, string code)
    {
        state.RimBridge ??= RimBridgeIntegrationState.Disabled(options.RimBridgeMode);
        RimBridgeEndpointStore.Delete(runtimeRoot);
        state.RimBridge.Host = null;
        state.RimBridge.Port = 0;
        state.RimBridge.TokenAvailable = false;
        state.RimBridge.LaunchId = null;
        state.RimBridge.Generation = 0;
        state.RimBridge.ProcessId = 0;
        state.RimBridge.ProcessStartUtcTicks = 0;
        state.RimBridge.DiscoveryTimestampUtc = null;
        state.RimBridge.LastVerificationTimestampUtc = null;
        state.RimBridge.LogBoundaryTimestampUtc = null;
        state.RimBridge.LogBoundaryPosition = 0;
        state.RimBridge.LogExistedAtBoundary = false;
        state.RimBridge.LogBoundaryAuthoritative = false;
        state.RimBridge.LogBoundaryPrefixLength = 0;
        state.RimBridge.LogBoundaryCreationUtcTicks = 0;
        state.RimBridge.LogBoundaryPrefixHash = null;
        state.RimBridge.CompanionAvailable = false;
        state.RimBridge.CompanionVerified = false;
        state.RimBridge.CompanionToolName = null;
        state.RimBridge.CompanionLaunchId = null;
        state.RimBridge.CompanionGeneration = 0;
        state.RimBridge.CompanionProcessId = 0;
        state.RimBridge.CompanionVerificationTimestampUtc = null;
        state.RimBridge.CompanionErrorCode = null;
        state.RimBridge.CompanionError = null;
        state.RimBridge.CompanionDiagnosticCode = null;
        state.RimBridge.CompanionDiagnosticReason = null;
        if (options.RimBridgeMode == RimBridgeMode.Off)
        {
            state.RimBridge.ErrorCode = null;
            state.RimBridge.Error = null;
            state.RimBridge.LifecycleState = RimBridgeLifecycleState.DISABLED;
        }
        else
        {
            state.RimBridge.ErrorCode = code;
            state.RimBridge.Error = reason;
            state.RimBridge.LifecycleState = RimBridgeLifecycleState.STALE;
        }
    }

    private void SetRimBridgeProfileStateLocked(ModProfile profile)
    {
        state.RimBridge ??= RimBridgeIntegrationState.Disabled(options.RimBridgeMode);
        state.RimBridge.ConfiguredMode = RimBridgeModes.Text(options.RimBridgeMode);
        state.RimBridge.PackageId = RimBridgeIntegrationConstants.PackageId;
        state.RimBridge.Version = profile?.RimBridgeVersion;
        state.RimBridge.ErrorCode = profile?.RimBridgeResolutionErrorCode;
        state.RimBridge.Error = profile?.RimBridgeResolutionError;
        if (options.RimBridgeMode == RimBridgeMode.Off)
            state.RimBridge.LifecycleState = RimBridgeLifecycleState.DISABLED;
        else if (profile?.RimBridgeResolutionErrorCode == "RIMBRIDGE_NOT_INSTALLED")
            state.RimBridge.LifecycleState = RimBridgeLifecycleState.NOT_INSTALLED;
        else if (state.RimBridge.LifecycleState == RimBridgeLifecycleState.DISABLED)
            state.RimBridge.LifecycleState = RimBridgeLifecycleState.WAITING;
    }

    private void BeginRimBridgeLaunchLocked(string launchId, int targetGeneration, ModProfile profile)
    {
        InvalidateRimBridgeEndpointLocked("A new RimWorld generation is starting.",
            "RIMBRIDGE_GENERATION_CHANGED");
        state.RimBridge.ConfiguredMode = RimBridgeModes.Text(options.RimBridgeMode);
        state.RimBridge.PackageId = RimBridgeIntegrationConstants.PackageId;
        state.RimBridge.LaunchId = launchId;
        state.RimBridge.Generation = targetGeneration;
        state.RimBridge.ProcessId = 0;
        state.RimBridge.ProcessStartUtcTicks = 0;
        state.RimBridge.LifecycleState = options.RimBridgeMode == RimBridgeMode.Off
            ? RimBridgeLifecycleState.DISABLED
            : RimBridgeLifecycleState.WAITING;
        state.RimBridge.Version = profile?.RimBridgeVersion ?? state.RimBridge.Version;
        state.RimBridge.ErrorCode = profile?.RimBridgeResolutionErrorCode;
        state.RimBridge.Error = profile?.RimBridgeResolutionError;
    }

    private PersistedState CloneStateLocked()
    {
        string json = JsonSerializer.Serialize(state, CoordinatorSerialization.JsonOptions);
        return JsonSerializer.Deserialize<PersistedState>(json, CoordinatorSerialization.JsonOptions) ?? new PersistedState();
    }

    private void SaveStateLocked()
    {
        if (persistedStateLoadBlocked)
            return;

        UpdateAgentJournalLocked();
        TracePhaseTransitionIfNeededLocked();
        long started = Stopwatch.GetTimestamp();
        TraceEvent("state.save.started");
        try
        {
            Directory.CreateDirectory(runtimeRoot);
            InjectFaultForTesting(CoordinatorFaultPoint.BeforeDurableStateWrite);
            AtomicWriteFile(statePath, Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(state, CoordinatorSerialization.JsonOptions)),
                beforeReplacement: () => InjectFaultForTesting(
                    CoordinatorFaultPoint.AfterStateTempFileWriteBeforeAtomicReplacement),
                afterReplacement: () => InjectFaultForTesting(
                    CoordinatorFaultPoint.AfterStateDurableReplacement));
            TraceEvent("state.save.completed", durationMs: ElapsedMilliseconds(started),
                success: true);
            Monitor.PulseAll(gate);
        }
        catch (Exception exception)
        {
            TraceEvent("state.save.failed", durationMs: ElapsedMilliseconds(started),
                success: false, errorCode: TraceExceptionCategory(exception));
            throw;
        }
    }

    private ModProfile ProfileFromStateLocked()
    {
        if (state.ProfileMode == ModProfile.LegacyMode)
            return null;
        return new ModProfile
        {
            Mode = state.ProfileMode,
            RequestedProjects = (state.RequestedProjects ?? new List<string>()).ToList(),
            ResolvedProjectPackageIds = (state.ResolvedProjectPackageIds ?? new List<string>()).ToList(),
            ResolvedMods = (state.ResolvedMods ?? new List<string>()).ToList(),
            TestInputs = TestGenerationInputs.CloneValues(state.TestInputs),
            ProfileFingerprint = state.ProfileFingerprint,
            BaselineFingerprint = state.BaselineFingerprint,
            RimBridgeMode = ParsePersistedRimBridgeMode(state.RimBridgeMode),
            RimBridgeVersion = state.RimBridgeVersion,
            RimBridgeResolutionErrorCode = state.RimBridgeResolutionErrorCode,
            RimBridgeResolutionError = state.RimBridgeResolutionError
        };
    }

    private void SetActiveProfileLocked(ModProfile profile)
    {
        if (profile == null)
        {
            ClearActiveProfileLocked();
            return;
        }

        state.ProfileMode = profile.Mode;
        state.RequestedProjects = profile.RequestedProjects.ToList();
        state.ResolvedProjectPackageIds = profile.ResolvedProjectPackageIds.ToList();
        state.ResolvedMods = profile.ResolvedMods.ToList();
        state.TestInputs = TestGenerationInputs.CloneValues(profile.TestInputs);
        state.ProfileFingerprint = profile.ProfileFingerprint;
        state.BaselineFingerprint = profile.BaselineFingerprint;
        state.RimBridgeMode = profile.RimBridgeMode;
        state.RimBridgeVersion = profile.RimBridgeVersion;
        state.RimBridgeResolutionErrorCode = profile.RimBridgeResolutionErrorCode;
        state.RimBridgeResolutionError = profile.RimBridgeResolutionError;
        SetRimBridgeProfileStateLocked(profile);
        state.LaunchProfileMode = profile.Mode == ModProfile.BaselineMode
            ? "aggregate-minimal-control" : "aggregate-projects";
    }

    private void ClearActiveProfileLocked()
    {
        state.ProfileMode = ModProfile.LegacyMode;
        state.RequestedProjects = new List<string>();
        state.ResolvedProjectPackageIds = new List<string>();
        state.ResolvedMods = new List<string>();
        state.TestInputs = new List<TestInputValue>();
        state.ProfileFingerprint = null;
        state.RimBridgeMode = RimBridgeMode.Off;
        state.RimBridgeVersion = null;
        state.RimBridgeResolutionErrorCode = null;
        state.RimBridgeResolutionError = null;
        state.LaunchProfileMode = "explicit-human-legacy";
    }

    private void RecordProfileError(string code, string message)
    {
        lock (gate)
            RecordProfileErrorLocked(code, message);
    }

    private void RecordProfileErrorLocked(string code, string message)
    {
        state.ProfileErrorCode = code;
        state.ProfileError = message;
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void QuarantineInvalidProfileLocked(ProfileException exception)
    {
        state.ProfileErrorCode = exception.Code;
        state.ProfileError = exception.Message;
        state.ErrorCode = exception.Code;
        state.Error = exception.Message;
        state.Phase = BridgePhase.ERROR;
        state.RestartPending = false;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.WaitingForBridgeDeadlineUtc = null;
        state.RequiresNewProcess = false;
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void EmitProfile(PersistedState snapshot, Action<string> emit)
    {
        emit("Profile mode: " + (snapshot.LaunchProfileMode ??
            (string.IsNullOrWhiteSpace(snapshot.ProfileMode) ? ModProfile.LegacyMode : snapshot.ProfileMode)));
        emit("Resolver profile mode: " + (string.IsNullOrWhiteSpace(snapshot.ProfileMode) ? ModProfile.LegacyMode : snapshot.ProfileMode));
        emit("Requested projects: " +
            (snapshot.RequestedProjects == null || snapshot.RequestedProjects.Count == 0
                ? "none" : string.Join(", ", snapshot.RequestedProjects)));
        emit("Resolved project package IDs: " +
            (snapshot.ResolvedProjectPackageIds == null || snapshot.ResolvedProjectPackageIds.Count == 0
                ? "none" : string.Join(", ", snapshot.ResolvedProjectPackageIds)));
        emit("Resolved mods (load order): " +
            (snapshot.ResolvedMods == null || snapshot.ResolvedMods.Count == 0
                ? "none" : string.Join(" -> ", snapshot.ResolvedMods)));
        emit("Profile fingerprint: " + (snapshot.ProfileFingerprint ?? "none"));
        emit("Baseline fingerprint: " + (snapshot.BaselineFingerprint ?? "none"));
        emit("ModsConfig ownership: " + (snapshot.ModsConfigOwnership ?? "UNKNOWN"));
        EmitRimBridgePolicyStatus(snapshot.RimBridgePolicy, snapshot.ExternalModsConfigMutation, emit);
    }

    private static void EmitRimBridgePolicyStatus(RimBridgePolicyState policy,
        ModsConfigMutationEvidence evidence, Action<string> emit)
    {
        policy ??= RimBridgePolicyState.CreateDefault();
        emit("RimBridge control policy: lifecycleOwner=" + policy.LifecycleOwner +
            ", modsConfigOwner=" + policy.ModsConfigOwner +
            ", generationOwner=" + policy.GenerationOwner);
        emit("Generation policy: currentGeneration=" + policy.CurrentGeneration +
            ", generationOwned=" + policy.GenerationOwned.ToString().ToLowerInvariant() +
            ", profileFrozen=" + policy.ProfileFrozen.ToString().ToLowerInvariant() +
            ", mutationAuthority=" + policy.ModsConfigMutationAuthority);
        emit("RimBridge operations blocked while DevBridge owns the generation: " +
            (policy.BlockedOperations == null || policy.BlockedOperations.Count == 0
                ? "none" : string.Join(", ", policy.BlockedOperations)));
        if (evidence != null)
        {
            emit("PROFILE_EXTERNAL_MUTATION evidence: generation=" + evidence.Generation +
                ", launchId=" + (evidence.LaunchId ?? "none") +
                ", expectedFingerprint=" + (evidence.ExpectedFingerprint ?? "none") +
                ", observedFingerprint=" + (evidence.ObservedFingerprint ?? "none") +
                ", detectedUtc=" + evidence.DetectedUtc.ToUniversalTime().ToString("O"));
            emit("The accepted generation is no longer trustworthy; the changed file was not absorbed. " +
                "Maintenance/profile reconciliation is required before another launch.");
        }
    }

}
