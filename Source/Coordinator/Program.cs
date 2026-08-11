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
        Console.WriteLine("DevBridge commands: status | test begin | test end <lease-id> | stop <lease-id> | ensure-ready <lease-id> | restart | wait-ready | doctor");
        Console.WriteLine("Append --json for machine-readable output.");
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
    public bool Json { get; set; }
}

internal static class CoordinatorClient
{
    internal static int Run(string root, IReadOnlyList<string> command)
    {
        bool json = command.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase));
        List<string> normalizedCommand = command
            .Where(argument => !string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (normalizedCommand.Count == 0)
            throw new ArgumentException("a command is required before --json");

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
                Command = normalizedCommand[0],
                Arguments = normalizedCommand.Skip(1).ToList(),
                Agent = AgentName(),
                ClientProcessId = Environment.ProcessId,
                Json = json
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

        // CLI clients are short-lived. Their OS process ID distinguishes concurrent
        // sessions without adding persistent profiles or registration state.
        return "agent-" + Environment.ProcessId.ToString("X4");
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
            BridgeRequest request = null;
            try
            {
                string requestLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    output.Write("Invalid empty coordinator request.");
                    output.Write("__DEVBRIDGE_END__|2");
                    return;
                }

                request = JsonSerializer.Deserialize<BridgeRequest>(requestLine, Program.JsonOptions);
                if (request == null || string.IsNullOrWhiteSpace(request.Command))
                {
                    output.Write("Invalid coordinator request.");
                    output.Write("__DEVBRIDGE_END__|2");
                    return;
                }
                request.Arguments ??= new List<string>();
                request.Agent = string.IsNullOrWhiteSpace(request.Agent) ? "unknown-agent" : request.Agent.Trim();

                List<string> buffered = request.Json ? new List<string>() : null;
                Action<string> emit = request.Json ? buffered.Add : output.Write;
                int exitCode = state.Execute(request, emit, () => output.Connected && pipe.IsConnected);
                if (request.Json)
                    output.Write(JsonSerializer.Serialize(state.CreateJsonResponse(request, exitCode, buffered),
                        Program.JsonOptions));
                output.Write("__DEVBRIDGE_END__|" + exitCode);
            }
            catch (Exception exception)
            {
                if (request?.Json == true)
                {
                    output.Write(JsonSerializer.Serialize(JsonCommandResponse.Failure(
                        request.Command, exception.Message, "Run: DevBridge.cmd doctor"), Program.JsonOptions));
                }
                else
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
    public int LaunchGeneration { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStartUtcTicks { get; set; }
    public DateTime LaunchStartedUtc { get; set; }
    public int TargetGeneration { get; set; }
    public bool RestartPending { get; set; }
    public DateTime? RestartRequestedUtc { get; set; }
    public bool MaintenanceReady { get; set; }
    public bool SessionDirty { get; set; }
    public string ErrorCode { get; set; }
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

internal sealed class JsonCommandResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; }

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; }

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("rimworldPid")]
    public int RimWorldPid { get; set; }

    [JsonPropertyName("rimworldProcessStartIdentity")]
    public long RimWorldProcessStartIdentity { get; set; }

    [JsonPropertyName("gameState")]
    public string GameState { get; set; }

    [JsonPropertyName("maintenanceReady")]
    public bool MaintenanceReady { get; set; }

    [JsonPropertyName("leaseState")]
    public string LeaseState { get; set; }

    [JsonPropertyName("sessionDirty")]
    public bool SessionDirty { get; set; }

    [JsonPropertyName("launchGeneration")]
    public int LaunchGeneration { get; set; }

    [JsonPropertyName("activeTests")]
    public int ActiveTests { get; set; }

    [JsonPropertyName("restartPending")]
    public bool RestartPending { get; set; }

    [JsonPropertyName("targetGeneration")]
    public int TargetGeneration { get; set; }

    [JsonPropertyName("accepted")]
    public bool? Accepted { get; set; }

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; set; }

    [JsonPropertyName("agent")]
    public string Agent { get; set; }

    [JsonPropertyName("leases")]
    public List<JsonLeaseInfo> Leases { get; set; } = new();

    [JsonPropertyName("checks")]
    public List<string> Checks { get; set; } = new();

    [JsonPropertyName("error")]
    public string Error { get; set; }

    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; }

    [JsonPropertyName("nextAction")]
    public string NextAction { get; set; }

    internal static JsonCommandResponse Failure(string command, string error, string nextAction)
    {
        return new JsonCommandResponse
        {
            Success = false,
            Command = command,
            ExitCode = 2,
            State = BridgePhase.ERROR.ToString(),
            Error = error,
            NextAction = nextAction
        };
    }
}

internal sealed class JsonLeaseInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("agent")]
    public string Agent { get; set; }

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("startedUtc")]
    public DateTime StartedUtc { get; set; }

    [JsonPropertyName("age")]
    public string Age { get; set; }

    [JsonPropertyName("staleIn")]
    public string StaleIn { get; set; }
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

internal sealed class ProcessLaunchRequest
{
    internal string FileName { get; init; }
    internal string WorkingDirectory { get; init; }
    internal IReadOnlyList<string> Arguments { get; init; }
    internal IReadOnlyDictionary<string, string> Environment { get; init; }
}

internal interface IManagedProcess : IDisposable
{
    int Id { get; }
    long StartIdentity { get; }
    string ExecutablePath { get; }
    bool HasExited { get; }
    bool RequestTermination();
    bool WaitForExit(TimeSpan timeout);
    bool ForceTerminate();
}

internal sealed class ProcessEnumeration
{
    internal bool Complete { get; init; }
    internal string Error { get; init; }
    internal IReadOnlyList<IManagedProcess> Processes { get; init; } = Array.Empty<IManagedProcess>();
}

internal sealed class ProcessInspectionException : Exception
{
    internal ProcessInspectionException() : base(ProcessInspection.Message)
    {
    }
}

internal static class ProcessInspection
{
    internal const string ErrorCode = "PROCESS_INSPECTION_AMBIGUOUS";
    internal const string Message = "RimWorld process inspection was incomplete; process state is ambiguous.";

    internal static ProcessInspectionException Failure() => new();
}

internal interface IProcessAdapter
{
    IManagedProcess Open(int processId);
    ProcessEnumeration EnumerateRimWorld(string executablePath);
    IManagedProcess Launch(ProcessLaunchRequest request);
}

internal interface ICoordinatorClock
{
    DateTime UtcNow { get; }
    void Sleep(TimeSpan duration);
}

internal sealed class SystemCoordinatorClock : ICoordinatorClock
{
    internal static readonly SystemCoordinatorClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}

