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

internal sealed partial class CoordinatorState
{
    private int Bridge(IReadOnlyList<string> arguments, BridgeRequest request, Action<string> emit)
    {
        string subcommand = arguments.Count == 0 ? "status" : arguments[0].Trim().ToLowerInvariant();
        if (subcommand == "status" || subcommand == "policy")
        {
            lock (gate)
            {
                SynchronizeLocked();
                RefreshRimBridgePolicyStateLocked();
                PersistedState snapshot = CloneStateLocked();
                if (subcommand == "status")
                {
                    emit("RimBridge integration");
                    EmitRimBridgeStatus(snapshot.RimBridge, emit);
                }
                EmitRimBridgePolicyStatus(snapshot.RimBridgePolicy,
                    snapshot.ExternalModsConfigMutation, emit);
            }
            return 0;
        }

        if (subcommand == "endpoint")
            return BridgeEndpointCommand(request, emit);

        if (subcommand == "tools")
            return BridgeToolsCommand(arguments.Skip(1).ToList(), request, emit);

        if (subcommand == "call")
            return BridgeCallCommand(arguments.Skip(1).ToList(), request, emit);

        emit("Usage: DevBridge.cmd bridge status | bridge policy | bridge endpoint | bridge tools | bridge call <tool-name> [arguments] [--lease <lease-id>] [--json]");
        return 2;
    }

    private int BridgeToolsCommand(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit)
    {
        if (!TryParseRouteLeaseOptions(arguments, out string requestedLeaseId, out string parseError))
        {
            emit(parseError);
            emit("Usage: DevBridge.cmd bridge tools [--json] [--lease <lease-id>]");
            return 2;
        }

        string leaseId = FindRouteLeaseId(request, requestedLeaseId);
        long routeStarted = Stopwatch.GetTimestamp();
        string routeCategory = RimBridgeOperationPolicy.CategoryFor("tools/list");
        TraceEvent("rimbridge.route.started", request, category: routeCategory);
        RimBridgeRoutePreparation preparation;
        lock (gate)
        {
            if (!TryPrepareStaleRimBridgeProcessRouteLocked(request, "tools/list", leaseId,
                    out preparation))
            {
                SynchronizeLocked();
                preparation = PrepareRimBridgeRouteLocked(request, "tools/list", leaseId);
            }
        }

        if (preparation.Failure != null)
        {
            TraceEvent("rimbridge.route.failed", request,
                durationMs: ElapsedMilliseconds(routeStarted), success: false,
                errorCode: preparation.Failure.ErrorCode, category: routeCategory);
            request.RimBridgeRouteResult = preparation.Failure;
            EmitRimBridgeRoute(preparation.Failure, request, emit);
            return 4;
        }

        RimBridgeWireResult wire = rimBridgeClient.ListTools(preparation.Context.Endpoint,
            preparation.Context.LaunchId, options.RimBridgeCallTimeout);
        RimBridgeRouteResult result = CompleteRimBridgeRoute("tools", null,
            preparation.Context, wire);
        HandleRimBridgeRouteCredentialFailure(preparation.Context.Endpoint, wire);
        request.RimBridgeRouteResult = result;
        EmitRimBridgeRoute(result, request, emit);
        TraceEvent(result.Success ? "rimbridge.route.completed" : "rimbridge.route.failed", request,
            durationMs: ElapsedMilliseconds(routeStarted), success: result.Success,
            errorCode: result.Success ? null : result.ErrorCode, category: routeCategory);
        return result.Success ? 0 : 4;
    }

    private int BridgeCallCommand(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit)
    {
        if (!TryParseBridgeCall(arguments, out RimBridgeRouteRequest routeRequest, out string parseError))
        {
            emit(parseError);
            emit("Usage: DevBridge.cmd bridge call <tool-name> [JSON arguments] [--lease <lease-id>] [--json]");
            return 2;
        }

        RimBridgeRouteResult result = RouteRimBridgeTool(request, routeRequest.ToolName,
            routeRequest.Arguments, routeRequest.LeaseId);
        EmitRimBridgeRoute(result, request, emit);
        return result.Success ? 0 : 4;
    }

