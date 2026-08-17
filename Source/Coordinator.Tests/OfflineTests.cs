using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DevBridge2;
using DevBridge2.BridgeTools;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static readonly DateTime ClockStart = new(2026, 8, 11, 16, 0, 0, DateTimeKind.Utc);
    private static int failures;

    private static int Main()
    {
        Run("readiness timeout retains process and accepts late same-process readiness", TestReadinessTimeoutContract);
        Run("stop authorization and identity checks", TestStopAuthorization);
        Run("stop requires confirmed exit and fails closed", TestStopFailsClosed);
        Run("stop succeeds, retains lease, marks dirty, and makes no launches", TestSuccessfulStop);
        Run("maintenance state has no background launch and expiry leaves it stopped", TestMaintenanceNoLaunch);
        Run("expired maintenance lease can be reacquired without launching", TestMaintenanceLeaseReacquisition);
        Run("failed process can be leased and stopped without relaunching", TestFailedProcessRecoveryStop);
        Run("other test holders queue during maintenance", TestMaintenanceQueue);
        Run("stop serializes ensure-ready and restart", TestStopSerialization);
        Run("ensure-ready launches exactly once after maintenance", TestEnsureReadyLaunch);
        Run("restart retains immediate launch behavior", TestImmediateRestart);
        Run("launch monitoring retries transient process inspection failures", TestLaunchMonitoringRetriesTransientInspection);
        Run("matching late readiness repairs process inspection quarantine", TestInspectionQuarantineAcceptsMatchingReadiness);
        Run("duplicate stop is idempotent", TestDuplicateStop);
        Run("process inspection uncertainty fails closed", TestInspectionFailsClosed);
        Run("doctor clears a stale inspection quarantine after a zero-process census", TestDoctorRecoversInspectionQuarantine);
        Run("doctor keeps inspection quarantine when the census is not conclusively empty", TestDoctorRecoveryFailsClosed);
        Run("maintenance claims are freshly re-enumerated", TestMaintenanceRevalidation);
        Run("uncertain maintenance operations make no adapter calls", TestMaintenanceInspectionNoLaunch);
        Run("status uses one authoritative process snapshot", TestStatusSnapshotConsistency);
        Run("duplicate launch requests have one slot owner", TestDuplicateLaunchOwnership);
        Run("fifty duplicate restart requests have one launch", TestDuplicateRestartOwnership);
        Run("competing restart owners cannot overwrite provenance", TestCompetingRestartOwners);
        Run("lease-blocked restart waits durably and resumes", TestDurableLeaseWait);
        Run("connected lease sessions heartbeat active tests", TestConnectedLeaseSession);
        Run("stopped lease sessions expire without an orphan heartbeat", TestStoppedLeaseSessionExpires);
        Run("lease heartbeats and stable-agent authorization", TestLeaseHeartbeatAndAuthorization);
        Run("profile-error maintenance windows reacquire a lease", TestMaintenanceLeaseReacquisitionAfterProfileError);
        Run("orphaned leases expire without blocking a restart", TestOrphanLeaseExpiry);
        Run("shared leases block restart until the final lease ends", TestMultipleSharedLeases);
        Run("lease JSON reports exact expiration and retry timing", TestLeaseJsonTiming);
        Run("missing process relaunches despite an active lease", TestMissingProcessRelaunchWithLease);
        Run("legacy lease-wait expiry recovers automatically", TestLegacyLeaseWaitRecovery);
        Run("recovery launch budget is finite", TestFiniteRecovery);
        Run("crash recovery never duplicates an ambiguous launch", TestCrashRecoveryNoDuplicateLaunch);
        Run("root and runtime slot bindings are authoritative", TestRuntimeScopeBinding);
        Run("ticket routing preserves its durable slot", TestTicketRouting);
        Run("goal wake and MCP scope metadata is preserved", TestScopeMetadata);
        Run("quicktest activation is ordered and bounded", TestQuicktestActivation);
        Run("quicktest request only records pending intent", TestQuicktestRequestRegistration);
        Run("quicktest pre-menu readiness cannot activate", TestQuicktestPreMainMenu);
        Run("quicktest activation uses one UI-thread boundary", TestQuicktestUiThreadBoundary);
        Run("quicktest duplicate ticks produce one activation", TestQuicktestSingleActivation);
        Run("quicktest callback preserves built-in order", TestQuicktestCallbackOrder);
        Run("quicktest old lifecycle failure is prevented", TestQuicktestLifecycleGuard);
        Run("quicktest activation failure clears pending state", TestQuicktestActivationFailure);
        Run("quicktest callback bursts cannot consume the elapsed-time wait", TestQuicktestCallbackBurst);
        Run("quicktest readiness expiry is terminal", TestQuicktestReadinessExpiry);
        Run("quicktest source boundary and lifecycle predicates are structural", TestQuicktestStructuralBoundary);
        Run("quicktest path has no fallback activation mechanism", TestQuicktestNoFallback);
        Run("quicktest failure artifact is bounded and atomically replaced", TestQuicktestFailureArtifactContract);
        Run("matching quicktest failure enters isolation before timeout", TestQuicktestFailureIsolation);
        Run("mismatched or malformed quicktest failure is quarantined", TestQuicktestFailureRejectsInvalidRecords);
        Run("quicktest failure/readiness conflict fails closed", TestQuicktestFailureReadinessConflict);
        Run("coordinator-root argument forms are accepted", TestCoordinatorRootArgumentForms);
        Run("plain restart launches the aggregate minimal control profile", TestPlainRestartUsesAggregateControlProfile);
        Run("project intent registration deduplicates and freezes all requesters", TestProjectIntentAggregationAndFreeze);
        Run("project resolve is pure and matches the accepted closure", TestProjectResolveIsPureAndMatchesAcceptedClosure);
        Run("project resolve is deterministic and compares pinned generations", TestProjectResolveIsDeterministicAndComparesPinnedGeneration);
        Run("project resolve failures are machine-readable and mutation-free", TestProjectResolveFailuresAreMachineReadableAndMutationFree);
        Run("invalid future configuration preserves current generation management", TestFutureConfigurationInvalidPreservesCurrentGeneration);
        Run("typed project resolve inputs are normalized and deterministic", TestTypedProjectResolveInputs);
        Run("typed project resolve inputs fail with machine-readable codes", TestTypedProjectResolveInputFailures);
        Run("typed inputs bind to immutable generation history and conflicts", TestTypedInputsBindToGenerationHistoryAndStayFrozen);
        Run("unknown typed inputs fail before mutation", TestTypedInputFailuresAreMutationFree);
        Run("aggregate-first guidance permits registration during another test", TestAggregateFirstGuidanceDuringActiveTest);
        Run("concurrent project intent registration aggregates deterministically", TestConcurrentProjectIntentAggregation);
        Run("late project intent is queued and test begin is denied", TestLateProjectIntentQueuesAndDeniesTest);
        Run("release and expiry affect only future aggregate generations", TestProjectIntentReleaseAndExpiry);
        Run("legacy production is explicit and fail-closed with active intent", TestLegacyProductionSafety);
        Run("RimBridge profile modes are deterministic and versioned", TestRimBridgeProfileModes);
        Run("RimBridge log discovery is launch-bound and rejects malformed segments", TestRimBridgeLogDiscovery);
        Run("RimBridge required readiness times out without replacement launches", TestRimBridgeRequiredReadinessTimeout);
        Run("RimBridge optional profile failure remains nonblocking and visible", TestRimBridgeOptionalFailureNonblocking);
        Run("RimBridge ordinary status never leaks the bridge token", TestRimBridgeTokenIsNotInStatus);
        Run("RimBridge endpoint identity invalidation removes stale credentials", TestRimBridgeEndpointIdentityInvalidation);
        Run("DevBridge generation context is serialized without secrets", TestDevBridgeGenerationContext);
        Run("authorized profile writes retain frozen ModsConfig ownership", TestAuthorizedModsConfigOwnership);
        Run("external ModsConfig mutation is detected with durable evidence", TestExternalModsConfigMutation);
        Run("external mutation fails closed without a restart loop", TestExternalMutationNoRestartLoop);
        Run("control policy companion is read-only and machine-readable", TestControlPolicyCompanion);
        Run("RimBridge companion identity mismatches fail closed", TestRimBridgeCompanionIdentityValidation);
        Run("RimBridge companion absence is endpoint-only and nonfatal", TestRimBridgeCompanionUnavailable);
        Run("RimBridge routes read-only calls with identity and provenance", TestRimBridgeRouteForwarding);
        Run("RimBridge route blocks persistent and lifecycle mutations", TestRimBridgeRoutePolicyBlocks);
        Run("RimBridge route rejects stale generation and process identity", TestRimBridgeRouteIdentitySafety);
        Run("RimBridge route enforces valid shared leases", TestRimBridgeRouteLeaseSafety);
        Run("RimBridge route auth failure invalidates credentials and redacts secrets", TestRimBridgeRouteAuthRedaction);
        Run("RimBridge route is disabled or unavailable fail closed", TestRimBridgeRouteUnavailableModes);
        Run("RimBridge hello sends structured client information", TestRimBridgeWireClientInfo);
        Run("RimBridge client maps wire auth, tool, and timeout failures", TestRimBridgeWireFailures);
        Run("RimBridge GABP compatibility contract is centralized", TestRimBridgeProtocolCompatibilityContract);
        Run("RimBridge GABP typed protocol fixtures are stable", TestRimBridgeProtocolTypedFixtures);
        Run("RimBridge GABP wire fixtures reject drift", TestRimBridgeProtocolWireFixtures);
        Run("RimBridge GABP framing rejects malformed messages", TestRimBridgeProtocolFramingFailures);
        Run("RimBridge companion authentication failures stay bounded", TestRimBridgeProtocolCompanionFailures);
        Run("DevBridge core mod remains SDK-free", TestCoreModRemainsSdkFree);
        Run("BridgeTools companion project and publish contract", TestBridgeToolsPublishContract);
        Run("BridgeTools publishing refreshes stale deployment output", TestBridgeToolsPublishRefreshesStaleDll);
        Run("doctor identifies a mod-local BridgeTools companion", TestBridgeToolsWrongLocationDiagnostic);
        Run("RimBridge companion status exposes a bounded diagnostic category", TestRimBridgeCompanionDiagnosticCategory);
        Run("baseline profile excludes managed projects and load-them-last", TestBaselineProfile);
        Run("profile dependency closure is ordered, deduplicated, and case-insensitive", TestProfileDependencyClosure);
        Run("structured dependency metadata accepts descriptive fields and preserves constraints", TestStructuredDependencyMetadata);
        Run("structured dependency metadata permits comments and formatting whitespace", TestStructuredDependencyCommentsAndWhitespace);
        Run("package discovery uses the mod package ID rather than dependency IDs", TestPackageDiscoveryUsesOwnPackageId);
        Run("profile config write waits for leases and process shutdown", TestProfileWriteWaitsForDrain);
        Run("profile writes fail closed on config and process races", TestProfileWritePreconditions);
        Run("generated config ownership survives lost state", TestGeneratedOwnershipSurvivesLostState);
        Run("invalid profiles fail before mutation or launch", TestInvalidProfilesFailClosed);
        Run("accepted profile survives coordinator recovery and conflicts", TestProfileRecoveryAndConflict);
        Run("recovery launches the frozen accepted profile without re-resolving metadata", TestFrozenProfileRecovery);
        Run("accepted generations pin immutable semantic evidence", TestGenerationHistoryPinsEvidence);
        Run("last-known-good survives normal termination", TestGenerationHistoryLastGoodAfterStop);
        Run("failed generations do not become last-known-good", TestGenerationHistoryFailedLaunch);
        Run("generation history corruption is visible and not rewritten", TestGenerationHistoryCorruption);
        Run("corrupt persisted profiles quarantine recovery", TestCorruptPersistedProfileQuarantine);
        Run("baseline restore is byte-for-byte and rejects external edits", TestBaselineRestoreSafety);
        Run("accepted project failure isolates and restores the control profile", TestCrashIsolationSingleProject);
        Run("process-exit evidence enters crash isolation", TestCrashIsolationProcessExit);
        Run("control failure is environmental and blames no project", TestCrashIsolationControlFailure);
        Run("stale process identity cannot start isolation", TestCrashIsolationRejectsStaleIdentity);
        Run("unsafe candidate failure is environmental and unattributed", TestCrashIsolationUnsafeCandidate);
        Run("isolation budget never replenishes after recovery", TestCrashIsolationBudgetDoesNotReplenish);
        Run("explicit human legacy launch ignores the aggregate runtime snapshot", TestLegacyLaunchIgnoresBaselineRuntimeProfile);
        Run("isolation preparation failure is durably quarantined", TestCrashIsolationPrepareFailure);
        Run("persisted unsafe isolation result resumes without relaunch", TestCrashIsolationUnsafeResultRecovery);
        Run("mismatched recovered candidate is quarantined without relaunch", TestCrashIsolationProfileMismatch);
        Run("isolation status tells agents not to retry", TestCrashIsolationStatusAction);
        Run("isolation resumes a durable in-flight final control attempt", TestCrashIsolationRecovery);
        Run("isolation resumes a persisted terminal attempt exactly once", TestCrashIsolationTerminalAttemptRecovery);
        Run("isolation restores the maximal safe remainder", TestCrashIsolationSafeRemainder);
        Run("non-reproducing failure is reported as intermittent", TestCrashIsolationIntermittent);
        Run("isolation finds a minimal incompatible project set", TestCrashIsolationMinimalSet);
        Run("isolation reports multiple independent failing sets", TestCrashIsolationMultipleSets);
        Run("component versions and schema markers stay consistent", TestComponentVersionsAndSchemas);
        Run("persisted schemas upgrade safely and reject newer artifacts", TestPersistedSchemaCompatibility);
        Run("doctor returns a stable healthy JSON contract", TestDoctorHealthyContract);
        Run("doctor collects independent findings", TestDoctorCollectsIndependentFindings);
        Run("doctor audits artifact permissions without writes", TestDoctorAuditsPermissions);
        Run("doctor detects stale readiness", TestDoctorDetectsStaleReadiness);
        Run("doctor findings are deterministic", TestDoctorFindingsAreDeterministic);
        Run("doctor detects process identity ambiguity", TestDoctorDetectsProcessIdentityAmbiguity);
        Run("doctor detects external ModsConfig mutation", TestDoctorDetectsExternalModsConfigMutation);
        Run("doctor diagnoses unsupported state schema", TestDoctorRejectsUnsupportedStateSchema);
        Run("doctor diagnoses unsupported generated-config schema", TestDoctorRejectsUnsupportedGeneratedConfigSchema);
        Run("doctor reports lease and maintenance conflicts", TestDoctorReportsLeaseAndMaintenanceConflicts);
        Run("doctor reports crash isolation and safe actions", TestDoctorReportsCrashIsolationAndSafeActions);
        Run("doctor redacts secret-shaped diagnostic values", TestDoctorRedactsSecrets);
        Run("structured recovery guidance is safe and parameterized", TestStructuredRecoveryGuidance);
        Run("wrapper propagates native exit codes", DevBridgeWrapperTests.Run);
        Run("named-pipe stop completes the originating client", TestNamedPipeStopCompletesClient);
        Run("named-pipe JSON stop completes with one terminal result", TestNamedPipeJsonStopCompletesClient);
        Run("coordinator shutdown responds before exit", TestCoordinatorShutdownRespondsBeforeExit);
        Run("coordinator shutdown reacquires mutex and pipe", TestCoordinatorShutdownReacquiresMutexAndPipe);
        Run("coordinator reloads current environment and executable", TestCoordinatorShutdownReloadsCurrentEnvironmentAndExecutable);
        Run("versioned IPC requestId and terminal result contract", TestVersionedIpcRequestIdAndTerminalResult);
        Run("versioned IPC events precede exactly one result", TestVersionedIpcEventsAndSingleResult);
        Run("versioned IPC preserves long-running session semantics", TestVersionedIpcLongRunningSession);
        Run("versioned IPC JSON and human clients remain supported", TestVersionedIpcJsonAndHumanClients);
        Run("versioned IPC malformed requests fail boundedly", TestVersionedIpcMalformedRequest);
        Run("versioned IPC rejects unsupported protocol versions", TestVersionedIpcUnsupportedProtocol);
        Run("versioned IPC rejects mismatched response correlation", TestVersionedIpcMismatchedRequestId);
        Run("versioned IPC rejects server disconnect before result", TestVersionedIpcDisconnectBeforeResult);
        Run("versioned IPC duplicate terminal results are rejected deterministically", TestVersionedIpcDuplicateResult);
        Run("coordinator build identity is exact and revision-distinguishable", TestCoordinatorBuildIdentity);
        Run("coordinator pipe trust boundary and IPC limits", TestCoordinatorPipeTrustBoundaryAndLimits);
        Run("oversized and malformed IPC requests are mutation-free", TestOversizedAndMalformedRequestsAreMutationFree);
        Run("runtime namespace identities are canonical and collision-resistant", TestRuntimeNamespaceInvariants);
        Run("durable identifiers are widened with safe legacy handling", TestIdentifierStrengthAndLegacyCompatibility);
        Run("legacy runtime slots migrate atomically and fail closed", TestLegacyRuntimeSlotMigration);
        Run("two coordinators cannot own one runtime slot", TestTwoCoordinatorsCannotOwnSameSlot);
        Run("finite commands have bounded terminal responses", TestFiniteCommandsHaveBoundedTerminalResponses);
        Run("durable wait response policy remains unbounded", TestDurableWaitResponsePolicyRemainsUnbounded);
        Run("simultaneous shutdown clients are bounded and durable", TestSimultaneousShutdownClientsAreBoundedAndDurable);
        Run("coordinator trace lifecycle events are ordered", TestCoordinatorTraceLifecycleOrder);
        Run("coordinator trace separates STOPPED persistence from result", TestCoordinatorTraceSeparatesStoppedPersistenceFromResult);
        Run("coordinator trace is secret-safe and bounded", TestCoordinatorTraceSecretSafetyAndBounds);
        Run("coordinator trace rotation is bounded", TestCoordinatorTraceRotation);
        Run("diagnostic write failure is non-fatal and has no unsafe fallback", TestCoordinatorTraceWriteFailureIsNonFatal);
        Run("concurrent requests retain distinguishable trace correlation", TestCoordinatorTraceConcurrentRequestCorrelation);
        Run("fault injection covers durable lifecycle boundaries", TestFaultInjectionDurableStateBoundaries);
        Run("fault injection covers launch boundaries", TestFaultInjectionLaunchBoundaries);
        Run("fault injection covers ensure-ready boundaries", TestFaultInjectionEnsureReadyBoundary);
        Run("fault injection covers IPC and shutdown boundaries", TestFaultInjectionIpcAndShutdownBoundaries);
        Run("fault injection covers artifacts and recovery", TestFaultInjectionArtifactAndRecoveryBoundaries);
        Run("deterministic coordinator state-machine sequences", TestDeterministicCoordinatorStateMachine);
        Run("agent capabilities are versioned and bounded", TestAgentCapabilities);
        Run("agent build plan reports loaded-code uncertainty", TestAgentBuildPlan);
        Run("agent snapshot is compact and fail-closed", TestAgentSnapshot);
        Run("agent epoch and delta journal semantics", TestAgentDeltaJournal);
        Run("agent wait-event wakes on durable state change", TestAgentWaitEvent);
        Run("agent wait-event timeout and shutdown are terminal", TestAgentWaitEventTimeoutAndShutdown);
        Run("agent IPC preserves one versioned terminal result", TestAgentIpc);
        Run("recipe parsing and compact discovery are strict", TestRecipeParsingAndDiscovery);
        Run("v2 behavioral recipe contract is explicit and bounded", TestV2RecipeContractIsExplicitAndBounded);
        Run("recipe and agent planning are pure and bounded", TestRecipePlanningIsPureAndBounded);
        Run("satisfied recipe execution avoids restart", TestRecipeAlreadySatisfiedAvoidsRestart);
        Run("recipe execution uses one launch and enforces caller budget", TestRecipeRunUsesOneLaunchAndEnforcesBudget);
        Run("recipe budgets cannot weaken coordinator safety limits", TestRecipeRunBudgetCannotWeakenCoordinatorLimit);
        Run("failure fingerprints normalize noise and preserve context changes", TestFailureFingerprintNormalization);
        Run("failure occurrences deduplicate with bounded evidence", TestFailureOccurrenceDeduplication);
        Run("repeated recipe failures short-circuit only equivalent inputs", TestRepeatedRecipeFailureEquivalence);
        Run("semantic logs are launch-bounded, deduplicated, and smaller", TestSemanticLogsAreBoundedAndCompact);
        Run("evidence lookup is lazy, bounded, and expires deterministically", TestEvidenceLookupBoundsAndExpiry);
        Run("forensic commands expose diagnosis references without loaded-code claims", TestForensicCommandsAndDiagnosisReference);

        Console.WriteLine(failures == 0 ? "OFFLINE TESTS PASS" : "OFFLINE TESTS FAIL: " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine("FAIL " + name + ": " + exception.Message);
        }
    }

    private sealed class ProfileSetup : IDisposable
    {
        internal readonly Fixture Fixture;
        internal readonly string MetadataRoot;

        private ProfileSetup(Fixture fixture, string metadataRoot)
        {
            Fixture = fixture;
            MetadataRoot = metadataRoot;
        }

        internal static ProfileSetup Create()
        {
            Fixture fixture = new(new PersistedState { Generation = 0, Phase = BridgePhase.STOPPED });
            string metadataRoot = Path.Combine(fixture.Root, "InstalledMods");
            Directory.CreateDirectory(metadataRoot);
            fixture.InstalledModsRoots = new[] { metadataRoot };
            WriteAllMetadata(metadataRoot);
            File.WriteAllText(Path.Combine(fixture.Root, "ModsConfig.xml"),
                "<ModsConfigData>\r\n  <activeMods>\r\n" +
                string.Join("\r\n", new[]
                {
                    "    <li>lan.devbridge2</li>",
                    "    <li>lan.horticulture.novelseeds</li>",
                    "    <li>lan.aquaculture.fishing</li>",
                    "    <li>ferny.loadthemlast</li>"
                }) + "\r\n  </activeMods>\r\n</ModsConfigData>", new UTF8Encoding(false));
            fixture.State = fixture.Reload();
            return new ProfileSetup(fixture, metadataRoot);
        }

        internal bool CaptureBaseline()
        {
            return Fixture.State.Execute(Request("mods", "agent", 1, "capture-baseline"), _ => { }, () => true) == 0;
        }

        public void Dispose() => Fixture.Dispose();

        internal static void WriteAllMetadata(string metadataRoot)
        {
            foreach (string packageId in ModProfileResolver.AlwaysOnPackageIds)
                WriteInstalledMetadata(metadataRoot, packageId, packageId, "");

            WriteInstalledMetadata(metadataRoot, "deferred-reality", "lan.deferredreality.framework", "");
            WriteInstalledMetadata(metadataRoot, "insight-canvas", "lan.insightcanvas", "");
            WriteInstalledMetadata(metadataRoot, "knowledge-framework", "lan.knowledgeframework", "");
            WriteInstalledMetadata(metadataRoot, "frontier", "lan.frontier", "");
            WriteInstalledMetadata(metadataRoot, "aquaculture", "lan.aquaculture.fishing",
                "FERNY.ReplaceLib", "ferny.progressionagriculture", "");
            WriteInstalledMetadata(metadataRoot, "horticulture", "lan.horticulture.novelseeds",
                "ferny.progressionagriculture", "", "lan.aquaculture.fishing");
            WriteInstalledMetadata(metadataRoot, "wildlife", "lan.wildlife", "");
            WriteInstalledMetadata(metadataRoot, "progression", "ferny.progressionagriculture", "ferny.replacelib");
            WriteInstalledMetadata(metadataRoot, "replacelib", "FERNY.ReplaceLib", "vanillaexpanded.vcef");
            WriteInstalledMetadata(metadataRoot, "vcef", "vanillaexpanded.vcef",
                "oskarpotocki.vanillafactionsexpanded.core");
            WriteInstalledMetadata(metadataRoot, "vfe-core", "oskarpotocki.vanillafactionsexpanded.core", "");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        internal readonly string RimWorldPath;
        internal readonly FakeClock Clock;
        internal readonly FakeProcessAdapter Adapter;
        internal CoordinatorState State;
        internal IReadOnlyList<string> InstalledModsRoots { get; set; }
        internal RimBridgeMode RimBridgeMode { get; set; } = RimBridgeMode.Off;
        internal string PlayerLogPath { get; set; }
        internal IRimBridgeClient RouteClient { get; set; }
        internal IRimBridgeGenerationVerifier RouteVerifier { get; set; }
        internal Action BeforeModsConfigWrite { get; set; }
        internal ICoordinatorFaultInjector FaultInjector { get; set; }

        internal Fixture(PersistedState initial)
        {
            Root = Path.Combine(Path.GetTempPath(), "DevBridge2-offline-" + Guid.NewGuid().ToString("N"), "Coordinator");
            Directory.CreateDirectory(Path.Combine(Root, "Runtime"));
            Directory.CreateDirectory(Path.Combine(Root, "About"));
            Directory.CreateDirectory(Path.Combine(Root, "1.6", "Assemblies"));
            RimWorldPath = Path.Combine(Root, "RimWorldWin64.exe");
            File.WriteAllText(RimWorldPath, "offline-test-executable");
            File.WriteAllText(Path.Combine(Root, "About", "About.xml"), "<ModMetaData />");
            File.WriteAllText(Path.Combine(Root, "1.6", "Assemblies", "DevBridge2.dll"), "offline-test-assembly");
            File.WriteAllText(Path.Combine(Root, "ModsConfig.xml"), "<activeMods><li>lan.devbridge2</li></activeMods>");
            string metadataRoot = Path.Combine(Root, "InstalledMods");
            Directory.CreateDirectory(metadataRoot);
            ProfileSetup.WriteAllMetadata(metadataRoot);
            InstalledModsRoots = new[] { metadataRoot };
            Clock = new FakeClock(ClockStart);
            Adapter = new FakeProcessAdapter(RimWorldPath, Root, Clock);
            WriteState(initial);
            State = Reload();
        }

        internal TestLease Lease(DateTime started) => Lease("T001", "holder", 77, started);

        internal TestLease Lease(string id, string agent, int pid, DateTime started) => new()
        {
            Id = id, Agent = agent, ClientProcessId = pid, Generation = 1,
            StartedUtc = started, LastHeartbeatUtc = started
        };

        internal static Fixture LoadingWithLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 0,
                Phase = BridgePhase.LOADING,
                LaunchId = "launch-1",
                LaunchGeneration = 1,
                TargetGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease>
                {
                    new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 0,
                        StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
                }
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture ReadyWithLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease> { new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1,
                    StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart } }
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture ReadyWithLeases()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease>
                {
                    new() { Id = "T001", Agent = "holder-a", ClientProcessId = 77, Generation = 1,
                        StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart },
                    new() { Id = "T002", Agent = "holder-b", ClientProcessId = 78, Generation = 1,
                        StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart }
                }
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture ReadyWithoutLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal static Fixture MaintenanceWithLease()
        {
            return new Fixture(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.STOPPED,
                MaintenanceReady = true,
                SessionDirty = true,
                ProcessId = 0,
                ProcessStartUtcTicks = 0,
                Leases = new List<TestLease> { new() { Id = "T001", Agent = "holder", ClientProcessId = 77, Generation = 1,
                    StartedUtc = ClockStart, LastHeartbeatUtc = ClockStart } }
            });
        }

        internal static Fixture FailedWithoutLease()
        {
            Fixture fixture = new(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.ERROR,
                ErrorCode = "QUICKTEST_GENERATION_FAILED",
                Error = "quicktest failed",
                LaunchId = "launch-failed",
                LaunchGeneration = 2,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart
            });
            fixture.Adapter.Add(new FakeProcess(101, 1001, fixture.RimWorldPath));
            return fixture;
        }

        internal CoordinatorState ReloadWithLease(DateTime started)
        {
            WriteState(new PersistedState
            {
                Generation = 1,
                Phase = BridgePhase.READY,
                LaunchId = "launch-ready",
                LaunchGeneration = 1,
                ProcessId = 101,
                ProcessStartUtcTicks = 1001,
                LaunchStartedUtc = ClockStart,
                Leases = new List<TestLease> { Lease(started) }
            });
            Adapter.Replace(101, 1001);
            return Reload();
        }

        internal void WriteReadiness(string launchId, int generation, int processId)
        {
            File.WriteAllText(Path.Combine(Root, "Runtime", "readiness.json"), JsonSerializer.Serialize(new ReadinessRecord
            {
                LaunchId = launchId,
                Generation = generation,
                ProcessId = processId,
                TimestampUtc = Clock.UtcNow
            }, Program.JsonOptions));
        }

        internal void WriteState(PersistedState value)
        {
            File.WriteAllText(Path.Combine(Root, "Runtime", "state.json"), JsonSerializer.Serialize(value, Program.JsonOptions));
        }

        internal CoordinatorState Reload()
        {
            ICoordinatorFaultInjector faultInjector = FaultInjector;
            CoordinatorState reloaded = new CoordinatorState(Root, new CoordinatorOptions
            {
                ReadinessTimeout = TimeSpan.FromSeconds(3),
                ProcessInspectionRetryTimeout = TimeSpan.FromSeconds(2),
                ProcessExitTimeout = TimeSpan.FromSeconds(1),
                ProcessAdapter = Adapter,
                Clock = Clock,
                RimWorldExecutablePath = RimWorldPath,
                ModsConfigPath = Path.Combine(Root, "ModsConfig.xml"),
                InstalledModsRoots = InstalledModsRoots,
                RimBridgeMode = RimBridgeMode,
                PlayerLogPath = PlayerLogPath ?? Path.Combine(Root, "Player.log"),
                RimBridgeClient = RouteClient,
                RimBridgeGenerationVerifier = RouteVerifier,
                BeforeModsConfigWrite = BeforeModsConfigWrite,
                // The fixture applies fault plans after construction so the
                // coordinator's process-scoped epoch initialization remains
                // outside operation-boundary fault tests.
                FaultInjector = null
            });
            reloaded.SetFaultInjectorForTesting(faultInjector);
            return reloaded;
        }

        public void Dispose()
        {
            try
            {
                string fixtureRoot = Directory.GetParent(Root)?.FullName ?? Root;
                Directory.Delete(fixtureRoot, true);
            }
            catch { }
        }
    }

    private sealed class FakeRimBridgeClient : IRimBridgeClient
    {
        internal RimBridgeWireResult ListResult { get; set; } = WireSuccess("{\"tools\":[]}");
        internal RimBridgeWireResult CallResult { get; set; } = WireSuccess("{\"success\":true}");
        internal int ListCalls { get; private set; }
        internal int CallCalls { get; private set; }
        internal string LastToolName { get; private set; }
        internal JsonElement LastArguments { get; private set; }

        public RimBridgeWireResult ListTools(RimBridgeEndpoint endpoint, string expectedLaunchId,
            TimeSpan timeout)
        {
            ListCalls++;
            return ListResult;
        }

        public RimBridgeWireResult CallTool(RimBridgeEndpoint endpoint, string expectedLaunchId,
            string toolName, JsonElement arguments, TimeSpan timeout)
        {
            CallCalls++;
            LastToolName = toolName;
            LastArguments = arguments.Clone();
            return CallResult;
        }
    }

    private sealed class FakeRimBridgeGenerationVerifier : IRimBridgeGenerationVerifier
    {
        internal RimBridgeCompanionVerification Result { get; set; }

        public RimBridgeCompanionVerification Verify(RimBridgeEndpoint endpoint, string expectedLaunchId,
            int expectedGeneration, int expectedProcessId, TimeSpan timeout) => Result;
    }

    private sealed class FakeClock : ICoordinatorClock
    {
        private long nowTicks;
        internal FakeClock(DateTime start) => nowTicks = start.Ticks;
        public DateTime UtcNow => new(Volatile.Read(ref nowTicks), DateTimeKind.Utc);
        public void Sleep(TimeSpan duration) => Interlocked.Add(ref nowTicks, duration.Ticks);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref nowTicks, duration.Ticks);
    }

    private sealed class FakeProcessAdapter : IProcessAdapter
    {
        private readonly string executablePath;
        private readonly string root;
        private readonly FakeClock clock;
        private readonly Dictionary<int, FakeProcess> processes = new();
        private int nextPid = 200;
        private long nextStart = 2000;

        internal int LaunchCalls { get; private set; }
        internal IReadOnlyList<string> LastLaunchArguments { get; private set; } = Array.Empty<string>();
        internal IReadOnlyDictionary<string, string> LastLaunchEnvironment { get; private set; } =
            new Dictionary<string, string>();
        internal int TerminationRequests => processes.Values.Sum(value => value.TerminationRequests);
        internal FakeProcess Current => processes.Values.OrderByDescending(value => value.Id).First();
        internal bool ReadyOnLaunch { get; set; }
        internal Func<bool> ReadyOnLaunchPredicate { get; set; }
        internal Func<bool> ExitOnLaunchPredicate { get; set; }
        internal Func<ProcessLaunchRequest, FakeProcess, QuicktestFailureRecord> QuicktestFailureOnLaunch { get; set; }
        internal string RawQuicktestFailureJsonOnLaunch { get; set; }
        internal bool ThrowOnLaunch { get; set; }
        internal Func<bool> ThrowOnLaunchedProcessHasExitedPredicate { get; set; }
        internal int LaunchedProcessExecutablePathFailures { get; set; }
        internal bool ExtraMatchingProcess { get; set; }
        internal bool AddExtraMatchingProcessOnSecondEnumeration { get; set; }
        internal bool EnumerationIncomplete { get; set; }
        internal int EnumerationCalls { get; private set; }
        internal bool BlockWaitForExit
        {
            get => Current.BlockWait;
            set
            {
                Current.BlockWait = value;
                Current.WaitSignal = ReleaseWait;
            }
        }
        internal ManualResetEventSlim TerminationRequested { get; } = new(false);
        internal ManualResetEventSlim ReleaseWait { get; } = new(false);

        internal FakeProcessAdapter(string executablePath, string root, FakeClock clock)
        {
            this.executablePath = executablePath;
            this.root = root;
            this.clock = clock;
        }

        internal void Add(FakeProcess process)
        {
            process.TerminationSignal = TerminationRequested;
            processes[process.Id] = process;
        }

        internal void Replace(int id, long startIdentity)
        {
            Add(new FakeProcess(id, startIdentity, executablePath));
        }

        public IManagedProcess Open(int processId)
        {
            processes.TryGetValue(processId, out FakeProcess process);
            return process;
        }

        public ProcessEnumeration EnumerateRimWorld(string configuredPath)
        {
            EnumerationCalls++;
            if (EnumerationIncomplete)
                return new ProcessEnumeration { Complete = false, Error = "simulated inspection failure" };
            if (ExtraMatchingProcess || (AddExtraMatchingProcessOnSecondEnumeration && EnumerationCalls >= 2))
                Add(new FakeProcess(999, 9999, executablePath));

            try
            {
                return new ProcessEnumeration
                {
                    Complete = true,
                    Processes = processes.Values.Where(value => !value.HasExited &&
                        string.Equals(value.ExecutablePath, configuredPath, StringComparison.OrdinalIgnoreCase) &&
                        value.StartIdentity > 0).Cast<IManagedProcess>().ToList()
                };
            }
            catch
            {
                return new ProcessEnumeration { Complete = false, Error = "simulated inspection failure" };
            }
        }

        public IManagedProcess Launch(ProcessLaunchRequest request)
        {
            LaunchCalls++;
            if (ThrowOnLaunch)
                throw new InvalidOperationException("simulated raw launch failure");
            LastLaunchArguments = request.Arguments?.ToArray() ?? Array.Empty<string>();
            LastLaunchEnvironment = new Dictionary<string, string>(request.Environment ??
                new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            FakeProcess process = new(nextPid++, nextStart++, executablePath)
            {
                WaitExits = true,
                ExecutablePathFailuresRemaining = LaunchedProcessExecutablePathFailures
            };
            process.TerminationSignal = TerminationRequested;
            processes[process.Id] = process;
            if (ThrowOnLaunchedProcessHasExitedPredicate?.Invoke() == true)
                process.ThrowOnHasExited = true;
            if (ExitOnLaunchPredicate?.Invoke() == true)
                process.ForceTerminate();
            if (RawQuicktestFailureJsonOnLaunch != null)
            {
                File.WriteAllText(QuicktestFailureArtifact.PathFor(root), RawQuicktestFailureJsonOnLaunch);
            }
            else if (QuicktestFailureOnLaunch != null)
            {
                QuicktestFailureArtifact.TryWrite(root, QuicktestFailureOnLaunch(request, process),
                    out _);
            }
            if (ReadyOnLaunch || ReadyOnLaunchPredicate?.Invoke() == true)
            {
                File.WriteAllText(Path.Combine(root, "Runtime", "readiness.json"), JsonSerializer.Serialize(new ReadinessRecord
                {
                    LaunchId = request.Environment["DEVBRIDGE_LAUNCH_ID"],
                    Generation = int.Parse(request.Environment["DEVBRIDGE_GENERATION"]),
                    ProcessId = process.Id,
                    TimestampUtc = clock.UtcNow
                }, Program.JsonOptions));
            }
            return process;
        }
    }

    private sealed class FakeProcess : IManagedProcess
    {
        internal ManualResetEventSlim WaitSignal { get; set; } = new(true);
        internal int TerminationRequests { get; private set; }
        internal bool WaitExits { get; set; } = true;
        internal bool BlockWait { get; set; }
        internal ManualResetEventSlim TerminationSignal { get; set; }
        private bool exited;

        internal FakeProcess(int id, long startIdentity, string executablePath)
        {
            Id = id;
            this.startIdentity = startIdentity;
            this.executablePath = executablePath;
        }

        public int Id { get; }
        internal bool ThrowOnStartIdentity { get; set; }
        internal bool ThrowOnExecutablePath { get; set; }
        internal bool ThrowOnHasExited { get; set; }
        internal int ExecutablePathFailuresRemaining { get; set; }
        public long StartIdentity => ThrowOnStartIdentity ? throw new InvalidOperationException("start identity unavailable") : startIdentity;
        public string ExecutablePath
        {
            get
            {
                if (ThrowOnExecutablePath)
                    throw new InvalidOperationException("path unavailable");
                if (ExecutablePathFailuresRemaining > 0)
                {
                    ExecutablePathFailuresRemaining--;
                    throw new InvalidOperationException("path unavailable");
                }
                return executablePath;
            }
        }
        public bool HasExited => ThrowOnHasExited ? throw new InvalidOperationException("exit state unavailable") : exited;

        private readonly long startIdentity;
        private readonly string executablePath;

        public bool RequestTermination()
        {
            TerminationRequests++;
            TerminationSignal?.Set();
            return true;
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            if (BlockWait)
                WaitSignal.Wait(timeout);
            if (WaitExits)
                exited = true;
            return exited;
        }

        public bool ForceTerminate()
        {
            exited = true;
            return true;
        }

        public void Dispose() { }
    }
}