internal sealed class CoordinatorOptions
{
    internal TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromMinutes(6);
    internal TimeSpan ProcessExitTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal IProcessAdapter ProcessAdapter { get; init; } = new SystemProcessAdapter();
    internal ICoordinatorClock Clock { get; init; } = SystemCoordinatorClock.Instance;
    internal string RimWorldExecutablePath { get; init; }
    internal string ModsConfigPath { get; init; }

    internal static CoordinatorOptions ForProduction()
    {
        TimeSpan timeout = TimeSpan.FromMinutes(6);
        string configured = Environment.GetEnvironmentVariable("DEVBRIDGE_READINESS_TIMEOUT_SECONDS");
        if (int.TryParse(configured, out int seconds) && seconds >= 30 && seconds <= 3600)
            timeout = TimeSpan.FromSeconds(seconds);

        return new CoordinatorOptions { ReadinessTimeout = timeout };
    }
}

internal sealed class SystemManagedProcess : IManagedProcess
{
    private readonly Process process;

    internal SystemManagedProcess(Process process)
    {
        this.process = process;
    }

    public int Id
    {
        get
        {
            try { return process.Id; }
            catch { throw ProcessInspection.Failure(); }
        }
    }

    public long StartIdentity => TryGetStartIdentity(process);

    public string ExecutablePath
    {
        get
        {
            try
            {
                string path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path))
                    throw ProcessInspection.Failure();
                return path;
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
    }

    public bool HasExited
    {
        get
        {
            try { return process.HasExited; }
            catch { throw ProcessInspection.Failure(); }
        }
    }

