namespace DevBridge.Coordinator;

// Test-only seams. Production leaves CoordinatorOptions.FaultInjector null,
// so these hooks are inert in published builds and do not change lifecycle
// semantics.
internal enum CoordinatorFaultPoint
{
    BeforeDurableStateWrite,
    AfterStateTempFileWriteBeforeAtomicReplacement,
    AfterStateDurableReplacement,
    AfterStatePersistedBeforeExternalProcessAction,
    AfterProcessActionBeforeResultingStatePersistence,
    AfterStoppedPersistenceBeforeIpcTerminalResult,
    AfterIpcResultWriteBeforeConnectionTeardown,
    DuringHistoryManifestPersistence,
    AfterHistoryTempFileWriteBeforeAtomicReplacement,
    AfterHistoryDurableReplacement,
    DuringModsConfigTransition,
    DuringProjectAggregateFreeze,
    DuringCrashIsolationAttemptPersistence,
    DuringGracefulCoordinatorShutdown
}

internal interface ICoordinatorFaultInjector
{
    void Hit(CoordinatorFaultPoint point);
}

internal sealed class CoordinatorFaultInjectedException : Exception
{
    internal CoordinatorFaultInjectedException(CoordinatorFaultPoint point)
        : base("simulated coordinator failure at " + point)
    {
        Point = point;
    }

    internal CoordinatorFaultPoint Point { get; }
}