    private RimBridgeRoutePreparation PrepareRimBridgeRouteLocked(BridgeRequest request,
        string toolName, string leaseId)
    {
        DateTime now = clock.UtcNow.ToUniversalTime();
        RimBridgeEndpoint endpoint = RimBridgeEndpointStore.Load(runtimeRoot);
        RimBridgeRouteContext identity = new()
        {
            Endpoint = endpoint,
            WorkflowId = request?.WorkflowId,
            LaunchId = state.LaunchId,
            Generation = state.Generation,
            ProcessId = state.ProcessId,
            ProfileFingerprint = state.LaunchProfileFingerprint ?? state.ProfileFingerprint,
            LeaseId = leaseId,
            Category = RimBridgeOperationPolicy.CategoryFor(toolName)
        };

        RimBridgeRouteResult Failure(string code, string error) => new()
        {
            Operation = toolName == "tools/list" ? "tools" : "call",
            ToolName = toolName == "tools/list" ? null : toolName,
            WorkflowId = request?.WorkflowId,
            Success = false,
            ErrorCode = code,
            Error = error,
            InvocationTimestampUtc = now,
            LaunchId = state.LaunchId,
            Generation = state.Generation,
            ProfileFingerprint = state.LaunchProfileFingerprint ?? state.ProfileFingerprint,
            ProcessId = state.ProcessId,
            EndpointHost = endpoint?.Host,
            EndpointPort = endpoint?.Port ?? 0
        };

        if (options.RimBridgeMode == RimBridgeMode.Off)
            return new RimBridgeRoutePreparation
            {
                Context = identity,
                Failure = Failure("RIMBRIDGE_DISABLED",
                    "RimBridge routing is disabled by the active DevBridge configuration.")
            };

        if (state.ExternalModsConfigMutation != null ||
            state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ExternalMutated)
            return new RimBridgeRoutePreparation
            {
                Context = identity,
                Failure = Failure("PROFILE_EXTERNAL_MUTATION",
                    ExternalModsConfigMutationMessage(state.ExternalModsConfigMutation) +
                    " Reconcile ModsConfig.xml before using a routed RimBridge call.")
            };

        if (state.Generation <= 0 || string.IsNullOrWhiteSpace(state.LaunchId) || state.ProcessId <= 0)
            return new RimBridgeRoutePreparation
            {
                Context = identity,
                Failure = Failure("DEVBRIDGE_NO_ACTIVE_GENERATION",
                    "DevBridge has no active generation, launch ID, and owned RimWorld process to route through.")
            };

        if (state.Phase != BridgePhase.READY)
            return new RimBridgeRoutePreparation
            {
                Context = identity,
                Failure = Failure("RIMBRIDGE_NOT_READY",
                    "The DevBridge generation is not READY; no RimBridge call was forwarded and no restart was requested.")
            };

        if (endpoint == null || !endpoint.IsValid)
            return new RimBridgeRoutePreparation
            {
                Context = identity,
                Failure = Failure("RIMBRIDGE_ENDPOINT_NOT_FOUND",
                    "The current generation has no valid RimBridge endpoint; rediscover it with wait-ready or bridge status.")
            };

        string endpointFailure = ValidateRimBridgeEndpointLocked(endpoint);
        if (endpointFailure != null)
        {
            bool launchOrGenerationMismatch = !string.Equals(endpoint.LaunchId, state.LaunchId,
                    StringComparison.Ordinal) || endpoint.Generation != state.Generation ||
                !string.Equals(endpoint.LaunchId, state.RimBridge?.LaunchId,
                    StringComparison.Ordinal) || endpoint.Generation != state.RimBridge?.Generation;
            InvalidateRimBridgeEndpointLocked(endpointFailure,
                RimBridgeIntegrationConstants.ProcessMismatchCode);
            SaveStateLocked();
            return new RimBridgeRoutePreparation
            {
                Context = identity,
                Failure = Failure(launchOrGenerationMismatch
                        ? "RIMBRIDGE_ENDPOINT_STALE"
                        : "RIMBRIDGE_PROCESS_IDENTITY_MISMATCH", endpointFailure)
            };
        }

        RimBridgeCompanionVerification verification = rimBridgeGenerationVerifier.Verify(endpoint,
            state.LaunchId, state.Generation, state.ProcessId, options.RimBridgeCallTimeout);
        if (verification.Status == RimBridgeCompanionVerificationStatus.Mismatch ||
            verification.Status == RimBridgeCompanionVerificationStatus.Invalid)
        {
            InvalidateRimBridgeEndpointLocked(verification.Error ??
                "RimBridge generation context did not match the active DevBridge process.",
                verification.Code ?? RimBridgeIntegrationConstants.CompanionIdentityMismatchCode);
            SaveStateLocked();
            return new RimBridgeRoutePreparation
            {
                Context = identity,
                Failure = Failure(verification.Code == RimBridgeIntegrationConstants.AuthFailedCode
                        ? "RIMBRIDGE_AUTH_FAILED"
                        : "RIMBRIDGE_PROCESS_IDENTITY_MISMATCH",
                    verification.Error ?? "RimBridge generation context did not match DevBridge state.")
            };
        }

        bool hasLease = !string.IsNullOrWhiteSpace(leaseId) &&
            TryGetLeaseHolderLocked(leaseId, request, out _);
        RimBridgePolicyDecision decision = RimBridgeOperationPolicy.Evaluate(toolName,
            state.RimBridgePolicy, hasLease);
        if (!decision.Allowed)
        {
            return new RimBridgeRoutePreparation
            {
                Context = new RimBridgeRouteContext
                {
                    Endpoint = endpoint,
                    WorkflowId = request?.WorkflowId,
                    LaunchId = state.LaunchId,
                    Generation = state.Generation,
                    ProcessId = state.ProcessId,
                    ProfileFingerprint = state.LaunchProfileFingerprint ?? state.ProfileFingerprint,
                    LeaseId = leaseId,
                    Category = decision.Category
                },
                Failure = Failure(decision.ErrorCode,
                    "Tool '" + toolName + "' is denied for generation " + state.Generation +
                    ": " + decision.Reason + ".")
            };
        }

        return new RimBridgeRoutePreparation
        {
            Context = new RimBridgeRouteContext
            {
                Endpoint = endpoint,
                WorkflowId = request?.WorkflowId,
                LaunchId = state.LaunchId,
                Generation = state.Generation,
                ProcessId = state.ProcessId,
                ProfileFingerprint = state.LaunchProfileFingerprint ?? state.ProfileFingerprint,
                LeaseId = leaseId,
                Category = decision.Category
            }
        };
    }

