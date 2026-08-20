#nullable enable

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DevBridge2;

namespace DevBridge.Coordinator;

/// <summary>
/// Lease-bound, scenario-neutral game primitives. This layer composes the
/// existing RimBridge route and RimBridgeServer tools; it intentionally has no
/// Frontier identifiers, test assertions, or scenario state.
/// </summary>
internal sealed partial class CoordinatorState
{
    private const int MaxGameWaitTimeoutMs = 300_000;
    private const int MaxGamePollMs = 5_000;
    private const int MaxGameTicks = 1_000_000;
    private const int DefaultGameOperationTimeoutMs = 120_000;
    private const int ErrorLogLimit = 1_000;
    // The token deliberately uses only shell-safe characters because callers
    // commonly pass it through DevBridge.cmd on Windows.
    private const string ErrorCheckpointPrefix = "devbridge-error-v1";

    private int Game(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit, Func<bool> connected)
    {
        if (arguments == null || arguments.Count == 0)
            return GameUsage(request, emit,
                "Usage: DevBridge.cmd game inspect|action|wait|advance|save|load|errors --json");

        return arguments[0]?.Trim().ToLowerInvariant() switch
        {
            "inspect" => GameForward(arguments, request, emit, "inspect"),
            "action" => GameForward(arguments, request, emit, "action"),
            "wait" => GameWait(arguments, request, emit, connected),
            "advance" => GameAdvance(arguments, request, emit),
            "save" => GameSave(arguments, request, emit),
            "load" => GameLoad(arguments, request, emit),
            "errors" => GameErrors(arguments, request, emit),
            _ => GameUsage(request, emit,
                "Usage: DevBridge.cmd game inspect|action|wait|advance|save|load|errors --json")
        };
    }

    private int GameForward(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit, string operation)
    {
        if (!TryParseBridgeCall(arguments.Skip(1).ToList(),
                out RimBridgeRouteRequest routeRequest, out string? parseError))
            return GameUsage(request, emit, parseError ??
                "A semantic RimBridge tool name and JSON object are required.");

        RimBridgeRouteResult route = RouteRimBridgeTool(request, routeRequest.ToolName,
            routeRequest.Arguments, routeRequest.LeaseId);
        return CompleteGame(request, emit, RouteGameResponse(operation, route));
    }

    private int GameWait(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit, Func<bool> connected)
    {
        if (!TryParseGameWait(arguments, out GameWaitOptions? options,
                out string? errorCode, out string? error))
            return GameFailure(request, emit, "wait", errorCode ?? "GAME_WAIT_USAGE",
                error ?? "The game wait options are invalid.", 2);
        GameWaitOptions waitOptions = options!;

        long started = Stopwatch.GetTimestamp();
        int attempts = 0;
        JsonElement? lastResult = null;
        RimBridgeRouteResult? lastRoute = null;

        while (ElapsedMilliseconds(started) < waitOptions.TimeoutMs)
        {
            if (connected != null && !connected())
                return CompleteGame(request, emit, WaitResponse(
                    lastRoute, waitOptions, attempts, started, lastResult, false,
                    "GAME_WAIT_CANCELLED", "The client disconnected while waiting for the game condition.", 4));

            attempts++;
            lastRoute = RouteRimBridgeTool(request, waitOptions.RouteRequest.ToolName,
                waitOptions.RouteRequest.Arguments, waitOptions.RouteRequest.LeaseId);
            if (!lastRoute.Success)
                return CompleteGame(request, emit, WaitResponse(
                    lastRoute, waitOptions, attempts, started, lastResult, false,
                    lastRoute.ErrorCode, lastRoute.Error, 4));

            lastResult = lastRoute.Payload;
            if (lastResult.HasValue && TryResolveJsonPointer(lastResult.Value,
                    waitOptions.Path, out JsonElement actual) && JsonValuesEqual(actual, waitOptions.Expected))
                return CompleteGame(request, emit, WaitResponse(
                    lastRoute, waitOptions, attempts, started, lastResult, true, null, null, 0));

            int remaining = waitOptions.TimeoutMs - (int)Math.Min(int.MaxValue,
                ElapsedMilliseconds(started));
            if (remaining > 0)
                Thread.Sleep(Math.Min(waitOptions.PollMs, remaining));
        }

        string expected = waitOptions.Expected.GetRawText();
        return CompleteGame(request, emit, WaitResponse(
            lastRoute, waitOptions, attempts, started, lastResult, false,
            "GAME_WAIT_TIMEOUT",
            "Timed out after " + waitOptions.TimeoutMs.ToString(CultureInfo.InvariantCulture) +
            "ms waiting for JSON pointer '" + waitOptions.Path + "' to equal " + expected + ".",
            4));
    }

