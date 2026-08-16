using System.Text.Json;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static AgentResponse ExecuteAgent(Fixture fixture, string agent, int processId,
        params string[] arguments)
    {
        BridgeRequest request = Request("agent", agent, processId, arguments);
        request.Json = true;
        int exitCode = fixture.State.Execute(request, _ => { }, () => true);
        AgentResponse response = fixture.State.CreateAgentJsonResponse(request, exitCode);
        Assert(response != null, "agent response must be created");
        Assert(response.ExitCode == exitCode, "agent response exit code must match execution");
        return response;
    }

    private static void TestAgentCapabilities()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        AgentCapabilitiesResponse capabilities = ExecuteAgent(fixture, "holder", 77,
            "capabilities") as AgentCapabilitiesResponse;
        Assert(capabilities != null, "capabilities must use the dedicated response type");
        Assert(capabilities.SchemaVersion == DevBridgeSchemaVersions.AgentCapabilitiesContract,
            "capabilities schema must be versioned");
        Assert(capabilities.Features.Snapshot && capabilities.Features.Delta &&
               capabilities.Features.WaitEvent && capabilities.Features.TestRecipes &&
               capabilities.Features.Plan && capabilities.Features.BuildPlan,
            "agent capabilities must advertise the supported operations");
        Assert(capabilities.Features.SemanticLogs,
            "agent capabilities must advertise the implemented semantic log query");
        string json = JsonSerializer.Serialize(capabilities, Program.JsonOptions);
        Assert(json.Length < 8 * 1024, "capabilities must be size bounded");
    }

    private static void TestAgentBuildPlan()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        AgentBuildPlanResponse plan = ExecuteAgent(fixture, "holder", 77,
            "build-plan") as AgentBuildPlanResponse;
        Assert(plan != null, "build-plan must use the dedicated response type");
        Assert(plan.SchemaVersion == DevBridgeSchemaVersions.AgentBuildPlanContract,
            "build-plan schema must be versioned");
        Assert(plan.ComponentBuilds != null && plan.ComponentBuilds.Mod != null &&
               plan.ComponentBuilds.BridgeTools != null,
            "build-plan must report bounded component publication state");
        Assert(plan.ComponentBuilds.Mod.LoadedStatus != "loaded" &&
               plan.ComponentBuilds.BridgeTools.LoadedStatus != "loaded",
            "build-plan must not claim external components are loaded without a host proof");
        string json = JsonSerializer.Serialize(plan, Program.JsonOptions);
        Assert(json.Length < 16 * 1024, "build-plan must be compact and size bounded");
    }

    private static void TestAgentSnapshot()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        AgentSnapshotResponse snapshot = ExecuteAgent(fixture, "holder", 77,
            "snapshot") as AgentSnapshotResponse;
        Assert(snapshot != null, "snapshot must use the dedicated response type");
        Assert(snapshot.SchemaVersion == DevBridgeSchemaVersions.AgentSnapshotContract,
            "snapshot schema must be versioned");
        Assert(snapshot.Phase == nameof(BridgePhase.READY) && snapshot.Generation == 1,
            "snapshot must report authoritative ready state");
        Assert(snapshot.RequestingAgentLease.State == "held",
            "snapshot must report the requesting agent lease");
        Assert(snapshot.NextAction == "run-tests" && snapshot.SafeActions.Contains("run-tests"),
            "ready lease holder must be told to run tests");
        Assert(snapshot.RimBridgeEndpoint.State == "disabled",
            "snapshot must expose a compact RimBridge endpoint category");

        string json = JsonSerializer.Serialize(snapshot, Program.JsonOptions);
        Assert(json.Length < 32 * 1024, "snapshot must be compact and size bounded");
        Assert(!json.Contains("\"host\"", StringComparison.OrdinalIgnoreCase) &&
               !json.Contains("\"port\"", StringComparison.OrdinalIgnoreCase) &&
               !json.Contains("\"token\"", StringComparison.OrdinalIgnoreCase) &&
               !json.Contains("Player.log", StringComparison.OrdinalIgnoreCase),
            "snapshot must not expose RimBridge endpoint secrets or raw logs");
    }

    private static void TestAgentDeltaJournal()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        AgentSnapshotResponse before = ExecuteAgent(fixture, "holder", 77, "snapshot") as AgentSnapshotResponse;
        PersistedState initialized = ReadPersistedState(fixture.Root);
        Assert(initialized.AgentEpoch == before.Epoch && initialized.AgentSequence == before.Sequence,
            "agent epoch and sequence must be durable");

        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "stop must create a durable agent change");
        AgentDeltaResponse delta = ExecuteAgent(fixture, "holder", 77, "delta",
            "--epoch", before.Epoch, "--since", before.Sequence.ToString()) as AgentDeltaResponse;
        Assert(delta != null && delta.ErrorCode == null && delta.ToSeq > before.Sequence,
            "delta must return changes after the cursor");
        Assert(delta.Delta.TryGetValue("phase", out JsonElement phase) &&
               phase.GetString() == nameof(BridgePhase.STOPPED),
            "delta must identify the durable phase change");

        AgentDeltaResponse wrongEpoch = ExecuteAgent(fixture, "holder", 77, "delta",
            "--epoch", "old-epoch", "--since", "0") as AgentDeltaResponse;
        Assert(wrongEpoch.ExitCode != 0 && wrongEpoch.ErrorCode == "AGENT_EPOCH_MISMATCH",
            "delta must fail closed for an old epoch");

        string oldEpoch = before.Epoch;
        fixture.State.RequestShutdown();
        fixture.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
        fixture.State = fixture.Reload();
        AgentSnapshotResponse afterReload = ExecuteAgent(fixture, "holder", 77, "snapshot") as AgentSnapshotResponse;
        Assert(!string.Equals(oldEpoch, afterReload.Epoch, StringComparison.Ordinal) &&
               afterReload.Sequence == 0,
            "a new coordinator process must create a new epoch and reset the journal");
    }

    private static void TestAgentWaitEvent()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        AgentSnapshotResponse before = ExecuteAgent(fixture, "holder", 77, "snapshot") as AgentSnapshotResponse;
        Task<AgentResponse> waiter = Task.Run(() => ExecuteAgent(fixture, "holder", 77, "wait-event",
            "--epoch", before.Epoch, "--since", before.Sequence.ToString(), "--timeout-ms", "5000"));

        Thread.Sleep(100);
        Assert(!waiter.IsCompleted, "wait-event must remain pending without a durable change");
        Assert(fixture.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true) == 0,
            "stop must complete while an agent waiter is attached");
        Assert(waiter.Wait(TimeSpan.FromSeconds(2)), "wait-event must wake on the durable change");
        AgentWaitEventResponse result = waiter.Result as AgentWaitEventResponse;
        Assert(result != null && result.Result == "changed" && result.ToSeq > before.Sequence,
            "wait-event must return one compact changed result");
        Assert(result.Delta.TryGetValue("phase", out JsonElement phase) &&
               phase.GetString() == nameof(BridgePhase.STOPPED),
            "wait-event delta must contain the changed phase");
    }

    private static void TestAgentWaitEventTimeoutAndShutdown()
    {
        using (Fixture timeoutFixture = Fixture.ReadyWithLease())
        {
            AgentSnapshotResponse snapshot = ExecuteAgent(timeoutFixture, "holder", 77, "snapshot") as AgentSnapshotResponse;
            AgentWaitEventResponse timeout = ExecuteAgent(timeoutFixture, "holder", 77, "wait-event",
                "--epoch", snapshot.Epoch, "--since", snapshot.Sequence.ToString(),
                "--timeout-ms", "50") as AgentWaitEventResponse;
            Assert(timeout.Result == "timeout" && timeout.ExitCode == 0,
                "wait-event must return a successful bounded timeout result");
        }

        using Fixture shutdownFixture = Fixture.ReadyWithLease();
        AgentSnapshotResponse before = ExecuteAgent(shutdownFixture, "holder", 77, "snapshot") as AgentSnapshotResponse;
        Task<AgentResponse> waiter = Task.Run(() => ExecuteAgent(shutdownFixture, "holder", 77, "wait-event",
            "--epoch", before.Epoch, "--since", before.Sequence.ToString(), "--timeout-ms", "5000"));
        Thread.Sleep(100);
        shutdownFixture.State.RequestShutdown();
        Assert(waiter.Wait(TimeSpan.FromSeconds(2)), "shutdown did not wake agent wait-event");
        AgentWaitEventResponse shutdown = waiter.Result as AgentWaitEventResponse;
        Assert(shutdown.Result == "shutdown" && shutdown.ExitCode == 0,
            "shutdown wake must be a successful terminal wait-event result");
    }

    private static void TestAgentIpc()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        using CoordinatorHarness harness = CoordinatorHarness.Start(fixture);
        BridgeRequest request = NewProtocolRequest("agent", "snapshot");
        request.Json = true;
        request.ClientProcessId = 77;
        List<CoordinatorIpcFrame> frames = SendRawProtocolRequest(harness, request);
        Assert(frames.Count(value => value.Type == CoordinatorIpcProtocol.ResultType) == 1,
            "agent IPC request must produce exactly one terminal result");
        Assert(frames.All(value => value.RequestId == request.RequestId &&
            value.ProtocolVersion == CoordinatorIpcProtocol.Version),
            "agent IPC frames must preserve v2 correlation");
        CoordinatorIpcFrame result = frames.Single(value => value.Type == CoordinatorIpcProtocol.ResultType);
        Assert(result.ExitCode == 0 && result.Payload.HasValue &&
               result.Payload.Value.GetProperty("schemaVersion").GetString() ==
                   DevBridgeSchemaVersions.AgentSnapshotContract,
            "agent IPC terminal result must contain the dedicated snapshot contract");
        Assert(result.Payload.Value.GetProperty("nextAction").GetString() == "run-tests",
            "agent IPC snapshot must preserve compact planning");
    }
}
