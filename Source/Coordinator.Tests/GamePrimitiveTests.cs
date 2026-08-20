using System.Text.Json;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static GamePrimitiveResponse ExecuteGame(Fixture fixture,
        params string[] arguments)
    {
        BridgeRequest request = Request("game", "holder", 77, arguments);
        request.Json = true;
        int exitCode = fixture.State.Execute(request, _ => { }, () => true);
        GamePrimitiveResponse response = fixture.State.CreateGameJsonResponse(request, exitCode);
        Assert(response != null && response.ExitCode == exitCode,
            "game primitive response must be dedicated and preserve the exit code");
        return response;
    }

    private static void TestGamePrimitiveDiscovery()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        AgentCapabilitiesResponse capabilities = ExecuteAgent(fixture, "holder", 77,
            "capabilities") as AgentCapabilitiesResponse;
        Assert(capabilities != null && capabilities.Features.GamePrimitives &&
               capabilities.Features.RuntimeErrorDelta,
            "agent capabilities must advertise game primitives and runtime error deltas");
        Assert(capabilities.GamePrimitives.LeaseRequired &&
               capabilities.GamePrimitives.DynamicToolForwarding &&
               capabilities.GamePrimitives.Operations.Select(value => value.Id)
                   .ToHashSet(StringComparer.OrdinalIgnoreCase)
                   .IsSupersetOf(new[]
                   {
                       "inspect", "action", "wait", "advance", "save", "load",
                       "errors-checkpoint", "errors-delta"
                   }),
            "game primitive discovery must list the complete bounded surface");
        string json = JsonSerializer.Serialize(capabilities, Program.JsonOptions);
        Assert(json.Contains(DevBridge2.DevBridgeSchemaVersions.GamePrimitives,
                   StringComparison.Ordinal) && json.Length < 16 * 1024,
            "game primitive discovery must be versioned and bounded");
    }

    private static void TestGamePrimitiveRouting()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        FakeRimBridgeClient client = new();
        client.CallHandler = (tool, arguments) => tool switch
        {
            "rimworld/step_game_ticks" => WireSuccess(
                "{\"ticksAdvanced\":4,\"completed\":true}"),
            "frontier/action" => WireSuccess(
                "{\"accepted\":true,\"state\":\"changed\"}"),
            _ => WireSuccess("{\"state\":\"initial\"}")
        };
        ConfigureRoutedFixture(fixture, client);

        GamePrimitiveResponse inspect = ExecuteGame(fixture, "inspect",
            "frontier/query", "{\"scope\":\"initial\"}");
        Assert(inspect.Success && inspect.Route?.Success == true &&
               inspect.ToolName == "frontier/query" &&
               inspect.Result?.GetProperty("state").GetString() == "initial",
            "game inspect must forward caller-selected semantic state queries");

        GamePrimitiveResponse action = ExecuteGame(fixture, "action",
            "frontier/action", "{\"id\":\"progress\"}");
        Assert(action.Success && action.Result?.GetProperty("accepted").GetBoolean() == true &&
               client.LastToolName == "frontier/action" &&
               client.LastArguments.GetProperty("id").GetString() == "progress",
            "game action must preserve stable caller-selected identifiers and arguments");

        GamePrimitiveResponse advance = ExecuteGame(fixture, "advance", "--ticks", "4");
        Assert(advance.Success && advance.CompletionConfirmed == null &&
               advance.Result?.GetProperty("ticksAdvanced").GetInt32() == 4 &&
               client.LastToolName == "rimworld/step_game_ticks" &&
               client.LastArguments.GetProperty("ticks").GetInt32() == 4 &&
               client.LastArguments.GetProperty("pauseFirst").GetBoolean(),
            "game advance must use bounded semantic tick advancement and return its result");

        using Fixture noLease = Fixture.ReadyWithoutLease();
        FakeRimBridgeClient deniedClient = new() { CallResult = WireSuccess("{\"ok\":true}") };
        ConfigureRoutedFixture(noLease, deniedClient, includeLease: false);
        GamePrimitiveResponse denied = ExecuteGame(noLease, "action", "frontier/action", "{}");
        Assert(!denied.Success && denied.ErrorCode == "RIMBRIDGE_LEASE_REQUIRED" &&
               deniedClient.CallCalls == 0,
            "game primitives must retain the central lease requirement without a bypass");
    }

    private static void TestGamePrimitiveWait()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        FakeRimBridgeClient client = new();
        int calls = 0;
        client.CallHandler = (_, _) =>
            WireSuccess(++calls == 1 ? "{\"state\":\"pending\"}" : "{\"state\":\"ready\"}");
        ConfigureRoutedFixture(fixture, client);

        GamePrimitiveResponse ready = ExecuteGame(fixture, "wait", "frontier/query", "{}",
            "--path", "/state", "--equals", "\"ready\"", "--timeout-ms", "100",
            "--poll-ms", "1");
        Assert(ready.Success && ready.Attempts == 2 && ready.Condition?.Path == "/state" &&
               ready.Result?.GetProperty("state").GetString() == "ready",
            "game wait must stop on an observable semantic state condition");

        calls = 0;
        client.CallHandler = (_, _) => WireSuccess("{\"state\":\"pending\"}");
        GamePrimitiveResponse timeout = ExecuteGame(fixture, "wait", "frontier/query", "{}",
            "--path", "/state", "--equals", "\"ready\"", "--timeout-ms", "10",
            "--poll-ms", "1");
        Assert(!timeout.Success && timeout.ErrorCode == "GAME_WAIT_TIMEOUT" &&
               timeout.TimeoutMs == 10 && timeout.Attempts > 0 && timeout.Result.HasValue,
            "game wait timeout must be bounded and include last-result diagnostics");
    }

    private static void TestGamePrimitiveLifecyclePrimitives()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        FakeRimBridgeClient client = new();
        client.CallHandler = (tool, arguments) => tool switch
        {
            "rimworld/save_game" => WireSuccess("{\"accepted\":true}"),
            "rimbridge/wait_for_long_event_idle" => WireSuccess("{\"idle\":true}"),
            "rimworld/load_game_ready" => WireSuccess("{\"readiness\":\"playable\"}"),
            "rimbridge/list_logs" => arguments.TryGetProperty("afterSequence", out _)
                ? WireSuccess("{\"logs\":[{\"Sequence\":11,\"Level\":\"error\",\"Message\":\"new\"}]}")
                : WireSuccess("{\"logs\":[{\"Sequence\":10,\"Level\":\"error\",\"Message\":\"old\"}]}") ,
            _ => WireSuccess("{}")
        };
        ConfigureRoutedFixture(fixture, client);

        GamePrimitiveResponse save = ExecuteGame(fixture, "save", "--name", "proof-save");
        Assert(save.Success && save.CompletionConfirmed == true &&
               save.CompletionRoute?.Success == true,
            "game save must confirm long-event completion after requesting the save");

        GamePrimitiveResponse load = ExecuteGame(fixture, "load", "--name", "proof-save",
            "--readiness", "playable", "--timeout-ms", "100");
        Assert(load.Success && load.CompletionConfirmed == true &&
               load.Result?.GetProperty("readiness").GetString() == "playable" &&
               load.Route?.ToolName == "rimworld/load_game_ready",
            "game load must use the semantic readiness-confirming tool");

        GamePrimitiveResponse checkpoint = ExecuteGame(fixture, "errors", "checkpoint");
        Assert(checkpoint.Success && checkpoint.CursorSequence == 10 &&
               !string.IsNullOrWhiteSpace(checkpoint.Checkpoint),
            "runtime error checkpoint must capture the current log sequence");

        GamePrimitiveResponse delta = ExecuteGame(fixture, "errors", "delta",
            "--checkpoint", checkpoint.Checkpoint!);
        Assert(delta.Success && delta.Errors?.GetArrayLength() == 1 &&
               delta.Errors?.EnumerateArray().Single().GetProperty("Sequence").GetInt64() == 11 &&
               delta.CursorSequence == 11,
            "runtime error delta must exclude the pre-checkpoint error and return only new entries");

        client.CallHandler = (tool, arguments) => tool switch
        {
            "rimbridge/list_logs" => arguments.TryGetProperty("afterSequence", out _)
                ? WireSuccess("{\"logs\":[{\"Sequence\":21,\"Level\":\"error\",\"Message\":\"new-after-clean-checkpoint\"}]}")
                : WireSuccess("{\"logs\":[{\"Sequence\":20,\"Level\":\"info\",\"Message\":\"old-info-only\"}]}") ,
            _ => WireSuccess("{}")
        };
        GamePrimitiveResponse cleanCheckpoint = ExecuteGame(fixture, "errors", "checkpoint");
        Assert(cleanCheckpoint.Success && cleanCheckpoint.CursorSequence == 20,
            "runtime error checkpoint must retain the global cursor when no prior errors exist");
        GamePrimitiveResponse cleanDelta = ExecuteGame(fixture, "errors", "delta",
            "--checkpoint", cleanCheckpoint.Checkpoint!);
        Assert(cleanDelta.Success && cleanDelta.Errors?.GetArrayLength() == 1 &&
               cleanDelta.Errors?.EnumerateArray().Single().GetProperty("Sequence").GetInt64() == 21,
            "runtime error delta must not re-report errors before an error-free checkpoint");
    }
}
