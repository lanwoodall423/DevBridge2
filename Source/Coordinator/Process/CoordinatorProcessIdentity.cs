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
                record = JsonSerializer.Deserialize<ReadinessRecord>(File.ReadAllText(readinessPath), Program.JsonOptions);
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

        try
        {
            using IManagedProcess process = processAdapter.Open(processId);
            return IsOwnedProcess(process, startTicks);
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
                if (processId == state.ProcessId && startTicks == state.ProcessStartUtcTicks)
                    ownedProcessRunning = true;
                else
                    unmanagedProcesses.Add(new UnmanagedRimWorldProcess { ProcessId = processId });
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
