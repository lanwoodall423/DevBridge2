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
                return CoordinatorServer.Run(root, parsed.RuntimeSlotId, parsed.TicketId);

            if (parsed.Command.Count == 0)
            {
                PrintUsage();
                return 2;
            }

            return CoordinatorClient.Run(root, parsed.Command, parsed.RuntimeSlotId, parsed.TicketId);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("DevBridge error: " + exception.Message);
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("DevBridge commands: status | mods status | mods capture-baseline | mods restore-baseline | test begin | test session | test renew <lease-id> | test end <lease-id> | stop <lease-id> | ensure-ready <lease-id> | restart [--projects none|alias[,alias...]] | wait-ready | doctor");
        Console.WriteLine("Append --json to a non-session command for machine-readable output.");
    }
}

internal sealed class ParsedArguments
{
    internal string Root { get; private set; }
    internal string CoordinatorRoot { get; private set; }
    internal string RuntimeSlotId { get; private set; }
    internal string TicketId { get; private set; }
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

            if (argument.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
            {
                result.Root = argument.Substring("--root=".Length);
                continue;
            }

            if (string.Equals(argument, "--coordinator-root", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException("--coordinator-root needs a path");
                result.CoordinatorRoot = args[++index];
                continue;
            }

            if (argument.StartsWith("--coordinator-root=", StringComparison.OrdinalIgnoreCase))
            {
                result.CoordinatorRoot = argument.Substring("--coordinator-root=".Length);
                continue;
            }

            if (string.Equals(argument, "--runtime-slot", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException("--runtime-slot needs an identifier");
                result.RuntimeSlotId = args[++index];
                continue;
            }

            if (argument.StartsWith("--runtime-slot=", StringComparison.OrdinalIgnoreCase))
            {
                result.RuntimeSlotId = argument.Substring("--runtime-slot=".Length);
                continue;
            }

            if (string.Equals(argument, "--ticket", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException("--ticket needs an identifier");
                result.TicketId = args[++index];
                continue;
            }

            if (argument.StartsWith("--ticket=", StringComparison.OrdinalIgnoreCase))
            {
                result.TicketId = argument.Substring("--ticket=".Length);
                continue;
            }

            result.Command.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(result.Root) && !string.IsNullOrWhiteSpace(result.CoordinatorRoot) &&
            !RuntimeScope.PathsEqual(result.Root, result.CoordinatorRoot))
            throw new ArgumentException("--root and --coordinator-root must identify the same directory");

        result.CoordinatorRoot ??= result.Root;
        result.Root ??= result.CoordinatorRoot;
        result.TicketId ??= Environment.GetEnvironmentVariable("DEVBRIDGE_TICKET");
        if (string.IsNullOrWhiteSpace(result.RuntimeSlotId) && !string.IsNullOrWhiteSpace(result.Root) &&
            string.IsNullOrWhiteSpace(result.TicketId))
            result.RuntimeSlotId = RuntimeScope.ForRoot(result.Root);

        return result;
    }
}

internal static class RuntimeScope
{
    internal static string ForRoot(string root)
    {
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant()));
        return "slot-" + Convert.ToHexString(bytes).Substring(0, 8);
    }