    public bool RequestTermination()
    {
        try
        {
            if (process.HasExited)
                return true;
            return process.CloseMainWindow();
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

    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            int milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            process.WaitForExit(milliseconds);
            return process.HasExited;
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

    public bool ForceTerminate()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(15000);
            return process.HasExited;
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

    public void Dispose() => process.Dispose();

    private static long TryGetStartIdentity(Process process)
    {
        try
        {
            long ticks = process.StartTime.ToUniversalTime().Ticks;
            if (ticks <= 0)
                throw ProcessInspection.Failure();
            return ticks;
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
}

internal sealed class SystemProcessAdapter : IProcessAdapter
{
    public IManagedProcess Open(int processId)
    {
        try { return new SystemManagedProcess(Process.GetProcessById(processId)); }
        catch (ArgumentException) { return null; }
        catch { throw ProcessInspection.Failure(); }
    }

    public ProcessEnumeration EnumerateRimWorld(string executablePath)
    {
        List<IManagedProcess> matches = new();
        bool complete = true;
        string error = null;
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("RimWorldWin64");
        }
        catch
        {
            return new ProcessEnumeration { Complete = false, Error = ProcessInspection.Message };
        }

        foreach (Process process in processes)
        {
            SystemManagedProcess managed = null;
            try
            {
                managed = new SystemManagedProcess(process);
                if (managed.HasExited)
                {
                    managed.Dispose();
                    continue;
                }

                if (!string.Equals(Path.GetFullPath(managed.ExecutablePath),
                        Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase))
                {
                    managed.Dispose();
                    continue;
                }

                if (managed.StartIdentity <= 0)
                    throw ProcessInspection.Failure();
                matches.Add(managed);
            }
            catch
            {
                complete = false;
                error ??= ProcessInspection.Message;
                managed?.Dispose();
            }
        }

        return new ProcessEnumeration { Complete = complete, Error = error, Processes = matches };
    }

    public IManagedProcess Launch(ProcessLaunchRequest request)
    {
        ProcessStartInfo start = new()
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        foreach (string argument in request.Arguments ?? Array.Empty<string>())
            start.ArgumentList.Add(argument);
        foreach (KeyValuePair<string, string> pair in request.Environment ??
                 new Dictionary<string, string>())
            start.Environment[pair.Key] = pair.Value;

        Process process = Process.Start(start);
        return process == null ? null : new SystemManagedProcess(process);
    }
}

internal sealed class CoordinatorState
{
    private const string DevBridgePackageId = "lan.devbridge2";
    private static readonly TimeSpan LeaseStaleAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    private readonly string root;
    private readonly string runtimeRoot;
    private readonly string statePath;
    private readonly string readinessPath;
    private readonly string rimWorldExe;
    private readonly string modsConfigPath;
    private readonly CoordinatorOptions options;
    private readonly IProcessAdapter processAdapter;
    private readonly ICoordinatorClock clock;
    private readonly object gate = new();
    private readonly object lifecycleGate = new();
    private PersistedState state;
    private Task restartTask;
    private Task launchTask;

    private sealed class MaintenanceValidation
    {
        internal bool Safe { get; init; }
        internal string ErrorCode { get; init; }
        internal string Error { get; init; }
    }

    private sealed class ProcessStatusSnapshot
    {
        internal bool OwnedProcessRunning { get; init; }
        internal int MatchingProcessCount { get; init; }
        internal List<UnmanagedRimWorldProcess> UnmanagedProcesses { get; init; } = new();
    }

    internal CoordinatorState(string root) : this(root, CoordinatorOptions.ForProduction())
    {
    }

    internal CoordinatorState(string root, CoordinatorOptions options)
    {
        this.root = Path.GetFullPath(root);
        this.options = options ?? CoordinatorOptions.ForProduction();
        processAdapter = this.options.ProcessAdapter ?? new SystemProcessAdapter();
        clock = this.options.Clock ?? SystemCoordinatorClock.Instance;
        runtimeRoot = Path.Combine(this.root, "Runtime");
        statePath = Path.Combine(runtimeRoot, "state.json");
        readinessPath = Path.Combine(runtimeRoot, "readiness.json");
        rimWorldExe = Path.GetFullPath(this.options.RimWorldExecutablePath ??
            Path.Combine(this.root, "..", "..", "RimWorldWin64.exe"));
        modsConfigPath = this.options.ModsConfigPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
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
            "status" => Status(request, emit),
            "doctor" => Doctor(request, emit),
            "wait-ready" => WaitReady(request, emit),
            "restart" => Restart(request, emit),
            "stop" => Stop(request, emit),
            "ensure-ready" => EnsureReady(request, emit),
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
        emit("  DevBridge.cmd stop <lease-id>");
        emit("  DevBridge.cmd ensure-ready <lease-id>");
        emit("  DevBridge.cmd restart");
        emit("  DevBridge.cmd wait-ready");
        emit("  DevBridge.cmd doctor");
        emit("Append --json to a command for one machine-readable result.");
        return 0;
    }

    private static int Unknown(string command, Action<string> emit)
    {
        emit("Unknown DevBridge command: " + command);
        emit("Use: status, test begin, test end <lease-id>, stop <lease-id>, ensure-ready <lease-id>, restart, wait-ready, doctor");
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
        emit("Next action: Keep waiting. Do not launch, kill, or restart RimWorld yourself.");
    }

    private int Status(BridgeRequest request, Action<string> emit)
    {
        PersistedState snapshot;
        ProcessStatusSnapshot processSnapshot = new();
        bool processInspectionAmbiguous = false;
        lock (lifecycleGate)
        {
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

                snapshot = CloneStateLocked();
            }
        }

        emit("DevBridge2 status");
        emit("Agent/session: " + request.Agent);
        emit("State: " + snapshot.Phase);
        string heldLease = snapshot.Leases.FirstOrDefault(value =>
            string.Equals(value.Agent, request.Agent, StringComparison.Ordinal) &&
            value.ClientProcessId == request.ClientProcessId)?.Id;
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
        emit("Active tests: " + snapshot.Leases.Count);
        emit("Session dirty: " + snapshot.SessionDirty);
        foreach (TestLease lease in snapshot.Leases.OrderBy(value => value.StartedUtc))
            emit("  " + lease.Id + " - " + lease.Agent + " - age " + FormatAge(lease.StartedUtc) +
                " - stale in " + FormatStaleIn(lease.StartedUtc));

        if (snapshot.RestartPending)
        {
            emit("Restart is in progress.");
            emit("Restart: pending for generation " + snapshot.TargetGeneration +
                (snapshot.RestartRequestedUtc.HasValue ? " (requested " + FormatAge(snapshot.RestartRequestedUtc.Value) + " ago)" : string.Empty));
            emit("New test requests are waiting for the new generation.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            emit("Error: " + snapshot.Error);
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorCode))
            emit("Error code: " + snapshot.ErrorCode);

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
            emit("Test leases are shared; multiple agents may test this generation concurrently.");
            EmitNextCommand(emit, "DevBridge.cmd test begin");
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
        string modAssembly = Path.Combine(root, "1.6", "Assemblies", "DevBridge2.dll");
        string about = Path.Combine(root, "About", "About.xml");
        bool exeExists = File.Exists(rimWorldExe);
        bool modExists = File.Exists(modAssembly);
        bool aboutExists = File.Exists(about);
        bool modEnabled = IsDevBridgeModEnabled();
        PersistedState snapshot;
        bool processRunning = false;
        bool processInspectionAmbiguous = false;
        List<UnmanagedRimWorldProcess> unmanagedProcesses = new();
        lock (gate)
        {
            SynchronizeLocked();
            RevalidateMaintenanceReadyLocked();
            try
            {
                processRunning = IsOwnedProcess(state.ProcessId, state.ProcessStartUtcTicks);
            }
            catch (ProcessInspectionException)
            {
                processInspectionAmbiguous = true;
                MarkProcessInspectionAmbiguousLocked();
            }

            try
            {
                unmanagedProcesses = FindUnmanagedRimWorldProcesses(state.ProcessId, state.ProcessStartUtcTicks);
            }
            catch (ProcessInspectionException)
            {
                processInspectionAmbiguous = true;
                MarkProcessInspectionAmbiguousLocked();
            }

            snapshot = CloneStateLocked();
        }

        emit("DevBridge2 doctor");
        emit("Agent/session: " + request.Agent);
        emit(Check(exeExists, "RimWorld executable: " + rimWorldExe));
        emit(Check(aboutExists, "Mod metadata: " + about));
        emit(Check(modExists, "Built mod assembly: " + modAssembly));
        emit(Check(Directory.Exists(runtimeRoot), "Runtime directory: " + runtimeRoot));
        emit(modEnabled
            ? "PASS DevBridge2 is active in " + modsConfigPath
            : "WARN DevBridge2 is not active in the current ModsConfig.xml; the coordinator will enable it before launch.");
        emit("Coordinator state: " + snapshot.Phase + ", generation " + snapshot.Generation);
        emit("Coordinator-owned RimWorld process: " + (processRunning ? "yes (PID " + snapshot.ProcessId + ")" : "no"));
        if (processInspectionAmbiguous)
            emit("WARN RimWorld process inspection is ambiguous; no process-control or launch action was taken.");
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

        int exitCode = exeExists && aboutExists && modExists ? 0 : 1;
        if (exitCode != 0)
            emit("Next action: Fix the failing check, then run:");
        else if (snapshot.Phase == BridgePhase.READY && !snapshot.RestartPending)
            EmitNextCommand(emit, "DevBridge.cmd test begin");
        else if (snapshot.RestartPending || snapshot.Phase == BridgePhase.DRAINING ||
                 snapshot.Phase == BridgePhase.RESTARTING || snapshot.Phase == BridgePhase.LOADING)
            EmitKeepWaiting(emit);
        else
            EmitNextCommand(emit, "DevBridge.cmd wait-ready");
        if (exitCode != 0)
            emit("DevBridge.cmd restart");
        return exitCode;
    }

    private static string Check(bool passed, string text) => (passed ? "PASS " : "FAIL ") + text;

    private int BeginLease(BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        emit("Agent/session: " + request.Agent);
        lock (lifecycleGate)
        {
            lock (gate)
            {
                SynchronizeLocked();
                if (state.Phase == BridgePhase.STOPPED && state.Generation == 0 && !state.RestartPending)
                {
                    emit("No ready RimWorld generation is running.");
                    emit("DevBridge is launching RimWorld normally, then requesting built-in Dev Quicktest.");
                    StartInitialLaunchLocked();
                }
                else if (state.Phase == BridgePhase.ERROR)
                {
                    emit("RimWorld is in ERROR state: " + state.Error);
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }
            }
        }

        if (!WaitForReady(emit, requireNoRestart: true, connected: connected, waitForMaintenance: true))
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
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
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
                        StartedUtc = clock.UtcNow
                    };
                    state.Leases.Add(lease);
                    SaveStateLocked();
                    Monitor.PulseAll(gate);
                    break;
                }
            }

