using System.Text.Json;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestViewportTransactionCycle()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        FakeViewportEnvironmentController controller = new();
        fixture.ViewportEnvironmentController = controller;
        fixture.State = fixture.Reload();

        JsonCommandResponse begin = Execute(fixture,
            Request("environment", "holder", 77, "viewport", "begin", "T001", "narrow"));
        Assert(begin.Success && begin.Viewport != null &&
               begin.Viewport.Status == "prepared" &&
               !string.IsNullOrWhiteSpace(begin.Viewport.TransactionId),
            "viewport begin must return a prepared transaction");
        Assert(controller.CaptureCalls == 1 && controller.ApplyCalls == 1,
            "viewport begin must capture once and apply once");
        Assert(Persisted(fixture).ViewportEnvironment != null &&
               Persisted(fixture).ViewportEnvironment.Prepared,
            "prepared viewport state must be durable");

        JsonCommandResponse restore = Execute(fixture,
            Request("environment", "holder", 77, "viewport", "restore", "T001",
                begin.Viewport.TransactionId));
        Assert(restore.Success && restore.Viewport != null &&
               restore.Viewport.Status == "restored" &&
               restore.Viewport.RestorationVerified,
            "viewport restore must verify the captured state");
        Assert(Persisted(fixture).ViewportEnvironment.Restored &&
               Persisted(fixture).ViewportEnvironment.RestorationVerified,
            "restoration must be durable");

        JsonCommandResponse duplicate = Execute(fixture,
            Request("environment", "holder", 77, "viewport", "restore", "T001",
                begin.Viewport.TransactionId));
        Assert(duplicate.Success && duplicate.Viewport.Status == "alreadyRestored",
            "restore must be idempotent");
    }

    private static void TestViewportEffectiveDimensions()
    {
        using Fixture fixture = Fixture.ReadyWithLease();
        FakeViewportEnvironmentController controller = new();
        string before = File.ReadAllText(Path.Combine(fixture.Root, "ModsConfig.xml"));
        fixture.ViewportEnvironmentController = controller;
        fixture.State = fixture.Reload();

        JsonCommandResponse begin = Execute(fixture,
            Request("environment", "holder", 77, "viewport", "begin", "T001",
                "explicit", "1280", "720"));
        Assert(begin.Success && begin.Viewport?.EffectiveViewport != null,
            "explicit viewport begin must return effective evidence");
        Assert(begin.Viewport.EffectiveViewport.ClientWidth == 1280 &&
               begin.Viewport.EffectiveViewport.ClientHeight == 720,
            "effective viewport evidence must report the actual client dimensions");
        Assert(!begin.Viewport.PersistentPreferenceMutation &&
               !Persisted(fixture).ViewportEnvironment.PersistentPreferenceMutation,
            "viewport control must not mutate persistent preferences");

        Execute(fixture,
            Request("environment", "holder", 77, "viewport", "restore", "T001",
                begin.Viewport.TransactionId));
        Assert(string.Equals(before,
                File.ReadAllText(Path.Combine(fixture.Root, "ModsConfig.xml")),
                StringComparison.Ordinal),
            "viewport control must not mutate ModsConfig");
    }

    private static void TestViewportFailureModes()
    {
        using (Fixture unsupported = Fixture.ReadyWithLease())
        {
            FakeViewportEnvironmentController controller = new()
            {
                CaptureFailureCode = "VIEWPORT_UNSUPPORTED_RUNTIME",
                CaptureFailure = "The test runtime has no supported window controller."
            };
            unsupported.ViewportEnvironmentController = controller;
            unsupported.State = unsupported.Reload();
            JsonCommandResponse response = Execute(unsupported,
                Request("environment", "holder", 77, "viewport", "begin", "T001", "narrow"));
            Assert(!response.Success && response.Viewport?.ErrorCode ==
                   "VIEWPORT_UNSUPPORTED_RUNTIME",
                "unsupported viewport control must fail with a structured code");
        }

        using (Fixture unavailable = Fixture.ReadyWithoutLease())
        {
            FakeViewportEnvironmentController controller = new();
            unavailable.ViewportEnvironmentController = controller;
            unavailable.State = unavailable.Reload();
            JsonCommandResponse response = Execute(unavailable,
                Request("environment", "holder", 77, "viewport", "begin", "T001", "wide"));
            Assert(!response.Success && response.Viewport?.ErrorCode ==
                   "VIEWPORT_LEASE_REQUIRED",
                "viewport control without a canonical lease must fail closed");
        }
    }

    private static void TestViewportConcurrencyAndExpiry()
    {
        using Fixture fixture = Fixture.ReadyWithLeases();
        FakeViewportEnvironmentController controller = new();
        fixture.ViewportEnvironmentController = controller;
        fixture.State = fixture.Reload();

        JsonCommandResponse first = Execute(fixture,
            Request("environment", "holder-a", 77, "viewport", "begin", "T001", "wide"));
        Assert(first.Success, "the first lease must acquire the viewport transaction");

        JsonCommandResponse competing = Execute(fixture,
            Request("environment", "holder-b", 78, "viewport", "begin", "T002", "narrow"));
        Assert(!competing.Success && competing.Viewport?.ErrorCode ==
               "VIEWPORT_ENVIRONMENT_BUSY",
            "a second lease must not resize the same RimWorld instance");

        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        Execute(fixture, Request("status", "observer", 90));
        Assert(controller.RestoreCalls > 0 && Persisted(fixture).ViewportEnvironment.Restored,
            "lease expiry must attempt and record viewport restoration");
    }

    private static PersistedState Persisted(Fixture fixture) =>
        JsonSerializer.Deserialize<PersistedState>(
            File.ReadAllText(Path.Combine(fixture.Root, "Runtime", "state.json")),
            Program.JsonOptions) ?? new PersistedState();

    private sealed class FakeViewportEnvironmentController : IViewportEnvironmentController
    {
        internal string CaptureFailureCode { get; init; }
        internal string CaptureFailure { get; init; }
        internal bool FailApply { get; init; }
        internal bool FailRestore { get; init; }
        internal int CaptureCalls { get; private set; }
        internal int ApplyCalls { get; private set; }
        internal int RestoreCalls { get; private set; }

        public ViewportEnvironmentControlResult Capture(int processId, long processStartIdentity)
        {
            CaptureCalls++;
            if (CaptureFailureCode != null)
                return Failure(CaptureFailureCode, CaptureFailure);
            return Success(State(processId, processStartIdentity, 1280, 720), true);
        }

        public ViewportEnvironmentControlResult Apply(
            ViewportEnvironmentRequest request,
            ViewportEnvironmentTransaction transaction)
        {
            ApplyCalls++;
            if (FailApply)
                return Failure("VIEWPORT_EFFECTIVE_DIMENSIONS_MISMATCH",
                    "The fake runtime rejected the requested client area.");
            int width = request.IsCurrent ? transaction.CapturedState.ClientWidth : request.Width;
            int height = request.IsCurrent ? transaction.CapturedState.ClientHeight : request.Height;
            return Success(State(transaction.CapturedState.ProcessId,
                transaction.CapturedState.ProcessStartIdentity, width, height), true);
        }

        public ViewportEnvironmentControlResult Restore(
            ViewportEnvironmentTransaction transaction)
        {
            RestoreCalls++;
            if (FailRestore)
                return Failure("VIEWPORT_RESTORE_FAILED",
                    "The fake runtime refused to restore the captured window.");
            return new ViewportEnvironmentControlResult
            {
                Success = true,
                State = Clone(transaction.CapturedState),
                Verified = true,
                RestorationVerified = true
            };
        }

        public ViewportEnvironmentControlResult VerifyPrepared(
            ViewportEnvironmentTransaction transaction) =>
            Success(Clone(transaction.PreparedState), true);

        private static ViewportWindowState State(int processId, long processStartIdentity,
            int width, int height) => new()
        {
            ProcessId = processId,
            ProcessStartIdentity = processStartIdentity,
            WindowHandle = 7001,
            Style = 0x00CF0000,
            ExtendedStyle = 0,
            ShowCommand = 1,
            PlacementFlags = 0,
            NormalLeft = 10,
            NormalTop = 20,
            NormalRight = 1290,
            NormalBottom = 760,
            OuterLeft = 10,
            OuterTop = 20,
            OuterRight = width + 26,
            OuterBottom = height + 46,
            OuterWidth = width + 16,
            OuterHeight = height + 26,
            ClientWidth = width,
            ClientHeight = height,
            CaptureMethod = ViewportEnvironmentSchemas.CaptureMethod
        };

        private static ViewportWindowState Clone(ViewportWindowState source) => source == null
            ? null
            : new ViewportWindowState
            {
                ProcessId = source.ProcessId,
                ProcessStartIdentity = source.ProcessStartIdentity,
                WindowHandle = source.WindowHandle,
                Style = source.Style,
                ExtendedStyle = source.ExtendedStyle,
                ShowCommand = source.ShowCommand,
                PlacementFlags = source.PlacementFlags,
                NormalLeft = source.NormalLeft,
                NormalTop = source.NormalTop,
                NormalRight = source.NormalRight,
                NormalBottom = source.NormalBottom,
                OuterLeft = source.OuterLeft,
                OuterTop = source.OuterTop,
                OuterRight = source.OuterRight,
                OuterBottom = source.OuterBottom,
                OuterWidth = source.OuterWidth,
                OuterHeight = source.OuterHeight,
                ClientWidth = source.ClientWidth,
                ClientHeight = source.ClientHeight,
                MonitorLeft = source.MonitorLeft,
                MonitorTop = source.MonitorTop,
                MonitorRight = source.MonitorRight,
                MonitorBottom = source.MonitorBottom,
                CaptureMethod = source.CaptureMethod
            };

        private static ViewportEnvironmentControlResult Success(
            ViewportWindowState state, bool verified) => new()
        {
            Success = true,
            State = state,
            Verified = verified,
            CaptureMethod = ViewportEnvironmentSchemas.CaptureMethod
        };

        private static ViewportEnvironmentControlResult Failure(
            string code, string error) => new()
        {
            Success = false,
            ErrorCode = code,
            Error = error,
            CaptureMethod = ViewportEnvironmentSchemas.CaptureMethod
        };
    }
}
