using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DevBridge2;

namespace DevBridge.Coordinator;

/// <summary>
/// Performs the one-time migration from the pre-v2 32-bit runtime slot.
/// This is deliberately a host-side maintenance operation: normal coordinator
/// startup continues to reject legacy state until this command has proved that
/// the old namespace is no longer owned and the persisted process is absent.
/// </summary>
internal static class CoordinatorLegacySlotMigration
{
    private const string StateFileName = "state.json";
    private const string MigrationLockFileName = "state.json.legacy-slot-migration.lock";

    internal static bool IsCommand(IReadOnlyList<string> command)
    {
        string[] normalized = command
            .Where(value => !string.Equals(value, "--json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return normalized.Length == 2 &&
            string.Equals(normalized[0], "coordinator", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalized[1], "migrate-legacy-slot", StringComparison.OrdinalIgnoreCase);
    }

    internal static int Run(string root, IReadOnlyList<string> command)
    {
        bool json = command.Any(value => string.Equals(value, "--json", StringComparison.OrdinalIgnoreCase));
        CoordinatorLegacySlotMigrationResult result = TryMigrate(root);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, Program.JsonOptions));
        }
        else if (result.Success)
        {
            Console.WriteLine(result.AlreadyMigrated
                ? "Legacy runtime slot migration is already complete."
                : "Legacy runtime slot migration completed atomically.");
            Console.WriteLine("Old slot: " + result.OldRuntimeSlotId);
            Console.WriteLine("Current slot: " + result.RuntimeSlotId);
            Console.WriteLine("State backup: " + result.BackupPath);
            Console.WriteLine("Updated scope tickets: " + result.MigratedScopeTicketCount);
        }
        else
        {
            Console.Error.WriteLine("Legacy runtime slot migration failed [" + result.ErrorCode + "]: " +
                result.Error);
        }
        return result.ExitCode;
    }

    internal static CoordinatorLegacySlotMigrationResult TryMigrate(string root)
    {
        try
        {
            string canonicalRoot = RuntimeScope.CanonicalizeRootPath(root);
            string runtimeRoot = Path.Combine(canonicalRoot, "Runtime");
            string statePath = Path.Combine(runtimeRoot, StateFileName);
            if (!File.Exists(statePath))
                return Failure("MIGRATION_STATE_MISSING", "Runtime/state.json does not exist.");

            string migrationLockPath = Path.Combine(runtimeRoot, MigrationLockFileName);
            using FileStream migrationLock = new(migrationLockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);

            byte[] originalBytes = File.ReadAllBytes(statePath);
            if (!TryReadState(originalBytes, out PersistedState state))
                return Failure("MIGRATION_STATE_MALFORMED", "Runtime/state.json is not a supported JSON state artifact.");

            if (state.SchemaVersion < 0 || state.SchemaVersion > DevBridgeSchemaVersions.RuntimeState)
                return Failure("MIGRATION_SCHEMA_UNSUPPORTED", "Runtime/state.json uses an unsupported schema version.");

            if (!RuntimeScope.PathsEqual(state.CoordinatorRoot, canonicalRoot))
                return Failure("MIGRATION_ROOT_MISMATCH", "Runtime/state.json belongs to a different coordinator root.");

            string currentSlot = RuntimeScope.ForRoot(canonicalRoot);
            string oldSlot = state.RuntimeSlotId;
            if (string.Equals(oldSlot, currentSlot, StringComparison.Ordinal))
            {
                return Success(oldSlot, currentSlot, null, 0, alreadyMigrated: true);
            }

            string expectedLegacySlot = RuntimeScope.LegacyForRoot(canonicalRoot);
            if (!RuntimeScope.IsLegacyRuntimeSlot(oldSlot))
                return Failure("MIGRATION_NOT_REQUIRED", "Runtime/state.json does not contain the legacy runtime slot format.");
            if (!string.Equals(oldSlot, expectedLegacySlot, StringComparison.OrdinalIgnoreCase))
                return Failure("MIGRATION_LEGACY_SLOT_ROOT_MISMATCH",
                    "The legacy runtime slot does not belong to this coordinator root.");

            CoordinatorLegacySlotMigrationResult safetyFailure = ValidateSafeState(state);
            if (safetyFailure != null)
                return safetyFailure;

            if (!TryAcquireMutex(LegacyPipeNames.MutexForSlot(canonicalRoot, oldSlot),
                    out Mutex legacyMutex, out bool legacyMutexOwned, out bool legacyMutexAbandoned))
            {
                return Failure("MIGRATION_LEGACY_COORDINATOR_RUNNING",
                    "The coordinator that owns the legacy runtime slot is still running.");
            }

            try
            {
                if (!TryAcquireMutex(PipeNames.MutexForSlot(canonicalRoot, currentSlot),
                        out Mutex currentMutex, out bool currentMutexOwned, out bool currentMutexAbandoned))
                {
                    return Failure("MIGRATION_CURRENT_COORDINATOR_RUNNING",
                        "A coordinator already owns the current runtime slot.");
                }

                try
                {
                    byte[] currentBytes = File.ReadAllBytes(statePath);
                    if (!originalBytes.SequenceEqual(currentBytes))
                    {
                        return Failure("MIGRATION_STATE_CHANGED",
                            "Runtime/state.json changed while migration ownership was being acquired.");
                    }

                    if (!TryBuildMigratedJson(originalBytes, state, oldSlot, currentSlot,
                            out byte[] migratedBytes, out int migratedTicketCount))
                    {
                        return Failure("MIGRATION_STATE_MALFORMED",
                            "Runtime/state.json did not contain the expected runtime-slot fields.");
                    }

                    string backupPath = NextBackupPath(statePath);
                    try
                    {
                        File.Copy(statePath, backupPath, overwrite: false);
                    }
                    catch (Exception exception) when (exception is IOException ||
                                                       exception is UnauthorizedAccessException)
                    {
                        return Failure("MIGRATION_BACKUP_FAILED",
                            "The original Runtime/state.json could not be preserved as a backup.");
                    }

                    string tempPath = statePath + ".legacy-slot-migration.tmp";
                    string replacementBackupPath = statePath + ".legacy-slot-replace.bak";
                    try
                    {
                        WriteDurably(tempPath, migratedBytes);
                        TryDelete(replacementBackupPath);
                        // The original state has already been copied byte-for-byte
                        // to the operator-visible backup. Ignore metadata-only
                        // replacement differences while retaining the atomic
                        // destination swap on Windows.
                        File.Replace(tempPath, statePath, replacementBackupPath,
                            ignoreMetadataErrors: true);
                        TryDelete(replacementBackupPath);
                    }
                    catch (Exception exception) when (exception is IOException ||
                                                       exception is UnauthorizedAccessException ||
                                                       exception is PlatformNotSupportedException)
                    {
                        TryDelete(tempPath);
                        TryDelete(replacementBackupPath);
                        return Failure("MIGRATION_ATOMIC_REPLACE_FAILED",
                            "The migrated state could not be atomically installed; the original state remains in the backup.");
                    }

                    return Success(oldSlot, currentSlot, RelativePath(canonicalRoot, backupPath),
                        migratedTicketCount, alreadyMigrated: false,
                        legacyMutexAbandoned || currentMutexAbandoned);
                }
                finally
                {
                    ReleaseMutex(currentMutex, currentMutexOwned);
                }
            }
            finally
            {
                ReleaseMutex(legacyMutex, legacyMutexOwned);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("MIGRATION_ACCESS_DENIED", "The migration could not access the coordinator runtime files.");
        }
        catch (IOException)
        {
            return Failure("MIGRATION_IO_FAILED", "The migration could not safely access the coordinator runtime files.");
        }
        catch (Exception)
        {
            return Failure("MIGRATION_FAILED", "The migration stopped before changing durable coordinator state.");
        }
    }

    private static CoordinatorLegacySlotMigrationResult ValidateSafeState(PersistedState state)
    {
        if (state.Leases?.Count > 0)
            return Failure("MIGRATION_ACTIVE_LEASES", "Active test leases must be released before migration.");

        if (state.RestartPending || state.AggregateFreezePending || state.LaunchAttemptStarted)
            return Failure("MIGRATION_ACTIVE_OPERATION", "An active coordinator operation must finish before migration.");

        if (state.Phase != BridgePhase.ERROR && state.Phase != BridgePhase.STOPPED)
            return Failure("MIGRATION_ACTIVE_LIFECYCLE", "Migration is supported only from STOPPED or ERROR state.");

        if (state.ProcessId <= 0)
            return null;

        try
        {
            using Process process = Process.GetProcessById(state.ProcessId);
            if (process.HasExited)
                return null;

            if (state.ProcessStartUtcTicks <= 0)
                return Failure("MIGRATION_PROCESS_IDENTITY_UNAVAILABLE",
                    "A persisted process ID exists without a verifiable process identity.");

            long actualStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            if (actualStartUtcTicks == state.ProcessStartUtcTicks)
                return Failure("MIGRATION_PROCESS_RUNNING",
                    "The RimWorld process recorded in Runtime/state.json is still running.");

            // The PID has been reused by a different process. It is not the
            // process identity recorded by the coordinator and is safe to leave
            // untouched during this namespace-only migration.
            return null;
        }
        catch (ArgumentException)
        {
            // The persisted PID no longer exists.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Failure("MIGRATION_PROCESS_INSPECTION_FAILED",
                "The persisted process could not be inspected; migration stopped fail-closed.");
        }
    }

    private static bool TryReadState(byte[] bytes, out PersistedState state)
    {
        state = null;
        try
        {
            state = JsonSerializer.Deserialize<PersistedState>(bytes, CoordinatorSerialization.JsonOptions);
            return state != null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryBuildMigratedJson(byte[] originalBytes, PersistedState state,
        string oldSlot, string currentSlot, out byte[] migratedBytes, out int migratedTicketCount)
    {
        migratedBytes = null;
        migratedTicketCount = 0;
        JsonNode document;
        try
        {
            document = JsonNode.Parse(Encoding.UTF8.GetString(originalBytes));
        }
        catch (JsonException)
        {
            return false;
        }

        if (document is not JsonObject rootObject)
            return false;

        JsonNode slotNode = FindProperty(rootObject, "RuntimeSlotId");
        if (slotNode == null)
            return false;
        SetProperty(rootObject, "RuntimeSlotId", currentSlot);

        JsonNode ticketsNode = FindProperty(rootObject, "ScopeTickets");
        if (state.ScopeTickets != null && state.ScopeTickets.Count > 0)
        {
            if (ticketsNode is not JsonArray tickets || tickets.Count < state.ScopeTickets.Count)
                return false;

            for (int index = 0; index < state.ScopeTickets.Count; index++)
            {
                ScopeTicket ticket = state.ScopeTickets[index];
                if (ticket == null)
                    return false;
                if (RuntimeScope.IsLegacyRuntimeSlot(ticket.RuntimeSlotId) &&
                    !string.Equals(ticket.RuntimeSlotId, oldSlot, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.Equals(ticket.RuntimeSlotId, oldSlot, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ticket.CoordinatorRoot != null &&
                    !RuntimeScope.PathsEqual(ticket.CoordinatorRoot, state.CoordinatorRoot))
                    return false;

                if (tickets[index] is not JsonObject ticketObject)
                    return false;
                SetProperty(ticketObject, "RuntimeSlotId", currentSlot);
                migratedTicketCount++;
            }
        }

        try
        {
            migratedBytes = Encoding.UTF8.GetBytes(document.ToJsonString(CoordinatorSerialization.JsonOptions));
            return migratedBytes.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonNode FindProperty(JsonObject objectNode, string propertyName)
    {
        return objectNode.FirstOrDefault(value =>
            string.Equals(value.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static void SetProperty(JsonObject objectNode, string propertyName, string value)
    {
        string existingName = objectNode.FirstOrDefault(entry =>
            string.Equals(entry.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Key;
        objectNode[existingName ?? propertyName] = value;
    }

    private static bool TryAcquireMutex(string name, out Mutex mutex, out bool owned, out bool abandoned)
    {
        mutex = null;
        owned = false;
        abandoned = false;
        try
        {
            mutex = new Mutex(false, name);
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
                abandoned = true;
            }
            if (owned)
                return true;

            mutex.Dispose();
            mutex = null;
            return false;
        }
        catch
        {
            mutex?.Dispose();
            mutex = null;
            return false;
        }
    }

    private static void ReleaseMutex(Mutex mutex, bool owned)
    {
        if (mutex == null)
            return;
        try
        {
            if (owned)
                mutex.ReleaseMutex();
        }
        finally
        {
            mutex.Dispose();
        }
    }

    private static string NextBackupPath(string statePath)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        string candidate = statePath + ".legacy-slot-" + stamp + ".bak";
        int suffix = 1;
        while (File.Exists(candidate))
            candidate = statePath + ".legacy-slot-" + stamp + "-" + suffix++ + ".bak";
        return candidate;
    }

    private static void WriteDurably(string path, byte[] bytes)
    {
        TryDelete(path);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, options: FileOptions.WriteThrough);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path);

    private static CoordinatorLegacySlotMigrationResult Success(string oldSlot, string currentSlot,
        string backupPath, int migratedTicketCount, bool alreadyMigrated, bool recoveredAbandonedMutex = false) => new()
    {
        Success = true,
        ExitCode = 0,
        OldRuntimeSlotId = oldSlot,
        RuntimeSlotId = currentSlot,
        BackupPath = backupPath,
        MigratedScopeTicketCount = migratedTicketCount,
        AlreadyMigrated = alreadyMigrated,
        RecoveredAbandonedMutex = recoveredAbandonedMutex
    };

    private static CoordinatorLegacySlotMigrationResult Failure(string errorCode, string error) => new()
    {
        Success = false,
        ExitCode = 4,
        ErrorCode = errorCode,
        Error = error
    };
}

/// <summary>
/// The old coordinator hashed the slot as a path and truncated the digest to
/// 80 bits when constructing its named mutex. Keep this compatibility helper
/// isolated from the current opaque-identifier naming implementation.
/// </summary>
internal static class LegacyPipeNames
{
    internal static string MutexForSlot(string root, string slot)
    {
        string historicalPath = Path.GetFullPath(slot, root).ToUpperInvariant();
        using SHA256 sha = SHA256.Create();
        string hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(historicalPath)))[..20];
        return "Local\\DevBridge2CoordinatorSlot-" + hash;
    }
}

internal sealed class CoordinatorLegacySlotMigrationResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }
    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; set; }
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; set; }
    [JsonPropertyName("oldRuntimeSlotId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string OldRuntimeSlotId { get; set; }
    [JsonPropertyName("runtimeSlotId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string RuntimeSlotId { get; set; }
    [JsonPropertyName("backupPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BackupPath { get; set; }
    [JsonPropertyName("migratedScopeTicketCount")]
    public int MigratedScopeTicketCount { get; set; }
    [JsonPropertyName("alreadyMigrated")]
    public bool AlreadyMigrated { get; set; }
    [JsonPropertyName("recoveredAbandonedMutex")]
    public bool RecoveredAbandonedMutex { get; set; }
}
