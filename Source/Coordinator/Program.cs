using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBridge.Coordinator;

internal static class Program
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static int Main(string[] args)
    {
        try
        {
            ParsedArguments parsed = ParsedArguments.Parse(args);
            if (string.IsNullOrWhiteSpace(parsed.Root))
                throw new ArgumentException("missing --root");

            string root = Path.GetFullPath(parsed.Root);
            Directory.CreateDirectory(root);

            if (parsed.Server)
                return CoordinatorServer.Run(root);

            if (parsed.Command.Count == 0)
            {
                PrintUsage();
                return 2;
            }

            return CoordinatorClient.Run(root, parsed.Command);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("DevBridge error: " + exception.Message);
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("DevBridge commands: status | test begin | test end <lease-id> | restart | wait-ready | doctor");
    }
}

internal sealed class ParsedArguments
{
    internal string Root { get; private set; }
    internal bool Server { get; private set; }
    internal List<string> Command { get; } = new();

    internal static ParsedArguments Parse(string[] args)
    {
        ParsedArguments result = new();
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (string.Equals(argument, "--server", StringComparison.OrdinalIgnoreCase))
            {
                result.Server = true;
                continue;
            }

            if (string.Equals(argument, "--root", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException("--root needs a path");
                result.Root = args[++index];
                continue;
            }

            result.Command.Add(argument);
        }

        return result;
    }
}

internal sealed class BridgeRequest
{
    public string Command { get; set; }
    public List<string> Arguments { get; set; } = new();
    public string Agent { get; set; }
    public int ClientProcessId { get; set; }
}

internal static class CoordinatorClient
{
    internal static int Run(string root, IReadOnlyList<string> command)
    {
        NamedPipeClientStream pipe = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        bool serverStartRequested = false;
        Exception lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                pipe = new NamedPipeClientStream(".", PipeNames.ForRoot(root), PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                pipe.Connect(500);
                break;
            }
            catch (Exception exception) when (exception is TimeoutException || exception is IOException ||
                                               exception is InvalidOperationException)
            {
                lastError = exception;
                pipe?.Dispose();
                pipe = null;
            }

            if (!serverStartRequested)
            {
                StartServer(root);
                serverStartRequested = true;
            }

            Thread.Sleep(100);
        }

        if (pipe == null || !pipe.IsConnected)
            throw new InvalidOperationException("could not connect to the DevBridge coordinator" +
                (lastError == null ? string.Empty : ": " + lastError.Message));

        using (pipe)
        using (StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
        using (StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true))
        {
            writer.AutoFlush = true;
            BridgeRequest request = new()
            {
                Command = command[0],
                Arguments = command.Skip(1).ToList(),
                Agent = AgentName(),
                ClientProcessId = Environment.ProcessId
            };
            writer.WriteLine(JsonSerializer.Serialize(request, Program.JsonOptions));

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("__DEVBRIDGE_END__|", StringComparison.Ordinal))
                {
                    string code = line.Substring("__DEVBRIDGE_END__|".Length);
                    return int.TryParse(code, out int exitCode) ? exitCode : 4;
                }

                Console.WriteLine(line);
            }
        }

        throw new IOException("the coordinator disconnected before completing the command; use DevBridge.cmd wait-ready or status");
    }

    private static string AgentName()
    {
        string configured = Environment.GetEnvironmentVariable("DEVBRIDGE_AGENT");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return Environment.UserName + "@" + Environment.MachineName;
    }

    private static void StartServer(string root)
    {
        string processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("the coordinator process path is unavailable");

        ProcessStartInfo start = new()
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = processPath;
            string entry = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entry))
                throw new InvalidOperationException("the coordinator entry assembly path is unavailable");
            start.ArgumentList.Add(entry);
        }
        else
        {
            start.FileName = processPath;
        }

        start.ArgumentList.Add("--server");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        Process.Start(start)?.Dispose();
    }
}

