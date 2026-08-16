using System.Text.Json;
using System.IO.Pipes;
using System.Diagnostics;
using System.Text;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestNamedPipeStopCompletesClient()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        List<string> received = RunNamedPipeCommand(fixture, "stop", "T001");

        Assert(received.Count(value => value.StartsWith("__DEVBRIDGE_END__|", StringComparison.Ordinal)) == 1,
            "stop did not receive exactly one terminal marker");
        Assert(received.Any(value => value.Contains("gameState=STOPPED", StringComparison.Ordinal)),
            "stop did not receive its terminal state message");
        PersistedState state = ReadPersistedState(fixture.Root);
        Assert(state.Phase == BridgePhase.STOPPED && state.MaintenanceReady && state.ProcessId == 0,
            "stop did not persist the terminal maintenance state");
    }

    private static void TestNamedPipeJsonStopCompletesClient()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        List<string> received = RunNamedPipeCommand(fixture, "stop", "T001", "--json");

        List<string> markers = received.Where(value => value.StartsWith("__DEVBRIDGE_END__|",
            StringComparison.Ordinal)).ToList();
        Assert(markers.Count == 1 && markers[0] == "__DEVBRIDGE_END__|0",
            "stop --json did not receive exactly one successful terminal marker");
        List<string> json = received.Where(value => !value.StartsWith("__DEVBRIDGE_END__|",
            StringComparison.Ordinal)).ToList();
        Assert(json.Count == 1, "stop --json did not receive one JSON response");
        using JsonDocument document = JsonDocument.Parse(json[0]);
        Assert(document.RootElement.GetProperty("state").GetString() == "STOPPED",
            "stop --json response did not report STOPPED");
        PersistedState state = ReadPersistedState(fixture.Root);
        Assert(state.Phase == BridgePhase.STOPPED && state.MaintenanceReady && state.ProcessId == 0,
            "stop --json did not persist the terminal maintenance state");
    }

    private static List<string> RunNamedPipeCommand(Fixture fixture, params string[] command)
    {
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        List<string> received = harness.Send(command);
        harness.Shutdown();
        return received;
    }

    private static PersistedState ReadPersistedState(string root)
    {
        return JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(root, "Runtime", "state.json")), Program.JsonOptions);
    }

    private static void TestCoordinatorShutdownRespondsBeforeExit()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        List<string> received = harness.Send("coordinator", "shutdown", "--json");

        int marker = received.FindIndex(value => value.StartsWith("__DEVBRIDGE_END__|",
            StringComparison.Ordinal));
        int json = received.FindIndex(value => value.StartsWith("{", StringComparison.Ordinal));
        Assert(marker > json && marker >= 0 && harness.MarkerObservedBeforeServerExit,
            "shutdown must flush its response and end marker before releasing the server");
        Assert(received[marker] == "__DEVBRIDGE_END__|0", "shutdown returned a failure marker");
        using JsonDocument document = JsonDocument.Parse(received[json]);
        Assert(document.RootElement.GetProperty("success").GetBoolean(),
            "shutdown JSON response was not successful");
        Assert(fixture.Adapter.TerminationRequests == 0,
            "coordinator shutdown must not terminate RimWorld");
        Assert(harness.ServerTask.Wait(TimeSpan.FromSeconds(5)),
            "coordinator did not exit after the shutdown response");
        PersistedState state = ReadPersistedState(fixture.Root);
        Assert(state.Phase == BridgePhase.READY && state.ProcessId == 101,
            "shutdown changed durable process state");
    }

    private static void TestCoordinatorShutdownReacquiresMutexAndPipe()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using (CoordinatorHarness first = CoordinatorHarness.Start(fixture))
        {
            first.Send("coordinator", "shutdown");
            Assert(first.ServerTask.Wait(TimeSpan.FromSeconds(5)),
                "first coordinator did not release its slot");
        }

        using CoordinatorHarness second = CoordinatorHarness.Start(fixture);
        List<string> received = second.Send("status", "--json");
        Assert(received.Any(value => value.StartsWith("{", StringComparison.Ordinal)),
            "a later command could not reacquire the pipe");
        second.Shutdown();
    }

    private static void TestCoordinatorShutdownReloadsCurrentEnvironmentAndExecutable()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        string previousTimeout = Environment.GetEnvironmentVariable("DEVBRIDGE_READINESS_TIMEOUT_SECONDS");
        string previousMode = Environment.GetEnvironmentVariable("DEVBRIDGE_RIMBRIDGE_MODE");
        try
        {
            Environment.SetEnvironmentVariable("DEVBRIDGE_READINESS_TIMEOUT_SECONDS", "31");
            Environment.SetEnvironmentVariable("DEVBRIDGE_RIMBRIDGE_MODE", "off");
            using (CoordinatorHarness first = CoordinatorHarness.StartProduction(fixture))
            {
                Assert(first.StartedState.ReadinessTimeoutForTesting == TimeSpan.FromSeconds(31),
                    "first coordinator did not read the current environment");
                first.Shutdown();
            }

            Environment.SetEnvironmentVariable("DEVBRIDGE_READINESS_TIMEOUT_SECONDS", "32");
            using (CoordinatorHarness second = CoordinatorHarness.StartProduction(fixture))
            {
                Assert(second.StartedState.ReadinessTimeoutForTesting == TimeSpan.FromSeconds(32),
                    "later command did not load refreshed coordinator configuration");
                second.Shutdown();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVBRIDGE_READINESS_TIMEOUT_SECONDS", previousTimeout);
            Environment.SetEnvironmentVariable("DEVBRIDGE_RIMBRIDGE_MODE", previousMode);
        }

        List<string> started = new();
        Func<string> previousPath = CoordinatorClient.ProcessPathProviderForTests;
        Action<ProcessStartInfo> previousStarter = CoordinatorClient.ProcessStarterForTests;
        string currentPath = "C:\\replaced\\DevBridge.Coordinator.exe";
        try
        {
            CoordinatorClient.ProcessStarterForTests = info => started.Add(info.FileName);
            CoordinatorClient.ProcessPathProviderForTests = () => currentPath;
            CoordinatorClient.StartServerForTests(fixture.Root, RuntimeScope.ForRoot(fixture.Root), null);
            currentPath = "C:\\new\\DevBridge.Coordinator.exe";
            CoordinatorClient.StartServerForTests(fixture.Root, RuntimeScope.ForRoot(fixture.Root), null);
        }
        finally
        {
            CoordinatorClient.ProcessPathProviderForTests = previousPath;
            CoordinatorClient.ProcessStarterForTests = previousStarter;
        }
        Assert(started.SequenceEqual(new[]
        {
            "C:\\replaced\\DevBridge.Coordinator.exe",
            "C:\\new\\DevBridge.Coordinator.exe"
        }), "lazy start cached an obsolete coordinator executable path");
    }

    private static void TestFiniteCommandsHaveBoundedTerminalResponses()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        string slot = RuntimeScope.ForRoot(fixture.Root);
        string pipeName = PipeNames.ForSlot(fixture.Root, slot);
        using NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task accept = Task.Run(() =>
        {
            try
            {
                server.WaitForConnection();
                using StreamReader reader = new(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                reader.ReadLine();
            }
            catch (IOException)
            {
            }
        });

        Exception timeout = null;
        try
        {
            CoordinatorClient.Run(fixture.Root, new[] { "status" }, slot, null, null,
                TimeSpan.FromMilliseconds(150));
        }
        catch (Exception exception)
        {
            timeout = exception;
        }
        finally
        {
            server.Dispose();
            accept.Wait(TimeSpan.FromSeconds(2));
        }
        Assert(timeout is IOException && timeout.Message.Contains("accepted", StringComparison.OrdinalIgnoreCase),
            "finite command timeout was not explicit about possible durable acceptance");
    }

    private static void TestDurableWaitResponsePolicyRemainsUnbounded()
    {
        Assert(!CoordinatorResponsePolicy.IsFinite("wait-ready", Array.Empty<string>()),
            "wait-ready must remain a durable wait");
        Assert(!CoordinatorResponsePolicy.IsFinite("restart", Array.Empty<string>()),
            "restart must remain a durable wait");
        Assert(!CoordinatorResponsePolicy.IsFinite("test", new[] { "begin" }),
            "test begin must remain a durable wait");
        Assert(!CoordinatorResponsePolicy.IsFinite("test", new[] { "session" }),
            "test session must remain a durable wait");
        Assert(CoordinatorResponsePolicy.IsFinite("status", Array.Empty<string>()),
            "status must have a bounded terminal response");
    }

    private static void TestSimultaneousShutdownClientsAreBoundedAndDurable()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        string previousAgent = Environment.GetEnvironmentVariable("DEVBRIDGE_AGENT");
        Environment.SetEnvironmentVariable("DEVBRIDGE_AGENT", "holder");
        List<string> restartMessages = new();
        Task<int> restartClient = Task.Run(() => CoordinatorClient.Run(fixture.Root,
            new[] { "restart" }, harness.Slot, null, restartMessages.Add));
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!restartMessages.Any(value => value.Contains("Restart accepted", StringComparison.Ordinal)) &&
                   DateTime.UtcNow < deadline)
                Thread.Sleep(20);
            Assert(restartMessages.Any(value => value.Contains("Restart accepted", StringComparison.Ordinal)),
                "long-running restart client was not accepted before shutdown");

            List<string> shutdown = harness.Send("coordinator", "shutdown");
            Assert(shutdown.Any(value => value == "__DEVBRIDGE_END__|0"),
                "shutdown client did not receive a terminal response");
            Assert(harness.ServerTask.Wait(TimeSpan.FromSeconds(5)),
                "simultaneous shutdown clients left the coordinator running");
            try
            {
                restartClient.GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                // The accepted durable wait is intentionally disconnected when
                // shutdown drains competing clients; the next command retries
                // against the preserved state.
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVBRIDGE_AGENT", previousAgent);
        }

        PersistedState state = ReadPersistedState(fixture.Root);
        Assert(state.RestartPending && (state.Phase == BridgePhase.DRAINING ||
            state.Phase == BridgePhase.WAITING_FOR_BRIDGE),
            "shutdown cancelled or rolled back an accepted durable restart; phase=" + state.Phase +
            ", restartPending=" + state.RestartPending);
        Assert(fixture.Adapter.TerminationRequests == 0,
            "shutdown terminated RimWorld while draining a durable restart");
    }

    private sealed class CoordinatorHarness : IDisposable
    {
        private CoordinatorHarness(Fixture fixture, CoordinatorOptions options)
        {
            Fixture = fixture;
            Slot = RuntimeScope.ForRoot(fixture.Root);
            ManualResetEventSlim started = new(false);
            ServerTask = Task.Run(() => CoordinatorServer.Run(fixture.Root, Slot, null, options,
                state =>
                {
                    StartedState = state;
                    started.Set();
                }));
            Assert(started.Wait(TimeSpan.FromSeconds(3)), "test coordinator did not start");
            started.Dispose();
        }

        internal Fixture Fixture { get; }
        internal string Slot { get; }
        internal Task<int> ServerTask { get; }
        internal CoordinatorState StartedState { get; private set; }
        internal bool MarkerObservedBeforeServerExit { get; private set; }

        internal static CoordinatorHarness Start(Fixture fixture) =>
            new(fixture, TestOptions(fixture));

        internal static CoordinatorHarness StartProduction(Fixture fixture) =>
            new(fixture, null);

        internal List<string> Send(params string[] command)
        {
            string previousAgent = Environment.GetEnvironmentVariable("DEVBRIDGE_AGENT");
            Environment.SetEnvironmentVariable("DEVBRIDGE_AGENT", "holder");
            try
            {
                List<string> received = new();
                int exitCode = CoordinatorClient.Run(Fixture.Root, command, Slot, null, value =>
                {
                    received.Add(value);
                    if (value.StartsWith("__DEVBRIDGE_END__|", StringComparison.Ordinal) &&
                        !ServerTask.IsCompleted)
                        MarkerObservedBeforeServerExit = true;
                });
                Assert(exitCode == 0, "named-pipe command returned " + exitCode);
                return received;
            }
            finally
            {
                Environment.SetEnvironmentVariable("DEVBRIDGE_AGENT", previousAgent);
            }
        }

        internal void Shutdown()
        {
            if (ServerTask.IsCompleted)
                return;
            Send("coordinator", "shutdown");
            Assert(ServerTask.Wait(TimeSpan.FromSeconds(5)), "coordinator shutdown did not complete");
        }

        public void Dispose()
        {
            try
            {
                Shutdown();
            }
            catch
            {
                if (!ServerTask.IsCompleted)
                    ServerTask.Wait(TimeSpan.FromSeconds(5));
                throw;
            }
        }

        private static CoordinatorOptions TestOptions(Fixture fixture) => new()
        {
            CoordinatorRoot = fixture.Root,
            RuntimeSlotId = RuntimeScope.ForRoot(fixture.Root),
            ReadinessTimeout = TimeSpan.FromSeconds(3),
            ProcessInspectionRetryTimeout = TimeSpan.FromSeconds(2),
            ProcessExitTimeout = TimeSpan.FromSeconds(1),
            ProcessAdapter = fixture.Adapter,
            Clock = fixture.Clock,
            RimWorldExecutablePath = fixture.RimWorldPath,
            ModsConfigPath = Path.Combine(fixture.Root, "ModsConfig.xml"),
            InstalledModsRoots = fixture.InstalledModsRoots,
            RimBridgeMode = fixture.RimBridgeMode,
            PlayerLogPath = fixture.PlayerLogPath ?? Path.Combine(fixture.Root, "Player.log"),
            RimBridgeClient = fixture.RouteClient,
            RimBridgeGenerationVerifier = fixture.RouteVerifier,
            BeforeModsConfigWrite = fixture.BeforeModsConfigWrite
        };
    }
}
