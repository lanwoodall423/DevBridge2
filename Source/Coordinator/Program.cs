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

    internal static bool IsRecipeCommand(BridgeRequest request)
    {
        IReadOnlyList<string> arguments = request?.Arguments ?? new List<string>();
        return string.Equals(request?.Command, "test", StringComparison.OrdinalIgnoreCase) &&
            arguments.Count > 0 && string.Equals(arguments[0], "recipe",
                StringComparison.OrdinalIgnoreCase);
    }

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

            // Legacy runtime-slot state cannot be loaded by the normal client
            // path. This guarded local maintenance command must run before
            // ResolveEffectiveSlot rejects the old namespace.
            if (CoordinatorLegacySlotMigration.IsCommand(parsed.Command))
                return CoordinatorLegacySlotMigration.Run(root, parsed.Command);

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
        Console.WriteLine("DevBridge commands: status | bridge status | bridge policy | bridge endpoint | bridge tools | bridge call <tool-name> [JSON arguments] [--lease <lease-id>] | project register <alias[,alias...]> | project status | project renew <registration-id> | project release <registration-id> | mods status | mods capture-baseline | mods restore-baseline | test begin | test session | test renew <lease-id> | test end <lease-id> | stop <lease-id> | coordinator shutdown | coordinator migrate-legacy-slot | ensure-ready <lease-id> | restart [--projects none|alias[,alias...]] [--legacy-production] | wait-ready | history [show <generation>|last-good] | doctor");
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
        string effectiveSlot = RuntimeScope.ResolveEffectiveSlot(root, runtimeSlotId, ticketId);
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
                ProtocolVersion = CoordinatorIpcProtocol.Version,
                RequestId = CoordinatorIpcProtocol.NewRequestId(),
                Type = CoordinatorIpcProtocol.RequestType,
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
            bool terminalSeen = false;
            while (true)
            {
                string line;
                try
                {
                    line = responseTimeout == null
                        ? CoordinatorIpcProtocol.ReadFrameLine(reader)
                        : CoordinatorIpcProtocol.ReadFrameLineAsync(reader, responseTimeout.Token)
                            .GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (responseTimeout?.IsCancellationRequested == true)
                {
                    throw new IOException(
                        "the coordinator disconnected or timed out before returning a terminal IPC result; " +
                        CoordinatorResponsePolicy.TimeoutMessage(normalizedCommand[0]));
                }
                catch (CoordinatorIpcException exception)
                {
                    throw new IOException("coordinator returned an invalid IPC frame: " + exception.Message,
                        exception);
                }
                catch (IOException exception)
                {
                    throw new IOException(
                        "the coordinator disconnected before returning a terminal IPC result; use DevBridge.cmd wait-ready or status",
                        exception);
                }
                catch (ObjectDisposedException exception)
                {
                    throw new IOException(
                        "the coordinator disconnected before returning a terminal IPC result; use DevBridge.cmd wait-ready or status",
                        exception);
                }

                if (line == null)
                    throw new IOException(
                        "the coordinator disconnected before returning a terminal IPC result; use DevBridge.cmd wait-ready or status");

                CoordinatorIpcFrame frame;
                try
                {
                    frame = JsonSerializer.Deserialize<CoordinatorIpcFrame>(line, Program.JsonOptions);
                }
                catch (JsonException exception)
                {
                    throw new IOException("coordinator returned malformed IPC JSON: " + exception.Message,
                        exception);
                }

                if (!CoordinatorIpcProtocol.TryValidateResponse(frame, request.RequestId, terminalSeen,
                        out string protocolError))
                    throw new IOException("coordinator IPC protocol error: " + protocolError);

                if (string.Equals(frame.Type, CoordinatorIpcProtocol.EventType, StringComparison.Ordinal))
                {
                    receivedLine?.Invoke(frame.Message);
                    Console.WriteLine(frame.Message);
                    continue;
                }

                terminalSeen = true;
                if (json)
                {
                    if (!frame.Payload.HasValue || frame.Payload.Value.ValueKind == JsonValueKind.Null)
                        throw new IOException("coordinator result did not contain the requested JSON payload");

                    string payload = frame.Payload.Value.GetRawText();
                    receivedLine?.Invoke(payload);
                    WriteJsonPayload(payload);
                }
                return frame.ExitCode.Value;
            }
        }

        throw new IOException("the coordinator disconnected before returning a terminal IPC result; use DevBridge.cmd wait-ready or status");
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

    private static void WriteJsonPayload(string payload)
    {
        string resultPath = Environment.GetEnvironmentVariable("DEVBRIDGE_TEST_RESULT_FILE");
        if (!string.IsNullOrWhiteSpace(resultPath) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEVBRIDGE_TEST_RIMWORLD_PATH")))
        {
            string fullPath = Path.GetFullPath(resultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            File.WriteAllText(fullPath, payload, new UTF8Encoding(false));
        }
        Console.WriteLine(payload);
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
            // A detached coordinator must not inherit the CLI's redirected
            // stdout/stderr handles. Those handles may remain open after the
            // finite client response and make shell callers wait forever.
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
        if (ProcessStarterForTests != null)
            ProcessStarterForTests(start);
        else
        {
            Process server = Process.Start(start);
            if (server != null)
            {
                // Keep the detached server's pipes drained while the server
                // remains available for later lazy client requests.
                _ = server.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                _ = server.StandardError.BaseStream.CopyToAsync(Stream.Null);
            }
        }
    }
}

internal static class CoordinatorServer
{
    internal const int MaxConcurrentClients = 16;
    private const int MaxPipeInstances = MaxConcurrentClients;
    private static readonly TimeSpan ShutdownClientGracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownWorkerGracePeriod = TimeSpan.FromSeconds(10);

    private sealed class ClientSession
    {
        internal ClientSession(NamedPipeServerStream pipe) => Pipe = pipe;

        internal NamedPipeServerStream Pipe { get; }
        internal Task Handler { get; set; }
        internal ManualResetEventSlim Completed { get; } = new(false);
        internal BridgeRequest Request { get; set; }
        internal bool IsShutdownRequest { get; set; }
        internal bool TerminalWritten { get; set; }
    }

    internal static int Run(string root, string runtimeSlotId = null, string ticketId = null,
        CoordinatorOptions optionsOverride = null, Action<CoordinatorState> started = null)
    {
        string slot = RuntimeScope.ResolveEffectiveSlot(root, runtimeSlotId, ticketId);
        Mutex mutex = new(false, PipeNames.MutexForSlot(root, slot));
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
            CoordinatorOptions configured = optionsOverride ?? CoordinatorOptions.ForProduction(root, slot);
            state = new(root, configured.ForScope(root, slot));
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
                bool atCapacity;
                lock (clientsGate)
                    atCapacity = clients.Count(value => !value.Completed.IsSet) >= MaxConcurrentClients;
                if (atCapacity)
                {
                    Thread.Sleep(50);
                    continue;
                }

                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeNames.ForSlot(root, slot), PipeDirection.InOut,
                        MaxPipeInstances, PipeTransmissionMode.Byte, CoordinatorPipeSecurity.ServerOptions);
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
            {
                state.TraceHostEvent("coordinator.shutdown.started", shutdownRequester?.Request);
                FinishShutdown(state, clients, shutdownRequester);
            }
            else
                state.RequestShutdown();
            return 0;
        }
        finally
        {
            state?.TraceHostEvent("coordinator.process.shutting_down");
            try
            {
                state?.Shutdown(ShutdownWorkerGracePeriod);
            }
            finally
            {
                state?.TraceHostEvent("coordinator.process.shutdown.completed", success: true);
                if (ownsMutex)
                {
                    try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
                }
                mutex.Dispose();
            }
        }
    }

    private static void FinishShutdown(CoordinatorState state, List<ClientSession> clients,
        ClientSession shutdownRequester)
    {
        state.RequestShutdown();
        state.InjectFaultForTesting(CoordinatorFaultPoint.DuringGracefulCoordinatorShutdown);
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
                bool resultWritten = false;

                bool WriteResult(int exitCode, object payload)
                {
                    if (resultWritten)
                        return true;
                    long started = Stopwatch.GetTimestamp();
                    state.TraceHostEvent("ipc.response.serialization.started", request);
                    resultWritten = output.WriteFrame(CoordinatorIpcProtocol.Result(
                        request?.RequestId, exitCode, payload, state.RunningBuildIdentity,
                        state.PublishedCoordinatorBuildIdentity, state.CoordinatorBuildMatchesPublished));
                    long durationMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    state.TraceHostEvent("ipc.response.serialization.completed", request,
                        durationMs: durationMs, success: resultWritten,
                        errorCode: resultWritten ? null : "TERMINAL_RESULT_NOT_WRITTEN");
                    state.TraceHostEvent("ipc.terminal_result.write", request,
                        durationMs: durationMs, success: resultWritten,
                        errorCode: resultWritten ? null : "TERMINAL_RESULT_NOT_WRITTEN");
                    if (resultWritten)
                        state.InjectFaultForTesting(
                            CoordinatorFaultPoint.AfterIpcResultWriteBeforeConnectionTeardown);
                    session.TerminalWritten = resultWritten;
                    return resultWritten;
                }

                void WriteProtocolFailure(string errorCode, string error, string command = null)
                {
                    JsonCommandResponse payload = CoordinatorIpcProtocol.ProtocolFailure(
                        command, errorCode, error, state.RunningBuildIdentity,
                        state.PublishedCoordinatorBuildIdentity, state.CoordinatorBuildMatchesPublished);
                    WriteResult(payload.ExitCode, payload);
                }

                try
                {
                    string requestLine;
                    try
                    {
                        requestLine = CoordinatorIpcProtocol.ReadFrameLine(reader);
                    }
                    catch (CoordinatorIpcException exception)
                    {
                        state.TraceRequestValidationRejected(null, exception.ErrorCode ?? "MALFORMED_REQUEST");
                        WriteProtocolFailure("MALFORMED_REQUEST", exception.Message);
                        return;
                    }

                    if (!CoordinatorIpcProtocol.TryDeserializeRequest(requestLine, out request,
                            out string deserializeErrorCode, out string deserializeError))
                    {
                        state.TraceRequestValidationRejected(request, deserializeErrorCode);
                        WriteProtocolFailure(deserializeErrorCode, deserializeError);
                        return;
                    }

                    if (!CoordinatorIpcProtocol.TryValidateRequest(request, out string errorCode,
                            out string validationError))
                    {
                        // Do not echo untrusted command or argument text in a
                        // protocol error. The error code is the stable contract.
                        state.TraceRequestValidationRejected(request, errorCode);
                        WriteProtocolFailure(errorCode, validationError);
                        return;
                    }

                    request.Arguments ??= new List<string>();
                    request.Agent = string.IsNullOrWhiteSpace(request.Agent) ? "unknown-agent" : request.Agent.Trim();
                    session.Request = request;
                    session.IsShutdownRequest = IsShutdownRequest(request);
                    state.TraceRequestAccepted(request);

                    List<string> buffered = request.Json ? new List<string>() : null;
                    int bufferedLength = 0;
                    bool bufferedOutputTruncated = false;
                    Action<string> emit = request.Json
                        ? value =>
                        {
                            if (bufferedOutputTruncated)
                                return;

                            string bounded = CoordinatorIpcProtocol.BoundEventMessage(value);
                            const string truncation = "[output truncated by coordinator IPC limit]";
                            if (buffered.Count >= CoordinatorIpcProtocol.MaxBufferedEventCount ||
                                bufferedLength + bounded.Length > CoordinatorIpcProtocol.MaxBufferedEventOutputLength)
                            {
                                if (bufferedLength + truncation.Length <=
                                    CoordinatorIpcProtocol.MaxBufferedEventOutputLength)
                                {
                                    buffered.Add(truncation);
                                    bufferedLength += truncation.Length;
                                }
                                bufferedOutputTruncated = true;
                                return;
                            }

                            buffered.Add(bounded);
                            bufferedLength += bounded.Length;
                        }
                        : value =>
                        {
                            long started = Stopwatch.GetTimestamp();
                            state.TraceHostEvent("ipc.event.write.started", request);
                            bool written = output.WriteFrame(CoordinatorIpcProtocol.Event(request.RequestId, value));
                            state.TraceHostEvent("ipc.event.write.completed", request,
                                durationMs: Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds),
                                success: written,
                                errorCode: written ? null : "EVENT_NOT_WRITTEN");
                        };
                    int exitCode = state.Execute(request, emit, () => output.Connected && pipe.IsConnected);
                    object response = null;
                    if (request.Json)
                    {
                        response = string.Equals(request.Command, "agent", StringComparison.OrdinalIgnoreCase)
                            ? state.CreateAgentJsonResponse(request, exitCode)
                            : Program.IsRecipeCommand(request)
                                ? state.CreateRecipeJsonResponse(request, exitCode)
                                : state.IsForensicCommand(request)
                                    ? state.CreateForensicJsonResponse(request, exitCode)
                                : state.CreateJsonResponse(request, exitCode, buffered);
                        exitCode = response switch
                        {
                            AgentResponse agentResponse => agentResponse.ExitCode,
                            RecipeResponse recipeResponse => recipeResponse.ExitCode,
                            ForensicResponse forensicResponse => forensicResponse.ExitCode,
                            JsonCommandResponse legacyResponse => legacyResponse.ExitCode,
                            _ => exitCode
                        };
                    }
                    if (string.Equals(request.Command, "stop", StringComparison.OrdinalIgnoreCase) &&
                        state.PhaseForTesting == BridgePhase.STOPPED)
                        state.InjectFaultForTesting(
                            CoordinatorFaultPoint.AfterStoppedPersistenceBeforeIpcTerminalResult);
                    bool terminalWritten = WriteResult(exitCode, response);
                    if (session.IsShutdownRequest && exitCode == 0 && terminalWritten)
                    {
                        state.TraceHostEvent("coordinator.shutdown.accepted", request, success: true);
                        requestShutdown(session);
                    }
                }
                catch (CoordinatorFaultInjectedException)
                {
                    // A configured fault represents coordinator death at the
                    // requested boundary. Do not manufacture a second result.
                    throw;
                }
                catch (Exception exception)
                {
                    state.TraceHostEvent("ipc.handler.failed", request,
                        success: false, errorCode: "COORDINATOR_ERROR",
                        detail: CoordinatorState.TraceExceptionCategory(exception));
                    if (!resultWritten)
                    {
                        if (request != null && !request.Json)
                            output.WriteFrame(CoordinatorIpcProtocol.Event(request.RequestId,
                                "DevBridge coordinator error: " + exception.Message));
                        JsonCommandResponse failure = CoordinatorIpcProtocol.ProtocolFailure(
                            request?.Command, "COORDINATOR_ERROR", exception.Message,
                            state.RunningBuildIdentity, state.PublishedCoordinatorBuildIdentity,
                            state.CoordinatorBuildMatchesPublished);
                        WriteResult(failure.ExitCode, request?.Json == true ? failure : null);
                    }
                }
                finally
                {
                    if (!resultWritten)
                        state.TraceHostEvent("ipc.client.disconnect.before_result", request,
                            success: false, errorCode: "TERMINAL_RESULT_NOT_WRITTEN");
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

    internal bool WriteFrame(CoordinatorIpcFrame frame)
    {
        if (!CoordinatorIpcProtocol.TrySerializeFrame(frame, out string line, out _))
        {
            Connected = false;
            return false;
        }
        return WriteRaw(line);
    }

    internal bool WriteRaw(string line)
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
        if (string.Equals(command, "agent", StringComparison.OrdinalIgnoreCase) && arguments.Count > 0 &&
            string.Equals(arguments[0], "wait-event", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) && arguments.Count > 0 &&
            (string.Equals(arguments[0], "begin", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(arguments[0], "session", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (string.Equals(command, "test", StringComparison.OrdinalIgnoreCase) && arguments.Count > 1 &&
            string.Equals(arguments[0], "recipe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(arguments[1], "run", StringComparison.OrdinalIgnoreCase))
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
        "DevBridge2-" + RuntimeScope.HashOpaqueIdentifier(
            RuntimeScope.CanonicalizeRootPath(root) + "|" + (slot ?? string.Empty));

    internal static string MutexForRoot(string root) =>
        "Local\\DevBridge2Coordinator-" + RuntimeScope.HashCanonicalPath(root);

    internal static string MutexForSlot(string root, string slot) =>
        "Local\\DevBridge2CoordinatorSlot-" + RuntimeScope.HashOpaqueIdentifier(
            RuntimeScope.CanonicalizeRootPath(root) + "|" + (slot ?? string.Empty));

}

internal static class CoordinatorPipeSecurity
{
    // CurrentUserOnly applies the strongest framework-provided local named-pipe
    // restriction on the net8 coordinator target. No global or Everyone ACL is
    // requested here.
    internal const PipeOptions ServerOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
}
