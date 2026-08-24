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
    private bool IsReadinessMatch(string launchId, int processId, int targetGeneration, DateTime launchStartedUtc)
    {
        try
        {
            if (!File.Exists(readinessPath))
                return false;

            ReadinessRecord record;
            try
            {
                record = JsonSerializer.Deserialize<ReadinessRecord>(File.ReadAllText(readinessPath), CoordinatorSerialization.JsonOptions);
            }
            catch (JsonException exception)
            {
                lock (gate)
                    RecordPersistedArtifactErrorLocked("READINESS_MALFORMED",
                        "Runtime/readiness.json was invalid: " + exception.Message);
                return false;
            }

            if (record == null)
            {
                lock (gate)
                    RecordPersistedArtifactErrorLocked("READINESS_MALFORMED",
                        "Runtime/readiness.json did not contain a readiness record.");
                return false;
            }
            if (record.SchemaVersion < 0)
            {
                lock (gate)
                    RecordPersistedArtifactErrorLocked("READINESS_SCHEMA_INVALID",
                        "Runtime/readiness.json contains an invalid schema version: " +
                        record.SchemaVersion + ".");
                return false;
            }
            if (record.SchemaVersion > DevBridgeSchemaVersions.Readiness)
            {
                lock (gate)
                    RecordPersistedArtifactErrorLocked("READINESS_SCHEMA_UNSUPPORTED",
                        "Runtime/readiness.json uses unsupported schema version " +
                        record.SchemaVersion + ".");
                return false;
            }
            if (!string.Equals(record.LaunchId, launchId, StringComparison.Ordinal))
                return false;
            if (record.ProcessId != processId || record.Generation != targetGeneration)
                return false;
            if (!string.IsNullOrWhiteSpace(record.InstallationId) &&
                !string.Equals(record.InstallationId, state.InstallationId, StringComparison.Ordinal))
                return false;
            if (!string.IsNullOrWhiteSpace(record.RuntimeSlotId) &&
                !string.Equals(record.RuntimeSlotId, state.RuntimeSlotId, StringComparison.Ordinal))
                return false;
            if (record.ProcessStartUtcTicks > 0 &&
                record.ProcessStartUtcTicks != state.ProcessStartUtcTicks)
                return false;
            return record.TimestampUtc.ToUniversalTime() >= launchStartedUtc.ToUniversalTime().AddSeconds(-2);
        }
        catch
        {
            return false;
        }
    }

    private bool IsOwnedProcess(int processId, long startTicks)
    {
        if (processId <= 0)
            return false;

        DateTime inspectionDeadline = clock.UtcNow.Add(options.ProcessInspectionRetryTimeout);
        try
        {
            while (true)
            {
                try
                {
                    using IManagedProcess process = processAdapter.Open(processId);
                    return IsOwnedProcess(process, startTicks);
                }
                catch (ProcessInspectionException)
                {
                    // Restart preflight and readiness probes can cross the same
                    // Windows exit/module boundary as StopOwnedProcess. Re-open
                    // the persisted PID and retry only within a bounded window;
                    // persistent uncertainty remains fail-closed.
                    if (clock.UtcNow >= inspectionDeadline)
                        throw;

                    TimeSpan remaining = inspectionDeadline - clock.UtcNow;
                    clock.Sleep(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
                }
            }
        }
        catch (ProcessInspectionException)
        {
            throw;
        }
        catch
        {
            throw ProcessInspection.Failure();
        }
    }

    private bool IsOwnedProcess(IManagedProcess process, long startTicks)
    {
        try
        {
            if (process == null || process.HasExited)
                return false;
            return IsExactProcessIdentity(process, startTicks);
        }
        catch (ProcessInspectionException)
        {
            throw;
        }
        catch
        {
            throw ProcessInspection.Failure();
        }
    }

    private bool HasCachedOwnedProcessPathProofLocked(int processId, long startTicks)
    {
        if (state == null || processId <= 0 || startTicks <= 0 ||
            state.ProcessId != processId || state.ProcessStartUtcTicks != startTicks ||
            string.IsNullOrWhiteSpace(state.OwnedProcessExecutablePath))
            return false;

        try
        {
            return RuntimeScope.PathsEqual(state.OwnedProcessExecutablePath, rimWorldExe);
        }
        catch
        {
            return false;
        }
    }

    private ProcessOwnershipObservation InspectOwnedProcessForLifecycle(int processId,
        long startTicks, bool allowCachedPathProof)
    {
        const string freshProof = "fresh-executable-proof";
        const string cachedProof = "durable-executable-proof";
        string ownershipSource = allowCachedPathProof ? cachedProof : freshProof;

        if (processId <= 0 || startTicks <= 0)
            return ObserveOwnership(ProcessOwnershipClassification.IdentityMismatch, processId,
                stage: "process.identity", processIdMatch: false, startIdentityMatch: false,
                executableIdentityMatch: false, ownershipSource: ownershipSource);

        IManagedProcess process = null;
        try
        {
            process = processAdapter.Open(processId);
            if (process == null)
                return ObserveOwnership(ProcessOwnershipClassification.Missing, processId,
                    stage: "process.open", processIdMatch: null, startIdentityMatch: null,
                    executableIdentityMatch: null, ownershipSource: ownershipSource);

            int actualProcessId = process.Id;
            if (actualProcessId <= 0)
                return ObserveOwnership(ProcessOwnershipClassification.InspectionUnavailable,
                    processId, stage: "process.id", processIdMatch: null,
                    startIdentityMatch: null, executableIdentityMatch: null, ownershipSource: ownershipSource);
            if (actualProcessId != processId)
                return ObserveOwnership(ProcessOwnershipClassification.IdentityMismatch,
                    processId, stage: "process.id", processIdMatch: false,
                    startIdentityMatch: null, executableIdentityMatch: null, ownershipSource: ownershipSource);

            bool exited = process.HasExited;
            long actualStartTicks = process.StartIdentity;
            if (actualStartTicks <= 0)
                return ObserveOwnership(ProcessOwnershipClassification.InspectionUnavailable,
                    processId, stage: "process.start-time", processIdMatch: true,
                    startIdentityMatch: null, executableIdentityMatch: null, ownershipSource: ownershipSource);
            if (actualStartTicks != startTicks)
                return ObserveOwnership(ProcessOwnershipClassification.IdentityMismatch,
                    processId, stage: "process.start-time", processIdMatch: true,
                    startIdentityMatch: false, executableIdentityMatch: null, ownershipSource: ownershipSource);

            if (allowCachedPathProof)
            {
                bool pathAvailable = TryReadOptionalExecutableIdentity(process,
                    out bool pathMatches, out string pathStage);
                if (pathAvailable && !pathMatches)
                    return ObserveOwnership(ProcessOwnershipClassification.IdentityMismatch,
                        processId, pathStage, processIdMatch: true, startIdentityMatch: true,
                        executableIdentityMatch: false, ownershipSource: ownershipSource);

                return ObserveOwnership(exited ? ProcessOwnershipClassification.OwnedExited :
                        ProcessOwnershipClassification.OwnedRunning,
                    processId, pathAvailable ? null : pathStage, processIdMatch: true,
                    startIdentityMatch: true, executableIdentityMatch: pathAvailable ? true : null,
                    ownershipSource);
            }

            bool requiredPathAvailable = TryReadOptionalExecutableIdentity(process,
                out bool requiredPathMatches, out string requiredPathStage);
            if (!requiredPathAvailable)
                return ObserveOwnership(ProcessOwnershipClassification.InspectionUnavailable,
                    processId, requiredPathStage, processIdMatch: true, startIdentityMatch: true,
                    executableIdentityMatch: null, ownershipSource: ownershipSource);
            if (!requiredPathMatches)
                return ObserveOwnership(ProcessOwnershipClassification.IdentityMismatch,
                    processId, requiredPathStage, processIdMatch: true, startIdentityMatch: true,
                    executableIdentityMatch: false, ownershipSource: ownershipSource);

            return ObserveOwnership(exited ? ProcessOwnershipClassification.OwnedExited :
                    ProcessOwnershipClassification.OwnedRunning,
                processId, requiredPathStage, processIdMatch: true, startIdentityMatch: true,
                executableIdentityMatch: true, ownershipSource: ownershipSource);
        }
        catch (ProcessInspectionException exception)
        {
            return ObserveOwnership(ProcessOwnershipClassification.InspectionUnavailable,
                processId, exception.Stage ?? "process.identity", processIdMatch: null,
                startIdentityMatch: null, executableIdentityMatch: null, ownershipSource: ownershipSource);
        }
        catch
        {
            return ObserveOwnership(ProcessOwnershipClassification.InspectionUnavailable,
                processId, "process.identity", processIdMatch: null,
                startIdentityMatch: null, executableIdentityMatch: null, ownershipSource: ownershipSource);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private bool TryReadOptionalExecutableIdentity(IManagedProcess process,
        out bool matches, out string stage)
    {
        matches = false;
        stage = "process.main-module";
        try
        {
            string executablePath = process.ExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath))
                return false;

            try
            {
                matches = RuntimeScope.PathsEqual(executablePath, rimWorldExe);
                return true;
            }
            catch
            {
                return false;
            }
        }
        catch (ProcessInspectionException exception)
        {
            stage = exception.Stage ?? stage;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private ProcessOwnershipObservation ObserveOwnership(ProcessOwnershipClassification classification,
        int processId, string stage, bool? processIdMatch, bool? startIdentityMatch,
        bool? executableIdentityMatch, string ownershipSource)
    {
        ProcessOwnershipObservation observation = new()
        {
            Classification = classification,
            ProcessId = processId,
            Stage = stage,
            ProcessIdMatch = processIdMatch,
            StartIdentityMatch = startIdentityMatch,
            ExecutableIdentityMatch = executableIdentityMatch,
            OwnershipSource = ownershipSource
        };
        string category = classification.ToString().ToUpperInvariant();
        string detail = "pid=" + processId.ToString(CultureInfo.InvariantCulture) +
            ";pidMatch=" + FormatInspectionMatch(processIdMatch) +
            ";startIdentityMatch=" + FormatInspectionMatch(startIdentityMatch) +
            ";executableIdentityMatch=" + FormatInspectionMatch(executableIdentityMatch) +
            ";ownershipSource=" + (ownershipSource ?? "none") +
            ";stage=" + (stage ?? "none");
        TraceEvent("process.ownership.classified", detail: detail, category: category,
            errorCode: classification is ProcessOwnershipClassification.IdentityMismatch or
                ProcessOwnershipClassification.InspectionUnavailable
                ? ProcessInspection.ErrorCode : null);
        return observation;
    }

    private static string FormatInspectionMatch(bool? value) =>
        value.HasValue ? value.Value.ToString().ToLowerInvariant() : "unknown";

    private bool IsExactProcessIdentityForTermination(IManagedProcess process, int processId,
        long startTicks, bool allowCachedPathProof)
    {
        try
        {
            if (process == null || processId <= 0 || startTicks <= 0)
            {
                TraceEvent("termination.identity.pid_match", success: false,
                    detail: "invalid-target");
                return false;
            }

            int actualProcessId = process.Id;
            bool processIdMatch = actualProcessId == processId;
            TraceEvent("termination.identity.pid_match", success: processIdMatch,
                detail: "expected=" + processId + ";actual=" + actualProcessId);
            if (!processIdMatch)
                return false;

            long actualStartTicks = process.StartIdentity;
            if (actualStartTicks <= 0)
                throw ProcessInspection.Failure("process.start-time");

            bool startIdentityMatch = actualStartTicks == startTicks;
            TraceEvent("termination.identity.start_match", success: startIdentityMatch,
                detail: "expected=" + startTicks + ";actual=" + actualStartTicks);
            if (!startIdentityMatch)
                return false;

            bool pathAvailable = TryReadOptionalExecutableIdentity(process,
                out bool pathMatches, out string pathStage);
            if (!pathAvailable)
            {
                TraceEvent("termination.path.unavailable", success: null,
                    errorCode: ProcessInspection.ErrorCode,
                    detail: "stage=" + (pathStage ?? "process.main-module") +
                        ";cached-path-proof=" + allowCachedPathProof.ToString().ToLowerInvariant());
                return allowCachedPathProof;
            }
            if (!pathMatches)
            {
                TraceEvent("termination.path.contradiction", success: false,
                    errorCode: "PROCESS_IDENTITY_CHANGED", detail: "executable-path-mismatch");
                return false;
            }

            TraceEvent("termination.path.match", success: true,
                detail: "executable-install-proof-matched");
            return true;
        }
        catch (ProcessInspectionException)
        {
            throw;
        }
        catch
        {
            throw ProcessInspection.Failure("process.identity");
        }
    }

    private bool IsExactProcessIdentity(IManagedProcess process, long startTicks)
    {
        try
        {
            if (process == null || startTicks <= 0)
                return false;
            string executablePath = process.ExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath))
                throw ProcessInspection.Failure();
            if (!string.Equals(Path.GetFullPath(executablePath), rimWorldExe, StringComparison.OrdinalIgnoreCase))
                return false;
            long actualStartTicks = process.StartIdentity;
            if (actualStartTicks <= 0)
                throw ProcessInspection.Failure();
            return actualStartTicks == startTicks;
        }
        catch (ProcessInspectionException)
        {
            throw;
        }
        catch
        {
            throw ProcessInspection.Failure();
        }
    }

    private bool IsExactExitedProcessIdentity(IManagedProcess process, long startTicks)
    {
        try
        {
            if (process == null || startTicks <= 0)
                return false;

            long actualStartTicks = process.StartIdentity;
            if (actualStartTicks <= 0)
                throw ProcessInspection.Failure();
            if (actualStartTicks != startTicks)
                return false;

            // Preserve the executable-path check whenever Windows still
            // exposes it. After exit, MainModule may be unavailable; the
            // exact start identity remains the ownership proof for the
            // coordinator-launched process in that expected boundary.
            try
            {
                string executablePath = process.ExecutablePath;
                if (!string.IsNullOrWhiteSpace(executablePath))
                    return string.Equals(Path.GetFullPath(executablePath), rimWorldExe,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (ProcessInspectionException)
            {
                // Expected for an exited process whose module handle is gone.
            }

            return true;
        }
        catch (ProcessInspectionException)
        {
            throw;
        }
        catch
        {
            throw ProcessInspection.Failure();
        }
    }

    private ProcessStatusSnapshot EnumerateStatusProcessesLocked()
    {
        ProcessEnumeration enumeration;
        try
        {
            enumeration = processAdapter.EnumerateRimWorld(rimWorldExe);
        }
        catch (ProcessInspectionException)
        {
            throw;
        }
        catch
        {
            throw ProcessInspection.Failure();
        }

        if (enumeration == null || !enumeration.Complete || enumeration.Processes == null)
            throw ProcessInspection.Failure();

        bool ownedProcessRunning = false;
        int matchingProcessCount = 0;
        List<UnmanagedRimWorldProcess> matchingProcesses = new();
        List<UnmanagedRimWorldProcess> unmanagedProcesses = new();
        try
        {
            foreach (IManagedProcess process in enumeration.Processes)
            {
                if (process == null)
                    throw ProcessInspection.Failure();
                int processId = process.Id;
                if (process.HasExited)
                    continue;
                string executablePath = process.ExecutablePath;
                if (string.IsNullOrWhiteSpace(executablePath))
                    throw ProcessInspection.Failure();
                if (!string.Equals(Path.GetFullPath(executablePath), rimWorldExe,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                long startTicks = process.StartIdentity;
                if (processId <= 0 || startTicks <= 0)
                    throw ProcessInspection.Failure();

                matchingProcessCount++;
                matchingProcesses.Add(new UnmanagedRimWorldProcess
                {
                    ProcessId = processId,
                    ProcessStartIdentity = startTicks
                });
                if (processId == state.ProcessId && startTicks == state.ProcessStartUtcTicks)
                    ownedProcessRunning = true;
                else
                    unmanagedProcesses.Add(new UnmanagedRimWorldProcess
                    {
                        ProcessId = processId,
                        ProcessStartIdentity = startTicks
                    });
            }
        }
        catch (ProcessInspectionException)
        {
            throw;
        }
        catch
        {
            throw ProcessInspection.Failure();
        }
        finally
        {
            foreach (IManagedProcess process in enumeration.Processes)
            {
                try { process?.Dispose(); }
                catch { }
            }
        }

        return new ProcessStatusSnapshot
        {
            OwnedProcessRunning = ownedProcessRunning,
            MatchingProcessCount = matchingProcessCount,
            MatchingProcesses = matchingProcesses,
            UnmanagedProcesses = unmanagedProcesses
        };
    }

    private List<UnmanagedRimWorldProcess> FindUnmanagedRimWorldProcesses(int processIdToExclude,
        long startTicksToExclude)
    {
        ProcessEnumeration enumeration;
        try
        {
            enumeration = processAdapter.EnumerateRimWorld(rimWorldExe);
        }
        catch (ProcessInspectionException)
        {
            throw;
        }
        catch
        {
            throw ProcessInspection.Failure();
        }

        if (enumeration == null || !enumeration.Complete || enumeration.Processes == null)
            throw ProcessInspection.Failure();

        List<UnmanagedRimWorldProcess> result = new();
        try
        {
            foreach (IManagedProcess process in enumeration.Processes)
            {
                if (process == null)
                    throw ProcessInspection.Failure();
                int processId = process.Id;
                if (process.HasExited)
                    continue;
                string executablePath = process.ExecutablePath;
                if (string.IsNullOrWhiteSpace(executablePath))
                    throw ProcessInspection.Failure();
                if (!string.Equals(Path.GetFullPath(executablePath), rimWorldExe,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                long startTicks = process.StartIdentity;
                if (processId <= 0 || startTicks <= 0)
                    throw ProcessInspection.Failure();
                if (processId == processIdToExclude && startTicks == startTicksToExclude)
                    continue;
                result.Add(new UnmanagedRimWorldProcess { ProcessId = processId });
            }
        }
        catch (ProcessInspectionException)
        {
            throw;
        }
        catch
        {
            throw ProcessInspection.Failure();
        }
        finally
        {
            foreach (IManagedProcess process in enumeration.Processes)
            {
                try { process?.Dispose(); }
                catch { }
            }
        }

        return result;
    }

}