    private int GameAdvance(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit)
    {
        if (!TryParseAdvance(arguments, out int ticks, out int timeoutMs,
                out int pollMs, out string? leaseId, out string? error))
            return GameFailure(request, emit, "advance", "GAME_ADVANCE_USAGE",
                error ?? "The game advance options are invalid.", 2);

        JsonElement toolArguments = JsonSerializer.SerializeToElement(new
        {
            ticks,
            timeoutMs,
            pollIntervalMs = pollMs,
            pauseFirst = true
        }, CoordinatorSerialization.JsonOptions);
        RimBridgeRouteResult route = RouteRimBridgeTool(request,
            "rimworld/step_game_ticks", toolArguments, leaseId);
        GamePrimitiveResponse response = RouteGameResponse("advance", route);
        return CompleteGame(request, emit, new GamePrimitiveResponse
        {
            Operation = response.Operation,
            Success = response.Success,
            ExitCode = response.ExitCode,
            ToolName = response.ToolName,
            Result = response.Result,
            Route = response.Route,
            ErrorCode = response.ErrorCode,
            Error = response.Error,
            NextAction = response.NextAction,
            Attempts = 1,
            TimeoutMs = timeoutMs
        });
    }

    private int GameSave(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit)
    {
        if (!TryParseSave(arguments, out string? saveName, out int timeoutMs,
                out string? leaseId, out string? error))
            return GameFailure(request, emit, "save", "GAME_SAVE_USAGE",
                error ?? "The game save options are invalid.", 2);

        JsonElement saveArguments = JsonSerializer.SerializeToElement(new { saveName },
            CoordinatorSerialization.JsonOptions);
        RimBridgeRouteResult saveRoute = RouteRimBridgeTool(request,
            "rimworld/save_game", saveArguments, leaseId);
        if (!saveRoute.Success)
            return CompleteGame(request, emit, RouteGameResponse("save", saveRoute));

        JsonElement confirmationArguments = JsonSerializer.SerializeToElement(new
        {
            timeoutMs,
            pollIntervalMs = 50
        }, CoordinatorSerialization.JsonOptions);
        RimBridgeRouteResult completionRoute = RouteRimBridgeTool(request,
            "rimbridge/wait_for_long_event_idle", confirmationArguments, leaseId);
        GamePrimitiveResponse response = new()
        {
            Operation = "save",
            Success = completionRoute.Success,
            ExitCode = completionRoute.Success ? 0 : 4,
            ToolName = "rimworld/save_game",
            Result = saveRoute.Payload,
            Route = saveRoute.ToJson(),
            CompletionRoute = completionRoute.ToJson(),
            CompletionConfirmed = completionRoute.Success,
            ErrorCode = completionRoute.Success ? null :
                completionRoute.ErrorCode ?? "GAME_SAVE_COMPLETION_FAILED",
            Error = completionRoute.Success ? null :
                completionRoute.Error ?? "The save request completed without a long-event idle confirmation.",
            TimeoutMs = timeoutMs,
            NextAction = completionRoute.Success ? null : "inspect-evidence"
        };
        return CompleteGame(request, emit, response);
    }

    private int GameLoad(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit)
    {
        if (!TryParseLoad(arguments, out string? saveName, out string readiness,
                out int timeoutMs, out int pollMs, out bool ignoreModCompatibility,
                out string? leaseId, out string? error))
            return GameFailure(request, emit, "load", "GAME_LOAD_USAGE",
                error ?? "The game load options are invalid.", 2);

        JsonElement loadArguments = JsonSerializer.SerializeToElement(new
        {
            saveName,
            timeoutMs,
            pollIntervalMs = pollMs,
            readiness,
            pauseIfNeeded = true,
            ignoreModCompatibility
        }, CoordinatorSerialization.JsonOptions);
        RimBridgeRouteResult route = RouteRimBridgeTool(request,
            "rimworld/load_game_ready", loadArguments, leaseId);
        GamePrimitiveResponse response = RouteGameResponse("load", route);
        return CompleteGame(request, emit, new GamePrimitiveResponse
        {
            Operation = response.Operation,
            Success = response.Success,
            ExitCode = response.ExitCode,
            ToolName = response.ToolName,
            Result = response.Result,
            Route = response.Route,
            ErrorCode = response.ErrorCode,
            Error = response.Error,
            TimeoutMs = timeoutMs,
            CompletionConfirmed = response.Success,
            NextAction = response.NextAction
        });
    }

    private int GameErrors(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit)
    {
        if (arguments.Count < 2)
            return GameUsage(request, emit,
                "Usage: DevBridge.cmd game errors checkpoint|delta ... --json");

        return arguments[1]?.Trim().ToLowerInvariant() switch
        {
            "checkpoint" => GameErrorCheckpoint(arguments, request, emit),
            "delta" => GameErrorDelta(arguments, request, emit),
            _ => GameUsage(request, emit,
                "Usage: DevBridge.cmd game errors checkpoint|delta ... --json")
        };
    }

