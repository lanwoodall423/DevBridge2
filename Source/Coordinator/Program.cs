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
        Console.WriteLine("DevBridge commands: status | bridge status | bridge policy | bridge endpoint | bridge tools | bridge call <tool-name> [JSON arguments] [--lease <lease-id>] | project register <alias[,alias...]> | project status | project renew <registration-id> | project release <registration-id> | mods status | mods capture-baseline | mods restore-baseline | test begin | test session | test renew <lease-id> | test end <lease-id> | stop <lease-id> | coordinator shutdown | ensure-ready <lease-id> | restart [--projects none|alias[,alias...]] [--legacy-production] | wait-ready | history [show <generation>|last-good] | doctor");
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
    public string SessionId { get; set; }
    // Server-side only.  This is populated after dispatch so the normal JSON
    // response can carry a routed result without making route state durable.
    internal RimBridgeRouteResult RimBridgeRouteResult { get; set; }
    // Server-side only. Doctor caches its complete audit here so JSON
    // serialization cannot rerun checks or observe a different state.
    internal DoctorAuditReport DoctorAudit { get; set; }
    // Server-side only. History commands cache their deterministic view here
    // so response construction does not reread or mutate the history files.
    internal GenerationHistoryView HistoryResult { get; set; }
    // Server-side only. `project resolve` caches its pure planning result so
    // JSON response construction cannot rerun resolution or cross a mutation boundary.
    internal ProjectResolutionResult ProjectResolutionResult { get; set; }
    // Server-side only. Typed test-input validation failures are reported
    // without persisting a future configuration error into the current state.
    internal string TestInputErrorCode { get; set; }
    internal string TestInputError { get; set; }
}

internal static class CoordinatorClient
{
    // Test-only seams keep the lazy-start contract observable without spawning
    // a second test runner. Production always uses the process path and starter
    // from the current invocation.
    internal static Func<string> ProcessPathProviderForTests { get; set; }
    internal static Action<ProcessStartInfo> ProcessStarterForTests { get; set; }

    internal static void StartServerForTests(string root, string runtimeSlotId, string ticketId) =>
        StartServer(root, runtimeSlotId, ticketId);