            emit("Restart is in progress. Waiting for generation " + CurrentTargetGeneration() + "...");
            EmitKeepWaiting(emit);
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
        emit("Next action: Test your mod, then run:");
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
            {
                emit("No active tests remain. DevBridge will continue the pending restart automatically.");
                EmitKeepWaiting(emit);
            }
            else
                emit("Next action: Continue your workflow; run DevBridge.cmd restart only after a change requiring a fresh process.");
            return 0;
        }
    }

    private int Stop(BridgeRequest request, Action<string> emit)
    {
        if (request.Arguments.Count < 1 || string.IsNullOrWhiteSpace(request.Arguments[0]))
        {
            emit("Usage: DevBridge.cmd stop <lease-id>");
            return 2;
        }

        string leaseId = request.Arguments[0].Trim().ToUpperInvariant();
        lock (lifecycleGate)
        {
            int processId;
            long processStartIdentity;
            lock (gate)
            {
                SynchronizeLocked();
                if (!TryGetLeaseHolderLocked(leaseId, request, out TestLease lease))
                {
                    emit("Stop denied: lease " + leaseId + " is not held by this agent/session.");
                    EmitNextCommand(emit, "DevBridge.cmd test begin");
                    return 4;
                }

                if (state.MaintenanceReady)
                {
                    MaintenanceValidation validation = RevalidateMaintenanceReadyLocked();
                    if (!validation.Safe)
                    {
                        emit("Stop failed: " + validation.Error);
                        emit("Error code: " + validation.ErrorCode);
                        emit("maintenanceReady=false");
                        EmitNextCommand(emit, "DevBridge.cmd doctor");
                        return 4;
                    }

                    emit("RimWorld is already stopped for maintenance.");
                    emit("gameState=STOPPED maintenanceReady=true leaseState=HELD");
                    EmitNextCommand(emit, "DevBridge.cmd ensure-ready " + lease.Id);
                    return 0;
                }

                if (state.RestartPending || state.Phase == BridgePhase.DRAINING ||
                    state.Phase == BridgePhase.RESTARTING || state.Phase == BridgePhase.LOADING ||
                    (state.Phase != BridgePhase.READY && state.ErrorCode != "READINESS_TIMEOUT"))
                {
                    emit("Stop denied: RimWorld is not in a stoppable ready or timed-out state.");
                    emit("No launch was attempted.");
                    EmitKeepWaiting(emit);
                    return 4;
                }

                processId = state.ProcessId;
                processStartIdentity = state.ProcessStartUtcTicks;
            }

            (bool success, string errorCode, string error) result = StopForMaintenance(processId, processStartIdentity);
            lock (gate)
            {
                if (!result.success)
                {
                    state.MaintenanceReady = false;
                    state.ErrorCode = result.errorCode;
                    state.Error = "RimWorld was not stopped safely: " + result.error;
                    SaveStateLocked();
                    Monitor.PulseAll(gate);
                    emit("Stop failed: " + result.error);
                    emit("maintenanceReady=false");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }

                state.Phase = BridgePhase.STOPPED;
                state.ProcessId = 0;
                state.ProcessStartUtcTicks = 0;
                state.MaintenanceReady = true;
                state.SessionDirty = true;
                state.Error = null;
                state.ErrorCode = null;
                state.RestartPending = false;
                state.TargetGeneration = 0;
                DeleteReadinessLocked();
                SaveStateLocked();
                Monitor.PulseAll(gate);
                emit("RimWorld stopped and confirmed absent from the configured installation.");
                emit("gameState=STOPPED maintenanceReady=true leaseState=HELD");
                EmitNextCommand(emit, "DevBridge.cmd ensure-ready " + leaseId);
                return 0;
            }
        }
    }

    private int EnsureReady(BridgeRequest request, Action<string> emit)
    {
        if (request.Arguments.Count < 1 || string.IsNullOrWhiteSpace(request.Arguments[0]))
        {
            emit("Usage: DevBridge.cmd ensure-ready <lease-id>");
            return 2;
        }

        string leaseId = request.Arguments[0].Trim().ToUpperInvariant();
        lock (lifecycleGate)
        {
            int targetGeneration = 0;
            bool shouldLaunch = false;
            lock (gate)
            {
                SynchronizeLocked();
                if (!TryGetLeaseHolderLocked(leaseId, request, out TestLease lease))
                {
                    emit("Ensure-ready denied: lease " + leaseId + " is not held by this agent/session.");
                    return 4;
                }

                if (state.MaintenanceReady)
                {
                    MaintenanceValidation validation = RevalidateMaintenanceReadyLocked();
                    if (!validation.Safe)
                    {
                        emit("Ensure-ready denied: " + validation.Error);
                        emit("Error code: " + validation.ErrorCode);
                        emit("No launch was attempted.");
                        EmitNextCommand(emit, "DevBridge.cmd doctor");
                        return 4;
                    }

                    targetGeneration = Math.Max(1, state.Generation + 1);
                    state.TargetGeneration = targetGeneration;
                    state.MaintenanceReady = false;
                    state.Error = null;
                    state.ErrorCode = null;
                    state.Phase = BridgePhase.RESTARTING;
                    DeleteReadinessLocked();
                    SaveStateLocked();
                    shouldLaunch = true;
                }
                else if (state.Phase == BridgePhase.READY && !state.RestartPending)
                {
                    emit("RimWorld is already ready.");
                    EmitNextCommand(emit, "DevBridge.cmd test end " + lease.Id);
                    return 0;
                }
                else if (state.ErrorCode == "READINESS_TIMEOUT")
                {
                    if (TryAcceptLateReadinessLocked())
                    {
                        emit("Late quicktest readiness accepted from the original process.");
                        emit("RimWorld is ready.");
                        EmitNextCommand(emit, "DevBridge.cmd test end " + lease.Id);
                        return 0;
                    }

                    if (state.ErrorCode == ProcessInspection.ErrorCode)
                    {
                        emit(ProcessInspection.Message);
                        emit("No launch was attempted.");
                        EmitNextCommand(emit, "DevBridge.cmd doctor");
                        return 4;
                    }

                    emit("READINESS_TIMEOUT: the original RimWorld process is still not ready.");
                    emit("No launch was attempted.");
                    EmitNextCommand(emit, "DevBridge.cmd stop " + lease.Id);
                    return 4;
                }
                else
                {
                    emit("Ensure-ready denied: no confirmed maintenance window or reusable timed-out process exists.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }
            }

            if (shouldLaunch)
            {
                emit("Maintenance window released by lease holder; launching one new RimWorld process.");
                LaunchGenerationWorker(targetGeneration, isRestart: true);
            }

            lock (gate)
            {
                if (state.Generation >= targetGeneration && state.Phase == BridgePhase.READY &&
                    !state.RestartPending)
                {
                    emit("RimWorld is ready.");
                    emit("Generation: " + state.Generation);
                    EmitNextCommand(emit, "DevBridge.cmd test end " + leaseId);
                    return 0;
                }

                emit(string.IsNullOrWhiteSpace(state.Error) ?
                    "Ensure-ready did not reach quicktest readiness." : state.Error);
                if (state.ErrorCode == "READINESS_TIMEOUT")
                    emit("READINESS_TIMEOUT");
                EmitNextCommand(emit, "DevBridge.cmd doctor");
                return 4;
            }
        }
    }

    private int Restart(BridgeRequest request, Action<string> emit)
    {
        int targetGeneration;
        int currentGeneration;
        bool alreadyPending;
        lock (lifecycleGate)
        {
            lock (gate)
            {
                SynchronizeLocked();
                if (state.ErrorCode == ProcessInspection.ErrorCode)
                {
                    emit(ProcessInspection.Message);
                    emit("No launch was attempted.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }
                currentGeneration = state.Generation;
                alreadyPending = state.RestartPending;
                if (state.MaintenanceReady)
                {
                    if (request.Arguments.Count < 1 ||
                        !TryGetLeaseHolderLocked(request.Arguments[0], request, out TestLease maintenanceLease))
                    {
                        emit("Restart denied: a lease-holder token is required while maintenanceReady=true.");
                        emit("No launch was attempted.");
                        return 4;
                    }

                    MaintenanceValidation validation = RevalidateMaintenanceReadyLocked();
                    if (!validation.Safe)
                    {
                        emit("Restart denied: " + validation.Error);
                        emit("Error code: " + validation.ErrorCode);
                        emit("No launch was attempted.");
                        EmitNextCommand(emit, "DevBridge.cmd doctor");
                        return 4;
                    }

                    state.Leases.Remove(maintenanceLease);
                    state.MaintenanceReady = false;
                    state.SessionDirty = true;
                    alreadyPending = false;
                }
                else if (state.Phase == BridgePhase.STOPPED && state.SessionDirty &&
                         (state.ErrorCode == ProcessInspection.ErrorCode ||
                          state.ErrorCode == "MAINTENANCE_PROCESS_PRESENT"))
                {
                    emit("Restart denied: the maintenance window is not safe to leave without a fresh process check.");
                    emit("No launch was attempted.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }

                if (!alreadyPending)
                {
                    targetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
                    state.TargetGeneration = targetGeneration;
                    state.RestartPending = true;
                    state.RestartRequestedUtc = clock.UtcNow;
                    state.Error = null;
                    state.ErrorCode = null;
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
        }

        if (alreadyPending)
            emit("Restart already accepted for generation " + currentGeneration + " -> " + targetGeneration + ".");
        else
            emit("Restart accepted for generation " + currentGeneration + " -> " + targetGeneration + ".");
        emit("Agent/session: " + request.Agent);
        emit("DevBridge now owns this restart.");
        emit("If this command is interrupted or times out, do not request another restart.");
        EmitNextCommand(emit, "DevBridge.cmd wait-ready");

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
                    EmitNextCommand(emit, "DevBridge.cmd test begin");
                    return 0;
                }

                if (state.Phase == BridgePhase.ERROR && !state.RestartPending)
                {
                    emit("Restart failed: " + state.Error);
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return 4;
                }
            }

            WaitForStateChange(ProgressInterval);
            EmitRestartWait(emit);
        }
    }

    private int WaitReady(BridgeRequest request, Action<string> emit)
    {
        emit("Agent/session: " + request.Agent);
        lock (lifecycleGate)
        {
            lock (gate)
            {
                SynchronizeLocked();
                if (state.Phase == BridgePhase.STOPPED && state.Generation == 0 && !state.RestartPending)
                {
                    emit("No ready RimWorld generation is running.");
                    emit("DevBridge is launching RimWorld normally, then requesting built-in Dev Quicktest.");
                    StartInitialLaunchLocked();
                }
            }
        }

        if (!WaitForReady(emit, requireNoRestart: true))
            return 4;

        lock (gate)
        {
            emit("RimWorld is ready.");
            emit("Generation: " + state.Generation);
            emit("Quicktest map is ready.");
            EmitNextCommand(emit, "DevBridge.cmd test begin");
        }
        return 0;
    }

    private bool WaitForReady(Action<string> emit, bool requireNoRestart, Func<bool> connected = null,
        bool waitForMaintenance = false)
    {
        DateTime nextProgress = clock.UtcNow;
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
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return false;
                }

                if (state.Phase == BridgePhase.STOPPED && state.Generation > 0 && !state.RestartPending)
                {
                    if (state.MaintenanceReady && waitForMaintenance)
                    {
                        emit("RimWorld is stopped for a lease-held maintenance window.");
                        emit("Waiting for the lease holder to run ensure-ready or restart.");
                        EmitKeepWaiting(emit);
                        first = false;
                        nextProgress = clock.UtcNow.Add(ProgressInterval);
                        Monitor.Wait(gate, 1000);
                        continue;
                    }

                    emit("RimWorld is stopped.");
                    EmitNextCommand(emit, "DevBridge.cmd restart");
                    return false;
                }

                if (first || clock.UtcNow >= nextProgress)
                {
                    int target = CurrentTargetGenerationLocked();
                    emit("Waiting for RimWorld generation " + target + "...");
                    emit("State: " + state.Phase + ". Waiting for the quicktest map readiness signal.");
                    EmitKeepWaiting(emit);
                    first = false;
                    nextProgress = clock.UtcNow.Add(ProgressInterval);
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
            emit("Restart is in progress.");
            emit("No active tests remain.");
            emit("State: " + snapshot.Phase + ". Waiting for generation " + snapshot.TargetGeneration +
                " quicktest map readiness.");
            EmitKeepWaiting(emit);
            return;
        }

        emit("Restart is in progress.");
        emit("Waiting for " + snapshot.Leases.Count + " active test" + (snapshot.Leases.Count == 1 ? "" : "s") + ":");
        foreach (TestLease lease in snapshot.Leases.OrderBy(value => value.StartedUtc))
            emit("  " + lease.Id + " - " + lease.Agent + " - active " + FormatAge(lease.StartedUtc) +
                " - stale in " + FormatStaleIn(lease.StartedUtc));
        EmitKeepWaiting(emit);
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
        state.ErrorCode = null;
        state.MaintenanceReady = false;
        state.LaunchId = null;
        state.LaunchGeneration = target;
        state.ProcessId = 0;
        state.ProcessStartUtcTicks = 0;
        DeleteReadinessLocked();
        SaveStateLocked();
        launchTask = Task.Run(() =>
        {
            lock (lifecycleGate)
                LaunchGenerationWorker(target, isRestart: false);
        });
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
            lock (lifecycleGate)
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
                    state.ErrorCode = null;
                    state.MaintenanceReady = false;
                    oldProcessId = state.ProcessId;
                    oldStartTicks = state.ProcessStartUtcTicks;
                    DeleteReadinessLocked();
                    SaveStateLocked();
                    break;
                }
            }

            (bool stopped, string stopErrorCode, string stopError) = StopOwnedProcess(oldProcessId, oldStartTicks);
            if (!stopped)
            {
                FailLaunch(stopError, stopErrorCode);
                return;
            }
            LaunchGenerationWorker(targetGeneration, isRestart: true);
            }
        }
        catch (Exception exception)
        {
            FailLaunch(exception is ProcessInspectionException ? ProcessInspection.Message :
                "restart coordinator failure: " + exception.Message,
                exception is ProcessInspectionException ? ProcessInspection.ErrorCode : "LAUNCH_FAILED");
        }
    }

    private void LaunchGenerationWorker(int targetGeneration, bool isRestart)
    {
        string launchId = Guid.NewGuid().ToString("N");
        IManagedProcess process = null;
        try
        {
            lock (gate)
            {
                state.Phase = BridgePhase.LOADING;
                state.TargetGeneration = targetGeneration;
                state.LaunchId = launchId;
                state.LaunchGeneration = targetGeneration;
                state.LaunchStartedUtc = clock.UtcNow;
                state.Error = null;
                state.ErrorCode = null;
                state.MaintenanceReady = false;
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

            process = processAdapter.Launch(new ProcessLaunchRequest
            {
                FileName = rimWorldExe,
                WorkingDirectory = Path.GetDirectoryName(rimWorldExe) ?? root,
                Arguments = Array.Empty<string>(),
                Environment = new Dictionary<string, string>
                {
                    ["DEVBRIDGE_ROOT"] = root,
                    ["DEVBRIDGE_LAUNCH_ID"] = launchId,
                    ["DEVBRIDGE_GENERATION"] = targetGeneration.ToString(),
                    ["DEVBRIDGE_QUICKTEST_REQUESTED"] = "1"
                }
            });
            if (process == null)
                throw new InvalidOperationException("launch adapter returned no RimWorld process");

            int processId = process.Id;
            long processStartTicks = process.StartIdentity;
            if (processStartTicks <= 0)
                throw new InvalidOperationException("launch adapter did not provide a process-start identity");
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
            FailLaunch(DescribeLaunchFailure(exception, process), exception is TimeoutException ?
                "READINESS_TIMEOUT" : exception is ProcessInspectionException ?
                    ProcessInspection.ErrorCode : "LAUNCH_FAILED");
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

            using IManagedProcess process = processAdapter.Open(processId);
            if (process == null)
                throw new InvalidOperationException("the persisted RimWorld process no longer exists");
            MonitorLaunchUntilReady(process, processId, startTicks, launchId, targetGeneration);
        }
        catch (Exception exception)
        {
            FailLaunch(exception is ProcessInspectionException ? ProcessInspection.Message :
                "RimWorld did not report readiness after coordinator recovery: " + exception.Message,
                exception is ProcessInspectionException ? ProcessInspection.ErrorCode : "LAUNCH_FAILED");
        }
    }

    private void MonitorLaunchUntilReady(IManagedProcess process, int processId, long processStartTicks,
        string launchId, int targetGeneration)
    {
        DateTime deadline;
        lock (gate)
            deadline = state.LaunchStartedUtc.ToUniversalTime().Add(options.ReadinessTimeout);

        while (clock.UtcNow < deadline)
        {
            if (process == null || process.HasExited)
            {
                FailLaunch("RimWorld exited before the quicktest map became ready", "PROCESS_EXITED");
                return;
            }

            if (!IsOwnedProcess(process, processStartTicks))
            {
                FailLaunch("the RimWorld process identity changed before readiness", "PROCESS_IDENTITY_CHANGED");
                return;
            }

            DateTime launchStarted = deadline - options.ReadinessTimeout;
            if (IsReadinessMatch(launchId, processId, targetGeneration, launchStarted))
            {
                lock (gate)
                {
                    MarkReadyLocked(launchId, targetGeneration, processId, processStartTicks);
                }
                return;
            }

            clock.Sleep(TimeSpan.FromSeconds(1));
        }

        FailLaunch("no matching readiness signal was written within " +
            options.ReadinessTimeout.TotalSeconds.ToString("0") + " seconds", "READINESS_TIMEOUT");
    }

    private void FailLaunch(string detail, string errorCode = "LAUNCH_FAILED")
    {
        lock (gate)
        {
            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
            state.ErrorCode = errorCode;
            state.Error = errorCode == ProcessInspection.ErrorCode ? ProcessInspection.Message :
                errorCode == "READINESS_TIMEOUT" ?
                "READINESS_TIMEOUT: " + detail + ". The original process was retained; no replacement launch was attempted." :
                "RimWorld did not report a playable quicktest map: " + detail +
                ". Inspect Runtime/readiness.json and the RimWorld logs, then run DevBridge.cmd restart.";
            SaveStateLocked();
            Monitor.PulseAll(gate);
        }
    }

    private static string DescribeLaunchFailure(Exception exception, IManagedProcess process)
    {
        if (exception is ProcessInspectionException)
            return ProcessInspection.Message;
        if (exception is FileNotFoundException)
            return exception.Message;
        if (process != null)
        {
            try
            {
                if (process.HasExited)
                    return "RimWorld exited before readiness";
            }
            catch
            {
                // Use the original exception below.
            }
        }

        return exception.GetType().Name + ": " + exception.Message;
    }

    private (bool success, string errorCode, string error) StopOwnedProcess(int processId, long startTicks)
    {
        if (processId <= 0)
            return (true, null, null);

        IManagedProcess process = null;
        try
        {
            process = processAdapter.Open(processId);
            if (process == null)
                return (true, null, null);
            if (!IsOwnedProcess(process, startTicks))
                return (false, "PROCESS_IDENTITY_CHANGED", "the persisted RimWorld process identity no longer matches");

            if (!process.HasExited)
            {
                try
                {
                    process.RequestTermination();
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
                catch (ProcessInspectionException)
                {
                    return (false, ProcessInspection.ErrorCode, ProcessInspection.Message);
                }
                catch
                {
                    // Fall through to the bounded kill below.
                }

                if (!process.HasExited)
                {
                    if (!process.ForceTerminate() || !process.HasExited)
                        return (false, "STOP_FAILED", "process exit was not confirmed");
                }
            }

            return (true, null, null);
        }
        catch (ProcessInspectionException)
        {
            return (false, ProcessInspection.ErrorCode, ProcessInspection.Message);
        }
        catch
        {
            return (false, "STOP_FAILED", "the verified process could not be stopped safely");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private (bool success, string errorCode, string error) StopForMaintenance(int processId, long startTicks)
    {
        if (processId <= 0 || startTicks <= 0)
            return (false, "STOP_FAILED", "the persisted process PID/start identity is incomplete");

        try
        {
            using IManagedProcess process = processAdapter.Open(processId);
            if (process == null)
                return (false, "STOP_FAILED", "the persisted RimWorld process no longer exists");
            if (!IsOwnedProcess(process, startTicks))
                return (false, "PROCESS_IDENTITY_CHANGED", "the persisted process identity no longer matches");
            if (process.HasExited)
                return (false, "STOP_FAILED", "the verified process was already exited before termination was requested");
            if (!process.RequestTermination())
                return (false, "STOP_FAILED", "the verified process rejected the termination request");
            if (!process.WaitForExit(options.ProcessExitTimeout) || !process.HasExited)
                return (false, "STOP_FAILED", "process exit was not confirmed within the configured timeout");

            List<UnmanagedRimWorldProcess> remaining = FindUnmanagedRimWorldProcesses(0, 0);
            if (remaining.Count != 0)
                return (false, "MAINTENANCE_PROCESS_PRESENT", "a matching RimWorld installation process remains");

            return (true, null, null);
        }
        catch (ProcessInspectionException)
        {
            return (false, ProcessInspection.ErrorCode, ProcessInspection.Message);
        }
        catch
        {
            return (false, "STOP_FAILED", "RimWorld could not be stopped safely");
        }
    }

    private bool TryGetLeaseHolderLocked(string leaseId, BridgeRequest request, out TestLease lease)
    {
        lease = state.Leases.FirstOrDefault(value =>
            string.Equals(value.Id, leaseId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.Agent, request.Agent, StringComparison.Ordinal) &&
            value.ClientProcessId == request.ClientProcessId);
        return lease != null;
    }

    private void MarkReadyLocked(string launchId, int targetGeneration, int processId, long processStartIdentity)
    {
        if (!string.Equals(state.LaunchId, launchId, StringComparison.Ordinal) ||
            state.LaunchGeneration != targetGeneration || state.ProcessId != processId ||
            state.ProcessStartUtcTicks != processStartIdentity || !IsOwnedProcess(processId, processStartIdentity))
            return;

        state.Generation = targetGeneration;
        state.Phase = BridgePhase.READY;
        state.Error = null;
        state.ErrorCode = null;
        state.RestartPending = false;
        state.RestartRequestedUtc = null;
        state.TargetGeneration = 0;
        state.MaintenanceReady = false;
        foreach (TestLease lease in state.Leases)
            lease.Generation = targetGeneration;
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private MaintenanceValidation RevalidateMaintenanceReadyLocked()
    {
        if (!state.MaintenanceReady)
            return new MaintenanceValidation { Safe = false, ErrorCode = state.ErrorCode, Error = state.Error };

        try
        {
            List<UnmanagedRimWorldProcess> remaining = FindUnmanagedRimWorldProcesses(0, 0);
            if (remaining.Count == 0)
                return new MaintenanceValidation { Safe = true };

            MarkMaintenanceProcessPresentLocked();
            return new MaintenanceValidation
            {
                Safe = false,
                ErrorCode = state.ErrorCode,
                Error = state.Error
            };
        }
        catch (ProcessInspectionException)
        {
            MarkProcessInspectionAmbiguousLocked();
            return new MaintenanceValidation
            {
                Safe = false,
                ErrorCode = ProcessInspection.ErrorCode,
                Error = ProcessInspection.Message
            };
        }
    }

    private void MarkMaintenanceProcessPresentLocked()
    {
        state.MaintenanceReady = false;
        state.ErrorCode = "MAINTENANCE_PROCESS_PRESENT";
        state.Error = "A matching RimWorld process is present; assembly replacement is not safe.";
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void MarkProcessInspectionAmbiguousLocked()
    {
        state.MaintenanceReady = false;
        state.ErrorCode = ProcessInspection.ErrorCode;
        state.Error = ProcessInspection.Message;
        if (state.Phase == BridgePhase.READY || state.Phase == BridgePhase.LOADING ||
            state.Phase == BridgePhase.RESTARTING || state.Phase == BridgePhase.DRAINING)
        {
            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
        }
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private bool TryAcceptLateReadinessLocked()
    {
        try
        {
            if (state.ErrorCode != "READINESS_TIMEOUT" || state.ProcessId <= 0 ||
                state.ProcessStartUtcTicks <= 0 || state.LaunchGeneration <= 0 ||
                !IsOwnedProcess(state.ProcessId, state.ProcessStartUtcTicks))
                return false;

            DateTime launchStarted = state.LaunchStartedUtc.ToUniversalTime();
            if (!IsReadinessMatch(state.LaunchId, state.ProcessId, state.LaunchGeneration, launchStarted))
                return false;

            MarkReadyLocked(state.LaunchId, state.LaunchGeneration, state.ProcessId, state.ProcessStartUtcTicks);
            return state.Phase == BridgePhase.READY;
        }
        catch (ProcessInspectionException)
        {
            MarkProcessInspectionAmbiguousLocked();
            return false;
        }
    }

    private void SynchronizeLocked()
    {
        PruneStaleLeasesLocked();

        bool owned = false;
        if (state.Phase == BridgePhase.READY && state.ProcessId > 0)
        {
            try
            {
                owned = IsOwnedProcess(state.ProcessId, state.ProcessStartUtcTicks);
            }
            catch (ProcessInspectionException)
            {
                MarkProcessInspectionAmbiguousLocked();
                return;
            }
        }

        if (state.Phase == BridgePhase.READY && (state.ProcessId <= 0 || !owned))
        {
            state.Phase = BridgePhase.STOPPED;
            state.Error = "The coordinator-owned RimWorld process is no longer running.";
            state.ErrorCode = "PROCESS_EXITED";
            state.MaintenanceReady = false;
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
        DateTime cutoff = clock.UtcNow - LeaseStaleAfter;
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
            string executablePath = process.ExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath))
                throw ProcessInspection.Failure();
            if (!string.Equals(Path.GetFullPath(executablePath), rimWorldExe, StringComparison.OrdinalIgnoreCase))
                return false;
            if (startTicks <= 0)
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

    internal JsonCommandResponse CreateJsonResponse(BridgeRequest request, int exitCode,
        IReadOnlyList<string> messages)
    {
        PersistedState snapshot;
        bool statusCommand = string.Equals(request.Command, "status", StringComparison.OrdinalIgnoreCase);
        lock (gate)
        {
            if (!statusCommand)
            {
                SynchronizeLocked();
                RevalidateMaintenanceReadyLocked();
            }
            snapshot = CloneStateLocked();
        }

        string commandName = request.Command ?? string.Empty;
        string command = commandName;
        if (request.Arguments.Count > 0)
            command += " " + string.Join(" ", request.Arguments);

        bool maintenanceSafetyLost = exitCode == 0 && !snapshot.MaintenanceReady &&
            (string.Equals(commandName, "stop", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(commandName, "ensure-ready", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(commandName, "restart", StringComparison.OrdinalIgnoreCase)) &&
            (snapshot.ErrorCode == ProcessInspection.ErrorCode ||
             snapshot.ErrorCode == "MAINTENANCE_PROCESS_PRESENT");
        int effectiveExitCode = maintenanceSafetyLost ? 4 : exitCode;

        JsonCommandResponse response = new()
        {
            Success = effectiveExitCode == 0,
            Command = command,
            ExitCode = effectiveExitCode,
            State = snapshot.Phase.ToString(),
            GameState = snapshot.Phase.ToString(),
            Generation = snapshot.Generation,
            RimWorldPid = snapshot.ProcessId,
            RimWorldProcessStartIdentity = snapshot.ProcessStartUtcTicks,
            LaunchGeneration = snapshot.LaunchGeneration,
            MaintenanceReady = snapshot.MaintenanceReady,
            LeaseState = snapshot.Leases.Any(value =>
                string.Equals(value.Agent, request.Agent, StringComparison.Ordinal) &&
                value.ClientProcessId == request.ClientProcessId) ? "HELD" : "QUEUED",
            SessionDirty = snapshot.SessionDirty,
            ActiveTests = snapshot.Leases.Count,
            RestartPending = snapshot.RestartPending,
            TargetGeneration = snapshot.TargetGeneration,
            Agent = request.Agent,
            Leases = snapshot.Leases
                .OrderBy(value => value.StartedUtc)
                .Select(ToJsonLease)
                .ToList(),
            Checks = messages
                .Where(value => value.StartsWith("PASS ", StringComparison.Ordinal) ||
                                value.StartsWith("FAIL ", StringComparison.Ordinal) ||
                                value.StartsWith("WARN ", StringComparison.Ordinal))
                .ToList()
        };

        if (string.Equals(request.Command, "restart", StringComparison.OrdinalIgnoreCase))
        {
            response.Accepted = effectiveExitCode == 0 && messages.Any(value =>
                value.StartsWith("Restart accepted", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Restart already accepted", StringComparison.OrdinalIgnoreCase));
        }

        if ((string.Equals(request.Command, "stop", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(request.Command, "ensure-ready", StringComparison.OrdinalIgnoreCase)) && effectiveExitCode == 0)
            response.Accepted = true;

        string subcommand = request.Arguments.Count > 0 ? request.Arguments[0] : string.Empty;
        if (string.Equals(request.Command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "begin", StringComparison.OrdinalIgnoreCase) && effectiveExitCode == 0)
        {
            TestLease lease = snapshot.Leases
                .Where(value => string.Equals(value.Agent, request.Agent, StringComparison.Ordinal) &&
                                value.ClientProcessId == request.ClientProcessId)
                .OrderByDescending(value => value.StartedUtc)
                .FirstOrDefault();
            response.LeaseId = lease?.Id;
        }
        else if (string.Equals(request.Command, "test", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(subcommand, "end", StringComparison.OrdinalIgnoreCase) &&
                 request.Arguments.Count > 1)
        {
            response.LeaseId = request.Arguments[1];
        }
        else if ((string.Equals(request.Command, "stop", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(request.Command, "ensure-ready", StringComparison.OrdinalIgnoreCase)) &&
                 request.Arguments.Count > 0)
        {
            response.LeaseId = request.Arguments[0];
        }

        response.Error = !string.IsNullOrWhiteSpace(snapshot.Error)
            ? snapshot.Error
            : effectiveExitCode == 0
                ? null
                : messages.LastOrDefault(value => !value.StartsWith("Next action:", StringComparison.Ordinal));
        response.ErrorCode = snapshot.ErrorCode;
        response.NextAction = JsonNextAction(request, snapshot, effectiveExitCode, response.LeaseId);
        return response;
    }

    private static string JsonNextAction(BridgeRequest request, PersistedState snapshot,
        int exitCode, string leaseId)
    {
        string command = request.Command ?? string.Empty;
        string subcommand = request.Arguments.Count > 0 ? request.Arguments[0] : string.Empty;

        if (snapshot.ErrorCode == ProcessInspection.ErrorCode ||
            snapshot.ErrorCode == "MAINTENANCE_PROCESS_PRESENT")
            return "Run: DevBridge.cmd doctor";

        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "begin", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
            return "Test your mod, then run: DevBridge.cmd test end " + leaseId;

        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "end", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
        {
            return snapshot.RestartPending
                ? "Keep waiting. Do not launch, kill, or restart RimWorld yourself."
                : "Continue your workflow; run DevBridge.cmd restart only after a change requiring a fresh process.";
        }

        if (string.Equals(command, "stop", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
            return "Replace the assembly, verify its hash, then run: DevBridge.cmd ensure-ready " + leaseId;

        if (string.Equals(command, "ensure-ready", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
            return "Run: DevBridge.cmd test end " + leaseId;

        if (exitCode != 0)
        {
            if (string.Equals(command, "doctor", StringComparison.OrdinalIgnoreCase))
                return "Fix the failing doctor check, then run: DevBridge.cmd restart";
            return "Run: DevBridge.cmd doctor";
        }

        if (snapshot.Phase == BridgePhase.ERROR)
            return "Run: DevBridge.cmd doctor";
        if (snapshot.MaintenanceReady)
            return "Replace the assembly, verify its hash, then run: DevBridge.cmd ensure-ready " + leaseId;
        if (snapshot.Phase == BridgePhase.READY && !snapshot.RestartPending)
            return "Run: DevBridge.cmd test begin";
        if (snapshot.RestartPending || snapshot.Phase == BridgePhase.DRAINING ||
            snapshot.Phase == BridgePhase.RESTARTING || snapshot.Phase == BridgePhase.LOADING)
            return "Keep waiting. Do not launch, kill, or restart RimWorld yourself.";
        if (snapshot.Phase == BridgePhase.STOPPED && snapshot.Generation > 0)
            return "Run: DevBridge.cmd restart";
        return "Run: DevBridge.cmd wait-ready";
    }

    private static JsonLeaseInfo ToJsonLease(TestLease lease)
    {
        return new JsonLeaseInfo
        {
            Id = lease.Id,
            Agent = lease.Agent,
            Generation = lease.Generation,
            StartedUtc = lease.StartedUtc,
            Age = FormatAge(lease.StartedUtc),
            StaleIn = FormatStaleIn(lease.StartedUtc)
        };
    }

    private static string FormatStaleIn(DateTime startedUtc)
    {
        TimeSpan age = DateTime.UtcNow - startedUtc.ToUniversalTime();
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        TimeSpan remaining = LeaseStaleAfter - age;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        return FormatDuration(remaining);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        if (duration.TotalHours >= 1)
            return ((int)duration.TotalHours).ToString("00") + ":" + duration.Minutes.ToString("00") +
                ":" + duration.Seconds.ToString("00");
        return duration.Minutes.ToString("00") + ":" + duration.Seconds.ToString("00");
    }

    private static string FormatAge(DateTime startedUtc)
    {
        TimeSpan age = DateTime.UtcNow - startedUtc.ToUniversalTime();
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        return FormatDuration(age);
    }
}