    private bool TryPrepareStaleRimBridgeProcessRouteLocked(BridgeRequest request,
        string toolName, string leaseId, out RimBridgeRoutePreparation preparation)
    {
        preparation = null;
        if (state.Phase != BridgePhase.READY || state.ProcessId <= 0 ||
            state.ExternalModsConfigMutation != null ||
            state.ModsConfigMutationAuthority == ModsConfigMutationAuthorityValues.ExternalMutated)
            return false;

        RimBridgeEndpoint endpoint = RimBridgeEndpointStore.Load(runtimeRoot);
        if (endpoint == null || !endpoint.IsValid ||
            !string.Equals(endpoint.LaunchId, state.LaunchId, StringComparison.Ordinal) ||
            endpoint.Generation != state.Generation || endpoint.ProcessId != state.ProcessId ||
            endpoint.ProcessStartUtcTicks != state.ProcessStartUtcTicks)
            return false;

        bool owned;
        try
        {
            owned = IsOwnedProcess(endpoint.ProcessId, endpoint.ProcessStartUtcTicks);
        }
        catch (ProcessInspectionException exception)
        {
            preparation = new RimBridgeRoutePreparation
            {
                Context = new RimBridgeRouteContext
                {
                    Endpoint = endpoint,
                    WorkflowId = request?.WorkflowId,
                    LaunchId = state.LaunchId,
                    Generation = state.Generation,
                    ProcessId = state.ProcessId,
                    ProfileFingerprint = state.LaunchProfileFingerprint ?? state.ProfileFingerprint,
                    LeaseId = leaseId,
                    Category = RimBridgeOperationPolicy.CategoryFor(toolName)
                },
                Failure = CreateRimBridgeRouteFailureLocked(toolName, leaseId,
                    "RIMBRIDGE_PROCESS_INSPECTION_AMBIGUOUS", exception.Message, endpoint,
                    request?.WorkflowId)
            };
            return true;
        }

        if (owned)
            return false;

        string error = "The RimWorld process identity no longer matches the routed endpoint; " +
            "the endpoint was invalidated and no call was forwarded.";
        InvalidateRimBridgeEndpointLocked(error, RimBridgeIntegrationConstants.ProcessMismatchCode);
        SaveStateLocked();
        preparation = new RimBridgeRoutePreparation
        {
            Context = new RimBridgeRouteContext
            {
                Endpoint = endpoint,
                WorkflowId = request?.WorkflowId,
                LaunchId = state.LaunchId,
                Generation = state.Generation,
                ProcessId = state.ProcessId,
                ProfileFingerprint = state.LaunchProfileFingerprint ?? state.ProfileFingerprint,
                LeaseId = leaseId,
                Category = RimBridgeOperationPolicy.CategoryFor(toolName)
            },
            Failure = CreateRimBridgeRouteFailureLocked(toolName, leaseId,
                "RIMBRIDGE_PROCESS_IDENTITY_MISMATCH", error, endpoint,
                request?.WorkflowId)
        };
        return true;
    }

