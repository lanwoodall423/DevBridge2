using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevBridge2;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static void TestFaultInjectionDurableStateBoundaries()
    {
        CoordinatorFaultPoint[] points =
        {
            CoordinatorFaultPoint.BeforeDurableStateWrite,
            CoordinatorFaultPoint.AfterStateTempFileWriteBeforeAtomicReplacement,
            CoordinatorFaultPoint.AfterStateDurableReplacement
        };

        foreach (CoordinatorFaultPoint point in points)
        {
            using Fixture fixture = Fixture.ReadyWithLease();
            FaultPlan plan = new(point);
            fixture.FaultInjector = plan;
            fixture.State = fixture.Reload();

            bool injected = false;
            try
            {
                fixture.State.Execute(Request("test", "holder", 77, "renew", "T001"),
                    _ => { }, () => true);
            }
            catch (CoordinatorFaultInjectedException exception)
            {
                injected = exception.Point == point;
            }

            fixture.State.WaitForWorkersForTesting(TimeSpan.FromMilliseconds(250));
            Assert(injected && plan.Hits.Contains(point),
                "state fault point was not observed: " + point);
            RecoverFixture(fixture);
            PersistedState recovered = ReadPersistedState(fixture.Root);
            Assert(recovered.Phase == BridgePhase.READY && recovered.ProcessId == 101 &&
                recovered.ProcessStartUtcTicks == 1001,
                "state-write fault recovery lost the previously accepted process: " + point);
            Assert(fixture.Adapter.LaunchCalls == 0 && fixture.Adapter.TerminationRequests == 0,
                "state-write fault recovery performed an unsafe process action: " + point);
        }
    }

    private static void TestFaultInjectionLaunchBoundaries()
    {
        CoordinatorFaultPoint[] points =
        {
            CoordinatorFaultPoint.AfterStatePersistedBeforeExternalProcessAction,
            CoordinatorFaultPoint.AfterProcessActionBeforeResultingStatePersistence
        };

        foreach (CoordinatorFaultPoint point in points)
        {
            using Fixture fixture = StoppedFixture();
            fixture.Adapter.ReadyOnLaunch = false;
            FaultPlan plan = new(point);
            fixture.FaultInjector = plan;
            fixture.State = fixture.Reload();
            try
            {
                fixture.State.Execute(Request("restart", "holder", 77), _ => { }, () => true);
            }
            catch (CoordinatorFaultInjectedException)
            {
            }

            fixture.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
            Assert(plan.Hits.Contains(point), "launch fault point was not observed: " + point);
            int launchesBeforeRecovery = fixture.Adapter.LaunchCalls;
            fixture.FaultInjector = null;
            fixture.State = fixture.Reload();
            fixture.State.StartRecoveryWork();
            fixture.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
            PersistedState recovered = ReadPersistedState(fixture.Root);
            Assert(fixture.Adapter.LaunchCalls == launchesBeforeRecovery,
                "recovery duplicated an uncertain launch at " + point);
            Assert(!(recovered.Phase == BridgePhase.READY && recovered.ProcessId <= 0),
                "uncertain launch was incorrectly promoted to READY at " + point);
            AssertAtMostOneProcess(fixture, point.ToString());
        }
    }

    private static void TestFaultInjectionEnsureReadyBoundary()
    {
        CoordinatorFaultPoint[] points =
        {
            CoordinatorFaultPoint.AfterStatePersistedBeforeExternalProcessAction,
            CoordinatorFaultPoint.AfterProcessActionBeforeResultingStatePersistence
        };

        foreach (CoordinatorFaultPoint point in points)
        {
            using Fixture fixture = Fixture.MaintenanceWithLease();
            fixture.Adapter.ReadyOnLaunch = false;
            FaultPlan plan = new(point);
            fixture.FaultInjector = plan;
            fixture.State = fixture.Reload();
            try
            {
                fixture.State.Execute(Request("ensure-ready", "holder", 77, "T001"), _ => { }, () => true);
            }
            catch (CoordinatorFaultInjectedException)
            {
            }

            fixture.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
            Assert(plan.Hits.Contains(point), "ensure-ready fault point was not observed: " + point);
            int launchesBeforeRecovery = fixture.Adapter.LaunchCalls;
            RecoverFixture(fixture);
            PersistedState recovered = ReadPersistedState(fixture.Root);
            Assert(fixture.Adapter.LaunchCalls <= launchesBeforeRecovery + 1 &&
                   !(recovered.Phase == BridgePhase.READY && recovered.ProcessId <= 0),
                "ensure-ready recovery made an unsafe or duplicate launch decision at " + point);
            AssertAtMostOneProcess(fixture, "ensure-ready recovery " + point);
        }
    }

    private static void TestFaultInjectionIpcAndShutdownBoundaries()
    {
        using (Fixture stopped = Fixture.ReadyWithLease())
        {
            FaultPlan plan = new(CoordinatorFaultPoint.AfterStoppedPersistenceBeforeIpcTerminalResult);
            stopped.FaultInjector = plan;
            using CoordinatorHarness harness = CoordinatorHarness.Start(stopped);
            List<CoordinatorIpcFrame> frames = SendRawProtocolRequest(harness,
                NewProtocolRequest("stop", "T001"));
            Assert(plan.Hits.Contains(CoordinatorFaultPoint.AfterStoppedPersistenceBeforeIpcTerminalResult),
                "STOPPED-before-result fault point was not observed");
            Assert(frames.Count(value => value.Type == CoordinatorIpcProtocol.ResultType) == 0,
                "a simulated coordinator death after STOPPED persistence manufactured a result");
            Assert(ReadPersistedState(stopped.Root).Phase == BridgePhase.STOPPED &&
                ReadPersistedState(stopped.Root).MaintenanceReady,
                "STOPPED persistence was not durable before the simulated IPC death");
            stopped.FaultInjector = null;
            harness.Send("coordinator", "shutdown");
        }

        using (Fixture resultWritten = Fixture.ReadyWithoutLease())
        {
            FaultPlan plan = new(CoordinatorFaultPoint.AfterIpcResultWriteBeforeConnectionTeardown);
            resultWritten.FaultInjector = plan;
            using CoordinatorHarness harness = CoordinatorHarness.Start(resultWritten);
            List<CoordinatorIpcFrame> frames = SendRawProtocolRequest(harness,
                NewProtocolRequest("status"));
            Assert(plan.WaitFor(CoordinatorFaultPoint.AfterIpcResultWriteBeforeConnectionTeardown),
                "post-result fault point was not observed; hits=" + string.Join(",", plan.Hits));
            Assert(frames.Count(value => value.Type == CoordinatorIpcProtocol.ResultType) == 1,
                "a result written before connection teardown was not preserved");
            resultWritten.FaultInjector = null;
            harness.Send("coordinator", "shutdown");
        }

            using (Fixture shutdown = Fixture.ReadyWithLease())
        {
            FaultPlan plan = new(CoordinatorFaultPoint.DuringGracefulCoordinatorShutdown);
            shutdown.FaultInjector = plan;
            using CoordinatorHarness harness = CoordinatorHarness.Start(shutdown);
            try
            {
                SendRawProtocolRequest(harness, NewProtocolRequest("coordinator", "shutdown"));
            }
            catch (Exception)
            {
                // The injected death occurs while the server is draining. The
                // client may observe either the already-written result or the
                // bounded disconnect; the durable shutdown boundary is what is
                // under test.
            }
            Assert(plan.WaitFor(CoordinatorFaultPoint.DuringGracefulCoordinatorShutdown),
                "graceful-shutdown fault point was not observed; hits=" +
                string.Join(",", plan.Hits));
            Assert(SpinWait.SpinUntil(() => harness.ServerTask.IsCompleted, TimeSpan.FromSeconds(20)),
                "graceful-shutdown server task did not finish after the injected failure");
            try
            {
                harness.ServerTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                AggregateException aggregate = exception as AggregateException;
                IEnumerable<Exception> causes = aggregate != null
                    ? aggregate.Flatten().InnerExceptions
                    : new[] { exception };
            Assert(causes.Any(value =>
                        value is CoordinatorFaultInjectedException injected &&
                        injected.Point == CoordinatorFaultPoint.DuringGracefulCoordinatorShutdown),
                    "graceful-shutdown terminated for an unexpected reason: " + exception.Message);
            }
            Assert(shutdown.Adapter.TerminationRequests == 0,
                "coordinator shutdown fault incorrectly terminated RimWorld");
            harness.SkipShutdownOnDispose = true;
        }
    }

    private static void TestFaultInjectionArtifactAndRecoveryBoundaries()
    {
        using (Fixture history = Fixture.ReadyWithLease())
        {
            FaultPlan plan = new(CoordinatorFaultPoint.DuringHistoryManifestPersistence);
            history.FaultInjector = plan;
            history.State = history.Reload();
            try
            {
                history.State.Execute(Request("stop", "holder", 77, "T001"), _ => { }, () => true);
            }
            catch (CoordinatorFaultInjectedException)
            {
            }
            history.State.WaitForWorkersForTesting(TimeSpan.FromMilliseconds(250));
            Assert(plan.Hits.Contains(CoordinatorFaultPoint.DuringHistoryManifestPersistence),
                "history/manifest fault point was not observed");
            RecoverFixture(history);
            AssertAtMostOneProcess(history, "history recovery");
            Assert(ReadPersistedState(history.Root).Phase != BridgePhase.READY ||
                ReadPersistedState(history.Root).ProcessId > 0,
                "history fault recovery promoted an unverified process");
        }

        using (Fixture baseline = StoppedFixture())
        {
            FaultPlan plan = new(CoordinatorFaultPoint.DuringModsConfigTransition);
            baseline.FaultInjector = plan;
            baseline.State = baseline.Reload();
            try
            {
                baseline.State.Execute(Request("mods", "holder", 77, "capture-baseline"),
                    _ => { }, () => true);
            }
            catch (CoordinatorFaultInjectedException)
            {
            }
            Assert(plan.Hits.Contains(CoordinatorFaultPoint.DuringModsConfigTransition),
                "ModsConfig transition fault point was not observed for baseline capture");
            RecoverFixture(baseline);
            PersistedState recovered = ReadPersistedState(baseline.Root);
            Assert(recovered.ModsConfigMutationAuthority != ModsConfigMutationAuthorityValues.DevBridgeTransition,
                "baseline transition remained in an unresolved pending state after recovery");
            AssertAtMostOneProcess(baseline, "baseline recovery");
        }

        using (ProfileSetup setup = ProfileSetup.Create())
        {
            Assert(setup.CaptureBaseline(), "baseline-restore fault setup failed");
            setup.Fixture.Adapter.ReadyOnLaunch = true;
            Assert(setup.Fixture.State.Execute(Request("restart", "holder", 77,
                    "--projects", "frontier"), _ => { }, () => true) == 0,
                "baseline-restore fault setup could not create a generated profile");
            setup.Fixture.Adapter.Current.ForceTerminate();
            FaultPlan plan = new(CoordinatorFaultPoint.DuringModsConfigTransition);
            setup.Fixture.FaultInjector = plan;
            setup.Fixture.State = setup.Fixture.Reload();
            List<string> restoreOutput = new();
            int restoreExit = 0;
            try
            {
                restoreExit = setup.Fixture.State.Execute(Request("mods", "holder", 77, "restore-baseline"),
                    restoreOutput.Add, () => true);
            }
            catch (CoordinatorFaultInjectedException)
            {
            }
            Assert(plan.Hits.Contains(CoordinatorFaultPoint.DuringModsConfigTransition),
                "ModsConfig transition fault point was not observed for baseline restore; exit=" +
                restoreExit + ", output=" + string.Join(" | ", restoreOutput));
            RecoverFixture(setup.Fixture);
            AssertAtMostOneProcess(setup.Fixture, "baseline restore recovery");
        }

        using (ProfileSetup setup = ProfileSetup.Create())
        {
            Fixture freeze = setup.Fixture;
            freeze.Adapter.ReadyOnLaunch = true;
            Assert(freeze.State.Execute(Request("project", "holder", 77, "register", "frontier"),
                _ => { }, () => true) == 0, "project registration setup failed");
            FaultPlan plan = new(CoordinatorFaultPoint.DuringProjectAggregateFreeze);
            freeze.FaultInjector = plan;
            freeze.State = freeze.Reload();
            try
            {
                freeze.State.Execute(Request("restart", "holder", 77), _ => { }, () => true);
            }
            catch (CoordinatorFaultInjectedException)
            {
            }
            freeze.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
            Assert(plan.Hits.Contains(CoordinatorFaultPoint.DuringProjectAggregateFreeze),
                "project aggregate-freeze fault point was not observed");
            int launchesBeforeRecovery = freeze.Adapter.LaunchCalls;
            RecoverFixture(freeze, startRecovery: true);
            freeze.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
            Assert(freeze.Adapter.LaunchCalls <= launchesBeforeRecovery + 1,
                "project freeze recovery launched more than one replacement");
            AssertAtMostOneProcess(freeze, "project freeze recovery");
        }

        using (ProfileSetup setup = ProfileSetup.Create())
        {
            Fixture generated = setup.Fixture;
            generated.Adapter.ReadyOnLaunch = false;
            Assert(generated.State.Execute(Request("project", "holder", 77, "register", "frontier"),
                _ => { }, () => true) == 0, "generated-profile setup failed");
            FaultPlan plan = new(CoordinatorFaultPoint.DuringModsConfigTransition);
            generated.FaultInjector = plan;
            generated.State = generated.Reload();
            try
            {
                generated.State.Execute(Request("restart", "holder", 77), _ => { }, () => true);
            }
            catch (CoordinatorFaultInjectedException)
            {
            }
            generated.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
            Assert(plan.Hits.Contains(CoordinatorFaultPoint.DuringModsConfigTransition),
                "generated ModsConfig install fault point was not observed");
            RecoverFixture(generated);
            AssertAtMostOneProcess(generated, "generated ModsConfig recovery");
        }

        using (ProfileSetup setup = ProfileSetup.Create())
        {
            Fixture isolation = setup.Fixture;
            isolation.Adapter.ThrowOnLaunch = false;
            isolation.Adapter.ReadyOnLaunch = true;
            Assert(setup.CaptureBaseline(), "crash-isolation setup baseline capture failed");
            Assert(isolation.State.Execute(Request("project", "holder", 77, "register", "frontier"),
                _ => { }, () => true) == 0, "crash-isolation setup failed");
            isolation.Adapter.ReadyOnLaunch = false;
            FaultPlan plan = new(CoordinatorFaultPoint.DuringCrashIsolationAttemptPersistence);
            isolation.FaultInjector = plan;
            isolation.State = isolation.Reload();
            try
            {
                isolation.State.Execute(Request("restart", "holder", 77), _ => { }, () => true);
            }
            catch (CoordinatorFaultInjectedException)
            {
            }
            isolation.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
            Assert(plan.WaitFor(CoordinatorFaultPoint.DuringCrashIsolationAttemptPersistence),
                "crash-isolation persistence fault point was not observed");
            isolation.Adapter.ReadyOnLaunch = true;
            RecoverFixture(isolation, startRecovery: true);
            isolation.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
            AssertAtMostOneProcess(isolation, "crash-isolation recovery");
        }

    }

    private static Fixture StoppedFixture() => new(new PersistedState
    {
        Generation = 0,
        Phase = BridgePhase.STOPPED,
        ProcessId = 0,
        ProcessStartUtcTicks = 0
    });

    private static void RecoverFixture(Fixture fixture, bool startRecovery = true)
    {
        fixture.FaultInjector = null;
        fixture.State = fixture.Reload();
        if (startRecovery)
        {
            fixture.State.StartRecoveryWork();
            fixture.State.WaitForWorkersForTesting(TimeSpan.FromSeconds(2));
        }
    }

    private static void AssertAtMostOneProcess(Fixture fixture, string context)
    {
        ProcessEnumeration enumeration = fixture.Adapter.EnumerateRimWorld(fixture.RimWorldPath);
        Assert(enumeration.Complete && enumeration.Processes.Count(value => !value.HasExited) <= 1,
            context + " observed more than one live RimWorld process or an incomplete census");
    }

    private sealed class FaultPlan : ICoordinatorFaultInjector
    {
        private readonly HashSet<CoordinatorFaultPoint> remaining;
        private readonly object gate = new();
        internal readonly ConcurrentQueue<CoordinatorFaultPoint> Hits = new();

        internal FaultPlan(params CoordinatorFaultPoint[] points) =>
            remaining = new HashSet<CoordinatorFaultPoint>(points ?? Array.Empty<CoordinatorFaultPoint>());

        public void Hit(CoordinatorFaultPoint point)
        {
            bool inject;
            lock (gate)
            {
                Hits.Enqueue(point);
                inject = remaining.Remove(point);
            }
            hitSignals.GetOrAdd(point, _ => new ManualResetEventSlim()).Set();
            if (inject)
                throw new CoordinatorFaultInjectedException(point);
        }

        private readonly ConcurrentDictionary<CoordinatorFaultPoint, ManualResetEventSlim> hitSignals = new();

        internal bool WaitFor(CoordinatorFaultPoint point)
        {
            if (!SpinWait.SpinUntil(() => hitSignals.ContainsKey(point), TimeSpan.FromSeconds(2)))
                return false;
            return hitSignals[point].Wait(TimeSpan.FromSeconds(2));
        }
    }
}