    private int GameErrorCheckpoint(IReadOnlyList<string> arguments,
        BridgeRequest request, Action<string> emit)
    {
        if (!TryParseLeaseOnly(arguments.Skip(2).ToList(), out string? leaseId,
                out string? error))
            return GameFailure(request, emit, "errors-checkpoint", "GAME_ERROR_USAGE",
                error ?? "The error checkpoint options are invalid.", 2);

        JsonElement logArguments = JsonSerializer.SerializeToElement(new
        {
            // The checkpoint must observe the global cursor, not only the
            // current error subset. Otherwise an error-free pre-scenario log
            // would produce sequence 0 and re-report older errors later.
            limit = ErrorLogLimit
        }, CoordinatorSerialization.JsonOptions);
        RimBridgeRouteResult route = RouteRimBridgeTool(request,
            "rimbridge/list_logs", logArguments, leaseId);
        if (!route.Success)
            return CompleteGame(request, emit, RouteGameResponse("errors-checkpoint", route));

        string? shapeError = null;
        if (!TryExtractLogEntries(route.Payload, out List<JsonElement> entries) ||
            !TryGetMaximumSequence(entries, out long sequence, out shapeError))
            return GameFailure(request, emit, "errors-checkpoint", "GAME_ERROR_LOG_SHAPE_INVALID",
                shapeError ?? "RimBridge log output did not contain sequence-numbered entries.", 4,
                route);

        if (route.Generation <= 0 || string.IsNullOrWhiteSpace(route.LaunchId))
            return GameFailure(request, emit, "errors-checkpoint", "GAME_ERROR_IDENTITY_UNAVAILABLE",
                "The current generation identity was not available for the error checkpoint.", 4,
                route);

        string checkpoint = BuildErrorCheckpoint(route.Generation, route.LaunchId, sequence);
        return CompleteGame(request, emit, new GamePrimitiveResponse
        {
            Operation = "errors-checkpoint",
            Success = true,
            ExitCode = 0,
            ToolName = "rimbridge/list_logs",
            Route = route.ToJson(),
            CursorSequence = sequence,
            Checkpoint = checkpoint,
            NextCheckpoint = checkpoint
        });
    }

    private int GameErrorDelta(IReadOnlyList<string> arguments,
        BridgeRequest request, Action<string> emit)
    {
        if (!TryParseErrorDelta(arguments, out string? checkpoint,
                out string? leaseId, out string? error))
            return GameFailure(request, emit, "errors-delta", "GAME_ERROR_USAGE",
                error ?? "The error delta options are invalid.", 2);

        if (!TryParseErrorCheckpoint(checkpoint, out int checkpointGeneration,
                out string? checkpointLaunchId, out long afterSequence, out string? checkpointError))
            return GameFailure(request, emit, "errors-delta", "GAME_ERROR_CHECKPOINT_INVALID",
                checkpointError ?? "The error checkpoint token is invalid.", 2);

        int currentGeneration;
        string? currentLaunchId;
        lock (gate)
        {
            currentGeneration = state.Generation;
            currentLaunchId = state.LaunchId;
        }
        if (checkpointGeneration != currentGeneration ||
            !string.Equals(checkpointLaunchId, currentLaunchId, StringComparison.Ordinal))
            return GameFailure(request, emit, "errors-delta", "GAME_ERROR_CHECKPOINT_STALE",
                "The error checkpoint belongs to an older RimWorld generation or launch.", 4);

        JsonElement logArguments = JsonSerializer.SerializeToElement(new
        {
            limit = ErrorLogLimit,
            minimumLevel = "error",
            afterSequence
        }, CoordinatorSerialization.JsonOptions);
        RimBridgeRouteResult route = RouteRimBridgeTool(request,
            "rimbridge/list_logs", logArguments, leaseId);
        if (!route.Success)
            return CompleteGame(request, emit, RouteGameResponse("errors-delta", route));

        string? shapeError = null;
        if (!TryExtractLogEntries(route.Payload, out List<JsonElement> entries) ||
            !TryGetMaximumSequence(entries, out long observedSequence, out shapeError))
            return GameFailure(request, emit, "errors-delta", "GAME_ERROR_LOG_SHAPE_INVALID",
                shapeError ?? "RimBridge log output did not contain sequence-numbered entries.", 4,
                route);

        List<JsonElement> newErrors = new();
        foreach (JsonElement entry in entries)
        {
            if (!TryGetLogSequence(entry, out long sequence))
                continue;
            if (sequence > afterSequence)
                newErrors.Add(entry);
        }

        long nextSequence = Math.Max(afterSequence, observedSequence);
        string nextCheckpoint = BuildErrorCheckpoint(currentGeneration,
            currentLaunchId ?? checkpointLaunchId!, nextSequence);
        JsonElement errors = JsonSerializer.SerializeToElement(newErrors,
            CoordinatorSerialization.JsonOptions);
        return CompleteGame(request, emit, new GamePrimitiveResponse
        {
            Operation = "errors-delta",
            Success = true,
            ExitCode = 0,
            ToolName = "rimbridge/list_logs",
            Result = errors,
            Errors = errors,
            Route = route.ToJson(),
            CursorSequence = nextSequence,
            Checkpoint = checkpoint,
            NextCheckpoint = nextCheckpoint
        });
    }

