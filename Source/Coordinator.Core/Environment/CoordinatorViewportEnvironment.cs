using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace DevBridge.Coordinator;

internal static class ViewportEnvironmentSchemas
{
    internal const string Response = "devbridge-viewport-environment/v1";
    internal const string CaptureMethod = "win32-window-runtime-only";
    internal const int WideWidth = 1600;
    internal const int WideHeight = 900;
    internal const int NarrowWidth = 1024;
    internal const int NarrowHeight = 768;
    internal const int MinimumWidth = 320;
    internal const int MinimumHeight = 240;
    internal const int MaximumWidth = 7680;
    internal const int MaximumHeight = 4320;
}

internal sealed class ViewportEnvironmentRequest
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; }
    [JsonPropertyName("width")]
    public int Width { get; set; }
    [JsonPropertyName("height")]
    public int Height { get; set; }

    internal bool IsCurrent => string.Equals(Kind, "current", StringComparison.Ordinal);

    internal static bool TryCreate(string kind, string widthText, string heightText,
        out ViewportEnvironmentRequest request, out string errorCode, out string error)
    {
        request = null;
        errorCode = "VIEWPORT_REQUEST_INVALID";
        error = "Viewport kind must be current, wide, narrow, or explicit width height.";
        string normalized = kind?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        int width;
        int height;
        switch (normalized)
        {
            case "current":
                if (widthText != null || heightText != null)
                {
                    error = "The current viewport request does not accept dimensions.";
                    return false;
                }

                request = new ViewportEnvironmentRequest
                {
                    Kind = normalized,
                    Width = 0,
                    Height = 0
                };
                return true;
            case "wide":
                if (widthText != null || heightText != null)
                {
                    error = "The wide viewport request uses the centralized supported dimensions.";
                    return false;
                }

                width = ViewportEnvironmentSchemas.WideWidth;
                height = ViewportEnvironmentSchemas.WideHeight;
                break;
            case "narrow":
                if (widthText != null || heightText != null)
                {
                    error = "The narrow viewport request uses the centralized supported dimensions.";
                    return false;
                }

                width = ViewportEnvironmentSchemas.NarrowWidth;
                height = ViewportEnvironmentSchemas.NarrowHeight;
                break;
            case "explicit":
                if (!int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture,
                        out width) ||
                    !int.TryParse(heightText, NumberStyles.None, CultureInfo.InvariantCulture,
                        out height))
                {
                    error = "An explicit viewport request requires integer width and height.";
                    return false;
                }

                break;
            default:
                error = "Viewport kind must be current, wide, narrow, or explicit width height.";
                return false;
        }

        if (width < ViewportEnvironmentSchemas.MinimumWidth ||
            width > ViewportEnvironmentSchemas.MaximumWidth ||
            height < ViewportEnvironmentSchemas.MinimumHeight ||
            height > ViewportEnvironmentSchemas.MaximumHeight)
        {
            error = "Viewport dimensions are outside the supported safety bounds.";
            return false;
        }

        request = new ViewportEnvironmentRequest
        {
            Kind = normalized,
            Width = width,
            Height = height
        };
        return true;
    }
}

internal sealed class ViewportWindowState
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }
    [JsonPropertyName("processStartIdentity")]
    public long ProcessStartIdentity { get; set; }
    [JsonPropertyName("windowHandle")]
    public long WindowHandle { get; set; }
    [JsonPropertyName("style")]
    public long Style { get; set; }
    [JsonPropertyName("extendedStyle")]
    public long ExtendedStyle { get; set; }
    [JsonPropertyName("showCommand")]
    public int ShowCommand { get; set; }
    [JsonPropertyName("placementFlags")]
    public int PlacementFlags { get; set; }
    [JsonPropertyName("minPositionX")]
    public int MinPositionX { get; set; }
    [JsonPropertyName("minPositionY")]
    public int MinPositionY { get; set; }
    [JsonPropertyName("maxPositionX")]
    public int MaxPositionX { get; set; }
    [JsonPropertyName("maxPositionY")]
    public int MaxPositionY { get; set; }
    [JsonPropertyName("normalLeft")]
    public int NormalLeft { get; set; }
    [JsonPropertyName("normalTop")]
    public int NormalTop { get; set; }
    [JsonPropertyName("normalRight")]
    public int NormalRight { get; set; }
    [JsonPropertyName("normalBottom")]
    public int NormalBottom { get; set; }
    [JsonPropertyName("outerLeft")]
    public int OuterLeft { get; set; }
    [JsonPropertyName("outerTop")]
    public int OuterTop { get; set; }
    [JsonPropertyName("outerRight")]
    public int OuterRight { get; set; }
    [JsonPropertyName("outerBottom")]
    public int OuterBottom { get; set; }
    [JsonPropertyName("outerWidth")]
    public int OuterWidth { get; set; }
    [JsonPropertyName("outerHeight")]
    public int OuterHeight { get; set; }
    [JsonPropertyName("clientWidth")]
    public int ClientWidth { get; set; }
    [JsonPropertyName("clientHeight")]
    public int ClientHeight { get; set; }
    [JsonPropertyName("monitorLeft")]
    public int MonitorLeft { get; set; }
    [JsonPropertyName("monitorTop")]
    public int MonitorTop { get; set; }
    [JsonPropertyName("monitorRight")]
    public int MonitorRight { get; set; }
    [JsonPropertyName("monitorBottom")]
    public int MonitorBottom { get; set; }
    [JsonPropertyName("captureMethod")]
    public string CaptureMethod { get; set; } = ViewportEnvironmentSchemas.CaptureMethod;
}

