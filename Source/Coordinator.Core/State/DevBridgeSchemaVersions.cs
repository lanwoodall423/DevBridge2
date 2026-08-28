namespace DevBridge2;

/// <summary>
/// Version markers for durable files shared by the coordinator and the mod.
/// Missing markers are treated as the supported legacy format; newer markers
/// are never interpreted as an older format.
/// </summary>
public static class DevBridgeSchemaVersions
{
    public const int RuntimeState = 1;
    public const int Readiness = 1;
    public const int GeneratedModsConfig = 1;
    public const int Doctor = 1;
    public const int GenerationManifest = 1;
    public const int GenerationHistory = 1;
    public const int HistoryDiff = 1;
    public const int HistoryDiagnosis = 1;
    public const int CoordinatorProtocolMajor = 2;
    public const int CoordinatorMaxOutputPayloadBytes = 192 * 1024;
    public const int Identity = 1;

    public const string IdentityContract = "devbridge-identity/v1";

    public const string RuntimeStateContract = "devbridge-runtime-state/v1";
    public const string ReadinessContract = "devbridge-readiness/v1";
    public const string GeneratedModsConfigContract = "devbridge-generated-mods-config/v1";
    public const string DoctorContract = "devbridge-doctor/v1";
    public const string GenerationManifestContract = "devbridge-generation-manifest/v1";
    public const string GenerationHistoryContract = "devbridge-generation-history/v1";
    public const string HistoryDiffContract = "devbridge-history-diff/v1";
    public const string HistoryDiagnosisContract = "devbridge-history-diagnosis/v1";
    public const string CoordinatorProtocolContract = "devbridge-coordinator-ipc/v2";
    public const string AgentCapabilitiesContract = "devbridge-agent-capabilities/v1";
    public const string AgentSnapshotContract = "devbridge-agent-snapshot/v1";
    public const string AgentDeltaContract = "devbridge-agent-delta/v1";
    public const string AgentEventContract = "devbridge-agent-event/v1";
    public const string TestRecipeContract = "devbridge-test-recipe/v1";
    public const string TestRecipeV2Contract = "devbridge-test-recipe/v2";
    public const string TestRecipeListContract = "devbridge-test-recipe-list/v1";
    public const string TestRecipeShowContract = "devbridge-test-recipe-show/v1";
    public const string TestRecipePlanContract = "devbridge-test-recipe-plan/v1";
    public const string TestRecipeRunContract = "devbridge-test-recipe-run/v1";
    public const string AgentPlanContract = "devbridge-agent-plan/v1";
    public const string AgentBuildPlanContract = "devbridge-agent-build-plan/v1";
    public const string FailureFingerprintContract = "devbridge-failure-fingerprint/v1";
    public const string EvidenceContract = "devbridge-evidence/v1";
    public const string LogsQueryContract = "devbridge-logs-query/v1";
    public const string GamePrimitivesContract = "devbridge-game-primitives/v1";

    public const string AgentCapabilities = AgentCapabilitiesContract;
    public const string AgentSnapshot = AgentSnapshotContract;
    public const string AgentDelta = AgentDeltaContract;
    public const string AgentEvent = AgentEventContract;
    public const string TestRecipe = TestRecipeContract;
    public const string TestRecipeList = TestRecipeListContract;
    public const string TestRecipeShow = TestRecipeShowContract;
    public const string TestRecipePlan = TestRecipePlanContract;
    public const string TestRecipeRun = TestRecipeRunContract;
    public const string AgentPlan = AgentPlanContract;
    public const string AgentBuildPlan = AgentBuildPlanContract;
    public const string FailureFingerprint = FailureFingerprintContract;
    public const string Evidence = EvidenceContract;
    public const string LogsQuery = LogsQueryContract;
    public const string GamePrimitives = GamePrimitivesContract;
}