    internal static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveTicketSlot(string root, string ticketId)
    {
        if (string.IsNullOrWhiteSpace(ticketId))
            return null;

        try
        {
            string statePath = Path.Combine(root, "Runtime", "state.json");
            if (!File.Exists(statePath))
                return null;
            PersistedState persisted = JsonSerializer.Deserialize<PersistedState>(
                File.ReadAllText(statePath), Program.JsonOptions);
            return persisted?.ScopeTickets?.FirstOrDefault(value =>
                string.Equals(value.Id, ticketId.Trim(), StringComparison.Ordinal))?.RuntimeSlotId;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class BridgeRequest
{
    public string Command { get; set; }
    public List<string> Arguments { get; set; } = new();
    public string Agent { get; set; }
    public int ClientProcessId { get; set; }
    public bool Json { get; set; }
    public string RuntimeSlotId { get; set; }
    public string CoordinatorRoot { get; set; }
    public string TicketId { get; set; }
    public string GoalId { get; set; }
    public string WakeId { get; set; }
    public string McpRequestId { get; set; }
}

internal static class CoordinatorClient
{
    internal static int Run(string root, IReadOnlyList<string> command, string runtimeSlotId = null,
        string ticketId = null)
    {
        string effectiveSlot = runtimeSlotId ?? RuntimeScope.ResolveTicketSlot(root, ticketId) ?? RuntimeScope.ForRoot(root);
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
                pipe = new NamedPipeClientStream(".", PipeNames.ForSlot(root, effectiveSlot), PipeDirection.InOut,
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
                StartServer(root, effectiveSlot, ticketId);
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
                Json = json,
                RuntimeSlotId = runtimeSlotId,
                CoordinatorRoot = root,
                TicketId = string.IsNullOrWhiteSpace(ticketId) ?
                    Environment.GetEnvironmentVariable("DEVBRIDGE_TICKET") : ticketId,
                GoalId = Environment.GetEnvironmentVariable("DEVBRIDGE_GOAL"),
                WakeId = Environment.GetEnvironmentVariable("DEVBRIDGE_WAKE"),
                McpRequestId = Environment.GetEnvironmentVariable("DEVBRIDGE_MCP_REQUEST")
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

    private static void StartServer(string root, string runtimeSlotId, string ticketId)
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
        if (!string.IsNullOrWhiteSpace(runtimeSlotId))
        {
            start.ArgumentList.Add("--runtime-slot");
            start.ArgumentList.Add(runtimeSlotId);
        }
        if (!string.IsNullOrWhiteSpace(ticketId))
        {
            start.ArgumentList.Add("--ticket");
            start.ArgumentList.Add(ticketId);
        }
        Process.Start(start)?.Dispose();
    }
}

internal static class CoordinatorServer
{
    internal static int Run(string root, string runtimeSlotId = null, string ticketId = null)
    {
        string slot = runtimeSlotId ?? RuntimeScope.ResolveTicketSlot(root, ticketId) ?? RuntimeScope.ForRoot(root);
        using Mutex mutex = new(false, PipeNames.MutexForSlot(slot));
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

        CoordinatorState state = new(root, new CoordinatorOptions
        {
            CoordinatorRoot = root,
            RuntimeSlotId = slot
        });
        state.StartRecoveryWork();

        while (true)
        {
            NamedPipeServerStream server = null;
            try
            {
                server = new NamedPipeServerStream(PipeNames.ForSlot(root, slot), PipeDirection.InOut, 16,
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
    internal static string ForRoot(string root) => ForSlot(root, RuntimeScope.ForRoot(root));

    internal static string ForSlot(string root, string slot) =>
        "DevBridge2-" + Hash(root + "|" + slot);

    internal static string MutexForRoot(string root) => "Local\\DevBridge2Coordinator-" + Hash(root);

    internal static string MutexForSlot(string slot) => "Local\\DevBridge2CoordinatorSlot-" + Hash(slot);

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
    WAITING_FOR_BRIDGE,
    RESTARTING,
    LOADING,
    ISOLATING,
    ERROR,
    STOPPED
}

internal sealed class PersistedState
{
    public string CoordinatorRoot { get; set; }
    public string RuntimeSlotId { get; set; }
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
    public string LaunchOwner { get; set; }
    public string LaunchRequestKey { get; set; }
    public string LastLaunchOwner { get; set; }
    public string LastLaunchRequestKey { get; set; }
    public int LaunchAttemptCount { get; set; }
    public int LaunchBudgetRemaining { get; set; }
    public DateTime? WaitingForBridgeDeadlineUtc { get; set; }
    public bool RequiresNewProcess { get; set; }
    public string ProfileMode { get; set; } = ModProfile.LegacyMode;
    public List<string> RequestedProjects { get; set; } = new();
    public List<string> ResolvedProjectPackageIds { get; set; } = new();
    public List<string> ResolvedMods { get; set; } = new();
    public string ProfileFingerprint { get; set; }
    public string BaselineFingerprint { get; set; }
    public string ModsConfigOwnership { get; set; }
    public string ModsConfigGeneratedHash { get; set; }
    public string ModsConfigGeneratedProfileFingerprint { get; set; }
    public int ModsConfigGeneratedGeneration { get; set; }
    public string ProfileErrorCode { get; set; }
    public string ProfileError { get; set; }
    public string ProfileConflict { get; set; }
    public PersistedProfileSnapshot LastKnownGoodProfile { get; set; }
    public PersistedProfileSnapshot RuntimeProfile { get; set; }
    public CrashIsolationIncident CrashIsolation { get; set; }
    public List<CrashIsolationIncident> CrashIsolationHistory { get; set; } = new();
    public string LaunchProfileFingerprint { get; set; }
    public bool LaunchProfileInstalled { get; set; }
    public bool LaunchAttemptStarted { get; set; }
    public int IsolationLaunchesRemaining { get; set; }
    public List<ScopeTicket> ScopeTickets { get; set; } = new();
    public List<TestLease> Leases { get; set; } = new();
}

internal sealed class PersistedProfileSnapshot
{
    public string Mode { get; set; } = ModProfile.LegacyMode;
    public List<string> RequestedProjects { get; set; } = new();
    public List<string> ResolvedProjectPackageIds { get; set; } = new();
    public List<string> ResolvedMods { get; set; } = new();
    public string ProfileFingerprint { get; set; }
    public string BaselineFingerprint { get; set; }

    internal ModProfile ToModProfile() => new()
    {
        Mode = Mode,
        RequestedProjects = (RequestedProjects ?? new List<string>()).ToList(),
        ResolvedProjectPackageIds = (ResolvedProjectPackageIds ?? new List<string>()).ToList(),
        ResolvedMods = (ResolvedMods ?? new List<string>()).ToList(),
        ProfileFingerprint = ProfileFingerprint,
        BaselineFingerprint = BaselineFingerprint
    };

    internal static PersistedProfileSnapshot FromModProfile(ModProfile profile) => profile == null ? null : new()
    {
        Mode = profile.Mode,
        RequestedProjects = (profile.RequestedProjects ?? new List<string>()).ToList(),
        ResolvedProjectPackageIds = (profile.ResolvedProjectPackageIds ?? new List<string>()).ToList(),
        ResolvedMods = (profile.ResolvedMods ?? new List<string>()).ToList(),
        ProfileFingerprint = profile.ProfileFingerprint,
        BaselineFingerprint = profile.BaselineFingerprint
    };
}

internal sealed class CrashIsolationSelection
{
    public List<string> Projects { get; set; } = new();
}

internal sealed class CrashIsolationAttempt
{
    public string AttemptId { get; set; }
    public string Kind { get; set; }
    public string ProfileFingerprint { get; set; }
    public List<string> RequestedProjects { get; set; } = new();
    public List<string> ResolvedProjectPackageIds { get; set; } = new();
    public string Result { get; set; }
    public int Generation { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStartUtcTicks { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public bool ProfileInstalled { get; set; }
    public bool ProcessExitObserved { get; set; }
    public string FailurePhase { get; set; }
    public string FailureCode { get; set; }
    public string FailureDetail { get; set; }
}

internal sealed class CrashIsolationDiagnosis
{
    public string Code { get; set; }
    public string Message { get; set; }
    public List<string> RequestedProjects { get; set; } = new();
    public List<string> ResolvedProjectPackageIds { get; set; } = new();
    public string ProfileFingerprint { get; set; }
}

internal sealed class CrashIsolationIncident
{
    // Original* fields are written once when an accepted project profile first
    // fails after its generated ModsConfig was safely installed. They are never
    // reused for temporary control or candidate profiles.
    public string IncidentId { get; set; }
    public string Status { get; set; }
    public string OriginalProfileMode { get; set; }
    public List<string> OriginalRequestedProjects { get; set; } = new();
    public List<string> OriginalResolvedProjectPackageIds { get; set; } = new();
    public List<string> OriginalResolvedMods { get; set; } = new();
    public string OriginalProfileFingerprint { get; set; }
    public string OriginalBaselineFingerprint { get; set; }
    public string OriginalLastKnownGoodFingerprint { get; set; }
    public int OriginalGeneration { get; set; }
    public string OriginalLaunchId { get; set; }
    public int OriginalProcessId { get; set; }
    public long OriginalProcessStartUtcTicks { get; set; }
    public DateTime OriginalFailureUtc { get; set; }
    public string OriginalFailurePhase { get; set; }
    public string OriginalFailureCode { get; set; }
    public string OriginalFailureDetail { get; set; }
    public bool OriginalProcessExitObserved { get; set; }
    public string OriginalExitInformation { get; set; }
    public Dictionary<string, string> OriginalDiagnosticMetadata { get; set; } = new();

    public string DiagnosisCode { get; set; }
    public string Diagnosis { get; set; }
    public string Stage { get; set; }
    public List<CrashIsolationDiagnosis> Diagnoses { get; set; } = new();
    public List<CrashIsolationAttempt> Attempts { get; set; } = new();
    public List<string> SearchPoolProjects { get; set; } = new();
    public List<string> DeltaCurrentProjects { get; set; } = new();
    public int DeltaGranularity { get; set; }
    public List<CrashIsolationSelection> PendingCandidates { get; set; } = new();
    public int PendingCandidateIndex { get; set; }
    public string PendingKind { get; set; }
    public bool SearchPoolKnownFail { get; set; }
    // A passing remainder is a durable candidate for the final recovery launch.
    // It is kept separate from the immutable accepted profile and from the
    // last-known-good control so a restart can resume this choice exactly.
    public PersistedProfileSnapshot SafeRemainderProfile { get; set; }
    public bool FinalControlBaselineAttempted { get; set; }
    public string CurrentAttemptId { get; set; }
    public string CurrentAttemptFingerprint { get; set; }
    public string CurrentAttemptKind { get; set; }
    public PersistedProfileSnapshot CurrentAttemptProfile { get; set; }
    public List<string> CurrentAttemptProjects { get; set; } = new();
    public string CurrentAttemptResult { get; set; }
    public string CurrentAttemptFailurePhase { get; set; }
    public string CurrentAttemptFailureCode { get; set; }
    public string CurrentAttemptFailureDetail { get; set; }
    public bool CurrentAttemptProfileInstalled { get; set; }
    public int IsolationLaunchesRemaining { get; set; }
}

internal sealed class GeneratedModsConfigManifest
{
    public string Hash { get; set; }
    public string ProfileFingerprint { get; set; }
    public int Generation { get; set; }
}

internal sealed class ScopeTicket
{
    public string Id { get; set; }
    public string RuntimeSlotId { get; set; }
    public string CoordinatorRoot { get; set; }
}

internal sealed class TestLease
{
    public string Id { get; set; }
    public string Agent { get; set; }
    public int ClientProcessId { get; set; }
    public int Generation { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
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

    [JsonPropertyName("coordinatorRoot")]
    public string CoordinatorRoot { get; set; }

    [JsonPropertyName("runtimeSlotId")]
    public string RuntimeSlotId { get; set; }

    [JsonPropertyName("goalId")]
    public string GoalId { get; set; }

    [JsonPropertyName("wakeId")]
    public string WakeId { get; set; }

    [JsonPropertyName("mcpRequestId")]
    public string McpRequestId { get; set; }

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

    [JsonPropertyName("launchOwner")]
    public string LaunchOwner { get; set; }

    [JsonPropertyName("launchAttemptCount")]
    public int LaunchAttemptCount { get; set; }

    [JsonPropertyName("launchBudgetRemaining")]
    public int LaunchBudgetRemaining { get; set; }

    [JsonPropertyName("waitingForBridgeDeadlineUtc")]
    public DateTime? WaitingForBridgeDeadlineUtc { get; set; }

    [JsonPropertyName("restartQueued")]
    public bool RestartQueued { get; set; }

    [JsonPropertyName("nextLeaseExpirationUtc")]
    public DateTime? NextLeaseExpirationUtc { get; set; }

    [JsonPropertyName("retryAfterSeconds")]
    public int? RetryAfterSeconds { get; set; }

    [JsonPropertyName("requiresNewProcess")]
    public bool RequiresNewProcess { get; set; }

    [JsonPropertyName("profileMode")]
    public string ProfileMode { get; set; }

    [JsonPropertyName("requestedProjects")]
    public List<string> RequestedProjects { get; set; } = new();

    [JsonPropertyName("resolvedProjectPackageIds")]
    public List<string> ResolvedProjectPackageIds { get; set; } = new();

    [JsonPropertyName("resolvedMods")]
    public List<string> ResolvedMods { get; set; } = new();

    [JsonPropertyName("profileFingerprint")]
    public string ProfileFingerprint { get; set; }

    [JsonPropertyName("baselineFingerprint")]
    public string BaselineFingerprint { get; set; }

    [JsonPropertyName("modsConfigOwnership")]
    public string ModsConfigOwnership { get; set; }

    [JsonPropertyName("profileConflict")]
    public string ProfileConflict { get; set; }

    [JsonPropertyName("runtimeProfileFingerprint")]
    public string RuntimeProfileFingerprint { get; set; }

    [JsonPropertyName("crashIsolation")]
    public CrashIsolationIncident CrashIsolation { get; set; }

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

    [JsonPropertyName("lastHeartbeatUtc")]
    public DateTime LastHeartbeatUtc { get; set; }

    [JsonPropertyName("expiresUtc")]
    public DateTime ExpiresUtc { get; set; }

    [JsonPropertyName("retryAfterSeconds")]
    public int RetryAfterSeconds { get; set; }

    [JsonPropertyName("age")]
    public string Age { get; set; }

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
    internal int MaxLaunchAttempts { get; init; } = 2;
    // Isolation launches are bounded separately from user-requested launches:
    // delta debugging can legitimately need more attempts than a normal retry.
    internal int IsolationMaxAttempts { get; init; } = 64;
    internal TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    internal TimeSpan LeaseHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan LeaseSessionPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan LeaseProgressInterval { get; init; } = TimeSpan.FromSeconds(5);
    internal IProcessAdapter ProcessAdapter { get; init; } = new SystemProcessAdapter();
    internal ICoordinatorClock Clock { get; init; } = SystemCoordinatorClock.Instance;
    internal string RimWorldExecutablePath { get; init; }
    internal string ModsConfigPath { get; init; }
    internal string CoordinatorRoot { get; init; }
    internal string RuntimeSlotId { get; init; }
    internal IReadOnlyList<string> InstalledModsRoots { get; init; }
    internal Action BeforeModsConfigWrite { get; init; }

    internal static CoordinatorOptions ForProduction()
    {
        TimeSpan timeout = TimeSpan.FromMinutes(6);
        string configured = Environment.GetEnvironmentVariable("DEVBRIDGE_READINESS_TIMEOUT_SECONDS");
        if (int.TryParse(configured, out int seconds) && seconds >= 30 && seconds <= 3600)
            timeout = TimeSpan.FromSeconds(seconds);

        return new CoordinatorOptions { ReadinessTimeout = timeout };
    }
}

internal sealed class ProfileException : Exception
{
    internal ProfileException(string code, string message) : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}

internal sealed class ModProfile
{
    internal const string LegacyMode = "legacy";
    internal const string BaselineMode = "baseline";
    internal const string ProjectsMode = "projects";

    internal string Mode { get; init; } = LegacyMode;
    internal List<string> RequestedProjects { get; init; } = new();
    internal List<string> ResolvedProjectPackageIds { get; init; } = new();
    internal List<string> ResolvedMods { get; init; } = new();
    internal string ProfileFingerprint { get; init; }
    internal string BaselineFingerprint { get; init; }

    internal ModProfile Clone() => new()
    {
        Mode = Mode,
        RequestedProjects = RequestedProjects.ToList(),
        ResolvedProjectPackageIds = ResolvedProjectPackageIds.ToList(),
        ResolvedMods = ResolvedMods.ToList(),
        ProfileFingerprint = ProfileFingerprint,
        BaselineFingerprint = BaselineFingerprint
    };
}

internal sealed class InstalledModMetadata
{
    internal string PackageId { get; init; }
    internal string DirectoryPath { get; init; }
    internal XDocument Document { get; init; }
    internal string MetadataError { get; init; }
    internal bool ReferencesLoaded { get; set; }
    internal List<string> Dependencies { get; } = new();
    internal List<string> LoadBefore { get; } = new();
    internal List<string> LoadAfter { get; } = new();
}

internal static class ModProfileResolver
{
    internal const string DevBridgePackageId = "lan.devbridge2";
    internal const string ForbiddenPackageId = "ferny.loadthemlast";

    internal static readonly string[] AlwaysOnPackageIds =
    {
        "zetrith.prepatcher",
        "brrainz.harmony",
        "taranchuk.fastergameloading",
        "ilyvion.loadingprogress",
        "ludeon.rimworld",
        "ludeon.rimworld.royalty",
        "ludeon.rimworld.ideology",
        "ludeon.rimworld.biotech",
        "ludeon.rimworld.anomaly",
        "ludeon.rimworld.odyssey",
        DevBridgePackageId,
        "mlie.dingongameloaded",
        "dubwise.dubsperformanceanalyzer.steam",
        "astryl.moderndevtools"
    };

    private static readonly IReadOnlyDictionary<string, string> ProjectAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deferred-reality"] = "lan.deferredreality.framework",
            ["insight-canvas"] = "lan.insightcanvas",
            ["knowledge-framework"] = "lan.knowledgeframework",
            ["frontier"] = "lan.frontier",
            ["aquaculture"] = "lan.aquaculture.fishing",
            ["horticulture"] = "lan.horticulture.novelseeds",
            ["wildlife"] = "lan.wildlife"
        };

    internal static bool TryGetProjectPackageId(string alias, out string packageId) =>
        ProjectAliases.TryGetValue(alias ?? string.Empty, out packageId);

    internal static IReadOnlyList<string> CanonicalAliases(IEnumerable<string> aliases)
    {
        List<string> result = new();
        foreach (string alias in aliases ?? Array.Empty<string>())
        {
            string trimmed = alias?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
                continue;
            if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
                throw new ProfileException("PROFILE_INVALID_REQUEST",
                    "--projects none must be used alone; it cannot be combined with a project alias.");
            if (!TryGetProjectPackageId(trimmed, out _))
                throw new ProfileException("PROFILE_UNKNOWN_PROJECT",
                    "Unknown project alias '" + trimmed + "'. Use: " +
                    string.Join(", ", ProjectAliases.Keys.OrderBy(value => value, StringComparer.Ordinal)) + ".");
            if (result.Contains(trimmed.ToLowerInvariant(), StringComparer.Ordinal))
                throw new ProfileException("PROFILE_DUPLICATE_PROJECT",
                    "Project alias '" + trimmed + "' was requested more than once.");
            result.Add(trimmed.ToLowerInvariant());
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    internal static ModProfile Resolve(string coordinatorRoot, string baselineFingerprint,
        IReadOnlyList<string> aliases, IReadOnlyList<string> configuredRoots = null)
    {
        if (string.IsNullOrWhiteSpace(baselineFingerprint))
            throw new ProfileException("PROFILE_BASELINE_MISSING",
                "Capture the user ModsConfig first with: DevBridge.cmd mods capture-baseline");

        List<string> canonicalAliases = CanonicalAliases(aliases).ToList();
        string mode = canonicalAliases.Count == 0 ? ModProfile.BaselineMode : ModProfile.ProjectsMode;
        List<string> requestedPackageIds = canonicalAliases
            .Select(alias => ProjectAliases[alias])
            .ToList();
        List<string> roots = AlwaysOnPackageIds.Concat(requestedPackageIds).ToList();

        Dictionary<string, List<InstalledModMetadata>> installed = Discover(coordinatorRoot, configuredRoots);
        Dictionary<string, InstalledModMetadata> resolved = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> visiting = new(StringComparer.OrdinalIgnoreCase);
        List<string> stack = new();
        Dictionary<string, int> discoveryOrder = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> rootOrder = new(StringComparer.OrdinalIgnoreCase);
        int discovery = 0;

        for (int index = 0; index < roots.Count; index++)
        {
            InstalledModMetadata root = Find(installed, roots[index], "project root");
            rootOrder.TryAdd(root.PackageId, index);
            Visit(root);
        }

        Dictionary<string, HashSet<string>> edges = resolved.Keys.ToDictionary(
            key => key, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> indegree = resolved.Keys.ToDictionary(
            key => key, _ => 0, StringComparer.OrdinalIgnoreCase);

        foreach (InstalledModMetadata metadata in resolved.Values)
        {
            foreach (string dependency in metadata.Dependencies)
            {
                InstalledModMetadata dependencyMetadata = Find(installed, dependency, "dependency of " + metadata.PackageId);
                AddEdge(dependencyMetadata.PackageId, metadata.PackageId);
            }

            foreach (string before in metadata.LoadBefore)
            {
                if (resolved.TryGetValue(before, out InstalledModMetadata target))
                    AddEdge(metadata.PackageId, target.PackageId);
            }

            foreach (string after in metadata.LoadAfter)
            {
                if (resolved.TryGetValue(after, out InstalledModMetadata target))
                    AddEdge(target.PackageId, metadata.PackageId);
            }
        }

        List<string> orderedKeys = new();
        List<string> ready = indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key).ToList();
        while (ready.Count > 0)
        {
            ready.Sort(CompareOrder);
            string next = ready[0];
            ready.RemoveAt(0);
            orderedKeys.Add(next);
            foreach (string dependent in edges[next])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                    ready.Add(dependent);
            }
        }

        if (orderedKeys.Count != resolved.Count)
        {
            string cycle = string.Join(", ", indegree.Where(pair => pair.Value > 0)
                .Select(pair => resolved[pair.Key].PackageId).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            throw new ProfileException("PROFILE_DEPENDENCY_CYCLE",
                "The requested profile contains a dependency/load-order cycle involving: " + cycle + ".");
        }

        List<string> resolvedMods = orderedKeys.Select(key => resolved[key].PackageId).ToList();
        List<string> resolvedProjects = requestedPackageIds
            .Select(packageId => Find(installed, packageId, "project root").PackageId)
            .ToList();
        string fingerprint = Fingerprint(mode, baselineFingerprint, canonicalAliases, resolvedProjects, resolvedMods);
        ModProfile profile = new()
        {
            Mode = mode,
            RequestedProjects = canonicalAliases,
            ResolvedProjectPackageIds = resolvedProjects,
            ResolvedMods = resolvedMods,
            ProfileFingerprint = fingerprint,
            BaselineFingerprint = baselineFingerprint
        };
        ValidateResolvedProfile(profile);
        return profile;

        void Visit(InstalledModMetadata metadata)
        {
            if (string.Equals(metadata.PackageId, ForbiddenPackageId, StringComparison.OrdinalIgnoreCase))
                throw new ProfileException("PROFILE_FORBIDDEN_MOD",
                    "The profile must never include " + ForbiddenPackageId + ".");

            if (visiting.TryGetValue(metadata.PackageId, out int status))
            {
                if (status == 1)
                {
                    int start = stack.FindIndex(value => string.Equals(value, metadata.PackageId,
                        StringComparison.OrdinalIgnoreCase));
                    IEnumerable<string> cycle = (start < 0 ? stack : stack.Skip(start))
                        .Concat(new[] { metadata.PackageId });
                    throw new ProfileException("PROFILE_DEPENDENCY_CYCLE",
                        "The requested profile contains a dependency cycle: " + string.Join(" -> ", cycle) + ".");
                }
                return;
            }

            visiting[metadata.PackageId] = 1;
            stack.Add(metadata.PackageId);
            LoadReferences(metadata);
            foreach (string dependency in metadata.Dependencies)
                Visit(Find(installed, dependency, "dependency of " + metadata.PackageId));
            stack.RemoveAt(stack.Count - 1);
            visiting[metadata.PackageId] = 2;
            resolved[metadata.PackageId] = metadata;
            discoveryOrder.TryAdd(metadata.PackageId, discovery++);
        }

        int CompareOrder(string left, string right)
        {
            int leftRoot = rootOrder.TryGetValue(left, out int lr) ? lr : int.MaxValue;
            int rightRoot = rootOrder.TryGetValue(right, out int rr) ? rr : int.MaxValue;
            int result = leftRoot.CompareTo(rightRoot);
            if (result != 0)
                return result;
            result = discoveryOrder[left].CompareTo(discoveryOrder[right]);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        void AddEdge(string from, string to)
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) || !edges.ContainsKey(from) ||
                !edges.ContainsKey(to) || !edges[from].Add(to))
                return;
            indegree[to]++;
        }
    }

    internal static ModProfile CreateBaselineProfile(string baselineFingerprint)
    {
        if (string.IsNullOrWhiteSpace(baselineFingerprint))
            throw new ProfileException("PROFILE_BASELINE_MISSING",
                "The durable baseline fingerprint is missing; no control profile can be run.");

        List<string> resolvedMods = AlwaysOnPackageIds.ToList();
        ModProfile profile = new()
        {
            Mode = ModProfile.BaselineMode,
            RequestedProjects = new List<string>(),
            ResolvedProjectPackageIds = new List<string>(),
            ResolvedMods = resolvedMods,
            ProfileFingerprint = Fingerprint(ModProfile.BaselineMode, baselineFingerprint,
                Array.Empty<string>(), Array.Empty<string>(), resolvedMods),
            BaselineFingerprint = baselineFingerprint
        };
        ValidateResolvedProfile(profile);
        return profile;
    }

    internal static void ValidateResolvedProfile(ModProfile profile)
    {
        if (profile == null)
            throw new ProfileException("PROFILE_INVALID_STATE", "The accepted profile is missing.");
        if (profile.Mode != ModProfile.BaselineMode && profile.Mode != ModProfile.ProjectsMode)
            throw new ProfileException("PROFILE_INVALID_STATE", "The accepted profile mode is invalid: " + profile.Mode + ".");
        if (!IsSha256(profile.BaselineFingerprint))
            throw new ProfileException("PROFILE_INVALID_STATE", "The accepted profile has no valid baseline fingerprint.");

        List<string> aliases;
        try
        {
            aliases = CanonicalAliases(profile.RequestedProjects).ToList();
        }
        catch (ProfileException exception)
        {
            throw new ProfileException("PROFILE_INVALID_STATE",
                "The accepted profile has invalid project roots: " + exception.Message);
        }

        if (profile.Mode == ModProfile.BaselineMode && aliases.Count != 0)
            throw new ProfileException("PROFILE_INVALID_STATE", "A baseline profile cannot contain project roots.");
        if (profile.Mode == ModProfile.ProjectsMode && aliases.Count == 0)
            throw new ProfileException("PROFILE_INVALID_STATE", "A project profile must contain at least one project root.");

        List<string> expectedProjects = aliases.Select(alias => ProjectAliases[alias]).ToList();
        if (!SequenceEqualPackageIds(expectedProjects, profile.ResolvedProjectPackageIds))
            throw new ProfileException("PROFILE_INVALID_STATE", "The accepted profile's project package IDs do not match its aliases.");

        List<string> resolvedMods = profile.ResolvedMods ?? new List<string>();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string packageId in resolvedMods)
        {
            if (string.IsNullOrWhiteSpace(packageId) || packageId.Any(char.IsWhiteSpace))
                throw new ProfileException("PROFILE_INVALID_STATE", "The accepted profile contains a malformed package ID.");
            if (!seen.Add(packageId))
                throw new ProfileException("PROFILE_INVALID_STATE", "The accepted profile contains duplicate package ID " + packageId + ".");
            if (string.Equals(packageId, ForbiddenPackageId, StringComparison.OrdinalIgnoreCase))
                throw new ProfileException("PROFILE_INVALID_STATE", "The accepted profile contains forbidden package ID " + ForbiddenPackageId + ".");
        }

        foreach (string required in AlwaysOnPackageIds)
        {
            if (!seen.Contains(required))
                throw new ProfileException("PROFILE_REQUIRED_MOD_MISSING",
                    "The accepted profile is missing required tooling package " + required + ".");
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileFingerprint))
            throw new ProfileException("PROFILE_INVALID_STATE", "The accepted profile has no fingerprint.");
        string expectedFingerprint = Fingerprint(profile.Mode, profile.BaselineFingerprint,
            aliases, expectedProjects, resolvedMods);
        if (!string.Equals(profile.ProfileFingerprint, expectedFingerprint, StringComparison.Ordinal))
            throw new ProfileException("PROFILE_FINGERPRINT_MISMATCH",
                "The accepted profile fingerprint does not match its persisted roots and ordered package list.");
    }

    private static bool SequenceEqualPackageIds(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left == null || right == null || left.Count != right.Count)
            return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool IsSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            return false;
        return value.All(character => Uri.IsHexDigit(character));
    }

    private static string Fingerprint(string mode, string baselineFingerprint, IReadOnlyList<string> aliases,
        IReadOnlyList<string> projectIds, IReadOnlyList<string> resolvedMods)
    {
        string canonical = string.Join("\n", new[]
        {
            "mode=" + mode,
            "baseline=" + baselineFingerprint.ToUpperInvariant(),
            "projects=" + string.Join(",", aliases),
            "projectPackageIds=" + string.Join(",", projectIds.Select(value => value.ToLowerInvariant())),
            "mods=" + string.Join(",", resolvedMods.Select(value => value.ToLowerInvariant()))
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static Dictionary<string, List<InstalledModMetadata>> Discover(string coordinatorRoot,
        IReadOnlyList<string> configuredRoots)
    {
        List<string> roots = new();
        HashSet<string> seenRoots = new(StringComparer.OrdinalIgnoreCase);
        void AddRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            string full = Path.GetFullPath(path);
            if (seenRoots.Add(full))
                roots.Add(full);
        }

        foreach (string path in configuredRoots ?? Array.Empty<string>())
            AddRoot(path);
        AddRoot(coordinatorRoot);
        AddRoot(Path.Combine(coordinatorRoot, ".."));
        AddRoot(Path.Combine(coordinatorRoot, "..", "..", "Data"));
        AddRoot(Path.Combine(coordinatorRoot, "..", "..", "Data", "Mods"));
        string workshopOverride = Environment.GetEnvironmentVariable("RIMWORLD_WORKSHOP_PATH");
        AddRoot(workshopOverride);

        DirectoryInfo cursor = new(Path.GetFullPath(coordinatorRoot));
        while (cursor != null)
        {
            if (string.Equals(cursor.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                AddRoot(Path.Combine(cursor.FullName, "workshop", "content", "294100"));
            cursor = cursor.Parent;
        }

        Dictionary<string, List<InstalledModMetadata>> result =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenAboutFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            foreach (string directory in EnumerateModDirectories(root))
            {
                string aboutPath = Path.Combine(directory, "About", "About.xml");
                if (!seenAboutFiles.Add(aboutPath))
                    continue;
                try
                {
                    XDocument document = XDocument.Load(aboutPath, LoadOptions.PreserveWhitespace);
                    // Dependency entries also contain packageId elements. Only the direct
                    // packageId of ModMetaData identifies this installed mod.
                    string packageId = document.Root?.Elements().FirstOrDefault(value =>
                        string.Equals(value.Name.LocalName, "packageId", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                    if (string.IsNullOrWhiteSpace(packageId))
                        continue;
                    InstalledModMetadata metadata = new()
                    {
                        PackageId = packageId,
                        DirectoryPath = directory,
                        Document = document
                    };
                    if (!result.TryGetValue(packageId, out List<InstalledModMetadata> candidates))
                    {
                        candidates = new List<InstalledModMetadata>();
                        result[packageId] = candidates;
                    }
                    candidates.Add(metadata);
                }
                catch (Exception exception)
                {
                    // Keep a recoverable package ID when possible so a relevant malformed mod
                    // reports malformed metadata rather than being mistaken for a missing mod.
                    string raw = null;
                    try { raw = File.ReadAllText(aboutPath); } catch { }
                    string packageId = TryExtractPackageId(raw);
                    if (string.IsNullOrWhiteSpace(packageId))
                        continue;
                    InstalledModMetadata metadata = new()
                    {
                        PackageId = packageId,
                        DirectoryPath = directory,
                        MetadataError = "About.xml could not be parsed: " + exception.Message
                    };
                    if (!result.TryGetValue(packageId, out List<InstalledModMetadata> candidates))
                    {
                        candidates = new List<InstalledModMetadata>();
                        result[packageId] = candidates;
                    }
                    candidates.Add(metadata);
                }
            }
        }

        return result;
    }

    private static string TryExtractPackageId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        Match match = Regex.Match(raw,
            @"<packageId\b[^>]*>\s*(?<id>[^<]+?)\s*</packageId\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["id"].Value.Trim() : null;
    }

    private static IEnumerable<string> EnumerateModDirectories(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        string directAbout = Path.Combine(root, "About", "About.xml");
        if (File.Exists(directAbout))
        {
            yield return root;
            yield break;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToList();
        }
        catch { yield break; }
        foreach (string child in children)
        {
            if (File.Exists(Path.Combine(child, "About", "About.xml")))
                yield return child;
        }
    }

    private static InstalledModMetadata Find(Dictionary<string, List<InstalledModMetadata>> installed,
        string packageId, string context)
    {
        if (string.Equals(packageId, ForbiddenPackageId, StringComparison.OrdinalIgnoreCase))
            throw new ProfileException("PROFILE_FORBIDDEN_MOD",
                "The profile must never include " + ForbiddenPackageId + " (required by " + context + ").");
        if (!installed.TryGetValue(packageId, out List<InstalledModMetadata> candidates) || candidates.Count == 0)
            throw new ProfileException("PROFILE_MISSING_PACKAGE",
                "Missing installed package " + packageId + " required by " + context + ". Check the local Mods and Steam Workshop installations.");
        if (candidates.Count > 1)
            throw new ProfileException("PROFILE_AMBIGUOUS_PACKAGE",
                "Package ID " + packageId + " is ambiguous; installed candidates are: " +
                string.Join("; ", candidates.Select(value => value.DirectoryPath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) + ".");
        if (!string.IsNullOrWhiteSpace(candidates[0].MetadataError))
            throw new ProfileException("PROFILE_MALFORMED_METADATA",
                "Installed metadata for package " + packageId + " is malformed at " +
                candidates[0].DirectoryPath + ": " + candidates[0].MetadataError);
        return candidates[0];
    }

    private static void LoadReferences(InstalledModMetadata metadata)
    {
        if (metadata.ReferencesLoaded)
            return;
        metadata.ReferencesLoaded = true;
        XElement root = metadata.Document.Root;
        if (root == null)
            throw new ProfileException("PROFILE_MALFORMED_METADATA", "Installed metadata has no XML root: " + metadata.DirectoryPath);

        ReadReferences(root, "modDependencies", metadata.Dependencies, metadata, required: true);
        ReadReferences(root, "loadBefore", metadata.LoadBefore, metadata, required: false);
        ReadReferences(root, "loadAfter", metadata.LoadAfter, metadata, required: false);
    }

    private static void ReadReferences(XElement root, string sectionName, List<string> destination,
        InstalledModMetadata metadata, bool required)
    {
        XElement section = root.Elements().FirstOrDefault(value =>
            string.Equals(value.Name.LocalName, sectionName, StringComparison.OrdinalIgnoreCase));
        if (section == null)
            return;
        if (section.Nodes().Any(node => node switch
        {
            XElement element => !string.Equals(element.Name.LocalName, "li", StringComparison.OrdinalIgnoreCase),
            XText text => !string.IsNullOrWhiteSpace(text.Value),
            _ => true
        }))
            throw new ProfileException("PROFILE_MALFORMED_METADATA",
                "Installed metadata for " + metadata.PackageId + " has a malformed " + sectionName + " section.");

        foreach (XElement child in section.Elements())
        {
            XElement li = child;
            XElement package = li.Elements().FirstOrDefault(value =>
                string.Equals(value.Name.LocalName, "packageId", StringComparison.OrdinalIgnoreCase));
            string value = package?.Value.Trim();
            if (package == null && !li.Elements().Any())
                value = li.Value.Trim();
            bool extraContent = li.Elements().Any(element => package == null || !ReferenceEquals(element, package)) ||
                (package != null && li.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)));
            if (extraContent || string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
                throw new ProfileException("PROFILE_MALFORMED_METADATA",
                    "Installed metadata for " + metadata.PackageId + " has a malformed " + sectionName + " entry.");
            destination.Add(value);
        }
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
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    private readonly string root;
    private readonly string runtimeRoot;
    private readonly string statePath;
    private readonly string readinessPath;
    private readonly string baselinePath;
    private readonly string generatedManifestPath;
    private readonly string rimWorldExe;
    private readonly string modsConfigPath;
    private readonly string coordinatorRoot;
    private readonly string runtimeSlotId;
    private readonly CoordinatorOptions options;
    private readonly IProcessAdapter processAdapter;
    private readonly ICoordinatorClock clock;
    private readonly object gate = new();
    private readonly object lifecycleGate = new();
    private PersistedState state;
    private Task restartTask;
    private Task launchTask;
    private Task isolationTask;

    private sealed class MaintenanceValidation
    {
        internal bool Safe { get; init; }
        internal string ErrorCode { get; init; }
        internal string Error { get; init; }
    }

    private sealed class RestartArguments
    {
        internal string LeaseId { get; init; }
        internal bool HasProjects { get; init; }
        internal List<string> Projects { get; init; } = new();
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
        coordinatorRoot = Path.GetFullPath(this.options.CoordinatorRoot ?? this.root);
        if (!RuntimeScope.PathsEqual(this.root, coordinatorRoot))
            throw new InvalidOperationException("Coordinator root does not match the runtime root.");
        runtimeSlotId = this.options.RuntimeSlotId ?? RuntimeScope.ForRoot(this.root);
        if (string.IsNullOrWhiteSpace(runtimeSlotId))
            throw new InvalidOperationException("Runtime slot identity is required.");
        processAdapter = this.options.ProcessAdapter ?? new SystemProcessAdapter();
        clock = this.options.Clock ?? SystemCoordinatorClock.Instance;
        runtimeRoot = Path.Combine(this.root, "Runtime");
        statePath = Path.Combine(runtimeRoot, "state.json");
        readinessPath = Path.Combine(runtimeRoot, "readiness.json");
        baselinePath = Path.Combine(runtimeRoot, "ModsConfig.baseline.xml");
        generatedManifestPath = Path.Combine(runtimeRoot, "ModsConfig.generated.json");
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
            if (IsolationActiveLocked() && state.CrashIsolation?.CurrentAttemptResult != null)
                ResumePersistedIsolationResultLocked();
            else if (IsolationActiveLocked() && state.Phase == BridgePhase.LOADING && state.ProcessId > 0)
            {
                if (IsolationLaunchStateMatchesLocked())
                    StartMonitorLaunchLocked(state.TargetGeneration);
                else
                    FinalizeIsolationEnvironmentalLocked("ISOLATION_PROFILE_MISMATCH",
                        "the persisted isolation launch profile does not match the durable candidate; no replacement launch was attempted");
            }
            else if (IsolationActiveLocked() && state.Phase == BridgePhase.LOADING)
                FailLaunch("the persisted isolation attempt has no verified process identity; attribution was not attempted",
                    "ISOLATION_RECOVERY_AMBIGUOUS");
            else if (IsolationActiveLocked())
                StartIsolationWorkerLocked();
            else if (state.RestartPending && state.Phase == BridgePhase.LOADING && state.ProcessId > 0)
                StartMonitorLaunchLocked(state.TargetGeneration);
            else if (state.RestartPending && state.Phase == BridgePhase.LOADING)
                FailLaunch("the persisted launch has no verified process identity; no replacement launch was attempted",
                    "LAUNCH_RECOVERY_AMBIGUOUS");
            else if (state.RestartPending)
                StartRestartWorkerLocked(state.TargetGeneration, state.LaunchOwner);
            else if (state.Phase == BridgePhase.LOADING && state.ProcessId > 0)
                StartMonitorLaunchLocked(state.TargetGeneration);
            else if (state.Phase == BridgePhase.LOADING)
                FailLaunch("the persisted launch has no verified process identity; no replacement launch was attempted",
                    "LAUNCH_RECOVERY_AMBIGUOUS");
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
        request ??= new BridgeRequest();
        request.Arguments ??= new List<string>();
        if (!TryResolveScope(request, emit))
            return 4;

        string command = (request.Command ?? string.Empty).Trim().ToLowerInvariant();
        List<string> arguments = request.Arguments ?? new List<string>();

        return command switch
        {
            "status" => Status(request, emit),
            "mods" => Mods(arguments, emit),
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

    private bool TryResolveScope(BridgeRequest request, Action<string> emit)
    {
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(request.TicketId))
            {
                ScopeTicket ticket = state.ScopeTickets.FirstOrDefault(value =>
                    string.Equals(value.Id, request.TicketId.Trim(), StringComparison.Ordinal));
                if (ticket == null)
                {
                    emit("Scope denied: the ticket is not bound to an authoritative runtime slot.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return false;
                }

                if ((!string.IsNullOrWhiteSpace(request.CoordinatorRoot) &&
                     !RuntimeScope.PathsEqual(request.CoordinatorRoot, ticket.CoordinatorRoot)) ||
                    (!string.IsNullOrWhiteSpace(request.RuntimeSlotId) &&
                     !string.Equals(request.RuntimeSlotId, ticket.RuntimeSlotId, StringComparison.Ordinal)))
                {
                    emit("Scope denied: the ticket scope conflicts with the requested runtime slot.");
                    EmitNextCommand(emit, "DevBridge.cmd doctor");
                    return false;
                }

                request.RuntimeSlotId = ticket.RuntimeSlotId;
                request.CoordinatorRoot = ticket.CoordinatorRoot;
            }

            if (string.IsNullOrWhiteSpace(request.CoordinatorRoot))
                request.CoordinatorRoot = coordinatorRoot;
            if (string.IsNullOrWhiteSpace(request.RuntimeSlotId))
                request.RuntimeSlotId = runtimeSlotId;

            if (!RuntimeScope.PathsEqual(request.CoordinatorRoot, coordinatorRoot) ||
                !string.Equals(request.RuntimeSlotId, runtimeSlotId, StringComparison.Ordinal))
            {
                emit("Scope denied: runtime slot and coordinator root do not match this coordinator.");
                EmitNextCommand(emit, "DevBridge.cmd doctor");
                return false;
            }

            return true;
        }
    }

    private int Test(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        if (arguments.Count == 0)
        {
            emit("Usage: DevBridge.cmd test begin | test session | test renew <lease-id> | test end <lease-id>");
            return 2;
        }

        return arguments[0].Trim().ToLowerInvariant() switch
        {
            "begin" => BeginLease(request, emit, connected),
            "session" => SessionLease(request, emit, connected),
            "renew" => RenewLease(request, arguments, emit),
            "end" => EndLease(request, arguments, emit),
            _ => Unknown("test " + arguments[0], emit)
        };
    }

    private int Mods(IReadOnlyList<string> arguments, Action<string> emit)
    {
        if (arguments.Count == 0)
        {
            emit("Usage: DevBridge.cmd mods status | mods capture-baseline | mods restore-baseline");
            return 2;
        }

        return arguments[0].Trim().ToLowerInvariant() switch
        {
            "status" when arguments.Count == 1 => ModsStatus(emit),
            "capture-baseline" when arguments.Count == 1 => CaptureBaseline(emit),
            "restore-baseline" when arguments.Count == 1 => RestoreBaseline(emit),
            _ => Unknown("mods " + string.Join(" ", arguments), emit)
        };
    }

    private int ModsStatus(Action<string> emit)
    {
        PersistedState snapshot;
        lock (gate)
        {
            snapshot = CloneStateLocked();
            snapshot.BaselineFingerprint = ReadBaselineFingerprintLocked() ?? snapshot.BaselineFingerprint;
            snapshot.ModsConfigOwnership = CurrentModsConfigOwnershipLocked();
        }

        emit("DevBridge2 mod profiles");
        EmitProfile(snapshot, emit);
        if (!string.IsNullOrWhiteSpace(snapshot.ProfileError))
            emit("Profile error: " + snapshot.ProfileErrorCode + " - " + snapshot.ProfileError);
        if (!string.IsNullOrWhiteSpace(snapshot.ProfileConflict))
            emit("Profile conflict: " + snapshot.ProfileConflict);
        return 0;
    }

    private int CaptureBaseline(Action<string> emit)
    {
        lock (lifecycleGate)
        {
            lock (gate)
            {
                if (!CanChangeModsConfigLocked(emit))
                    return 4;
                if (!File.Exists(modsConfigPath))
                {
                    emit("Baseline capture failed: ModsConfig.xml was not found at " + modsConfigPath + ".");
                    return 4;
                }

                byte[] contents = File.ReadAllBytes(modsConfigPath);
                string fingerprint = HashBytes(contents);
                string ownership = CurrentModsConfigOwnershipLocked(contents, fingerprint);
                if (ownership == "DEVBRIDGE_GENERATED" || ownership == "DEVBRIDGE_PENDING")
                {
                    RecordProfileErrorLocked("PROFILE_BASELINE_GENERATED",
                        "The current ModsConfig.xml was generated by DevBridge; edit it intentionally, then capture the changed file.");
                    emit("Baseline capture refused: the current ModsConfig.xml is DevBridge-generated.");
                    emit("Error code: PROFILE_BASELINE_GENERATED");
                    return 4;
                }

                options.BeforeModsConfigWrite?.Invoke();
                byte[] latest;
                try
                {
                    latest = File.ReadAllBytes(modsConfigPath);
                }
                catch
                {
                    RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                        "ModsConfig.xml changed or disappeared while preparing the baseline capture.");
                    emit("Baseline capture refused: ModsConfig.xml changed while the capture was preparing.");
                    emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                    return 4;
                }
                if (!string.Equals(HashBytes(latest), fingerprint, StringComparison.Ordinal))
                {
                    RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                        "ModsConfig.xml changed while preparing the baseline capture.");
                    emit("Baseline capture refused: an unexpected edit would be captured as the baseline.");
                    emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                    return 4;
                }
                try
                {
                    EnsureNoMatchingRimWorldProcess();
                }
                catch (ProfileException exception)
                {
                    RecordProfileErrorLocked(exception.Code, exception.Message);
                    emit("Baseline capture refused: " + exception.Message);
                    emit("Error code: " + exception.Code);
                    return 4;
                }
                catch (ProcessInspectionException)
                {
                    RecordProfileErrorLocked(ProcessInspection.ErrorCode, ProcessInspection.Message);
                    emit("Baseline capture refused: " + ProcessInspection.Message);
                    emit("Error code: " + ProcessInspection.ErrorCode);
                    return 4;
                }

                AtomicWriteFile(baselinePath, contents);
                ClearGeneratedModsConfigManifestLocked();
                state.BaselineFingerprint = fingerprint;
                state.ModsConfigOwnership = "BASELINE";
                state.ModsConfigGeneratedHash = null;
                state.ModsConfigGeneratedProfileFingerprint = null;
                state.ModsConfigGeneratedGeneration = 0;
                ClearActiveProfileLocked();
                state.LastKnownGoodProfile = PersistedProfileSnapshot.FromModProfile(
                    ModProfileResolver.CreateBaselineProfile(fingerprint));
                state.RuntimeProfile = state.LastKnownGoodProfile;
                state.CrashIsolation = null;
                state.LaunchProfileFingerprint = null;
                state.LaunchProfileInstalled = false;
                state.LaunchAttemptStarted = false;
                state.ProfileErrorCode = null;
                state.ProfileError = null;
                state.ProfileConflict = null;
                SaveStateLocked();
                emit("Captured the user ModsConfig baseline byte-for-byte.");
                emit("Baseline fingerprint: " + fingerprint);
                emit("Next action: choose an opt-in profile with DevBridge.cmd restart --projects none or --projects <alias>.");
                return 0;
            }
        }
    }

    private int RestoreBaseline(Action<string> emit)
    {
        lock (lifecycleGate)
        {
            lock (gate)
            {
                if (!CanChangeModsConfigLocked(emit))
                    return 4;
                if (!File.Exists(baselinePath))
                {
                    emit("Baseline restore failed: no captured baseline exists.");
                    emit("Next action: DevBridge.cmd mods capture-baseline");
                    return 4;
                }

                byte[] baseline = File.ReadAllBytes(baselinePath);
                string baselineFingerprint = HashBytes(baseline);
                if (!File.Exists(modsConfigPath))
                {
                    emit("Baseline restore failed: ModsConfig.xml was not found at " + modsConfigPath + ".");
                    return 4;
                }

                byte[] current = File.ReadAllBytes(modsConfigPath);
                string currentFingerprint = HashBytes(current);
                string ownership = CurrentModsConfigOwnershipLocked(current, currentFingerprint);
                if (currentFingerprint != baselineFingerprint && ownership != "DEVBRIDGE_GENERATED" &&
                    ownership != "DEVBRIDGE_PENDING")
                {
                    RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                        "ModsConfig.xml differs from the captured baseline and is not a known DevBridge-generated file.");
                    emit("Baseline restore refused: an unexpected user edit would be overwritten.");
                    emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                    emit("Capture the intentional edit as the new baseline, or restore it manually before retrying.");
                    return 4;
                }

                if (currentFingerprint != baselineFingerprint)
                {
                    options.BeforeModsConfigWrite?.Invoke();
                    byte[] latest;
                    try
                    {
                        latest = File.ReadAllBytes(modsConfigPath);
                    }
                    catch
                    {
                        RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                            "ModsConfig.xml changed or disappeared while preparing the baseline restore.");
                        emit("Baseline restore refused: ModsConfig.xml changed while the restore was preparing.");
                        emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                        return 4;
                    }

                    if (!string.Equals(HashBytes(latest), currentFingerprint, StringComparison.Ordinal))
                    {
                        RecordProfileErrorLocked("MODS_CONFIG_EXTERNAL_EDIT",
                            "ModsConfig.xml changed while preparing the baseline restore.");
                        emit("Baseline restore refused: an unexpected edit would be overwritten.");
                        emit("Error code: MODS_CONFIG_EXTERNAL_EDIT");
                        return 4;
                    }

                    try
                    {
                        EnsureNoMatchingRimWorldProcess();
                    }
                    catch (ProfileException exception)
                    {
                        RecordProfileErrorLocked(exception.Code, exception.Message);
                        emit("Baseline restore refused: " + exception.Message);
                        emit("Error code: " + exception.Code);
                        return 4;
                    }
                    catch (ProcessInspectionException)
                    {
                        RecordProfileErrorLocked(ProcessInspection.ErrorCode, ProcessInspection.Message);
                        emit("Baseline restore refused: " + ProcessInspection.Message);
                        emit("Error code: " + ProcessInspection.ErrorCode);
                        return 4;
                    }

                    AtomicWriteFile(modsConfigPath, baseline);
                }
                ClearGeneratedModsConfigManifestLocked();
                state.BaselineFingerprint = baselineFingerprint;
                state.ModsConfigOwnership = "BASELINE";
                state.ModsConfigGeneratedHash = null;
                state.ModsConfigGeneratedProfileFingerprint = null;
                state.ModsConfigGeneratedGeneration = 0;
                ClearActiveProfileLocked();
                state.LastKnownGoodProfile = PersistedProfileSnapshot.FromModProfile(
                    ModProfileResolver.CreateBaselineProfile(baselineFingerprint));
                state.RuntimeProfile = state.LastKnownGoodProfile;
                state.CrashIsolation = null;
                state.LaunchProfileFingerprint = null;
                state.LaunchProfileInstalled = false;
                state.LaunchAttemptStarted = false;
                state.ProfileErrorCode = null;
                state.ProfileError = null;
                state.ProfileConflict = null;
                SaveStateLocked();
                emit(currentFingerprint == baselineFingerprint
                    ? "ModsConfig.xml already matches the captured baseline."
                    : "Restored ModsConfig.xml atomically from the captured byte-for-byte baseline.");
                emit("Baseline fingerprint: " + baselineFingerprint);
                emit("Restoration occurs only while no RimWorld process, lease, or pending restart is active.");
                return 0;
            }
        }
    }

    private int Help(Action<string> emit)
    {
        emit("DevBridge commands:");
        emit("  DevBridge.cmd status");
        emit("  DevBridge.cmd mods status");
        emit("  DevBridge.cmd mods capture-baseline");
        emit("  DevBridge.cmd mods restore-baseline");
        emit("  DevBridge.cmd test begin");
        emit("  DevBridge.cmd test session");
        emit("  DevBridge.cmd test renew <lease-id>");
        emit("  DevBridge.cmd test end <lease-id>");
        emit("  DevBridge.cmd stop <lease-id>");
        emit("  DevBridge.cmd ensure-ready <lease-id>");
        emit("  DevBridge.cmd restart [--projects none|alias[,alias...]]");
        emit("  DevBridge.cmd wait-ready");
        emit("  DevBridge.cmd doctor");
        emit("Append --json to a non-session command for one machine-readable result.");
        emit("test session is a connected streaming lease owner; keep it attached to the test owner.");
        return 0;
    }

    private static int Unknown(string command, Action<string> emit)
    {
        emit("Unknown DevBridge command: " + command);
        emit("Use: status, mods status, mods capture-baseline, mods restore-baseline, test begin, test session, test renew <lease-id>, test end <lease-id>, stop <lease-id>, ensure-ready <lease-id>, restart [--projects ...], wait-ready, doctor");
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
        emit("Next action: Keep waiting. DevBridge owns the accepted restart; reconnect with DevBridge.cmd wait-ready. Do not launch, kill, restart, or end your task because of lease contention.");
    }

    private int Status(BridgeRequest request, Action<string> emit)
    {
        PersistedState snapshot;
        ProcessStatusSnapshot processSnapshot = new();
        bool processInspectionAmbiguous = false;
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

        emit("DevBridge2 status");
        emit("Agent/session: " + request.Agent);
        emit("State: " + snapshot.Phase);
        string heldLease = snapshot.Leases.FirstOrDefault(value =>
            string.Equals(value.Agent, request.Agent, StringComparison.Ordinal))?.Id;
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
        snapshot.BaselineFingerprint = ReadBaselineFingerprintLocked() ?? snapshot.BaselineFingerprint;
        snapshot.ModsConfigOwnership = CurrentModsConfigOwnershipLocked();
        EmitProfile(snapshot, emit);
        foreach (TestLease lease in snapshot.Leases.OrderBy(value => value.StartedUtc))
            emit("  " + lease.Id + " - " + lease.Agent + " - age " + FormatAge(lease.StartedUtc) +
                " - lastHeartbeatUtc=" + FormatUtc(LeaseActivityUtc(lease)) +
                " - expiresUtc=" + FormatUtc(LeaseExpiresUtc(lease)) +
                " - retryAfterSeconds=" + RetryAfterSeconds(LeaseExpiresUtc(lease), clock.UtcNow));

        if (snapshot.RestartPending)
        {
            emit("Restart is queued and owned by DevBridge.");
            emit("Restart: pending for generation " + snapshot.TargetGeneration +
                (snapshot.RestartRequestedUtc.HasValue ? " (requested " + FormatAge(snapshot.RestartRequestedUtc.Value) + " ago)" : string.Empty));
            if (snapshot.Leases.Count > 0)
                EmitLeaseWaitDetails(snapshot, emit);
            emit("New test requests are waiting for the new generation.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            emit("Error: " + snapshot.Error);
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorCode))
            emit("Error code: " + snapshot.ErrorCode);
        if (!string.IsNullOrWhiteSpace(snapshot.ProfileError))
            emit("Profile error: " + snapshot.ProfileErrorCode + " - " + snapshot.ProfileError);
        if (!string.IsNullOrWhiteSpace(snapshot.ProfileConflict))
            emit("Profile conflict: " + snapshot.ProfileConflict);

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
        bool processInspectionRecovered = false;
        List<UnmanagedRimWorldProcess> unmanagedProcesses = new();
        lock (gate)
        {
            SynchronizeLocked();
            RevalidateMaintenanceReadyLocked();
            try
            {
                ProcessStatusSnapshot processSnapshot = EnumerateStatusProcessesLocked();
                processRunning = processSnapshot.OwnedProcessRunning;
                unmanagedProcesses = processSnapshot.UnmanagedProcesses;
                if (state.ErrorCode == ProcessInspection.ErrorCode &&
                    state.Phase == BridgePhase.ERROR && !state.RestartPending &&
                    state.Leases.Count == 0 && processSnapshot.MatchingProcessCount == 0)
                {
                    RecoverProcessInspectionQuarantineLocked();
                    processInspectionRecovered = true;
                }
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
        if (snapshot.RestartPending && snapshot.Leases.Count > 0)
            EmitLeaseWaitDetails(snapshot, emit);
        if (processInspectionAmbiguous)
            emit("WARN RimWorld process inspection is ambiguous; no process-control or launch action was taken.");
        if (processInspectionRecovered)
            emit("PASS Cleared the stale process-inspection quarantine after a complete zero-process census; no launch was attempted.");
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
        else if (snapshot.Phase == BridgePhase.STOPPED && snapshot.Generation > 0)
            EmitNextCommand(emit, "DevBridge.cmd restart");
        else
            EmitNextCommand(emit, "DevBridge.cmd wait-ready");
        if (exitCode != 0)
            emit("DevBridge.cmd restart");
        return exitCode;
    }

    private static string Check(bool passed, string text) => (passed ? "PASS " : "FAIL ") + text;

    private int BeginLease(BridgeRequest request, Action<string> emit, Func<bool> connected,
        Action<TestLease> acquired = null)
    {
        emit("Agent/session: " + request.Agent);
        bool startInitialLaunch;
        lock (gate)
        {
            SynchronizeLocked();
            if (state.Phase == BridgePhase.ERROR)
            {
                emit("RimWorld is in ERROR state: " + state.Error);
                EmitNextCommand(emit, "DevBridge.cmd doctor");
                return 4;
            }
            startInitialLaunch = state.Phase == BridgePhase.STOPPED &&
                state.Generation == 0 && !state.RestartPending;
        }

        if (startInitialLaunch)
        {
            lock (lifecycleGate)
            {
                lock (gate)
                {
                    SynchronizeLocked();
                    if (state.Phase == BridgePhase.STOPPED && state.Generation == 0 && !state.RestartPending)
                    {
                        emit("No ready RimWorld generation is running.");
                        emit("DevBridge is launching RimWorld normally, then requesting built-in Dev Quicktest.");
                        StartInitialLaunchLocked(LaunchOwnerFor(request));
                    }
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
                        StartedUtc = clock.UtcNow,
                        LastHeartbeatUtc = clock.UtcNow
                    };
                    state.Leases.Add(lease);
                    acquired?.Invoke(lease);
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
        emit("Next action: Test your mod; this lease expires two minutes after its last heartbeat. Renew it before expiresUtc; for automatic renewal, start long-running work with test session, then run:");
        emit("DevBridge.cmd test end " + lease.Id);
        if (!connected())
        {
            ReleaseLeaseSilently(lease.Id);
            return 4;
        }
        return 0;
    }

    private int SessionLease(BridgeRequest request, Action<string> emit, Func<bool> connected)
    {
        if (request.Json)
        {
            emit("Usage: DevBridge.cmd test session (streaming command; omit --json)");
            return 2;
        }

        TestLease lease = null;
        int result = BeginLease(request, emit, connected, acquired: value => lease = value);
        if (result != 0 || lease == null)
            return result;

        emit("Connected lease session is active for " + lease.Id + ".");
        emit("DevBridge will heartbeat this lease every " +
            options.LeaseHeartbeatInterval.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) +
            " seconds while this command remains connected.");
        emit("Keep this session attached to the test owner; cancellation or disconnect stops heartbeats.");
        return RunLeaseSession(request, lease, emit, connected);
    }

    private int RunLeaseSession(BridgeRequest request, TestLease lease, Action<string> emit,
        Func<bool> connected)
    {
        DateTime nextHeartbeatUtc = clock.UtcNow.Add(options.LeaseHeartbeatInterval);
        DateTime nextProgressUtc = clock.UtcNow;

        while (connected())
        {
            bool heartbeat = false;
            bool missing = false;
            string progress = null;
            DateTime now = clock.UtcNow;
            lock (gate)
            {
                PruneStaleLeasesLocked();
                TestLease current = state.Leases.FirstOrDefault(value =>
                    string.Equals(value.Id, lease.Id, StringComparison.OrdinalIgnoreCase));
                if (current == null || !string.Equals(current.Agent, request.Agent, StringComparison.Ordinal))
                {
                    missing = true;
                }
                else
                {
                    if (now >= nextHeartbeatUtc)
                    {
                        current.LastHeartbeatUtc = now;
                        SaveStateLocked();
                        Monitor.PulseAll(gate);
                        heartbeat = true;
                        nextHeartbeatUtc = now.Add(options.LeaseHeartbeatInterval);
                    }

                    if (now >= nextProgressUtc)
                    {
                        progress = "Lease session active: " + current.Id +
                            " expiresUtc=" + FormatUtc(LeaseExpiresUtc(current)) +
                            " retryAfterSeconds=" + RetryAfterSeconds(LeaseExpiresUtc(current), now);
                        nextProgressUtc = now.Add(options.LeaseProgressInterval);
                    }
                }
            }

            if (missing)
            {
                emit("Lease session ended; DevBridge will not renew " + lease.Id + ".");
                return 0;
            }

            if (heartbeat)
                emit("Test lease heartbeat: " + lease.Id);
            if (progress != null)
                emit(progress);

            if (!connected())
                break;

            now = clock.UtcNow;
            TimeSpan delay = options.LeaseSessionPollInterval;
            TimeSpan untilHeartbeat = nextHeartbeatUtc - now;
            TimeSpan untilProgress = nextProgressUtc - now;
            if (untilHeartbeat < delay)
                delay = untilHeartbeat;
            if (untilProgress < delay)
                delay = untilProgress;
            if (delay <= TimeSpan.Zero)
                continue;
            clock.Sleep(delay);
        }

        return 0;
    }

    private int RenewLease(BridgeRequest request, IReadOnlyList<string> arguments, Action<string> emit)
    {
        if (arguments.Count < 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            emit("Usage: DevBridge.cmd test renew <lease-id>");
            return 2;
        }

        string leaseId = arguments[1].Trim().ToUpperInvariant();
        lock (gate)
        {
            PruneStaleLeasesLocked();
            if (!TryGetLeaseHolderLocked(leaseId, request, out TestLease lease))
            {
                emit("Test lease renewal denied: lease " + leaseId +
                    " is not held by this agent or has expired.");
                return 4;
            }

            lease.LastHeartbeatUtc = clock.UtcNow;
            SaveStateLocked();
            Monitor.PulseAll(gate);
            emit("Test lease renewed: " + lease.Id);
            emit("Next action: Continue testing; renew the lease before expiresUtc, or keep a connected test session.");
            return 0;
        }
    }

    private int EndLease(BridgeRequest request, IReadOnlyList<string> arguments, Action<string> emit)
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

            if (!string.Equals(lease.Agent, request.Agent, StringComparison.Ordinal))
            {
                emit("Test lease release denied: lease " + leaseId +
                    " is not held by this stable agent identity.");
                return 4;
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
                state.LaunchOwner = null;
                state.LaunchRequestKey = null;
                state.WaitingForBridgeDeadlineUtc = null;
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

                string requestOwner = LaunchOwnerFor(request);
                if (!state.MaintenanceReady &&
                    (state.Phase == BridgePhase.RESTARTING || state.Phase == BridgePhase.LOADING ||
                     state.Phase == BridgePhase.DRAINING || state.RestartPending))
                {
                    if (string.Equals(state.LaunchOwner, requestOwner, StringComparison.Ordinal))
                    {
                        emit("Ensure-ready is already owned by this agent/session.");
                        EmitNextCommand(emit, "DevBridge.cmd wait-ready");
                        return 0;
                    }

                    emit("Ensure-ready denied: another owner is already launching this runtime slot.");
                    emit("No launch was attempted.");
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
                    if (!TryAcquireLaunchOwnerLocked(requestOwner, "ensure-" + targetGeneration, resetBudget: true))
                    {
                        emit("Ensure-ready denied: the runtime slot launch owner is unavailable.");
                        emit("No launch was attempted.");
                        return 4;
                    }
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
                LaunchGenerationWorker(targetGeneration, isRestart: true, owner: LaunchOwnerFor(request));
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
        RestartArguments restartArguments;
        try
        {
            restartArguments = ParseRestartArguments(request.Arguments);
        }
        catch (ProfileException exception)
        {
            RecordProfileError(exception.Code, exception.Message);
            emit("Restart request denied: " + exception.Message);
            emit("Error code: " + exception.Code);
            return 2;
        }

        int targetGeneration;
        int currentGeneration;
        bool alreadyPending;
        bool observedPending;
        bool reuseAcceptedProfile = false;
        lock (gate)
        {
            observedPending = state.RestartPending;
            string requestOwner = LaunchOwnerFor(request);
            if (observedPending && !string.Equals(state.LaunchOwner, requestOwner, StringComparison.Ordinal) &&
                (!string.IsNullOrWhiteSpace(state.LaunchOwner) ||
                 !string.Equals(state.LastLaunchOwner, requestOwner, StringComparison.Ordinal)))
            {
                emit("Restart denied: another owner already controls this runtime slot launch.");
                emit("No launch was attempted.");
                return 4;
            }
            if (!restartArguments.HasProjects && state.ProfileMode != ModProfile.LegacyMode)
            {
                string message = "an opt-in profile is already active; choose --projects with the accepted roots or run mods restore-baseline before an unprofiled restart";
                state.ProfileConflict = message;
                state.ProfileErrorCode = "PROFILE_CONFLICT";
                state.ProfileError = message;
                SaveStateLocked();
                emit("Restart denied: " + message + ".");
                emit("Error code: PROFILE_CONFLICT");
                emit("No launch was attempted.");
                return 4;
            }
            if (state.RestartPending && !ProfileRequestMatchesLocked(restartArguments, null))
            {
                string message = "a different profile is already accepted for generation " + state.TargetGeneration +
                    "; the pending restart cannot be replaced silently";
                state.ProfileConflict = message;
                state.ProfileErrorCode = "PROFILE_CONFLICT";
                state.ProfileError = message;
                SaveStateLocked();
                emit("Restart denied: " + message + ".");
                emit("Error code: PROFILE_CONFLICT");
                emit("No launch was attempted.");
                return 4;
            }
            if (state.RestartPending)
                reuseAcceptedProfile = true;

            string completedRequestKey = "restart-" + state.Generation;
            if (!state.RestartPending && state.Phase == BridgePhase.READY &&
                string.Equals(state.LastLaunchOwner, requestOwner, StringComparison.Ordinal) &&
                string.Equals(state.LastLaunchRequestKey, completedRequestKey, StringComparison.Ordinal) &&
                ProfileRequestMatchesLocked(restartArguments, null))
                reuseAcceptedProfile = true;
        }

        ModProfile requestedProfile = null;
        if (restartArguments.HasProjects && !reuseAcceptedProfile)
        {
            try
            {
                requestedProfile = ResolveRequestedProfile(restartArguments.Projects);
            }
            catch (ProfileException exception)
            {
                RecordProfileError(exception.Code, exception.Message);
                emit("Profile request denied: " + exception.Message);
                emit("Error code: " + exception.Code);
                return 4;
            }
        }

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
                string requestOwner = LaunchOwnerFor(request);
                currentGeneration = state.Generation;
                alreadyPending = state.RestartPending;
                if (alreadyPending && !string.IsNullOrWhiteSpace(state.LaunchOwner) &&
                    !string.Equals(state.LaunchOwner, requestOwner, StringComparison.Ordinal))
                {
                    emit("Restart denied: another owner already controls this runtime slot launch.");
                    emit("No launch was attempted.");
                    return 4;
                }

                if (alreadyPending && !ProfileRequestMatchesLocked(restartArguments, requestedProfile))
                {
                    string message = "a different profile is already accepted for generation " + state.TargetGeneration +
                        "; the pending restart cannot be replaced silently";
                    state.ProfileConflict = message;
                    state.ProfileErrorCode = "PROFILE_CONFLICT";
                    state.ProfileError = message;
                    SaveStateLocked();
                    emit("Restart denied: " + message + ".");
                    emit("Error code: PROFILE_CONFLICT");
                    emit("No launch was attempted.");
                    return 4;
                }

                if (!alreadyPending && requestedProfile != null)
                {
                    try
                    {
                        ModProfileResolver.ValidateResolvedProfile(requestedProfile);
                    }
                    catch (ProfileException exception)
                    {
                        RecordProfileErrorLocked(exception.Code, exception.Message);
                        emit("Profile request denied: " + exception.Message);
                        emit("Error code: " + exception.Code);
                        return 4;
                    }

                    string currentBaselineFingerprint = ReadBaselineFingerprintLocked();
                    if (!string.Equals(currentBaselineFingerprint, requestedProfile.BaselineFingerprint,
                            StringComparison.Ordinal))
                    {
                        string message = "the captured baseline changed while the profile request was resolving";
                        RecordProfileErrorLocked("PROFILE_BASELINE_CHANGED", message);
                        emit("Profile request denied: " + message + ".");
                        emit("Error code: PROFILE_BASELINE_CHANGED");
                        return 4;
                    }
                }

                if (state.MaintenanceReady)
                {
                    if (string.IsNullOrWhiteSpace(restartArguments.LeaseId) ||
                        !TryGetLeaseHolderLocked(restartArguments.LeaseId, request, out TestLease maintenanceLease))
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

                    if (!TryAcquireLaunchOwnerLocked(LaunchOwnerFor(request),
                            "restart-" + Math.Max(1, state.Generation + 1), resetBudget: true))
                    {
                        emit("Restart denied: another owner already controls this runtime slot launch.");
                        emit("No launch was attempted.");
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

                string completedRequestKey = "restart-" + state.Generation;
                if (!alreadyPending && state.Phase == BridgePhase.READY &&
                    string.Equals(state.LastLaunchOwner, LaunchOwnerFor(request), StringComparison.Ordinal) &&
                    string.Equals(state.LastLaunchRequestKey, completedRequestKey, StringComparison.Ordinal) &&
                    ProfileRequestMatchesLocked(restartArguments, requestedProfile))
                {
                    targetGeneration = state.Generation;
                    emit("Restart already completed for generation " + targetGeneration + ".");
                    return 0;
                }

                if (!alreadyPending)
                {
                    targetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
                    if (!TryAcquireLaunchOwnerLocked(LaunchOwnerFor(request), "restart-" + targetGeneration,
                            resetBudget: true))
                    {
                        emit("Restart denied: another owner already controls this runtime slot launch.");
                        emit("No launch was attempted.");
                        return 4;
                    }
                    ModProfile acceptedProfile = restartArguments.HasProjects ? requestedProfile : null;
                    ArchiveCompletedIsolationLocked();
                    SetActiveProfileLocked(acceptedProfile);
                    state.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(acceptedProfile);
                    state.LaunchProfileFingerprint = null;
                    state.LaunchProfileInstalled = false;
                    state.ProfileErrorCode = null;
                    state.ProfileError = null;
                    state.ProfileConflict = null;
                    state.TargetGeneration = targetGeneration;
                    state.RestartPending = true;
                    state.RestartRequestedUtc = clock.UtcNow;
                    state.WaitingForBridgeDeadlineUtc = null;
                    state.RequiresNewProcess = true;
                    state.Error = null;
                    state.ErrorCode = null;
                    state.Phase = BridgePhase.DRAINING;
                    DeleteReadinessLocked();
                    SaveStateLocked();
                    StartRestartWorkerLocked(targetGeneration, LaunchOwnerFor(request));
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
        lock (gate)
            EmitProfile(state, emit);
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

    private RestartArguments ParseRestartArguments(IReadOnlyList<string> arguments)
    {
        string leaseId = null;
        string projectValue = null;
        bool hasProjects = false;
        for (int index = 0; index < (arguments?.Count ?? 0); index++)
        {
            string argument = arguments[index]?.Trim() ?? string.Empty;
            if (string.Equals(argument, "--projects", StringComparison.OrdinalIgnoreCase))
            {
                if (hasProjects || index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[++index]))
                    throw new ProfileException("PROFILE_INVALID_REQUEST", "restart --projects requires one value.");
                projectValue = arguments[index].Trim();
                hasProjects = true;
                continue;
            }
            if (argument.StartsWith("--projects=", StringComparison.OrdinalIgnoreCase))
            {
                if (hasProjects)
                    throw new ProfileException("PROFILE_INVALID_REQUEST", "restart accepts only one --projects option.");
                projectValue = argument.Substring("--projects=".Length).Trim();
                if (projectValue.Length == 0)
                    throw new ProfileException("PROFILE_INVALID_REQUEST", "restart --projects requires one value.");
                hasProjects = true;
                continue;
            }
            if (argument.StartsWith("--", StringComparison.Ordinal))
                throw new ProfileException("PROFILE_INVALID_REQUEST", "Unknown restart option '" + argument + "'.");
            if (string.IsNullOrWhiteSpace(leaseId))
                leaseId = argument;
            else
                throw new ProfileException("PROFILE_INVALID_REQUEST", "restart accepts at most one lease ID.");
        }

        if (!hasProjects)
            return new RestartArguments { LeaseId = leaseId };
        if (string.Equals(projectValue, "none", StringComparison.OrdinalIgnoreCase))
            return new RestartArguments { LeaseId = leaseId, HasProjects = true };
        string[] parts = projectValue.Split(',', StringSplitOptions.None);
        if (parts.Length == 0 || parts.Any(value => string.IsNullOrWhiteSpace(value)))
            throw new ProfileException("PROFILE_INVALID_REQUEST", "restart --projects requires none or one or more aliases.");
        List<string> aliases = parts.Select(value => value.Trim()).ToList();
        return new RestartArguments { LeaseId = leaseId, HasProjects = true, Projects = aliases };
    }

    private ModProfile ResolveRequestedProfile(IReadOnlyList<string> aliases)
    {
        string baselineFingerprint;
        lock (gate)
            baselineFingerprint = ReadBaselineFingerprintLocked();
        return ModProfileResolver.Resolve(root, baselineFingerprint, aliases, options.InstalledModsRoots);
    }

    private bool ProfileRequestMatchesLocked(RestartArguments arguments, ModProfile requestedProfile)
    {
        if (arguments.HasProjects)
        {
            try
            {
                IReadOnlyList<string> canonical = ModProfileResolver.CanonicalAliases(arguments.Projects);
                if (canonical.Count == 0)
                    return state.ProfileMode == ModProfile.BaselineMode &&
                        (state.RequestedProjects?.Count ?? 0) == 0;
                return state.ProfileMode == ModProfile.ProjectsMode &&
                    (state.RequestedProjects ?? new List<string>()).SequenceEqual(canonical, StringComparer.Ordinal);
            }
            catch (ProfileException)
            {
                return false;
            }
        }
        return state.ProfileMode == ModProfile.LegacyMode;
    }

    private int WaitReady(BridgeRequest request, Action<string> emit)
    {
        emit("Agent/session: " + request.Agent);
        bool startInitialLaunch;
        lock (gate)
        {
            SynchronizeLocked();
            startInitialLaunch = state.Phase == BridgePhase.STOPPED &&
                state.Generation == 0 && !state.RestartPending;
        }

        if (startInitialLaunch)
        {
            lock (lifecycleGate)
            {
                lock (gate)
                {
                    SynchronizeLocked();
                    if (state.Phase == BridgePhase.STOPPED && state.Generation == 0 && !state.RestartPending)
                    {
                        emit("No ready RimWorld generation is running.");
                        emit("DevBridge is launching RimWorld normally, then requesting built-in Dev Quicktest.");
                        StartInitialLaunchLocked(LaunchOwnerFor(request));
                    }
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
            emit("Restart is queued and owned by DevBridge.");
            emit("No active tests remain.");
            emit("State: " + snapshot.Phase + ". Waiting for generation " + snapshot.TargetGeneration +
                " quicktest map readiness.");
            EmitKeepWaiting(emit);
            return;
        }

        emit("Restart is queued and owned by DevBridge.");
        emit("Waiting for " + snapshot.Leases.Count + " active test" + (snapshot.Leases.Count == 1 ? "" : "s") + ".");
        EmitLeaseWaitDetails(snapshot, emit);
        EmitKeepWaiting(emit);
    }

    private void EmitLeaseWaitDetails(PersistedState snapshot, Action<string> emit)
    {
        TestLease next = snapshot.Leases
            .OrderBy(value => LeaseExpiresUtc(value))
            .FirstOrDefault();
        if (next == null)
            return;

        DateTime now = clock.UtcNow;
        emit("Next blocking lease can expire at " + FormatUtc(LeaseExpiresUtc(next)) +
            " (retryAfterSeconds=" + RetryAfterSeconds(LeaseExpiresUtc(next), now) + ").");
        foreach (TestLease lease in snapshot.Leases.OrderBy(value => value.StartedUtc))
            emit("  " + lease.Id + " - " + lease.Agent +
                " - lastHeartbeatUtc=" + FormatUtc(LeaseActivityUtc(lease)) +
                " - expiresUtc=" + FormatUtc(LeaseExpiresUtc(lease)) +
                " - retryAfterSeconds=" + RetryAfterSeconds(LeaseExpiresUtc(lease), now));
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

    private string LaunchOwnerFor(BridgeRequest request)
    {
        string agent = string.IsNullOrWhiteSpace(request?.Agent) ? "unknown-agent" : request.Agent.Trim();
        return agent + "@" + (request?.ClientProcessId ?? 0).ToString();
    }

    private bool TryAcquireLaunchOwnerLocked(string owner, string requestKey, bool resetBudget)
    {
        bool pending = state.RestartPending || state.Phase == BridgePhase.RESTARTING ||
            state.Phase == BridgePhase.LOADING || state.Phase == BridgePhase.DRAINING;
        if (pending && !string.IsNullOrWhiteSpace(state.LaunchOwner))
        {
            if (string.Equals(state.LaunchOwner, owner, StringComparison.Ordinal) &&
                string.Equals(state.LaunchRequestKey, requestKey, StringComparison.Ordinal))
                return true;
            return false;
        }

        if (resetBudget)
        {
            state.LaunchAttemptCount = 0;
            state.LaunchBudgetRemaining = Math.Max(1, options.MaxLaunchAttempts);
        }

        if (state.LaunchBudgetRemaining <= 0)
        {
            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
            state.ErrorCode = "LAUNCH_BUDGET_EXHAUSTED";
            state.Error = "The finite RimWorld launch budget is exhausted; no further launch was attempted.";
            state.LaunchOwner = null;
            state.LaunchRequestKey = null;
            state.WaitingForBridgeDeadlineUtc = null;
            SaveStateLocked();
            Monitor.PulseAll(gate);
            return false;
        }

        state.LaunchOwner = owner;
        state.LaunchRequestKey = requestKey;
        SaveStateLocked();
        return true;
    }

    private void StartInitialLaunchLocked(string owner = null)
    {
        if (launchTask != null && !launchTask.IsCompleted)
            return;

        owner ??= "coordinator@" + runtimeSlotId;
        if (!TryAcquireLaunchOwnerLocked(owner, "initial", resetBudget: true))
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
        state.LaunchProfileFingerprint = null;
        state.LaunchProfileInstalled = false;
        state.LaunchAttemptStarted = false;
        state.RequiresNewProcess = true;
        state.WaitingForBridgeDeadlineUtc = null;
        DeleteReadinessLocked();
        SaveStateLocked();
        launchTask = Task.Run(() =>
        {
            lock (lifecycleGate)
                LaunchGenerationWorker(target, isRestart: false, owner: owner);
        });
    }

    private void StartRestartWorkerLocked(int targetGeneration, string owner = null)
    {
        if (restartTask != null && !restartTask.IsCompleted)
            return;

        owner ??= state.LaunchOwner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            FailLaunch("the accepted restart has no durable launch owner; no launch was attempted",
                "LAUNCH_OWNER_MISSING");
            return;
        }
        if (state.LaunchBudgetRemaining <= 0)
        {
            FailLaunch("the finite launch budget is exhausted", "LAUNCH_BUDGET_EXHAUSTED");
            return;
        }
        if (!string.Equals(state.LaunchOwner, owner, StringComparison.Ordinal))
            return;

        restartTask = Task.Run(() => RestartWorker(targetGeneration, owner));
    }

    private void StartMonitorLaunchLocked(int targetGeneration)
    {
        if (launchTask != null && !launchTask.IsCompleted)
            return;

        launchTask = Task.Run(() => MonitorLaunchWorker(targetGeneration));
    }

    private void RestartWorker(int targetGeneration, string owner)
    {
        try
        {
            int oldProcessId;
            long oldStartTicks;
            // Process-control operations remain serialized. The gate is intentionally
            // not taken by status, doctor, wait-ready, or lease cleanup, so those
            // commands remain responsive while this worker waits on a lease.
            lock (lifecycleGate)
            {
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

                        bool ownedProcessRunning = state.ProcessId > 0 &&
                            IsOwnedProcess(state.ProcessId, state.ProcessStartUtcTicks);
                        if (state.Leases.Count > 0 && ownedProcessRunning)
                        {
                            if (state.Phase != BridgePhase.WAITING_FOR_BRIDGE)
                            {
                                state.Phase = BridgePhase.WAITING_FOR_BRIDGE;
                                SaveStateLocked();
                            }
                            Monitor.Wait(gate, 1000);
                            continue;
                        }

                        if (state.Phase == BridgePhase.WAITING_FOR_BRIDGE)
                        {
                            state.Phase = BridgePhase.DRAINING;
                            SaveStateLocked();
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

                lock (gate)
                {
                    if (!state.RestartPending || state.TargetGeneration != targetGeneration)
                        return;
                }

                (bool stopped, string stopErrorCode, string stopError) = StopOwnedProcess(oldProcessId, oldStartTicks);
                if (!stopped)
                {
                    FailLaunch(stopError, stopErrorCode);
                    return;
                }
                LaunchGenerationWorker(targetGeneration, isRestart: true, owner: owner);
            }
        }
        catch (Exception exception)
        {
            FailLaunch(exception is ProcessInspectionException ? ProcessInspection.Message :
                "restart coordinator failure: " + exception.Message,
                exception is ProcessInspectionException ? ProcessInspection.ErrorCode : "LAUNCH_FAILED");
        }
    }

    private void LaunchGenerationWorker(int targetGeneration, bool isRestart, string owner = null,
        ModProfile isolationProfile = null, string isolationAttemptId = null)
    {
        string launchId = Guid.NewGuid().ToString("N");
        IManagedProcess process = null;
        bool isolationAttempt = !string.IsNullOrWhiteSpace(isolationAttemptId);
        try
        {
            owner ??= "coordinator@" + runtimeSlotId;
            lock (gate)
            {
                if (!string.Equals(state.LaunchOwner, owner, StringComparison.Ordinal))
                    return;
                if (isolationAttempt)
                {
                    CrashIsolationIncident incident = state.CrashIsolation;
                    if (incident == null ||
                        !string.Equals(incident.CurrentAttemptId, isolationAttemptId,
                            StringComparison.Ordinal) ||
                        !string.Equals(state.LaunchRequestKey, isolationAttemptId, StringComparison.Ordinal) ||
                        isolationProfile == null ||
                        !string.Equals(incident.CurrentAttemptFingerprint, isolationProfile.ProfileFingerprint,
                            StringComparison.Ordinal) ||
                        incident.CurrentAttemptProfile == null ||
                        !string.Equals(incident.CurrentAttemptProfile.ProfileFingerprint,
                            isolationProfile.ProfileFingerprint, StringComparison.Ordinal) ||
                        !string.Equals(state.LaunchProfileFingerprint,
                            isolationProfile.ProfileFingerprint, StringComparison.Ordinal) ||
                        incident.CurrentAttemptResult != null ||
                        state.LaunchAttemptStarted || state.Phase == BridgePhase.LOADING ||
                        state.IsolationLaunchesRemaining <= 0 ||
                        incident.IsolationLaunchesRemaining <= 0)
                    {
                        return;
                    }
                    int remaining = Math.Min(state.IsolationLaunchesRemaining,
                        incident.IsolationLaunchesRemaining);
                    if (remaining <= 0)
                        return;
                    state.IsolationLaunchesRemaining = remaining - 1;
                    incident.IsolationLaunchesRemaining = remaining - 1;
                }
                else if (state.LaunchBudgetRemaining <= 0)
                {
                    FailLaunch("the finite launch budget is exhausted", "LAUNCH_BUDGET_EXHAUSTED");
                    return;
                }
                state.Phase = BridgePhase.LOADING;
                state.TargetGeneration = targetGeneration;
                state.LaunchId = launchId;
                state.LaunchGeneration = targetGeneration;
                state.LaunchStartedUtc = clock.UtcNow;
                // A failed raw launch must not inherit the previous generation's
                // identity.  Recovery may only attribute a process identity that
                // was durably recorded for this launch intent.
                state.ProcessId = 0;
                state.ProcessStartUtcTicks = 0;
                state.Error = null;
                state.ErrorCode = null;
                state.MaintenanceReady = false;
                state.LaunchProfileInstalled = false;
                state.LaunchAttemptStarted = false;
                state.LaunchProfileFingerprint = isolationProfile?.ProfileFingerprint ??
                    (state.ProfileMode == ModProfile.LegacyMode ? null : state.ProfileFingerprint);
                DeleteReadinessLocked();
                SaveStateLocked();
            }

            if (!File.Exists(rimWorldExe))
                throw new FileNotFoundException("RimWorld executable was not found", rimWorldExe);

            lock (gate)
            {
                // Check the census before changing ModsConfig. lifecycleGate serializes this
                // launch with coordinator-owned lifecycle operations, so an unmanaged process
                // fails closed without leaving a generated profile behind.
                List<UnmanagedRimWorldProcess> unmanagedProcesses =
                    FindUnmanagedRimWorldProcesses(processIdToExclude: 0, startTicksToExclude: 0);
                if (unmanagedProcesses.Count > 0)
                    throw new InvalidOperationException("an unmanaged RimWorld process is already running (PID " +
                        string.Join(", ", unmanagedProcesses.Select(value => value.ProcessId.ToString())) +
                        "); close it through Steam before retrying");
            }

            ModProfile profile;
            lock (gate)
            {
                // Legacy launches use the user's existing ModsConfig.  A
                // baseline/runtime snapshot is not an implicit legacy profile.
                if (isolationProfile != null)
                    profile = isolationProfile;
                else if (state.ProfileMode == ModProfile.LegacyMode)
                    profile = null;
                else
                {
                    // The accepted profile is authoritative. RuntimeProfile is
                    // a reporting/recovery snapshot and may be stale after a
                    // coordinator crash; never launch a different profile.
                    ModProfile acceptedProfile = ProfileFromStateLocked();
                    ModProfile runtimeProfile = state.RuntimeProfile?.ToModProfile();
                    profile = runtimeProfile != null && acceptedProfile != null &&
                        string.Equals(runtimeProfile.ProfileFingerprint,
                            acceptedProfile.ProfileFingerprint, StringComparison.Ordinal)
                        ? runtimeProfile
                        : acceptedProfile;
                }
            }
            if (profile == null)
            {
                EnsureDevBridgeModEnabled();
                lock (gate)
                {
                    // Baseline capture/restore intentionally keeps a control
                    // snapshot for isolation, but it must not be reported as
                    // the profile used by a subsequent ordinary legacy launch.
                    if (!isolationAttempt && state.ProfileMode == ModProfile.LegacyMode &&
                        state.RuntimeProfile != null)
                    {
                        state.RuntimeProfile = null;
                        SaveStateLocked();
                    }
                }
            }
            else
            {
                ApplyProfile(profile, targetGeneration);
                lock (gate)
                {
                    state.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(profile);
                    state.LaunchProfileInstalled = true;
                    SaveStateLocked();
                }
            }

            // A process may have appeared after the pre-write census. Check again at the
            // launch boundary so an external RimWorld start cannot become a duplicate launch.
            EnsureNoMatchingRimWorldProcess();
            lock (gate)
            {
                // Profile application is complete immediately before the only raw launch call.
                if (!isolationAttempt)
                {
                    state.LaunchAttemptCount++;
                    state.LaunchBudgetRemaining--;
                }
                state.LaunchAttemptStarted = true;
                SaveStateLocked();
            }

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
            string detail = DescribeLaunchFailure(exception, process);
            FailLaunch(detail, LaunchFailureCode(exception, process));
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
            {
                FailLaunch("RimWorld exited before the quicktest map became ready", "PROCESS_EXITED");
                return;
            }
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

    private static string LaunchFailureCode(Exception exception, IManagedProcess process)
    {
        if (exception is TimeoutException)
            return "READINESS_TIMEOUT";
        if (exception is ProcessInspectionException)
            return ProcessInspection.ErrorCode;
        if (exception is ProfileException profileException)
            return profileException.Code;
        try
        {
            if (process != null && process.HasExited)
                return "PROCESS_EXITED";
        }
        catch
        {
            return ProcessInspection.ErrorCode;
        }
        return "LAUNCH_FAILED";
    }

    private static bool IsTerminalIsolationStatus(string status) =>
        string.Equals(status, "COMPLETED", StringComparison.Ordinal) ||
        string.Equals(status, "ENVIRONMENTAL_FAILURE", StringComparison.Ordinal) ||
        string.Equals(status, "INCONCLUSIVE", StringComparison.Ordinal);

    private bool IsolationActiveLocked() => state.CrashIsolation != null &&
        !IsTerminalIsolationStatus(state.CrashIsolation.Status);

    private bool IsolationLaunchStateMatchesLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null || string.IsNullOrWhiteSpace(incident.CurrentAttemptId) ||
            !string.Equals(state.ProfileFingerprint, incident.OriginalProfileFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(state.BaselineFingerprint, incident.OriginalBaselineFingerprint,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(state.LaunchId) ||
            state.LaunchGeneration <= 0 || state.LaunchGeneration != state.TargetGeneration ||
            !state.RestartPending ||
            !string.Equals(state.LaunchOwner, "isolation@" + runtimeSlotId, StringComparison.Ordinal) ||
            !string.Equals(state.LaunchRequestKey, incident.CurrentAttemptId, StringComparison.Ordinal) ||
            !string.Equals(state.LaunchProfileFingerprint, incident.CurrentAttemptFingerprint,
                StringComparison.Ordinal) || incident.CurrentAttemptProfile == null ||
            !string.Equals(incident.CurrentAttemptProfile.ProfileFingerprint,
                incident.CurrentAttemptFingerprint, StringComparison.Ordinal) ||
            !state.LaunchProfileInstalled || !state.LaunchAttemptStarted)
            return false;
        try
        {
            ModProfileResolver.ValidateResolvedProfile(incident.CurrentAttemptProfile.ToModProfile());
            return true;
        }
        catch (ProfileException)
        {
            return false;
        }
    }

    private void ResumePersistedIsolationResultLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null)
            return;
        if (string.Equals(incident.CurrentAttemptResult, "UNSAFE", StringComparison.Ordinal))
        {
            FinalizeIsolationEnvironmentalLocked(
                incident.CurrentAttemptFailureCode ?? "ISOLATION_UNSAFE_RESULT",
                incident.CurrentAttemptFailureDetail ??
                "the persisted isolation attempt did not produce safe profile-failure evidence");
        }
        else if (IsolationLaunchStateMatchesLocked())
            StartIsolationWorkerLocked();
        else
            FinalizeIsolationEnvironmentalLocked("ISOLATION_PROFILE_MISMATCH",
                "the persisted terminal isolation attempt does not match its durable launch intent; no replacement launch was attempted");
    }

    private void ArchiveCompletedIsolationLocked()
    {
        if (state.CrashIsolation == null || IsolationActiveLocked())
            return;
        state.CrashIsolationHistory ??= new List<CrashIsolationIncident>();
        state.CrashIsolationHistory.Add(state.CrashIsolation);
        while (state.CrashIsolationHistory.Count > 8)
            state.CrashIsolationHistory.RemoveAt(0);
        state.CrashIsolation = null;
    }

    private bool IsEligibleForCrashIsolationLocked(string errorCode)
    {
        if (!IsIsolationEvidenceFailure(errorCode))
            return false;
        if (state.ProfileMode != ModProfile.ProjectsMode ||
            string.IsNullOrWhiteSpace(state.ProfileFingerprint) ||
            !state.LaunchProfileInstalled || !state.LaunchAttemptStarted ||
            !string.Equals(state.LaunchProfileFingerprint, state.ProfileFingerprint,
                StringComparison.Ordinal) ||
            state.ProcessId <= 0 || state.ProcessStartUtcTicks <= 0 ||
            state.CrashIsolation != null || state.Leases.Count != 0 ||
            state.MaintenanceReady || state.SessionDirty)
            return false;

        if (errorCode == ProcessInspection.ErrorCode ||
            errorCode == "PROCESS_IDENTITY_CHANGED" ||
            errorCode == "LAUNCH_RECOVERY_AMBIGUOUS" ||
            errorCode == "ISOLATION_RECOVERY_AMBIGUOUS" ||
            errorCode == "LAUNCH_OWNER_MISSING" ||
            errorCode == "LAUNCH_BUDGET_EXHAUSTED" ||
            errorCode == "MAINTENANCE_PROCESS_PRESENT" ||
            errorCode == "PROFILE_CONFLICT" ||
            errorCode == "PROFILE_RESTART_PENDING" ||
            errorCode.StartsWith("PROFILE_", StringComparison.Ordinal) ||
            errorCode.StartsWith("MODS_CONFIG_", StringComparison.Ordinal))
            return false;

        // A failed ownership/lease/maintenance check is not evidence about the
        // managed profile, even when a previous write happened in this generation.
        if (errorCode.Contains("LEASE", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("MAINTENANCE", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("OWNERSHIP", StringComparison.OrdinalIgnoreCase) ||
            errorCode.Contains("PROCESS_INSPECTION", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool IsIsolationEvidenceFailure(string errorCode) =>
        string.Equals(errorCode, "PROCESS_EXITED", StringComparison.Ordinal) ||
        string.Equals(errorCode, "READINESS_TIMEOUT", StringComparison.Ordinal);

    private void BeginCrashIsolationLocked(string detail, string errorCode)
    {
        ModProfile accepted = ProfileFromStateLocked();
        if (accepted == null)
            return;

        List<string> projects = (accepted.ResolvedProjectPackageIds ?? new List<string>())
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();
        CrashIsolationIncident incident = new()
        {
            IncidentId = DeterministicIsolationId("incident", accepted.ProfileFingerprint),
            Status = "RUNNING",
            Stage = "CONTROL",
            OriginalProfileMode = accepted.Mode,
            OriginalRequestedProjects = (accepted.RequestedProjects ?? new List<string>()).ToList(),
            OriginalResolvedProjectPackageIds = (accepted.ResolvedProjectPackageIds ?? new List<string>()).ToList(),
            OriginalResolvedMods = (accepted.ResolvedMods ?? new List<string>()).ToList(),
            OriginalProfileFingerprint = accepted.ProfileFingerprint,
            OriginalBaselineFingerprint = accepted.BaselineFingerprint,
            OriginalLastKnownGoodFingerprint = state.LastKnownGoodProfile?.ProfileFingerprint ??
                accepted.BaselineFingerprint,
            OriginalGeneration = state.LaunchGeneration > 0 ? state.LaunchGeneration : state.Generation,
            OriginalLaunchId = state.LaunchId,
            OriginalProcessId = state.ProcessId,
            OriginalProcessStartUtcTicks = state.ProcessStartUtcTicks,
            OriginalFailureUtc = clock.UtcNow,
            OriginalFailurePhase = state.Phase.ToString(),
            OriginalFailureCode = errorCode,
            OriginalFailureDetail = detail,
            OriginalProcessExitObserved = errorCode == "PROCESS_EXITED",
            OriginalExitInformation = detail,
            SearchPoolProjects = projects,
            DeltaCurrentProjects = projects.ToList(),
            DeltaGranularity = Math.Min(2, Math.Max(1, projects.Count)),
            IsolationLaunchesRemaining = Math.Max(1, options.IsolationMaxAttempts)
        };
        incident.OriginalDiagnosticMetadata["acceptedAtUtc"] =
            state.RestartRequestedUtc?.ToUniversalTime().ToString("O") ?? string.Empty;
        incident.OriginalDiagnosticMetadata["modsConfigGeneratedHash"] =
            state.ModsConfigGeneratedHash ?? string.Empty;
        incident.OriginalDiagnosticMetadata["modsConfigGeneratedGeneration"] =
            state.ModsConfigGeneratedGeneration.ToString(CultureInfo.InvariantCulture);

        state.CrashIsolation = incident;
        state.Phase = BridgePhase.ISOLATING;
        state.RestartPending = true;
        state.TargetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
        state.LaunchOwner = "isolation@" + runtimeSlotId;
        state.LaunchRequestKey = "isolation-control-" + incident.IncidentId;
        state.IsolationLaunchesRemaining = incident.IsolationLaunchesRemaining;
        state.WaitingForBridgeDeadlineUtc = null;
        state.ErrorCode = "CRASH_ISOLATION_RUNNING";
        state.Error = "The accepted project profile failed during startup; deterministic crash isolation is running.";
        state.ProfileErrorCode = null;
        state.ProfileError = null;
        SaveStateLocked();
        StartIsolationWorkerLocked();
    }

    private void StartIsolationWorkerLocked()
    {
        if (!IsolationActiveLocked() || (isolationTask != null && !isolationTask.IsCompleted))
            return;
        isolationTask = Task.Run(IsolationWorker);
    }

    private void QueueIsolationContinuationLocked()
    {
        if (IsolationActiveLocked() && (isolationTask == null || isolationTask.IsCompleted))
            StartIsolationWorkerLocked();
    }

    private static string DeterministicIsolationId(string kind, string fingerprint)
    {
        string input = (kind ?? string.Empty) + "\n" + (fingerprint ?? string.Empty);
        using SHA256 sha = SHA256.Create();
        return "iso-" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static List<string> StableProjectOrder(IEnumerable<string> projects) =>
        (projects ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();

    private ModProfile BuildIsolationProfileLocked(IReadOnlyList<string> projectPackageIds)
    {
        List<string> aliases = new();
        for (int index = 0; index < state.CrashIsolation.OriginalResolvedProjectPackageIds.Count; index++)
        {
            string packageId = state.CrashIsolation.OriginalResolvedProjectPackageIds[index];
            if (projectPackageIds.Any(value => string.Equals(value, packageId, StringComparison.OrdinalIgnoreCase)))
                aliases.Add(state.CrashIsolation.OriginalRequestedProjects[index]);
        }
        return ModProfileResolver.Resolve(coordinatorRoot, state.CrashIsolation.OriginalBaselineFingerprint,
            aliases, options.InstalledModsRoots);
    }

    private CrashIsolationAttempt FindIsolationAttemptLocked(string attemptId)
    {
        return state.CrashIsolation?.Attempts?.FirstOrDefault(value =>
            string.Equals(value.AttemptId, attemptId, StringComparison.Ordinal));
    }

    private void SetCurrentIsolationAttemptLocked(string kind, ModProfile profile,
        IReadOnlyList<string> projects)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        string attemptId = kind.StartsWith("MINIMIZE_", StringComparison.Ordinal)
            ? DeterministicIsolationId("candidate", profile.ProfileFingerprint)
            : DeterministicIsolationId(kind, profile.ProfileFingerprint);
        CrashIsolationAttempt previous = FindIsolationAttemptLocked(attemptId);
        incident.CurrentAttemptId = attemptId;
        incident.CurrentAttemptKind = kind;
        incident.CurrentAttemptFingerprint = profile.ProfileFingerprint;
        incident.CurrentAttemptProfile = PersistedProfileSnapshot.FromModProfile(profile);
        incident.CurrentAttemptProjects = StableProjectOrder(projects);
        incident.CurrentAttemptProfileInstalled = false;
        incident.CurrentAttemptFailurePhase = null;
        incident.CurrentAttemptFailureCode = null;
        incident.CurrentAttemptFailureDetail = null;
        incident.CurrentAttemptResult = previous?.Result;
        if (previous != null)
        {
            incident.CurrentAttemptFailurePhase = previous.FailurePhase;
            incident.CurrentAttemptFailureCode = previous.FailureCode;
            incident.CurrentAttemptFailureDetail = previous.FailureDetail;
            incident.CurrentAttemptProfileInstalled = previous.ProfileInstalled;
        }
        state.TargetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
        state.LaunchOwner = "isolation@" + runtimeSlotId;
        state.LaunchRequestKey = attemptId;
        state.RestartPending = true;
        state.Phase = BridgePhase.ISOLATING;
        SaveStateLocked();
    }

    private List<CrashIsolationSelection> PartitionCandidatesLocked(List<string> current,
        int granularity, bool complements)
    {
        current = StableProjectOrder(current);
        int count = Math.Min(Math.Max(2, granularity), current.Count);
        List<CrashIsolationSelection> result = new();
        for (int part = 0; part < count; part++)
        {
            List<string> selected = current.Where((_, index) => index % count == part).ToList();
            List<string> candidate = complements
                ? current.Where(value => !selected.Contains(value, StringComparer.OrdinalIgnoreCase)).ToList()
                : selected;
            if (candidate.Count == 0 || candidate.Count == current.Count)
                continue;
            result.Add(new CrashIsolationSelection { Projects = StableProjectOrder(candidate) });
        }
        return result;
    }

    private void StartIsolationRoundLocked(bool complements, bool persist = true)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        List<string> current = StableProjectOrder(incident.DeltaCurrentProjects);
        if (current.Count <= 1)
        {
            CompleteMinimalSetLocked(current, persist);
            return;
        }
        int n = Math.Min(current.Count, Math.Max(2, incident.DeltaGranularity));
        incident.PendingKind = complements ? "REMOVE" : "DIRECT";
        incident.PendingCandidates = PartitionCandidatesLocked(current, n, complements);
        incident.PendingCandidateIndex = 0;
        if (incident.PendingCandidates.Count == 0)
            CompleteMinimalSetLocked(current, persist);
        else if (persist)
            SaveStateLocked();
    }

    private void CompleteMinimalSetLocked(IReadOnlyList<string> projects, bool persist = true)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        List<string> minimal = StableProjectOrder(projects);
        ModProfile minimalProfile = BuildIsolationProfileLocked(minimal);
        CrashIsolationDiagnosis diagnosis = new()
        {
            Code = minimal.Count == 1 ? "PROJECT_OR_REQUIRED_DEPENDENCY_CLOSURE" :
                "MINIMAL_INCOMPATIBLE_PROJECT_SET",
            Message = minimal.Count == 1
                ? "Project " + minimal[0] + " or its required dependency closure causes the startup failure."
                : "The minimal incompatible project set is: " + string.Join(", ", minimal) + ".",
            ResolvedProjectPackageIds = minimal.ToList(),
            RequestedProjects = RequestedAliasesForPackagesLocked(minimal),
            ProfileFingerprint = minimalProfile.ProfileFingerprint
        };
        incident.Diagnoses.Add(diagnosis);
        if (incident.Diagnoses.Count == 1)
        {
            incident.DiagnosisCode = diagnosis.Code;
            incident.Diagnosis = diagnosis.Message;
        }
        else
        {
            incident.DiagnosisCode = "MULTIPLE_INDEPENDENT_FAILING_PROJECT_SETS";
            incident.Diagnosis = "Multiple independent failing project sets were isolated: " +
                string.Join("; ", incident.Diagnoses.Select(value =>
                    "[" + string.Join(", ", value.ResolvedProjectPackageIds) + "]")) + ".";
        }
        incident.SearchPoolProjects = incident.SearchPoolProjects
            .Where(value => !minimal.Contains(value, StringComparer.OrdinalIgnoreCase)).ToList();
        incident.DeltaCurrentProjects = incident.SearchPoolProjects.ToList();
        incident.PendingCandidates = new List<CrashIsolationSelection>();
        incident.PendingCandidateIndex = 0;
        incident.PendingKind = null;
        if (incident.SearchPoolProjects.Count == 0)
            incident.Stage = "FINAL_CONTROL";
        else
        {
            incident.Stage = "VERIFY_REMAINDER";
            incident.DeltaGranularity = Math.Min(2, incident.SearchPoolProjects.Count);
        }
        if (persist)
            SaveStateLocked();
    }

    private List<string> RequestedAliasesForPackagesLocked(IEnumerable<string> packages)
    {
        HashSet<string> wanted = new(packages ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        List<string> aliases = new();
        for (int index = 0; index < state.CrashIsolation.OriginalResolvedProjectPackageIds.Count; index++)
        {
            if (wanted.Contains(state.CrashIsolation.OriginalResolvedProjectPackageIds[index]))
                aliases.Add(state.CrashIsolation.OriginalRequestedProjects[index]);
        }
        return aliases.OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    private bool TryPlanIsolationAttemptLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null || incident.CurrentAttemptId != null || IsTerminalIsolationStatus(incident.Status))
            return false;

        if (incident.Stage != "CONTROL" && incident.Stage != "REPRODUCE" &&
            incident.Stage != "VERIFY_REMAINDER" && incident.Stage != "MINIMIZE" &&
            incident.Stage != "FINAL_CONTROL" && incident.Stage != "FINAL_BASELINE_CONTROL")
        {
            FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_STATE_INVALID",
                "the durable isolation incident contains an unknown search phase; no project was attributed");
            return false;
        }
        if (incident.Stage == "MINIMIZE" && incident.PendingKind != null &&
            incident.PendingKind != "DIRECT" && incident.PendingKind != "REMOVE")
        {
            FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_STATE_INVALID",
                "the durable isolation incident contains an unknown candidate partition kind; no project was attributed");
            return false;
        }

        while (true)
        {
            ModProfile profile;
            string kind;
            List<string> projects;
            if (incident.Stage == "CONTROL")
            {
                profile = state.LastKnownGoodProfile?.ToModProfile() ??
                    ModProfileResolver.CreateBaselineProfile(incident.OriginalBaselineFingerprint);
                kind = "CONTROL";
                projects = Array.Empty<string>().ToList();
            }
            else if (incident.Stage == "REPRODUCE")
            {
                profile = new PersistedProfileSnapshot
                {
                    Mode = incident.OriginalProfileMode,
                    RequestedProjects = incident.OriginalRequestedProjects.ToList(),
                    ResolvedProjectPackageIds = incident.OriginalResolvedProjectPackageIds.ToList(),
                    ResolvedMods = incident.OriginalResolvedMods.ToList(),
                    ProfileFingerprint = incident.OriginalProfileFingerprint,
                    BaselineFingerprint = incident.OriginalBaselineFingerprint
                }.ToModProfile();
                kind = "REPRODUCE";
                projects = incident.OriginalResolvedProjectPackageIds.ToList();
            }
            else if (incident.Stage == "VERIFY_REMAINDER")
            {
                profile = BuildIsolationProfileLocked(incident.DeltaCurrentProjects);
                kind = "VERIFY_REMAINDER";
                projects = incident.DeltaCurrentProjects.ToList();
            }
            else if (incident.Stage == "FINAL_CONTROL")
            {
                profile = incident.SafeRemainderProfile?.ToModProfile() ??
                    state.LastKnownGoodProfile?.ToModProfile() ??
                    ModProfileResolver.CreateBaselineProfile(incident.OriginalBaselineFingerprint);
                kind = "FINAL_CONTROL";
                projects = incident.SafeRemainderProfile?.ResolvedProjectPackageIds?.ToList() ??
                    Array.Empty<string>().ToList();
            }
            else if (incident.Stage == "FINAL_BASELINE_CONTROL")
            {
                profile = ModProfileResolver.CreateBaselineProfile(incident.OriginalBaselineFingerprint);
                kind = "FINAL_BASELINE_CONTROL";
                projects = Array.Empty<string>().ToList();
            }
            else if (incident.Stage == "MINIMIZE")
            {
                if (incident.PendingCandidates == null ||
                    incident.PendingCandidateIndex >= incident.PendingCandidates.Count)
                {
                    StartIsolationRoundLocked(incident.PendingKind != "DIRECT");
                    if (incident.Stage != "MINIMIZE" || incident.CurrentAttemptId != null)
                        return false;
                    continue;
                }
                projects = StableProjectOrder(incident.PendingCandidates[incident.PendingCandidateIndex].Projects);
                profile = BuildIsolationProfileLocked(projects);
                kind = "MINIMIZE_" + incident.PendingKind;
            }
            else
                return false;

            ModProfileResolver.ValidateResolvedProfile(profile);
            SetCurrentIsolationAttemptLocked(kind, profile, projects);
            return true;
        }
    }

    private bool StopIsolationProcess(out string errorCode, out string error)
    {
        int processId;
        long startTicks;
        lock (gate)
        {
            processId = state.ProcessId;
            startTicks = state.ProcessStartUtcTicks;
        }

        (bool stopped, string stopCode, string stopError) = StopOwnedProcess(processId, startTicks);
        if (!stopped)
        {
            errorCode = stopCode;
            error = stopError;
            return false;
        }
        try
        {
            if (FindUnmanagedRimWorldProcesses(0, 0).Count != 0)
            {
                errorCode = "MAINTENANCE_PROCESS_PRESENT";
                error = "a RimWorld process remained after the isolated attempt was stopped";
                return false;
            }
        }
        catch (ProcessInspectionException)
        {
            errorCode = ProcessInspection.ErrorCode;
            error = ProcessInspection.Message;
            return false;
        }
        errorCode = null;
        error = null;
        return true;
    }

    private bool PrepareIsolationAttempt(ModProfile profile, string attemptId, int targetGeneration,
        out string errorCode, out string error)
    {
        if (!StopIsolationProcess(out errorCode, out error))
            return false;

        lock (gate)
        {
            CrashIsolationIncident incident = state.CrashIsolation;
            if (incident == null || !string.Equals(incident.CurrentAttemptId, attemptId, StringComparison.Ordinal))
            {
                errorCode = "CRASH_ISOLATION_STATE_CHANGED";
                error = "the durable isolation attempt changed before launch";
                return false;
            }
            state.TargetGeneration = Math.Max(state.Generation + 1, targetGeneration);
            state.Phase = BridgePhase.RESTARTING;
            state.RestartPending = true;
            state.LaunchOwner = "isolation@" + runtimeSlotId;
            state.LaunchRequestKey = attemptId;
            state.LaunchId = null;
            state.ProcessId = 0;
            state.ProcessStartUtcTicks = 0;
            state.LaunchProfileFingerprint = profile.ProfileFingerprint;
            state.LaunchProfileInstalled = false;
            state.LaunchAttemptStarted = false;
            state.RequiresNewProcess = true;
            state.Error = null;
            state.ErrorCode = null;
            DeleteReadinessLocked();
            SaveStateLocked();
        }
        errorCode = null;
        error = null;
        return true;
    }

    private void StoreCurrentIsolationAttemptLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        CrashIsolationAttempt attempt = FindIsolationAttemptLocked(incident.CurrentAttemptId);
        if (attempt == null)
        {
            attempt = new CrashIsolationAttempt { AttemptId = incident.CurrentAttemptId };
            incident.Attempts.Add(attempt);
        }
        attempt.Kind = incident.CurrentAttemptKind;
        attempt.ProfileFingerprint = incident.CurrentAttemptFingerprint;
        attempt.RequestedProjects = incident.CurrentAttemptProfile?.RequestedProjects?.ToList() ?? new List<string>();
        attempt.ResolvedProjectPackageIds = incident.CurrentAttemptProfile?.ResolvedProjectPackageIds?.ToList() ?? new List<string>();
        attempt.Result = incident.CurrentAttemptResult;
        attempt.Generation = state.LaunchGeneration;
        attempt.ProcessId = state.ProcessId;
        attempt.ProcessStartUtcTicks = state.ProcessStartUtcTicks;
        attempt.CompletedUtc = clock.UtcNow;
        attempt.ProfileInstalled = incident.CurrentAttemptProfileInstalled;
        attempt.ProcessExitObserved = incident.OriginalProcessExitObserved ||
            incident.CurrentAttemptFailureCode == "PROCESS_EXITED";
        attempt.FailurePhase = incident.CurrentAttemptFailurePhase;
        attempt.FailureCode = incident.CurrentAttemptFailureCode;
        attempt.FailureDetail = incident.CurrentAttemptFailureDetail;
        if (attempt.StartedUtc == default)
            attempt.StartedUtc = attempt.CompletedUtc;
    }

    private void ClearCurrentIsolationAttemptLocked(bool persist = true)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        incident.CurrentAttemptId = null;
        incident.CurrentAttemptFingerprint = null;
        incident.CurrentAttemptKind = null;
        incident.CurrentAttemptProfile = null;
        incident.CurrentAttemptProjects = new List<string>();
        incident.CurrentAttemptResult = null;
        incident.CurrentAttemptFailurePhase = null;
        incident.CurrentAttemptFailureCode = null;
        incident.CurrentAttemptFailureDetail = null;
        incident.CurrentAttemptProfileInstalled = false;
        state.ProcessId = 0;
        state.ProcessStartUtcTicks = 0;
        state.LaunchId = null;
        state.LaunchProfileFingerprint = null;
        state.LaunchProfileInstalled = false;
        state.LaunchAttemptStarted = false;
        state.Phase = BridgePhase.ISOLATING;
        if (persist)
            SaveStateLocked();
    }

    private void IsolationWorker()
    {
        try
        {
            lock (lifecycleGate)
            {
                while (true)
                {
                    ModProfile profile = null;
                    string attemptId = null;
                    string kind = null;
                    int targetGeneration = 0;
                    bool consume = false;
                    bool recoveryAmbiguous = false;

                    lock (gate)
                    {
                        if (!IsolationActiveLocked())
                            return;

                        CrashIsolationIncident incident = state.CrashIsolation;
                        if (incident.CurrentAttemptId == null)
                        {
                            if (state.IsolationLaunchesRemaining <= 0)
                            {
                                FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_BUDGET_EXHAUSTED",
                                    "The deterministic isolation launch budget was exhausted before a conclusive diagnosis; no opted-in project was blamed.");
                                return;
                            }
                            if (!TryPlanIsolationAttemptLocked())
                            {
                                if (!IsolationActiveLocked())
                                    return;
                                continue;
                            }
                        }

                        incident = state.CrashIsolation;
                        attemptId = incident.CurrentAttemptId;
                        kind = incident.CurrentAttemptKind;
                        targetGeneration = Math.Max(state.Generation + 1, state.TargetGeneration);
                        profile = incident.CurrentAttemptProfile?.ToModProfile();
                        if (incident.CurrentAttemptResult != null)
                            consume = true;
                        else if (state.Phase == BridgePhase.LOADING)
                        {
                            // A recovery monitor owns an in-flight attempt. It will
                            // queue this worker after recording PASS/FAIL.
                            if (state.ProcessId > 0)
                                return;
                            incident.CurrentAttemptResult = "UNSAFE";
                            incident.CurrentAttemptFailurePhase = "LOADING";
                            incident.CurrentAttemptFailureCode = "ISOLATION_RECOVERY_AMBIGUOUS";
                            incident.CurrentAttemptFailureDetail =
                                "the coordinator restarted after isolation launch intent was persisted but before a verified process identity was recorded";
                            consume = true;
                            recoveryAmbiguous = true;
                        }
                    }

                    if (consume)
                    {
                        bool retainFinalControl;
                        lock (gate)
                        {
                            retainFinalControl = (string.Equals(kind, "FINAL_CONTROL", StringComparison.Ordinal) ||
                                string.Equals(kind, "FINAL_BASELINE_CONTROL", StringComparison.Ordinal)) &&
                                string.Equals(state.CrashIsolation?.CurrentAttemptResult, "PASS", StringComparison.Ordinal);
                        }
                        if (!retainFinalControl)
                        {
                            if (!StopIsolationProcess(out string stopCode, out string stopError))
                            {
                                lock (gate)
                                    FinalizeIsolationEnvironmentalLocked(stopCode ?? "ISOLATION_STOP_FAILED",
                                        stopError ?? "the isolated RimWorld process could not be drained safely");
                                return;
                            }
                        }

                        lock (gate)
                        {
                            if (!IsolationActiveLocked())
                                return;
                            CrashIsolationIncident incident = state.CrashIsolation;
                            StoreCurrentIsolationAttemptLocked();
                            if (recoveryAmbiguous)
                            {
                                FinalizeIsolationEnvironmentalLocked("ISOLATION_RECOVERY_AMBIGUOUS",
                                    incident.CurrentAttemptFailureDetail);
                                return;
                            }
                            if (retainFinalControl)
                            {
                                FinalizeIsolationCompletedLocked();
                                return;
                            }
                            AdvanceIsolationAfterAttemptLocked(persist: false);
                            if (!IsolationActiveLocked())
                                return;
                            ClearCurrentIsolationAttemptLocked(persist: false);
                            SaveStateLocked();
                        }
                        continue;
                    }

                    if (profile == null)
                    {
                        lock (gate)
                            FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_PROFILE_MISSING",
                                "the durable isolation candidate profile was missing");
                        return;
                    }

                    if (!PrepareIsolationAttempt(profile, attemptId, targetGeneration,
                            out string prepareErrorCode, out string prepareError))
                    {
                        lock (gate)
                        {
                            CrashIsolationIncident incident = state.CrashIsolation;
                            if (incident != null && string.Equals(incident.CurrentAttemptId, attemptId,
                                    StringComparison.Ordinal))
                            {
                                incident.CurrentAttemptResult = "UNSAFE";
                                incident.CurrentAttemptFailurePhase = "PREPARE";
                                incident.CurrentAttemptFailureCode = prepareErrorCode ?? "ISOLATION_PREPARE_FAILED";
                                incident.CurrentAttemptFailureDetail = prepareError;
                                incident.CurrentAttemptProfileInstalled = false;
                                StoreCurrentIsolationAttemptLocked();
                                FinalizeIsolationEnvironmentalLocked(
                                    prepareErrorCode ?? "ISOLATION_PREPARE_FAILED", prepareError);
                                return;
                            }
                        }
                        continue;
                    }

                    LaunchGenerationWorker(targetGeneration, isRestart: true,
                        owner: "isolation@" + runtimeSlotId, isolationProfile: profile,
                        isolationAttemptId: attemptId);
                }
            }
        }
        catch (ProfileException exception)
        {
            lock (gate)
                FinalizeIsolationEnvironmentalLocked(exception.Code, exception.Message);
        }
        catch (ProcessInspectionException)
        {
            lock (gate)
                FinalizeIsolationEnvironmentalLocked(ProcessInspection.ErrorCode, ProcessInspection.Message);
        }
        catch (Exception exception)
        {
            lock (gate)
                FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_FAILED", exception.Message);
        }
    }

    private void AdvanceIsolationAfterAttemptLocked(bool persist = true)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        string stage = incident.Stage;
        string result = incident.CurrentAttemptResult;
        string kind = incident.CurrentAttemptKind;

        if (!string.Equals(result, "PASS", StringComparison.Ordinal) &&
            !string.Equals(result, "FAIL", StringComparison.Ordinal))
        {
            FinalizeIsolationEnvironmentalLocked("ISOLATION_UNSAFE_RESULT",
                incident.CurrentAttemptFailureDetail ??
                "the isolation attempt did not produce safe profile-failure evidence");
            return;
        }

        if (stage == "CONTROL")
        {
            if (!string.Equals(result, "PASS", StringComparison.Ordinal))
            {
                FinalizeIsolationEnvironmentalLocked("ENVIRONMENTAL_BASELINE_FAILURE",
                    "The durable baseline/last-known-good control profile also failed before readiness; no opted-in project was blamed.");
                return;
            }
            incident.Stage = "REPRODUCE";
            if (persist)
                SaveStateLocked();
            return;
        }

        if (stage == "REPRODUCE")
        {
            if (string.Equals(result, "PASS", StringComparison.Ordinal))
            {
                incident.DiagnosisCode = "INTERMITTENT_PROFILE_FAILURE";
                incident.Diagnosis = "The accepted project profile passed when reproduced after the control profile; the original startup failure is intermittent/nondeterministic, so no project was attributed.";
                incident.Diagnoses.Clear();
                incident.Stage = "FINAL_CONTROL";
            }
            else
            {
                incident.SearchPoolKnownFail = true;
                incident.DeltaCurrentProjects = StableProjectOrder(incident.SearchPoolProjects);
                incident.DeltaGranularity = Math.Min(2, Math.Max(1, incident.DeltaCurrentProjects.Count));
                incident.PendingCandidates = new List<CrashIsolationSelection>();
                incident.PendingCandidateIndex = 0;
                incident.PendingKind = null;
                incident.Stage = "MINIMIZE";
            }
            if (persist)
                SaveStateLocked();
            return;
        }

        if (stage == "VERIFY_REMAINDER")
        {
            if (string.Equals(result, "FAIL", StringComparison.Ordinal))
            {
                incident.SearchPoolKnownFail = true;
                incident.DeltaCurrentProjects = StableProjectOrder(incident.DeltaCurrentProjects);
                incident.DeltaGranularity = Math.Min(2, Math.Max(1, incident.DeltaCurrentProjects.Count));
                incident.PendingCandidates = new List<CrashIsolationSelection>();
                incident.PendingCandidateIndex = 0;
                incident.PendingKind = null;
                incident.Stage = "MINIMIZE";
            }
            else
            {
                // This remainder has passed after the diagnosed roots were
                // removed. Preserve it durably so unrelated requested roots
                // can remain enabled in the recovered runtime.
                incident.SafeRemainderProfile = incident.CurrentAttemptProfile == null
                    ? null
                    : PersistedProfileSnapshot.FromModProfile(incident.CurrentAttemptProfile.ToModProfile());
                incident.Stage = "FINAL_CONTROL";
            }
            if (persist)
                SaveStateLocked();
            return;
        }

        if (stage == "MINIMIZE")
        {
            List<string> current = StableProjectOrder(incident.DeltaCurrentProjects);
            if (incident.PendingCandidateIndex < incident.PendingCandidates.Count)
                incident.PendingCandidateIndex++;

            if (string.Equals(result, "FAIL", StringComparison.Ordinal))
            {
                incident.DeltaCurrentProjects = StableProjectOrder(incident.CurrentAttemptProjects);
                incident.DeltaGranularity = Math.Max(2,
                    Math.Min(incident.DeltaCurrentProjects.Count, incident.DeltaGranularity - 1));
                incident.PendingCandidates = new List<CrashIsolationSelection>();
                incident.PendingCandidateIndex = 0;
                incident.PendingKind = null;
            }
            else if (incident.PendingCandidateIndex >= incident.PendingCandidates.Count)
            {
                int n = Math.Min(current.Count, Math.Max(2, incident.DeltaGranularity));
                if (incident.PendingKind == "REMOVE" && n < current.Count)
                {
                    StartIsolationRoundLocked(complements: false, persist: persist);
                }
                else if (incident.PendingKind == "DIRECT" && n < current.Count)
                {
                    incident.DeltaGranularity = Math.Min(current.Count, n * 2);
                    StartIsolationRoundLocked(complements: true, persist: persist);
                }
                else
                    CompleteMinimalSetLocked(current, persist);
            }
            if (persist)
                SaveStateLocked();
            return;
        }

        if (stage == "FINAL_CONTROL")
        {
            if (string.Equals(result, "FAIL", StringComparison.Ordinal) &&
                !incident.FinalControlBaselineAttempted)
            {
                ModProfile baseline = ModProfileResolver.CreateBaselineProfile(
                    incident.OriginalBaselineFingerprint);
                bool finalProfileWasBaseline = incident.CurrentAttemptProfile != null &&
                    string.Equals(incident.CurrentAttemptProfile.ProfileFingerprint,
                        baseline.ProfileFingerprint, StringComparison.Ordinal);
                if (!finalProfileWasBaseline)
                {
                    incident.SafeRemainderProfile = null;
                    incident.FinalControlBaselineAttempted = true;
                    incident.Stage = "FINAL_BASELINE_CONTROL";
                    if (persist)
                        SaveStateLocked();
                    return;
                }
            }
            FinalizeIsolationEnvironmentalLocked("ENVIRONMENTAL_BASELINE_FAILURE",
                "The known-good control profile failed while restoring after isolation; no opted-in project was blamed.");
            return;
        }

        if (stage == "FINAL_BASELINE_CONTROL")
        {
            FinalizeIsolationEnvironmentalLocked("ENVIRONMENTAL_BASELINE_FAILURE",
                "The durable baseline control profile failed while restoring after isolation; no opted-in project was blamed.");
            return;
        }

        FinalizeIsolationEnvironmentalLocked("CRASH_ISOLATION_FAILED",
            "the isolation state machine reached an unknown phase");
    }

    private void FinalizeIsolationEnvironmentalLocked(string code, string detail)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null)
            return;
        // A terminal environmental result is deliberately non-attributive.
        // Do not leave earlier candidate diagnoses visible beside it.
        incident.Diagnoses ??= new List<CrashIsolationDiagnosis>();
        incident.Diagnoses.Clear();
        if (incident.CurrentAttemptId != null)
        {
            if (incident.CurrentAttemptResult == null)
            {
                incident.CurrentAttemptResult = "UNSAFE";
                incident.CurrentAttemptFailurePhase = state.Phase.ToString();
                incident.CurrentAttemptFailureCode = code;
                incident.CurrentAttemptFailureDetail = detail;
            }
            StoreCurrentIsolationAttemptLocked();
        }

        string finalCode = code;
        string finalDetail = detail;
        bool noRimWorldProcess = false;
        bool censusComplete = false;
        try
        {
            noRimWorldProcess = FindUnmanagedRimWorldProcesses(0, 0).Count == 0;
            censusComplete = true;
        }
        catch (ProcessInspectionException)
        {
            finalCode = ProcessInspection.ErrorCode;
            finalDetail = ProcessInspection.Message + " Isolation was quarantined without changing ModsConfig.xml.";
        }

        if (noRimWorldProcess)
        {
            try
            {
                ModProfile control = state.LastKnownGoodProfile?.ToModProfile() ??
                    ModProfileResolver.CreateBaselineProfile(incident.OriginalBaselineFingerprint);
                string ownership = CurrentModsConfigOwnershipLocked();
                bool generatedOwnership = ownership == "DEVBRIDGE_GENERATED" || ownership == "DEVBRIDGE_PENDING";
                string generatedProfileFingerprint = state.ModsConfigGeneratedProfileFingerprint;
                if (string.IsNullOrWhiteSpace(generatedProfileFingerprint))
                    generatedProfileFingerprint = ReadGeneratedModsConfigManifestLocked(out _)?.ProfileFingerprint;

                if (state.ProfileMode == ModProfile.ProjectsMode && ownership != "BASELINE" &&
                    !generatedOwnership)
                {
                    throw new ProfileException("MODS_CONFIG_EXTERNAL_EDIT",
                        "ModsConfig.xml ownership is " + ownership + "; the candidate profile was not overwritten.");
                }

                string installedProfileFingerprint = ownership == "BASELINE"
                    ? incident.OriginalBaselineFingerprint
                    : generatedProfileFingerprint;
                if (state.ProfileMode == ModProfile.ProjectsMode &&
                    (ownership == "BASELINE" || generatedOwnership) &&
                    !string.Equals(installedProfileFingerprint, control.ProfileFingerprint,
                        StringComparison.Ordinal))
                {
                    ApplyProfile(control, Math.Max(state.Generation + 1, state.TargetGeneration));
                }
                if (state.ProfileMode == ModProfile.ProjectsMode &&
                    (ownership == "BASELINE" || generatedOwnership))
                    state.RuntimeProfile = PersistedProfileSnapshot.FromModProfile(control);
            }
            catch (ProfileException exception)
            {
                finalCode = "CRASH_ISOLATION_RECOVERY_UNSAFE";
                finalDetail = exception.Message + " ModsConfig.xml was left unchanged.";
                state.SessionDirty = true;
            }
            catch (ProcessInspectionException)
            {
                finalCode = ProcessInspection.ErrorCode;
                finalDetail = ProcessInspection.Message + " ModsConfig.xml was left unchanged.";
                state.SessionDirty = true;
            }
            catch (Exception exception)
            {
                finalCode = "CRASH_ISOLATION_RECOVERY_UNSAFE";
                finalDetail = "DevBridge could not safely restore the control profile: " +
                    exception.Message + " ModsConfig.xml may require manual verification.";
                state.SessionDirty = true;
            }
        }
        else if (censusComplete)
        {
            finalCode = "CRASH_ISOLATION_RECOVERY_QUARANTINED";
            finalDetail = detail + " A RimWorld process is still present or could not be safely identified; no process was stopped and ModsConfig.xml was not changed.";
            state.SessionDirty = true;
        }
        else
            state.SessionDirty = true;

        incident.Status = "ENVIRONMENTAL_FAILURE";
        incident.Stage = "TERMINAL";
        incident.DiagnosisCode = finalCode;
        incident.Diagnosis = finalDetail;
        state.Phase = BridgePhase.ERROR;
        state.RestartPending = false;
        state.TargetGeneration = 0;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.WaitingForBridgeDeadlineUtc = null;
        if (noRimWorldProcess)
        {
            state.ProcessId = 0;
            state.ProcessStartUtcTicks = 0;
            state.LaunchId = null;
            state.LaunchProfileFingerprint = null;
            state.LaunchProfileInstalled = false;
            state.LaunchAttemptStarted = false;
        }
        state.IsolationLaunchesRemaining = 0;
        incident.IsolationLaunchesRemaining = 0;
        state.ErrorCode = finalCode;
        state.Error = finalDetail;
        state.ProfileErrorCode = finalCode;
        state.ProfileError = finalDetail;
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void FinalizeIsolationCompletedLocked()
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        if (incident == null)
            return;
        // The profile that passed FINAL_CONTROL is the maximal safe remainder
        // (or the baseline fallback). Promote that exact snapshot to the new
        // durable control; retaining the pre-isolation baseline here would
        // silently discard unrelated healthy projects.
        PersistedProfileSnapshot restoredProfile = incident.SafeRemainderProfile ??
            state.RuntimeProfile ?? state.LastKnownGoodProfile;
        if (restoredProfile != null)
        {
            state.LastKnownGoodProfile = restoredProfile;
            state.RuntimeProfile = restoredProfile;
        }
        incident.Status = "COMPLETED";
        incident.Stage = "TERMINAL";
        if (string.IsNullOrWhiteSpace(incident.DiagnosisCode))
        {
            incident.DiagnosisCode = "NO_DETERMINISTIC_PROJECT_FAILURE";
            incident.Diagnosis = "The accepted project profile could not be reduced to a deterministic failing project set.";
        }
        state.Phase = BridgePhase.READY;
        state.Generation = state.TargetGeneration;
        state.RestartPending = false;
        state.RestartRequestedUtc = null;
        state.TargetGeneration = 0;
        state.LastLaunchOwner = state.LaunchOwner;
        state.LastLaunchRequestKey = state.LaunchRequestKey;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.RequiresNewProcess = false;
        state.MaintenanceReady = false;
        state.LaunchProfileFingerprint = null;
        state.LaunchProfileInstalled = false;
        state.LaunchAttemptStarted = false;
        state.IsolationLaunchesRemaining = 0;
        incident.IsolationLaunchesRemaining = 0;
        state.Error = null;
        state.ErrorCode = "CRASH_ISOLATION_COMPLETE";
        state.ProfileErrorCode = incident.DiagnosisCode;
        state.ProfileError = incident.Diagnosis;
        incident.CurrentAttemptId = null;
        incident.CurrentAttemptFingerprint = null;
        incident.CurrentAttemptKind = null;
        incident.CurrentAttemptProfile = null;
        incident.CurrentAttemptProjects = new List<string>();
        incident.CurrentAttemptResult = null;
        incident.CurrentAttemptFailurePhase = null;
        incident.CurrentAttemptFailureCode = null;
        incident.CurrentAttemptFailureDetail = null;
        incident.CurrentAttemptProfileInstalled = false;
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void FailLaunch(string detail, string errorCode = "LAUNCH_FAILED")
    {
        lock (gate)
        {
            string failurePhase = state.Phase.ToString();
            if (IsolationActiveLocked() && state.CrashIsolation.CurrentAttemptId != null)
            {
                CrashIsolationIncident incident = state.CrashIsolation;
                if (!IsIsolationEvidenceFailure(errorCode) ||
                    !state.LaunchProfileInstalled || !state.LaunchAttemptStarted ||
                    !string.Equals(state.LaunchProfileFingerprint,
                        incident.CurrentAttemptFingerprint, StringComparison.Ordinal))
                {
                    FinalizeIsolationEnvironmentalLocked(errorCode, detail);
                    return;
                }

                if (incident.CurrentAttemptResult != null)
                    return;
                incident.CurrentAttemptResult = "FAIL";
                incident.CurrentAttemptFailurePhase = failurePhase;
                incident.CurrentAttemptFailureCode = errorCode;
                incident.CurrentAttemptFailureDetail = detail;
                incident.CurrentAttemptProfileInstalled = state.LaunchProfileInstalled;
                state.Phase = BridgePhase.ISOLATING;
                state.RestartPending = true;
                state.ErrorCode = "CRASH_ISOLATION_ATTEMPT_FAILED";
                state.Error = detail;
                SaveStateLocked();
                QueueIsolationContinuationLocked();
                Monitor.PulseAll(gate);
                return;
            }

            if (IsolationActiveLocked())
            {
                FinalizeIsolationEnvironmentalLocked(errorCode, detail);
                return;
            }

            if (IsEligibleForCrashIsolationLocked(errorCode))
            {
                BeginCrashIsolationLocked(detail, errorCode);
                return;
            }

            state.Phase = BridgePhase.ERROR;
            state.RestartPending = false;
            state.ErrorCode = errorCode;
            if (errorCode.StartsWith("PROFILE_", StringComparison.Ordinal) ||
                errorCode.StartsWith("MODS_CONFIG_", StringComparison.Ordinal))
            {
                state.ProfileErrorCode = errorCode;
                state.ProfileError = detail;
            }
            state.Error = errorCode == ProcessInspection.ErrorCode ? ProcessInspection.Message :
                errorCode == "READINESS_TIMEOUT" ?
                "READINESS_TIMEOUT: " + detail + ". The original process was retained; no replacement launch was attempted." :
                "RimWorld did not report a playable quicktest map: " + detail +
                ". Inspect Runtime/readiness.json and the RimWorld logs, then run DevBridge.cmd restart.";
            state.LaunchOwner = null;
            state.LaunchRequestKey = null;
            state.WaitingForBridgeDeadlineUtc = null;
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
            bool alreadyExited = process.HasExited;
            // An exact, already-exited instance is safely drained. This is
            // important after PROCESS_EXITED: requiring a running process here
            // would turn an observed crash into an identity ambiguity.
            if (!IsExactProcessIdentity(process, startTicks))
                return (false, "PROCESS_IDENTITY_CHANGED", "the persisted RimWorld process identity no longer matches");

            if (alreadyExited)
                return (true, null, null);

            if (!process.HasExited)
            {
                try
                {
                    process.RequestTermination();
                    process.WaitForExit(options.ProcessExitTimeout);
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
        // CLI commands are separate short-lived processes. The lease ID is the
        // capability and Agent is the durable caller identity; ClientProcessId
        // remains diagnostic metadata rather than a later-command authorization key.
        lease = state.Leases.FirstOrDefault(value =>
            string.Equals(value.Id, leaseId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.Agent, request.Agent, StringComparison.Ordinal));
        return lease != null;
    }

    private void MarkReadyLocked(string launchId, int targetGeneration, int processId, long processStartIdentity)
    {
        if (!string.Equals(state.LaunchId, launchId, StringComparison.Ordinal) ||
            state.LaunchGeneration != targetGeneration || state.ProcessId != processId ||
            state.ProcessStartUtcTicks != processStartIdentity || !IsOwnedProcess(processId, processStartIdentity))
            return;

        if (state.CrashIsolation != null &&
            !string.IsNullOrWhiteSpace(state.CrashIsolation.CurrentAttemptId))
        {
            CrashIsolationIncident incident = state.CrashIsolation;
            incident.CurrentAttemptResult = "PASS";
            incident.CurrentAttemptFailurePhase = null;
            incident.CurrentAttemptFailureCode = null;
            incident.CurrentAttemptFailureDetail = null;
            incident.CurrentAttemptProfileInstalled = state.LaunchProfileInstalled;
            state.Generation = targetGeneration;
            state.Phase = BridgePhase.ISOLATING;
            state.Error = null;
            state.ErrorCode = null;
            state.RestartPending = true;
            state.TargetGeneration = targetGeneration;
            state.RequiresNewProcess = true;
            state.MaintenanceReady = false;
            SaveStateLocked();
            QueueIsolationContinuationLocked();
            Monitor.PulseAll(gate);
            return;
        }

        state.Generation = targetGeneration;
        state.Phase = BridgePhase.READY;
        state.Error = null;
        state.ErrorCode = null;
        state.RestartPending = false;
        state.RestartRequestedUtc = null;
        state.TargetGeneration = 0;
        state.LastLaunchOwner = state.LaunchOwner;
        state.LastLaunchRequestKey = state.LaunchRequestKey;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.WaitingForBridgeDeadlineUtc = null;
        state.RequiresNewProcess = false;
        state.MaintenanceReady = false;
        if (state.LaunchProfileInstalled && state.RuntimeProfile != null)
            state.LastKnownGoodProfile = state.RuntimeProfile;
        state.RuntimeProfile = state.LaunchProfileInstalled && state.LaunchProfileFingerprint != null
            ? (state.RuntimeProfile ?? (state.ProfileMode == ModProfile.LegacyMode ? null :
                PersistedProfileSnapshot.FromModProfile(ProfileFromStateLocked())))
            : state.RuntimeProfile;
        state.LaunchProfileFingerprint = null;
        state.LaunchProfileInstalled = false;
        state.LaunchAttemptStarted = false;
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

    private void RecoverProcessInspectionQuarantineLocked()
    {
        state.Phase = BridgePhase.STOPPED;
        state.Error = null;
        state.ErrorCode = null;
        state.ProcessId = 0;
        state.ProcessStartUtcTicks = 0;
        state.LaunchId = null;
        state.LaunchStartedUtc = default;
        state.RestartPending = false;
        state.RestartRequestedUtc = null;
        state.MaintenanceReady = false;
        state.LaunchOwner = null;
        state.LaunchRequestKey = null;
        state.WaitingForBridgeDeadlineUtc = null;
        state.RequiresNewProcess = true;
        DeleteReadinessLocked();
        SaveStateLocked();
        Monitor.PulseAll(gate);
    }

    private void SynchronizeLocked()
    {
        PruneStaleLeasesLocked();

        if (IsolationActiveLocked())
        {
            if (state.CrashIsolation?.CurrentAttemptResult != null &&
                (launchTask == null || launchTask.IsCompleted) &&
                (isolationTask == null || isolationTask.IsCompleted))
                ResumePersistedIsolationResultLocked();
            else if (state.Phase == BridgePhase.LOADING && state.ProcessId > 0 &&
                (launchTask == null || launchTask.IsCompleted) &&
                (isolationTask == null || isolationTask.IsCompleted))
            {
                if (IsolationLaunchStateMatchesLocked())
                    StartMonitorLaunchLocked(state.TargetGeneration);
                else
                    FinalizeIsolationEnvironmentalLocked("ISOLATION_PROFILE_MISMATCH",
                        "the persisted isolation launch profile does not match the durable candidate; no replacement launch was attempted");
            }
            else if (state.Phase == BridgePhase.LOADING && state.ProcessId <= 0 &&
                     (launchTask == null || launchTask.IsCompleted) &&
                     (isolationTask == null || isolationTask.IsCompleted))
                FailLaunch("the persisted isolation attempt has no verified process identity; attribution was not attempted",
                    "ISOLATION_RECOVERY_AMBIGUOUS");
            else if (state.Phase != BridgePhase.LOADING)
                StartIsolationWorkerLocked();
            return;
        }

        if (state.Phase == BridgePhase.LOADING && state.ProcessId <= 0 &&
            (launchTask == null || launchTask.IsCompleted) &&
            (restartTask == null || restartTask.IsCompleted))
        {
            FailLaunch("the persisted launch has no verified process identity; no replacement launch was attempted",
                "LAUNCH_RECOVERY_AMBIGUOUS");
            return;
        }

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
            if (string.IsNullOrWhiteSpace(state.LaunchOwner))
            {
                FailLaunch("the accepted restart has no durable launch owner; no launch was attempted",
                    "LAUNCH_OWNER_MISSING");
                return;
            }
            if ((state.Phase != BridgePhase.LOADING || launchTask == null || launchTask.IsCompleted) &&
                (restartTask == null || restartTask.IsCompleted))
                StartRestartWorkerLocked(state.TargetGeneration, state.LaunchOwner);
        }
        else if (state.Phase == BridgePhase.LOADING && state.ProcessId > 0 &&
                 (launchTask == null || launchTask.IsCompleted))
        {
            StartMonitorLaunchLocked(state.TargetGeneration);
        }
    }

    private void PruneStaleLeasesLocked()
    {
        DateTime cutoff = clock.UtcNow - options.LeaseDuration;
        int before = state.Leases.Count;
        state.Leases.RemoveAll(lease => lease == null || LeaseActivityUtc(lease) <= cutoff);
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
        bool changed = false;
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
            throw new InvalidOperationException("Persisted runtime slot does not match this coordinator root.");
        }

        state.Leases ??= new List<TestLease>();
        state.ScopeTickets ??= new List<ScopeTicket>();
        state.CrashIsolationHistory ??= new List<CrashIsolationIncident>();
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

        if (state.ProfileMode != ModProfile.LegacyMode && state.ProfileMode != ModProfile.BaselineMode &&
            state.ProfileMode != ModProfile.ProjectsMode)
        {
            QuarantineInvalidProfileLocked(new ProfileException("PROFILE_INVALID_STATE",
                "Persisted profile mode is invalid: " + persistedProfileMode + "."));
        }
        state.RequestedProjects ??= new List<string>();
        state.ResolvedProjectPackageIds ??= new List<string>();
        state.ResolvedMods ??= new List<string>();
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
                    ModProfileResolver.CreateBaselineProfile(state.BaselineFingerprint));
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
        if (changed)
            SaveStateLocked();
    }

    private PersistedState CloneStateLocked()
    {
        string json = JsonSerializer.Serialize(state, Program.JsonOptions);
        return JsonSerializer.Deserialize<PersistedState>(json, Program.JsonOptions) ?? new PersistedState();
    }

    private void SaveStateLocked()
    {
        Directory.CreateDirectory(runtimeRoot);
        AtomicWriteFile(statePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Program.JsonOptions)));
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
            ProfileFingerprint = state.ProfileFingerprint,
            BaselineFingerprint = state.BaselineFingerprint
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
        state.ProfileFingerprint = profile.ProfileFingerprint;
        state.BaselineFingerprint = profile.BaselineFingerprint;
    }

    private void ClearActiveProfileLocked()
    {
        state.ProfileMode = ModProfile.LegacyMode;
        state.RequestedProjects = new List<string>();
        state.ResolvedProjectPackageIds = new List<string>();
        state.ResolvedMods = new List<string>();
        state.ProfileFingerprint = null;
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
        emit("Profile mode: " + (string.IsNullOrWhiteSpace(snapshot.ProfileMode) ? ModProfile.LegacyMode : snapshot.ProfileMode));
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
    }

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

    private string CurrentModsConfigOwnershipLocked(byte[] contents, string fingerprint)
    {
        GeneratedModsConfigManifest generatedManifest = ReadGeneratedModsConfigManifestLocked(out bool manifestPresent);
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
            if (ownership == "USER_EDIT" || ownership == "USER" || ownership == "UNKNOWN" || ownership == "MISSING")
                throw new ProfileException("MODS_CONFIG_EXTERNAL_EDIT",
                    "ModsConfig.xml differs from the captured baseline or known DevBridge output; capture the intentional edit before using a reduced profile.");
        }

        byte[] updated = RenderProfileModsConfig(current, profile.ResolvedMods);
        string updatedFingerprint = HashBytes(updated);
        lock (gate)
        {
            state.ModsConfigOwnership = "DEVBRIDGE_PENDING";
            state.ModsConfigGeneratedHash = updatedFingerprint;
            state.ModsConfigGeneratedProfileFingerprint = profile.ProfileFingerprint;
            state.ModsConfigGeneratedGeneration = targetGeneration;
            SaveStateLocked();
        }

        try
        {
            WriteGeneratedModsConfigManifest(updatedFingerprint, profile.ProfileFingerprint, targetGeneration);
        }
        catch (Exception exception)
        {
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
        AtomicWriteFile(modsConfigPath, updated);
        lock (gate)
        {
            state.ModsConfigOwnership = "DEVBRIDGE_GENERATED";
            state.ModsConfigGeneratedHash = updatedFingerprint;
            state.ModsConfigGeneratedProfileFingerprint = profile.ProfileFingerprint;
            state.ModsConfigGeneratedGeneration = targetGeneration;
            state.BaselineFingerprint = profile.BaselineFingerprint;
            SaveStateLocked();
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

    private GeneratedModsConfigManifest ReadGeneratedModsConfigManifestLocked(out bool present)
    {
        present = false;
        try
        {
            if (!File.Exists(generatedManifestPath))
                return null;
            present = true;
            GeneratedModsConfigManifest manifest = JsonSerializer.Deserialize<GeneratedModsConfigManifest>(
                File.ReadAllText(generatedManifestPath), Program.JsonOptions);
            if (manifest == null || !IsValidHash(manifest.Hash))
                return null;
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    private void WriteGeneratedModsConfigManifest(string hash, string profileFingerprint, int generation)
    {
        if (!IsValidHash(hash))
            throw new InvalidDataException("the generated ModsConfig hash was invalid");
        GeneratedModsConfigManifest manifest = new()
        {
            Hash = hash.ToUpperInvariant(),
            ProfileFingerprint = profileFingerprint,
            Generation = generation
        };
        AtomicWriteFile(generatedManifestPath,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Program.JsonOptions)));
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

    private void EnsureNoMatchingRimWorldProcess()
    {
        ProcessStatusSnapshot processes;
        lock (gate)
            processes = EnumerateStatusProcessesLocked();
        if (processes.MatchingProcessCount > 0)
            throw new ProfileException("MODS_CONFIG_PROCESS_RUNNING",
                "a matching RimWorld process is running; ModsConfig.xml was not changed");
    }

    private static void AtomicWriteFile(string path, byte[] contents)
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
            generation = state.TargetGeneration > 0 ? state.TargetGeneration : state.Generation;
        try
        {
            // Record the expected output before replacement so a crash after the config
            // swap cannot make generated content look like a user baseline.
            WriteGeneratedModsConfigManifest(updatedFingerprint, null, generation);
        }
        catch (Exception exception)
        {
            throw new ProfileException("MODS_CONFIG_OWNERSHIP_WRITE_FAILED",
                "DevBridge could not durably record generated ModsConfig ownership: " + exception.Message);
        }
        AtomicWriteFile(modsConfigPath, updated);
        lock (gate)
        {
            state.ModsConfigOwnership = "DEVBRIDGE_GENERATED";
            state.ModsConfigGeneratedHash = updatedFingerprint;
            state.ModsConfigGeneratedProfileFingerprint = null;
            state.ModsConfigGeneratedGeneration = generation;
            SaveStateLocked();
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
            else
                PruneStaleLeasesLocked();
            snapshot = CloneStateLocked();
            snapshot.BaselineFingerprint = ReadBaselineFingerprintLocked() ?? snapshot.BaselineFingerprint;
            snapshot.ModsConfigOwnership = CurrentModsConfigOwnershipLocked();
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
            CoordinatorRoot = snapshot.CoordinatorRoot,
            RuntimeSlotId = snapshot.RuntimeSlotId,
            GoalId = request.GoalId,
            WakeId = request.WakeId,
            McpRequestId = request.McpRequestId,
            GameState = snapshot.Phase.ToString(),
            Generation = snapshot.Generation,
            RimWorldPid = snapshot.ProcessId,
            RimWorldProcessStartIdentity = snapshot.ProcessStartUtcTicks,
            LaunchGeneration = snapshot.LaunchGeneration,
            MaintenanceReady = snapshot.MaintenanceReady,
            LeaseState = snapshot.Leases.Any(value =>
                string.Equals(value.Agent, request.Agent, StringComparison.Ordinal)) ? "HELD" : "QUEUED",
            SessionDirty = snapshot.SessionDirty,
            ActiveTests = snapshot.Leases.Count,
            RestartPending = snapshot.RestartPending,
            RestartQueued = snapshot.RestartPending,
            TargetGeneration = snapshot.TargetGeneration,
            LaunchOwner = snapshot.LaunchOwner,
            LaunchAttemptCount = snapshot.LaunchAttemptCount,
            LaunchBudgetRemaining = snapshot.LaunchBudgetRemaining,
            WaitingForBridgeDeadlineUtc = snapshot.WaitingForBridgeDeadlineUtc,
            NextLeaseExpirationUtc = snapshot.Leases.Count == 0
                ? null
                : snapshot.Leases.Min(value => LeaseExpiresUtc(value)),
            RetryAfterSeconds = snapshot.Leases.Count == 0
                ? null
                : RetryAfterSeconds(snapshot.Leases.Min(value => LeaseExpiresUtc(value)), clock.UtcNow),
            RequiresNewProcess = snapshot.RequiresNewProcess,
            ProfileMode = snapshot.ProfileMode,
            RequestedProjects = snapshot.RequestedProjects ?? new List<string>(),
            ResolvedProjectPackageIds = snapshot.ResolvedProjectPackageIds ?? new List<string>(),
            ResolvedMods = snapshot.ResolvedMods ?? new List<string>(),
            ProfileFingerprint = snapshot.ProfileFingerprint,
            BaselineFingerprint = snapshot.BaselineFingerprint,
            ModsConfigOwnership = snapshot.ModsConfigOwnership,
            ProfileConflict = snapshot.ProfileConflict,
            RuntimeProfileFingerprint = snapshot.RuntimeProfile?.ProfileFingerprint,
            CrashIsolation = snapshot.CrashIsolation,
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
        else if (string.Equals(request.Command, "test", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(subcommand, "renew", StringComparison.OrdinalIgnoreCase) &&
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
            : !string.IsNullOrWhiteSpace(snapshot.ProfileError)
                ? snapshot.ProfileError
            : effectiveExitCode == 0
                ? null
                : messages.LastOrDefault(value => !value.StartsWith("Next action:", StringComparison.Ordinal));
        response.ErrorCode = snapshot.ErrorCode ?? snapshot.ProfileErrorCode;
        response.NextAction = JsonNextAction(request, snapshot, effectiveExitCode, response.LeaseId);
        return response;
    }

    private string JsonNextAction(BridgeRequest request, PersistedState snapshot,
        int exitCode, string leaseId)
    {
        string command = request.Command ?? string.Empty;
        string subcommand = request.Arguments.Count > 0 ? request.Arguments[0] : string.Empty;

        if (snapshot.ErrorCode == ProcessInspection.ErrorCode ||
            snapshot.ErrorCode == "MAINTENANCE_PROCESS_PRESENT")
            return "Run: DevBridge.cmd doctor";

        if (snapshot.Phase == BridgePhase.ISOLATING ||
            (snapshot.CrashIsolation != null &&
             !IsTerminalIsolationStatus(snapshot.CrashIsolation.Status)))
        {
            return "Crash isolation is running; Do not retry or change ModsConfig.xml. Run: DevBridge.cmd status and keep waiting.";
        }

        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "begin", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
            return "Test your mod; this lease expires two minutes after its last heartbeat. Renew before expiresUtc, or start long-running work with test session, then run: DevBridge.cmd test end " + leaseId;

        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "end", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
        {
            return snapshot.RestartPending
                ? WaitingNextAction(snapshot)
                : "Continue your workflow; run DevBridge.cmd restart only after a change requiring a fresh process.";
        }

        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(subcommand, "renew", StringComparison.OrdinalIgnoreCase) && exitCode == 0)
            return "Continue testing; renew the lease before expiresUtc, or keep a connected test session.";

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
            return WaitingNextAction(snapshot);
        if (snapshot.Phase == BridgePhase.STOPPED && snapshot.Generation > 0)
            return "Run: DevBridge.cmd restart";
        return "Run: DevBridge.cmd wait-ready";
    }

    private string WaitingNextAction(PersistedState snapshot)
    {
        TestLease next = snapshot.Leases.OrderBy(value => LeaseExpiresUtc(value)).FirstOrDefault();
        if (next == null)
            return "Restart is queued and owned by DevBridge; reconnect with DevBridge.cmd wait-ready and keep waiting. Do not end the task.";

        DateTime expiresUtc = LeaseExpiresUtc(next);
        return "Restart is queued and owned by DevBridge; reconnect with DevBridge.cmd wait-ready and keep waiting. The next blocking lease can expire at " +
            FormatUtc(expiresUtc) + " (retryAfterSeconds=" + RetryAfterSeconds(expiresUtc, clock.UtcNow) + "). Do not end the task.";
    }

    private JsonLeaseInfo ToJsonLease(TestLease lease)
    {
        DateTime expiresUtc = LeaseExpiresUtc(lease);
        return new JsonLeaseInfo
        {
            Id = lease.Id,
            Agent = lease.Agent,
            Generation = lease.Generation,
            StartedUtc = lease.StartedUtc,
            LastHeartbeatUtc = LeaseActivityUtc(lease),
            ExpiresUtc = expiresUtc,
            RetryAfterSeconds = RetryAfterSeconds(expiresUtc, clock.UtcNow),
            Age = FormatAge(lease.StartedUtc)
        };
    }

    private DateTime LeaseExpiresUtc(TestLease lease)
    {
        return LeaseActivityUtc(lease).Add(options.LeaseDuration);
    }

    private static int RetryAfterSeconds(DateTime expiresUtc, DateTime nowUtc)
    {
        double seconds = (expiresUtc.ToUniversalTime() - nowUtc.ToUniversalTime()).TotalSeconds;
        if (seconds <= 0)
            return 0;
        return (int)Math.Min(int.MaxValue, Math.Ceiling(seconds));
    }

    private static DateTime LeaseActivityUtc(TestLease lease)
    {
        return (lease.LastHeartbeatUtc == default ? lease.StartedUtc : lease.LastHeartbeatUtc)
            .ToUniversalTime();
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

    private string FormatAge(DateTime startedUtc)
    {
        TimeSpan age = clock.UtcNow - startedUtc.ToUniversalTime();
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        return FormatDuration(age);
    }

    private static string FormatUtc(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
