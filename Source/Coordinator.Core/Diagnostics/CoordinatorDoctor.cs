using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using DevBridge2;

namespace DevBridge.Coordinator;

internal sealed partial class CoordinatorState
{
    private DoctorAuditReport RunDoctorAudit(BridgeRequest request)
    {
        DoctorAuditReport report = new();
        report.Components = ComponentVersions.Current;
        PersistedState initial = SnapshotForDoctor();
        ProcessStatusSnapshot processSnapshot = null;
        string modsOwnership = null;

        RunDoctorCheck(report, "Coordinator", () => InspectCoordinator(report, initial));
        RunDoctorCheck(report, "Compatibility", () => InspectComponents(report, initial));
        RunDoctorCheck(report, "Process", () => processSnapshot = InspectProcess(report, initial));
        RunDoctorCheck(report, "ModsConfig", () => modsOwnership = InspectModsConfig(report, initial));
        RunDoctorCheck(report, "Projects/Profile", () => InspectProjectsAndProfile(report, initial));
        RunDoctorCheck(report, "Generation", () => InspectGeneration(report, initial));
        GenerationHistoryView generationHistory = null;
        RunDoctorCheck(report, "Generation history", () => generationHistory =
            InspectGenerationHistory(report, initial));
        RunDoctorCheck(report, "Leases", () => InspectLeases(report, initial));
        RunDoctorCheck(report, "Readiness", () => InspectReadiness(report, initial));
        RunDoctorCheck(report, "Recovery", () => InspectRecovery(report, initial));
        RunDoctorCheck(report, "Permissions", () => InspectPermissions(report));

        PersistedState finalSnapshot = SnapshotForDoctor();
        report.GenerationHistory = generationHistory ?? BuildGenerationHistoryViewLocked(finalSnapshot.Generation);
        report.Identity = BuildIdentityContract(finalSnapshot, processSnapshot);
        report.OperationalState = BuildOperationalState(finalSnapshot, processSnapshot, modsOwnership,
            report.GenerationHistory, report.NextGenerationConfig);
        report.Complete();
        return report;
    }

    private PersistedState SnapshotForDoctor()
    {
        lock (gate)
        {
            try
            {
                return CloneStateLocked();
            }
            catch
            {
                return state;
            }
        }
    }