    internal static int Run(string root, IReadOnlyList<string> command, string runtimeSlotId = null,
        string ticketId = null, Action<string> receivedLine = null,
        TimeSpan? terminalResponseTimeout = null)
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
                McpRequestId = Environment.GetEnvironmentVariable("DEVBRIDGE_MCP_REQUEST"),
                SessionId = Environment.GetEnvironmentVariable("DEVBRIDGE_SESSION")
            };
            writer.WriteLine(JsonSerializer.Serialize(request, Program.JsonOptions));

            bool bounded = CoordinatorResponsePolicy.IsFinite(normalizedCommand[0],
                normalizedCommand.Skip(1).ToList());
            using CancellationTokenSource responseTimeout = bounded
                ? new CancellationTokenSource(terminalResponseTimeout ?? CoordinatorResponsePolicy.FiniteTimeout)
                : null;
            while (true)
            {
                string line;
                try
                {
                    line = responseTimeout == null
                        ? reader.ReadLine()
                        : reader.ReadLineAsync(responseTimeout.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (responseTimeout?.IsCancellationRequested == true)
                {
                    throw new IOException(CoordinatorResponsePolicy.TimeoutMessage(
                        normalizedCommand[0]));
                }

                if (line == null)
                    break;
                receivedLine?.Invoke(line);
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
        string processPath = ProcessPathProviderForTests?.Invoke() ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("the coordinator process path is unavailable");

        ProcessStartInfo start = new()
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            // A detached coordinator must not inherit redirected pipes that no
            // process drains. A full stdout/stderr buffer otherwise freezes the
            // server after a diagnostic write, leaving clients without an end
            // marker even when the durable operation already completed.
            RedirectStandardOutput = false,
            RedirectStandardError = false
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
        if (ProcessStarterForTests != null)
            ProcessStarterForTests(start);
        else
            Process.Start(start)?.Dispose();
    }
}

internal static class CoordinatorServer
{
    private const int MaxPipeInstances = 16;
    private static readonly TimeSpan ShutdownClientGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownWorkerGracePeriod = TimeSpan.FromSeconds(10);

    private sealed class ClientSession
    {
        internal ClientSession(NamedPipeServerStream pipe) => Pipe = pipe;

        internal NamedPipeServerStream Pipe { get; }
        internal Task Handler { get; set; }
        internal ManualResetEventSlim Completed { get; } = new(false);
        internal bool IsShutdownRequest { get; set; }
        internal bool TerminalWritten { get; set; }
    }

    internal static int Run(string root, string runtimeSlotId = null, string ticketId = null,
        CoordinatorOptions optionsOverride = null, Action<CoordinatorState> started = null)
    {
        string slot = runtimeSlotId ?? RuntimeScope.ResolveTicketSlot(root, ticketId) ?? RuntimeScope.ForRoot(root);
        Mutex mutex = new(false, PipeNames.MutexForSlot(slot));
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
        {
            mutex.Dispose();
            return 0;
        }

        CoordinatorState state = null;
        try
        {
            CoordinatorOptions production = optionsOverride ?? CoordinatorOptions.ForProduction();
            state = new(root, optionsOverride ?? new CoordinatorOptions
            {
                CoordinatorRoot = root,
                RuntimeSlotId = slot,
                ReadinessTimeout = production.ReadinessTimeout,
                RimBridgeMode = production.RimBridgeMode,
                PlayerLogPath = production.PlayerLogPath
            });
            state.StartRecoveryWork();
            started?.Invoke(state);

            using CancellationTokenSource acceptShutdown = new();
            List<ClientSession> clients = new();
            object clientsGate = new();
            bool shutdownRequested = false;
            ClientSession shutdownRequester = null;

            void RequestShutdown(ClientSession requester)
            {
                lock (clientsGate)
                {
                    if (!shutdownRequested)
                    {
                        shutdownRequested = true;
                        shutdownRequester = requester;
                        acceptShutdown.Cancel();
                    }
                }
            }

            while (!acceptShutdown.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeNames.ForSlot(root, slot), PipeDirection.InOut,
                        MaxPipeInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(acceptShutdown.Token).GetAwaiter().GetResult();
                    if (acceptShutdown.IsCancellationRequested)
                        break;

                    ClientSession session = new(server);
                    server = null;
                    lock (clientsGate)
                        clients.Add(session);
                    session.Handler = Task.Run(() => HandleClient(state, session, RequestShutdown));
                }
                catch (OperationCanceledException) when (acceptShutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (acceptShutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (acceptShutdown.IsCancellationRequested)
                        break;
                    Console.Error.WriteLine("DevBridge server pipe error: " + exception.Message);
                    Thread.Sleep(250);
                }
                finally
                {
                    server?.Dispose();
                }
            }

            if (shutdownRequested)
                FinishShutdown(state, clients, shutdownRequester);
            else
                state.RequestShutdown();
            return 0;
        }
        finally
        {
            state?.Shutdown(ShutdownWorkerGracePeriod);
            if (ownsMutex)
            {
                try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
            }
            mutex.Dispose();
        }
    }

    private static void FinishShutdown(CoordinatorState state, List<ClientSession> clients,
        ClientSession shutdownRequester)
    {
        state.RequestShutdown();
        DateTime graceDeadline = DateTime.UtcNow.Add(ShutdownClientGracePeriod);
        while (DateTime.UtcNow < graceDeadline)
        {
            ClientSession[] pending = clients.Where(value => !value.Completed.IsSet &&
                (value.IsShutdownRequest || value == shutdownRequester)).ToArray();
            if (pending.Length == 0)
                break;
            int remaining = Math.Max(1, (int)(graceDeadline - DateTime.UtcNow).TotalMilliseconds);
            Task[] handlers = pending.Select(value => value.Handler).Where(value => value != null).ToArray();
            if (handlers.Length == 0 || Task.WaitAll(handlers, Math.Min(remaining, 100)))
                continue;
        }

        // A long-running command that was already accepted is deliberately
        // disconnected at shutdown. Its durable state remains authoritative;
        // the client receives the bounded reconnect guidance from CoordinatorClient.
        foreach (ClientSession session in clients)
        {
            if (!session.IsShutdownRequest && session != shutdownRequester)
                session.Pipe.Dispose();
        }

        DateTime handlerDeadline = DateTime.UtcNow.Add(ShutdownClientGracePeriod);
        while (DateTime.UtcNow < handlerDeadline && clients.Any(value =>
            !value.Completed.IsSet && value != shutdownRequester))
        {
            foreach (ClientSession session in clients.Where(value =>
                !value.Completed.IsSet && value != shutdownRequester))
                session.Pipe.Dispose();
            Thread.Sleep(25);
        }

        // The requester is never disconnected after a successful terminal
        // write. This ordering is what makes `coordinator shutdown --json`
        // reliable even when another client is in a durable wait.
        if (shutdownRequester != null && !shutdownRequester.Completed.IsSet)
        {
            DateTime requesterDeadline = DateTime.UtcNow.Add(ShutdownClientGracePeriod);
            while (DateTime.UtcNow < requesterDeadline && !shutdownRequester.Completed.IsSet)
                shutdownRequester.Completed.Wait(25);
            if (!shutdownRequester.Completed.IsSet)
                shutdownRequester.Pipe.Dispose();
        }
    }

    private static bool IsShutdownRequest(BridgeRequest request)
    {
        return request != null && string.Equals(request.Command, "coordinator",
            StringComparison.OrdinalIgnoreCase) && request.Arguments.Count > 0 &&
            (string.Equals(request.Arguments[0], "shutdown", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(request.Arguments[0], "reload", StringComparison.OrdinalIgnoreCase));
    }

    private static void HandleClient(CoordinatorState state, ClientSession session,
        Action<ClientSession> requestShutdown)
    {
        NamedPipeServerStream pipe = session.Pipe;
        try
        {
            using (pipe)
            using (StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
            using (StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true))
            {
                writer.AutoFlush = true;
                ClientOutput output = new(writer, CoordinatorResponsePolicy.WriteTimeout);
                BridgeRequest request = null;
                bool terminalWritten = false;
                try
                {
                    string requestLine = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        output.Write("Invalid empty coordinator request.");
                        terminalWritten = output.Write("__DEVBRIDGE_END__|2");
                        return;
                    }

                    request = JsonSerializer.Deserialize<BridgeRequest>(requestLine, Program.JsonOptions);
                    if (request == null || string.IsNullOrWhiteSpace(request.Command))
                    {
                        output.Write("Invalid coordinator request.");
                        terminalWritten = output.Write("__DEVBRIDGE_END__|2");
                        return;
                    }
                    request.Arguments ??= new List<string>();
                    request.Agent = string.IsNullOrWhiteSpace(request.Agent) ? "unknown-agent" : request.Agent.Trim();
                    session.IsShutdownRequest = IsShutdownRequest(request);

                    List<string> buffered = request.Json ? new List<string>() : null;
                    Action<string> emit = request.Json ? buffered.Add : value => { output.Write(value); };
                    int exitCode = state.Execute(request, emit, () => output.Connected && pipe.IsConnected);
                    if (request.Json)
                        output.Write(JsonSerializer.Serialize(state.CreateJsonResponse(request, exitCode, buffered),
                            Program.JsonOptions));
                    terminalWritten = output.Write("__DEVBRIDGE_END__|" + exitCode);
                    session.TerminalWritten = terminalWritten;
                    if (session.IsShutdownRequest && exitCode == 0 && terminalWritten)
                        requestShutdown(session);
                }
                catch (Exception exception)
                {
                    if (!terminalWritten && request?.Json == true)
                    {
                        output.Write(JsonSerializer.Serialize(JsonCommandResponse.Failure(
                            request.Command, exception.Message, "Run: DevBridge.cmd doctor"), Program.JsonOptions));
                    }
                    else if (!terminalWritten)
                        output.Write("DevBridge coordinator error: " + exception.Message);
                    if (!terminalWritten)
                        terminalWritten = output.Write("__DEVBRIDGE_END__|2");
                }
            }
        }
        finally
        {
            session.Completed.Set();
        }
    }
}

internal sealed class ClientOutput
{
    private readonly StreamWriter writer;
    private readonly TimeSpan writeTimeout;

    internal ClientOutput(StreamWriter writer, TimeSpan writeTimeout)
    {
        this.writer = writer;
        this.writeTimeout = writeTimeout;
    }

    internal bool Connected { get; private set; } = true;

    internal bool Write(string line)
    {
        if (!Connected)
            return false;

        try
        {
            writer.WriteLineAsync(line ?? string.Empty).WaitAsync(writeTimeout)
                .GetAwaiter().GetResult();
            return true;
        }
        catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException ||
                                           exception is TimeoutException || exception is InvalidOperationException)
        {
            Connected = false;
            return false;
        }
    }
}

