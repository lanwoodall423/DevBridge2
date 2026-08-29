using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBridge2;

namespace DevBridge.Coordinator;

internal enum CoordinatorLivenessState
{
    Absent,
    Starting,
    Responsive,
    BusyHealthy,
    Draining,
    Unresponsive,
    IdentityMismatch,
    IpcUnavailable,
    AcceptedOperationOwned,
    RecoveryFailed
}

internal sealed class CoordinatorControlSnapshot
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("state")] public CoordinatorLivenessState State { get; init; }
    [JsonPropertyName("underlyingState")] public CoordinatorLivenessState? UnderlyingState { get; init; }
    [JsonPropertyName("errorCode")] public string ErrorCode { get; init; }
    [JsonPropertyName("error")] public string Error { get; init; }
    [JsonPropertyName("nextAction")] public string NextAction { get; init; }
    [JsonPropertyName("runtimeRoot")] public string RuntimeRoot { get; init; }
    [JsonPropertyName("runtimeSlotId")] public string RuntimeSlotId { get; init; }
    [JsonPropertyName("coordinatorPid")] public int? CoordinatorPid { get; init; }
    [JsonPropertyName("coordinatorStartIdentity")] public long? CoordinatorStartIdentity { get; init; }
    [JsonPropertyName("coordinatorExecutable")] public string CoordinatorExecutable { get; init; }
    [JsonPropertyName("coordinatorExecutableSha256")] public string CoordinatorExecutableSha256 { get; init; }
    [JsonPropertyName("expectedCoordinatorExecutableSha256")] public string ExpectedCoordinatorExecutableSha256 { get; init; }
    [JsonPropertyName("healthPipeAvailable")] public bool HealthPipeAvailable { get; init; }
    [JsonPropertyName("durableStatePreserved")] public bool DurableStatePreserved { get; init; }
    [JsonPropertyName("acceptedOperationOwned")] public bool AcceptedOperationOwned { get; init; }
    [JsonPropertyName("recoverySafe")] public bool RecoverySafe { get; init; }
    [JsonPropertyName("replacementPid")] public int? ReplacementPid { get; init; }
    [JsonPropertyName("replacementStartIdentity")] public long? ReplacementStartIdentity { get; init; }
}

internal static class CoordinatorControlPlane
{
    private const string ControlFileName = "coordinator-control.json";
    private const int MaximumControlFileBytes = 16 * 1024;
    private static readonly TimeSpan ProbeConnectTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProbeResponseTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReplacementWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(3);

    internal static string ControlPath(string root) => Path.Combine(
        Path.GetFullPath(root), "Runtime", ControlFileName);

    internal static string HealthPipeName(string root, string slot) =>
        PipeNames.ForSlot(root, slot) + "-health";

