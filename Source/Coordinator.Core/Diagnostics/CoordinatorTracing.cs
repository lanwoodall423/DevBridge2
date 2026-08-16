using System.Diagnostics;
using System.Text.Json;

namespace DevBridge.Coordinator;

internal sealed class CoordinatorTraceContext
{
    internal string RequestId { get; init; }
    internal string Command { get; init; }
    internal string OperationId { get; init; }
    internal long StartedTimestamp { get; init; }
}

internal sealed partial class CoordinatorState
{
    internal BridgePhase PhaseForTesting => state?.Phase ?? BridgePhase.ERROR;
    internal int ProcessIdForTesting => state?.ProcessId ?? 0;

    internal void InjectFaultForTesting(CoordinatorFaultPoint point) =>
        options.FaultInjector?.Hit(point);

    internal void SetFaultInjectorForTesting(ICoordinatorFaultInjector injector) =>
        options.FaultInjector = injector;

    internal void WaitForWorkersForTesting(TimeSpan timeout)
    {
        Task[] workers;
        lock (gate)
        {
            workers = new[] { restartTask, launchTask, isolationTask }
                .Where(value => value != null && !value.IsCompleted).Distinct().ToArray();
        }
        try
        {
            if (workers.Length > 0)
                Task.WaitAll(workers, timeout);
        }
        catch (AggregateException)
        {
            // The fault-injection harness intentionally observes worker death.
        }
    }

    internal IDisposable BeginTraceRequest(BridgeRequest request)
    {
        CoordinatorTraceContext previous = traceContext.Value;
        traceContext.Value = request == null
            ? null
            : new CoordinatorTraceContext
            {
                RequestId = request.RequestId,
                Command = request.Command,
                OperationId = string.IsNullOrWhiteSpace(request.RequestId)
                    ? null : "op-" + request.RequestId,
                StartedTimestamp = Stopwatch.GetTimestamp()
            };
        return new TraceScope(() => traceContext.Value = previous);
    }

    internal void TraceRequestAccepted(BridgeRequest request) =>
        TraceEvent("ipc.request.accepted", request);

    internal void TraceRequestValidationRejected(BridgeRequest request, string errorCode) =>
        TraceEvent("ipc.request.validation.rejected", request, errorCode: errorCode);

    internal void TraceCommandStarted(BridgeRequest request) =>
        TraceEvent("command.dispatch.started", request);

    internal void TraceCommandCompleted(BridgeRequest request, int? exitCode)
    {
        CoordinatorTraceContext context = traceContext.Value;
        TraceEvent("command.dispatch.completed", request,
            durationMs: context == null ? null : ElapsedMilliseconds(context.StartedTimestamp),
            success: exitCode.HasValue && exitCode.Value == 0,
            errorCode: exitCode.HasValue && exitCode.Value == 0
                ? null : "COMMAND_EXIT_" + (exitCode?.ToString() ?? "EXCEPTION"));
    }

    internal void TraceHostEvent(string eventName, BridgeRequest request = null,
        string requestId = null, string command = null, long? durationMs = null,
        bool? success = null, string errorCode = null, string detail = null,
        string category = null, int? protocolVersion = null)
    {
        TraceEvent(eventName, request, requestId, command, durationMs, success,
            errorCode, detail, category, protocolVersion);
    }

    internal void TraceRecoveryActivity(string detail = null, string errorCode = null)
    {
        TraceEvent("recovery.worker.activity", operationId: "recovery-" +
            CoordinatorTraceOperations.NewId(), detail: detail, errorCode: errorCode);
    }

    internal void TraceLifecycleEvent(string eventName, BridgeRequest request = null,
        string detail = null, string errorCode = null, bool? success = null,
        long? durationMs = null)
    {
        TraceEvent(eventName, request, detail: detail, errorCode: errorCode,
            success: success, durationMs: durationMs);
    }

    private void TraceEvent(string eventName, BridgeRequest request = null,
        string requestId = null, string command = null, long? durationMs = null,
        bool? success = null, string errorCode = null, string detail = null,
        string category = null, int? protocolVersion = null,
        CoordinatorBuildIdentity buildIdentity = null,
        string operationId = null)
    {
        CoordinatorTraceContext context = traceContext.Value;
        PersistedState current = state;
        trace.Record(new CoordinatorTraceEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Event = eventName,
            RequestId = requestId ?? request?.RequestId ?? context?.RequestId,
            OperationId = operationId ?? context?.OperationId,
            Command = command ?? request?.Command ?? context?.Command,
            RuntimeSlotId = runtimeSlotId,
            Generation = current?.Generation,
            Phase = current?.Phase.ToString(),
            DurationMs = durationMs,
            Success = success,
            ErrorCode = errorCode,
            Detail = detail,
            Category = category,
            ProtocolVersion = protocolVersion,
            BuildIdentity = buildIdentity
        });
    }

    private void TracePhaseTransitionIfNeededLocked()
    {
        if (state == null || lastTracedPhase == state.Phase)
            return;

        BridgePhase previous = lastTracedPhase;
        lastTracedPhase = state.Phase;
        TraceEvent("lifecycle.phase.transition", detail:
            previous + "->" + state.Phase);
    }

    private static long ElapsedMilliseconds(long startedTimestamp)
    {
        if (startedTimestamp <= 0)
            return 0;
        return Math.Max(0, (long)(Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds));
    }

    internal static string TraceExceptionCategory(Exception exception)
    {
        if (exception == null)
            return null;
        if (exception is UnauthorizedAccessException)
            return "UNAUTHORIZED";
        if (exception is IOException)
            return "IO_ERROR";
        if (exception is JsonException)
            return "JSON_ERROR";
        if (exception is OperationCanceledException)
            return "CANCELED";
        return exception.GetType().Name;
    }
}

internal static class CoordinatorTraceOperations
{
    internal static string NewId() => Guid.NewGuid().ToString("N");
}

internal sealed class TraceScope : IDisposable
{
    private Action dispose;

    internal TraceScope(Action dispose) => this.dispose = dispose;

    public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
}