    private RimBridgeRouteResult RouteRimBridgeTool(BridgeRequest request,
        string toolName, JsonElement arguments, string? requestedLeaseId)
    {
        string leaseId = FindRouteLeaseId(request, requestedLeaseId);
        long routeStarted = Stopwatch.GetTimestamp();
        string routeCategory = RimBridgeOperationPolicy.CategoryFor(toolName);
        TraceEvent("rimbridge.route.started", request, category: routeCategory);
        RimBridgeRoutePreparation preparation;
        lock (gate)
        {
            if (!TryPrepareStaleRimBridgeProcessRouteLocked(request, toolName,
                    leaseId, out preparation))
            {
                SynchronizeLocked();
                preparation = PrepareRimBridgeRouteLocked(request, toolName, leaseId);
            }
        }

        if (preparation.Failure != null)
        {
            TraceEvent("rimbridge.route.failed", request,
                durationMs: ElapsedMilliseconds(routeStarted), success: false,
                errorCode: preparation.Failure.ErrorCode, category: routeCategory);
            request.RimBridgeRouteResult = preparation.Failure;
            return preparation.Failure;
        }

        RimBridgeWireResult wire = rimBridgeClient.CallTool(preparation.Context.Endpoint,
            preparation.Context.LaunchId, toolName, arguments, options.RimBridgeCallTimeout);
        RimBridgeRouteResult result = BuildRimBridgeRouteResult("call", toolName,
            preparation.Context, wire);
        HandleRimBridgeRouteCredentialFailure(preparation.Context.Endpoint, wire);
        request.RimBridgeRouteResult = result;
        TraceEvent(result.Success ? "rimbridge.route.completed" : "rimbridge.route.failed", request,
            durationMs: ElapsedMilliseconds(routeStarted), success: result.Success,
            errorCode: result.Success ? null : result.ErrorCode, category: routeCategory);
        return result;
    }

    internal GamePrimitiveResponse CreateGameJsonResponse(BridgeRequest request,
        int exitCode)
    {
        if (request.GameResponse is GamePrimitiveResponse response)
        {
            response.ExitCode = response.ExitCode == 0 && exitCode != 0 ? exitCode : response.ExitCode;
            return response;
        }

        return new GamePrimitiveResponse
        {
            Operation = request.Arguments?.FirstOrDefault() ?? "unknown",
            Success = false,
            ExitCode = exitCode == 0 ? 4 : exitCode,
            ErrorCode = "GAME_RESPONSE_MISSING",
            Error = "The game command did not produce its dedicated response."
        };
    }

    private static GamePrimitiveResponse RouteGameResponse(string operation,
        RimBridgeRouteResult route) => new()
    {
        Operation = operation,
        Success = route?.Success == true,
        ExitCode = route?.Success == true ? 0 : 4,
        ToolName = route?.ToolName,
        Result = route?.Payload,
        Route = route?.ToJson(),
        ErrorCode = route?.Success == true ? null : route?.ErrorCode,
        Error = route?.Success == true ? null : route?.Error,
        NextAction = route?.Success == true ? null :
            route?.ErrorCode == "RIMBRIDGE_LEASE_REQUIRED" ? "test begin" : "inspect-evidence"
    };

    private static GamePrimitiveResponse WaitResponse(RimBridgeRouteResult? route,
        GameWaitOptions options, int attempts, long started, JsonElement? lastResult,
        bool success, string? errorCode, string? error, int exitCode) => new()
    {
        Operation = "wait",
        Success = success,
        ExitCode = exitCode,
        ToolName = options.RouteRequest.ToolName,
        Result = lastResult,
        Route = route?.ToJson(),
        Condition = new GameCondition { Path = options.Path, Expected = options.Expected },
        Attempts = attempts,
        ElapsedMs = ElapsedMilliseconds(started),
        TimeoutMs = options.TimeoutMs,
        ErrorCode = errorCode,
        Error = error,
        NextAction = success ? null : errorCode == "RIMBRIDGE_LEASE_REQUIRED"
            ? "test begin" : "inspect-evidence"
    };

    private int CompleteGame(BridgeRequest request, Action<string> emit,
        GamePrimitiveResponse response)
    {
        request.GameResponse = response;
        if (!response.Success)
            emit((response.ErrorCode ?? "GAME_PRIMITIVE_FAILED") + ": " +
                (response.Error ?? "The game primitive failed."));
        else
            emit("Game primitive '" + response.Operation + "' completed.");
        return response.ExitCode;
    }

    private int GameFailure(BridgeRequest request, Action<string> emit, string operation,
        string errorCode, string error, int exitCode, RimBridgeRouteResult? route = null)
    {
        GamePrimitiveResponse response = new()
        {
            Operation = operation,
            Success = false,
            ExitCode = exitCode,
            ErrorCode = errorCode,
            Error = error,
            Route = route?.ToJson(),
            NextAction = errorCode == "RIMBRIDGE_LEASE_REQUIRED" ? "test begin" : null
        };
        return CompleteGame(request, emit, response);
    }

    private int GameUsage(BridgeRequest request, Action<string> emit, string error)
    {
        return GameFailure(request, emit, request.Arguments?.FirstOrDefault() ?? "game",
            "GAME_USAGE", error, 2);
    }