    internal static CoordinatorControlSnapshot Probe(string root, string requestedSlot = null)
    {
        string runtimeRoot = Path.GetFullPath(root);
        string slot = RuntimeScope.ResolveEffectiveSlot(runtimeRoot, requestedSlot, null);
        string executable = CanonicalCoordinatorExecutable(runtimeRoot);
        string expectedHash = TryHash(executable);
        bool durableState = HasMatchingDurableState(runtimeRoot, slot);
        ControlRecord record = ReadControlRecord(runtimeRoot);
        ProcessObservation[] candidates = FindCoordinatorProcesses(executable);
        ProcessObservation selected = SelectCandidate(candidates, record, out bool identityMismatch,
            out int matchingCandidateCount);
        bool health = TryHealthPing(runtimeRoot, slot);
        CoordinatorLivenessState state;
        string errorCode = null;
        string error = null;
        bool recoverySafe = false;

        if (!File.Exists(executable) || expectedHash == null)
        {
            state = CoordinatorLivenessState.IdentityMismatch;
            errorCode = "DEVBRIDGE_COORDINATOR_IDENTITY_MISMATCH";
            error = "The canonical coordinator executable is absent or unreadable.";
        }
        else if (identityMismatch || (selected == null && health && matchingCandidateCount == 0))
        {
            state = CoordinatorLivenessState.IdentityMismatch;
            errorCode = "DEVBRIDGE_COORDINATOR_IDENTITY_MISMATCH";
            error = "The coordinator process identity does not match the canonical installed runtime.";
        }
        else if (selected == null)
        {
            state = record != null ? CoordinatorLivenessState.Starting : CoordinatorLivenessState.Absent;
            errorCode = state == CoordinatorLivenessState.Absent
                ? "DEVBRIDGE_COORDINATOR_ABSENT" : "DEVBRIDGE_COORDINATOR_STARTING";
            error = state == CoordinatorLivenessState.Absent
                ? "No coordinator process owns the canonical runtime slot."
                : "The coordinator has started but has not published a live control identity.";
        }
        else if (health)
        {
            state = CoordinatorLivenessState.Responsive;
        }
        else
        {
            state = record != null ? CoordinatorLivenessState.IpcUnavailable :
                CoordinatorLivenessState.Unresponsive;
            errorCode = record != null ? "DEVBRIDGE_IPC_UNAVAILABLE" :
                "DEVBRIDGE_COORDINATOR_UNRESPONSIVE";
            error = record != null
                ? "The coordinator identity is alive, but the independent health IPC boundary is unavailable."
                : "The coordinator process is alive, but its bounded health boundary did not respond.";
            recoverySafe = durableState && matchingCandidateCount == 1 && selected.ExecutableMatches &&
                selected.StartIdentity > 0 && string.Equals(selected.ExecutableHash, expectedHash,
                    StringComparison.OrdinalIgnoreCase);
        }
        BridgePhase? phase = ReadPersistedPhase(runtimeRoot);
        bool accepted = phase is BridgePhase.RESTARTING or BridgePhase.DRAINING or
            BridgePhase.WAITING_FOR_BRIDGE or BridgePhase.LOADING;
        if (accepted && state == CoordinatorLivenessState.Responsive)
            state = phase == BridgePhase.DRAINING ? CoordinatorLivenessState.Draining :
                CoordinatorLivenessState.BusyHealthy;

        CoordinatorLivenessState underlyingState = state;
        if (accepted && state is CoordinatorLivenessState.Unresponsive or
            CoordinatorLivenessState.IpcUnavailable)
            state = CoordinatorLivenessState.AcceptedOperationOwned;

        return new CoordinatorControlSnapshot
        {
            Success = state is CoordinatorLivenessState.Responsive or CoordinatorLivenessState.BusyHealthy or
                CoordinatorLivenessState.Draining,
            State = state,
            UnderlyingState = underlyingState == state ? null : underlyingState,
            ErrorCode = errorCode,
            Error = error,
            NextAction = state == CoordinatorLivenessState.Unresponsive ||
                state == CoordinatorLivenessState.IpcUnavailable ||
                state == CoordinatorLivenessState.AcceptedOperationOwned
                ? recoverySafe ? "coordinator recover --json; reconnect with status or wait-ready; do not resubmit a mutation"
                    : "do not kill or retry; preserve evidence and escalate as TOOLCHAIN_BLOCKED"
                : state == CoordinatorLivenessState.IdentityMismatch
                    ? "preserve Runtime/state.json and inspect the installed runtime identity"
                    : null,
            RuntimeRoot = runtimeRoot,
            RuntimeSlotId = slot,
            CoordinatorPid = selected?.ProcessId,
            CoordinatorStartIdentity = selected?.StartIdentity,
            CoordinatorExecutable = selected?.ExecutablePath ?? executable,
            CoordinatorExecutableSha256 = selected?.ExecutableHash,
            ExpectedCoordinatorExecutableSha256 = expectedHash,
            HealthPipeAvailable = health,
            DurableStatePreserved = durableState,
            AcceptedOperationOwned = accepted,
            RecoverySafe = recoverySafe
        };
    }