internal static class CoordinatorServer
{
    internal static int Run(string root)
    {
        using Mutex mutex = new(false, PipeNames.MutexForRoot(root));
        bool ownsMutex;
        try
        {
            ownsMutex = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
            return 0;

        CoordinatorState state = new(root);
        state.StartRecoveryWork();

        while (true)
        {
            NamedPipeServerStream server = null;
            try
            {
                server = new NamedPipeServerStream(PipeNames.ForRoot(root), PipeDirection.InOut, 16,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnection();
                NamedPipeServerStream connected = server;
                server = null;
                _ = Task.Run(() => HandleClient(state, connected));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("DevBridge server pipe error: " + exception.Message);
                Thread.Sleep(250);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private static void HandleClient(CoordinatorState state, NamedPipeServerStream pipe)
    {
        using (pipe)
        using (StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
        using (StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true))
        {
            writer.AutoFlush = true;
            ClientOutput output = new(writer);
            try
            {
                string requestLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    output.Write("Invalid empty coordinator request.");
                    output.Write("__DEVBRIDGE_END__|2");
                    return;
                }

                BridgeRequest request = JsonSerializer.Deserialize<BridgeRequest>(requestLine, Program.JsonOptions);
                if (request == null || string.IsNullOrWhiteSpace(request.Command))
                {
                    output.Write("Invalid coordinator request.");
                    output.Write("__DEVBRIDGE_END__|2");
                    return;
                }

                int exitCode = state.Execute(request, output.Write, () => output.Connected);
                output.Write("__DEVBRIDGE_END__|" + exitCode);
            }
            catch (Exception exception)
            {
                output.Write("DevBridge coordinator error: " + exception.Message);
                output.Write("__DEVBRIDGE_END__|2");
            }
        }
    }
}

internal sealed class ClientOutput
{
    private readonly StreamWriter writer;

    internal ClientOutput(StreamWriter writer)
    {
        this.writer = writer;
    }

    internal bool Connected { get; private set; } = true;

    internal void Write(string line)
    {
        if (!Connected)
            return;

        try
        {
            writer.WriteLine(line ?? string.Empty);
        }
        catch (IOException)
        {
            Connected = false;
        }
        catch (ObjectDisposedException)
        {
            Connected = false;
        }
    }
}

internal static class PipeNames
{
    internal static string ForRoot(string root) => "DevBridge2-" + Hash(root);

    internal static string MutexForRoot(string root) => "Local\\DevBridge2Coordinator-" + Hash(root);

    private static string Hash(string value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(value).ToUpperInvariant()));
        return Convert.ToHexString(bytes).Substring(0, 20);
    }
}

internal enum BridgePhase
{
    READY,
    DRAINING,
    RESTARTING,
    LOADING,
    ERROR,
    STOPPED
}

internal sealed class PersistedState
{
    public int Generation { get; set; }
    public BridgePhase Phase { get; set; } = BridgePhase.STOPPED;
    public string Error { get; set; }
    public string LaunchId { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStartUtcTicks { get; set; }
    public DateTime LaunchStartedUtc { get; set; }
    public int TargetGeneration { get; set; }
    public bool RestartPending { get; set; }
    public DateTime? RestartRequestedUtc { get; set; }
    public List<TestLease> Leases { get; set; } = new();
}

internal sealed class TestLease
{
    public string Id { get; set; }
    public string Agent { get; set; }
    public int ClientProcessId { get; set; }
    public int Generation { get; set; }
    public DateTime StartedUtc { get; set; }
}

internal sealed class ReadinessRecord
{
    public string LaunchId { get; set; }
    public int Generation { get; set; }
    public int ProcessId { get; set; }
    public DateTime TimestampUtc { get; set; }
}

internal sealed class UnmanagedRimWorldProcess
{
    public int ProcessId { get; set; }
}

internal sealed class CoordinatorState
{
    private const string DevBridgePackageId = "lan.devbridge2";
    private static readonly TimeSpan LeaseStaleAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    private readonly string root;
    private readonly string runtimeRoot;
    private readonly string statePath;
    private readonly string readinessPath;
    private readonly string rimWorldExe;
    private readonly string modsConfigPath;
    private readonly object gate = new();
    private PersistedState state;
    private Task restartTask;
    private Task launchTask;

    internal CoordinatorState(string root)
    {
        this.root = Path.GetFullPath(root);
        runtimeRoot = Path.Combine(this.root, "Runtime");
        statePath = Path.Combine(runtimeRoot, "state.json");
        readinessPath = Path.Combine(runtimeRoot, "readiness.json");
        rimWorldExe = Path.GetFullPath(Path.Combine(this.root, "..", "..", "RimWorldWin64.exe"));
        modsConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Config", "ModsConfig.xml");
        Directory.CreateDirectory(runtimeRoot);

        lock (gate)
        {
            state = LoadState();
            NormalizeStateLocked();
        }
    }

    internal void StartRecoveryWork()
    {
        lock (gate)
        {
            if (state.RestartPending && state.Phase == BridgePhase.LOADING && state.ProcessId > 0)
                StartMonitorLaunchLocked(state.TargetGeneration);
            else if (state.RestartPending)
                StartRestartWorkerLocked(state.TargetGeneration);
            else if (state.Phase == BridgePhase.LOADING && state.ProcessId > 0)
                StartMonitorLaunchLocked(state.TargetGeneration);
            else if (state.Phase == BridgePhase.RESTARTING && state.ProcessId <= 0)
            {
                state.Phase = BridgePhase.ERROR;
                state.Error = "The coordinator was stopped during a launch. Run DevBridge.cmd restart to retry.";
                SaveStateLocked();
            }
        }
    }

    internal int Execute(BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        string command = (request.Command ?? string.Empty).Trim().ToLowerInvariant();
        List<string> arguments = request.Arguments ?? new List<string>();

        return command switch
        {
            "status" => Status(emit),
            "doctor" => Doctor(emit),
            "wait-ready" => WaitReady(emit),
            "restart" => Restart(emit),
            "test" => Test(arguments, request, emit, connected),
            "help" => Help(emit),
            _ => Unknown(command, emit)
        };
    }

    private int Test(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        if (arguments.Count == 0)
        {
            emit("Usage: DevBridge.cmd test begin | test end <lease-id>");
            return 2;
        }

        return arguments[0].Trim().ToLowerInvariant() switch
        {
            "begin" => BeginLease(request, emit, connected),
            "end" => EndLease(arguments, emit),
            _ => Unknown("test " + arguments[0], emit)
        };
    }

    private int Help(Action<string> emit)
    {
        emit("DevBridge commands:");
        emit("  DevBridge.cmd status");
        emit("  DevBridge.cmd test begin");
        emit("  DevBridge.cmd test end <lease-id>");
        emit("  DevBridge.cmd restart");
        emit("  DevBridge.cmd wait-ready");
        emit("  DevBridge.cmd doctor");
        return 0;
    }

    private static int Unknown(string command, Action<string> emit)
    {
        emit("Unknown DevBridge command: " + command);
        emit("Use: status, test begin, test end <lease-id>, restart, wait-ready, doctor");
        return 2;
    }

    private int Status(Action<string> emit)
    {
        PersistedState snapshot;
        bool processRunning;
        List<UnmanagedRimWorldProcess> unmanagedProcesses;
        lock (gate)
        {
            SynchronizeLocked();
            snapshot = CloneStateLocked();
            processRunning = IsOwnedProcess(snapshot.ProcessId, snapshot.ProcessStartUtcTicks);
            unmanagedProcesses = FindUnmanagedRimWorldProcesses(snapshot.ProcessId, snapshot.ProcessStartUtcTicks);
        }

        emit("DevBridge2 status");
        emit("State: " + snapshot.Phase);
        emit("Generation: " + snapshot.Generation);
        emit("RimWorld: " + (processRunning ? "running" : "not running") +
            (snapshot.ProcessId > 0 ? " (PID " + snapshot.ProcessId + ")" : string.Empty));
        if (unmanagedProcesses.Count > 0)
        {
            emit("WARNING: unmanaged RimWorld process(es) detected: " +
                string.Join(", ", unmanagedProcesses.Select(value => value.ProcessId.ToString())));
            emit("Close the unmanaged process through Steam before the next DevBridge restart.");
        }
        emit("Launch ID: " + (string.IsNullOrWhiteSpace(snapshot.LaunchId) ? "none" : snapshot.LaunchId));
        emit("Active tests: " + snapshot.Leases.Count);
        foreach (TestLease lease in snapshot.Leases.OrderBy(value => value.StartedUtc))
            emit("  " + lease.Id + " - " + FormatAge(lease.StartedUtc) + " - " + lease.Agent);

        if (snapshot.RestartPending)
        {
            emit("Restart: pending for generation " + snapshot.TargetGeneration +
                (snapshot.RestartRequestedUtc.HasValue ? " (requested " + FormatAge(snapshot.RestartRequestedUtc.Value) + " ago)" : string.Empty));
            emit("New test requests are waiting for the new generation.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            emit("Error: " + snapshot.Error);

        if (snapshot.Phase == BridgePhase.READY && !snapshot.RestartPending)
        {
            emit("Test leases are shared; multiple agents may test this generation concurrently.");
            emit("Next: run DevBridge.cmd test begin before interacting with RimWorld.");
        }
        else if (snapshot.Phase == BridgePhase.ERROR)
            emit("Next: inspect Runtime and RimWorld logs, then run DevBridge.cmd restart.");
        else
            emit("DevBridge is waiting; use wait-ready to follow this operation.");

        return 0;
    }

    private int Doctor(Action<string> emit)
    {
        string modAssembly = Path.Combine(root, "1.6", "Assemblies", "DevBridge2.dll");
        string about = Path.Combine(root, "About", "About.xml");
        bool exeExists = File.Exists(rimWorldExe);
        bool modExists = File.Exists(modAssembly);
        bool aboutExists = File.Exists(about);
        bool modEnabled = IsDevBridgeModEnabled();
        PersistedState snapshot;
        bool processRunning;
        List<UnmanagedRimWorldProcess> unmanagedProcesses;
        lock (gate)
        {
            SynchronizeLocked();
            snapshot = CloneStateLocked();
            processRunning = IsOwnedProcess(snapshot.ProcessId, snapshot.ProcessStartUtcTicks);
            unmanagedProcesses = FindUnmanagedRimWorldProcesses(snapshot.ProcessId, snapshot.ProcessStartUtcTicks);
        }

        emit("DevBridge2 doctor");
        emit(Check(exeExists, "RimWorld executable: " + rimWorldExe));
        emit(Check(aboutExists, "Mod metadata: " + about));
        emit(Check(modExists, "Built mod assembly: " + modAssembly));
        emit(Check(Directory.Exists(runtimeRoot), "Runtime directory: " + runtimeRoot));
        emit(modEnabled
            ? "PASS DevBridge2 is active in " + modsConfigPath
            : "WARN DevBridge2 is not active in the current ModsConfig.xml; the coordinator will enable it before launch.");
        emit("Coordinator state: " + snapshot.Phase + ", generation " + snapshot.Generation);
        emit("Coordinator-owned RimWorld process: " + (processRunning ? "yes (PID " + snapshot.ProcessId + ")" : "no"));
        if (unmanagedProcesses.Count > 0)
            emit("WARN Unmanaged RimWorld process(es): " + string.Join(", ", unmanagedProcesses.Select(value => value.ProcessId.ToString())) +
                ". Close them through Steam before restarting.");

        if (File.Exists(readinessPath))
        {
            try
            {
                ReadinessRecord record = JsonSerializer.Deserialize<ReadinessRecord>(File.ReadAllText(readinessPath), Program.JsonOptions);
                emit("Readiness file: " + (record == null ? "invalid" :
                    record.LaunchId + ", generation " + record.Generation + ", PID " + record.ProcessId));
            }
            catch (Exception exception)
            {
                emit("WARN Readiness file could not be read: " + exception.Message);
            }
        }
        else
        {
            emit("Readiness file: not present (normal before a map is loaded)");
        }

        return exeExists && aboutExists && modExists ? 0 : 1;
    }

    private static string Check(bool passed, string text) => (passed ? "PASS " : "FAIL ") + text;

    private int BeginLease(BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        lock (gate)
        {
            SynchronizeLocked();
            if (state.Phase == BridgePhase.STOPPED && state.Generation == 0 && !state.RestartPending)
            {
                emit("No ready RimWorld generation is running.");
                emit("DevBridge is launching RimWorld with -quicktest and waiting for a map.");
                StartInitialLaunchLocked();
            }
            else if (state.Phase == BridgePhase.ERROR)
            {
                emit("RimWorld is in ERROR state: " + state.Error);
                emit("Inspect the logs, then run DevBridge.cmd restart to retry.");
                return 4;
            }
        }

        if (!WaitForReady(emit, requireNoRestart: true, connected: connected))
            return 4;

        if (!connected())
            return 4;

        TestLease lease;
        while (true)
        {
            lock (gate)
            {
                SynchronizeLocked();
                if (state.Phase == BridgePhase.ERROR)
                {
                    emit("RimWorld is in ERROR state: " + state.Error);
                    emit("Run DevBridge.cmd restart after inspecting the logs.");
                    return 4;
                }

                if (state.Phase == BridgePhase.READY && !state.RestartPending)
                {
                    if (!connected())
                        return 4;
                    lease = new TestLease
                    {
                        Id = NewLeaseIdLocked(),
                        Agent = string.IsNullOrWhiteSpace(request.Agent) ? "unknown-agent" : request.Agent,
                        ClientProcessId = request.ClientProcessId,
                        Generation = state.Generation,
                        StartedUtc = DateTime.UtcNow
                    };
                    state.Leases.Add(lease);
                    SaveStateLocked();
                    Monitor.PulseAll(gate);
                    break;
                }
            }

            emit("A restart is pending. Waiting for generation " + CurrentTargetGeneration() + "...");
            emit("Do not launch or restart RimWorld yourself. DevBridge will continue automatically.");
            WaitForStateChange();
        }

        emit(string.Empty);
        if (!connected())
        {
            ReleaseLeaseSilently(lease.Id);
            return 4;
        }
        emit("Test lease acquired: " + lease.Id);
        if (!connected())
        {
            ReleaseLeaseSilently(lease.Id);
            return 4;
        }
        emit("Generation: " + lease.Generation);
        emit(string.Empty);
        emit("When finished testing, run:");
        emit("DevBridge.cmd test end " + lease.Id);
        if (!connected())
        {
            ReleaseLeaseSilently(lease.Id);
            return 4;
        }
        return 0;
    }

    private int EndLease(IReadOnlyList<string> arguments, Action<string> emit)
    {
        if (arguments.Count < 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            emit("Usage: DevBridge.cmd test end <lease-id>");
            return 2;
        }

        string leaseId = arguments[1].Trim().ToUpperInvariant();
        lock (gate)
        {
            PruneStaleLeasesLocked();
            TestLease lease = state.Leases.FirstOrDefault(value =>
                string.Equals(value.Id, leaseId, StringComparison.OrdinalIgnoreCase));
            if (lease == null)
            {
                emit("Test lease " + leaseId + " was already released or expired.");
                return 0;
            }

            state.Leases.Remove(lease);
            SaveStateLocked();
            Monitor.PulseAll(gate);
            emit("Test lease released: " + leaseId);
            if (state.RestartPending && state.Leases.Count == 0)
                emit("No active tests remain. DevBridge will continue the pending restart automatically.");
            return 0;
        }
    }

    private int Restart(Action<string> emit)
    {
        int targetGeneration;
        bool alreadyPending;
        lock (gate)
        {
            SynchronizeLocked();
            alreadyPending = state.RestartPending;
            if (!alreadyPending)
            {
                targetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
                state.TargetGeneration = targetGeneration;
                state.RestartPending = true;
                state.RestartRequestedUtc = DateTime.UtcNow;
                state.Error = null;
                state.Phase = BridgePhase.DRAINING;
                DeleteReadinessLocked();
                SaveStateLocked();
                StartRestartWorkerLocked(targetGeneration);
                Monitor.PulseAll(gate);
            }
            else
            {
                targetGeneration = state.TargetGeneration;
            }
        }

        if (alreadyPending)
            emit("Restart is already pending for generation " + targetGeneration + ".");
        else
            emit("Restart requested for generation " + Math.Max(0, targetGeneration - 1) + ".");

        EmitRestartWait(emit);
        while (true)
        {
            lock (gate)
            {
                SynchronizeLocked();
                if (state.Generation >= targetGeneration && state.Phase == BridgePhase.READY && !state.RestartPending)
                {
                    emit(string.Empty);
                    emit("RimWorld restarted successfully.");
                    emit("Generation: " + state.Generation);
                    emit("Quicktest map is ready.");
                    return 0;
                }

                if (state.Phase == BridgePhase.ERROR && !state.RestartPending)
                {
                    emit("Restart failed: " + state.Error);
                    emit("Inspect Runtime and the RimWorld logs, then retry with DevBridge.cmd restart.");
                    return 4;
                }
            }

            WaitForStateChange(ProgressInterval);
            EmitRestartWait(emit);
        }
    }

    private int WaitReady(Action<string> emit)
    {
        lock (gate)
        {
            SynchronizeLocked();
            if (state.Phase == BridgePhase.STOPPED && state.Generation == 0 && !state.RestartPending)
            {
                emit("No ready RimWorld generation is running.");
                emit("DevBridge is launching RimWorld with -quicktest and waiting for a map.");
                StartInitialLaunchLocked();
            }
        }

        if (!WaitForReady(emit, requireNoRestart: true))
            return 4;

        lock (gate)
        {
            emit("RimWorld is ready.");
            emit("Generation: " + state.Generation);
            emit("Quicktest map is ready.");
        }
        return 0;
    }

    private bool WaitForReady(Action<string> emit, bool requireNoRestart, Func<bool> connected = null)
    {
        DateTime nextProgress = DateTime.UtcNow;
        bool first = true;
        while (true)
        {
            if (connected != null && !connected())
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
                    return false;
                }

                if (state.Phase == BridgePhase.STOPPED && state.Generation > 0 && !state.RestartPending)
                {
                    emit("RimWorld is stopped. Run DevBridge.cmd restart to launch a new generation.");
                    return false;
                }

                if (first || DateTime.UtcNow >= nextProgress)
                {
                    int target = CurrentTargetGenerationLocked();
                    emit("Waiting for RimWorld generation " + target + "...");
                    emit("State: " + state.Phase + ". Waiting for the quicktest map readiness signal.");
                    first = false;
                    nextProgress = DateTime.UtcNow.Add(ProgressInterval);
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
            emit("No active tests remain.");
            emit("State: " + snapshot.Phase + ". Waiting for generation " + snapshot.TargetGeneration +
                " quicktest map readiness.");
            return;
        }

        emit("Waiting for " + snapshot.Leases.Count + " active test" + (snapshot.Leases.Count == 1 ? "" : "s") + ":");
        foreach (TestLease lease in snapshot.Leases.OrderBy(value => value.StartedUtc))
            emit("  " + lease.Id + " - active " + FormatAge(lease.StartedUtc));
        emit("Do not terminate RimWorld.");
        emit("DevBridge will restart it automatically when these tests finish.");
    }

    private void ReleaseLeaseSilently(string leaseId)
    {
        lock (gate)
        {
            TestLease lease = state.Leases.FirstOrDefault(value =>
                string.Equals(value.Id, leaseId, StringComparison.OrdinalIgnoreCase));
            if (lease == null)
                return;
            state.Leases.Remove(lease);
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }
    }

    private void StartInitialLaunchLocked()
    {
        if (launchTask != null && !launchTask.IsCompleted)
            return;

        int target = Math.Max(1, state.Generation + 1);
        state.TargetGeneration = target;
        state.Phase = BridgePhase.RESTARTING;
        state.Error = null;
        state.LaunchId = null;
        state.ProcessId = 0;
        state.ProcessStartUtcTicks = 0;
        DeleteReadinessLocked();
        SaveStateLocked();
        launchTask = Task.Run(() => LaunchGenerationWorker(target, isRestart: false));
    }

    private void StartRestartWorkerLocked(int targetGeneration)
    {
        if (restartTask != null && !restartTask.IsCompleted)
            return;

        restartTask = Task.Run(() => RestartWorker(targetGeneration));
    }

    private void StartMonitorLaunchLocked(int targetGeneration)
    {
        if (launchTask != null && !launchTask.IsCompleted)
            return;

        launchTask = Task.Run(() => MonitorLaunchWorker(targetGeneration));
    }

    private void RestartWorker(int targetGeneration)
    {
        try
        {
            int oldProcessId;
            long oldStartTicks;
            while (true)
            {
                lock (gate)
                {
                    PruneStaleLeasesLocked();
                    if (!state.RestartPending || state.TargetGeneration != targetGeneration)
                        return;

                    if (launchTask != null && !launchTask.IsCompleted)
                    {
                        Monitor.Wait(gate, 1000);
                        continue;
                    }

                    if (state.Leases.Count > 0)
                    {
                        Monitor.Wait(gate, 1000);
                        continue;
                    }

                    state.Phase = BridgePhase.RESTARTING;
                    state.Error = null;
                    oldProcessId = state.ProcessId;
                    oldStartTicks = state.ProcessStartUtcTicks;
                    state.ProcessId = 0;
                    state.ProcessStartUtcTicks = 0;
                    DeleteReadinessLocked();
                    SaveStateLocked();
                    break;
                }
            }

            StopOwnedProcess(oldProcessId, oldStartTicks);
            LaunchGenerationWorker(targetGeneration, isRestart: true);
        }
        catch (Exception exception)
        {
            FailLaunch("restart coordinator failure: " + exception.Message);
        }
    }

    private void LaunchGenerationWorker(int targetGeneration, bool isRestart)
    {
        string launchId = Guid.NewGuid().ToString("N");
        Process process = null;
        try
        {
            lock (gate)
            {
                state.Phase = BridgePhase.LOADING;
                state.TargetGeneration = targetGeneration;
                state.LaunchId = launchId;
                state.LaunchStartedUtc = DateTime.UtcNow;
                state.Error = null;
                DeleteReadinessLocked();
                SaveStateLocked();
            }

            if (!File.Exists(rimWorldExe))
                throw new FileNotFoundException("RimWorld executable was not found", rimWorldExe);

            EnsureDevBridgeModEnabled();

            List<UnmanagedRimWorldProcess> unmanagedProcesses =
                FindUnmanagedRimWorldProcesses(processIdToExclude: 0, startTicksToExclude: 0);
            if (unmanagedProcesses.Count > 0)
                throw new InvalidOperationException("an unmanaged RimWorld process is already running (PID " +
                    string.Join(", ", unmanagedProcesses.Select(value => value.ProcessId.ToString())) +
                    "); close it through Steam before retrying");

            ProcessStartInfo start = new()
            {
                FileName = rimWorldExe,
                WorkingDirectory = Path.GetDirectoryName(rimWorldExe) ?? root,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            start.ArgumentList.Add("-quicktest");
            start.Environment["DEVBRIDGE_ROOT"] = root;
            start.Environment["DEVBRIDGE_LAUNCH_ID"] = launchId;
            start.Environment["DEVBRIDGE_GENERATION"] = targetGeneration.ToString();

            process = Process.Start(start);
            if (process == null)
                throw new InvalidOperationException("Process.Start returned no RimWorld process");

            int processId = process.Id;
            long processStartTicks = TryGetStartTicks(process);
            lock (gate)
            {
                state.ProcessId = processId;
                state.ProcessStartUtcTicks = processStartTicks;
                SaveStateLocked();
                Monitor.PulseAll(gate);
            }

            MonitorLaunchUntilReady(process, processId, processStartTicks, launchId, targetGeneration);
        }
        catch (Exception exception)
        {
            FailLaunch(DescribeLaunchFailure(exception, process));
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void MonitorLaunchWorker(int targetGeneration)
    {
        try
        {
            int processId;
            long startTicks;
            string launchId;
            lock (gate)
            {
                processId = state.ProcessId;
                startTicks = state.ProcessStartUtcTicks;
                launchId = state.LaunchId;
            }

            if (processId <= 0 || string.IsNullOrWhiteSpace(launchId))
                throw new InvalidOperationException("persisted launch information is incomplete");

            using Process process = Process.GetProcessById(processId);
            MonitorLaunchUntilReady(process, processId, startTicks, launchId, targetGeneration);
        }
        catch (Exception exception)
        {
            FailLaunch("RimWorld did not report readiness after coordinator recovery: " + exception.Message);
        }
    }

    private void MonitorLaunchUntilReady(Process process, int processId, long processStartTicks,
        string launchId, int targetGeneration)
    {
        DateTime deadline;
        lock (gate)
            deadline = state.LaunchStartedUtc.ToUniversalTime().Add(ReadinessTimeout);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    throw new InvalidOperationException("RimWorld exited before the quicktest map became ready (exit code " +
                        process.ExitCode + ")");
                }
            }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("No process",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("RimWorld exited before the quicktest map became ready");
            }

            if (IsReadinessMatch(launchId, processId, targetGeneration, deadline - ReadinessTimeout))
            {
                lock (gate)
                {
                    if (state.LaunchId == launchId && state.TargetGeneration == targetGeneration)
                    {
                        state.Generation = targetGeneration;
                        state.Phase = BridgePhase.READY;
                        state.Error = null;
                        state.RestartPending = false;
                        state.RestartRequestedUtc = null;
                        state.TargetGeneration = 0;
                        SaveStateLocked();
                        Monitor.PulseAll(gate);
                    }
                }
                return;
            }

            Thread.Sleep(1000);
        }

        throw new TimeoutException("no matching readiness signal was written within " +
            ReadinessTimeout.TotalMinutes.ToString("0") + " minutes");
    }

    private void FailLaunch(string detail)
    {
        lock (gate)
        {
            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
            state.Error = "RimWorld did not report a playable quicktest map: " + detail +
                ". Inspect Runtime/readiness.json and the RimWorld logs, then run DevBridge.cmd restart.";
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }
    }

    private static string DescribeLaunchFailure(Exception exception, Process process)
    {
        if (exception is FileNotFoundException)
            return exception.Message;
        if (process != null)
        {
            try
            {
                if (process.HasExited)
                    return "RimWorld exited before readiness (exit code " + process.ExitCode + ")";
            }
            catch
            {
                // Use the original exception below.
            }
        }

        return exception.GetType().Name + ": " + exception.Message;
    }

    private void StopOwnedProcess(int processId, long startTicks)
    {
        if (processId <= 0)
            return;

        Process process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (!IsOwnedProcess(process, startTicks))
                return;

            if (!process.HasExited)
            {
                try
                {
                    process.CloseMainWindow();
                    process.WaitForExit(5000);
                }
                catch
                {
                    // Fall through to the bounded kill below.
                }
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(15000);
            }
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
        catch (InvalidOperationException)
        {
            // The process already exited or cannot be controlled.
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void SynchronizeLocked()
    {
        PruneStaleLeasesLocked();

        if (state.Phase == BridgePhase.READY &&
            (state.ProcessId <= 0 || !IsOwnedProcess(state.ProcessId, state.ProcessStartUtcTicks)))
        {
            state.Phase = BridgePhase.STOPPED;
            state.Error = "The coordinator-owned RimWorld process is no longer running.";
            state.ProcessId = 0;
            state.ProcessStartUtcTicks = 0;
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }

        if (state.RestartPending)
        {
            if (state.TargetGeneration <= state.Generation)
                state.TargetGeneration = state.Generation + 1;
            if ((state.Phase != BridgePhase.LOADING || launchTask == null || launchTask.IsCompleted) &&
                (restartTask == null || restartTask.IsCompleted))
                StartRestartWorkerLocked(state.TargetGeneration);
        }
        else if (state.Phase == BridgePhase.LOADING && state.ProcessId > 0 &&
                 (launchTask == null || launchTask.IsCompleted))
        {
            StartMonitorLaunchLocked(state.TargetGeneration);
        }
    }

    private void PruneStaleLeasesLocked()
    {
        DateTime cutoff = DateTime.UtcNow - LeaseStaleAfter;
        int before = state.Leases.Count;
        state.Leases.RemoveAll(lease => lease == null || lease.StartedUtc.ToUniversalTime() < cutoff);
        if (state.Leases.Count != before)
        {
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }
    }

    private bool IsReadinessMatch(string launchId, int processId, int targetGeneration, DateTime launchStartedUtc)
    {
        try
        {
            if (!File.Exists(readinessPath))
                return false;

            ReadinessRecord record = JsonSerializer.Deserialize<ReadinessRecord>(File.ReadAllText(readinessPath), Program.JsonOptions);
            if (record == null || !string.Equals(record.LaunchId, launchId, StringComparison.Ordinal))
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
            using Process process = Process.GetProcessById(processId);
            return IsOwnedProcess(process, startTicks);
        }
        catch
        {
            return false;
        }
    }

    private bool IsOwnedProcess(Process process, long startTicks)
    {
        try
        {
            if (process.HasExited)
                return false;
            string executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !string.Equals(Path.GetFullPath(executablePath), rimWorldExe, StringComparison.OrdinalIgnoreCase))
                return false;
            if (startTicks <= 0)
                return true;
            long actual = process.StartTime.ToUniversalTime().Ticks;
            return Math.Abs(actual - startTicks) < TimeSpan.FromSeconds(3).Ticks;
        }
        catch
        {
            return false;
        }
    }

    private List<UnmanagedRimWorldProcess> FindUnmanagedRimWorldProcesses(int processIdToExclude,
        long startTicksToExclude)
    {
        List<UnmanagedRimWorldProcess> result = new();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("RimWorldWin64");
        }
        catch
        {
            return result;
        }

        foreach (Process process in processes)
        {
            try
            {
                if (process.HasExited)
                    continue;
                if (process.Id == processIdToExclude &&
                    (startTicksToExclude <= 0 || TryGetStartTicks(process) == startTicksToExclude))
                    continue;

                string executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath) ||
                    !string.Equals(Path.GetFullPath(executablePath), rimWorldExe, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new UnmanagedRimWorldProcess { ProcessId = process.Id });
            }
            catch
            {
                // Ignore processes that exit or cannot be inspected during enumeration.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    private static long TryGetStartTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

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

    private string NewLeaseIdLocked()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string id = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            if (state.Leases.All(lease => !string.Equals(lease.Id, id, StringComparison.OrdinalIgnoreCase)))
                return id;
        }

        return Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
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
            PersistedState loaded = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(statePath), Program.JsonOptions);
            return loaded ?? new PersistedState();
        }
        catch
        {
            string backup = statePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(statePath, backup); } catch { }
            return new PersistedState
            {
                Phase = BridgePhase.ERROR,
                Error = "Runtime/state.json was invalid and was moved to " + backup + "."
            };
        }
    }

    private void NormalizeStateLocked()
    {
        state.Leases ??= new List<TestLease>();
        state.Phase = Enum.IsDefined(state.Phase) ? state.Phase : BridgePhase.STOPPED;
        if (state.RestartPending && state.TargetGeneration <= state.Generation)
            state.TargetGeneration = state.Generation + 1;
        if (state.Phase == BridgePhase.READY && state.Generation <= 0)
            state.Phase = BridgePhase.STOPPED;
    }

    private PersistedState CloneStateLocked()
    {
        string json = JsonSerializer.Serialize(state, Program.JsonOptions);
        return JsonSerializer.Deserialize<PersistedState>(json, Program.JsonOptions) ?? new PersistedState();
    }

    private void SaveStateLocked()
    {
        Directory.CreateDirectory(runtimeRoot);
        string temporary = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Program.JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, statePath, true);
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

        string contents = File.ReadAllText(modsConfigPath);
        string normalized = contents.Replace("<li>Lan.DevBridge2</li>",
            "<li>" + DevBridgePackageId + "</li>", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(contents, normalized, StringComparison.Ordinal))
        {
            WriteModsConfig(normalized);
            return;
        }

        int activeModsEnd = contents.IndexOf("</activeMods>", StringComparison.OrdinalIgnoreCase);
        if (activeModsEnd < 0)
            throw new InvalidOperationException("ModsConfig.xml has no activeMods section at " + modsConfigPath);

        string entry = Environment.NewLine + "    <li>" + DevBridgePackageId + "</li>";
        string updated = contents.Insert(activeModsEnd, entry);
        WriteModsConfig(updated);
    }

    private void WriteModsConfig(string contents)
    {
        string temporary = modsConfigPath + ".devbridge2.tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            try
            {
                File.Replace(temporary, modsConfigPath, null);
            }
            catch
            {
                File.Delete(modsConfigPath);
                File.Move(temporary, modsConfigPath);
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string FormatAge(DateTime startedUtc)
    {
        TimeSpan age = DateTime.UtcNow - startedUtc.ToUniversalTime();
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalHours >= 1)
            return ((int)age.TotalHours).ToString("00") + ":" + age.Minutes.ToString("00") + ":" + age.Seconds.ToString("00");
        return age.Minutes.ToString("00") + ":" + age.Seconds.ToString("00");
    }
}
