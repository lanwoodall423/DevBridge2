using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestDeterministicCoordinatorStateMachine()
    {
        uint[] regressionSeeds =
        {
            0x13579BDFu,
            0x2468ACE1u,
            0x51A7E5u,
            0xC0FFEE42u,
            0x7F4A7C15u
        };
        bool stress = string.Equals(Environment.GetEnvironmentVariable("DEVBRIDGE_MODEL_STRESS"),
            "1", StringComparison.Ordinal);
        int steps = stress ? 72 : 32;

        foreach (uint seed in regressionSeeds)
        {
            using ModelScenario scenario = new(seed);
            scenario.Run(steps);
        }
    }

    private sealed class ModelScenario : IDisposable
    {
        private const string ModelAgent = "model-agent";
        private const string ModelSession = "model-session";
        private const string ModelSecret = "MODEL_SECRET_MUST_NOT_BE_PERSISTED";
        private static readonly string[] OperationNames =
        {
            "status",
            "test begin",
            "test renew",
            "test end",
            "test session disconnect",
            "project register",
            "project release",
            "restart",
            "wait-ready",
            "stop",
            "ensure-ready",
            "external ModsConfig mutation",
            "process disappearance",
            "readiness arrival",
            "quicktest failure",
            "coordinator shutdown/restart",
            "companion unavailable",
            "RimBridge endpoint invalidation",
            "lease expiration",
            "coordinator recovery",
            "mods capture-baseline",
            "mods restore-baseline",
            "history",
            "secret-shaped diagnostic input"
        };

        private readonly uint seed;
        private readonly DeterministicRandom random;
        private readonly List<string> trace = new();
        private readonly Dictionary<string, string> generationEvidence = new(StringComparer.OrdinalIgnoreCase);
        private Fixture fixture;
        private CoordinatorHarness harness;
        private int requestNumber;
        private int highestGeneration;
        private int previousBudget;

        internal ModelScenario(uint seed)
        {
            this.seed = seed;
            random = new(seed);
            fixture = Fixture.ReadyWithoutLease();
            fixture.Adapter.ReadyOnLaunch = true;
            fixture.WriteReadiness("launch-ready", 1, 101);
            harness = CoordinatorHarness.Start(fixture);
            PersistedState initial = ReadPersistedState(fixture.Root);
            highestGeneration = initial.Generation;
            previousBudget = initial.LaunchBudgetRemaining;
            RememberGenerationEvidence();
        }

        internal void Run(int steps)
        {
            for (int step = 0; step < steps; step++)
            {
                int operation = step < OperationNames.Length
                    ? step
                    : random.Next(OperationNames.Length);
                string name = OperationNames[operation];
                trace.Add(step.ToString("D3") + ":" + name);
                try
                {
                    bool mayResetLaunchBudget = Execute(operation);
                    AssertInvariants(name, mayResetLaunchBudget);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "seed=0x" + seed.ToString("X8") +
                        " step=" + step.ToString() +
                        " operations=" + string.Join(" | ", trace) +
                        " :: " + exception.Message, exception);
                }
            }
        }

        private bool Execute(int operation)
        {
            PersistedState before = ReadPersistedState(fixture.Root);
            bool mayResetLaunchBudget = false;
            switch (operation)
            {
                case 0:
                    Command("status");
                    break;
                case 1:
                    if (CurrentLease(before) == null)
                        Command("test", "begin");
                    else
                        Command("status");
                    break;
                case 2:
                    Command("test", "renew", CurrentLease(before)?.Id ?? "MODEL-NO-LEASE");
                    break;
                case 3:
                    Command("test", "end", CurrentLease(before)?.Id ?? "MODEL-NO-LEASE");
                    break;
                case 4:
                    if (CurrentLease(before) == null)
                        DisconnectSession();
                    else
                        Command("status");
                    break;
                case 5:
                    Command("project", "register", "frontier", "--id", RegistrationId);
                    break;
                case 6:
                    Command("project", "release", RegistrationId);
                    break;
                case 7:
                    if (before.Leases.Count == 0)
                    {
                        Command("restart");
                        mayResetLaunchBudget = true;
                    }
                    else
                        Command("status");
                    break;
                case 8:
                    Command("wait-ready");
                    mayResetLaunchBudget = before.Generation == 0 && before.Phase == BridgePhase.STOPPED;
                    break;
                case 9:
                    Command("stop", CurrentLease(before)?.Id ?? "MODEL-NO-LEASE");
                    break;
                case 10:
                    Command("ensure-ready", CurrentLease(before)?.Id ?? "MODEL-NO-LEASE");
                    mayResetLaunchBudget = before.MaintenanceReady && CurrentLease(before) != null;
                    break;
                case 11:
                    File.AppendAllText(Path.Combine(fixture.Root, "ModsConfig.xml"),
                        "<!-- deterministic external mutation -->");
                    Command("status");
                    break;
                case 12:
                    DisappearFromCurrentProcess(before);
                    break;
                case 13:
                    ArriveReadiness(before);
                    break;
                case 14:
                    WriteQuicktestFailure(before);
                    break;
                case 15:
                    RestartCoordinator();
                    break;
                case 16:
                    Command("bridge", "status");
                    break;
                case 17:
                    RimBridgeEndpointStore.Delete(Path.Combine(fixture.Root, "Runtime"));
                    Command("bridge", "status");
                    break;
                case 18:
                    fixture.Clock.Advance(TimeSpan.FromMinutes(3));
                    Command("status");
                    break;
                case 19:
                    RestartCoordinator();
                    break;
                case 20:
                    Command("mods", "capture-baseline");
                    break;
                case 21:
                    Command("mods", "restore-baseline");
                    break;
                case 22:
                    Command("history");
                    break;
                case 23:
                    Command("not-a-real-command", ModelSecret);
                    break;
                default:
                    Command("status");
                    break;
            }
            return mayResetLaunchBudget;
        }

        private string RegistrationId => "MODEL-REG-" + seed.ToString("X8");

        private TestLease CurrentLease(PersistedState state) => (state.Leases ?? new List<TestLease>())
            .FirstOrDefault(value => string.Equals(value.Agent, ModelAgent, StringComparison.Ordinal));

        private BridgeRequest CreateRequest(string command, params string[] arguments)
        {
            return new BridgeRequest
            {
                ProtocolVersion = CoordinatorIpcProtocol.Version,
                RequestId = "model-" + seed.ToString("X8") + "-" + requestNumber++.ToString("D4"),
                Type = CoordinatorIpcProtocol.RequestType,
                Command = command,
                Arguments = arguments?.ToList() ?? new List<string>(),
                Agent = ModelAgent,
                ClientProcessId = 7001,
                Json = true,
                RuntimeSlotId = RuntimeScope.ForRoot(fixture.Root),
                CoordinatorRoot = fixture.Root,
                SessionId = ModelSession
            };
        }

        private List<CoordinatorIpcFrame> Command(string command, params string[] arguments)
        {
            BridgeRequest request = CreateRequest(command, arguments);
            List<CoordinatorIpcFrame> frames = SendBounded(request);
            int resultCount = frames.Count(value => value.Type == CoordinatorIpcProtocol.ResultType);
            Assert(resultCount == 1,
                "command did not produce exactly one terminal result: " + command);
            return frames;
        }

        private List<CoordinatorIpcFrame> SendBounded(BridgeRequest request)
        {
            using NamedPipeClientStream pipe = new(".",
                PipeNames.ForSlot(fixture.Root, RuntimeScope.ForRoot(fixture.Root)),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
            try
            {
                pipe.ConnectAsync(cancellation.Token).GetAwaiter().GetResult();
                using StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true
                };
                writer.WriteLine(JsonSerializer.Serialize(request, Program.JsonOptions));

                List<CoordinatorIpcFrame> frames = new();
                bool terminalSeen = false;
                while (true)
                {
                    string line = CoordinatorIpcProtocol.ReadFrameLineAsync(reader, cancellation.Token)
                        .GetAwaiter().GetResult();
                    if (line == null)
                        throw new IOException("model client disconnected before a terminal result");
                    CoordinatorIpcFrame frame = JsonSerializer.Deserialize<CoordinatorIpcFrame>(
                        line, Program.JsonOptions);
                    if (!CoordinatorIpcProtocol.TryValidateResponse(frame, request.RequestId, terminalSeen,
                            out string protocolError))
                        throw new InvalidOperationException("model IPC response was invalid: " + protocolError);
                    frames.Add(frame);
                    if (frame.Payload.HasValue)
                        ObservedOutput(frame.Payload.Value.GetRawText());
                    if (!string.IsNullOrEmpty(frame.Message))
                        ObservedOutput(frame.Message);
                    if (frame.Type == CoordinatorIpcProtocol.ResultType)
                    {
                        terminalSeen = true;
                        break;
                    }
                }
                return frames;
            }
            catch (OperationCanceledException exception)
            {
                throw new IOException("bounded model IPC operation timed out: " + request.Command, exception);
            }
        }

        private void DisconnectSession()
        {
            BridgeRequest request = CreateRequest("test", "session");
            request.Json = false;
            using NamedPipeClientStream pipe = new(".",
                PipeNames.ForSlot(fixture.Root, RuntimeScope.ForRoot(fixture.Root)),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
            pipe.ConnectAsync(cancellation.Token).GetAwaiter().GetResult();
            using StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
            writer.WriteLine(JsonSerializer.Serialize(request, Program.JsonOptions));

            bool accepted = false;
            while (!accepted)
            {
                string line = CoordinatorIpcProtocol.ReadFrameLineAsync(reader, cancellation.Token)
                    .GetAwaiter().GetResult();
                if (line == null)
                    break;
                CoordinatorIpcFrame frame = JsonSerializer.Deserialize<CoordinatorIpcFrame>(
                    line, Program.JsonOptions);
                if (frame != null)
                {
                    if (!CoordinatorIpcProtocol.TryValidateResponse(frame, request.RequestId, false,
                            out string protocolError))
                        throw new InvalidOperationException("session response was invalid: " + protocolError);
                    ObservedOutput(frame.Message);
                    accepted = frame.Type == CoordinatorIpcProtocol.EventType &&
                        (frame.Message ?? string.Empty).Contains("Connected lease session is active",
                            StringComparison.Ordinal);
                }
            }
        }

        private void DisappearFromCurrentProcess(PersistedState state)
        {
            if (state.ProcessId <= 0)
            {
                Command("status");
                return;
            }

            if (fixture.Adapter.Open(state.ProcessId) is FakeProcess process)
                process.ForceTerminate();
            Command("status");
        }

        private void ArriveReadiness(PersistedState state)
        {
            if (state.ProcessId > 0 && !string.IsNullOrWhiteSpace(state.LaunchId) &&
                state.LaunchGeneration > 0)
            {
                fixture.WriteReadiness(state.LaunchId, state.LaunchGeneration, state.ProcessId);
            }
            Command("status");
        }

        private void WriteQuicktestFailure(PersistedState state)
        {
            if (state.ProcessId > 0 && !string.IsNullOrWhiteSpace(state.LaunchId) &&
                state.LaunchGeneration > 0)
            {
                QuicktestFailureArtifact.TryWrite(fixture.Root, new QuicktestFailureRecord
                {
                    SchemaVersion = QuicktestFailureArtifact.CurrentSchemaVersion,
                    LaunchId = state.LaunchId,
                    Generation = state.LaunchGeneration,
                    ProcessId = state.ProcessId,
                    ProcessStartUtcTicks = state.ProcessStartUtcTicks,
                    ProfileFingerprint = state.ProfileFingerprint,
                    BaselineFingerprint = state.BaselineFingerprint,
                    ProfileMode = state.ProfileMode,
                    TimestampUtc = fixture.Clock.UtcNow,
                    FailurePhase = "model.failure",
                    FailureCode = QuicktestFailureArtifact.StableFailureCode,
                    ExceptionType = "ModelInjectedFailure",
                    ExceptionMessage = "deterministic model fault",
                    DiagnosticDetail = "model fault injection"
                }, out _);
            }
            Command("status");
        }

        private void RestartCoordinator()
        {
            if (harness != null && !harness.ServerTask.IsCompleted)
            {
                Command("coordinator", "shutdown");
                Assert(harness.ServerTask.Wait(TimeSpan.FromSeconds(5)),
                    "coordinator did not complete deterministic shutdown");
                harness.Dispose();
                harness = null;
            }
            harness = CoordinatorHarness.Start(fixture);
            Command("status");
        }

        private void AssertInvariants(string operation, bool mayResetLaunchBudget)
        {
            harness.StartedState.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
            PersistedState state = ReadPersistedState(fixture.Root);
            ProcessEnumeration census = fixture.Adapter.EnumerateRimWorld(fixture.RimWorldPath);
            Assert(census.Complete, operation + " produced an incomplete process census");
            int liveProcesses = census.Processes.Count(value => !value.HasExited);
            Assert(liveProcesses <= 1,
                operation + " accepted more than one coordinator-owned RimWorld process");

            if (state.MaintenanceReady)
            {
                Assert(state.Phase == BridgePhase.STOPPED && state.ProcessId == 0 && liveProcesses == 0,
                    operation + " violated the maintenanceReady process-absence invariant");
            }

            if (state.Phase == BridgePhase.READY)
            {
                Assert(state.Generation > 0 && state.ProcessId > 0 && state.ProcessStartUtcTicks > 0 &&
                    !string.IsNullOrWhiteSpace(state.LaunchId) && state.LaunchGeneration > 0,
                    operation + " promoted READY without complete accepted process identity");
                Assert(census.Processes.Any(value => value.Id == state.ProcessId && !value.HasExited),
                    operation + " promoted READY without a live accepted process");
                string readinessPath = Path.Combine(fixture.Root, "Runtime", "readiness.json");
                Assert(File.Exists(readinessPath), operation + " promoted READY without readiness evidence");
                ReadinessRecord readiness = JsonSerializer.Deserialize<ReadinessRecord>(
                    File.ReadAllText(readinessPath), Program.JsonOptions);
                Assert(readiness != null && readiness.LaunchId == state.LaunchId &&
                    readiness.Generation == state.LaunchGeneration && readiness.ProcessId == state.ProcessId,
                    operation + " had readiness evidence for a different process or generation");
            }

            Assert(state.Generation >= highestGeneration,
                operation + " decreased the durable generation");
            highestGeneration = Math.Max(highestGeneration, state.Generation);
            if (state.RestartPending)
                Assert(state.TargetGeneration > state.Generation,
                    operation + " left a pending restart without a future target generation");
            if (state.TargetGeneration > 0)
                Assert(state.TargetGeneration >= state.Generation,
                    operation + " left targetGeneration behind the accepted generation");

            Assert(state.LaunchAttemptCount >= 0 && state.LaunchBudgetRemaining >= 0 &&
                state.LaunchBudgetRemaining <= 2,
                operation + " violated the finite launch-attempt budget");
            if (state.LaunchBudgetRemaining > previousBudget && !mayResetLaunchBudget)
                throw new InvalidOperationException(operation +
                    " unexpectedly replenished the launch-attempt budget");
            previousBudget = state.LaunchBudgetRemaining;

            RememberGenerationEvidence();
            AssertNoSecrets();
            AssertWrongOwnerCannotUseLease(state);
        }

        private void AssertWrongOwnerCannotUseLease(PersistedState state)
        {
            TestLease lease = CurrentLease(state);
            if (lease == null || fixture.Clock.UtcNow >= lease.LastHeartbeatUtc.AddMinutes(2))
                return;

            string statePath = Path.Combine(fixture.Root, "Runtime", "state.json");
            string beforeState = File.ReadAllText(statePath);
            string beforeMods = File.ReadAllText(Path.Combine(fixture.Root, "ModsConfig.xml"));
            int launches = fixture.Adapter.LaunchCalls;
            int terminations = fixture.Adapter.TerminationRequests;
            BridgeRequest request = CreateRequest("test", "renew", lease.Id);
            request.Agent = "different-model-owner";
            List<CoordinatorIpcFrame> frames = SendBounded(request);
            Assert(frames.Count(value => value.Type == CoordinatorIpcProtocol.ResultType) == 1 &&
                frames.Single(value => value.Type == CoordinatorIpcProtocol.ResultType).ExitCode == 4,
                "a lease was accepted by a different owner");
            Assert(File.ReadAllText(statePath) == beforeState &&
                File.ReadAllText(Path.Combine(fixture.Root, "ModsConfig.xml")) == beforeMods &&
                fixture.Adapter.LaunchCalls == launches && fixture.Adapter.TerminationRequests == terminations,
                "a rejected lease operation mutated durable state, config, or process control");
        }

        private void RememberGenerationEvidence()
        {
            string generationsRoot = Path.Combine(fixture.Root, "Runtime", "generations");
            if (!Directory.Exists(generationsRoot))
                return;
            foreach (string path in Directory.GetFiles(generationsRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                if (generationEvidence.TryGetValue(path, out string previous))
                    Assert(previous == hash, "completed generation evidence mutated: " + Path.GetFileName(path));
                else
                    generationEvidence[path] = hash;
            }
        }

        private void ObservedOutput(string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            string safe = value.Replace(ModelSecret, "[REDACTED]", StringComparison.Ordinal);
            if (safe.Length > 512)
                safe = safe[..512] + "...[bounded]";
            trace.Add("output:" + safe);
        }

        private void AssertNoSecrets()
        {
            foreach (string path in Directory.GetFiles(Path.Combine(fixture.Root, "Runtime"), "*",
                         SearchOption.AllDirectories))
            {
                string contents;
                try
                {
                    contents = File.ReadAllText(path, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }
                Assert(!contents.Contains(ModelSecret, StringComparison.Ordinal),
                    "secret-shaped input entered persisted diagnostics: " + path);
            }
            Assert(!trace.Any(value => value.Contains(ModelSecret, StringComparison.Ordinal)),
                "secret-shaped input entered the model trace or IPC output");
        }

        public void Dispose()
        {
            try
            {
                harness?.Dispose();
            }
            finally
            {
                fixture?.Dispose();
            }
        }
    }

    private sealed class DeterministicRandom
    {
        private uint state;

        internal DeterministicRandom(uint seed) => state = seed == 0 ? 0xA341316Cu : seed;

        internal int Next(int exclusiveMaximum)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (int)(state % (uint)exclusiveMaximum);
        }
    }
}