    private RimBridgeRouteResult CreateRimBridgeRouteFailureLocked(string toolName,
        string leaseId, string code, string error, RimBridgeEndpoint endpoint,
        string workflowId)
    {
        return new RimBridgeRouteResult
        {
            Operation = toolName == "tools/list" ? "tools" : "call",
            ToolName = toolName == "tools/list" ? null : toolName,
            WorkflowId = workflowId,
            Success = false,
            ErrorCode = code,
            Error = error,
            InvocationTimestampUtc = clock.UtcNow.ToUniversalTime(),
            LaunchId = state.LaunchId,
            Generation = state.Generation,
            ProfileFingerprint = state.LaunchProfileFingerprint ?? state.ProfileFingerprint,
            ProcessId = state.ProcessId,
            EndpointHost = endpoint?.Host,
            EndpointPort = endpoint?.Port ?? 0
        };
    }

    private string FindRouteLeaseId(BridgeRequest request, string requestedLeaseId)
    {
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(requestedLeaseId))
                return requestedLeaseId.Trim();
            return state.Leases
                .Where(value => string.Equals(value.Agent, request.Agent, StringComparison.Ordinal))
                .OrderByDescending(value => value.LastHeartbeatUtc)
                .Select(value => value.Id)
                .FirstOrDefault();
        }
    }

    private static bool TryParseRouteLeaseOptions(IReadOnlyList<string> arguments,
        out string leaseId, out string error)
    {
        leaseId = null;
        error = null;
        for (int index = 0; index < (arguments?.Count ?? 0); index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(argument, "--lease", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                {
                    error = "--lease requires a lease ID.";
                    return false;
                }
                leaseId = arguments[index].Trim();
                continue;
            }
            if (argument.StartsWith("--lease=", StringComparison.OrdinalIgnoreCase) &&
                argument.Length > "--lease=".Length)
            {
                leaseId = argument.Substring("--lease=".Length).Trim();
                continue;
            }
            error = "bridge tools accepts only --lease and --json options.";
            return false;
        }
        return true;
    }

    private static bool TryParseBridgeCall(IReadOnlyList<string> arguments,
        out RimBridgeRouteRequest routeRequest, out string error)
    {
        routeRequest = null;
        error = null;
        if (arguments == null || arguments.Count == 0 || string.IsNullOrWhiteSpace(arguments[0]))
        {
            error = "A RimBridge tool name is required.";
            return false;
        }

        string leaseId = null;
        List<string> jsonParts = new();
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--lease", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                {
                    error = "--lease requires a lease ID.";
                    return false;
                }
                leaseId = arguments[index];
            }
            else if (argument.StartsWith("--lease=", StringComparison.OrdinalIgnoreCase))
                leaseId = argument.Substring("--lease=".Length);
            else if (string.Equals(argument, "--args", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(argument, "--arguments", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Count)
                {
                    error = argument + " requires a JSON object.";
                    return false;
                }
                jsonParts.Add(arguments[index]);
            }
            else
                jsonParts.Add(argument);
        }

        string json = jsonParts.Count == 0 ? "{}" : string.Join(" ", jsonParts);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "RimBridge tool arguments must be a JSON object.";
                return false;
            }
            routeRequest = new RimBridgeRouteRequest
            {
                LeaseId = leaseId,
                ToolName = arguments[0].Trim(),
                Arguments = document.RootElement.Clone()
            };
            return true;
        }
        catch (JsonException exception)
        {
            error = "RimBridge arguments are not valid JSON: " + exception.Message;
            return false;
        }
    }

    private RimBridgeRouteResult BuildRimBridgeRouteResult(string operation, string toolName,
        RimBridgeRouteContext context, RimBridgeWireResult wire)
    {
        JsonElement? payload = RimBridgeRouteSecurity.Redact(wire?.Payload,
            context?.Endpoint?.Token);
        JsonElement? opaqueEvidence = RimBridgeRouteSecurity.Redact(
            ExtractOpaqueEvidence(wire?.RawResponse, wire?.Payload), context?.Endpoint?.Token);
        string errorCode = wire?.ErrorCode;
        string error = RimBridgeRouteSecurity.RedactText(wire?.Error, context?.Endpoint?.Token);
        bool success = wire?.Success == true;

        if (success && payload.HasValue && payload.Value.ValueKind == JsonValueKind.Object &&
            payload.Value.TryGetProperty("success", out JsonElement toolSuccess) &&
            toolSuccess.ValueKind == JsonValueKind.False)
        {
            success = false;
            errorCode = payload.Value.TryGetProperty("errorCode", out JsonElement code) &&
                        code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : "RIMBRIDGE_TOOL_ERROR";
            error = payload.Value.TryGetProperty("error", out JsonElement detail) &&
                    detail.ValueKind == JsonValueKind.String
                ? detail.GetString()
                : "RimBridge tool returned success=false.";
        }

        return new RimBridgeRouteResult
        {
            Operation = operation,
            ToolName = toolName,
            OperationId = ExtractOperationId(wire?.RawResponse, wire?.Payload),
            WorkflowId = context?.WorkflowId,
            Success = success,
            ErrorCode = errorCode,
            Error = error,
            InvocationTimestampUtc = clock.UtcNow.ToUniversalTime(),
            LaunchId = context?.LaunchId,
            Generation = context?.Generation ?? 0,
            ProfileFingerprint = context?.ProfileFingerprint,
            ProcessId = context?.ProcessId ?? 0,
            EndpointHost = context?.Endpoint?.Host,
            EndpointPort = context?.Endpoint?.Port ?? 0,
            Payload = payload,
            OpaqueEvidence = opaqueEvidence
        };
    }

    private RimBridgeRouteResult CompleteRimBridgeRoute(string operation, string toolName,
        RimBridgeRouteContext context, RimBridgeWireResult wire)
    {
        options.BeforeRimBridgeRouteCompletion?.Invoke(this);
        RimBridgeWireResult completionFailure = null;
        lock (gate)
        {
            SynchronizeLocked();
            completionFailure = ValidateRimBridgeRouteCompletionLocked(context);
        }

        return BuildRimBridgeRouteResult(operation, toolName, context,
            completionFailure ?? wire);
    }

    private RimBridgeWireResult ValidateRimBridgeRouteCompletionLocked(
        RimBridgeRouteContext context)
    {
        if (context?.Endpoint == null)
            return new RimBridgeWireResult
            {
                ErrorCode = "RIMBRIDGE_ENDPOINT_STALE",
                Error = "The routed RimBridge endpoint context was unavailable at completion; " +
                        "the result was discarded."
            };

        bool launchOrGenerationChanged =
            !string.Equals(state.LaunchId, context.LaunchId, StringComparison.Ordinal) ||
            state.Generation != context.Generation;
        if (launchOrGenerationChanged)
            return new RimBridgeWireResult
            {
                ErrorCode = "RIMBRIDGE_ENDPOINT_STALE",
                Error = "The active DevBridge launch or generation changed while the RimBridge " +
                        "operation was in flight; the result was discarded."
            };

        bool processChanged = state.ProcessId != context.ProcessId ||
            state.ProcessStartUtcTicks != context.Endpoint.ProcessStartUtcTicks;
        string endpointFailure = ValidateRimBridgeEndpointLocked(context.Endpoint);
        if (processChanged || (endpointFailure != null &&
            endpointFailure.Contains("process identity", StringComparison.OrdinalIgnoreCase)))
            return new RimBridgeWireResult
            {
                ErrorCode = "RIMBRIDGE_PROCESS_IDENTITY_MISMATCH",
                Error = "The RimWorld process identity changed while the RimBridge operation was " +
                        "in flight; the result was discarded."
            };

        RimBridgeEndpoint current = RimBridgeEndpointStore.Load(runtimeRoot);
        bool endpointChanged = current == null || !current.IsValid ||
            state.Phase != BridgePhase.READY ||
            !string.Equals(current.Host, context.Endpoint.Host, StringComparison.OrdinalIgnoreCase) ||
            current.Port != context.Endpoint.Port ||
            !string.Equals(current.Token, context.Endpoint.Token, StringComparison.Ordinal) ||
            !string.Equals(current.LaunchId, context.LaunchId, StringComparison.Ordinal) ||
            current.Generation != context.Generation || current.ProcessId != context.ProcessId ||
            current.ProcessStartUtcTicks != context.Endpoint.ProcessStartUtcTicks;
        if (endpointChanged || endpointFailure != null)
            return new RimBridgeWireResult
            {
                ErrorCode = "RIMBRIDGE_ENDPOINT_STALE",
                Error = "The active DevBridge route changed while the RimBridge operation was in " +
                        "flight; the result was discarded."
            };

        return null;
    }

    private static string ExtractOperationId(JsonElement? rawResponse, JsonElement? payload)
    {
        foreach (JsonElement? candidate in new[] { payload, rawResponse })
        {
            if (candidate is not { ValueKind: JsonValueKind.Object } value)
                continue;
            if (TryExtractOperationId(value, out string operationId))
                return operationId;
        }

        return null;
    }

    private static bool TryExtractOperationId(JsonElement value, out string operationId)
    {
        operationId = null;
        if (value.ValueKind != JsonValueKind.Object)
            return false;

        // RimBridgeServer's legacy tool aliases place the OperationEnvelope
        // under `operation` and serialize its CLR property as `OperationId`.
        // Newer/typed responses may expose the normalized lower-camel field
        // directly or under metadata/result. Accept only these bounded,
        // protocol-defined locations; do not recursively search arbitrary
        // tool payloads for a value that merely happens to be named alike.
        foreach (string propertyName in new[] { "operationId", "OperationId" })
        {
            if (value.TryGetProperty(propertyName, out JsonElement direct) &&
                direct.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(direct.GetString()))
            {
                operationId = direct.GetString();
                return true;
            }
        }

        foreach (string containerName in new[] { "metadata", "operation", "result" })
        {
            if (!value.TryGetProperty(containerName, out JsonElement container) ||
                container.ValueKind != JsonValueKind.Object)
                continue;
            foreach (string propertyName in new[] { "operationId", "OperationId" })
            {
                if (container.TryGetProperty(propertyName, out JsonElement nested) &&
                    nested.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nested.GetString()))
                {
                    operationId = nested.GetString();
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonElement? ExtractOpaqueEvidence(JsonElement? rawResponse,
        JsonElement? payload)
    {
        if (rawResponse.HasValue && rawResponse.Value.ValueKind == JsonValueKind.Object)
        {
            if (rawResponse.Value.TryGetProperty("evidence", out JsonElement evidence))
                return evidence.Clone();
            if (rawResponse.Value.TryGetProperty("metadata", out JsonElement metadata))
                return metadata.Clone();
        }
        if (payload.HasValue && payload.Value.ValueKind == JsonValueKind.Object)
        {
            if (payload.Value.TryGetProperty("evidence", out JsonElement evidence))
                return evidence.Clone();
            if (payload.Value.TryGetProperty("metadata", out JsonElement metadata))
                return metadata.Clone();
        }
        return null;
    }

    private void HandleRimBridgeRouteCredentialFailure(RimBridgeEndpoint endpoint,
        RimBridgeWireResult wire)
    {
        if (wire?.AuthenticationFailure != true || endpoint == null)
            return;
        lock (gate)
        {
            RimBridgeEndpoint current = RimBridgeEndpointStore.Load(runtimeRoot);
            if (current != null && string.Equals(current.Token, endpoint.Token, StringComparison.Ordinal) &&
                string.Equals(current.LaunchId, endpoint.LaunchId, StringComparison.Ordinal))
            {
                InvalidateRimBridgeEndpointLocked("RimBridge authentication failed; the endpoint credential was cleared.",
                    RimBridgeIntegrationConstants.AuthFailedCode);
                SaveStateLocked();
            }
        }
    }

    private static void EmitRimBridgeRoute(RimBridgeRouteResult result, BridgeRequest request,
        Action<string> emit)
    {
        if (request.Json)
        {
            emit(result.Success
                ? "RimBridge route completed; structured result is in the JSON response."
                : "RimBridge route failed; structured error is in the JSON response.");
            return;
        }

        if (!result.Success)
        {
            emit("RimBridge route failed: " + result.ErrorCode +
                (string.IsNullOrWhiteSpace(result.Error) ? string.Empty : " - " + result.Error));
            if (result.ErrorCode == "RIMBRIDGE_OPERATION_BLOCKED_BY_DEVBRIDGE_POLICY")
                emit("Tool: " + result.ToolName + "; generation: " + result.Generation + ". The call was not forwarded.");
            return;
        }

        emit("RimBridge route succeeded: " + (result.ToolName ?? "tools/list") +
            " via generation " + result.Generation + ", PID " + result.ProcessId + ".");
        if (result.Payload.HasValue)
            emit(JsonSerializer.Serialize(result.Payload.Value, CoordinatorSerialization.JsonOptions));
    }

    private int BridgeEndpointCommand(BridgeRequest request, Action<string> emit)
    {
        RimBridgeEndpoint endpoint;
        string failure;
        lock (gate)
        {
            SynchronizeLocked();
            endpoint = RimBridgeEndpointStore.Load(runtimeRoot);
            failure = ValidateRimBridgeEndpointLocked(endpoint);
            if (failure != null)
            {
                InvalidateRimBridgeEndpointLocked(failure, RimBridgeIntegrationConstants.ProcessMismatchCode);
                SaveStateLocked();
            }
        }

        if (failure != null || endpoint == null)
        {
            emit("RimBridge endpoint is unavailable; no token was returned.");
            if (!string.IsNullOrWhiteSpace(failure))
                emit("Error: " + failure);
            emit("Next action: Run DevBridge.cmd bridge status, then wait-ready or restart as indicated.");
            return 4;
        }

        if (request.Json)
        {
            emit("RimBridge endpoint is available; the explicit JSON endpoint response contains the token.");
            return 0;
        }

        emit("RimBridge endpoint (explicit credential command)");
        emit("Host: " + endpoint.Host);
        emit("Port: " + endpoint.Port);
        emit("Token: " + endpoint.Token);
        emit("Launch ID: " + endpoint.LaunchId);
        emit("Generation: " + endpoint.Generation);
        emit("RimWorld PID: " + endpoint.ProcessId);
        return 0;
    }

    private string ValidateRimBridgeEndpointLocked(RimBridgeEndpoint endpoint)
    {
        if (options.RimBridgeMode == RimBridgeMode.Off)
            return "RimBridge integration is disabled by configuration.";
        if (endpoint == null || !endpoint.IsValid)
            return "no valid same-launch RimBridge endpoint is persisted.";
        if (!string.Equals(endpoint.LaunchId, state.RimBridge?.LaunchId, StringComparison.Ordinal) ||
            endpoint.Generation != state.RimBridge?.Generation || endpoint.ProcessId != state.ProcessId ||
            endpoint.ProcessStartUtcTicks != state.ProcessStartUtcTicks ||
            !string.Equals(endpoint.LaunchId, state.LaunchId, StringComparison.Ordinal))
            return "the persisted endpoint does not match the active launch, generation, or process identity.";
        try
        {
            if (!IsOwnedProcess(endpoint.ProcessId, endpoint.ProcessStartUtcTicks))
                return "the RimWorld process identity changed; the endpoint was invalidated.";
        }
        catch (ProcessInspectionException)
        {
            return ProcessInspection.Message;
        }
        return null;
    }

    private static void EmitRimBridgeStatus(RimBridgeIntegrationState bridge, Action<string> emit)
    {
        bridge ??= RimBridgeIntegrationState.Disabled(RimBridgeMode.Off);
        emit("Mode: " + bridge.ConfiguredMode);
        emit("Package: " + bridge.PackageId +
            (string.IsNullOrWhiteSpace(bridge.Version) ? string.Empty : " version " + bridge.Version));
        emit("Lifecycle: " + bridge.LifecycleState);
        emit("Endpoint: " + (bridge.TokenAvailable && bridge.Port > 0
            ? bridge.Host + ":" + bridge.Port + " (token available; credential hidden)"
            : "not available"));
        if (!string.IsNullOrWhiteSpace(bridge.LaunchId))
            emit("Binding: launch " + bridge.LaunchId + ", generation " + bridge.Generation +
                ", PID " + bridge.ProcessId + ", start identity " + bridge.ProcessStartUtcTicks);
        if (bridge.DiscoveryTimestampUtc.HasValue)
            emit("Discovery UTC: " + bridge.DiscoveryTimestampUtc.Value.ToUniversalTime().ToString("O"));
        emit("Companion: " + (bridge.CompanionVerified
            ? "verified (" + (bridge.CompanionToolName ?? RimBridgeIntegrationConstants.CompanionToolName) + ")"
            : bridge.CompanionAvailable
                ? "present but not verified"
                : "not available") +
            (string.IsNullOrWhiteSpace(bridge.CompanionErrorCode)
                ? string.Empty
                : " [" + bridge.CompanionErrorCode + "]"));
        string companionDiagnosticCode = RimBridgeCompanionDiagnostics.Code(bridge);
        string companionDiagnosticReason = RimBridgeCompanionDiagnostics.Reason(bridge);
        if (!string.IsNullOrWhiteSpace(companionDiagnosticCode))
            emit("Companion diagnostic: " + companionDiagnosticCode +
                (string.IsNullOrWhiteSpace(companionDiagnosticReason)
                    ? string.Empty
                    : " - " + DiagnosticRedactor.Text(companionDiagnosticReason)));
        if (!string.IsNullOrWhiteSpace(bridge.ErrorCode))
            emit("Bridge error: " + bridge.ErrorCode +
                (string.IsNullOrWhiteSpace(bridge.Error) ? string.Empty : " - " + bridge.Error));
    }

}