internal sealed class ViewportEnvironmentTransaction
{
    public string TransactionId { get; set; }
    public string LeaseId { get; set; }
    public int Generation { get; set; }
    public string RequestedKind { get; set; }
    public int RequestedWidth { get; set; }
    public int RequestedHeight { get; set; }
    public ViewportWindowState CapturedState { get; set; }
    public ViewportWindowState PreparedState { get; set; }
    public bool Prepared { get; set; }
    public bool Verified { get; set; }
    public bool Restored { get; set; }
    public bool RestorationVerified { get; set; }
    public bool PersistentPreferenceMutation { get; set; }
    public string CaptureMethod { get; set; } = ViewportEnvironmentSchemas.CaptureMethod;
    public DateTime StartedUtc { get; set; }
    public DateTime? RestoredUtc { get; set; }
    public string RestorationErrorCode { get; set; }
    public string RestorationError { get; set; }
}

internal sealed class ViewportEnvironmentResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = ViewportEnvironmentSchemas.Response;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; set; }

    [JsonPropertyName("nextAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string NextAction { get; set; }

    [JsonPropertyName("transactionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string TransactionId { get; set; }

    [JsonPropertyName("leaseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string LeaseId { get; set; }

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("requested")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ViewportEnvironmentRequest Requested { get; set; }

    [JsonPropertyName("capturedState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ViewportWindowState CapturedState { get; set; }

    [JsonPropertyName("effectiveViewport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ViewportWindowState EffectiveViewport { get; set; }

    [JsonPropertyName("restoredViewport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ViewportWindowState RestoredViewport { get; set; }

    [JsonPropertyName("persistentPreferenceMutation")]
    public bool PersistentPreferenceMutation { get; set; }

    [JsonPropertyName("restorationVerified")]
    public bool RestorationVerified { get; set; }

    [JsonPropertyName("cleanupStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string CleanupStatus { get; set; }

    internal static ViewportEnvironmentResponse Failure(
        string code, string error, string nextAction = null, string status = "blocked") => new()
    {
        Success = false,
        Status = status,
        ErrorCode = code,
        Error = error,
        NextAction = nextAction,
        PersistentPreferenceMutation = false
    };

    internal static ViewportEnvironmentResponse FromTransaction(
        ViewportEnvironmentTransaction transaction, string status, bool success,
        ViewportWindowState effective = null, ViewportWindowState restored = null,
        string cleanupStatus = null) => new()
    {
        Success = success,
        Status = status,
        TransactionId = transaction?.TransactionId,
        LeaseId = transaction?.LeaseId,
        Generation = transaction?.Generation ?? 0,
        Requested = transaction == null ? null : new ViewportEnvironmentRequest
        {
            Kind = transaction.RequestedKind,
            Width = transaction.RequestedWidth,
            Height = transaction.RequestedHeight
        },
        CapturedState = transaction?.CapturedState,
        EffectiveViewport = effective ?? transaction?.PreparedState,
        RestoredViewport = restored,
        PersistentPreferenceMutation = transaction?.PersistentPreferenceMutation ?? false,
        RestorationVerified = transaction?.RestorationVerified ?? false,
        CleanupStatus = cleanupStatus
    };
}

internal sealed class ViewportEnvironmentControlResult
{
    internal bool Success { get; init; }
    internal string ErrorCode { get; init; }
    internal string Error { get; init; }
    internal ViewportWindowState State { get; init; }
    internal bool Verified { get; init; }
    internal bool RestorationVerified { get; init; }
    internal string CaptureMethod { get; init; } = ViewportEnvironmentSchemas.CaptureMethod;
}

internal interface IViewportEnvironmentController
{
    ViewportEnvironmentControlResult Capture(int processId, long processStartIdentity);
    ViewportEnvironmentControlResult Apply(
        ViewportEnvironmentRequest request,
        ViewportEnvironmentTransaction transaction);
    ViewportEnvironmentControlResult Restore(ViewportEnvironmentTransaction transaction);
    ViewportEnvironmentControlResult VerifyPrepared(ViewportEnvironmentTransaction transaction);
}

internal sealed partial class CoordinatorState
{
    private int ViewportEnvironmentCommand(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit, Func<bool> connected)
    {
        if (arguments.Count == 0 ||
            !string.Equals(arguments[0], "viewport", StringComparison.OrdinalIgnoreCase))
        {
            emit("Usage: DevBridge.cmd environment viewport begin <lease-id> <current|wide|narrow|explicit> [width height] | restore <lease-id> <transaction-id> | status");
            return 2;
        }

        if (arguments.Count < 2)
        {
            emit("Usage: DevBridge.cmd environment viewport begin <lease-id> <current|wide|narrow|explicit> [width height] | restore <lease-id> <transaction-id> | status");
            return 2;
        }

        return arguments[1].Trim().ToLowerInvariant() switch
        {
            "begin" => BeginViewportEnvironment(arguments, request, emit, connected),
            "restore" => RestoreViewportEnvironment(arguments, request, emit),
            "status" => StatusViewportEnvironment(arguments, request, emit),
            _ => EnvironmentUsage(arguments, emit)
        };
    }

    private static int EnvironmentUsage(IReadOnlyList<string> arguments, Action<string> emit)
    {
        emit("Unknown environment viewport operation: " + string.Join(" ", arguments));
        emit("Usage: DevBridge.cmd environment viewport begin <lease-id> <current|wide|narrow|explicit> [width height] | restore <lease-id> <transaction-id> | status");
        return 2;
    }

    private int BeginViewportEnvironment(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit, Func<bool> connected)
    {
        if (arguments.Count < 4 || arguments.Count > 6 || !connected())
        {
            request.ViewportResponse = ViewportEnvironmentResponse.Failure(
                "VIEWPORT_REQUEST_INVALID",
                "Viewport begin requires a connected caller, lease id, and viewport kind.",
                nextAction: null,
                status: "error");
            return 2;
        }

        string leaseId = arguments[2]?.Trim();
        string kind = arguments[3]?.Trim();
        string widthText = arguments.Count > 4 ? arguments[4] : null;
        string heightText = arguments.Count > 5 ? arguments[5] : null;
        if (!ViewportEnvironmentRequest.TryCreate(kind, widthText, heightText,
                out ViewportEnvironmentRequest viewportRequest,
                out string requestCode, out string requestError))
        {
            request.ViewportResponse = ViewportEnvironmentResponse.Failure(
                requestCode, requestError, nextAction: null, status: "error");
            return 2;
        }

        lock (lifecycleGate)
        {
            lock (gate)
            {
                PruneStaleLeasesLocked();
                if (!TryGetLeaseHolderLocked(leaseId, request, out TestLease lease))
                    return SetViewportFailure(request, "VIEWPORT_LEASE_REQUIRED",
                        "The viewport operation requires the caller's current canonical test lease.");

                if (state.Phase != BridgePhase.READY || state.ProcessId <= 0 ||
                    state.ProcessStartUtcTicks <= 0 || lease.Generation != state.Generation)
                    return SetViewportFailure(request, "VIEWPORT_RUNTIME_NOT_READY",
                        "Viewport control requires the lease-bound READY RimWorld generation.");

                ViewportEnvironmentTransaction active = state.ViewportEnvironment;
                if (active != null && !active.Restored)
                {
                    if (!string.Equals(active.LeaseId, lease.Id, StringComparison.OrdinalIgnoreCase))
                        return SetViewportFailure(request, "VIEWPORT_ENVIRONMENT_BUSY",
                            "Another canonical lease already owns a viewport transaction.");

                    if (!ViewportRequestMatches(active, viewportRequest))
                        return SetViewportFailure(request, "VIEWPORT_ENVIRONMENT_BUSY",
                            "The current lease already owns a different viewport transaction.");

                    ViewportEnvironmentControlResult verified = active.Prepared
                        ? NormalizeViewportResult(
                            SafeViewportOperation(
                                () => options.ViewportEnvironmentController.VerifyPrepared(active),
                                "VIEWPORT_VERIFICATION_FAILED",
                                "The existing viewport transaction could not be verified."),
                            requireRestoration: false)
                        : NormalizeViewportResult(
                            SafeViewportOperation(
                                () => options.ViewportEnvironmentController.Restore(active),
                                "VIEWPORT_RESTORE_FAILED",
                                "The captured viewport could not be restored."),
                            requireRestoration: true);
                    if (!verified.Success)
                    {
                        if (active.Prepared)
                        {
                            ViewportEnvironmentControlResult restoredAfterVerificationFailure =
                                NormalizeViewportResult(
                                    SafeViewportOperation(
                                        () => options.ViewportEnvironmentController.Restore(active),
                                        "VIEWPORT_RESTORE_FAILED",
                                        "The captured viewport could not be restored after verification failed."),
                                    requireRestoration: true);
                            if (restoredAfterVerificationFailure.Success)
                            {
                                active.Restored = true;
                                active.RestorationVerified = true;
                                active.RestoredUtc = clock.UtcNow;
                                active.RestorationErrorCode = null;
                                active.RestorationError = null;
                                SaveStateLocked();
                                active = null;
                            }
                            else
                            {
                                active.RestorationErrorCode = restoredAfterVerificationFailure.ErrorCode ??
                                    "VIEWPORT_RESTORE_FAILED";
                                active.RestorationError = restoredAfterVerificationFailure.Error ??
                                    "The existing viewport transaction could not be restored after verification failed.";
                                SaveStateLocked();
                                return SetViewportFailure(request, active.RestorationErrorCode,
                                    active.RestorationError);
                            }
                        }
                        else
                        {
                            active.RestorationErrorCode = verified.ErrorCode ??
                                "VIEWPORT_RESTORE_FAILED";
                            active.RestorationError = verified.Error ??
                                "The captured viewport could not be restored.";
                            SaveStateLocked();
                            return SetViewportFailure(request, active.RestorationErrorCode,
                                active.RestorationError);
                        }
                    }

                    if (active == null)
                    {
                        // The stale transaction was safely restored above. Continue
                        // with a fresh capture so the new request has its own exact
                        // restoration snapshot.
                    }
                    else if (!active.Prepared)
                    {
                        active.Restored = true;
                        active.RestorationVerified = verified.RestorationVerified;
                        active.RestoredUtc = clock.UtcNow;
                        SaveStateLocked();
                        active = null;
                    }
                    else
                    {
                        active.PreparedState = verified.State ?? active.PreparedState;
                        active.Verified = verified.Verified;
                        request.ViewportResponse = ViewportEnvironmentResponse.FromTransaction(
                            active, "prepared", true, active.PreparedState);
                        return 0;
                    }
                }

                ViewportEnvironmentControlResult captured = NormalizeViewportResult(
                    SafeViewportOperation(
                        () => options.ViewportEnvironmentController.Capture(
                            state.ProcessId, state.ProcessStartUtcTicks),
                        "VIEWPORT_CAPTURE_FAILED",
                        "DevBridge could not capture the exact RimWorld window state."),
                    requireRestoration: false);
                if (!captured.Success || captured.State == null)
                {
                    return SetViewportFailure(request, captured.ErrorCode ??
                        "VIEWPORT_CAPTURE_FAILED", captured.Error ??
                        "DevBridge could not capture the exact RimWorld window state.");
                }

                ViewportEnvironmentTransaction transaction = new()
                {
                    TransactionId = "viewport-" + Guid.NewGuid().ToString("N"),
                    LeaseId = lease.Id,
                    Generation = state.Generation,
                    RequestedKind = viewportRequest.Kind,
                    RequestedWidth = viewportRequest.Width,
                    RequestedHeight = viewportRequest.Height,
                    CapturedState = captured.State,
                    PersistentPreferenceMutation = false,
                    CaptureMethod = captured.CaptureMethod,
                    StartedUtc = clock.UtcNow
                };

                // Persist the exact snapshot before any window mutation. A
                // coordinator crash after this point still has enough data to
                // attempt restoration during recovery.
                state.ViewportEnvironment = transaction;
                SaveStateLocked();

                ViewportEnvironmentControlResult applied = NormalizeViewportResult(
                    SafeViewportOperation(
                        () => options.ViewportEnvironmentController.Apply(viewportRequest, transaction),
                        "VIEWPORT_APPLY_FAILED",
                        "DevBridge could not apply the temporary viewport safely."),
                    requireRestoration: false);
                if (applied.Success && applied.State != null && applied.Verified)
                {
                    transaction.PreparedState = applied.State;
                    transaction.Prepared = true;
                    transaction.Verified = true;
                    SaveStateLocked();
                    request.ViewportResponse = ViewportEnvironmentResponse.FromTransaction(
                        transaction, "prepared", true, applied.State);
                    return 0;
                }

                ViewportEnvironmentControlResult restored = NormalizeViewportResult(
                    SafeViewportOperation(
                        () => options.ViewportEnvironmentController.Restore(transaction),
                        "VIEWPORT_RESTORE_FAILED",
                        "DevBridge could not restore the original RimWorld window state after preparation failed."),
                    requireRestoration: true);
                if (restored.Success)
                {
                    transaction.Restored = true;
                    transaction.RestorationVerified = restored.RestorationVerified;
                    transaction.RestoredUtc = clock.UtcNow;
                    transaction.RestorationErrorCode = null;
                    transaction.RestorationError = null;
                    SaveStateLocked();
                    request.ViewportResponse = ViewportEnvironmentResponse.Failure(
                        applied.ErrorCode ?? "VIEWPORT_PREPARE_FAILED",
                        applied.Error ?? "DevBridge could not verify the requested viewport.",
                        nextAction: null,
                        status: "error");
                    request.ViewportResponse.TransactionId = transaction.TransactionId;
                    request.ViewportResponse.LeaseId = transaction.LeaseId;
                    request.ViewportResponse.Generation = transaction.Generation;
                    request.ViewportResponse.Requested = viewportRequest;
                    request.ViewportResponse.CapturedState = transaction.CapturedState;
                    request.ViewportResponse.RestoredViewport = restored.State;
                    request.ViewportResponse.RestorationVerified = restored.RestorationVerified;
                    request.ViewportResponse.CleanupStatus = "restored-after-prepare-failure";
                    return 4;
                }

                transaction.RestorationErrorCode = restored.ErrorCode ?? "VIEWPORT_RESTORE_FAILED";
                transaction.RestorationError = restored.Error ??
                    "DevBridge could not restore the original RimWorld window state after a failed viewport preparation.";
                SaveStateLocked();
                request.ViewportResponse = ViewportEnvironmentResponse.Failure(
                    "VIEWPORT_RESTORE_FAILED", transaction.RestorationError,
                    nextAction: "DevBridge.cmd environment viewport restore " + lease.Id + " " + transaction.TransactionId,
                    status: "cleanupFailed");
                request.ViewportResponse.TransactionId = transaction.TransactionId;
                request.ViewportResponse.LeaseId = transaction.LeaseId;
                request.ViewportResponse.Generation = transaction.Generation;
                request.ViewportResponse.Requested = viewportRequest;
                request.ViewportResponse.CapturedState = transaction.CapturedState;
                request.ViewportResponse.CleanupStatus = "restore-required";
                return 4;
            }
        }
    }

    private int RestoreViewportEnvironment(IReadOnlyList<string> arguments,
        BridgeRequest request, Action<string> emit)
    {
        if (arguments.Count != 4 || string.IsNullOrWhiteSpace(arguments[2]) ||
            string.IsNullOrWhiteSpace(arguments[3]))
        {
            request.ViewportResponse = ViewportEnvironmentResponse.Failure(
                "VIEWPORT_REQUEST_INVALID",
                "Viewport restore requires lease id and transaction id.",
                nextAction: null,
                status: "error");
            return 2;
        }

        string leaseId = arguments[2].Trim();
        string transactionId = arguments[3].Trim();
        lock (lifecycleGate)
        {
            lock (gate)
            {
                PruneStaleLeasesLocked();
                ViewportEnvironmentTransaction transaction = state.ViewportEnvironment;
                if (transaction == null)
                {
                    request.ViewportResponse = ViewportEnvironmentResponse.Failure(
                        null, null, nextAction: null, status: "alreadyRestored");
                    request.ViewportResponse.Success = true;
                    request.ViewportResponse.TransactionId = transactionId;
                    request.ViewportResponse.LeaseId = leaseId;
                    request.ViewportResponse.RestorationVerified = true;
                    request.ViewportResponse.CleanupStatus = "already-restored";
                    return 0;
                }

                if (!string.Equals(transaction.TransactionId, transactionId,
                        StringComparison.Ordinal))
                    return SetViewportFailure(request, "VIEWPORT_TRANSACTION_NOT_FOUND",
                        "The requested viewport transaction is not the current durable transaction.");

                if (!TryGetLeaseHolderLocked(leaseId, request, out TestLease lease) ||
                    !string.Equals(transaction.LeaseId, lease.Id, StringComparison.OrdinalIgnoreCase))
                    return SetViewportFailure(request, "VIEWPORT_LEASE_REQUIRED",
                        "The viewport transaction can only be restored by its owning canonical lease.");

                if (transaction.Restored)
                {
                    request.ViewportResponse = ViewportEnvironmentResponse.FromTransaction(
                        transaction, "alreadyRestored", true,
                        transaction.PreparedState, transaction.CapturedState, "already-restored");
                    request.ViewportResponse.RestorationVerified = transaction.RestorationVerified;
                    return 0;
                }

                ViewportEnvironmentControlResult restored = NormalizeViewportResult(
                    SafeViewportOperation(
                        () => options.ViewportEnvironmentController.Restore(transaction),
                        "VIEWPORT_RESTORE_FAILED",
                        "DevBridge could not verify restoration of the captured RimWorld window state."),
                    requireRestoration: true);
                if (!restored.Success)
                {
                    transaction.RestorationErrorCode = restored.ErrorCode ?? "VIEWPORT_RESTORE_FAILED";
                    transaction.RestorationError = restored.Error ??
                        "DevBridge could not verify restoration of the captured RimWorld window state.";
                    SaveStateLocked();
                    request.ViewportResponse = ViewportEnvironmentResponse.Failure(
                        transaction.RestorationErrorCode, transaction.RestorationError,
                        nextAction: "DevBridge.cmd environment viewport restore " + lease.Id + " " + transaction.TransactionId,
                        status: "cleanupFailed");
                    request.ViewportResponse.TransactionId = transaction.TransactionId;
                    request.ViewportResponse.LeaseId = transaction.LeaseId;
                    request.ViewportResponse.Generation = transaction.Generation;
                    request.ViewportResponse.Requested = new ViewportEnvironmentRequest
                    {
                        Kind = transaction.RequestedKind,
                        Width = transaction.RequestedWidth,
                        Height = transaction.RequestedHeight
                    };
                    request.ViewportResponse.CapturedState = transaction.CapturedState;
                    request.ViewportResponse.RestorationVerified = false;
                    request.ViewportResponse.CleanupStatus = "restore-required";
                    return 4;
                }

                transaction.Restored = true;
                transaction.RestorationVerified = restored.RestorationVerified;
                transaction.RestoredUtc = clock.UtcNow;
                transaction.RestorationErrorCode = null;
                transaction.RestorationError = null;
                SaveStateLocked();
                request.ViewportResponse = ViewportEnvironmentResponse.FromTransaction(
                    transaction, "restored", true, transaction.PreparedState,
                    restored.State, "restored");
                request.ViewportResponse.RestorationVerified = restored.RestorationVerified;
                return 0;
            }
        }
    }

    private int StatusViewportEnvironment(IReadOnlyList<string> arguments,
        BridgeRequest request, Action<string> emit)
    {
        if (arguments.Count != 2)
        {
            request.ViewportResponse = ViewportEnvironmentResponse.Failure(
                "VIEWPORT_REQUEST_INVALID", "Viewport status does not accept arguments.",
                nextAction: null, status: "error");
            return 2;
        }

        lock (gate)
        {
            ViewportEnvironmentTransaction transaction = state.ViewportEnvironment;
            request.ViewportResponse = transaction == null
                ? ViewportEnvironmentResponse.Failure(null, null, nextAction: null,
                    status: "idle")
                : ViewportEnvironmentResponse.FromTransaction(
                    transaction,
                    transaction.Restored ? "restored" : transaction.Prepared ? "prepared" : "captured",
                    transaction.Restored || transaction.Prepared,
                    transaction.PreparedState,
                    transaction.Restored ? transaction.CapturedState : null,
                    transaction.Restored ? "restored" : "restore-required");
            if (transaction == null)
            {
                request.ViewportResponse.Success = true;
                request.ViewportResponse.CleanupStatus = "none";
            }

            return request.ViewportResponse.Success ? 0 : 4;
        }
    }

    private static bool ViewportRequestMatches(ViewportEnvironmentTransaction transaction,
        ViewportEnvironmentRequest request) =>
        transaction != null && request != null &&
        string.Equals(transaction.RequestedKind, request.Kind, StringComparison.Ordinal) &&
        transaction.RequestedWidth == request.Width && transaction.RequestedHeight == request.Height;

    private static int SetViewportFailure(BridgeRequest request, string code, string error)
    {
        request.ViewportResponse = ViewportEnvironmentResponse.Failure(code, error);
        return 4;
    }

    private bool TryRestoreViewportForLeaseLocked(string leaseId)
    {
        ViewportEnvironmentTransaction transaction = state.ViewportEnvironment;
        if (transaction == null || transaction.Restored ||
            !string.Equals(transaction.LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
            return true;

        ViewportEnvironmentControlResult restored = NormalizeViewportResult(
            SafeViewportOperation(
                () => options.ViewportEnvironmentController.Restore(transaction),
                "VIEWPORT_RESTORE_FAILED",
                "DevBridge could not restore the viewport while releasing the canonical lease."),
            requireRestoration: true);
        if (restored.Success)
        {
            transaction.Restored = true;
            transaction.RestorationVerified = restored.RestorationVerified;
            transaction.RestoredUtc = clock.UtcNow;
            transaction.RestorationErrorCode = null;
            transaction.RestorationError = null;
            return true;
        }

        transaction.RestorationErrorCode = restored.ErrorCode ?? "VIEWPORT_RESTORE_FAILED";
        transaction.RestorationError = restored.Error ??
            "DevBridge could not restore the viewport while releasing the canonical lease.";
        return false;
    }

    private bool RecoverViewportEnvironmentLocked()
    {
        ViewportEnvironmentTransaction transaction = state.ViewportEnvironment;
        if (transaction == null || transaction.Restored)
            return true;

        bool restored = TryRestoreViewportForLeaseLocked(transaction.LeaseId);
        SaveStateLocked();
        return restored;
    }

    private static ViewportEnvironmentControlResult SafeViewportOperation(
        Func<ViewportEnvironmentControlResult> operation,
        string errorCode,
        string error)
    {
        try
        {
            return operation() ?? new ViewportEnvironmentControlResult
            {
                Success = false,
                ErrorCode = errorCode,
                Error = error
            };
        }
        catch
        {
            return new ViewportEnvironmentControlResult
            {
                Success = false,
                ErrorCode = errorCode,
                Error = error
            };
        }
    }

    private static ViewportEnvironmentControlResult NormalizeViewportResult(
        ViewportEnvironmentControlResult result,
        bool requireRestoration)
    {
        if (result == null || !result.Success)
            return result ?? new ViewportEnvironmentControlResult
            {
                Success = false,
                ErrorCode = requireRestoration
                    ? "VIEWPORT_RESTORE_FAILED"
                    : "VIEWPORT_OPERATION_FAILED",
                Error = requireRestoration
                    ? "The viewport restoration operation returned no result."
                    : "The viewport operation returned no result."
            };

        if (result.State != null && (!requireRestoration || result.RestorationVerified) &&
            (requireRestoration || result.Verified))
            return result;

        return new ViewportEnvironmentControlResult
        {
            Success = false,
            ErrorCode = requireRestoration
                ? "VIEWPORT_RESTORE_VERIFICATION_FAILED"
                : "VIEWPORT_VERIFICATION_FAILED",
            Error = requireRestoration
                ? "The original RimWorld window state was not verified after restoration."
                : "The requested RimWorld viewport was not verified after the operation.",
            State = result.State,
            Verified = result.Verified,
            RestorationVerified = result.RestorationVerified,
            CaptureMethod = result.CaptureMethod
        };
    }
}

internal sealed class WindowsViewportEnvironmentController : IViewportEnvironmentController
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const long WsPopup = 0x80000000L;
    private const long WsOverlappedWindow = 0x00CF0000L;
    private const long WsMaximize = 0x01000000L;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwRestore = 9;
    private const uint MonitorDefaultToNearest = 2;

    public ViewportEnvironmentControlResult Capture(int processId, long processStartIdentity)
    {
        if (!OperatingSystem.IsWindows())
            return Failure("VIEWPORT_UNSUPPORTED_RUNTIME",
                "Transactional viewport control requires the supported Windows runtime window API.");

        try
        {
            IntPtr window = FindOwnedWindow(processId, processStartIdentity);
            if (window == IntPtr.Zero)
                return Failure("VIEWPORT_WINDOW_NOT_FOUND",
                    "The lease-bound RimWorld process has no accessible top-level window.");

            ViewportWindowState state = ReadState(processId, processStartIdentity, window);
            return state == null
                ? Failure("VIEWPORT_CAPTURE_FAILED",
                    "DevBridge could not capture the RimWorld window placement and client dimensions.")
                : Success(state, verified: true);
        }
        catch (ViewportControlException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch
        {
            return Failure("VIEWPORT_CAPTURE_FAILED",
                "DevBridge could not capture the RimWorld window state safely.");
        }
    }

    public ViewportEnvironmentControlResult Apply(
        ViewportEnvironmentRequest request,
        ViewportEnvironmentTransaction transaction)
    {
        if (request.IsCurrent)
            return Success(transaction.CapturedState, verified: true);

        try
        {
            IntPtr window = new(transaction.CapturedState.WindowHandle);
            long style = GetWindowLong(window, GwlStyle);
            long exStyle = GetWindowLong(window, GwlExStyle);
            long targetStyle = (style & ~WsPopup) | WsOverlappedWindow;
            targetStyle &= ~WsMaximize;
            if (!SetWindowLong(window, GwlStyle, targetStyle) ||
                !SetWindowLong(window, GwlExStyle, exStyle))
                throw new ViewportControlException("VIEWPORT_APPLY_FAILED",
                    "The RimWorld window rejected the temporary supported windowed style.");

            ShowWindow(window, SwRestore);
            RECT client = new() { Left = 0, Top = 0, Right = request.Width, Bottom = request.Height };
            if (!AdjustWindowRectEx(ref client, (uint)targetStyle, false, (uint)exStyle))
                throw new ViewportControlException("VIEWPORT_APPLY_FAILED",
                    "Windows could not calculate a safe outer rectangle for the requested client viewport.");

            int outerWidth = client.Right - client.Left;
            int outerHeight = client.Bottom - client.Top;
            RECT source = new()
            {
                Left = transaction.CapturedState.OuterLeft,
                Top = transaction.CapturedState.OuterTop,
                Right = transaction.CapturedState.OuterLeft + outerWidth,
                Bottom = transaction.CapturedState.OuterTop + outerHeight
            };
            if (!SetWindowPos(window, IntPtr.Zero, source.Left, source.Top,
                    source.Right - source.Left, source.Bottom - source.Top,
                    SwpNoZOrder | SwpNoActivate | SwpFrameChanged))
                throw new ViewportControlException("VIEWPORT_APPLY_FAILED",
                    "Windows could not apply the temporary viewport rectangle.");

            ViewportWindowState effective = ReadState(
                transaction.CapturedState.ProcessId,
                transaction.CapturedState.ProcessStartIdentity,
                window);
            if (effective == null || effective.ClientWidth != request.Width ||
                effective.ClientHeight != request.Height)
                return Failure("VIEWPORT_EFFECTIVE_DIMENSIONS_MISMATCH",
                    "The requested viewport was not verified at the actual RimWorld client area.",
                    effective);

            return Success(effective, verified: true);
        }
        catch (ViewportControlException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch
        {
            return Failure("VIEWPORT_APPLY_FAILED",
                "DevBridge could not apply the temporary viewport safely.");
        }
    }

    public ViewportEnvironmentControlResult Restore(ViewportEnvironmentTransaction transaction)
    {
        if (transaction?.CapturedState == null)
            return Failure("VIEWPORT_RESTORE_STATE_MISSING",
                "The durable viewport snapshot is missing; restoration cannot be attempted safely.");

        try
        {
            IntPtr window = FindOwnedWindow(transaction.CapturedState.ProcessId,
                transaction.CapturedState.ProcessStartIdentity);
            if (window == IntPtr.Zero || window.ToInt64() != transaction.CapturedState.WindowHandle)
                return Failure("VIEWPORT_PROCESS_OR_WINDOW_CHANGED",
                    "The original RimWorld window identity is no longer available for exact restoration.");

            if (!SetWindowLong(window, GwlStyle, transaction.CapturedState.Style) ||
                !SetWindowLong(window, GwlExStyle, transaction.CapturedState.ExtendedStyle))
                throw new ViewportControlException("VIEWPORT_RESTORE_FAILED",
                    "Windows rejected restoration of the captured window style.");

            WINDOWPLACEMENT placement = new()
            {
                Length = Marshal.SizeOf<WINDOWPLACEMENT>(),
                Flags = transaction.CapturedState.PlacementFlags,
                ShowCmd = transaction.CapturedState.ShowCommand,
                MinPosition = new POINT
                {
                    X = transaction.CapturedState.MinPositionX,
                    Y = transaction.CapturedState.MinPositionY
                },
                MaxPosition = new POINT
                {
                    X = transaction.CapturedState.MaxPositionX,
                    Y = transaction.CapturedState.MaxPositionY
                },
                NormalPosition = new RECT
                {
                    Left = transaction.CapturedState.NormalLeft,
                    Top = transaction.CapturedState.NormalTop,
                    Right = transaction.CapturedState.NormalRight,
                    Bottom = transaction.CapturedState.NormalBottom
                }
            };
            if (!SetWindowPlacement(window, ref placement))
                throw new ViewportControlException("VIEWPORT_RESTORE_FAILED",
                    "Windows rejected restoration of the captured window placement.");

            ShowWindow(window, transaction.CapturedState.ShowCommand);
            IntPtr insertAfter = (transaction.CapturedState.ExtendedStyle & WsExTopmost) != 0
                ? HwndTopmost
                : HwndNotTopmost;
            if (!SetWindowPos(window, insertAfter,
                    transaction.CapturedState.OuterLeft,
                    transaction.CapturedState.OuterTop,
                    transaction.CapturedState.OuterWidth,
                    transaction.CapturedState.OuterHeight,
                    SwpNoActivate | SwpFrameChanged))
                throw new ViewportControlException("VIEWPORT_RESTORE_FAILED",
                    "Windows rejected restoration of the captured outer window rectangle.");

            ViewportWindowState restored = ReadState(
                transaction.CapturedState.ProcessId,
                transaction.CapturedState.ProcessStartIdentity,
                window);
            if (!MatchesCapturedState(restored, transaction.CapturedState))
                return Failure("VIEWPORT_RESTORE_VERIFICATION_FAILED",
                    "The RimWorld window did not return to the captured style, placement, and client dimensions.",
                    restored,
                    restorationVerified: false);

            return Success(restored, verified: true, restorationVerified: true);
        }
        catch (ViewportControlException exception)
        {
            return Failure(exception.Code, exception.Message, restorationVerified: false);
        }
        catch
        {
            return Failure("VIEWPORT_RESTORE_FAILED",
                "DevBridge could not restore the captured RimWorld window state.",
                restorationVerified: false);
        }
    }

    public ViewportEnvironmentControlResult VerifyPrepared(ViewportEnvironmentTransaction transaction)
    {
        if (transaction?.PreparedState == null)
            return Failure("VIEWPORT_PREPARED_STATE_MISSING",
                "The durable viewport transaction has no verified prepared state.");

        try
        {
            IntPtr window = FindOwnedWindow(transaction.PreparedState.ProcessId,
                transaction.PreparedState.ProcessStartIdentity);
            if (window == IntPtr.Zero || window.ToInt64() != transaction.PreparedState.WindowHandle)
                return Failure("VIEWPORT_PROCESS_OR_WINDOW_CHANGED",
                    "The prepared RimWorld window identity is no longer available.");

            ViewportWindowState current = ReadState(
                transaction.PreparedState.ProcessId,
                transaction.PreparedState.ProcessStartIdentity,
                window);
            if (current == null || current.ClientWidth != transaction.RequestedWidth ||
                current.ClientHeight != transaction.RequestedHeight)
                return Failure("VIEWPORT_EFFECTIVE_DIMENSIONS_MISMATCH",
                    "The prepared viewport is no longer effective at the actual RimWorld client area.",
                    current);

            return Success(current, verified: true);
        }
        catch (ViewportControlException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch
        {
            return Failure("VIEWPORT_VERIFICATION_FAILED",
                "DevBridge could not verify the effective RimWorld viewport.");
        }
    }

    private static IntPtr FindOwnedWindow(int processId, long processStartIdentity)
    {
        using Process process = Process.GetProcessById(processId);
        process.Refresh();
        long actualStart = process.StartTime.ToUniversalTime().Ticks;
        if (actualStart != processStartIdentity)
            throw new ViewportControlException("VIEWPORT_PROCESS_IDENTITY_MISMATCH",
                "The RimWorld process start identity no longer matches the lease-bound generation.");

        IntPtr window = process.MainWindowHandle;
        if (window == IntPtr.Zero)
            return IntPtr.Zero;

        GetWindowThreadProcessId(window, out uint ownerPid);
        return ownerPid == (uint)processId ? window : IntPtr.Zero;
    }

    private static ViewportWindowState ReadState(int processId, long processStartIdentity,
        IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out RECT outer) ||
            !GetClientRect(window, out RECT client))
            throw new ViewportControlException("VIEWPORT_CAPTURE_FAILED",
                "Windows did not return a complete RimWorld window state.");

        WINDOWPLACEMENT placement = new() { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(window, ref placement))
            throw new ViewportControlException("VIEWPORT_CAPTURE_FAILED",
                "Windows did not return the RimWorld window placement.");

        GetWindowThreadProcessId(window, out uint ownerPid);
        if (ownerPid != (uint)processId)
            throw new ViewportControlException("VIEWPORT_PROCESS_IDENTITY_MISMATCH",
                "The target window is no longer owned by the lease-bound RimWorld process.");

        RECT monitorRect = new();
        IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        MONITORINFO monitorInfo = new() { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
            monitorRect = monitorInfo.Monitor;

        return new ViewportWindowState
        {
            ProcessId = processId,
            ProcessStartIdentity = processStartIdentity,
            WindowHandle = window.ToInt64(),
            Style = GetWindowLong(window, GwlStyle),
            ExtendedStyle = GetWindowLong(window, GwlExStyle),
            ShowCommand = placement.ShowCmd,
            PlacementFlags = placement.Flags,
            MinPositionX = placement.MinPosition.X,
            MinPositionY = placement.MinPosition.Y,
            MaxPositionX = placement.MaxPosition.X,
            MaxPositionY = placement.MaxPosition.Y,
            NormalLeft = placement.NormalPosition.Left,
            NormalTop = placement.NormalPosition.Top,
            NormalRight = placement.NormalPosition.Right,
            NormalBottom = placement.NormalPosition.Bottom,
            OuterLeft = outer.Left,
            OuterTop = outer.Top,
            OuterRight = outer.Right,
            OuterBottom = outer.Bottom,
            OuterWidth = outer.Right - outer.Left,
            OuterHeight = outer.Bottom - outer.Top,
            ClientWidth = client.Right - client.Left,
            ClientHeight = client.Bottom - client.Top,
            MonitorLeft = monitorRect.Left,
            MonitorTop = monitorRect.Top,
            MonitorRight = monitorRect.Right,
            MonitorBottom = monitorRect.Bottom
        };
    }

    private static bool MatchesCapturedState(ViewportWindowState current,
        ViewportWindowState captured) => current != null && captured != null &&
        current.WindowHandle == captured.WindowHandle &&
        current.Style == captured.Style &&
        current.ExtendedStyle == captured.ExtendedStyle &&
        current.ShowCommand == captured.ShowCommand &&
        current.PlacementFlags == captured.PlacementFlags &&
        current.MinPositionX == captured.MinPositionX &&
        current.MinPositionY == captured.MinPositionY &&
        current.MaxPositionX == captured.MaxPositionX &&
        current.MaxPositionY == captured.MaxPositionY &&
        current.NormalLeft == captured.NormalLeft && current.NormalTop == captured.NormalTop &&
        current.NormalRight == captured.NormalRight && current.NormalBottom == captured.NormalBottom &&
        current.OuterLeft == captured.OuterLeft && current.OuterTop == captured.OuterTop &&
        current.OuterWidth == captured.OuterWidth && current.OuterHeight == captured.OuterHeight &&
        current.ClientWidth == captured.ClientWidth && current.ClientHeight == captured.ClientHeight;

    private static long GetWindowLong(IntPtr window, int index)
    {
        IntPtr value = IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));
        return value.ToInt64();
    }

    private static bool SetWindowLong(IntPtr window, int index, long value)
    {
        SetLastError(0);
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(window, index, new IntPtr(value));
        else
            SetWindowLong32(window, index, new IntPtr(value).ToInt32());
        return Marshal.GetLastWin32Error() == 0;
    }

    private static ViewportEnvironmentControlResult Success(ViewportWindowState state,
        bool verified, bool restorationVerified = false) => new()
    {
        Success = true,
        State = state,
        Verified = verified,
        RestorationVerified = restorationVerified,
        CaptureMethod = ViewportEnvironmentSchemas.CaptureMethod
    };

    private static ViewportEnvironmentControlResult Failure(string code, string error,
        ViewportWindowState state = null, bool restorationVerified = false) => new()
    {
        Success = false,
        ErrorCode = code,
        Error = error,
        State = state,
        RestorationVerified = restorationVerified,
        CaptureMethod = ViewportEnvironmentSchemas.CaptureMethod
    };

    private sealed class ViewportControlException : Exception
    {
        internal ViewportControlException(string code, string message) : base(message)
        {
            Code = code;
        }

        internal string Code { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public POINT MinPosition;
        public POINT MaxPosition;
        public RECT NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRectEx(ref RECT lpRect, uint dwStyle,
        bool bMenu, uint dwExStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint dwErrCode);
}