    private static void RunDoctorCheck(DoctorAuditReport report, string component, Action check)
    {
        try
        {
            check();
        }
        catch (Exception exception)
        {
            report.AddFinding(DoctorSeverities.Error, "DOCTOR_CHECK_FAILED",
                "The " + component + " diagnostic could not complete; other checks continued.", component,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = exception.GetType().Name,
                    ["detail"] = DiagnosticRedactor.Text(exception.Message)
                });
        }
    }

    private void InspectCoordinator(DoctorAuditReport report, PersistedState snapshot)
    {
        report.AddFinding(DoctorSeverities.Info, "COORDINATOR_REACHABLE",
            "The coordinator accepted the doctor request.", "Coordinator");
        report.AddFinding(DoctorSeverities.Info, "RUNTIME_IDENTITY_RESOLVED",
            "Resolved source, installed runtime, and RimWorld identities are available.",
            "Runtime identity",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requestedDevBridgeRoot"] = runtimeIdentity?.RequestedDevBridgeRoot,
                ["devBridgeSourceRoot"] = runtimeIdentity?.DevBridgeSourceRoot,
                ["devBridgeRuntimeRoot"] = runtimeIdentity?.DevBridgeRuntimeRoot,
                ["devBridgePinnedWorktreeRoot"] = runtimeIdentity?.DevBridgePinnedWorktreeRoot,
                ["rimWorldRoot"] = runtimeIdentity?.RimWorldRoot,
                ["rimWorldExecutable"] = runtimeIdentity?.RimWorldExecutable,
                ["resolutionSource"] = runtimeIdentity?.ResolutionSource,
                ["rimWorldRootExists"] = (runtimeIdentity?.RimWorldRootExists ?? false).ToString().ToLowerInvariant(),
                ["rimWorldExecutableExists"] = (runtimeIdentity?.RimWorldExecutableExists ?? false).ToString().ToLowerInvariant(),
                ["devBridgeSourceRootExists"] = (runtimeIdentity?.DevBridgeSourceRootExists ?? false).ToString().ToLowerInvariant(),
                ["devBridgePinnedWorktreeRootExists"] = (runtimeIdentity?.DevBridgePinnedWorktreeRootExists ?? false).ToString().ToLowerInvariant(),
                ["devBridgeRuntimeRootExists"] = (runtimeIdentity?.DevBridgeRuntimeRootExists ?? false).ToString().ToLowerInvariant(),
                ["installedRuntimeLayoutValid"] = (runtimeIdentity?.InstalledRuntimeLayoutValid ?? false).ToString().ToLowerInvariant(),
                ["runtimeBelongsToRimWorld"] = (runtimeIdentity?.RuntimeBelongsToRimWorld ?? false).ToString().ToLowerInvariant()
            });

        bool runtimeExists = Directory.Exists(runtimeRoot);
        report.AddFinding(runtimeExists ? DoctorSeverities.Info : DoctorSeverities.Error,
            runtimeExists ? "RUNTIME_DIRECTORY_PRESENT" : "RUNTIME_DIRECTORY_MISSING",
            runtimeExists ? "The authoritative Runtime directory is present." :
                "The authoritative Runtime directory is missing.", "Coordinator",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["root"] = coordinatorRoot,
                ["runtimeSlotId"] = runtimeSlotId,
                ["runtimePath"] = runtimeRoot
            });

        bool executableExists = File.Exists(rimWorldExe);
        report.AddFinding(executableExists ? DoctorSeverities.Info : DoctorSeverities.Error,
            executableExists ? "RIMWORLD_EXECUTABLE_PRESENT" : "RIMWORLD_EXECUTABLE_MISSING",
            executableExists ? "The configured RimWorld executable is present." :
                "The configured RimWorld executable is missing.", "Coordinator",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = rimWorldExe });

        string aboutPath = Path.Combine(root, "About", "About.xml");
        bool aboutExists = File.Exists(aboutPath);
        report.AddFinding(aboutExists ? DoctorSeverities.Info : DoctorSeverities.Error,
            aboutExists ? "MOD_METADATA_PRESENT" : "MOD_METADATA_MISSING",
            aboutExists ? "About/About.xml is present." : "About/About.xml is missing.", "Coordinator",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = aboutPath });

        bool scopeValid = snapshot != null &&
            RuntimeScope.PathsEqual(snapshot.CoordinatorRoot, coordinatorRoot) &&
            string.Equals(snapshot.RuntimeSlotId, runtimeSlotId, StringComparison.Ordinal);
        report.AddFinding(scopeValid ? DoctorSeverities.Info : DoctorSeverities.Error,
            scopeValid ? "RUNTIME_SCOPE_IDENTIFIED" : "RUNTIME_SCOPE_INVALID",
            scopeValid ? "Persisted state identifies this authoritative root and runtime slot." :
                "Persisted state does not identify the active coordinator root and runtime slot.", "Coordinator",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["coordinatorRoot"] = coordinatorRoot,
                ["runtimeSlotId"] = runtimeSlotId,
                ["persistedStatePresent"] = File.Exists(statePath).ToString().ToLowerInvariant()
            });

        if (persistedStateLoadBlocked)
        {
            string code = snapshot?.ErrorCode ?? "PERSISTED_STATE_UNAVAILABLE";
            report.AddFinding(DoctorSeverities.Error, code,
                snapshot?.Error ?? "The persisted coordinator state is unavailable.", "Coordinator",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["statePath"] = statePath,
                    ["schemaVersion"] = (snapshot?.SchemaVersion ?? 0).ToString(CultureInfo.InvariantCulture)
                });
        }
        else if (snapshot?.SchemaVersion == DevBridgeSchemaVersions.RuntimeState)
        {
            report.AddFinding(DoctorSeverities.Info, "RUNTIME_SCHEMA_SUPPORTED",
                "The persisted runtime state uses the supported schema.", "Compatibility",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = snapshot.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                    ["contract"] = DevBridgeSchemaVersions.RuntimeStateContract
                });
        }
        else
        {
            report.AddFinding(DoctorSeverities.Warning, "RUNTIME_SCHEMA_LEGACY",
                "The persisted runtime state was loaded from a legacy schema and will be upgraded on a safe save.",
                "Compatibility", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = (snapshot?.SchemaVersion ?? 0).ToString(CultureInfo.InvariantCulture),
                    ["supportedSchemaVersion"] = DevBridgeSchemaVersions.RuntimeState.ToString(CultureInfo.InvariantCulture)
                });
        }
        List<AlternateRootContract> alternateRoots = FindAlternateRoots();
        if (alternateRoots.Count > 0)
        {
            report.AddFinding(DoctorSeverities.Error, "DUPLICATE_INSTALLATION_ROOT",
                "More than one DevBridge installation has durable state in the authoritative root's environment; no alternate root was adopted.",
                "Coordinator", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["authoritativeRoot"] = coordinatorRoot,
                    ["alternateRoots"] = string.Join(",", alternateRoots.Select(value => value.Root))
                });
        }
    }

    private void InspectComponents(DoctorAuditReport report, PersistedState snapshot)
    {
        ComponentVersionReport versions = ComponentVersions.Current;
        versions.CoordinatorBuild = RunningBuildIdentity;
        versions.PublishedCoordinatorBuild = PublishedCoordinatorBuildIdentity;
        versions.CoordinatorVersion = versions.CoordinatorBuild.ProductVersion ??
            AssemblyVersion(typeof(CoordinatorState).Assembly) ?? versions.CoordinatorVersion;

        string aboutVersion = ReadAboutVersion();
        string modAssemblyPath = Path.Combine(root, "1.6", "Assemblies", "DevBridge2.dll");
        versions.ModBuild = DevBridgeBuildIdentity.FromAssemblyPath(modAssemblyPath);
        string modVersion = versions.ModBuild?.ProductVersion ?? AssemblyVersion(modAssemblyPath);
        versions.ModVersion = modVersion ?? aboutVersion ?? "missing";

        string bridgeToolsPath = FindBridgeToolsAssembly();
        versions.BridgeToolsBuild = DevBridgeBuildIdentity.FromAssemblyPath(bridgeToolsPath);
        versions.BridgeToolsVersion = versions.BridgeToolsBuild?.ProductVersion ??
            AssemblyVersion(bridgeToolsPath) ??
            (bridgeToolsPath == null ? "not-deployed" : "unknown");
        versions.BridgeToolsPath = bridgeToolsPath;
        report.Components = versions;

        if (string.Equals(versions.CoordinatorVersion, "unknown", StringComparison.Ordinal))
            report.AddFinding(DoctorSeverities.Error, "COORDINATOR_VERSION_MISSING",
                "The coordinator version could not be determined.", "Compatibility");

        if (aboutVersion != null && !string.Equals(aboutVersion, versions.CoordinatorVersion, StringComparison.Ordinal))
            report.AddFinding(DoctorSeverities.Error, "COORDINATOR_VERSION_MISMATCH",
                "About/About.xml does not match the coordinator product version.", "Compatibility",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["coordinatorVersion"] = versions.CoordinatorVersion,
                    ["aboutVersion"] = aboutVersion
                });

        if (modAssemblyPath != null && File.Exists(modAssemblyPath) &&
            !string.Equals(versions.ModVersion, versions.CoordinatorVersion, StringComparison.Ordinal))
        {
            string code = ProtocolSkewCode(versions.ModVersion, versions.CoordinatorVersion);
            report.AddFinding(code == "COMPONENT_VERSION_SKEW" ? DoctorSeverities.Warning : DoctorSeverities.Error,
                code, "The deployed mod product/protocol version differs from the coordinator.", "Compatibility",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["coordinatorVersion"] = versions.CoordinatorVersion,
                    ["modVersion"] = versions.ModVersion,
                    ["coordinatorProtocolMajor"] = versions.CoordinatorProtocolMajor.ToString(CultureInfo.InvariantCulture),
                    ["modProtocolMajor"] = versions.ModProtocolMajor.ToString(CultureInfo.InvariantCulture)
                });
        }
        else if (!File.Exists(modAssemblyPath))
        {
            report.AddFinding(DoctorSeverities.Error, "MOD_ASSEMBLY_MISSING",
                "The built DevBridge mod assembly is missing.", "Compatibility",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = modAssemblyPath });
        }
        else
        {
            report.AddFinding(DoctorSeverities.Info, "COMPONENT_VERSIONS_COMPATIBLE",
                "Coordinator and deployed mod versions are protocol-compatible.", "Compatibility",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["coordinatorVersion"] = versions.CoordinatorVersion,
                    ["modVersion"] = versions.ModVersion,
                    ["protocolCompatible"] = versions.ProtocolCompatible.ToString().ToLowerInvariant()
                });
        }

        if (bridgeToolsPath == null)
        {
            string modLocalBridgeToolsPath = FindModLocalBridgeToolsAssembly();
            string sourceBridgeToolsPath = FindSourceBridgeToolsAssembly();
            if (modLocalBridgeToolsPath != null)
            {
                report.AddFinding(DoctorSeverities.Warning, "BRIDGETOOLS_WRONG_LOCATION",
                    "The BridgeTools companion is nested inside the mod; RimBridgeServer discovers the sibling global BridgeTools bundle.",
                    "Compatibility", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["expectedPath"] = ExpectedBridgeToolsAssemblyPath(),
                        ["wrongLocationPath"] = modLocalBridgeToolsPath
                    });
            }
            else if (sourceBridgeToolsPath != null)
            {
                report.AddFinding(DoctorSeverities.Warning, "BRIDGETOOLS_WRONG_LOCATION",
                    "The BridgeTools companion exists only in a source build output; RimBridgeServer discovers the sibling global BridgeTools bundle.",
                    "Compatibility", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["expectedPath"] = ExpectedBridgeToolsAssemblyPath(),
                        ["sourceBuildPath"] = sourceBridgeToolsPath
                    });
            }
            else
            {
                report.AddFinding(DoctorSeverities.Info, "BRIDGETOOLS_ASSEMBLY_NOT_DISCOVERED",
                    "The optional BridgeTools companion assembly was not found in the sibling global BridgeTools bundle.",
                    "Compatibility", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["expectedPath"] = ExpectedBridgeToolsAssemblyPath()
                    });
            }
        }
        else if (versions.BridgeToolsVersion == "unknown")
        {
            report.AddFinding(DoctorSeverities.Error, "BRIDGETOOLS_LOAD_FAILED",
                "The deployed BridgeTools companion assembly could not be read for compatibility inspection.",
                "Compatibility", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["path"] = bridgeToolsPath
                });
        }
        else if (!string.Equals(versions.BridgeToolsVersion, versions.CoordinatorVersion, StringComparison.Ordinal))
        {
            report.AddFinding(DoctorSeverities.Warning, "BRIDGETOOLS_STALE_BINARY",
                "The optional BridgeTools companion has a different product version.", "Compatibility",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["coordinatorVersion"] = versions.CoordinatorVersion,
                    ["bridgeToolsVersion"] = versions.BridgeToolsVersion,
                    ["path"] = bridgeToolsPath
                });
        }

        if (versions.RuntimeStateSchema != DevBridgeSchemaVersions.RuntimeStateContract ||
            versions.ReadinessSchema != DevBridgeSchemaVersions.ReadinessContract ||
            versions.GeneratedModsConfigSchema != DevBridgeSchemaVersions.GeneratedModsConfigContract)
            report.AddFinding(DoctorSeverities.Error, "RUNTIME_SCHEMA_UNSUPPORTED",
                "A coordinator schema contract is not supported by this build.", "Compatibility");
        else
            report.AddFinding(DoctorSeverities.Info, "SCHEMA_CONTRACTS_SUPPORTED",
                "Runtime, readiness, generated ModsConfig, and Quicktest schema contracts are supported.",
                "Compatibility", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["runtime"] = versions.RuntimeStateSchema,
                    ["readiness"] = versions.ReadinessSchema,
                    ["generatedModsConfig"] = versions.GeneratedModsConfigSchema,
                    ["quicktestFailure"] = versions.QuicktestFailureSchema.ToString(CultureInfo.InvariantCulture)
                });
    }

    private ProcessStatusSnapshot InspectProcess(DoctorAuditReport report, PersistedState snapshot)
    {
        ProcessStatusSnapshot processSnapshot = new();
        bool censusComplete = false;
        lock (gate)
        {
            try
            {
                processSnapshot = EnumerateStatusProcessesLocked();
                censusComplete = true;
                if (!persistedStateLoadBlocked && state.ErrorCode == ProcessInspection.ErrorCode &&
                    state.Phase == BridgePhase.ERROR && !state.RestartPending && state.Leases.Count == 0 &&
                    processSnapshot.MatchingProcessCount == 0 && processSnapshot.UnmanagedProcesses.Count == 0)
                {
                    RecoverProcessInspectionQuarantineLocked();
                    report.AddFinding(DoctorSeverities.Info, "PROCESS_QUARANTINE_CLEARED",
                        "A complete zero-process census cleared stale process-inspection quarantine; no launch was attempted.",
                        "Process");
                    snapshot = CloneStateLocked();
                }
            }
            catch (ProcessInspectionException exception)
            {
                report.AddFinding(DoctorSeverities.Error, ProcessInspection.ErrorCode,
                    "A complete process census was not available; process control remains quarantined.", "Process",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["detail"] = exception.Message,
                        ["recordedProcessId"] = (snapshot?.ProcessId ?? 0).ToString(CultureInfo.InvariantCulture)
                    });
            }
        }

        if (!censusComplete)
            return processSnapshot;

        int matching = processSnapshot.MatchingProcessCount;
        int unmanaged = processSnapshot.UnmanagedProcesses.Count;
        if (matching > 1)
            report.AddFinding(DoctorSeverities.Error, "PROCESS_MULTIPLE_MATCHING",
                "Multiple RimWorld processes match the configured executable; process ownership is ambiguous.",
                "Process", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["matchingProcessCount"] = matching.ToString(CultureInfo.InvariantCulture)
                });
        if (unmanaged > 0)
            report.AddFinding(DoctorSeverities.Error, "PROCESS_OWNERSHIP_AMBIGUOUS",
                "One or more RimWorld processes are outside the coordinator-owned identity.", "Process",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["unmanagedProcessCount"] = unmanaged.ToString(CultureInfo.InvariantCulture),
                    ["unmanagedProcessIds"] = string.Join(",", processSnapshot.UnmanagedProcesses
                        .Select(value => value.ProcessId).OrderBy(value => value))
                });

        bool expectedRunning = snapshot != null && snapshot.Phase != BridgePhase.STOPPED &&
            snapshot.Phase != BridgePhase.ERROR && !snapshot.MaintenanceReady;
        if (snapshot != null && snapshot.ProcessId > 0 && !processSnapshot.OwnedProcessRunning)
        {
            string code = matching > 0 ? "PROCESS_IDENTITY_MISMATCH" :
                expectedRunning ? "PROCESS_IDENTITY_STALE" : "PROCESS_EXPECTED_ABSENT";
            string severity = code == "PROCESS_EXPECTED_ABSENT" ? DoctorSeverities.Warning : DoctorSeverities.Error;
            report.AddFinding(severity, code,
                code == "PROCESS_EXPECTED_ABSENT"
                    ? "The recorded process identity is absent while the coordinator expects RimWorld to be stopped."
                    : "The recorded RimWorld PID/start identity does not match the current process census.",
                "Process", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["recordedProcessId"] = snapshot.ProcessId.ToString(CultureInfo.InvariantCulture),
                    ["matchingProcessCount"] = matching.ToString(CultureInfo.InvariantCulture),
                    ["phase"] = snapshot.Phase.ToString()
                });
        }
        else if (processSnapshot.OwnedProcessRunning)
        {
            report.AddFinding(DoctorSeverities.Info, "PROCESS_IDENTITY_MATCH",
                "The recorded RimWorld PID/start identity matches the coordinator-owned process.", "Process",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["processId"] = snapshot?.ProcessId.ToString(CultureInfo.InvariantCulture) ?? "0"
                });
        }
        else
        {
            report.AddFinding(DoctorSeverities.Info, "PROCESS_CORRECTLY_ABSENT",
                "No coordinator-owned RimWorld process is present for the current state.", "Process",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["phase"] = snapshot?.Phase.ToString() ?? "unknown",
                    ["matchingProcessCount"] = matching.ToString(CultureInfo.InvariantCulture)
                });
        }

        if (snapshot?.MaintenanceReady == true && matching > 0)
            report.AddFinding(DoctorSeverities.Error, "MAINTENANCE_PROCESS_PRESENT",
                "Maintenance is marked ready but a matching RimWorld process is still present.", "Process");
        if (matching == 0 && unmanaged == 0 && snapshot?.ErrorCode == ProcessInspection.ErrorCode)
            report.AddFinding(DoctorSeverities.Warning, ProcessInspection.ErrorCode,
                "Process inspection quarantine remains recorded despite a complete zero-process census.", "Process");
        return processSnapshot;
    }

    private string InspectModsConfig(DoctorAuditReport report, PersistedState initial)
    {
        bool exists = File.Exists(modsConfigPath);
        if (!exists)
        {
            report.AddFinding(DoctorSeverities.Error, "MODSCONFIG_MISSING",
                "ModsConfig.xml is missing or has not been created yet.", "ModsConfig",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = modsConfigPath });
            return "MISSING";
        }

        byte[] contents;
        try
        {
            contents = File.ReadAllBytes(modsConfigPath);
        }
        catch (Exception exception)
        {
            report.AddFinding(DoctorSeverities.Error, "MODSCONFIG_UNREADABLE",
                "ModsConfig.xml exists but could not be read.", "ModsConfig",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["path"] = modsConfigPath,
                    ["exceptionType"] = exception.GetType().Name,
                    ["detail"] = exception.Message
                });
            return "UNKNOWN";
        }

        string currentFingerprint = HashBytes(contents);
        InspectGeneratedModsConfigManifest(report);
        bool modEnabled;
        try
        {
            modEnabled = IsDevBridgeModEnabled();
            report.AddFinding(modEnabled ? DoctorSeverities.Info : DoctorSeverities.Warning,
                modEnabled ? "MOD_ENABLED" : "MOD_NOT_ENABLED",
                modEnabled ? "DevBridge2 is enabled in the current ModsConfig.xml." :
                    "DevBridge2 is not enabled in the current ModsConfig.xml; launch-time ownership may enable it.",
                "ModsConfig");
        }
        catch (Exception exception)
        {
            report.AddFinding(DoctorSeverities.Warning, "MOD_ENABLED_STATE_UNKNOWN",
                "DevBridge2 enabled state could not be determined independently of ModsConfig ownership.",
                "ModsConfig", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = exception.GetType().Name
                });
        }
        string ownership;
        string baselineFingerprint;
        lock (gate)
        {
            ownership = CurrentModsConfigOwnershipLocked(contents, currentFingerprint, recordManifestErrors: false);
            baselineFingerprint = ReadBaselineFingerprintLocked() ?? state.BaselineFingerprint;
            initial = CloneStateLocked();
        }

        string ownershipCode = ownership switch
        {
            "DEVBRIDGE_GENERATED" => "MODSCONFIG_OWNERSHIP_DEVBRIDGE",
            "BASELINE" => "MODSCONFIG_OWNERSHIP_BASELINE",
            "USER_EDIT" => "MODSCONFIG_USER_EDIT",
            "USER" => "MODSCONFIG_USER",
            "MISSING" => "MODSCONFIG_MISSING",
            _ => "MODSCONFIG_OWNERSHIP_UNKNOWN"
        };
        string ownershipSeverity = ownership switch
        {
            "DEVBRIDGE_GENERATED" or "BASELINE" => DoctorSeverities.Info,
            "USER_EDIT" or "USER" => DoctorSeverities.Warning,
            _ => DoctorSeverities.Error
        };
        report.AddFinding(ownershipSeverity, ownershipCode,
            "Current ModsConfig ownership is " + ownership + ".", "ModsConfig",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ownership"] = ownership,
                ["currentFingerprint"] = currentFingerprint,
                ["baselineFingerprint"] = baselineFingerprint ?? "missing",
                ["expectedFingerprint"] = initial?.ModsConfigGeneratedHash ?? initial?.ProfileFingerprint ?? "none",
                ["mutationAuthority"] = initial?.ModsConfigMutationAuthority ?? "unknown"
            });

        if (initial?.ExternalModsConfigMutation != null ||
            string.Equals(initial?.ModsConfigMutationAuthority, ModsConfigMutationAuthorityValues.ExternalMutated,
                StringComparison.Ordinal))
        {
            report.AddFinding(DoctorSeverities.Error, "PROFILE_EXTERNAL_MUTATION",
                "ModsConfig.xml differs from the generation-owned fingerprint; DevBridge will not absorb the mutation.",
                "ModsConfig", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["observedFingerprint"] = currentFingerprint,
                    ["expectedFingerprint"] = initial.ExternalModsConfigMutation?.ExpectedFingerprint ??
                        initial.ModsConfigGeneratedHash ?? "unknown"
                });
        }
        else if (!string.IsNullOrWhiteSpace(initial?.ModsConfigGeneratedHash) &&
                 !string.Equals(initial.ModsConfigGeneratedHash, currentFingerprint, StringComparison.Ordinal))
        {
            report.AddFinding(DoctorSeverities.Error, "PROFILE_EXTERNAL_MUTATION",
                "ModsConfig.xml differs from the persisted generation-owned fingerprint; DevBridge will not absorb the mutation.",
                "ModsConfig", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["observedFingerprint"] = currentFingerprint,
                    ["expectedFingerprint"] = initial.ModsConfigGeneratedHash
                });
        }

        if (baselineFingerprint == null)
            report.AddFinding(DoctorSeverities.Warning, "BASELINE_NOT_CAPTURED",
                "No durable ModsConfig baseline fingerprint is available.", "ModsConfig");
        else
            report.AddFinding(DoctorSeverities.Info, "BASELINE_FINGERPRINT_AVAILABLE",
                "The durable ModsConfig baseline fingerprint is readable.", "ModsConfig",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["baselineFingerprint"] = baselineFingerprint
                });

        bool safeToWrite = !persistedStateLoadBlocked &&
            (ownership == "DEVBRIDGE_GENERATED" || ownership == "BASELINE" || ownership == "DEVBRIDGE_PENDING") &&
            initial?.ExternalModsConfigMutation == null &&
            initial?.ModsConfigMutationAuthority != ModsConfigMutationAuthorityValues.ExternalMutated;
        report.AddFinding(safeToWrite ? DoctorSeverities.Info : DoctorSeverities.Warning,
            safeToWrite ? "MODSCONFIG_WRITE_GUARD_CLEAR" : "MODSCONFIG_WRITE_GUARD_ACTIVE",
            safeToWrite ? "No external mutation evidence currently blocks DevBridge ModsConfig ownership."
                : "DevBridge must not write ModsConfig.xml until ownership and mutation evidence are reconciled.",
            "ModsConfig", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["safeToWrite"] = safeToWrite.ToString().ToLowerInvariant()
            });
        return ownership;
    }

    private void InspectGeneratedModsConfigManifest(DoctorAuditReport report)
    {
        string path = generatedManifestPath;
        if (!File.Exists(path))
            return;

        try
        {
            GeneratedModsConfigManifest manifest = JsonSerializer.Deserialize<GeneratedModsConfigManifest>(
                File.ReadAllText(path), CoordinatorSerialization.JsonOptions);
            if (manifest == null)
            {
                report.AddFinding(DoctorSeverities.Error, "GENERATED_MODS_CONFIG_MALFORMED",
                    "The generated ModsConfig manifest is present but does not contain an object.",
                    "ModsConfig", new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = path });
                return;
            }

            if (manifest.SchemaVersion < 0)
            {
                report.AddFinding(DoctorSeverities.Error, "GENERATED_MODS_CONFIG_SCHEMA_INVALID",
                    "The generated ModsConfig manifest has an invalid schema version.", "Compatibility",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["schemaVersion"] = manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                        ["supportedSchemaVersion"] = DevBridgeSchemaVersions.GeneratedModsConfig.ToString(CultureInfo.InvariantCulture),
                        ["contract"] = DevBridgeSchemaVersions.GeneratedModsConfigContract
                    });
                return;
            }

            if (manifest.SchemaVersion > DevBridgeSchemaVersions.GeneratedModsConfig)
            {
                report.AddFinding(DoctorSeverities.Error, "GENERATED_MODS_CONFIG_SCHEMA_UNSUPPORTED",
                    "The generated ModsConfig manifest uses a schema newer than this coordinator supports.",
                    "Compatibility", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["schemaVersion"] = manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                        ["supportedSchemaVersion"] = DevBridgeSchemaVersions.GeneratedModsConfig.ToString(CultureInfo.InvariantCulture),
                        ["contract"] = DevBridgeSchemaVersions.GeneratedModsConfigContract
                    });
                return;
            }

            if (string.IsNullOrWhiteSpace(manifest.Hash) || manifest.Hash.Length != 64 ||
                !manifest.Hash.All(Uri.IsHexDigit))
            {
                report.AddFinding(DoctorSeverities.Error, "GENERATED_MODS_CONFIG_MALFORMED",
                    "The generated ModsConfig manifest contains an invalid hash.", "ModsConfig",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = path });
                return;
            }

            report.AddFinding(DoctorSeverities.Info, "GENERATED_MODS_CONFIG_SCHEMA_SUPPORTED",
                "The generated ModsConfig manifest uses a supported schema and hash format.", "Compatibility",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                    ["contract"] = DevBridgeSchemaVersions.GeneratedModsConfigContract
                });
        }
        catch (JsonException exception)
        {
            report.AddFinding(DoctorSeverities.Error, "GENERATED_MODS_CONFIG_MALFORMED",
                "The generated ModsConfig manifest is malformed; Doctor left it untouched.", "ModsConfig",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = exception.GetType().Name,
                    ["detail"] = exception.Message
                });
        }
        catch (Exception exception)
        {
            report.AddFinding(DoctorSeverities.Error, "GENERATED_MODS_CONFIG_READ_FAILED",
                "The generated ModsConfig manifest could not be read; Doctor left it untouched.", "ModsConfig",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = exception.GetType().Name,
                    ["detail"] = exception.Message
                });
        }
    }

    private void InspectProjectsAndProfile(DoctorAuditReport report, PersistedState initial)
    {
        List<string> aliases;
        DateTime now = clock.UtcNow;
        GenerationHistoryView pinnedHistory = null;
        ProfileException aliasException = null;
        lock (gate)
        {
            try
            {
                aliases = CanonicalProjectUnion(ActiveProjectIntentsLocked()
                    .SelectMany(value => value.RequestedProjects ?? new List<string>()));
            }
            catch (ProfileException exception)
            {
                aliases = new List<string>();
                aliasException = exception;
            }
            if (initial?.Generation > 0)
                pinnedHistory = BuildGenerationHistoryViewLocked(initial.Generation);
            foreach (ProjectIntentRegistration registration in state.ProjectIntents ?? new List<ProjectIntentRegistration>())
            {
                if (registration == null || !string.Equals(registration.Status, "ACTIVE", StringComparison.Ordinal))
                    continue;
                if (string.IsNullOrWhiteSpace(registration.Id) || string.IsNullOrWhiteSpace(registration.Owner) ||
                    string.IsNullOrWhiteSpace(registration.SessionId) || registration.RequestedProjects == null ||
                    registration.RequestedProjects.Count == 0)
                    report.AddFinding(DoctorSeverities.Error, "PROFILE_REGISTRATION_INVALID",
                        "An active project registration is structurally incomplete.", "Projects");
                if (registration.ExpiresUtc != default && registration.ExpiresUtc <= now)
                    report.AddFinding(DoctorSeverities.Warning, "PROJECT_INTENT_EXPIRED",
                        "An active project registration has passed its expiration time and needs pruning.", "Projects",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["registrationIdPresent"] = (!string.IsNullOrWhiteSpace(registration.Id)).ToString().ToLowerInvariant()
                        });
            }
        }

        report.AddFinding(aliases.Count == 0 ? DoctorSeverities.Info : DoctorSeverities.Info,
            "PROJECT_REGISTRATIONS_VALIDATED",
            aliases.Count == 0 ? "No active project aliases are queued." :
                "Active project registrations were structurally inspected.", "Projects",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["activeAliasCount"] = aliases.Count.ToString(CultureInfo.InvariantCulture),
                ["activeRegistrationCount"] = (initial?.ProjectIntents ?? new List<ProjectIntentRegistration>())
                    .Count(value => value != null && string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal))
                    .ToString(CultureInfo.InvariantCulture)
            });

        int queuedProjectIntentCount = QueuedProjectIntentCount(initial);
        if (queuedProjectIntentCount > 0)
            report.AddFinding(DoctorSeverities.Warning, "QUEUED_PROJECT_INTENT",
                "One or more active project registrations are queued for a future aggregate generation; test begin must wait for an explicit replacement restart.",
                "Projects/Profile", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["queuedProjectIntentCount"] = queuedProjectIntentCount.ToString(CultureInfo.InvariantCulture)
                });

        if (aliasException != null)
        {
            bool currentGenerationValid = pinnedHistory?.Corrupt != true &&
                pinnedHistory?.Current?.Manifest != null;
            report.NextGenerationConfig = new ConfigurationHealth
            {
                Valid = false,
                ErrorCode = aliasException.Code,
                Error = DiagnosticRedactor.Text(aliasException.Message)
            };
            report.AddFinding(currentGenerationValid ? DoctorSeverities.Warning : DoctorSeverities.Error,
                currentGenerationValid ? "FUTURE_CONFIGURATION_INVALID" : aliasException.Code,
                currentGenerationValid
                    ? "A future project configuration is invalid; the pinned current generation remains manageable."
                    : "The project configuration contains an invalid alias: " + aliasException.Message,
                "Projects/Profile", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["errorCode"] = aliasException.Code,
                    ["currentGenerationTrust"] = currentGenerationValid ? "VALID" : "UNKNOWN"
                });
            return;
        }

        if (persistedStateLoadBlocked)
        {
            report.NextGenerationConfig = new ConfigurationHealth
            {
                Valid = false,
                ErrorCode = "PERSISTED_STATE_UNAVAILABLE",
                Error = "The persisted coordinator state is unavailable."
            };
            return;
        }

        if (aliases.Count == 0 && initial?.Generation == 0 && initial.Phase == BridgePhase.STOPPED)
        {
            report.NextGenerationConfig = new ConfigurationHealth { Valid = true };
            report.AddFinding(DoctorSeverities.Info, "PROFILE_NOT_ACTIVE",
                "No active generation or project intent requires prospective profile resolution.", "Projects/Profile");
            return;
        }

        try
        {
            ModProfile profile = ResolveAggregateProfile(aliases);
            ModProfileResolver.ValidateResolvedProfile(profile);
            report.NextGenerationConfig = new ConfigurationHealth { Valid = true };
            if (string.IsNullOrWhiteSpace(profile.ProfileFingerprint))
                report.AddFinding(DoctorSeverities.Error, "PROFILE_FINGERPRINT_MISSING",
                    "The prospective aggregate profile did not produce a fingerprint.", "Projects/Profile");
            else
                report.AddFinding(DoctorSeverities.Info, "PROFILE_RESOLUTION_VALID",
                    "The prospective aggregate profile resolves with deterministic metadata, dependencies, and load order.",
                    "Projects/Profile", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["prospectiveFingerprint"] = profile.ProfileFingerprint,
                        ["resolvedProjectCount"] = (profile.ResolvedProjectPackageIds ?? new List<string>()).Count
                            .ToString(CultureInfo.InvariantCulture),
                        ["resolvedModCount"] = (profile.ResolvedMods ?? new List<string>()).Count
                            .ToString(CultureInfo.InvariantCulture)
                    });

            string expectedFingerprint = pinnedHistory?.Current?.Manifest?.Profile?.ProfileFingerprint ??
                initial?.FrozenProfileFingerprint ?? initial?.ProfileFingerprint;
            if (!string.IsNullOrWhiteSpace(expectedFingerprint) &&
                !string.Equals(expectedFingerprint, profile.ProfileFingerprint, StringComparison.Ordinal) &&
                !(initial?.RestartPending == true && aliases.Count > 0))
                report.AddFinding(pinnedHistory?.Current?.Manifest != null
                        ? DoctorSeverities.Warning : DoctorSeverities.Error,
                    pinnedHistory?.Current?.Manifest != null
                        ? "FUTURE_CONFIGURATION_DIFFERS" : "PROFILE_FINGERPRINT_MISMATCH",
                    pinnedHistory?.Current?.Manifest != null
                        ? "The prospective next-generation profile differs from the pinned current generation; the current generation remains unchanged."
                        : "The recomputed prospective profile fingerprint differs from the persisted profile.",
                    "Projects/Profile", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["persistedFingerprint"] = expectedFingerprint,
                        ["prospectiveFingerprint"] = profile.ProfileFingerprint
                    });

            if (initial?.FrozenTargetGeneration > 0 && !string.IsNullOrWhiteSpace(initial.FrozenProfileFingerprint))
                report.AddFinding(DoctorSeverities.Info, "FROZEN_PROFILE_IDENTIFIED",
                    "A frozen current-generation profile is distinguishable from queued future project intent.",
                    "Projects/Profile", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["frozenGeneration"] = initial.FrozenTargetGeneration.ToString(CultureInfo.InvariantCulture),
                        ["queuedProjectIntentCount"] = QueuedProjectIntentCount(initial).ToString(CultureInfo.InvariantCulture)
                    });
        }
        catch (ProfileException exception)
        {
            bool currentGenerationValid = pinnedHistory?.Corrupt != true &&
                pinnedHistory?.Current?.Manifest != null;
            report.NextGenerationConfig = new ConfigurationHealth
            {
                Valid = false,
                ErrorCode = exception.Code,
                Error = DiagnosticRedactor.Text(exception.Message)
            };
            report.AddFinding(currentGenerationValid ? DoctorSeverities.Warning : DoctorSeverities.Error,
                currentGenerationValid ? "FUTURE_CONFIGURATION_INVALID" : exception.Code,
                currentGenerationValid
                    ? "Prospective project/profile resolution is invalid; the pinned current generation remains manageable."
                    : "Prospective project/profile resolution failed: " + exception.Message,
                "Projects/Profile", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["errorCode"] = exception.Code,
                    ["currentGenerationTrust"] = currentGenerationValid ? "VALID" : "UNKNOWN"
                });
        }
        catch (Exception exception)
        {
            bool currentGenerationValid = pinnedHistory?.Corrupt != true &&
                pinnedHistory?.Current?.Manifest != null;
            report.NextGenerationConfig = new ConfigurationHealth
            {
                Valid = false,
                ErrorCode = "PROFILE_RESOLUTION_CHECK_FAILED",
                Error = "Prospective project/profile resolution could not complete."
            };
            report.AddFinding(currentGenerationValid ? DoctorSeverities.Warning : DoctorSeverities.Error,
                currentGenerationValid ? "FUTURE_CONFIGURATION_INVALID" : "PROFILE_RESOLUTION_CHECK_FAILED",
                currentGenerationValid
                    ? "Prospective project/profile resolution is unavailable; the pinned current generation remains manageable."
                    : "Prospective project/profile resolution could not complete.", "Projects/Profile",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = exception.GetType().Name,
                    ["detail"] = exception.Message
                });
        }
    }

    private void InspectGeneration(DoctorAuditReport report, PersistedState snapshot)
    {
        if (snapshot == null)
            return;

        bool launchIdentityComplete = !string.IsNullOrWhiteSpace(snapshot.LaunchId) &&
            snapshot.LaunchGeneration > 0 && snapshot.ProcessId > 0 && snapshot.ProcessStartUtcTicks > 0;
        bool activeProcessState = snapshot.Phase != BridgePhase.STOPPED && snapshot.Phase != BridgePhase.ERROR &&
            !snapshot.MaintenanceReady;
        if (activeProcessState && !launchIdentityComplete)
            report.AddFinding(DoctorSeverities.Error, "GENERATION_IDENTITY_INCONSISTENT",
                "The active generation does not have a complete launch/process identity.", "Generation",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["generation"] = snapshot.Generation.ToString(CultureInfo.InvariantCulture),
                    ["launchGeneration"] = snapshot.LaunchGeneration.ToString(CultureInfo.InvariantCulture),
                    ["processId"] = snapshot.ProcessId.ToString(CultureInfo.InvariantCulture)
                });
        else
            report.AddFinding(DoctorSeverities.Info, "GENERATION_IDENTITY_CHECKED",
                "Current generation and launch identity fields were inspected.", "Generation",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["generation"] = snapshot.Generation.ToString(CultureInfo.InvariantCulture),
                    ["launchGeneration"] = snapshot.LaunchGeneration.ToString(CultureInfo.InvariantCulture),
                    ["targetGeneration"] = snapshot.TargetGeneration.ToString(CultureInfo.InvariantCulture),
                    ["launchIdPresent"] = (!string.IsNullOrWhiteSpace(snapshot.LaunchId)).ToString().ToLowerInvariant()
                });

        if (snapshot.Phase == BridgePhase.READY && snapshot.LaunchGeneration != snapshot.Generation)
            report.AddFinding(DoctorSeverities.Error, "GENERATION_PROFILE_MISMATCH",
                "The READY generation does not match its launch generation.", "Generation");
        if (snapshot.RestartPending && snapshot.TargetGeneration <= snapshot.Generation)
            report.AddFinding(DoctorSeverities.Error, "QUEUED_GENERATION_INVALID",
                "Queued future work does not target a strictly newer generation.", "Generation",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["generation"] = snapshot.Generation.ToString(CultureInfo.InvariantCulture),
                    ["targetGeneration"] = snapshot.TargetGeneration.ToString(CultureInfo.InvariantCulture)
                });

        if (snapshot.RestartPending || snapshot.AggregateFreezePending)
            report.AddFinding(DoctorSeverities.Info, "QUEUED_WORK_DISTINGUISHED",
                "Queued restart/freeze work is recorded separately from the frozen current generation.", "Generation",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["restartQueued"] = snapshot.RestartPending.ToString().ToLowerInvariant(),
                    ["freezePending"] = snapshot.AggregateFreezePending.ToString().ToLowerInvariant(),
                    ["targetGeneration"] = snapshot.TargetGeneration.ToString(CultureInfo.InvariantCulture)
                });
    }

    private GenerationHistoryView InspectGenerationHistory(DoctorAuditReport report,
        PersistedState snapshot)
    {
        GenerationHistoryView view;
        lock (gate)
            view = BuildGenerationHistoryViewLocked(snapshot?.Generation ?? state.Generation);

        if (view.Corrupt)
        {
            report.AddFinding(DoctorSeverities.Error, view.ErrorCode ?? "GENERATION_HISTORY_CORRUPT",
                view.Error ?? "Durable generation history could not be read; it was left untouched.",
                "Generation history", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["historyPath"] = generationHistoryPath,
                    ["historyWasRewritten"] = "false"
                });
        }
        else
        {
            report.AddFinding(DoctorSeverities.Info, "GENERATION_HISTORY_READ",
                "Durable generation history and accepted manifests were read without rewriting them.",
                "Generation history", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["recordCount"] = view.Records.Count.ToString(CultureInfo.InvariantCulture),
                    ["currentGeneration"] = view.CurrentGeneration.ToString(CultureInfo.InvariantCulture),
                    ["lastKnownGoodGeneration"] = (view.LastKnownGoodGeneration?.ToString(CultureInfo.InvariantCulture) ?? "none")
                });
        }
        return view;
    }

    private void InspectLeases(DoctorAuditReport report, PersistedState snapshot)
    {
        DateTime now = clock.UtcNow;
        List<TestLease> leases = snapshot?.Leases ?? new List<TestLease>();
        HashSet<string> ids = new(StringComparer.Ordinal);
        int expired = 0;
        foreach (TestLease lease in leases)
        {
            if (lease == null || string.IsNullOrWhiteSpace(lease.Id) || string.IsNullOrWhiteSpace(lease.Agent) ||
                lease.StartedUtc == default || lease.LastHeartbeatUtc == default)
            {
                report.AddFinding(DoctorSeverities.Error, "LEASE_METADATA_INVALID",
                    "A persisted test lease has invalid identity or timing metadata.", "Leases");
                continue;
            }
            if (!ids.Add(lease.Id))
                report.AddFinding(DoctorSeverities.Error, "LEASE_OWNERSHIP_CONFLICT",
                    "Multiple persisted leases use the same lease ID.", "Leases");
            DateTime expires = LeaseExpiresUtc(lease);
            if (expires <= lease.StartedUtc)
                report.AddFinding(DoctorSeverities.Error, "LEASE_METADATA_INVALID",
                    "A lease expiration does not occur after its start time.", "Leases",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["leaseIdPresent"] = "true" });
            if (expires <= now)
            {
                expired++;
                report.AddFinding(DoctorSeverities.Warning, "LEASE_EXPIRED",
                    "A persisted lease has expired and is awaiting normal pruning.", "Leases",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["expiresUtc"] = expires.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        ["retryAfterSeconds"] = "0"
                    });
            }
        }

        if (snapshot?.MaintenanceReady == true && leases.Count == 0)
            report.AddFinding(DoctorSeverities.Error, "MAINTENANCE_LEASE_REQUIRED",
                "Maintenance is marked ready without an owning lease.", "Leases");
        if (snapshot?.MaintenanceReady == true && snapshot.Phase != BridgePhase.STOPPED)
            report.AddFinding(DoctorSeverities.Error, "MAINTENANCE_STATE_INVALID",
                "Maintenance readiness is set while the coordinator phase is not STOPPED.", "Leases",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["phase"] = snapshot.Phase.ToString()
                });
        if (snapshot?.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ControlledFrozen &&
            string.IsNullOrWhiteSpace(snapshot.FrozenProfileFingerprint))
            report.AddFinding(DoctorSeverities.Error, "OWNERSHIP_STATE_CONFLICT",
                "Controlled-frozen ModsConfig ownership is recorded without a frozen profile fingerprint.", "Leases");

        report.AddFinding(DoctorSeverities.Info, "LEASES_VALIDATED",
            "Persisted lease identities, expiration metadata, and maintenance ownership were inspected.", "Leases",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["leaseCount"] = leases.Count.ToString(CultureInfo.InvariantCulture),
                ["expiredLeaseCount"] = expired.ToString(CultureInfo.InvariantCulture),
                ["maintenanceReady"] = (snapshot?.MaintenanceReady == true).ToString().ToLowerInvariant()
            });
    }

    private void InspectReadiness(DoctorAuditReport report, PersistedState snapshot)
    {
        bool readinessExists = File.Exists(readinessPath);
        ReadinessRecord record = null;
        if (readinessExists)
        {
            try
            {
                record = JsonSerializer.Deserialize<ReadinessRecord>(File.ReadAllText(readinessPath), CoordinatorSerialization.JsonOptions);
                if (record == null)
                    throw new JsonException("readiness record is null");
            }
            catch (Exception exception)
            {
                report.AddFinding(DoctorSeverities.Error, "READINESS_MALFORMED",
                    "The readiness artifact is present but malformed.", "Readiness",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["path"] = readinessPath,
                        ["exceptionType"] = exception.GetType().Name,
                        ["detail"] = exception.Message
                    });
            }
        }

        if (record == null && !readinessExists)
        {
            string severity = snapshot?.Phase == BridgePhase.READY ? DoctorSeverities.Error : DoctorSeverities.Info;
            report.AddFinding(severity, snapshot?.Phase == BridgePhase.READY ? "READINESS_MISSING" : "READINESS_NOT_PRESENT",
                snapshot?.Phase == BridgePhase.READY
                    ? "The coordinator reports READY but no readiness artifact is present."
                    : "No readiness artifact is present; this is normal before a playable map is loaded.", "Readiness");
        }

        if (record != null)
        {
            if (record.SchemaVersion < 0)
                report.AddFinding(DoctorSeverities.Error, "READINESS_SCHEMA_INVALID",
                    "The readiness artifact has an invalid schema version.", "Readiness");
            else if (record.SchemaVersion > DevBridgeSchemaVersions.Readiness)
                report.AddFinding(DoctorSeverities.Error, "READINESS_SCHEMA_UNSUPPORTED",
                    "The readiness artifact uses a schema newer than this coordinator supports.", "Readiness",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["schemaVersion"] = record.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                        ["supportedSchemaVersion"] = DevBridgeSchemaVersions.Readiness.ToString(CultureInfo.InvariantCulture)
                    });
            else if (record.SchemaVersion == 0)
                report.AddFinding(DoctorSeverities.Warning, "READINESS_SCHEMA_LEGACY",
                    "The readiness artifact uses the supported legacy schema marker.", "Readiness");

            int expectedGeneration = snapshot?.TargetGeneration > 0 ? snapshot.TargetGeneration : snapshot?.Generation ?? 0;
            bool identityMatches = snapshot != null &&
                string.Equals(record.LaunchId, snapshot.LaunchId, StringComparison.Ordinal) &&
                record.Generation == expectedGeneration && record.ProcessId == snapshot.ProcessId &&
                record.ProcessId > 0;
            bool timestampValid = snapshot == null || snapshot.LaunchStartedUtc == default ||
                (record.TimestampUtc != default &&
                 record.TimestampUtc.ToUniversalTime() >= snapshot.LaunchStartedUtc.ToUniversalTime().AddSeconds(-2) &&
                 record.TimestampUtc.ToUniversalTime() <= clock.UtcNow.ToUniversalTime().AddSeconds(2));
            if (!identityMatches)
                report.AddFinding(DoctorSeverities.Error, "READINESS_IDENTITY_MISMATCH",
                    "The readiness launchId/generation/process identity does not match the authoritative state.",
                    "Readiness", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["launchIdMatches"] = (snapshot != null && string.Equals(record.LaunchId, snapshot.LaunchId,
                            StringComparison.Ordinal)).ToString().ToLowerInvariant(),
                        ["generationMatches"] = (record.Generation == expectedGeneration).ToString().ToLowerInvariant(),
                        ["processIdMatches"] = (snapshot != null && record.ProcessId == snapshot.ProcessId).ToString().ToLowerInvariant()
                    });
            else if (!timestampValid)
                report.AddFinding(DoctorSeverities.Error, "READINESS_STALE",
                    "The readiness artifact timestamp is outside the current launch window.", "Readiness");
            else
                report.AddFinding(DoctorSeverities.Info, "READINESS_IDENTITY_MATCH",
                    "The readiness artifact matches the authoritative launch identity.", "Readiness",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["generation"] = record.Generation.ToString(CultureInfo.InvariantCulture),
                        ["processId"] = record.ProcessId.ToString(CultureInfo.InvariantCulture)
                    });
        }

        if (snapshot?.ErrorCode == "READINESS_TIMEOUT")
            report.AddFinding(DoctorSeverities.Error, "READINESS_TIMEOUT",
                snapshot.Error ?? "The current generation did not become ready before the bounded timeout.", "Readiness");

        InspectQuicktestFailure(report, snapshot);
    }

    private void InspectQuicktestFailure(DoctorAuditReport report, PersistedState snapshot)
    {
        string path = QuicktestFailureArtifact.PathFor(root);
        if (!File.Exists(path))
            return;

        try
        {
            QuicktestFailureRecord record = JsonSerializer.Deserialize<QuicktestFailureRecord>(
                File.ReadAllText(path), CoordinatorSerialization.JsonOptions);
            if (record == null)
                throw new JsonException("failure record is null");
            if (record.SchemaVersion != QuicktestFailureArtifact.CurrentSchemaVersion)
            {
                report.AddFinding(DoctorSeverities.Error, "QUICKTEST_FAILURE_SCHEMA_UNSUPPORTED",
                    "A Quicktest terminal-failure artifact uses an unsupported schema.", "Readiness",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["schemaVersion"] = record.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                        ["supportedSchemaVersion"] = QuicktestFailureArtifact.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)
                    });
                return;
            }

            bool identityMatches = snapshot != null &&
                string.Equals(record.LaunchId, snapshot.LaunchId, StringComparison.Ordinal) &&
                record.Generation == (snapshot.TargetGeneration > 0 ? snapshot.TargetGeneration : snapshot.Generation) &&
                record.ProcessId == snapshot.ProcessId && record.ProcessStartUtcTicks == snapshot.ProcessStartUtcTicks;
            if (identityMatches && string.Equals(record.FailureCode, QuicktestFailureArtifact.StableFailureCode,
                    StringComparison.Ordinal))
                report.AddFinding(DoctorSeverities.Error, "QUICKTEST_TERMINAL_FAILURE",
                    "A launch-matching Quicktest terminal-failure artifact is present.", "Readiness",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["failurePhase"] = record.FailurePhase ?? "unknown",
                        ["failureCode"] = record.FailureCode
                    });
            else
                report.AddFinding(DoctorSeverities.Warning, "QUICKTEST_FAILURE_ARTIFACT_STALE",
                    "A Quicktest failure artifact is present but does not match the current launch identity.", "Readiness",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["identityMatches"] = identityMatches.ToString().ToLowerInvariant()
                    });
        }
        catch (Exception exception)
        {
            report.AddFinding(DoctorSeverities.Error, "QUICKTEST_FAILURE_MALFORMED",
                "The Quicktest failure artifact is malformed; Doctor left it untouched for forensic inspection.",
                "Readiness", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = exception.GetType().Name,
                    ["detail"] = exception.Message
                });
        }

        if (!string.IsNullOrWhiteSpace(snapshot?.TerminalFailureCode))
            report.AddFinding(DoctorSeverities.Error, snapshot.TerminalFailureCode,
                "The persisted state records a terminal launch failure.", "Recovery",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["phase"] = snapshot.TerminalFailurePhase ?? "unknown"
                });
    }

    private void InspectRecovery(DoctorAuditReport report, PersistedState snapshot)
    {
        CrashIsolationIncident isolation = snapshot?.CrashIsolation;
        if (snapshot?.Phase == BridgePhase.ISOLATING || isolation != null &&
            !IsTerminalIsolationStatus(isolation.Status))
        {
            report.AddFinding(DoctorSeverities.Error, "CRASH_ISOLATION_ACTIVE",
                "Crash isolation is active; mutation commands remain blocked until its terminal outcome is known.",
                "Recovery", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = isolation?.Status ?? snapshot?.Phase.ToString(),
                    ["stage"] = isolation?.Stage ?? "unknown",
                    ["mutationCommandsAllowed"] = "false"
                });
        }
        else if (isolation != null)
        {
            report.AddFinding(DoctorSeverities.Info, "CRASH_ISOLATION_TERMINAL",
                "Crash isolation has a terminal recorded outcome.", "Recovery",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = isolation.Status ?? "unknown",
                    ["diagnosisCount"] = (isolation.Diagnoses ?? new List<CrashIsolationDiagnosis>()).Count
                        .ToString(CultureInfo.InvariantCulture)
                });
        }
        else
        {
            report.AddFinding(DoctorSeverities.Info, "CRASH_ISOLATION_CLEAR",
                "No active crash-isolation quarantine is recorded.", "Recovery");
        }

        bool mutationAllowed = !persistedStateLoadBlocked && snapshot?.Phase != BridgePhase.ISOLATING &&
            snapshot?.ExternalModsConfigMutation == null &&
            snapshot?.ErrorCode != ProcessInspection.ErrorCode &&
            snapshot?.ErrorCode != "MAINTENANCE_PROCESS_PRESENT";
        report.AddFinding(mutationAllowed ? DoctorSeverities.Info : DoctorSeverities.Warning,
            mutationAllowed ? "MUTATION_GUARD_CLEAR" : "MUTATION_GUARD_ACTIVE",
            mutationAllowed ? "No recovery quarantine currently blocks mutation commands."
                : "Recovery/quarantine state currently blocks mutation commands.", "Recovery",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mutationCommandsAllowed"] = mutationAllowed.ToString().ToLowerInvariant()
            });
    }

    private void InspectPermissions(DoctorAuditReport report)
    {
        string[] paths =
        {
            statePath,
            readinessPath,
            generatedManifestPath,
            QuicktestFailureArtifact.PathFor(runtimeRoot),
            generationHistoryPath,
            modsConfigPath
        };
        int readable = 0;
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;

            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                readable++;
            }
            catch (UnauthorizedAccessException)
            {
                report.AddFinding(DoctorSeverities.Error, "FILE_PERMISSION_DENIED",
                    "Doctor could not read a runtime artifact because access was denied.", "Permissions",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["path"] = path,
                        ["access"] = "read"
                    });
            }
            catch (IOException exception)
            {
                report.AddFinding(DoctorSeverities.Error, "FILE_ACCESS_FAILED",
                    "Doctor could not read a runtime artifact.", "Permissions",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["path"] = path,
                        ["access"] = "read",
                        ["detail"] = exception.Message
                    });
            }
        }

        report.AddFinding(DoctorSeverities.Info, "FILE_READ_ACCESS_CHECKED",
            "Doctor verified read access for present runtime artifacts without modifying them.", "Permissions",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["readableFileCount"] = readable.ToString(CultureInfo.InvariantCulture),
                ["writeProbe"] = "not-performed",
                ["platform"] = Environment.OSVersion.Platform.ToString()
            });

        if (OperatingSystem.IsWindows())
        {
            report.AddFinding(DoctorSeverities.Info, "FILE_WRITE_ACCESS_NOT_PROBED",
                "Doctor does not create or modify probe files; write access must be confirmed by the owning command.",
                "Permissions",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["platform"] = "windows",
                    ["aclProbe"] = "not-performed"
                });
        }
    }

    private DoctorOperationalState BuildOperationalState(PersistedState snapshot,
        ProcessStatusSnapshot processSnapshot, string modsOwnership, GenerationHistoryView history,
        ConfigurationHealth nextGenerationConfig)
    {
        DateTime now = clock.UtcNow;
        int expired = (snapshot?.Leases ?? new List<TestLease>()).Count(value => value != null &&
            value.StartedUtc != default && LeaseExpiresUtc(value) <= now);
        HashSet<string> frozen = new((snapshot?.FrozenRegistrations ?? new List<ProjectIntentSnapshot>())
            .Select(value => value.Id), StringComparer.Ordinal);
        int queued = (snapshot?.ProjectIntents ?? new List<ProjectIntentRegistration>()).Count(value =>
            value != null && string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal) &&
            !frozen.Contains(value.Id));
        bool safeToWrite = !persistedStateLoadBlocked &&
            (modsOwnership == "DEVBRIDGE_GENERATED" || modsOwnership == "BASELINE" ||
             modsOwnership == "DEVBRIDGE_PENDING") &&
            snapshot?.ExternalModsConfigMutation == null &&
            snapshot?.ModsConfigMutationAuthority != ModsConfigMutationAuthorityValues.ExternalMutated;
        bool mutationAllowed = !persistedStateLoadBlocked && snapshot?.Phase != BridgePhase.ISOLATING &&
            snapshot?.ExternalModsConfigMutation == null && snapshot?.ErrorCode != ProcessInspection.ErrorCode;
        return new DoctorOperationalState
        {
            Phase = snapshot?.Phase.ToString() ?? "UNKNOWN",
            Generation = snapshot?.Generation ?? 0,
            LaunchIdPresent = !string.IsNullOrWhiteSpace(snapshot?.LaunchId),
            ProcessId = snapshot?.ProcessId ?? 0,
            ProcessRunning = processSnapshot?.OwnedProcessRunning == true,
            MaintenanceReady = snapshot?.MaintenanceReady == true,
            RestartQueued = snapshot?.RestartPending == true,
            TargetGeneration = snapshot?.TargetGeneration ?? 0,
            ActiveLeaseCount = snapshot?.Leases?.Count ?? 0,
            ExpiredLeaseCount = expired,
            ProfileFingerprint = snapshot?.ProfileFingerprint,
            FrozenProfileFingerprint = snapshot?.FrozenProfileFingerprint,
            ModsConfigOwnership = modsOwnership ?? snapshot?.ModsConfigOwnership,
            ModsConfigSafeToWrite = safeToWrite,
            ReadinessState = snapshot?.ErrorCode?.StartsWith("READINESS_", StringComparison.Ordinal) == true
                ? "error" : File.Exists(readinessPath) ? "artifact-present" : "not-present",
            CrashIsolationStatus = snapshot?.CrashIsolation?.Status ?? "clear",
            MutationCommandsAllowed = mutationAllowed,
            QueuedProjectIntentCount = queued,
            CurrentAcceptedGeneration = history?.CurrentGeneration ?? snapshot?.Generation ?? 0,
            PreviousAcceptedGeneration = history?.PreviousGeneration,
            LastKnownGoodGeneration = history?.LastKnownGoodGeneration,
            CurrentProfileMatchesLastKnownGood = history?.ProfileComparison?.SameProfile,
            TerminalFailureCode = snapshot?.TerminalFailureCode ??
                (snapshot?.Phase == BridgePhase.ERROR ? snapshot.ErrorCode : null),
            TerminalFailureDetail = snapshot?.TerminalFailureDetail ??
                (snapshot?.Phase == BridgePhase.ERROR ? snapshot.Error : null),
            HistoryCorrupt = history?.Corrupt == true,
            CurrentGenerationTrust = CurrentGenerationTrust(history, snapshot),
            NextGenerationConfig = nextGenerationConfig
        };
    }

    private static int QueuedProjectIntentCount(PersistedState snapshot)
    {
        HashSet<string> frozen = new((snapshot?.FrozenRegistrations ?? new List<ProjectIntentSnapshot>())
            .Select(value => value.Id), StringComparer.Ordinal);
        return (snapshot?.ProjectIntents ?? new List<ProjectIntentRegistration>()).Count(value =>
            value != null && string.Equals(value.Status, "ACTIVE", StringComparison.Ordinal) &&
            !frozen.Contains(value.Id));
    }

    private static string AssemblyVersion(Assembly assembly)
    {
        try
        {
            return assembly?.GetName().Version?.ToString(3);
        }
        catch
        {
            return null;
        }
    }

    private static string AssemblyVersion(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            return AssemblyName.GetAssemblyName(path).Version?.ToString(3);
        }
        catch
        {
            return null;
        }
    }

    private string ReadAboutVersion()
    {
        string path = Path.Combine(root, "About", "About.xml");
        if (!File.Exists(path))
            return null;
        try
        {
            return XDocument.Load(path).Root?.Element("modVersion")?.Value?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private string FindBridgeToolsAssembly()
    {
        string deployedPath = ExpectedBridgeToolsAssemblyPath();
        return File.Exists(deployedPath) ? deployedPath : null;
    }

    private string FindModLocalBridgeToolsAssembly()
    {
        string modLocalPath = Path.Combine(root, "BridgeTools", "DevBridge2.BridgeTools.dll");
        return File.Exists(modLocalPath) ? modLocalPath : null;
    }

    private string ExpectedBridgeToolsAssemblyPath()
    {
        string configuredRimWorldRoot = Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRimWorldRoot))
        {
            return Path.Combine(
                Path.GetFullPath(configuredRimWorldRoot),
                "BridgeTools",
                Path.GetFileName(root),
                "DevBridge2.BridgeTools.dll");
        }

        DirectoryInfo modRoot = new(root);
        DirectoryInfo modsRoot = modRoot.Parent;
        DirectoryInfo rimWorldRoot = modsRoot?.Parent;
        if (rimWorldRoot == null || !string.Equals(modsRoot.Name, "Mods", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(root, "..", "..", "BridgeTools", modRoot.Name, "DevBridge2.BridgeTools.dll");

        return Path.Combine(rimWorldRoot.FullName, "BridgeTools", modRoot.Name, "DevBridge2.BridgeTools.dll");
    }

    private string FindSourceBridgeToolsAssembly()
    {
        string[] candidates =
        {
            Path.Combine(root, "Source", "BridgeTools", "bin", "Release", "DevBridge2.BridgeTools.dll"),
            Path.Combine(root, "Source", "BridgeTools", "bin", "Debug", "DevBridge2.BridgeTools.dll")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ProtocolSkewCode(string componentVersion, string coordinatorVersion)
    {
        if (Version.TryParse(componentVersion, out Version component) &&
            Version.TryParse(coordinatorVersion, out Version coordinator))
        {
            if (component.Major < coordinator.Major)
                return "MOD_PROTOCOL_TOO_OLD";
            if (component.Major > coordinator.Major)
                return "MOD_PROTOCOL_TOO_NEW";
        }
        return "COMPONENT_VERSION_SKEW";
    }
}