    internal static CoordinatorControlSnapshot Recover(string root, string requestedSlot = null)
    {
        string runtimeRoot = Path.GetFullPath(root);
        string slot = RuntimeScope.ResolveEffectiveSlot(runtimeRoot, requestedSlot, null);
        CoordinatorControlSnapshot before = Probe(runtimeRoot, slot);
        if (before.State is not (CoordinatorLivenessState.Unresponsive or CoordinatorLivenessState.AcceptedOperationOwned) ||
            !before.RecoverySafe || !before.CoordinatorPid.HasValue || !before.CoordinatorStartIdentity.HasValue)
            return Failure(before, "DEVBRIDGE_COORDINATOR_RECOVERY_FAILED",
                "Coordinator-only recovery was denied because the exact process identity and durable safety preconditions were not proven.");

        Process process = null;
        try
        {
            process = Process.GetProcessById(before.CoordinatorPid.Value);
            if (!Matches(process, before.CoordinatorExecutable, before.CoordinatorStartIdentity.Value,
                    before.ExpectedCoordinatorExecutableSha256))
                return Failure(before, "DEVBRIDGE_COORDINATOR_IDENTITY_MISMATCH",
                    "The coordinator identity changed before recovery began; no process was terminated.");
            process.Kill(entireProcessTree: false);
            if (!process.WaitForExit((int)ProcessExitTimeout.TotalMilliseconds))
                return Failure(before, "DEVBRIDGE_COORDINATOR_RECOVERY_FAILED",
                    "The exact coordinator process did not exit within the bounded recovery deadline.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return Failure(before, "DEVBRIDGE_COORDINATOR_RECOVERY_FAILED",
                "The exact coordinator process could not be safely recycled: " + exception.Message);
        }
        finally
        {
            process?.Dispose();
        }

        using Mutex mutex = new(false, PipeNames.MutexForSlot(runtimeRoot, slot));
        bool ownsMutex;
        try
        {
            ownsMutex = mutex.WaitOne(1000);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }
        if (!ownsMutex)
            return Failure(before, "DEVBRIDGE_COORDINATOR_RECOVERY_FAILED",
                "The coordinator slot mutex remained owned after the exact coordinator exited; no replacement was started.");
        mutex.ReleaseMutex();

        string executable = CanonicalCoordinatorExecutable(runtimeRoot);
        Process replacement;
        try
        {
            ProcessStartInfo start = new()
            {
                FileName = executable,
                WorkingDirectory = runtimeRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--server");
            start.ArgumentList.Add("--root");
            start.ArgumentList.Add(runtimeRoot);
            start.ArgumentList.Add("--runtime-slot");
            start.ArgumentList.Add(slot);
            replacement = Process.Start(start);
            if (replacement == null)
                return Failure(before, "DEVBRIDGE_COORDINATOR_RECOVERY_FAILED",
                    "The canonical coordinator replacement did not start.");
            _ = replacement.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = replacement.StandardError.BaseStream.CopyToAsync(Stream.Null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or
            IOException or UnauthorizedAccessException)
        {
            return Failure(before, "DEVBRIDGE_COORDINATOR_RECOVERY_FAILED",
                "The canonical coordinator replacement could not start: " + exception.Message);
        }

        int replacementPid = replacement.Id;
        long replacementStart = TryGetStartIdentity(replacement);
        DateTime deadline = DateTime.UtcNow.Add(ReplacementWaitTimeout);
        while (DateTime.UtcNow < deadline)
        {
            CoordinatorControlSnapshot after = Probe(runtimeRoot, slot);
            if (after.State == CoordinatorLivenessState.Responsive &&
                after.CoordinatorPid == replacementPid &&
                after.CoordinatorStartIdentity == replacementStart)
            {
                replacement.Dispose();
                return new CoordinatorControlSnapshot
                {
                    Success = true,
                    State = CoordinatorLivenessState.Responsive,
                    ErrorCode = "DEVBRIDGE_COORDINATOR_RECOVERED",
                    Error = "The exact unresponsive coordinator was safely recycled; durable state was preserved.",
                    NextAction = before.AcceptedOperationOwned
                        ? "reconnect with status or wait-ready; do not resubmit the accepted mutation"
                        : "reconnect and continue",
                    RuntimeRoot = after.RuntimeRoot,
                    RuntimeSlotId = after.RuntimeSlotId,
                    CoordinatorPid = after.CoordinatorPid,
                    CoordinatorStartIdentity = after.CoordinatorStartIdentity,
                    CoordinatorExecutable = after.CoordinatorExecutable,
                    CoordinatorExecutableSha256 = after.CoordinatorExecutableSha256,
                    ExpectedCoordinatorExecutableSha256 = after.ExpectedCoordinatorExecutableSha256,
                    HealthPipeAvailable = after.HealthPipeAvailable,
                    DurableStatePreserved = after.DurableStatePreserved,
                    AcceptedOperationOwned = before.AcceptedOperationOwned,
                    RecoverySafe = false,
                    ReplacementPid = replacementPid,
                    ReplacementStartIdentity = replacementStart
                };
            }
            Thread.Sleep(100);
        }

        try
        {
            if (!replacement.HasExited && Matches(replacement, executable, replacementStart,
                    TryHash(executable)))
                replacement.Kill(entireProcessTree: false);
        }
        catch
        {
        }
        finally
        {
            replacement.Dispose();
        }
        return Failure(before, "DEVBRIDGE_COORDINATOR_RECOVERY_FAILED",
            "The replacement coordinator did not publish a responsive health boundary within the bounded deadline.");
    }

    internal static IDisposable StartHealthServer(string root, string slot, Func<bool> draining)
    {
        return new HealthServer(Path.GetFullPath(root), slot, draining);
    }

    internal static void PublishIdentity(string root, string slot)
    {
        string runtimeRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(runtimeRoot, "Runtime"));
        ControlRecord record = new()
        {
            RuntimeRoot = runtimeRoot,
            RuntimeSlotId = slot,
            ProcessId = Environment.ProcessId,
            StartIdentity = TryGetStartIdentity(Process.GetCurrentProcess()),
            ExecutablePath = Path.GetFullPath(Environment.ProcessPath ?? string.Empty),
            ExecutableHash = TryHash(Environment.ProcessPath),
            PublishedUtc = DateTime.UtcNow
        };
        string temp = ControlPath(runtimeRoot) + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Program.JsonOptions), new UTF8Encoding(false));
        File.Move(temp, ControlPath(runtimeRoot), true);
    }

    internal static void RemoveIdentityIfOwned(string root)
    {
        try
        {
            ControlRecord record = ReadControlRecord(root);
            if (record?.ProcessId == Environment.ProcessId &&
                record.StartIdentity == TryGetStartIdentity(Process.GetCurrentProcess()))
                File.Delete(ControlPath(root));
        }
        catch
        {
        }
    }

    private sealed class HealthServer : IDisposable
    {
        private readonly string root;
        private readonly string slot;
        private readonly Func<bool> draining;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task task;

        internal HealthServer(string root, string slot, Func<bool> draining)
        {
            this.root = root;
            this.slot = slot;
            this.draining = draining;
            task = Task.Run(Run);
        }

        private void Run()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using NamedPipeServerStream pipe = new(HealthPipeName(root, slot), PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnectionAsync(cancellation.Token).GetAwaiter().GetResult();
                    using StreamReader reader = new(pipe, Encoding.UTF8, false, 1024, true);
                    using StreamWriter writer = new(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                    string request = reader.ReadLineAsync().WaitAsync(ProbeResponseTimeout).GetAwaiter().GetResult();
                    if (string.Equals(request, "ping", StringComparison.Ordinal))
                        writer.WriteLine(JsonSerializer.Serialize(new
                        {
                            success = true,
                            state = draining?.Invoke() == true ? CoordinatorLivenessState.Draining :
                                CoordinatorLivenessState.Responsive,
                            processId = Environment.ProcessId,
                            startIdentity = TryGetStartIdentity(Process.GetCurrentProcess())
                        }, Program.JsonOptions));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    if (!cancellation.IsCancellationRequested)
                        Thread.Sleep(25);
                }
            }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            try { task.Wait(1000); } catch { }
            cancellation.Dispose();
        }
    }

    private static CoordinatorControlSnapshot Failure(CoordinatorControlSnapshot source, string code, string error) =>
        new()
        {
            Success = false,
            State = CoordinatorLivenessState.RecoveryFailed,
            ErrorCode = code,
            Error = error,
            NextAction = "preserve Runtime/state.json and escalate as TOOLCHAIN_BLOCKED; do not retry a possibly accepted mutation",
            RuntimeRoot = source.RuntimeRoot,
            RuntimeSlotId = source.RuntimeSlotId,
            CoordinatorPid = source.CoordinatorPid,
            CoordinatorStartIdentity = source.CoordinatorStartIdentity,
            CoordinatorExecutable = source.CoordinatorExecutable,
            CoordinatorExecutableSha256 = source.CoordinatorExecutableSha256,
            ExpectedCoordinatorExecutableSha256 = source.ExpectedCoordinatorExecutableSha256,
            HealthPipeAvailable = source.HealthPipeAvailable,
            DurableStatePreserved = source.DurableStatePreserved,
            AcceptedOperationOwned = source.AcceptedOperationOwned,
            RecoverySafe = false
        };

    private static string CanonicalCoordinatorExecutable(string root) => Path.GetFullPath(
        Path.Combine(root, "Coordinator", "DevBridge.Coordinator.exe"));

    private static bool HasMatchingDurableState(string root, string slot)
    {
        try
        {
            string path = Path.Combine(root, "Runtime", "state.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
                return false;
            PersistedState state = JsonSerializer.Deserialize<PersistedState>(
                File.ReadAllText(path), CoordinatorSerialization.JsonOptions);
            return state != null && RuntimeScope.PathsEqual(state.CoordinatorRoot ?? root, root) &&
                string.Equals(state.RuntimeSlotId ?? slot, slot, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static BridgePhase? ReadPersistedPhase(string root)
    {
        try
        {
            string path = Path.Combine(root, "Runtime", "state.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
                return null;
            PersistedState state = JsonSerializer.Deserialize<PersistedState>(
                File.ReadAllText(path), CoordinatorSerialization.JsonOptions);
            if (state == null)
                return null;
            return state.RestartPending ? BridgePhase.RESTARTING : state.Phase;
        }
        catch
        {
            return null;
        }
    }

    private sealed class ControlRecord
    {
        public string RuntimeRoot { get; set; }
        public string RuntimeSlotId { get; set; }
        public int ProcessId { get; set; }
        public long StartIdentity { get; set; }
        public string ExecutablePath { get; set; }
        public string ExecutableHash { get; set; }
        public DateTime PublishedUtc { get; set; }
    }

    private static ControlRecord ReadControlRecord(string root)
    {
        try
        {
            string path = ControlPath(root);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumControlFileBytes)
                return null;
            return JsonSerializer.Deserialize<ControlRecord>(File.ReadAllText(path), Program.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed record ProcessObservation(int ProcessId, long StartIdentity, string ExecutablePath,
        string ExecutableHash, bool ExecutableMatches);

    private static ProcessObservation[] FindCoordinatorProcesses(string expectedExecutable)
    {
        List<ProcessObservation> result = new();
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expectedExecutable)))
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.HasExited)
                    continue;
                string path = process.MainModule?.FileName;
                long start = TryGetStartIdentity(process);
                string hash = TryHash(path);
                if (start > 0 && path != null)
                    result.Add(new ProcessObservation(process.Id, start, Path.GetFullPath(path), hash,
                        RuntimeScope.PathsEqual(path, expectedExecutable)));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return result.ToArray();
    }

    private static ProcessObservation SelectCandidate(ProcessObservation[] candidates, ControlRecord record,
        out bool identityMismatch, out int matchingCandidateCount)
    {
        ProcessObservation[] matching = candidates.Where(value => value.ExecutableMatches).ToArray();
        matchingCandidateCount = matching.Length;
        identityMismatch = false;
        if (record != null)
        {
            ProcessObservation selectedRecord = candidates.SingleOrDefault(value => value.ProcessId == record.ProcessId);
            if (selectedRecord != null)
            {
                if (selectedRecord.StartIdentity != record.StartIdentity ||
                    !RuntimeScope.PathsEqual(selectedRecord.ExecutablePath, record.ExecutablePath) ||
                    !string.Equals(selectedRecord.ExecutableHash, record.ExecutableHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    identityMismatch = true;
                    return null;
                }
                return selectedRecord;
            }
            return null;
        }
        return matching.Length == 1 ? matching[0] : null;
    }
    private static bool TryHealthPing(string root, string slot)
    {
        try
        {
            using NamedPipeClientStream pipe = new(".", HealthPipeName(root, slot), PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipe.Connect((int)ProbeConnectTimeout.TotalMilliseconds);
            using StreamReader reader = new(pipe, Encoding.UTF8, false, 1024, true);
            using StreamWriter writer = new(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            writer.WriteLine("ping");
            string line = reader.ReadLineAsync().WaitAsync(ProbeResponseTimeout).GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("success").GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    private static bool Matches(Process process, string expectedPath, long expectedStart, string expectedHash)
    {
        try
        {
            return !process.HasExited && process.Id != Environment.ProcessId &&
                TryGetStartIdentity(process) == expectedStart &&
                RuntimeScope.PathsEqual(process.MainModule?.FileName, expectedPath) &&
                string.Equals(TryHash(process.MainModule?.FileName), expectedHash,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static long TryGetStartIdentity(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    private static string TryHash(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            using SHA256 sha = SHA256.Create();
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return null;
        }
    }
}
