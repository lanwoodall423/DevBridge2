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
    private bool CanChangeModsConfigLocked(Action<string> emit)
    {
        if (state.RestartPending || state.Phase == BridgePhase.DRAINING ||
            state.Phase == BridgePhase.RESTARTING || state.Phase == BridgePhase.LOADING)
        {
            emit("ModsConfig change denied: a restart or launch is already pending.");
            emit("Error code: PROFILE_RESTART_PENDING");
            return false;
        }
        if (state.Leases.Count > 0)
        {
            emit("ModsConfig change denied: active test leases still exist.");
            emit("Error code: PROFILE_LEASES_ACTIVE");
            return false;
        }

        try
        {
            ProcessStatusSnapshot processes = EnumerateStatusProcessesLocked();
            if (processes.MatchingProcessCount > 0)
            {
                emit("ModsConfig change denied: RimWorld is still running.");
                emit("Error code: PROFILE_PROCESS_RUNNING");
                return false;
            }
            return true;
        }
        catch (ProcessInspectionException)
        {
            RecordProfileErrorLocked(ProcessInspection.ErrorCode, ProcessInspection.Message);
            emit("ModsConfig change denied: " + ProcessInspection.Message);
            emit("Error code: " + ProcessInspection.ErrorCode);
            return false;
        }
    }

    private string ReadBaselineFingerprintLocked()
    {
        try
        {
            return File.Exists(baselinePath) ? HashBytes(File.ReadAllBytes(baselinePath)) : null;
        }
        catch
        {
            return null;
        }
    }

    private string CurrentModsConfigOwnershipLocked()
    {
        if (!File.Exists(modsConfigPath))
            return "MISSING";
        try
        {
            byte[] bytes = File.ReadAllBytes(modsConfigPath);
            return CurrentModsConfigOwnershipLocked(bytes, HashBytes(bytes));
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    private string CurrentModsConfigOwnershipLocked(byte[] contents, string fingerprint,
        bool recordManifestErrors = true)
    {
        if (state.ExternalModsConfigMutation != null ||
            state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ExternalMutated)
            return ModsConfigMutationAuthorityValues.ExternalMutated;

        GeneratedModsConfigManifest generatedManifest = ReadGeneratedModsConfigManifestLocked(
            out bool manifestPresent, recordManifestErrors);
        if (string.Equals(state.ModsConfigGeneratedHash, fingerprint, StringComparison.Ordinal) ||
            string.Equals(generatedManifest?.Hash, fingerprint, StringComparison.OrdinalIgnoreCase))
            return state.ModsConfigOwnership == "DEVBRIDGE_PENDING" ? "DEVBRIDGE_PENDING" : "DEVBRIDGE_GENERATED";
        string baselineFingerprint = ReadBaselineFingerprintLocked() ?? state.BaselineFingerprint;
        if (!string.IsNullOrWhiteSpace(baselineFingerprint) &&
            string.Equals(baselineFingerprint, fingerprint, StringComparison.Ordinal))
            return "BASELINE";
        if (manifestPresent && generatedManifest == null)
            return "UNKNOWN";
        if (!string.IsNullOrWhiteSpace(state.ModsConfigGeneratedHash))
            return "USER_EDIT";
        return "USER";
    }

    private bool RefreshRimBridgePolicyStateLocked()
    {
        state.RimBridgePolicy ??= RimBridgePolicyState.CreateDefault();
        RimBridgePolicyState policy = state.RimBridgePolicy;
        bool changed = false;
        if (!string.Equals(policy.LifecycleOwner, "devbridge", StringComparison.Ordinal))
        {
            policy.LifecycleOwner = "devbridge";
            changed = true;
        }
        if (!string.Equals(policy.ModsConfigOwner, "devbridge", StringComparison.Ordinal))
        {
            policy.ModsConfigOwner = "devbridge";
            changed = true;
        }
        if (!string.Equals(policy.GenerationOwner, "devbridge", StringComparison.Ordinal))
        {
            policy.GenerationOwner = "devbridge";
            changed = true;
        }

        List<string> blocked = RimBridgePolicyState.DefaultBlockedOperations();
        if (policy.BlockedOperations == null || !policy.BlockedOperations.SequenceEqual(blocked, StringComparer.Ordinal))
        {
            policy.BlockedOperations = blocked;
            changed = true;
        }
        Dictionary<string, string> categories = RimBridgePolicyState.DefaultOperationCategories();
        if (policy.OperationCategories == null || policy.OperationCategories.Count != categories.Count ||
            categories.Any(value => !policy.OperationCategories.TryGetValue(value.Key, out string actual) ||
                !string.Equals(actual, value.Value, StringComparison.Ordinal)))
        {
            policy.OperationCategories = categories;
            changed = true;
        }

        bool generationOwned = state.Phase == BridgePhase.READY && state.Generation > 0 &&
            state.ExternalModsConfigMutation == null;
        bool profileFrozen = state.ProfileMode != ModProfile.LegacyMode &&
            !string.IsNullOrWhiteSpace(state.ProfileFingerprint) &&
            state.ExternalModsConfigMutation == null;
        bool external = state.ExternalModsConfigMutation != null ||
            state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ExternalMutated;
        bool generatedConfigOwned = state.ModsConfigGeneratedGeneration > 0 &&
            !string.IsNullOrWhiteSpace(state.ModsConfigGeneratedHash);
        string authority = external
            ? ModsConfigMutationAuthorityValues.ExternalMutated
            : state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.DevBridgeTransition
                ? ModsConfigMutationAuthorityValues.DevBridgeTransition
                : generatedConfigOwned
                    ? ModsConfigMutationAuthorityValues.ControlledFrozen
                    : ModsConfigMutationAuthorityValues.NotGenerationOwned;

        if (state.ModsConfigMutationAuthority != authority)
        {
            state.ModsConfigMutationAuthority = authority;
            changed = true;
        }
        if (policy.CurrentGeneration != state.Generation)
        {
            policy.CurrentGeneration = state.Generation;
            changed = true;
        }
        if (policy.GenerationOwned != generationOwned)
        {
            policy.GenerationOwned = generationOwned;
            changed = true;
        }
        if (policy.ProfileFrozen != profileFrozen)
        {
            policy.ProfileFrozen = profileFrozen;
            changed = true;
        }
        if (!string.Equals(policy.ModsConfigMutationAuthority, authority, StringComparison.Ordinal))
        {
            policy.ModsConfigMutationAuthority = authority;
            changed = true;
        }
        return changed;
    }

    private void BeginModsConfigTransitionLocked(string targetFingerprint = null,
        string targetProfileFingerprint = null, int targetGeneration = 0,
        bool allowExternalMutationReconciliation = false)
    {
        if (!allowExternalMutationReconciliation && (state.ExternalModsConfigMutation != null ||
            state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ExternalMutated)
            )
            throw new ProfileException("PROFILE_EXTERNAL_MUTATION",
                ExternalModsConfigMutationMessage(state.ExternalModsConfigMutation) +
                " Reconcile the baseline/profile explicitly before changing ModsConfig.xml.");

        if (state.ModsConfigMutationAuthority != ModsConfigMutationAuthorityValues.DevBridgeTransition)
        {
            state.ModsConfigTransitionPreviousAuthority = state.ModsConfigMutationAuthority;
            state.ModsConfigTransitionPreviousOwnership = state.ModsConfigOwnership;
            state.ModsConfigTransitionPreviousHash = state.ModsConfigGeneratedHash;
            state.ModsConfigTransitionPreviousProfileFingerprint = state.ModsConfigGeneratedProfileFingerprint;
            state.ModsConfigTransitionPreviousGeneration = state.ModsConfigGeneratedGeneration;
            state.ModsConfigTransitionSourceFingerprint = ReadModsConfigFingerprintLocked();
        }
        state.ModsConfigMutationAuthority = ModsConfigMutationAuthorityValues.DevBridgeTransition;
        if (!string.IsNullOrWhiteSpace(targetFingerprint))
        {
            state.ModsConfigOwnership = "DEVBRIDGE_PENDING";
            state.ModsConfigGeneratedHash = targetFingerprint;
            state.ModsConfigGeneratedProfileFingerprint = targetProfileFingerprint;
            state.ModsConfigGeneratedGeneration = targetGeneration;
        }
        RefreshRimBridgePolicyStateLocked();
        SaveStateLocked();
        InjectFaultForTesting(CoordinatorFaultPoint.DuringModsConfigTransition);
    }

    private void CompleteModsConfigTransitionLocked()
    {
        if (state.ExternalModsConfigMutation != null)
            return;
        if (!string.IsNullOrWhiteSpace(state.ModsConfigGeneratedHash) &&
            state.ModsConfigGeneratedGeneration > 0)
            state.ModsConfigOwnership = "DEVBRIDGE_GENERATED";
        state.ModsConfigMutationAuthority = ModsConfigMutationAuthorityValues.ControlledFrozen;
        ClearModsConfigTransitionRecoveryLocked();
        RefreshRimBridgePolicyStateLocked();
        SaveStateLocked();
    }

    private void AbortModsConfigTransitionLocked()
    {
        if (state.ExternalModsConfigMutation != null)
            return;
        bool hadPrevious = !string.IsNullOrWhiteSpace(state.ModsConfigTransitionPreviousOwnership) ||
            !string.IsNullOrWhiteSpace(state.ModsConfigTransitionPreviousHash) ||
            state.ModsConfigTransitionPreviousGeneration > 0;
        if (hadPrevious)
        {
            state.ModsConfigOwnership = state.ModsConfigTransitionPreviousOwnership;
            state.ModsConfigGeneratedHash = state.ModsConfigTransitionPreviousHash;
            state.ModsConfigGeneratedProfileFingerprint = state.ModsConfigTransitionPreviousProfileFingerprint;
            state.ModsConfigGeneratedGeneration = state.ModsConfigTransitionPreviousGeneration;
            state.ModsConfigMutationAuthority = state.ModsConfigTransitionPreviousAuthority;
        }
        else
        {
            state.ModsConfigOwnership = null;
            state.ModsConfigGeneratedHash = null;
            state.ModsConfigGeneratedProfileFingerprint = null;
            state.ModsConfigGeneratedGeneration = 0;
            state.ModsConfigMutationAuthority = ModsConfigMutationAuthorityValues.NotGenerationOwned;
        }
        ClearModsConfigTransitionRecoveryLocked();
        RefreshRimBridgePolicyStateLocked();
        SaveStateLocked();
    }

    private void ClearModsConfigTransitionRecoveryLocked()
    {
        state.ModsConfigTransitionSourceFingerprint = null;
        state.ModsConfigTransitionPreviousAuthority = null;
        state.ModsConfigTransitionPreviousOwnership = null;
        state.ModsConfigTransitionPreviousHash = null;
        state.ModsConfigTransitionPreviousProfileFingerprint = null;
        state.ModsConfigTransitionPreviousGeneration = 0;
    }

    private string ReadModsConfigFingerprintLocked()
    {
        try
        {
            return File.Exists(modsConfigPath)
                ? HashBytes(File.ReadAllBytes(modsConfigPath))
                : "MISSING";
        }
        catch
        {
            return "UNAVAILABLE";
        }
    }

    private bool DetectExternalModsConfigMutationLocked(bool allowTransition = false,
        int generationOverride = 0)
    {
        if (state.ExternalModsConfigMutation != null ||
            state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ExternalMutated)
        {
            state.ModsConfigMutationAuthority = ModsConfigMutationAuthorityValues.ExternalMutated;
            RefreshRimBridgePolicyStateLocked();
            return true;
        }
        if (!allowTransition &&
            state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.DevBridgeTransition)
            return false;
        int generation = generationOverride > 0
            ? generationOverride
            : state.ModsConfigGeneratedGeneration > 0
                ? state.ModsConfigGeneratedGeneration
                : state.Generation;
        if (generation <= 0 || string.IsNullOrWhiteSpace(state.ModsConfigGeneratedHash))
            return false;

        string observedFingerprint = ReadModsConfigFingerprintLocked();
        if (string.Equals(observedFingerprint, state.ModsConfigGeneratedHash,
                StringComparison.OrdinalIgnoreCase))
            return false;

        RecordExternalModsConfigMutationLocked(generation, observedFingerprint);
        return true;
    }

    private void RecordExternalModsConfigMutationLocked(int generation, string observedFingerprint)
    {
        DateTime detectedUtc = clock.UtcNow.ToUniversalTime();
        string expectedFingerprint = state.ModsConfigGeneratedHash;
        state.ExternalModsConfigMutation = new ModsConfigMutationEvidence
        {
            Generation = generation,
            LaunchId = state.LaunchId,
            ExpectedFingerprint = expectedFingerprint,
            ObservedFingerprint = observedFingerprint,
            ExpectedProfileFingerprint = state.ModsConfigGeneratedProfileFingerprint ?? state.ProfileFingerprint,
            DetectedUtc = detectedUtc,
            Reason = "ModsConfig.xml no longer matches the DevBridge-generated fingerprint."
        };
        state.ModsConfigMutationAuthority = ModsConfigMutationAuthorityValues.ExternalMutated;
        state.Phase = BridgePhase.ERROR;
        state.RestartPending = false;
        state.RestartRequestedUtc = null;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.WaitingForBridgeDeadlineUtc = null;
        state.MaintenanceReady = false;
        state.RequiresNewProcess = true;
        state.ErrorCode = "PROFILE_EXTERNAL_MUTATION";
        state.ProfileErrorCode = "PROFILE_EXTERNAL_MUTATION";
        state.Error = ExternalModsConfigMutationMessage(state.ExternalModsConfigMutation);
        state.ProfileError = state.Error;
        RefreshRimBridgePolicyStateLocked();
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private static string ExternalModsConfigMutationMessage(ModsConfigMutationEvidence evidence)
    {
        if (evidence == null)
            return "PROFILE_EXTERNAL_MUTATION: ModsConfig.xml is marked externally mutated and the accepted generation is no longer trustworthy.";
        return "PROFILE_EXTERNAL_MUTATION: the accepted generation is no longer trustworthy. " +
            "DevBridge will not treat the observed ModsConfig.xml as part of the accepted profile. " +
            "Generation=" + evidence.Generation + ", launchId=" + (evidence.LaunchId ?? "none") +
            ", expectedFingerprint=" + (evidence.ExpectedFingerprint ?? "none") +
            ", observedFingerprint=" + (evidence.ObservedFingerprint ?? "none") +
            ", detectedUtc=" + evidence.DetectedUtc.ToUniversalTime().ToString("O") +
            ". Maintenance/profile reconciliation is required; no automatic restart was attempted.";
    }

    private bool ExternalMutationBlocksLaunchLocked(Action<string> emit, string operation)
    {
        DetectExternalModsConfigMutationLocked();
        if (state.ExternalModsConfigMutation == null &&
            state.ModsConfigMutationAuthority != ModsConfigMutationAuthorityValues.ExternalMutated)
            return false;

        if (emit != null)
        {
            emit(operation + " denied: " + ExternalModsConfigMutationMessage(state.ExternalModsConfigMutation));
            emit("The changed file was not blessed or absorbed into the accepted profile.");
            emit("No automatic restart or replacement launch was attempted.");
            emit("Maintenance/profile reconciliation is required before another generation can be accepted.");
            emit("Next action: run DevBridge.cmd mods status, then use the explicit baseline/profile maintenance workflow.");
            emit("Error code: PROFILE_EXTERNAL_MUTATION");
        }
        return true;
    }

    private void ClearExternalModsConfigMutationLocked()
    {
        state.ExternalModsConfigMutation = null;
        state.ModsConfigMutationAuthority = ModsConfigMutationAuthorityValues.NotGenerationOwned;
        ClearModsConfigTransitionRecoveryLocked();
        if (state.ErrorCode == "PROFILE_EXTERNAL_MUTATION")
        {
            state.ErrorCode = null;
            state.Error = null;
        }
        if (state.ProfileErrorCode == "PROFILE_EXTERNAL_MUTATION")
        {
            state.ProfileErrorCode = null;
            state.ProfileError = null;
        }
        RefreshRimBridgePolicyStateLocked();
    }

    private void ApplyProfile(ModProfile profile, int targetGeneration)
    {
        if (profile == null || profile.Mode == ModProfile.LegacyMode)
            return;
        ModProfileResolver.ValidateResolvedProfile(profile);
        lock (gate)
        {
            string baselineFingerprint = ReadBaselineFingerprintLocked();
            if (!string.Equals(baselineFingerprint, profile.BaselineFingerprint, StringComparison.Ordinal))
                throw new ProfileException("PROFILE_BASELINE_CHANGED",
                    "The captured baseline no longer matches the accepted profile; no ModsConfig change was made.");
        }
        if (!File.Exists(modsConfigPath))
            throw new ProfileException("PROFILE_MODS_CONFIG_MISSING",
                "ModsConfig.xml was not found at " + modsConfigPath + ".");

        byte[] current = File.ReadAllBytes(modsConfigPath);
        string currentFingerprint = HashBytes(current);
        string ownership;
        lock (gate)
        {
            ownership = CurrentModsConfigOwnershipLocked(current, currentFingerprint);
            if (state.ExternalModsConfigMutation != null ||
                state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ExternalMutated)
                throw new ProfileException("PROFILE_EXTERNAL_MUTATION",
                    ExternalModsConfigMutationMessage(state.ExternalModsConfigMutation) +
                    " Reconcile the baseline/profile explicitly before changing ModsConfig.xml.");
            if (ownership == "USER_EDIT" || ownership == "USER" || ownership == "UNKNOWN" || ownership == "MISSING")
                throw new ProfileException("MODS_CONFIG_EXTERNAL_EDIT",
                    "ModsConfig.xml differs from the captured baseline or known DevBridge output; capture the intentional edit before using a reduced profile.");
        }

        byte[] updated = RenderProfileModsConfig(current, profile.ResolvedMods);
        string updatedFingerprint = HashBytes(updated);
        lock (gate)
            BeginModsConfigTransitionLocked(updatedFingerprint, profile.ProfileFingerprint, targetGeneration);
        try
        {
            WriteGeneratedModsConfigManifest(updatedFingerprint, profile.ProfileFingerprint, targetGeneration);
        }
        catch (Exception exception)
        {
            lock (gate)
                AbortModsConfigTransitionLocked();
            throw new ProfileException("MODS_CONFIG_OWNERSHIP_WRITE_FAILED",
                "DevBridge could not durably record generated ModsConfig ownership: " + exception.Message);
        }
        options.BeforeModsConfigWrite?.Invoke();
        byte[] latest;
        try
        {
            latest = File.ReadAllBytes(modsConfigPath);
        }
        catch
        {
            throw new ProfileException("MODS_CONFIG_EXTERNAL_EDIT",
                "ModsConfig.xml changed or disappeared while preparing the profile write.");
        }
        if (!string.Equals(HashBytes(latest), currentFingerprint, StringComparison.Ordinal))
            throw new ProfileException("MODS_CONFIG_EXTERNAL_EDIT",
                "ModsConfig.xml changed while preparing the profile write; no user edit was overwritten.");
        EnsureNoMatchingRimWorldProcess();
        try
        {
            AtomicWriteFile(modsConfigPath, updated);
        }
        catch
        {
            lock (gate)
                AbortModsConfigTransitionLocked();
            throw;
        }
        lock (gate)
        {
            state.ModsConfigOwnership = "DEVBRIDGE_GENERATED";
            state.ModsConfigGeneratedHash = updatedFingerprint;
            state.ModsConfigGeneratedProfileFingerprint = profile.ProfileFingerprint;
            state.ModsConfigGeneratedGeneration = targetGeneration;
            state.BaselineFingerprint = profile.BaselineFingerprint;
            CompleteModsConfigTransitionLocked();
        }
    }

    private static byte[] RenderProfileModsConfig(byte[] contents, IReadOnlyList<string> packageIds)
    {
        XDocument document;
        try
        {
            using MemoryStream stream = new(contents, writable: false);
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception)
        {
            throw new ProfileException("PROFILE_MALFORMED_MODS_CONFIG",
                "ModsConfig.xml could not be parsed before profile application: " + exception.Message);
        }

        List<XElement> activeSections = document.Descendants().Where(value =>
            string.Equals(value.Name.LocalName, "activeMods", StringComparison.OrdinalIgnoreCase)).ToList();
        if (activeSections.Count != 1)
            throw new ProfileException("PROFILE_MALFORMED_MODS_CONFIG",
                "ModsConfig.xml must contain exactly one activeMods section before profile application.");

        XElement active = activeSections[0];
        string newline = contents.AsSpan().IndexOf((byte)'\r') >= 0 ? "\r\n" : "\n";
        active.RemoveNodes();
        active.Add(new XText(newline));
        foreach (string packageId in packageIds ?? Array.Empty<string>())
        {
            active.Add(new XElement("li", packageId));
            active.Add(new XText(newline));
        }

        return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
    }

    private static string HashBytes(byte[] contents) =>
        Convert.ToHexString(SHA256.HashData(contents ?? Array.Empty<byte>()));

    private GeneratedModsConfigManifest ReadGeneratedModsConfigManifestLocked(out bool present,
        bool recordErrors = true)
    {
        present = false;
        try
        {
            if (!File.Exists(generatedManifestPath))
                return null;
            present = true;
            GeneratedModsConfigManifest manifest = JsonSerializer.Deserialize<GeneratedModsConfigManifest>(
                File.ReadAllText(generatedManifestPath), CoordinatorSerialization.JsonOptions);
            if (manifest == null)
            {
                if (recordErrors)
                    RecordPersistedArtifactErrorLocked("GENERATED_MODS_CONFIG_MALFORMED",
                        "Runtime/ModsConfig.generated.json did not contain a manifest object.");
                return null;
            }
            if (manifest.SchemaVersion < 0)
            {
                if (recordErrors)
                    RecordPersistedArtifactErrorLocked("GENERATED_MODS_CONFIG_SCHEMA_INVALID",
                        "Runtime/ModsConfig.generated.json contains an invalid schema version: " +
                        manifest.SchemaVersion + ".");
                return null;
            }
            if (manifest.SchemaVersion > DevBridgeSchemaVersions.GeneratedModsConfig)
            {
                if (recordErrors)
                    RecordPersistedArtifactErrorLocked("GENERATED_MODS_CONFIG_SCHEMA_UNSUPPORTED",
                        "Runtime/ModsConfig.generated.json uses unsupported schema version " +
                        manifest.SchemaVersion + ".");
                return null;
            }
            if (!IsValidHash(manifest.Hash))
            {
                if (recordErrors)
                    RecordPersistedArtifactErrorLocked("GENERATED_MODS_CONFIG_MALFORMED",
                        "Runtime/ModsConfig.generated.json contains an invalid generated ModsConfig hash.");
                return null;
            }
            return manifest;
        }
        catch (JsonException exception)
        {
            if (recordErrors)
                RecordPersistedArtifactErrorLocked("GENERATED_MODS_CONFIG_MALFORMED",
                    "Runtime/ModsConfig.generated.json was invalid: " + exception.Message);
            return null;
        }
        catch (IOException exception)
        {
            if (recordErrors)
                RecordPersistedArtifactErrorLocked("GENERATED_MODS_CONFIG_READ_FAILED",
                    "Runtime/ModsConfig.generated.json could not be read: " + exception.Message);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            if (recordErrors)
                RecordPersistedArtifactErrorLocked("GENERATED_MODS_CONFIG_READ_FAILED",
                    "Runtime/ModsConfig.generated.json could not be read: " + exception.Message);
            return null;
        }
    }

    private void WriteGeneratedModsConfigManifest(string hash, string profileFingerprint, int generation)
    {
        if (!IsValidHash(hash))
            throw new InvalidDataException("the generated ModsConfig hash was invalid");
        GeneratedModsConfigManifest manifest = new()
        {
            SchemaVersion = DevBridgeSchemaVersions.GeneratedModsConfig,
            Hash = hash.ToUpperInvariant(),
            ProfileFingerprint = profileFingerprint,
            Generation = generation
        };
        long started = Stopwatch.GetTimestamp();
        TraceEvent("mods.manifest.write.started");
        try
        {
            AtomicWriteFile(generatedManifestPath,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, CoordinatorSerialization.JsonOptions)));
            TraceEvent("mods.manifest.write.completed",
                durationMs: ElapsedMilliseconds(started), success: true);
        }
        catch (Exception exception)
        {
            TraceEvent("mods.manifest.write.failed",
                durationMs: ElapsedMilliseconds(started), success: false,
                errorCode: TraceExceptionCategory(exception));
            throw;
        }
    }

    private void ClearGeneratedModsConfigManifestLocked()
    {
        try
        {
            if (File.Exists(generatedManifestPath))
                File.Delete(generatedManifestPath);
        }
        catch
        {
            // A stale manifest cannot claim the new baseline unless its hash matches it.
        }
    }

    private static bool IsValidHash(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private void EnsureNoMatchingRimWorldProcess(bool postTermination = false)
    {
        ProcessStatusSnapshot processes;
        try
        {
            lock (gate)
                processes = EnumerateStatusProcessesLocked();
        }
        catch (Exception exception)
        {
            if (postTermination)
                TraceEvent("post_termination.census.completed", success: false,
                    errorCode: exception is ProcessInspectionException
                        ? ProcessInspection.ErrorCode : "CENSUS_FAILED",
                    detail: "complete=false");
            throw;
        }
        if (processes.MatchingProcessCount > 0)
        {
            if (postTermination)
                TraceEvent("post_termination.census.completed", success: false,
                    errorCode: "PROCESS_PRESENT",
                    detail: "complete=true;matching=" + processes.MatchingProcessCount);
            throw new ProfileException("MODS_CONFIG_PROCESS_RUNNING",
                "a matching RimWorld process is running; ModsConfig.xml was not changed");
        }
        if (postTermination)
            TraceEvent("post_termination.census.completed", success: true,
                detail: "complete=true;matching=0");
    }

    private static void AtomicWriteFile(string path, byte[] contents,
        Action beforeReplacement = null, Action afterReplacement = null)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents ?? Array.Empty<byte>());
                stream.Flush(true);
            }

            beforeReplacement?.Invoke();

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporary, path, null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporary, path, true);
                }
                catch (IOException)
                {
                    File.Move(temporary, path, true);
                }
            }
            else
                File.Move(temporary, path);

            afterReplacement?.Invoke();
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private bool IsDevBridgeModEnabled()
    {
        try
        {
            if (!File.Exists(modsConfigPath))
                return false;
            string contents = File.ReadAllText(modsConfigPath);
            return contents.IndexOf("<li>" + DevBridgePackageId + "</li>", StringComparison.Ordinal) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureDevBridgeModEnabled()
    {
        if (IsDevBridgeModEnabled())
            return;

        if (!File.Exists(modsConfigPath))
            throw new InvalidOperationException("ModsConfig.xml was not found at " + modsConfigPath +
                "; enable lan.devbridge2 in RimWorld before using quicktest");

        byte[] originalBytes = File.ReadAllBytes(modsConfigPath);
        string originalFingerprint = HashBytes(originalBytes);
        string contents = File.ReadAllText(modsConfigPath);
        string normalized = contents.Replace("<li>Lan.DevBridge2</li>",
            "<li>" + DevBridgePackageId + "</li>", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(contents, normalized, StringComparison.Ordinal))
        {
            WriteModsConfig(normalized, originalFingerprint);
            return;
        }

        int activeModsEnd = contents.IndexOf("</activeMods>", StringComparison.OrdinalIgnoreCase);
        if (activeModsEnd < 0)
            throw new InvalidOperationException("ModsConfig.xml has no activeMods section at " + modsConfigPath);

        string entry = Environment.NewLine + "    <li>" + DevBridgePackageId + "</li>";
        string updated = contents.Insert(activeModsEnd, entry);
        WriteModsConfig(updated, originalFingerprint);
    }

    private void WriteModsConfig(string contents, string expectedSourceFingerprint)
    {
        options.BeforeModsConfigWrite?.Invoke();
        byte[] current;
        try
        {
            current = File.ReadAllBytes(modsConfigPath);
        }
        catch
        {
            throw new ProfileException("MODS_CONFIG_EXTERNAL_EDIT",
                "ModsConfig.xml changed or disappeared while preparing the DevBridge activation write.");
        }
        if (!string.Equals(HashBytes(current), expectedSourceFingerprint, StringComparison.Ordinal))
            throw new ProfileException("MODS_CONFIG_EXTERNAL_EDIT",
                "ModsConfig.xml changed while preparing the DevBridge activation write; no user edit was overwritten.");
        EnsureNoMatchingRimWorldProcess();
        byte[] updated = new UTF8Encoding(false).GetBytes(contents);
        string updatedFingerprint = HashBytes(updated);
        int generation;
        lock (gate)
        {
            generation = state.TargetGeneration > 0 ? state.TargetGeneration : state.Generation;
            BeginModsConfigTransitionLocked(updatedFingerprint, null, generation);
        }
        try
        {
            // Record the expected output before replacement so a crash after the config
            // swap cannot make generated content look like a user baseline.
            WriteGeneratedModsConfigManifest(updatedFingerprint, null, generation);
        }
        catch (Exception exception)
        {
            lock (gate)
                AbortModsConfigTransitionLocked();
            throw new ProfileException("MODS_CONFIG_OWNERSHIP_WRITE_FAILED",
                "DevBridge could not durably record generated ModsConfig ownership: " + exception.Message);
        }
        try
        {
            AtomicWriteFile(modsConfigPath, updated);
        }
        catch
        {
            lock (gate)
                AbortModsConfigTransitionLocked();
            throw;
        }
        lock (gate)
        {
            state.ModsConfigOwnership = "DEVBRIDGE_GENERATED";
            state.ModsConfigGeneratedHash = updatedFingerprint;
            state.ModsConfigGeneratedProfileFingerprint = null;
            state.ModsConfigGeneratedGeneration = generation;
            CompleteModsConfigTransitionLocked();
        }
    }

}