    private static bool TryParseGameWait(IReadOnlyList<string> arguments,
        out GameWaitOptions? options, out string? errorCode, out string? error)
    {
        options = null;
        errorCode = null;
        error = null;
        if (arguments == null || arguments.Count < 2)
        {
            errorCode = "GAME_WAIT_USAGE";
            error = "game wait requires a semantic query tool.";
            return false;
        }

        List<string> forwarded = new() { arguments[1] };
        string? path = null;
        string? expectedText = null;
        int timeoutMs = 0;
        int pollMs = 100;
        bool pathSeen = false;
        bool expectedSeen = false;
        bool timeoutSeen = false;
        bool pollSeen = false;

        for (int index = 2; index < arguments.Count; index++)
        {
            string argument = arguments[index] ?? string.Empty;
            if (string.Equals(argument, "--path", StringComparison.OrdinalIgnoreCase))
            {
                if (pathSeen || ++index >= arguments.Count)
                    return WaitParseFailure(out options, out errorCode, out error,
                        "GAME_WAIT_USAGE", "--path requires one JSON pointer.");
                path = arguments[index];
                pathSeen = true;
                continue;
            }
            if (argument.StartsWith("--path=", StringComparison.OrdinalIgnoreCase))
            {
                if (pathSeen || argument.Length == "--path=".Length)
                    return WaitParseFailure(out options, out errorCode, out error,
                        "GAME_WAIT_USAGE", "--path requires one JSON pointer.");
                path = argument.Substring("--path=".Length);
                pathSeen = true;
                continue;
            }
            if (string.Equals(argument, "--equals", StringComparison.OrdinalIgnoreCase))
            {
                if (expectedSeen || ++index >= arguments.Count)
                    return WaitParseFailure(out options, out errorCode, out error,
                        "GAME_WAIT_USAGE", "--equals requires one JSON value.");
                expectedText = arguments[index];
                expectedSeen = true;
                continue;
            }
            if (argument.StartsWith("--equals=", StringComparison.OrdinalIgnoreCase))
            {
                if (expectedSeen || argument.Length == "--equals=".Length)
                    return WaitParseFailure(out options, out errorCode, out error,
                        "GAME_WAIT_USAGE", "--equals requires one JSON value.");
                expectedText = argument.Substring("--equals=".Length);
                expectedSeen = true;
                continue;
            }
            if (string.Equals(argument, "--timeout-ms", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--timeout-ms=", StringComparison.OrdinalIgnoreCase))
            {
                if (timeoutSeen || !TryReadIntOption(arguments, ref index, "--timeout-ms",
                        out timeoutMs, out error))
                    return WaitParseFailure(out options, out errorCode, out error,
                        "GAME_WAIT_USAGE", error ?? "--timeout-ms requires a positive bounded integer.");
                timeoutSeen = true;
                continue;
            }
            if (string.Equals(argument, "--poll-ms", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--poll-ms=", StringComparison.OrdinalIgnoreCase))
            {
                if (pollSeen || !TryReadIntOption(arguments, ref index, "--poll-ms",
                        out pollMs, out error))
                    return WaitParseFailure(out options, out errorCode, out error,
                        "GAME_WAIT_USAGE", error ?? "--poll-ms requires a positive bounded integer.");
                pollSeen = true;
                continue;
            }

            // Keep route-owned options (`--lease`, `--args`, and `--json`) for
            // the existing strict RimBridge argument parser.
            forwarded.Add(argument);
            if ((string.Equals(argument, "--lease", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(argument, "--args", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(argument, "--arguments", StringComparison.OrdinalIgnoreCase)) &&
                index + 1 < arguments.Count)
                forwarded.Add(arguments[++index]);
        }

        if (!pathSeen || !expectedSeen || !timeoutSeen)
            return WaitParseFailure(out options, out errorCode, out error,
                "GAME_WAIT_USAGE", "game wait requires --path, --equals, and --timeout-ms.");
        if (path!.Length > 1024 || (!path.StartsWith("/", StringComparison.Ordinal) && path.Length != 0))
            return WaitParseFailure(out options, out errorCode, out error,
                "GAME_WAIT_CONDITION_INVALID", "--path must be an RFC 6901 JSON pointer.");
        if (timeoutMs < 1 || timeoutMs > MaxGameWaitTimeoutMs)
            return WaitParseFailure(out options, out errorCode, out error,
                "GAME_WAIT_USAGE", "--timeout-ms must be between 1 and " +
                MaxGameWaitTimeoutMs.ToString(CultureInfo.InvariantCulture) + ".");
        if (pollMs < 1 || pollMs > MaxGamePollMs)
            return WaitParseFailure(out options, out errorCode, out error,
                "GAME_WAIT_USAGE", "--poll-ms must be between 1 and " +
                MaxGamePollMs.ToString(CultureInfo.InvariantCulture) + ".");

        JsonElement expected;
        try
        {
            using JsonDocument document = JsonDocument.Parse(expectedText!);
            expected = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            return WaitParseFailure(out options, out errorCode, out error,
                "GAME_WAIT_CONDITION_INVALID", "--equals must be one valid JSON value: " +
                exception.Message);
        }

        if (!TryParseBridgeCall(forwarded, out RimBridgeRouteRequest routeRequest,
                out string? routeError))
            return WaitParseFailure(out options, out errorCode, out error,
                "GAME_WAIT_USAGE", routeError ?? "The semantic query arguments are invalid.");

        options = new GameWaitOptions
        {
            RouteRequest = routeRequest,
            Path = path,
            Expected = expected,
            TimeoutMs = timeoutMs,
            PollMs = pollMs
        };
        return true;
    }

    private static bool WaitParseFailure(out GameWaitOptions? options,
        out string? errorCode, out string? error, string code, string detail)
    {
        options = null;
        errorCode = code;
        error = detail;
        return false;
    }

    private static bool TryParseAdvance(IReadOnlyList<string> arguments,
        out int ticks, out int timeoutMs, out int pollMs, out string? leaseId,
        out string? error)
    {
        ticks = 0;
        timeoutMs = 10_000;
        pollMs = 10;
        leaseId = null;
        error = null;
        bool ticksSeen = false;
        bool timeoutSeen = false;
        bool pollSeen = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index] ?? string.Empty;
            if (string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(argument, "--lease", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                    return SimpleParseFailure(out error, "--lease requires a lease ID.");
                leaseId = arguments[index];
                continue;
            }
            if (argument.StartsWith("--lease=", StringComparison.OrdinalIgnoreCase))
            {
                leaseId = argument.Substring("--lease=".Length);
                continue;
            }
            if (argument == "--ticks" || argument.StartsWith("--ticks=", StringComparison.OrdinalIgnoreCase))
            {
                if (ticksSeen || !TryReadIntOption(arguments, ref index, "--ticks", out ticks, out error))
                    return false;
                ticksSeen = true;
                continue;
            }
            if (argument == "--timeout-ms" || argument.StartsWith("--timeout-ms=", StringComparison.OrdinalIgnoreCase))
            {
                if (timeoutSeen || !TryReadIntOption(arguments, ref index, "--timeout-ms", out timeoutMs, out error))
                    return false;
                timeoutSeen = true;
                continue;
            }
            if (argument == "--poll-ms" || argument.StartsWith("--poll-ms=", StringComparison.OrdinalIgnoreCase))
            {
                if (pollSeen || !TryReadIntOption(arguments, ref index, "--poll-ms", out pollMs, out error))
                    return false;
                pollSeen = true;
                continue;
            }
            return SimpleParseFailure(out error, "Unknown game advance option '" + argument + "'.");
        }

        if (!ticksSeen || ticks < 1 || ticks > MaxGameTicks)
            return SimpleParseFailure(out error, "--ticks must be between 1 and " +
                MaxGameTicks.ToString(CultureInfo.InvariantCulture) + ".");
        if (timeoutMs < 1 || timeoutMs > MaxGameWaitTimeoutMs)
            return SimpleParseFailure(out error, "--timeout-ms must be between 1 and " +
                MaxGameWaitTimeoutMs.ToString(CultureInfo.InvariantCulture) + ".");
        if (pollMs < 1 || pollMs > MaxGamePollMs)
            return SimpleParseFailure(out error, "--poll-ms must be between 1 and " +
                MaxGamePollMs.ToString(CultureInfo.InvariantCulture) + ".");
        return true;
    }

    private static bool TryParseSave(IReadOnlyList<string> arguments,
        out string? saveName, out int timeoutMs, out string? leaseId, out string? error)
    {
        saveName = null;
        timeoutMs = DefaultGameOperationTimeoutMs;
        leaseId = null;
        error = null;
        bool nameSeen = false;
        bool timeoutSeen = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index] ?? string.Empty;
            if (argument == "--json")
                continue;
            if (argument == "--lease")
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                    return SimpleParseFailure(out error, "--lease requires a lease ID.");
                leaseId = arguments[index];
                continue;
            }
            if (argument.StartsWith("--lease=", StringComparison.OrdinalIgnoreCase))
            {
                leaseId = argument.Substring("--lease=".Length);
                continue;
            }
            if (argument == "--name" || argument.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
            {
                if (nameSeen || !TryReadStringOption(arguments, ref index, "--name", out saveName, out error))
                    return false;
                nameSeen = true;
                continue;
            }
            if (argument == "--timeout-ms" || argument.StartsWith("--timeout-ms=", StringComparison.OrdinalIgnoreCase))
            {
                if (timeoutSeen || !TryReadIntOption(arguments, ref index, "--timeout-ms", out timeoutMs, out error))
                    return false;
                timeoutSeen = true;
                continue;
            }
            return SimpleParseFailure(out error, "Unknown game save option '" + argument + "'.");
        }
        if (!nameSeen || !IsValidSaveName(saveName))
            return SimpleParseFailure(out error, "--name must be a non-empty save name without path separators.");
        if (timeoutMs < 1 || timeoutMs > MaxGameWaitTimeoutMs)
            return SimpleParseFailure(out error, "--timeout-ms must be between 1 and " +
                MaxGameWaitTimeoutMs.ToString(CultureInfo.InvariantCulture) + ".");
        return true;
    }

    private static bool TryParseLoad(IReadOnlyList<string> arguments,
        out string? saveName, out string readiness, out int timeoutMs, out int pollMs,
        out bool ignoreModCompatibility, out string? leaseId, out string? error)
    {
        saveName = null;
        readiness = "playable";
        timeoutMs = DefaultGameOperationTimeoutMs;
        pollMs = 50;
        ignoreModCompatibility = false;
        leaseId = null;
        error = null;
        bool nameSeen = false;
        bool readinessSeen = false;
        bool timeoutSeen = false;
        bool pollSeen = false;
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index] ?? string.Empty;
            if (argument == "--json")
                continue;
            if (argument == "--ignore-mod-compatibility")
            {
                if (ignoreModCompatibility)
                    return SimpleParseFailure(out error, "--ignore-mod-compatibility may be specified only once.");
                ignoreModCompatibility = true;
                continue;
            }
            if (argument == "--lease")
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                    return SimpleParseFailure(out error, "--lease requires a lease ID.");
                leaseId = arguments[index];
                continue;
            }
            if (argument.StartsWith("--lease=", StringComparison.OrdinalIgnoreCase))
            {
                leaseId = argument.Substring("--lease=".Length);
                continue;
            }
            if (argument == "--name" || argument.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
            {
                if (nameSeen || !TryReadStringOption(arguments, ref index, "--name", out saveName, out error))
                    return false;
                nameSeen = true;
                continue;
            }
            if (argument == "--readiness" || argument.StartsWith("--readiness=", StringComparison.OrdinalIgnoreCase))
            {
                if (readinessSeen || !TryReadStringOption(arguments, ref index, "--readiness", out string? parsedReadiness, out error))
                    return false;
                readiness = parsedReadiness!;
                readinessSeen = true;
                continue;
            }
            if (argument == "--timeout-ms" || argument.StartsWith("--timeout-ms=", StringComparison.OrdinalIgnoreCase))
            {
                if (timeoutSeen || !TryReadIntOption(arguments, ref index, "--timeout-ms", out timeoutMs, out error))
                    return false;
                timeoutSeen = true;
                continue;
            }
            if (argument == "--poll-ms" || argument.StartsWith("--poll-ms=", StringComparison.OrdinalIgnoreCase))
            {
                if (pollSeen || !TryReadIntOption(arguments, ref index, "--poll-ms", out pollMs, out error))
                    return false;
                pollSeen = true;
                continue;
            }
            return SimpleParseFailure(out error, "Unknown game load option '" + argument + "'.");
        }
        if (!nameSeen || !IsValidSaveName(saveName))
            return SimpleParseFailure(out error, "--name must be a non-empty save name without path separators.");
        if (!new[] { "gameData", "mapData", "currentMap", "playable", "visual" }
                .Contains(readiness, StringComparer.OrdinalIgnoreCase))
            return SimpleParseFailure(out error,
                "--readiness must be gameData, mapData, currentMap, playable, or visual.");
        if (timeoutMs < 1 || timeoutMs > MaxGameWaitTimeoutMs)
            return SimpleParseFailure(out error, "--timeout-ms must be between 1 and " +
                MaxGameWaitTimeoutMs.ToString(CultureInfo.InvariantCulture) + ".");
        if (pollMs < 1 || pollMs > MaxGamePollMs)
            return SimpleParseFailure(out error, "--poll-ms must be between 1 and " +
                MaxGamePollMs.ToString(CultureInfo.InvariantCulture) + ".");
        return true;
    }

    private static bool TryParseLeaseOnly(IReadOnlyList<string> arguments,
        out string? leaseId, out string? error)
    {
        leaseId = null;
        error = null;
        if (arguments == null)
            return SimpleParseFailure(out error, "Only --lease and --json are accepted here.");
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index] ?? string.Empty;
            if (argument == "--json")
                continue;
            if (argument == "--lease")
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                    return SimpleParseFailure(out error, "--lease requires a lease ID.");
                leaseId = arguments[index];
                continue;
            }
            if (argument.StartsWith("--lease=", StringComparison.OrdinalIgnoreCase))
            {
                leaseId = argument.Substring("--lease=".Length);
                continue;
            }
            return SimpleParseFailure(out error, "Only --lease and --json are accepted here.");
        }
        return true;
    }

    private static bool TryParseErrorDelta(IReadOnlyList<string> arguments,
        out string? checkpoint, out string? leaseId, out string? error)
    {
        checkpoint = null;
        leaseId = null;
        error = null;
        bool checkpointSeen = false;
        for (int index = 2; index < arguments.Count; index++)
        {
            string argument = arguments[index] ?? string.Empty;
            if (argument == "--json")
                continue;
            if (argument == "--checkpoint" || argument.StartsWith("--checkpoint=", StringComparison.OrdinalIgnoreCase))
            {
                if (checkpointSeen || !TryReadStringOption(arguments, ref index, "--checkpoint", out checkpoint, out error))
                    return false;
                checkpointSeen = true;
                continue;
            }
            if (argument == "--lease")
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                    return SimpleParseFailure(out error, "--lease requires a lease ID.");
                leaseId = arguments[index];
                continue;
            }
            if (argument.StartsWith("--lease=", StringComparison.OrdinalIgnoreCase))
            {
                leaseId = argument.Substring("--lease=".Length);
                continue;
            }
            return SimpleParseFailure(out error, "Unknown game error delta option '" + argument + "'.");
        }
        if (!checkpointSeen || string.IsNullOrWhiteSpace(checkpoint))
            return SimpleParseFailure(out error, "--checkpoint requires one checkpoint token.");
        return true;
    }

    private static bool TryReadIntOption(IReadOnlyList<string> arguments, ref int index,
        string option, out int value, out string? error)
    {
        value = 0;
        error = null;
        string? text = null;
        string argument = arguments[index] ?? string.Empty;
        string prefix = option + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            text = argument.Substring(prefix.Length);
        else if (++index < arguments.Count)
            text = arguments[index];
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = option + " requires an integer.";
            return false;
        }
        return true;
    }

    private static bool TryReadStringOption(IReadOnlyList<string> arguments, ref int index,
        string option, out string? value, out string? error)
    {
        value = null;
        error = null;
        string argument = arguments[index] ?? string.Empty;
        string prefix = option + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = argument.Substring(prefix.Length);
        else if (++index < arguments.Count)
            value = arguments[index];
        if (string.IsNullOrWhiteSpace(value))
        {
            error = option + " requires a value.";
            return false;
        }
        return true;
    }

    private static bool SimpleParseFailure(out string? error, string detail)
    {
        error = detail;
        return false;
    }

    private static bool IsValidSaveName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 &&
        !name.Any(char.IsControl) && !name.Contains('/') && !name.Contains('\\');

    private static bool TryResolveJsonPointer(JsonElement root, string path,
        out JsonElement value)
    {
        value = default;
        if (path.Length == 0)
        {
            value = root;
            return true;
        }
        if (!path.StartsWith("/", StringComparison.Ordinal))
            return false;

        JsonElement current = root;
        foreach (string rawSegment in path.Substring(1).Split('/'))
        {
            if (!TryDecodePointerSegment(rawSegment, out string? segment))
                return false;
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment!, out current))
                    return false;
                continue;
            }
            if (current.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }
            return false;
        }
        value = current;
        return true;
    }

    private static bool TryDecodePointerSegment(string raw, out string? segment)
    {
        segment = raw.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
        for (int index = 0; index < segment.Length; index++)
        {
            if (segment[index] == '~' && (index + 1 >= segment.Length ||
                    (segment[index + 1] != '0' && segment[index + 1] != '1')))
            {
                segment = null;
                return false;
            }
        }
        return true;
    }

    private static bool JsonValuesEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count() &&
                left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out JsonElement other) &&
                    JsonValuesEqual(property.Value, other)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() &&
                left.EnumerateArray().Zip(right.EnumerateArray(), (a, b) => JsonValuesEqual(a, b)).All(value => value),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText()
        };
    }

    private static bool TryExtractLogEntries(JsonElement? payload,
        out List<JsonElement> entries)
    {
        entries = new List<JsonElement>();
        if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
            return false;
        JsonElement array = default;
        bool found = payload.Value.TryGetProperty("logs", out array) ||
            payload.Value.TryGetProperty("Logs", out array);
        if (!found || array.ValueKind != JsonValueKind.Array)
            return false;
        foreach (JsonElement entry in array.EnumerateArray())
            entries.Add(entry.Clone());
        return true;
    }

    private static bool TryGetMaximumSequence(IReadOnlyList<JsonElement> entries,
        out long sequence, out string? error)
    {
        sequence = 0;
        error = null;
        foreach (JsonElement entry in entries)
        {
            if (!TryGetLogSequence(entry, out long current))
            {
                error = "Every RimBridge error log entry must contain a numeric Sequence field.";
                return false;
            }
            sequence = Math.Max(sequence, current);
        }
        return true;
    }

    private static bool TryGetLogSequence(JsonElement entry, out long sequence)
    {
        sequence = 0;
        if (entry.ValueKind != JsonValueKind.Object)
            return false;
        foreach (string propertyName in new[] { "sequence", "Sequence" })
        {
            if (entry.TryGetProperty(propertyName, out JsonElement value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out sequence))
                return true;
        }
        return false;
    }

    private static string BuildErrorCheckpoint(int generation, string launchId,
        long sequence) => ErrorCheckpointPrefix + ".g" +
        generation.ToString(CultureInfo.InvariantCulture) + ".s" +
        sequence.ToString(CultureInfo.InvariantCulture) + ".l" + launchId;

    private static bool TryParseErrorCheckpoint(string? token, out int generation,
        out string? launchId, out long sequence, out string? error)
    {
        generation = 0;
        launchId = null;
        sequence = 0;
        error = null;
        string[] parts = (token ?? string.Empty).Split('.');
        if (parts.Length != 4 || parts[0] != ErrorCheckpointPrefix ||
            !parts[1].StartsWith("g", StringComparison.Ordinal) ||
            !parts[2].StartsWith("s", StringComparison.Ordinal) ||
            !parts[3].StartsWith("l", StringComparison.Ordinal) ||
            !int.TryParse(parts[1].Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out generation) ||
            !long.TryParse(parts[2].Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence) ||
            generation < 1 || sequence < 0 || string.IsNullOrWhiteSpace(parts[3]))
        {
            error = "Expected a checkpoint returned by game errors checkpoint.";
            return false;
        }
        launchId = parts[3].Substring(1);
        if (string.IsNullOrWhiteSpace(launchId))
        {
            error = "Expected a checkpoint returned by game errors checkpoint.";
            return false;
        }
        return true;
    }

    private sealed class GameWaitOptions
    {
        internal RimBridgeRouteRequest RouteRequest { get; init; } = null!;
        internal string Path { get; init; } = string.Empty;
        internal JsonElement Expected { get; init; }
        internal int TimeoutMs { get; init; }
        internal int PollMs { get; init; }
    }
}