internal static class CoordinatorResponsePolicy
{
    internal static readonly TimeSpan FiniteTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    internal static bool IsFinite(string command, IReadOnlyList<string> arguments)
    {
        if (string.Equals(command, "wait-ready", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "restart", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) && arguments.Count > 0 &&
            (string.Equals(arguments[0], "begin", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(arguments[0], "session", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    internal static string TimeoutMessage(string command)
    {
        return "The coordinator did not complete the finite '" + command +
            "' response within the response deadline. The operation may already be accepted; " +
            "reconnect with 'DevBridge.cmd status' or 'DevBridge.cmd doctor'. No durable operation was cancelled or rolled back.";
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
    // Version 0 is the supported pre-schema format. It is upgraded in place
    // after the rest of the state has been validated.
    public int SchemaVersion { get; set; }
    public string CoordinatorRoot { get; set; }
    public string RuntimeSlotId { get; set; }
    public int Generation { get; set; }
    public BridgePhase Phase { get; set; } = BridgePhase.STOPPED;
    public string Error { get; set; }
    public int TerminalFailureSchemaVersion { get; set; }
    public string TerminalFailurePhase { get; set; }
    public string TerminalFailureCode { get; set; }
    public string TerminalFailureDetail { get; set; }
    public string TerminalFailureExceptionType { get; set; }
    public string TerminalFailureExceptionMessage { get; set; }
    public string TerminalFailureDiagnosticDetail { get; set; }
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
    // ProfileMode is the resolver's legacy/baseline/projects value. LaunchProfileMode
    // is the operator-facing contract and deliberately distinguishes the aggregate
    // control profile from explicit human legacy mode.
    public string LaunchProfileMode { get; set; } = "explicit-human-legacy";
    public List<string> RequestedProjects { get; set; } = new();
    public List<string> ResolvedProjectPackageIds { get; set; } = new();
    public List<string> ResolvedMods { get; set; } = new();
    public List<TestInputValue> TestInputs { get; set; } = new();
    public string ProfileFingerprint { get; set; }
    public string BaselineFingerprint { get; set; }
    public RimBridgeMode RimBridgeMode { get; set; } = RimBridgeMode.Off;
    public string RimBridgeVersion { get; set; }
    public string RimBridgeResolutionErrorCode { get; set; }
    public string RimBridgeResolutionError { get; set; }
    public string ModsConfigOwnership { get; set; }
    public string ModsConfigGeneratedHash { get; set; }
    public string ModsConfigGeneratedProfileFingerprint { get; set; }
    public int ModsConfigGeneratedGeneration { get; set; }
    public string ModsConfigMutationAuthority { get; set; } =
        ModsConfigMutationAuthorityValues.NotGenerationOwned;
    public ModsConfigMutationEvidence ExternalModsConfigMutation { get; set; }
    public RimBridgePolicyState RimBridgePolicy { get; set; } = RimBridgePolicyState.CreateDefault();
    // These fields make an authorized write recoverable across a coordinator
    // crash.  The generated fields may already describe the target profile
    // when the transition is persisted, so an abort must be able to restore
    // the previously accepted ownership evidence instead of treating the
    // old file as an external mutation.
    public string ModsConfigTransitionSourceFingerprint { get; set; }
    public string ModsConfigTransitionPreviousAuthority { get; set; }
    public string ModsConfigTransitionPreviousOwnership { get; set; }
    public string ModsConfigTransitionPreviousHash { get; set; }
    public string ModsConfigTransitionPreviousProfileFingerprint { get; set; }
    public int ModsConfigTransitionPreviousGeneration { get; set; }
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
    public List<ProjectIntentRegistration> ProjectIntents { get; set; } = new();
    public bool AggregateFreezePending { get; set; }
    public DateTime? AggregateFreezeRequestedUtc { get; set; }
    public DateTime? AggregateFrozenUtc { get; set; }
    public int FrozenTargetGeneration { get; set; }
    public string FrozenLaunchOwner { get; set; }
    public string FrozenLaunchRequestKey { get; set; }
    public List<ProjectIntentSnapshot> FrozenRegistrations { get; set; } = new();
    public List<string> FrozenRequestedProjects { get; set; } = new();
    public List<string> FrozenResolvedProjectPackageIds { get; set; } = new();
    public List<string> FrozenResolvedMods { get; set; } = new();
    public List<TestInputValue> FrozenTestInputs { get; set; } = new();
    public string FrozenProfileFingerprint { get; set; }
    public string FrozenBaselineFingerprint { get; set; }
    public List<AggregateGenerationEvidence> AggregateGenerations { get; set; } = new();
    public RimBridgeIntegrationState RimBridge { get; set; } = new();
}

internal sealed class ProjectIntentRegistration
{
    public string Id { get; set; }
    public string Owner { get; set; }
    public string SessionId { get; set; }
    public int ClientProcessId { get; set; }
    public List<string> RequestedProjects { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public DateTime? ReleasedUtc { get; set; }
    public string ReleaseReason { get; set; }
}

internal sealed class ProjectIntentSnapshot
{
    public string Id { get; set; }
    public string Owner { get; set; }
    public string SessionId { get; set; }
    public List<string> RequestedProjects { get; set; } = new();
}

internal sealed class AggregateGenerationEvidence
{
    public int Generation { get; set; }
    public DateTime FrozenUtc { get; set; }
    public string LaunchOwner { get; set; }
    public string LaunchRequestKey { get; set; }
    public string ProfileMode { get; set; }
    public List<ProjectIntentSnapshot> Registrations { get; set; } = new();
    public List<string> RequestedProjects { get; set; } = new();
    public List<string> ResolvedProjectPackageIds { get; set; } = new();
    public List<string> ResolvedMods { get; set; } = new();
    public List<TestInputValue> TestInputs { get; set; } = new();
    public string ProfileFingerprint { get; set; }
    public string BaselineFingerprint { get; set; }
}

internal sealed class PersistedProfileSnapshot
{
    public string Mode { get; set; } = ModProfile.LegacyMode;
    public List<string> RequestedProjects { get; set; } = new();
    public List<string> ResolvedProjectPackageIds { get; set; } = new();
    public List<string> ResolvedMods { get; set; } = new();
    public List<TestInputValue> TestInputs { get; set; } = new();
    public string ProfileFingerprint { get; set; }
    public string BaselineFingerprint { get; set; }
    public RimBridgeMode RimBridgeMode { get; set; } = RimBridgeMode.Off;
    public string RimBridgeVersion { get; set; }
    public string RimBridgeResolutionErrorCode { get; set; }
    public string RimBridgeResolutionError { get; set; }

    internal ModProfile ToModProfile() => new()
    {
        Mode = Mode,
        RequestedProjects = (RequestedProjects ?? new List<string>()).ToList(),
        ResolvedProjectPackageIds = (ResolvedProjectPackageIds ?? new List<string>()).ToList(),
        ResolvedMods = (ResolvedMods ?? new List<string>()).ToList(),
        TestInputs = TestGenerationInputs.CloneValues(TestInputs),
        ProfileFingerprint = ProfileFingerprint,
        BaselineFingerprint = BaselineFingerprint,
        RimBridgeMode = RimBridgeMode,
        RimBridgeVersion = RimBridgeVersion,
        RimBridgeResolutionErrorCode = RimBridgeResolutionErrorCode,
        RimBridgeResolutionError = RimBridgeResolutionError
    };

    internal static PersistedProfileSnapshot FromModProfile(ModProfile profile) => profile == null ? null : new()
    {
        Mode = profile.Mode,
        RequestedProjects = (profile.RequestedProjects ?? new List<string>()).ToList(),
        ResolvedProjectPackageIds = (profile.ResolvedProjectPackageIds ?? new List<string>()).ToList(),
        ResolvedMods = (profile.ResolvedMods ?? new List<string>()).ToList(),
        TestInputs = TestGenerationInputs.CloneValues(profile.TestInputs),
        ProfileFingerprint = profile.ProfileFingerprint,
        BaselineFingerprint = profile.BaselineFingerprint,
        RimBridgeMode = profile.RimBridgeMode,
        RimBridgeVersion = profile.RimBridgeVersion,
        RimBridgeResolutionErrorCode = profile.RimBridgeResolutionErrorCode,
        RimBridgeResolutionError = profile.RimBridgeResolutionError
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
    public int OriginalFailureSchemaVersion { get; set; }
    public string OriginalFailureExceptionType { get; set; }
    public string OriginalFailureExceptionMessage { get; set; }
    public string OriginalFailureDiagnosticDetail { get; set; }
    public bool OriginalProcessExitObserved { get; set; }
    public string OriginalExitInformation { get; set; }
    public Dictionary<string, string> OriginalDiagnosticMetadata { get; set; } = new();
    public List<ProjectIntentSnapshot> OriginalRegistrations { get; set; } = new();
    public Dictionary<string, List<ProjectIntentRequester>> ProjectRequesters { get; set; } = new();

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

internal sealed class ProjectIntentRequester
{
    public string RegistrationId { get; set; }
    public string Owner { get; set; }
    public string SessionId { get; set; }
}

internal sealed class GeneratedModsConfigManifest
{
    // Version 0 is the legacy unmarked manifest format.
    public int SchemaVersion { get; set; }
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

    [JsonPropertyName("launchProfileMode")]
    public string LaunchProfileMode { get; set; }

    [JsonPropertyName("resolverProfileMode")]
    public string ResolverProfileMode { get; set; }

    [JsonPropertyName("requestedProjects")]
    public List<string> RequestedProjects { get; set; } = new();

    [JsonPropertyName("resolvedProjectPackageIds")]
    public List<string> ResolvedProjectPackageIds { get; set; } = new();

    [JsonPropertyName("resolvedMods")]
    public List<string> ResolvedMods { get; set; } = new();

    [JsonPropertyName("testInputs")]
    public List<TestInputValue> TestInputs { get; set; } = new();

    [JsonPropertyName("profileFingerprint")]
    public string ProfileFingerprint { get; set; }

    [JsonPropertyName("baselineFingerprint")]
    public string BaselineFingerprint { get; set; }

    [JsonPropertyName("rimBridge")]
    public RimBridgeIntegrationState RimBridge { get; set; }

    // Populated only by the explicit `bridge endpoint` command. Ordinary status,
    // doctor, and lifecycle responses leave this null so credentials cannot leak.
    [JsonPropertyName("rimBridgeEndpoint")]
    public JsonRimBridgeEndpoint RimBridgeEndpoint { get; set; }

    [JsonPropertyName("modsConfigOwnership")]
    public string ModsConfigOwnership { get; set; }

    [JsonPropertyName("modsConfigMutationAuthority")]
    public string ModsConfigMutationAuthority { get; set; }

    [JsonPropertyName("externalModsConfigMutation")]
    public ModsConfigMutationEvidence ExternalModsConfigMutation { get; set; }

    [JsonPropertyName("rimBridgePolicy")]
    public RimBridgePolicyState RimBridgePolicy { get; set; }

    [JsonPropertyName("rimBridgeRoute")]
    public JsonRimBridgeRoute RimBridgeRoute { get; set; }

    [JsonPropertyName("profileConflict")]
    public string ProfileConflict { get; set; }

    [JsonPropertyName("profileStrategy")]
    public string ProfileStrategy { get; set; }

    [JsonPropertyName("aggregateAllowed")]
    public bool AggregateAllowed { get; set; }

    [JsonPropertyName("terminalFailurePhase")]
    public string TerminalFailurePhase { get; set; }

    [JsonPropertyName("terminalFailureCode")]
    public string TerminalFailureCode { get; set; }

    [JsonPropertyName("terminalFailureDetail")]
    public string TerminalFailureDetail { get; set; }

    [JsonPropertyName("terminalFailureExceptionType")]
    public string TerminalFailureExceptionType { get; set; }

    [JsonPropertyName("terminalFailureExceptionMessage")]
    public string TerminalFailureExceptionMessage { get; set; }

    [JsonPropertyName("terminalFailureDiagnosticDetail")]
    public string TerminalFailureDiagnosticDetail { get; set; }

    [JsonPropertyName("runtimeProfileFingerprint")]
    public string RuntimeProfileFingerprint { get; set; }

    [JsonPropertyName("crashIsolation")]
    public CrashIsolationIncident CrashIsolation { get; set; }

    [JsonPropertyName("frozenGeneration")]
    public int FrozenGeneration { get; set; }

    [JsonPropertyName("frozenRequestedProjects")]
    public List<string> FrozenRequestedProjects { get; set; } = new();

    [JsonPropertyName("frozenResolvedProjectPackageIds")]
    public List<string> FrozenResolvedProjectPackageIds { get; set; } = new();

    [JsonPropertyName("frozenResolvedMods")]
    public List<string> FrozenResolvedMods { get; set; } = new();

    [JsonPropertyName("frozenTestInputs")]
    public List<TestInputValue> FrozenTestInputs { get; set; } = new();

    [JsonPropertyName("frozenProfileFingerprint")]
    public string FrozenProfileFingerprint { get; set; }

    [JsonPropertyName("frozenBaselineFingerprint")]
    public string FrozenBaselineFingerprint { get; set; }

    [JsonPropertyName("aggregateFreezePending")]
    public bool AggregateFreezePending { get; set; }

    [JsonPropertyName("frozenLaunchOwner")]
    public string FrozenLaunchOwner { get; set; }

    [JsonPropertyName("frozenLaunchRequestKey")]
    public string FrozenLaunchRequestKey { get; set; }

    [JsonPropertyName("frozenRegistrationIds")]
    public List<string> FrozenRegistrationIds { get; set; } = new();

    [JsonPropertyName("frozenRegistrations")]
    public List<ProjectIntentSnapshot> FrozenRegistrations { get; set; } = new();

    [JsonPropertyName("activeProjectIntents")]
    public List<ProjectIntentRegistrationInfo> ActiveProjectIntents { get; set; } = new();

    [JsonPropertyName("queuedProjectIntents")]
    public List<ProjectIntentRegistrationInfo> QueuedProjectIntents { get; set; } = new();

    [JsonPropertyName("aggregateGenerations")]
    public List<AggregateGenerationEvidence> AggregateGenerations { get; set; } = new();

    [JsonPropertyName("missingProjects")]
    public List<string> MissingProjects { get; set; } = new();

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

    // Doctor-only fields. Null omission keeps the established status and
    // command response shapes unchanged for callers that do not request doctor.
    [JsonPropertyName("schemaVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SchemaVersion { get; set; }

    [JsonPropertyName("healthy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Healthy { get; set; }

    [JsonPropertyName("findings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DoctorFinding> Findings { get; set; }

    [JsonPropertyName("components")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ComponentVersionReport Components { get; set; }

    [JsonPropertyName("operationalState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DoctorOperationalState OperationalState { get; set; }

    [JsonPropertyName("nextActions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DoctorNextAction> NextActions { get; set; }

    [JsonPropertyName("generationHistory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationHistoryView GenerationHistory { get; set; }

    [JsonPropertyName("projectResolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectResolutionResult ProjectResolution { get; set; }

    [JsonPropertyName("currentGenerationTrust")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string CurrentGenerationTrust { get; set; }

    [JsonPropertyName("nextGenerationConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConfigurationHealth NextGenerationConfig { get; set; }

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

internal sealed class ProjectIntentRegistrationInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("owner")]
    public string Owner { get; set; }

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; }

    [JsonPropertyName("requestedProjects")]
    public List<string> RequestedProjects { get; set; } = new();

    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("lastHeartbeatUtc")]
    public DateTime LastHeartbeatUtc { get; set; }

    [JsonPropertyName("expiresUtc")]
    public DateTime ExpiresUtc { get; set; }

    [JsonPropertyName("releasedUtc")]
    public DateTime? ReleasedUtc { get; set; }

    [JsonPropertyName("releaseReason")]
    public string ReleaseReason { get; set; }

    [JsonPropertyName("clientProcessId")]
    public int ClientProcessId { get; set; }
}

internal sealed class ReadinessRecord
{
    // Version 0 is the legacy unmarked readiness format.
    public int SchemaVersion { get; set; }
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
    internal TimeSpan ProcessInspectionRetryTimeout { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan ProcessExitTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal int MaxLaunchAttempts { get; init; } = 2;
    // Isolation launches are bounded separately from user-requested launches:
    // delta debugging can legitimately need more attempts than a normal retry.
    internal int IsolationMaxAttempts { get; init; } = 64;
    internal TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    internal TimeSpan LeaseHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan LeaseSessionPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan ProjectIntentDuration { get; init; } = TimeSpan.FromMinutes(10);
    internal TimeSpan LeaseProgressInterval { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan RimBridgeCallTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal IProcessAdapter ProcessAdapter { get; init; } = new SystemProcessAdapter();
    internal ICoordinatorClock Clock { get; init; } = SystemCoordinatorClock.Instance;
    internal string RimWorldExecutablePath { get; init; }
    internal string ModsConfigPath { get; init; }
    internal string CoordinatorRoot { get; init; }
    internal string RuntimeSlotId { get; init; }
    internal IReadOnlyList<string> InstalledModsRoots { get; init; }
    internal RimBridgeMode RimBridgeMode { get; init; } = RimBridgeMode.Off;
    internal string PlayerLogPath { get; init; }
    internal IRimBridgeClient RimBridgeClient { get; init; }
    internal IRimBridgeGenerationVerifier RimBridgeGenerationVerifier { get; init; }
    internal Action BeforeModsConfigWrite { get; init; }

    internal static CoordinatorOptions ForProduction()
    {
        TimeSpan timeout = TimeSpan.FromMinutes(6);
        string configured = Environment.GetEnvironmentVariable("DEVBRIDGE_READINESS_TIMEOUT_SECONDS");
        if (int.TryParse(configured, out int seconds) && seconds >= 30 && seconds <= 3600)
            timeout = TimeSpan.FromSeconds(seconds);

        return new CoordinatorOptions
        {
            ReadinessTimeout = timeout,
            RimBridgeMode = RimBridgeModes.Parse(
                Environment.GetEnvironmentVariable("DEVBRIDGE_RIMBRIDGE_MODE")),
            PlayerLogPath = Environment.GetEnvironmentVariable("DEVBRIDGE_PLAYER_LOG")
        };
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
    internal RimBridgeMode RimBridgeMode { get; init; } = RimBridgeMode.Off;
    internal string RimBridgeVersion { get; init; }
    internal string RimBridgeResolutionErrorCode { get; init; }
    internal string RimBridgeResolutionError { get; init; }
    internal List<ProfileModProvenance> Provenance { get; init; } = new();
    internal List<ProfileResolutionEdge> ResolutionEdges { get; init; } = new();
    internal List<TestInputValue> TestInputs { get; init; } = new();

    internal ModProfile Clone() => new()
    {
        Mode = Mode,
        RequestedProjects = RequestedProjects.ToList(),
        ResolvedProjectPackageIds = ResolvedProjectPackageIds.ToList(),
        ResolvedMods = ResolvedMods.ToList(),
        TestInputs = TestGenerationInputs.CloneValues(TestInputs),
        ProfileFingerprint = ProfileFingerprint,
        BaselineFingerprint = BaselineFingerprint,
        RimBridgeMode = RimBridgeMode,
        RimBridgeVersion = RimBridgeVersion,
        RimBridgeResolutionErrorCode = RimBridgeResolutionErrorCode,
        RimBridgeResolutionError = RimBridgeResolutionError,
        Provenance = Provenance.Select(value => new ProfileModProvenance
        {
            PackageId = value.PackageId,
            Reasons = (value.Reasons ?? new List<ProfileModReason>()).Select(reason => new ProfileModReason
            {
                Category = reason.Category,
                RelatedPackageId = reason.RelatedPackageId,
                Detail = reason.Detail
            }).ToList()
        }).ToList(),
        ResolutionEdges = ResolutionEdges.Select(value => new ProfileResolutionEdge
        {
            FromPackageId = value.FromPackageId,
            ToPackageId = value.ToPackageId,
            Kind = value.Kind
        }).ToList()
    };
}

internal sealed class InstalledModMetadata
{
    internal string PackageId { get; init; }
    internal string DirectoryPath { get; init; }
    internal string Version { get; init; }
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
        "astryl.moderndevtools",
        RimBridgeIntegrationConstants.PackageId
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
        IReadOnlyList<string> aliases, IReadOnlyList<string> configuredRoots = null,
        RimBridgeMode rimBridgeMode = RimBridgeMode.Off,
        IEnumerable<TestInputAssignment> testInputAssignments = null)
    {
        if (string.IsNullOrWhiteSpace(baselineFingerprint))
            throw new ProfileException("PROFILE_BASELINE_MISSING",
                "Capture the user ModsConfig first with: DevBridge.cmd mods capture-baseline");

        List<string> canonicalAliases = CanonicalAliases(aliases).ToList();
        string mode = canonicalAliases.Count == 0 ? ModProfile.BaselineMode : ModProfile.ProjectsMode;
        TestInputSet testInputs = TestGenerationInputs.Normalize(testInputAssignments, mode);
        List<string> requestedPackageIds = canonicalAliases
            .Select(alias => ProjectAliases[alias])
            .ToList();

        Dictionary<string, List<InstalledModMetadata>> installed = Discover(coordinatorRoot, configuredRoots);
        RimBridgeProfileDecision rimBridge = RimBridgeProfilePolicy.Decide(rimBridgeMode, installed);
        List<string> roots = AlwaysOnPackageIds
            .Concat(requestedPackageIds)
            .ToList();
        Dictionary<string, InstalledModMetadata> resolved = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> rootPackageIds = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> visiting = new(StringComparer.OrdinalIgnoreCase);
        List<string> stack = new();
        Dictionary<string, int> discoveryOrder = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> rootOrder = new(StringComparer.OrdinalIgnoreCase);
        List<ProfileResolutionEdge> resolutionEdges = new();
        int discovery = 0;

        for (int index = 0; index < roots.Count; index++)
        {
            InstalledModMetadata root = Find(installed, roots[index], "project root");
            rootOrder.TryAdd(root.PackageId, index);
            rootPackageIds.TryAdd(root.PackageId, roots[index]);
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
                AddEdge(dependencyMetadata.PackageId, metadata.PackageId, "DEPENDENCY_OF");
            }

            foreach (string before in metadata.LoadBefore)
            {
                if (resolved.TryGetValue(before, out InstalledModMetadata target))
                    AddEdge(metadata.PackageId, target.PackageId, "LOAD_ORDER_CONSTRAINT");
            }

            foreach (string after in metadata.LoadAfter)
            {
                if (resolved.TryGetValue(after, out InstalledModMetadata target))
                    AddEdge(target.PackageId, metadata.PackageId, "LOAD_ORDER_CONSTRAINT");
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

        List<string> resolvedMods = orderedKeys
            .Select(key => rootPackageIds.TryGetValue(key, out string rootPackageId)
                ? rootPackageId
                : resolved[key].PackageId)
            .ToList();
        List<string> resolvedProjects = requestedPackageIds.ToList();
        string fingerprint = Fingerprint(mode, baselineFingerprint, canonicalAliases, resolvedProjects,
            resolvedMods, rimBridgeMode, testInputs.Values);
        List<ProfileModProvenance> provenance = resolvedMods.Select(packageId =>
        {
            List<ProfileModReason> reasons = new();
            if (AlwaysOnPackageIds.Any(value => string.Equals(value, packageId, StringComparison.OrdinalIgnoreCase)))
            {
                reasons.Add(new ProfileModReason
                {
                    Category = packageId.StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase)
                        ? "OFFICIAL_CONTENT" : "CONTROL_REQUIRED",
                    Detail = packageId.StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase)
                        ? "Required official content root." : "Required DevBridge/tooling root."
                });
            }
            if (string.Equals(packageId, RimBridgeIntegrationConstants.PackageId, StringComparison.OrdinalIgnoreCase))
                reasons.Add(new ProfileModReason
                {
                    Category = "OTHER_REQUIRED_BASELINE",
                    Detail = "Required by the base profile; endpoint participation follows the selected RimBridge policy."
                });
            if (requestedPackageIds.Any(value => string.Equals(value, packageId, StringComparison.OrdinalIgnoreCase)))
                reasons.Add(new ProfileModReason
                {
                    Category = "PROJECT_ROOT",
                    Detail = "Requested project root."
                });
            foreach (ProfileResolutionEdge edge in resolutionEdges.Where(value =>
                         string.Equals(value.FromPackageId, packageId, StringComparison.OrdinalIgnoreCase)))
            {
                reasons.Add(new ProfileModReason
                {
                    Category = edge.Kind,
                    RelatedPackageId = edge.ToPackageId,
                    Detail = edge.Kind == "DEPENDENCY_OF"
                        ? "Required by this resolved project/root." : "Pinned load-order relationship."
                });
            }
            foreach (ProfileResolutionEdge edge in resolutionEdges.Where(value =>
                         string.Equals(value.ToPackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                         value.Kind == "LOAD_ORDER_CONSTRAINT"))
            {
                reasons.Add(new ProfileModReason
                {
                    Category = edge.Kind,
                    RelatedPackageId = edge.FromPackageId,
                    Detail = "Pinned load-order relationship."
                });
            }
            if (reasons.Count == 0)
                reasons.Add(new ProfileModReason
                {
                    Category = "OTHER_REQUIRED_BASELINE",
                    Detail = "Required by the resolved baseline closure."
                });
            return new ProfileModProvenance
            {
                PackageId = packageId,
                Reasons = reasons
                    .OrderBy(value => value.Category, StringComparer.Ordinal)
                    .ThenBy(value => value.RelatedPackageId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value.Detail, StringComparer.Ordinal)
                    .ToList()
            };
        }).ToList();
        ModProfile profile = new()
        {
            Mode = mode,
            RequestedProjects = canonicalAliases,
            ResolvedProjectPackageIds = resolvedProjects,
            ResolvedMods = resolvedMods,
            ProfileFingerprint = fingerprint,
            BaselineFingerprint = baselineFingerprint,
            RimBridgeMode = rimBridgeMode,
            RimBridgeVersion = rimBridge.Version,
            RimBridgeResolutionErrorCode = rimBridge.ErrorCode,
            RimBridgeResolutionError = rimBridge.Error,
            TestInputs = TestGenerationInputs.CloneValues(testInputs.Values),
            Provenance = provenance,
            ResolutionEdges = resolutionEdges
                .OrderBy(value => value.FromPackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.ToPackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Kind, StringComparer.Ordinal)
                .ToList()
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

        void AddEdge(string from, string to, string kind)
        {
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) || !edges.ContainsKey(from) ||
                !edges.ContainsKey(to))
                return;
            if (resolutionEdges.All(value =>
                    !string.Equals(value.FromPackageId, from, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(value.ToPackageId, to, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(value.Kind, kind, StringComparison.Ordinal)))
                resolutionEdges.Add(new ProfileResolutionEdge
                {
                    FromPackageId = from,
                    ToPackageId = to,
                    Kind = kind
                });
            if (edges[from].Add(to))
                indegree[to]++;
        }
    }

    internal static ModProfile CreateBaselineProfile(string baselineFingerprint) =>
        CreateBaselineProfile(baselineFingerprint, RimBridgeMode.Off, null, null);

    internal static ModProfile CreateBaselineProfile(string baselineFingerprint, RimBridgeMode rimBridgeMode,
        string coordinatorRoot, IReadOnlyList<string> configuredRoots,
        IEnumerable<TestInputAssignment> testInputAssignments = null)
    {
        if (string.IsNullOrWhiteSpace(baselineFingerprint))
            throw new ProfileException("PROFILE_BASELINE_MISSING",
                "The durable baseline fingerprint is missing; no control profile can be run.");

        Dictionary<string, List<InstalledModMetadata>> installed =
            rimBridgeMode == RimBridgeMode.Off
                ? null
                : Discover(coordinatorRoot, configuredRoots);
        RimBridgeProfileDecision rimBridge = RimBridgeProfilePolicy.Decide(rimBridgeMode, installed);
        TestInputSet testInputs = TestGenerationInputs.Normalize(testInputAssignments, ModProfile.BaselineMode);
        List<string> resolvedMods = AlwaysOnPackageIds.ToList();
        ModProfile profile = new()
        {
            Mode = ModProfile.BaselineMode,
            RequestedProjects = new List<string>(),
            ResolvedProjectPackageIds = new List<string>(),
            ResolvedMods = resolvedMods,
            ProfileFingerprint = Fingerprint(ModProfile.BaselineMode, baselineFingerprint,
                Array.Empty<string>(), Array.Empty<string>(), resolvedMods, rimBridgeMode),
            BaselineFingerprint = baselineFingerprint,
            RimBridgeMode = rimBridgeMode,
            RimBridgeVersion = rimBridge.Version,
            RimBridgeResolutionErrorCode = rimBridge.ErrorCode,
            RimBridgeResolutionError = rimBridge.Error,
            TestInputs = TestGenerationInputs.CloneValues(testInputs.Values)
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
            aliases, expectedProjects, resolvedMods, profile.RimBridgeMode, profile.TestInputs);
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
        IReadOnlyList<string> projectIds, IReadOnlyList<string> resolvedMods,
        RimBridgeMode rimBridgeMode = RimBridgeMode.Off,
        IEnumerable<TestInputValue> testInputs = null)
    {
        List<string> parts = new()
        {
            "mode=" + mode,
            "baseline=" + baselineFingerprint.ToUpperInvariant(),
            "projects=" + string.Join(",", aliases),
            "projectPackageIds=" + string.Join(",", projectIds.Select(value => value.ToLowerInvariant())),
            "mods=" + string.Join(",", resolvedMods.Select(value => value.ToLowerInvariant()))
        };
        IReadOnlyList<string> semanticInputs = TestGenerationInputs.SemanticFingerprintEntries(testInputs);
        if (semanticInputs.Count > 0)
            parts.Add("testInputs=" + string.Join(",", semanticInputs));
        // Non-off modes must still change the profile identity even though RimBridgeServer
        // is present in every base profile.
        if (rimBridgeMode != RimBridgeMode.Off)
            parts.Add("rimbridge=" + RimBridgeModes.Text(rimBridgeMode) + ":" +
                (resolvedMods.Any(value => string.Equals(value,
                    RimBridgeIntegrationConstants.PackageId, StringComparison.OrdinalIgnoreCase))
                    ? "included" : "absent"));
        string canonical = string.Join("\n", parts);
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
                        Document = document,
                        Version = document.Root?.Elements().FirstOrDefault(value =>
                            string.Equals(value.Name.LocalName, "modVersion", StringComparison.OrdinalIgnoreCase))?.Value.Trim()
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

        ReadReferences(root, "modDependencies", metadata.Dependencies, metadata);
        ReadReferences(root, "loadBefore", metadata.LoadBefore, metadata);
        ReadReferences(root, "loadAfter", metadata.LoadAfter, metadata);
    }

    private static void ReadReferences(XElement root, string sectionName, List<string> destination,
        InstalledModMetadata metadata)
    {
        List<XElement> sections = root.Elements().Where(value =>
            string.Equals(value.Name.LocalName, sectionName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sections.Count == 0)
            return;
        if (sections.Count > 1)
            throw new ProfileException("PROFILE_MALFORMED_METADATA",
                "Installed metadata for " + metadata.PackageId + " has multiple " + sectionName + " sections.");

        XElement section = sections[0];
        if (section.Nodes().Any(node => node switch
        {
            XElement element => !string.Equals(element.Name.LocalName, "li", StringComparison.OrdinalIgnoreCase),
            XText text => !string.IsNullOrWhiteSpace(text.Value),
            XComment => false,
            _ => true
        }))
            throw new ProfileException("PROFILE_MALFORMED_METADATA",
                "Installed metadata for " + metadata.PackageId + " has a malformed " + sectionName + " section.");

        foreach (XElement li in section.Elements())
        {
            List<XElement> elements = li.Elements().ToList();
            List<XElement> packages = elements.Where(value =>
                string.Equals(value.Name.LocalName, "packageId", StringComparison.OrdinalIgnoreCase)).ToList();
            string value;
            if (elements.Count == 0)
                value = li.Value.Trim();
            else
            {
                if (packages.Count != 1 ||
                    li.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
                    throw new ProfileException("PROFILE_MALFORMED_METADATA",
                        "Installed metadata for " + metadata.PackageId + " has a malformed " + sectionName + " entry.");

                XElement package = packages[0];
                if (package.Elements().Any() || package.Nodes().Any(node => node is not XText && node is not XComment))
                    throw new ProfileException("PROFILE_MALFORMED_METADATA",
                        "Installed metadata for " + metadata.PackageId + " has a malformed " + sectionName + " entry.");
                value = package.Value.Trim();
            }

            if (!IsValidReferencePackageId(value))
                throw new ProfileException("PROFILE_MALFORMED_METADATA",
                    "Installed metadata for " + metadata.PackageId + " has a malformed " + sectionName + " entry.");
            destination.Add(value);
        }
    }

    private static bool IsValidReferencePackageId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
            return false;
        if (!char.IsLetterOrDigit(value[0]) || !char.IsLetterOrDigit(value[^1]))
            return false;
        return value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
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

internal sealed partial class CoordinatorState
{
    private const string DevBridgePackageId = "lan.devbridge2";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    private readonly string root;
    private readonly string runtimeRoot;
    private readonly string statePath;
    private readonly string readinessPath;
    private readonly string quicktestFailurePath;
    private readonly string baselinePath;
    private readonly string generatedManifestPath;
    private readonly string generationsRoot;
    private readonly string generationHistoryPath;
    private readonly string rimWorldExe;
    private readonly string modsConfigPath;
    private readonly string rimBridgeLogPath;
    private readonly string coordinatorRoot;
    private readonly string runtimeSlotId;
    private readonly CoordinatorOptions options;
    private readonly IProcessAdapter processAdapter;
    private readonly ICoordinatorClock clock;
    private readonly IRimBridgeClient rimBridgeClient;
    private readonly IRimBridgeGenerationVerifier rimBridgeGenerationVerifier;
    private readonly object gate = new();
    private readonly object lifecycleGate = new();
    private readonly CancellationTokenSource shutdownCancellation = new();
    private PersistedState state;
    private bool persistedStateLoadBlocked;
    private int shutdownRequested;
    private Task restartTask;
    private Task launchTask;
    private Task isolationTask;

    internal TimeSpan ReadinessTimeoutForTesting => options.ReadinessTimeout;

    private sealed class MaintenanceValidation
    {
        internal bool Safe { get; init; }
        internal string ErrorCode { get; init; }
        internal string Error { get; init; }
    }

    internal bool ShutdownRequested => Volatile.Read(ref shutdownRequested) != 0;
    internal CancellationToken ShutdownToken => shutdownCancellation.Token;

    internal void RequestShutdown()
    {
        if (Interlocked.Exchange(ref shutdownRequested, 1) != 0)
            return;

        shutdownCancellation.Cancel();
        lock (gate)
            Monitor.PulseAll(gate);
    }

    private void ThrowIfShutdownRequested()
    {
        if (ShutdownRequested)
            throw new OperationCanceledException(shutdownCancellation.Token);
    }

    internal void Shutdown(TimeSpan timeout)
    {
        RequestShutdown();
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            Task[] tasks;
            lock (gate)
            {
                tasks = new[] { restartTask, launchTask, isolationTask }
                    .Where(value => value != null && !value.IsCompleted).Distinct().ToArray();
            }

            if (tasks.Length == 0)
                return;

            int remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            try
            {
                Task.WaitAll(tasks, Math.Min(remaining, 100));
            }
            catch (AggregateException)
            {
                // Worker failures are already represented in durable state. A
                // coordinator refresh must still release its slot mutex.
            }
        }
    }

    private sealed class RestartArguments
    {
        internal string LeaseId { get; init; }
        internal bool LegacyProduction { get; init; }
        internal bool HasProjects { get; init; }
        internal List<string> Projects { get; init; } = new();
        internal List<TestInputAssignment> TestInputs { get; init; } = new();
    }

    private sealed class ProcessStatusSnapshot
    {
        internal bool OwnedProcessRunning { get; init; }
        internal int MatchingProcessCount { get; init; }
        internal List<UnmanagedRimWorldProcess> UnmanagedProcesses { get; init; } = new();
    }

    private sealed class RimBridgeRoutePreparation
    {
        internal RimBridgeRouteContext Context { get; init; }
        internal RimBridgeRouteResult Failure { get; init; }
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
        rimBridgeClient = this.options.RimBridgeClient ?? new RimBridgeClient();
        rimBridgeGenerationVerifier = this.options.RimBridgeGenerationVerifier ??
            new RimBridgeCompanionGenerationVerifier();
        runtimeRoot = Path.Combine(this.root, "Runtime");
        statePath = Path.Combine(runtimeRoot, "state.json");
        readinessPath = Path.Combine(runtimeRoot, "readiness.json");
        quicktestFailurePath = QuicktestFailureArtifact.PathFor(this.root);
        baselinePath = Path.Combine(runtimeRoot, "ModsConfig.baseline.xml");
        generatedManifestPath = Path.Combine(runtimeRoot, "ModsConfig.generated.json");
        generationsRoot = Path.Combine(runtimeRoot, "generations");
        generationHistoryPath = Path.Combine(runtimeRoot, "generation-history.json");
        rimWorldExe = Path.GetFullPath(this.options.RimWorldExecutablePath ??
            Path.Combine(this.root, "..", "..", "RimWorldWin64.exe"));
        modsConfigPath = this.options.ModsConfigPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Config", "ModsConfig.xml");
        string modsConfigDirectory = Directory.GetParent(Path.GetFullPath(modsConfigPath))?.FullName;
        string rimWorldUserDataDirectory = Directory.GetParent(modsConfigDirectory ?? string.Empty)?.FullName
            ?? modsConfigDirectory ?? this.root;
        rimBridgeLogPath = Path.GetFullPath(this.options.PlayerLogPath ??
            Path.Combine(rimWorldUserDataDirectory, "Player.log"));
        Directory.CreateDirectory(runtimeRoot);

        lock (gate)
        {
            state = LoadState();
            NormalizeStateLocked();
            if (!persistedStateLoadBlocked && NormalizeRimBridgeStateLocked())
                SaveStateLocked();
        }
    }

    internal void StartRecoveryWork()
    {
        lock (gate)
        {
            if (persistedStateLoadBlocked)
                return;
            if (ExternalMutationBlocksLaunchLocked(null, "Recovery"))
                return;
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

}
