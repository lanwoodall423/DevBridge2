using System.Text.Json;
using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestNamedPipeStopCompletesClient()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        List<string> received = RunNamedPipeCommand(fixture, "stop", "T001");

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

        Assert(received.Count == 1, "stop --json did not receive one JSON response");
        using JsonDocument document = JsonDocument.Parse(received[0]);
        Assert(document.RootElement.GetProperty("state").GetString() == "STOPPED",
            "stop --json response did not report STOPPED");
        Assert(document.RootElement.GetProperty("coordinatorBuild").GetProperty("sourceRevision").GetString() != null,
            "stop --json response did not report coordinator build identity");
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
        BridgeRequest request = NewProtocolRequest("coordinator", "shutdown");
        request.Json = true;
        List<CoordinatorIpcFrame> frames = SendRawProtocolRequest(harness, request);
        Assert(frames.Count(frame => frame.Type == CoordinatorIpcProtocol.ResultType) == 1,
            "shutdown must produce exactly one terminal result");
        int resultIndex = frames.FindIndex(frame => frame.Type == CoordinatorIpcProtocol.ResultType);
        Assert(resultIndex >= 0 && harness.ServerTask.Wait(TimeSpan.FromSeconds(5)),
            "shutdown must flush its result before releasing the server");
        Assert(resultIndex == frames.Count - 1, "shutdown result must be the final IPC frame");
        Assert(frames[resultIndex].ExitCode == 0, "shutdown returned a failure result");
        Assert(frames[resultIndex].Payload.HasValue, "shutdown result omitted its JSON payload");
        using JsonDocument document = JsonDocument.Parse(frames[resultIndex].Payload.Value.GetRawText());
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
        string previousTestRimWorldPath = Environment.GetEnvironmentVariable("DEVBRIDGE_TEST_RIMWORLD_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DEVBRIDGE_READINESS_TIMEOUT_SECONDS", "31");
            Environment.SetEnvironmentVariable("DEVBRIDGE_RIMBRIDGE_MODE", "off");
            Environment.SetEnvironmentVariable("DEVBRIDGE_TEST_RIMWORLD_PATH", fixture.RimWorldPath);
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
            Environment.SetEnvironmentVariable("DEVBRIDGE_TEST_RIMWORLD_PATH", previousTestRimWorldPath);
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

    private static void TestFiniteJsonTimeoutReportsLiveness()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        string slot = RuntimeScope.ForRoot(fixture.Root);
        using NamedPipeServerStream server = new(PipeNames.ForSlot(fixture.Root, slot),
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task accept = Task.Run(() =>
        {
            try
            {
                server.WaitForConnection();
                using StreamReader reader = new(server, Encoding.UTF8, false, 4096, leaveOpen: true);
                reader.ReadLine();
                Thread.Sleep(500);
            }
            catch (IOException)
            {
            }
        });

        TextWriter previousOut = Console.Out;
        using StringWriter output = new();
        Console.SetOut(output);
        try
        {
            int exitCode = CoordinatorClient.Run(fixture.Root, new[] { "status", "--json" },
                slot, null, null, TimeSpan.FromMilliseconds(150));
            Assert(exitCode == 4, "finite JSON timeout did not return the liveness failure exit code");
        }
        finally
        {
            Console.SetOut(previousOut);
            server.Dispose();
            accept.Wait(TimeSpan.FromSeconds(2));
        }

        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement response = document.RootElement;
        Assert(response.GetProperty("errorCode").GetString() == "DEVBRIDGE_COMMAND_TIMEOUT",
            "finite JSON timeout did not preserve its error code");
        Assert(response.GetProperty("timeoutBoundary").GetString() == "coordinator-response",
            "finite JSON timeout did not identify the response boundary");
        Assert(response.GetProperty("commandMayHaveBeenAccepted").GetBoolean() &&
            response.GetProperty("retrySafe").GetBoolean(),
            "finite JSON timeout did not classify a read-only retry as safe");
    }

    private static void TestDurableWaitResponsePolicyRemainsUnbounded()
    {
        Assert(!CoordinatorResponsePolicy.IsFinite("wait-ready", Array.Empty<string>()),
            "wait-ready must remain a durable wait");
        Assert(!CoordinatorResponsePolicy.IsFinite("restart", Array.Empty<string>()),
            "restart must remain a durable wait");
        Assert(!CoordinatorResponsePolicy.IsFinite("ensure-ready", Array.Empty<string>()),
            "ensure-ready must remain a durable wait");
        Assert(!CoordinatorResponsePolicy.IsFinite("test", new[] { "begin" }),
            "test begin must remain a durable wait");
        Assert(!CoordinatorResponsePolicy.IsFinite("test", new[] { "session" }),
            "test session must remain a durable wait");
        Assert(CoordinatorResponsePolicy.IsFinite("status", Array.Empty<string>()),
            "status must have a bounded terminal response");
    }

    private static void TestVersionedIpcRequestIdAndTerminalResult()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        BridgeRequest request = NewProtocolRequest("status");
        List<CoordinatorIpcFrame> frames = SendRawProtocolRequest(harness, request);

        Assert(CoordinatorIpcProtocol.IsValidRequestId(request.RequestId),
            "the request did not carry a stable requestId");
        Assert(frames.Count > 0 && frames.All(value => value.ProtocolVersion == CoordinatorIpcProtocol.Version),
            "IPC response frames did not carry the supported protocol version");
        Assert(frames.All(value => value.RequestId == request.RequestId),
            "IPC response frames did not preserve requestId correlation");
        Assert(frames.Count(value => value.Type == CoordinatorIpcProtocol.ResultType) == 1,
            "finite status did not produce exactly one terminal result");
        CoordinatorIpcFrame result = frames.Single(value => value.Type == CoordinatorIpcProtocol.ResultType);
        Assert(result.ExitCode == 0 && result.CoordinatorBuild != null,
            "terminal result did not include exitCode and coordinator build metadata");
    }

    private static void TestVersionedIpcEventsAndSingleResult()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        List<CoordinatorIpcFrame> frames = SendRawProtocolRequest(harness,
            NewProtocolRequest("stop", "T001"));

        int resultIndex = frames.FindIndex(value => value.Type == CoordinatorIpcProtocol.ResultType);
        Assert(resultIndex > 0, "stop did not emit an event before its terminal result");
        Assert(frames.Take(resultIndex).All(value => value.Type == CoordinatorIpcProtocol.EventType),
            "an IPC result was confused with an event");
        Assert(frames.Count(value => value.Type == CoordinatorIpcProtocol.ResultType) == 1 &&
               resultIndex == frames.Count - 1,
            "stop did not end with exactly one terminal result");
        Assert(frames[resultIndex].ExitCode == 0,
            "stop terminal result was not successful");
    }

    private static void TestVersionedIpcLongRunningSession()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Exception failure = RunAgainstFakeServer(fixture, new[] { "test", "session" }, (request, writer) =>
        {
            writer.WriteLine(JsonSerializer.Serialize(
                CoordinatorIpcProtocol.Event(request.RequestId, "session event"), Program.JsonOptions));
            Thread.Sleep(TimeSpan.FromSeconds(2.2));
            writer.WriteLine(JsonSerializer.Serialize(
                CoordinatorIpcProtocol.Result(request.RequestId, 0, null, null), Program.JsonOptions));
        });
        Assert(failure == null,
            "a long-running session was incorrectly subject to the finite response timeout");
    }

    private static void TestVersionedIpcJsonAndHumanClients()
    {
        using (Fixture jsonFixture = Fixture.ReadyWithLease())
        {
            List<string> json = RunNamedPipeCommand(jsonFixture, "status", "--json");
            Assert(json.Count == 1 && json[0].StartsWith("{", StringComparison.Ordinal),
                "JSON client did not receive one JSON payload");
            using JsonDocument document = JsonDocument.Parse(json[0]);
            Assert(document.RootElement.GetProperty("coordinatorBuild").GetProperty("protocolContract")
                .GetString() == DevBridgeSchemaVersions.CoordinatorProtocolContract,
                "JSON client payload did not expose the v2 coordinator protocol contract");
        }

        using (Fixture humanFixture = Fixture.ReadyWithLease())
        {
            List<string> messages = RunNamedPipeCommand(humanFixture, "stop", "T001");
            Assert(messages.Any(value => value.Contains("gameState=STOPPED", StringComparison.Ordinal)),
                "human client did not receive event messages");
        }
    }

    private static void TestVersionedIpcMalformedRequest()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        List<string> lines = SendRawLine(harness, "{not-json");
        Assert(lines.Count == 1, "malformed request did not receive one bounded protocol response");
        CoordinatorIpcFrame frame = DeserializeFrame(lines[0]);
        Assert(frame.Type == CoordinatorIpcProtocol.ResultType && frame.ExitCode == 2,
            "malformed request did not receive a terminal failure result");
        Assert(frame.Payload.HasValue && frame.Payload.Value.GetProperty("errorCode").GetString() == "MALFORMED_REQUEST",
            "malformed request failure did not identify its protocol error");
    }

    private static void TestVersionedIpcUnsupportedProtocol()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        BridgeRequest legacy = NewProtocolRequest("status");
        legacy.ProtocolVersion = 1;
        List<string> lines = SendRawLine(harness, JsonSerializer.Serialize(legacy, Program.JsonOptions));
        CoordinatorIpcFrame frame = DeserializeFrame(lines.Single());
        Assert(frame.Type == CoordinatorIpcProtocol.ResultType && frame.RequestId == legacy.RequestId &&
               frame.Payload.HasValue && frame.Payload.Value.GetProperty("errorCode").GetString() ==
               "INCOMPATIBLE_PROTOCOL" && frame.Payload.Value.GetProperty("error").GetString()
                   .Contains("supported v2", StringComparison.Ordinal),
            "unsupported protocol did not fail immediately with a clear machine-readable compatibility error");
    }

    private static void TestVersionedIpcMismatchedRequestId()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Exception failure = RunAgainstFakeServer(fixture, new[] { "status" }, (request, writer) =>
        {
            CoordinatorIpcFrame frame = CoordinatorIpcProtocol.Result("wrong-request-id", 0, null, null);
            writer.WriteLine(JsonSerializer.Serialize(frame, Program.JsonOptions));
        });
        Assert(failure is IOException && failure.Message.Contains("requestId", StringComparison.Ordinal),
            "client accepted a response with a mismatched requestId");
    }

    private static void TestVersionedIpcDisconnectBeforeResult()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        Exception failure = RunAgainstFakeServer(fixture, new[] { "status" }, (request, writer) => { });
        Assert(failure is IOException && failure.Message.Contains("terminal IPC result", StringComparison.Ordinal),
            "client did not fail clearly when the server disconnected before a result");
    }

    private static void TestVersionedIpcDuplicateResult()
    {
        CoordinatorIpcFrame result = CoordinatorIpcProtocol.Result("request-1", 0, null, null);
        Assert(CoordinatorIpcProtocol.TryValidateResponse(result, "request-1", false, out _),
            "the first terminal result was rejected");
        Assert(!CoordinatorIpcProtocol.TryValidateResponse(result, "request-1", true, out string error) &&
               error.Contains("duplicate terminal result", StringComparison.Ordinal),
            "duplicate terminal results were not rejected deterministically");
    }

    private static void TestCoordinatorBuildIdentity()
    {
        string productVersion = ComponentVersions.Current.CoordinatorVersion;
        DateTime started = new(2026, 8, 16, 12, 34, 56, DateTimeKind.Utc);
        CoordinatorBuildIdentity first = CoordinatorBuildIdentity.FromInformationalVersion(
            productVersion + "+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Release", started);
        CoordinatorBuildIdentity second = CoordinatorBuildIdentity.FromInformationalVersion(
            productVersion + "+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Release", started);
        Assert(first.ProductVersion == second.ProductVersion &&
               first.SourceRevision != second.SourceRevision &&
               first.InformationalVersion != second.InformationalVersion,
            "different published revisions were not distinguishable");
        Assert(first.ProcessStartedUtc == started && first.CoordinatorProtocolVersion ==
            DevBridgeSchemaVersions.CoordinatorProtocolMajor,
            "build identity did not retain process start or protocol metadata");
        CoordinatorBuildIdentity dirty = CoordinatorBuildIdentity.FromInformationalVersion(
            productVersion + "+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.dirty", "Debug", started);
        Assert(dirty.Dirty && dirty.SourceRevision == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" &&
               dirty.BuildConfiguration == "Debug",
            "dirty build identity did not preserve revision and configuration without claiming a clean build");
        CoordinatorIpcFrame identityResult = CoordinatorIpcProtocol.Result(
            "request-identity", 0, null, first, second, false);
        string identityJson = JsonSerializer.Serialize(identityResult, Program.JsonOptions);
        using JsonDocument identityDocument = JsonDocument.Parse(identityJson);
        Assert(identityDocument.RootElement.GetProperty("coordinatorBuild").GetProperty("sourceRevision")
                   .GetString() == first.SourceRevision &&
               identityDocument.RootElement.GetProperty("publishedCoordinatorBuild")
                   .GetProperty("sourceRevision").GetString() == second.SourceRevision &&
               !identityDocument.RootElement.GetProperty("coordinatorBuildMatchesPublished").GetBoolean(),
            "IPC result metadata did not distinguish running and published coordinator revisions");

        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        List<string> status = harness.Send("status", "--json");
        using JsonDocument document = JsonDocument.Parse(status.Single());
        JsonElement build = document.RootElement.GetProperty("coordinatorBuild");
        Assert(build.GetProperty("sourceRevision").GetString() != null &&
               build.GetProperty("informationalVersion").GetString().Contains("+", StringComparison.Ordinal) &&
               build.GetProperty("processStartedUtc").GetString() != null &&
               build.GetProperty("coordinatorProtocolVersion").GetInt32() ==
               DevBridgeSchemaVersions.CoordinatorProtocolMajor,
            "status --json did not expose the running coordinator build identity");

        using Fixture doctorFixture = new(new PersistedState { Phase = BridgePhase.STOPPED });
        using CoordinatorHarness doctorHarness = CoordinatorHarness.Start(doctorFixture);
        List<string> doctor = doctorHarness.Send("doctor", "--json");
        doctorHarness.Shutdown();
        using JsonDocument doctorDocument = JsonDocument.Parse(doctor.Single());
        JsonElement doctorBuild = doctorDocument.RootElement.GetProperty("components")
            .GetProperty("coordinatorBuild");
        Assert(doctorBuild.GetProperty("protocolContract").GetString() ==
               DevBridgeSchemaVersions.CoordinatorProtocolContract &&
               doctorBuild.GetProperty("sourceRevision").GetString() != null &&
               doctorBuild.GetProperty("processStartedUtc").GetString() != null,
            "doctor --json did not expose the coordinator build identity in its version section");
    }

    private static void TestCoordinatorPipeTrustBoundaryAndLimits()
    {
        Assert((CoordinatorPipeSecurity.ServerOptions & PipeOptions.CurrentUserOnly) != 0,
            "the production coordinator pipe is not restricted to the current Windows user");
        Assert(CoordinatorServer.MaxConcurrentClients > 0 &&
               CoordinatorServer.MaxConcurrentClients <= 16,
            "coordinator client concurrency is not explicitly bounded");

        BridgeRequest request = NewProtocolRequest("status");
        request.Command = new string('c', CoordinatorIpcProtocol.MaxCommandLength + 1);
        Assert(!CoordinatorIpcProtocol.TryValidateRequest(request, out string errorCode, out _) &&
               errorCode == "COMMAND_TOO_LONG", "long commands did not receive a stable limit error");

        request = NewProtocolRequest("status", new string('a', CoordinatorIpcProtocol.MaxArgumentLength + 1));
        Assert(!CoordinatorIpcProtocol.TryValidateRequest(request, out errorCode, out _) &&
               errorCode == "ARGUMENT_TOO_LONG", "long arguments did not receive a stable limit error");

        request = NewProtocolRequest("status");
        request.Arguments = Enumerable.Repeat("a", CoordinatorIpcProtocol.MaxArgumentCount + 1).ToList();
        Assert(!CoordinatorIpcProtocol.TryValidateRequest(request, out errorCode, out _) &&
               errorCode == "ARGUMENT_COUNT_EXCEEDED", "too many arguments did not receive a stable limit error");

        CoordinatorIpcFrame boundedEvent = CoordinatorIpcProtocol.Event("request",
            new string('e', CoordinatorIpcProtocol.MaxEventMessageLength + 100));
        Assert(boundedEvent.Message.Length == CoordinatorIpcProtocol.MaxEventMessageLength,
            "event output was not bounded before serialization");

        CoordinatorIpcFrame boundedResult = CoordinatorIpcProtocol.Result("request", 0,
            new { output = new string('p', CoordinatorIpcProtocol.MaxOutputPayloadLength + 100) }, null);
        Assert(boundedResult.ExitCode == 2 && boundedResult.Payload.HasValue &&
               boundedResult.Payload.Value.GetProperty("errorCode").GetString() == "OUTPUT_TOO_LARGE",
            "oversized result payload did not become a bounded machine-readable failure");
    }

    private static void TestOversizedAndMalformedRequestsAreMutationFree()
    {
        using Fixture fixture = new(new PersistedState { Phase = BridgePhase.STOPPED });
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        string secret = "rimbridge-secret-must-not-appear";
        List<string> oversized = SendRawLine(harness,
            new string('x', CoordinatorIpcProtocol.MaxRequestLength + 20) + secret);
        CoordinatorIpcFrame oversizedFrame = DeserializeFrame(oversized.Single());
        Assert(oversizedFrame.Payload.HasValue &&
               oversizedFrame.Payload.Value.GetProperty("errorCode").GetString() == "REQUEST_TOO_LARGE" &&
               !oversized.Single().Contains(secret, StringComparison.Ordinal),
            "oversized request did not receive a bounded redacted error");

        List<string> malformed = SendRawLine(harness,
            "{\"protocolVersion\":2,\"requestId\":\"bad\",\"type\":\"request\",\"command\":\"status\",\"arguments\":[\"" +
            secret + "\"]");
        CoordinatorIpcFrame malformedFrame = DeserializeFrame(malformed.Single());
        Assert(malformedFrame.Payload.HasValue &&
               malformedFrame.Payload.Value.GetProperty("errorCode").GetString() == "MALFORMED_REQUEST" &&
               !malformed.Single().Contains(secret, StringComparison.Ordinal),
            "malformed request did not receive a bounded redacted error");

        harness.Shutdown();
        PersistedState state = ReadPersistedState(fixture.Root);
        Assert(state.Phase == BridgePhase.STOPPED && state.Generation == 0 &&
               fixture.Adapter.LaunchCalls == 0,
            "malformed or oversized IPC input mutated state or started RimWorld");
    }

    private static void TestRuntimeNamespaceInvariants()
    {
        string root = Path.Combine(Path.GetTempPath(), "DevBridge2-namespace-" + Guid.NewGuid().ToString("N"), "Root");
        string otherRoot = Path.Combine(Path.GetDirectoryName(root), "Other");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(otherRoot);
        string previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Path.GetTempPath());
            string firstSlot = RuntimeScope.ForRoot(root);
            string firstPipe = PipeNames.ForSlot(root, firstSlot);
            string firstMutex = PipeNames.MutexForSlot(root, firstSlot);
            string opaqueHash = RuntimeScope.HashOpaqueIdentifier("slot-runtime-opaque");

            Directory.SetCurrentDirectory(Path.GetDirectoryName(root));
            string secondSlot = RuntimeScope.ForRoot(root.ToLowerInvariant());
            Assert(firstSlot == secondSlot && firstPipe == PipeNames.ForSlot(root, secondSlot) &&
                   firstMutex == PipeNames.MutexForSlot(root, secondSlot),
                "root namespace names changed with working directory or Windows case normalization");
            Assert(firstSlot.Length == "slot-".Length + RuntimeScope.RuntimeSlotHashHexLength &&
                   firstPipe.Length > RuntimeScope.RuntimeSlotHashHexLength &&
                   firstMutex.Length > RuntimeScope.RuntimeSlotHashHexLength,
                "runtime namespace identifiers were not widened to the practical 96-bit length");

            Directory.SetCurrentDirectory(Path.GetTempPath());
            string opaqueFromTemp = RuntimeScope.HashOpaqueIdentifier("slot-runtime-opaque");
            Directory.SetCurrentDirectory(Path.GetDirectoryName(root));
            string opaqueFromParent = RuntimeScope.HashOpaqueIdentifier("slot-runtime-opaque");
            Assert(opaqueHash == opaqueFromTemp && opaqueHash == opaqueFromParent,
                "opaque runtime slot hashing depended on the process working directory");

            string otherSlot = RuntimeScope.ForRoot(otherRoot);
            Assert(firstSlot != otherSlot &&
                   firstPipe != PipeNames.ForSlot(otherRoot, otherSlot) &&
                   firstMutex != PipeNames.MutexForSlot(otherRoot, otherSlot),
                "different roots shared coordinator ownership identities");
            Assert(PipeNames.ForRoot(root) == PipeNames.ForSlot(root, firstSlot),
                "wrapper and direct server startup did not derive the same pipe name");
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            try { Directory.Delete(Path.GetDirectoryName(root), true); } catch { }
        }
    }

    private static void TestIdentifierStrengthAndLegacyCompatibility()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 1,
            Phase = BridgePhase.STOPPED,
            MaintenanceReady = true
        });
        int begin = fixture.State.Execute(Request("test", "new-agent", 77, "begin"), _ => { }, () => true);
        PersistedState acquired = ReadPersistedState(fixture.Root);
        Assert(begin == 0 && acquired.Leases.Count == 1 && acquired.Leases[0].Id.StartsWith("lease-", StringComparison.Ordinal) &&
               acquired.Leases[0].Id.Length >= "lease-".Length + 32,
            "new durable lease IDs were not full-width capabilities");

        using Fixture legacyLeaseFixture = Fixture.MaintenanceWithLease();
        int renew = legacyLeaseFixture.State.Execute(Request("test", "holder", 77, "renew", "T001"), _ => { }, () => true);
        PersistedState legacyLease = ReadPersistedState(legacyLeaseFixture.Root);
        Assert(renew == 0 && legacyLease.Leases.Any(value => value.Id == "T001"),
            "an existing short persisted lease was not retained safely for backward compatibility");

        using Fixture legacySlotFixture = new(new PersistedState { Phase = BridgePhase.STOPPED });
        string legacySlot = RuntimeScope.LegacyForRoot(legacySlotFixture.Root);
        File.WriteAllText(Path.Combine(legacySlotFixture.Root, "Runtime", "state.json"),
            JsonSerializer.Serialize(new PersistedState
            {
                CoordinatorRoot = legacySlotFixture.Root,
                RuntimeSlotId = legacySlot,
                Phase = BridgePhase.STOPPED
            }, Program.JsonOptions));
        Exception failure = null;
        try
        {
            RuntimeScope.ResolveEffectiveSlot(legacySlotFixture.Root, null, null);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        Assert(failure is InvalidOperationException && failure.Message.Contains("legacy runtime slot", StringComparison.Ordinal) &&
               failure.Message.Contains("coordinator shutdown", StringComparison.Ordinal) &&
               failure.Message.Contains("do not delete", StringComparison.Ordinal),
            "legacy persisted runtime slots did not fail with actionable migration guidance");
    }

    private static void TestLegacyRuntimeSlotMigration()
    {
        using Fixture fixture = new(new PersistedState
        {
            Generation = 7,
            Phase = BridgePhase.ERROR,
            ErrorCode = "LEGACY_SLOT_TEST",
            Error = "preserve this durable diagnostic",
            ProcessId = 0,
            ScopeTickets = new List<ScopeTicket>()
        });
        string oldSlot = RuntimeScope.LegacyForRoot(fixture.Root);
        string currentSlot = RuntimeScope.ForRoot(fixture.Root);
        PersistedState legacy = ReadPersistedState(fixture.Root);
        legacy.CoordinatorRoot = fixture.Root;
        legacy.RuntimeSlotId = oldSlot;
        legacy.Phase = BridgePhase.ERROR;
        legacy.ProcessId = 0;
        legacy.ProcessStartUtcTicks = 0;
        legacy.Leases = new List<TestLease>();
        legacy.ScopeTickets = new List<ScopeTicket>
        {
            new() { Id = "ticket-preserved", RuntimeSlotId = oldSlot, CoordinatorRoot = fixture.Root }
        };
        fixture.WriteState(legacy);
        string statePath = Path.Combine(fixture.Root, "Runtime", "state.json");
        byte[] original = File.ReadAllBytes(statePath);

        CoordinatorLegacySlotMigrationResult migrated = CoordinatorLegacySlotMigration.TryMigrate(fixture.Root);
        Assert(migrated.Success && !migrated.AlreadyMigrated && migrated.OldRuntimeSlotId == oldSlot &&
               migrated.RuntimeSlotId == currentSlot && migrated.MigratedScopeTicketCount == 1 &&
               migrated.BackupPath != null,
            "legacy migration did not report the exact old/new slot mapping");
        string backupPath = Path.Combine(fixture.Root, migrated.BackupPath);
        Assert(File.Exists(backupPath) && File.ReadAllBytes(backupPath).SequenceEqual(original),
            "legacy migration did not preserve an exact backup of the original state");

        PersistedState after = ReadPersistedState(fixture.Root);
        Assert(after.RuntimeSlotId == currentSlot && after.Generation == legacy.Generation &&
               after.Phase == legacy.Phase && after.ErrorCode == legacy.ErrorCode &&
               after.Error == legacy.Error && after.ScopeTickets.Count == 1 &&
               after.ScopeTickets[0].RuntimeSlotId == currentSlot &&
               after.ScopeTickets[0].Id == legacy.ScopeTickets[0].Id,
            "legacy migration changed durable state beyond runtime-slot ownership");
        Assert(RuntimeScope.ResolveEffectiveSlot(fixture.Root, null, null) == currentSlot,
            "the current coordinator could not resolve the migrated runtime slot");

        CoordinatorLegacySlotMigrationResult repeated = CoordinatorLegacySlotMigration.TryMigrate(fixture.Root);
        Assert(repeated.Success && repeated.AlreadyMigrated && repeated.BackupPath == null,
            "legacy migration was not idempotent after the atomic replacement");

        legacy.RuntimeSlotId = oldSlot;
        legacy.ProcessId = Environment.ProcessId;
        legacy.ProcessStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        fixture.WriteState(legacy);
        byte[] beforeRunningProcessFailure = File.ReadAllBytes(statePath);
        CoordinatorLegacySlotMigrationResult blocked = CoordinatorLegacySlotMigration.TryMigrate(fixture.Root);
        Assert(!blocked.Success && blocked.ErrorCode == "MIGRATION_PROCESS_RUNNING" &&
               File.ReadAllBytes(statePath).SequenceEqual(beforeRunningProcessFailure),
            "migration did not fail closed when the persisted RimWorld process identity was still running");
    }

    private static void TestTwoCoordinatorsCannotOwnSameSlot()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness first = CoordinatorHarness.Start(fixture);
        Task<int> competitor = Task.Run(() => CoordinatorServer.Run(fixture.Root, first.Slot));
        Assert(competitor.Wait(TimeSpan.FromSeconds(3)) && competitor.Result == 0,
            "a second coordinator did not fail closed on the existing slot mutex");
        first.Shutdown();
    }

    private static BridgeRequest NewProtocolRequest(string command, params string[] arguments)
    {
        return new BridgeRequest
        {
            ProtocolVersion = CoordinatorIpcProtocol.Version,
            RequestId = CoordinatorIpcProtocol.NewRequestId(),
            Type = CoordinatorIpcProtocol.RequestType,
            Command = command,
            Arguments = arguments.ToList(),
            Agent = "holder",
            ClientProcessId = Environment.ProcessId,
            Json = false
        };
    }

    private static List<CoordinatorIpcFrame> SendRawProtocolRequest(CoordinatorHarness harness,
        BridgeRequest request)
    {
        request.CoordinatorRoot ??= harness.Fixture.Root;
        request.RuntimeSlotId ??= harness.Slot;
        using NamedPipeClientStream pipe = new(".", PipeNames.ForSlot(harness.Fixture.Root, harness.Slot),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(2000);
        using StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.AutoFlush = true;
        writer.WriteLine(JsonSerializer.Serialize(request, Program.JsonOptions));

        List<CoordinatorIpcFrame> frames = new();
        string line;
        while ((line = CoordinatorIpcProtocol.ReadFrameLine(reader)) != null)
        {
            frames.Add(DeserializeFrame(line));
            if (frames[^1].Type == CoordinatorIpcProtocol.ResultType)
                break;
        }
        return frames;
    }

    private static List<string> SendRawLine(CoordinatorHarness harness, string line)
    {
        using NamedPipeClientStream pipe = new(".", PipeNames.ForSlot(harness.Fixture.Root, harness.Slot),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(2000);
        using StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.AutoFlush = true;
        writer.WriteLine(line);

        List<string> lines = new();
        string response;
        while ((response = CoordinatorIpcProtocol.ReadFrameLine(reader)) != null)
        {
            lines.Add(response);
            try
            {
                if (DeserializeFrame(response).Type == CoordinatorIpcProtocol.ResultType)
                    break;
            }
            catch (JsonException)
            {
                // Unsupported protocol v1 deliberately receives a bounded
                // human-readable compatibility error rather than a v2 frame.
                break;
            }
        }
        return lines;
    }

    private static CoordinatorIpcFrame DeserializeFrame(string line)
    {
        return JsonSerializer.Deserialize<CoordinatorIpcFrame>(line, Program.JsonOptions)
            ?? throw new InvalidOperationException("IPC test frame was null");
    }

    private static Exception RunAgainstFakeServer(Fixture fixture, IReadOnlyList<string> command,
        Action<BridgeRequest, StreamWriter> responder)
    {
        string slot = RuntimeScope.ForRoot(fixture.Root);
        using NamedPipeServerStream server = new(PipeNames.ForSlot(fixture.Root, slot), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Exception serverFailure = null;
        Task serverTask = Task.Run(() =>
        {
            try
            {
                server.WaitForConnection();
                using (StreamReader reader = new(server, Encoding.UTF8, false, 4096, leaveOpen: true))
                using (StreamWriter writer = new(server, new UTF8Encoding(false), 4096, leaveOpen: true))
                {
                    writer.AutoFlush = true;
                    string requestLine = CoordinatorIpcProtocol.ReadFrameLine(reader);
                    BridgeRequest request = JsonSerializer.Deserialize<BridgeRequest>(requestLine, Program.JsonOptions);
                    responder(request, writer);
                }

                try
                {
                    server.Dispose();
                }
                catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException)
                {
                    // A fake server disconnect is expected when the client
                    // rejects a response or is waiting for a terminal result.
                }
            }
            catch (Exception exception)
            {
                serverFailure = exception;
            }
        });

        Exception clientFailure = null;
        try
        {
            CoordinatorClient.Run(fixture.Root, command, slot, null, null,
                TimeSpan.FromSeconds(2));
        }
        catch (Exception exception)
        {
            clientFailure = exception;
        }
        finally
        {
            try
            {
                server.Dispose();
            }
            catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException)
            {
                // A fake server disconnect is part of the test contract.
            }
            serverTask.Wait(TimeSpan.FromSeconds(3));
        }

        if (serverFailure != null)
            throw new InvalidOperationException($"fake IPC server failed: {serverFailure.Message}", serverFailure);
        return clientFailure;
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
            Assert(shutdown.Any(value => value.Contains("Coordinator shutdown accepted", StringComparison.Ordinal)),
                "shutdown client did not receive its terminal event");
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
            ICoordinatorFaultInjector faultInjector = options?.FaultInjector;
            if (options != null)
                options.FaultInjector = null;
            ManualResetEventSlim started = new(false);
            ServerTask = Task.Run(() => CoordinatorServer.Run(fixture.Root, Slot, null, options,
                state =>
                {
                    state.SetFaultInjectorForTesting(faultInjector);
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
        internal bool SkipShutdownOnDispose { get; set; }

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
            if (SkipShutdownOnDispose)
            {
                if (ServerTask.IsFaulted)
                {
                    try
                    {
                        ServerTask.GetAwaiter().GetResult();
                    }
                    catch (Exception exception) when (exception is CoordinatorFaultInjectedException ||
                               exception is AggregateException aggregate &&
                               aggregate.Flatten().InnerExceptions.All(value =>
                                   value is CoordinatorFaultInjectedException))
                    {
                        // The caller already observed the expected injected
                        // server death and deliberately skipped a new IPC call.
                    }
                }
                return;
            }
            if (ServerTask.IsFaulted)
            {
                try
                {
                    ServerTask.GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is CoordinatorFaultInjectedException ||
                           exception is AggregateException aggregate &&
                           aggregate.Flatten().InnerExceptions.All(value =>
                               value is CoordinatorFaultInjectedException))
                {
                    // Fault-injection tests deliberately terminate the host at a
                    // named boundary; observe and consume only that expected
                    // server-task failure during harness cleanup.
                }
                return;
            }
            try
            {
                Shutdown();
            }
            catch (Exception exception) when (exception is CoordinatorFaultInjectedException ||
                       exception is AggregateException aggregate &&
                       aggregate.Flatten().InnerExceptions.All(value =>
                           value is CoordinatorFaultInjectedException))
            {
                // A fault can race the IsCompleted check while cleanup is
                // issuing its final shutdown request. It is still the named
                // injected failure, not an additional test failure.
                return;
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
            BeforeModsConfigWrite = fixture.BeforeModsConfigWrite,
            FaultInjector = fixture.FaultInjector
        };
    }
}
