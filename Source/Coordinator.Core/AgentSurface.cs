using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBridge2;

namespace DevBridge.Coordinator;

internal sealed class AgentChangeRecord
{
    public long Sequence { get; set; }
    public Dictionary<string, string> Changes { get; set; } = new();
}

internal abstract class AgentResponse
{
    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ExitCode { get; set; }
}

internal sealed class AgentCapabilitiesResponse : AgentResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.AgentCapabilities;

    [JsonPropertyName("agentApiVersion")]
    public int AgentApiVersion { get; init; } = 1;

    [JsonPropertyName("coordinatorProtocol")]
    public int CoordinatorProtocol { get; init; } = DevBridgeSchemaVersions.CoordinatorProtocolMajor;

    [JsonPropertyName("features")]
    public AgentFeatureFlags Features { get; init; } = new();

    // Game primitives are part of the existing agent capability contract. They
    // are discovery metadata only; callers still use the lease-bound `game`
    // command for live operations.
    [JsonPropertyName("gamePrimitives")]
    public AgentGamePrimitiveSet GamePrimitives { get; init; } = AgentGamePrimitiveSet.Create();
}

internal sealed class AgentFeatureFlags
{
    [JsonPropertyName("snapshot")]
    public bool Snapshot { get; init; } = true;

    [JsonPropertyName("delta")]
    public bool Delta { get; init; } = true;

    [JsonPropertyName("waitEvent")]
    public bool WaitEvent { get; init; } = true;

    [JsonPropertyName("testRecipes")]
    public bool TestRecipes { get; init; } = true;

    [JsonPropertyName("plan")]
    public bool Plan { get; init; } = true;

    [JsonPropertyName("buildPlan")]
    public bool BuildPlan { get; init; } = true;

    [JsonPropertyName("semanticLogs")]
    public bool SemanticLogs { get; init; } = true;

    [JsonPropertyName("gamePrimitives")]
    public bool GamePrimitives { get; init; } = true;

    [JsonPropertyName("runtimeErrorDelta")]
    public bool RuntimeErrorDelta { get; init; } = true;
}

internal sealed class AgentGamePrimitiveSet
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.GamePrimitives;

    [JsonPropertyName("contract")]
    public string Contract { get; init; } = DevBridgeSchemaVersions.GamePrimitives;

    [JsonPropertyName("leaseRequired")]
    public bool LeaseRequired { get; init; } = true;

    [JsonPropertyName("command")]
    public string Command { get; init; } = "game";

    [JsonPropertyName("dynamicToolForwarding")]
    public bool DynamicToolForwarding { get; init; } = true;

    [JsonPropertyName("operations")]
    public List<AgentGamePrimitiveOperation> Operations { get; init; } = new();

    internal static AgentGamePrimitiveSet Create() => new()
    {
        Operations = new List<AgentGamePrimitiveOperation>
        {
            new()
            {
                Id = "inspect",
                Command = "game inspect <tool-name> [JSON object] [--lease <lease-id>] --json",
                Kind = "state-query",
                Tool = "caller-selected semantic tool",
                Bounded = true,
                Description = "Forward one caller-selected semantic state query through the normal RimBridge route."
            },
            new()
            {
                Id = "action",
                Command = "game action <tool-name> [JSON object] [--lease <lease-id>] --json",
                Kind = "action",
                Tool = "caller-selected semantic tool",
                Bounded = true,
                Description = "Invoke one caller-selected semantic game/mod action; DevBridge does not encode a scenario."
            },
            new()
            {
                Id = "wait",
                Command = "game wait <tool-name> [JSON object] --path <JSON pointer> --equals <JSON value> --timeout-ms <n> [--poll-ms <n>] [--lease <lease-id>] --json",
                Kind = "condition-wait",
                Tool = "caller-selected semantic query tool",
                Bounded = true,
                Description = "Poll an observable structured result until one JSON-pointer value equals the requested value."
            },
            new()
            {
                Id = "advance",
                Command = "game advance --ticks <n> [--timeout-ms <n>] [--poll-ms <n>] [--lease <lease-id>] --json",
                Kind = "simulation",
                Tool = "rimworld/step_game_ticks",
                Bounded = true,
                Description = "Advance a bounded number of RimWorld ticks and return the terminal tool result."
            },
            new()
            {
                Id = "save",
                Command = "game save --name <save-name> [--timeout-ms <n>] [--lease <lease-id>] --json",
                Kind = "save",
                Tool = "rimworld/save_game + rimbridge/wait_for_long_event_idle",
                Bounded = true,
                Description = "Request a save and confirm the save operation reaches long-event idle."
            },
            new()
            {
                Id = "load",
                Command = "game load --name <save-name> [--readiness <level>] [--timeout-ms <n>] [--poll-ms <n>] [--ignore-mod-compatibility] [--lease <lease-id>] --json",
                Kind = "load",
                Tool = "rimworld/load_game_ready",
                Bounded = true,
                Description = "Load a named save and return only after the requested game readiness level is reached."
            },
            new()
            {
                Id = "errors-checkpoint",
                Command = "game errors checkpoint [--lease <lease-id>] --json",
                Kind = "runtime-error-checkpoint",
                Tool = "rimbridge/list_logs",
                Bounded = true,
                Description = "Capture the current generation-bound RimBridge log sequence before a scenario."
            },
            new()
            {
                Id = "errors-delta",
                Command = "game errors delta --checkpoint <token> [--lease <lease-id>] --json",
                Kind = "runtime-error-delta",
                Tool = "rimbridge/list_logs",
                Bounded = true,
                Description = "Return only error log entries with a sequence newer than a prior checkpoint."
            }
        }
    };
}

internal sealed class AgentGamePrimitiveOperation
{
    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("command")]
    public string Command { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; }

    [JsonPropertyName("tool")]
    public string Tool { get; init; }

    [JsonPropertyName("bounded")]
    public bool Bounded { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; }
}

internal sealed class AgentBuildIdentity
{
    [JsonPropertyName("productVersion")]
    public string ProductVersion { get; init; }

    [JsonPropertyName("informationalVersion")]
    public string InformationalVersion { get; init; }

    [JsonPropertyName("sourceRevision")]
    public string SourceRevision { get; init; }

    [JsonPropertyName("revisionKnown")]
    public bool RevisionKnown { get; init; }

    [JsonPropertyName("dirty")]
    public bool Dirty { get; init; }

    [JsonPropertyName("buildConfiguration")]
    public string BuildConfiguration { get; init; }

    [JsonPropertyName("processStartedUtc")]
    public DateTime? ProcessStartedUtc { get; init; }

    [JsonPropertyName("coordinatorProtocolVersion")]
    public int CoordinatorProtocolVersion { get; init; }

    [JsonPropertyName("protocolContract")]
    public string ProtocolContract { get; init; }

    internal static AgentBuildIdentity From(DevBridgeBuildIdentity identity) => identity == null ? null : new()
    {
        ProductVersion = Bounded(identity.ProductVersion, 128),
        InformationalVersion = Bounded(identity.InformationalVersion, 192),
        SourceRevision = Bounded(identity.SourceRevision, 128),
        RevisionKnown = identity.RevisionKnown,
        Dirty = identity.Dirty,
        BuildConfiguration = Bounded(identity.BuildConfiguration, 32)
    };

    internal static AgentBuildIdentity From(CoordinatorBuildIdentity identity)
    {
        if (identity == null)
            return null;
        AgentBuildIdentity result = From((DevBridgeBuildIdentity)identity);
        return new AgentBuildIdentity
        {
            ProductVersion = result.ProductVersion,
            InformationalVersion = result.InformationalVersion,
            SourceRevision = result.SourceRevision,
            RevisionKnown = result.RevisionKnown,
            Dirty = result.Dirty,
            BuildConfiguration = result.BuildConfiguration,
            ProcessStartedUtc = identity.ProcessStartedUtc,
            CoordinatorProtocolVersion = identity.CoordinatorProtocolVersion,
            ProtocolContract = Bounded(identity.ProtocolContract, 96)
        };
    }

    private static string Bounded(string value, int maximum) =>
        string.IsNullOrEmpty(value) || value.Length <= maximum ? value : value.Substring(0, maximum);
}

internal sealed class AgentComponentBuildSummary
{
    [JsonPropertyName("build")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentBuildIdentity Build { get; init; }

    [JsonPropertyName("artifactSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ArtifactSha256 { get; init; }

    [JsonPropertyName("buildMatchesPublished")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BuildMatchesPublished { get; init; }

    [JsonPropertyName("loadedStatus")]
    public string LoadedStatus { get; init; }
}

internal sealed class AgentComponentBuildSnapshot
{
    [JsonPropertyName("mod")]
    public AgentComponentBuildSummary Mod { get; init; }

    [JsonPropertyName("bridgeTools")]
    public AgentComponentBuildSummary BridgeTools { get; init; }

    [JsonPropertyName("requiredRefresh")]
    public string RequiredRefresh { get; init; }
}

internal sealed class AgentBuildPlanResponse : AgentResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.AgentBuildPlan;

    [JsonPropertyName("coordinatorBuild")]
    public AgentBuildIdentity CoordinatorBuild { get; init; }

    [JsonPropertyName("coordinatorBuildMatchesPublished")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CoordinatorBuildMatchesPublished { get; init; }

    [JsonPropertyName("componentBuilds")]
    public AgentComponentBuildSnapshot ComponentBuilds { get; init; }

    [JsonPropertyName("nextAction")]
    public string NextAction { get; init; }
}

internal sealed class AgentProfileSummary
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; }

    [JsonPropertyName("projectCount")]
    public int ProjectCount { get; init; }

    [JsonPropertyName("projects")]
    public List<string> Projects { get; init; } = new();

    [JsonPropertyName("projectsTruncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProjectsTruncated { get; init; }

    [JsonPropertyName("resolvedPackageCount")]
    public int ResolvedPackageCount { get; init; }

    [JsonPropertyName("fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Fingerprint { get; init; }

    [JsonPropertyName("baselineFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BaselineFingerprint { get; init; }
}

internal sealed class AgentPendingProfileSummary
{
    [JsonPropertyName("pending")]
    public bool Pending { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    [JsonPropertyName("projectCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ProjectCount { get; init; }

    [JsonPropertyName("projects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> Projects { get; init; }

    [JsonPropertyName("fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Fingerprint { get; init; }

    [JsonPropertyName("baselineFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BaselineFingerprint { get; init; }
}

internal sealed class AgentLeaseSummary
{
    [JsonPropertyName("state")]
    public string State { get; init; }

    [JsonPropertyName("leaseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string LeaseId { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Generation { get; init; }

    [JsonPropertyName("expiresUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExpiresUtc { get; init; }
}

internal sealed class AgentMaintenanceSummary
{
    [JsonPropertyName("state")]
    public string State { get; init; }

    [JsonPropertyName("sessionDirty")]
    public bool SessionDirty { get; init; }

    [JsonPropertyName("modsConfigOwnership")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ModsConfigOwnership { get; init; }
}

internal sealed class AgentQuicktestSummary
{
    [JsonPropertyName("state")]
    public string State { get; init; }

    [JsonPropertyName("failureCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string FailureCode { get; init; }

    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Evidence { get; init; }
}

internal sealed class AgentRimBridgeSummary
{
    [JsonPropertyName("state")]
    public string State { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; init; }
}

internal sealed class AgentCompanionSummary
{
    [JsonPropertyName("state")]
    public string State { get; init; }

    [JsonPropertyName("diagnostic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Diagnostic { get; init; }
}

internal sealed class AgentCrashIsolationSummary
{
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("incidentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string IncidentId { get; init; }

    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Stage { get; init; }

    [JsonPropertyName("attempts")]
    public int Attempts { get; init; }

    [JsonPropertyName("launchesRemaining")]
    public int LaunchesRemaining { get; init; }

    [JsonPropertyName("failureCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string FailureCode { get; init; }
}

internal sealed class AgentFailureSummary
{
    [JsonPropertyName("code")]
    public string Code { get; init; }

    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Phase { get; init; }

    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Evidence { get; init; }

    [JsonPropertyName("failureFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string FailureFingerprint { get; init; }

    [JsonPropertyName("seenBefore")]
    public bool SeenBefore { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Summary { get; init; }

    [JsonPropertyName("evidenceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string EvidenceId { get; init; }

    [JsonPropertyName("diagnosisReference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string DiagnosisReference { get; init; }
}

internal sealed class AgentSnapshotResponse : AgentResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.AgentSnapshot;

    [JsonPropertyName("epoch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Epoch { get; init; }

    [JsonPropertyName("runtimeSlotId")]
    public string RuntimeSlotId { get; init; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("generation")]
    public int Generation { get; init; }

    [JsonPropertyName("targetGeneration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TargetGeneration { get; init; }

    [JsonPropertyName("phase")]
    public string Phase { get; init; }

    [JsonPropertyName("coordinatorBuild")]
    public AgentBuildIdentity CoordinatorBuild { get; init; }

    [JsonPropertyName("coordinatorBuildMatchesPublished")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CoordinatorBuildMatchesPublished { get; init; }

    [JsonPropertyName("componentBuilds")]
    public AgentComponentBuildSnapshot ComponentBuilds { get; init; }

    [JsonPropertyName("acceptedProfile")]
    public AgentProfileSummary AcceptedProfile { get; init; }

    [JsonPropertyName("pendingProfile")]
    public AgentPendingProfileSummary PendingProfile { get; init; }

    [JsonPropertyName("requestingAgentLease")]
    public AgentLeaseSummary RequestingAgentLease { get; init; }

    [JsonPropertyName("maintenance")]
    public AgentMaintenanceSummary Maintenance { get; init; }

    [JsonPropertyName("quicktest")]
    public AgentQuicktestSummary Quicktest { get; init; }

    [JsonPropertyName("rimBridgeEndpoint")]
    public AgentRimBridgeSummary RimBridgeEndpoint { get; init; }

    [JsonPropertyName("companion")]
    public AgentCompanionSummary Companion { get; init; }

    [JsonPropertyName("crashIsolation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentCrashIsolationSummary CrashIsolation { get; init; }

    [JsonPropertyName("failure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentFailureSummary Failure { get; init; }

    [JsonPropertyName("nextAction")]
    public string NextAction { get; init; }

    [JsonPropertyName("safeActions")]
    public List<string> SafeActions { get; init; } = new();

    [JsonPropertyName("blockedActions")]
    public List<string> BlockedActions { get; init; } = new();

    [JsonPropertyName("blockers")]
    public List<string> Blockers { get; init; } = new();

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; init; }
}

internal sealed class AgentDeltaResponse : AgentResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.AgentDelta;

    [JsonPropertyName("epoch")]
    public string Epoch { get; init; }

    [JsonPropertyName("fromSeq")]
    public long FromSeq { get; init; }

    [JsonPropertyName("toSeq")]
    public long ToSeq { get; init; }

    [JsonPropertyName("delta")]
    public Dictionary<string, JsonElement> Delta { get; init; } = new();

    [JsonPropertyName("nextAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string NextAction { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; init; }
}

internal sealed class AgentWaitEventResponse : AgentResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.AgentEvent;

    [JsonPropertyName("result")]
    public string Result { get; init; }

    [JsonPropertyName("epoch")]
    public string Epoch { get; init; }

    [JsonPropertyName("fromSeq")]
    public long FromSeq { get; init; }

    [JsonPropertyName("toSeq")]
    public long ToSeq { get; init; }

    [JsonPropertyName("delta")]
    public Dictionary<string, JsonElement> Delta { get; init; } = new();

    [JsonPropertyName("nextAction")]
    public string NextAction { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ErrorCode { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; init; }
}

internal sealed partial class CoordinatorState
{
    private const int AgentJournalCapacity = 128;
    private const int AgentMaxArgumentLength = 256;
    private const int AgentMaxProjectCount = 16;
    private static readonly TimeSpan AgentDefaultWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AgentMaxWait = TimeSpan.FromMinutes(5);

    internal long AgentSequenceForTesting
    {
        get { lock (gate) return state?.AgentSequence ?? 0; }
    }

    internal string AgentEpochForTesting
    {
        get { lock (gate) return state?.AgentEpoch; }
    }

    internal int AgentWaiterCountForTesting { get; private set; }

    private void BeginAgentEpochLocked()
    {
        state.AgentEpoch = "epoch-" + Guid.NewGuid().ToString("N");
        state.AgentSequence = 0;
        state.AgentChangeJournal = new List<AgentChangeRecord>();
        agentObservation = null;
        agentObservationInitialized = false;
    }

    private void InitializeAgentTrackingLocked()
    {
        agentObservation = BuildAgentObservationLocked();
        agentObservationInitialized = true;
    }

    private void UpdateAgentJournalLocked()
    {
        if (!agentObservationInitialized)
            return;

        Dictionary<string, string> current = BuildAgentObservationLocked();
        if (DictionariesEqual(agentObservation, current))
            return;

        long sequence = checked(state.AgentSequence + 1);
        Dictionary<string, string> changes = new(StringComparer.Ordinal);
        foreach (string key in agentObservation.Keys.Union(current.Keys, StringComparer.Ordinal))
        {
            agentObservation.TryGetValue(key, out string previous);
            current.TryGetValue(key, out string value);
            if (!string.Equals(previous, value, StringComparison.Ordinal))
                changes[key] = value ?? "null";
        }

        state.AgentSequence = sequence;
        state.AgentChangeJournal ??= new List<AgentChangeRecord>();
        state.AgentChangeJournal.Add(new AgentChangeRecord { Sequence = sequence, Changes = changes });
        while (state.AgentChangeJournal.Count > AgentJournalCapacity)
            state.AgentChangeJournal.RemoveAt(0);
        agentObservation = current;
    }

    private static bool DictionariesEqual(IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left == null || right == null || left.Count != right.Count)
            return false;
        foreach (KeyValuePair<string, string> pair in left)
        {
            if (!right.TryGetValue(pair.Key, out string value) ||
                !string.Equals(pair.Value, value, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private Dictionary<string, string> BuildAgentObservationLocked()
    {
        AgentSnapshotResponse snapshot = BuildAgentSnapshotLocked("unknown-agent", 0, includeBuild: false);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["phase"] = JsonValue(snapshot.Phase),
            ["generation"] = JsonValue(snapshot.Generation),
            ["targetGeneration"] = JsonValue(snapshot.TargetGeneration),
            ["acceptedProfile"] = JsonValue(snapshot.AcceptedProfile),
            ["pendingProfile"] = JsonValue(snapshot.PendingProfile),
            ["leaseAvailability"] = JsonValue(snapshot.RequestingAgentLease.State),
            ["maintenance"] = JsonValue(snapshot.Maintenance),
            ["quicktest"] = JsonValue(snapshot.Quicktest),
            ["rimBridgeEndpoint"] = JsonValue(snapshot.RimBridgeEndpoint),
            ["companion"] = JsonValue(snapshot.Companion),
            ["crashIsolation"] = JsonValue(snapshot.CrashIsolation),
            ["failure"] = JsonValue(snapshot.Failure),
            ["nextAction"] = JsonValue(snapshot.NextAction),
            ["safeActions"] = JsonValue(snapshot.SafeActions),
            ["blockedActions"] = JsonValue(snapshot.BlockedActions),
            ["blockers"] = JsonValue(snapshot.Blockers)
        };
    }

    private static string JsonValue<T>(T value) =>
        JsonSerializer.Serialize(value, CoordinatorSerialization.JsonOptions);

    private int Agent(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit, Func<bool> connected)
    {
        if (arguments == null || arguments.Count == 0)
        {
            emit("Usage: DevBridge.cmd agent capabilities|plan|build-plan|snapshot|delta|wait-event --json");
            return 2;
        }

        string operation = arguments[0]?.Trim().ToLowerInvariant();
        switch (operation)
        {
            case "capabilities":
                request.AgentResponse = new AgentCapabilitiesResponse();
                return 0;
            case "plan":
                if (!TryGetRecipeId(arguments, out string recipeId, out string planErrorCode,
                        out string planError))
                {
                    request.AgentResponse = new AgentRecipePlanResponse
                    {
                        ExitCode = 2,
                        ErrorCode = planErrorCode,
                        Error = planError,
                        NextAction = "inspect-evidence"
                    };
                    return 2;
                }
                RecipePlanData plan = BuildRecipePlan(recipeId, request);
                request.AgentResponse = AgentRecipePlanResponse.From(plan,
                    plan.ErrorCode == null ? 0 : 4);
                return request.AgentResponse.ExitCode;
            case "build-plan":
                if (arguments.Skip(1).Any(value =>
                        !string.Equals(value, "--json", StringComparison.OrdinalIgnoreCase)))
                {
                    request.AgentResponse = new AgentBuildPlanResponse
                    {
                        ExitCode = 2,
                        NextAction = "inspect-evidence",
                        CoordinatorBuild = AgentBuildIdentity.From(RunningBuildIdentity),
                        CoordinatorBuildMatchesPublished = CoordinatorBuildMatchesPublished,
                        ComponentBuilds = BuildComponentBuildSnapshotLocked()
                    };
                    return 2;
                }
                lock (gate)
                {
                    AgentSnapshotResponse snapshot = BuildAgentSnapshotLocked(
                        request.Agent, request.ClientProcessId, includeBuild: true);
                    request.AgentResponse = new AgentBuildPlanResponse
                    {
                        CoordinatorBuild = snapshot.CoordinatorBuild,
                        CoordinatorBuildMatchesPublished = snapshot.CoordinatorBuildMatchesPublished,
                        ComponentBuilds = snapshot.ComponentBuilds,
                        NextAction = snapshot.NextAction
                    };
                    return 0;
                }
            case "snapshot":
                lock (gate)
                {
                    if (!persistedStateLoadBlocked)
                        SynchronizeLocked();
                }
                return 0;
            case "delta":
                return AgentDelta(arguments, request);
            case "wait-event":
                return AgentWaitEvent(arguments, request, connected);
            default:
                emit("Usage: DevBridge.cmd agent capabilities|plan|build-plan|snapshot|delta|wait-event --json");
                return 2;
        }
    }

    private static bool TryGetRecipeId(IReadOnlyList<string> arguments, out string recipeId,
        out string errorCode, out string error)
    {
        recipeId = null;
        errorCode = null;
        error = null;
        for (int index = 1; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(arguments[index], "--recipe", StringComparison.OrdinalIgnoreCase) ||
                ++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) ||
                recipeId != null)
            {
                errorCode = "AGENT_PLAN_USAGE";
                error = "agent plan requires exactly one --recipe <id>.";
                return false;
            }
            recipeId = arguments[index].Trim();
        }
        if (recipeId == null)
        {
            errorCode = "AGENT_PLAN_USAGE";
            error = "agent plan requires exactly one --recipe <id>.";
            return false;
        }
        return true;
    }

    private int AgentDelta(IReadOnlyList<string> arguments, BridgeRequest request)
    {
        if (!TryParseAgentCursor(arguments, out long since, out string epoch,
                out string errorCode, out string error))
        {
            request.AgentResponse = AgentDeltaError(epoch, since, errorCode, error);
            return request.AgentResponse is AgentDeltaResponse response ? response.ExitCode : 4;
        }

        lock (gate)
        {
            if (!persistedStateLoadBlocked)
                SynchronizeLocked();
            request.AgentResponse = BuildAgentDeltaLocked(since, epoch, request.Agent, request.ClientProcessId);
            return request.AgentResponse is AgentDeltaResponse response ? response.ExitCode : 4;
        }
    }

    private int AgentWaitEvent(IReadOnlyList<string> arguments, BridgeRequest request,
        Func<bool> connected)
    {
        if (!TryParseAgentWaitOptions(arguments, out AgentWaitOptions options,
                out string errorCode, out string error))
        {
            request.AgentResponse = new AgentWaitEventResponse
            {
                Result = "error",
                Epoch = ReadAgentEpoch(),
                ErrorCode = errorCode,
                Error = error,
                ExitCode = 2,
                NextAction = "inspect-evidence"
            };
            return 2;
        }

        lock (gate)
        {
            if (!persistedStateLoadBlocked)
                SynchronizeLocked();
            AgentWaiterCountForTesting++;
            try
            {
                request.AgentResponse = WaitForAgentEventLocked(options, request.Agent,
                    request.ClientProcessId, connected);
                return request.AgentResponse is AgentWaitEventResponse response ? response.ExitCode : 4;
            }
            finally
            {
                AgentWaiterCountForTesting--;
            }
        }
    }

    private string ReadAgentEpoch()
    {
        lock (gate)
            return state?.AgentEpoch;
    }

    private sealed class AgentWaitOptions
    {
        internal long Since { get; init; }
        internal string Epoch { get; init; }
        internal HashSet<string> Until { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        internal TimeSpan Timeout { get; init; }
    }

    private static bool TryParseAgentCursor(IReadOnlyList<string> arguments,
        out long since, out string epoch, out string errorCode, out string error)
    {
        since = 0;
        epoch = null;
        errorCode = null;
        error = null;
        Dictionary<string, string> values = ParseAgentOptions(arguments, 1, out errorCode, out error);
        if (values == null)
            return false;
        if ((!values.TryGetValue("since-seq", out string sinceText) &&
             !values.TryGetValue("since", out sinceText)) ||
            !long.TryParse(sinceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out since) ||
            since < 0)
        {
            errorCode = "AGENT_SEQUENCE_INVALID";
            error = "--since-seq must be a non-negative integer.";
            return false;
        }
        if (!values.TryGetValue("epoch", out epoch) || string.IsNullOrWhiteSpace(epoch) ||
            epoch.Length > AgentMaxArgumentLength)
        {
            errorCode = "AGENT_EPOCH_INVALID";
            error = "--epoch is required and must be bounded.";
            return false;
        }
        return true;
    }

    private static bool TryParseAgentWaitOptions(IReadOnlyList<string> arguments,
        out AgentWaitOptions options, out string errorCode, out string error)
    {
        options = null;
        Dictionary<string, string> values = ParseAgentOptions(arguments, 1, out errorCode, out error);
        if (values == null)
            return false;
        if ((!values.TryGetValue("since-seq", out string sinceText) &&
             !values.TryGetValue("since", out sinceText)) ||
            !long.TryParse(sinceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long since) ||
            since < 0)
        {
            errorCode = "AGENT_SEQUENCE_INVALID";
            error = "--since-seq must be a non-negative integer.";
            return false;
        }
        if (!values.TryGetValue("epoch", out string epoch) || string.IsNullOrWhiteSpace(epoch) ||
            epoch.Length > AgentMaxArgumentLength)
        {
            errorCode = "AGENT_EPOCH_INVALID";
            error = "--epoch is required and must be bounded.";
            return false;
        }

        TimeSpan timeout = AgentDefaultWait;
        if (values.TryGetValue("timeout-seconds", out string timeoutText))
        {
            if (!double.TryParse(timeoutText, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double seconds) || seconds < 0 || seconds > AgentMaxWait.TotalSeconds)
            {
                errorCode = "AGENT_TIMEOUT_INVALID";
                error = "--timeout-seconds must be between 0 and " +
                    AgentMaxWait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + ".";
                return false;
            }
            timeout = TimeSpan.FromSeconds(seconds);
        }
        else if (values.TryGetValue("timeout-ms", out string timeoutMillisecondsText))
        {
            if (!double.TryParse(timeoutMillisecondsText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double milliseconds) || milliseconds < 0 ||
                milliseconds > AgentMaxWait.TotalMilliseconds)
            {
                errorCode = "AGENT_TIMEOUT_INVALID";
                error = "--timeout-ms must be between 0 and " +
                    AgentMaxWait.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + ".";
                return false;
            }
            timeout = TimeSpan.FromMilliseconds(milliseconds);
        }

        HashSet<string> until = new(StringComparer.OrdinalIgnoreCase);
        if (values.TryGetValue("until", out string untilText) && !string.IsNullOrWhiteSpace(untilText))
        {
            foreach (string value in untilText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string normalized = value.ToLowerInvariant();
                if (!IsKnownAgentCondition(normalized))
                {
                    errorCode = "AGENT_UNTIL_INVALID";
                    error = "Unsupported --until condition: " + value + ".";
                    return false;
                }
                until.Add(normalized);
            }
        }

        options = new AgentWaitOptions { Since = since, Epoch = epoch, Until = until, Timeout = timeout };
        return true;
    }

    private static Dictionary<string, string> ParseAgentOptions(IReadOnlyList<string> arguments,
        int start, out string errorCode, out string error)
    {
        errorCode = null;
        error = null;
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = start; index < (arguments?.Count ?? 0); index++)
        {
            string argument = arguments[index] ?? string.Empty;
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                errorCode = "AGENT_ARGUMENT_INVALID";
                error = "Agent options must use --name value syntax.";
                return null;
            }
            string option = argument.Substring(2);
            int equals = option.IndexOf('=');
            string name = equals >= 0 ? option.Substring(0, equals) : option;
            string value = equals >= 0 ? option.Substring(equals + 1) : null;
            if (string.IsNullOrWhiteSpace(name) || name.Length > AgentMaxArgumentLength)
            {
                errorCode = "AGENT_ARGUMENT_INVALID";
                error = "Agent option name is invalid.";
                return null;
            }
            if (value == null)
            {
                if (++index >= arguments.Count)
                {
                    errorCode = "AGENT_ARGUMENT_MISSING";
                    error = "Missing value for --" + name + ".";
                    return null;
                }
                value = arguments[index];
            }
            if (value == null || value.Length > AgentMaxArgumentLength)
            {
                errorCode = "AGENT_ARGUMENT_INVALID";
                error = "Value for --" + name + " is too long.";
                return null;
            }
            values[name] = value;
        }
        return values;
    }

    private static bool IsKnownAgentCondition(string value) => value switch
    {
        "ready" or "failed" or "error" or "stopped" or "loading" or "restarting" or
        "maintenance" or "quicktest-ready" or "changed" => true,
        _ => false
    };

    private AgentDeltaResponse BuildAgentDeltaLocked(long since, string epoch,
        string agent, int clientProcessId)
    {
        if (persistedStateLoadBlocked)
            return AgentDeltaError(epoch, since, "PERSISTED_STATE_UNAVAILABLE",
                state.Error ?? "The persisted coordinator state is unavailable.");
        if (!string.Equals(epoch, state.AgentEpoch, StringComparison.Ordinal))
            return AgentDeltaError(state.AgentEpoch, since, "AGENT_EPOCH_MISMATCH",
                "The requested cursor belongs to a different coordinator epoch.");
        if (since > state.AgentSequence)
            return AgentDeltaError(state.AgentEpoch, since, "AGENT_SEQUENCE_AHEAD",
                "The requested sequence is newer than the coordinator sequence.");
        if (since < state.AgentSequence &&
            (state.AgentChangeJournal == null || state.AgentChangeJournal.Count == 0 ||
             since < state.AgentChangeJournal[0].Sequence - 1))
            return AgentDeltaError(state.AgentEpoch, since, "AGENT_DELTA_EXPIRED",
                "The requested delta window has expired; request a fresh agent snapshot.");

        Dictionary<string, JsonElement> delta = BuildDeltaValuesLocked(since, agent, clientProcessId);
        AgentSnapshotResponse current = BuildAgentSnapshotLocked(agent, clientProcessId, includeBuild: false);
        return new AgentDeltaResponse
        {
            Epoch = state.AgentEpoch,
            FromSeq = since,
            ToSeq = state.AgentSequence,
            Delta = delta,
            NextAction = delta.Count == 0 ? null : current.NextAction,
            ExitCode = 0
        };
    }

    private AgentDeltaResponse AgentDeltaError(string epoch, long since, string code, string error)
    {
        return new AgentDeltaResponse
        {
            Epoch = epoch ?? state?.AgentEpoch,
            FromSeq = since,
            ToSeq = state?.AgentSequence ?? 0,
            ErrorCode = code,
            Error = Bounded(error, 256),
            ExitCode = 4
        };
    }

    private Dictionary<string, JsonElement> BuildDeltaValuesLocked(long since,
        string agent, int clientProcessId)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        foreach (AgentChangeRecord record in state.AgentChangeJournal ?? new List<AgentChangeRecord>())
        {
            if (record == null || record.Sequence <= since)
                continue;
            foreach (KeyValuePair<string, string> change in record.Changes ?? new Dictionary<string, string>())
            {
                string name = change.Key == "leaseAvailability"
                    ? "requestingAgentLease" : change.Key;
                string value = change.Value ?? "null";
                try
                {
                    if (name == "requestingAgentLease")
                    {
                        // The journal tracks aggregate lease availability. The
                        // requester-specific lease is resolved at response time.
                        value = JsonValue(BuildAgentLeaseLocked(agent, clientProcessId));
                    }
                    using JsonDocument document = JsonDocument.Parse(value);
                    result[name] = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    result[name] = JsonDocument.Parse("null").RootElement.Clone();
                }
            }
        }
        return result;
    }

    private AgentWaitEventResponse WaitForAgentEventLocked(AgentWaitOptions options,
        string agent, int clientProcessId, Func<bool> connected)
    {
        if (persistedStateLoadBlocked)
            return AgentWaitError(options, "PERSISTED_STATE_UNAVAILABLE",
                state.Error ?? "The persisted coordinator state is unavailable.");
        if (!string.Equals(options.Epoch, state.AgentEpoch, StringComparison.Ordinal))
            return AgentWaitError(options, "AGENT_EPOCH_MISMATCH",
                "The requested cursor belongs to a different coordinator epoch.");
        if (options.Since > state.AgentSequence)
            return AgentWaitError(options, "AGENT_SEQUENCE_AHEAD",
                "The requested sequence is newer than the coordinator sequence.");
        if (options.Since < state.AgentSequence &&
            (state.AgentChangeJournal == null || state.AgentChangeJournal.Count == 0 ||
             options.Since < state.AgentChangeJournal[0].Sequence - 1))
            return AgentWaitError(options, "AGENT_DELTA_EXPIRED",
                "The requested delta window has expired; request a fresh agent snapshot.");

        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (connected != null && !connected())
                return AgentWaitResult(options, "shutdown", agent, clientProcessId, exitCode: 0);

            AgentSnapshotResponse current = BuildAgentSnapshotLocked(agent, clientProcessId, includeBuild: false);
            bool changed = state.AgentSequence > options.Since;
            bool conditionMet = options.Until.Count == 0
                ? changed
                : MatchesAgentCondition(options.Until, current, options.Since);
            if (conditionMet)
            {
                return AgentWaitResult(options,
                    options.Until.Count == 0 ? "changed" : "condition-met",
                    agent, clientProcessId, exitCode: 0);
            }

            if (ShutdownRequested)
                return AgentWaitResult(options, "shutdown", agent, clientProcessId, exitCode: 0);

            TimeSpan remaining = options.Timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return AgentWaitResult(options, "timeout", agent, clientProcessId, exitCode: 0);

            Monitor.Wait(gate, remaining);

            if (options.Since < state.AgentSequence &&
                (state.AgentChangeJournal == null || state.AgentChangeJournal.Count == 0 ||
                 options.Since < state.AgentChangeJournal[0].Sequence - 1))
                return AgentWaitError(options, "AGENT_DELTA_EXPIRED",
                    "The requested delta window expired while waiting; request a fresh agent snapshot.");
        }
    }

    private AgentWaitEventResponse AgentWaitResult(AgentWaitOptions options, string result,
        string agent, int clientProcessId, int exitCode)
    {
        AgentSnapshotResponse current = BuildAgentSnapshotLocked(agent, clientProcessId, includeBuild: false);
        return new AgentWaitEventResponse
        {
            Result = result,
            Epoch = state.AgentEpoch,
            FromSeq = options.Since,
            ToSeq = state.AgentSequence,
            Delta = options.Since <= state.AgentSequence
                ? BuildDeltaValuesLocked(options.Since, agent, clientProcessId)
                : new Dictionary<string, JsonElement>(),
            NextAction = result == "shutdown" ? "reconnect" : current.NextAction,
            ExitCode = exitCode
        };
    }

    private AgentWaitEventResponse AgentWaitError(AgentWaitOptions options, string code, string error)
    {
        return new AgentWaitEventResponse
        {
            Result = "error",
            Epoch = state?.AgentEpoch ?? options.Epoch,
            FromSeq = options.Since,
            ToSeq = state?.AgentSequence ?? 0,
            ErrorCode = code,
            Error = Bounded(error, 256),
            NextAction = "inspect-evidence",
            ExitCode = 4
        };
    }

    private static bool MatchesAgentCondition(ISet<string> conditions, AgentSnapshotResponse current,
        long since)
    {
        foreach (string condition in conditions)
        {
            if (condition == "changed" && current.Sequence > since)
                return true;
            if (condition == "ready" && current.Phase == nameof(BridgePhase.READY))
                return true;
            if ((condition == "failed" || condition == "error") &&
                (current.Phase == nameof(BridgePhase.ERROR) || current.Failure != null))
                return true;
            if (condition == "stopped" && current.Phase == nameof(BridgePhase.STOPPED))
                return true;
            if (condition == "loading" && current.Phase == nameof(BridgePhase.LOADING))
                return true;
            if (condition == "restarting" &&
                (current.Phase == nameof(BridgePhase.RESTARTING) || current.Phase == nameof(BridgePhase.WAITING_FOR_BRIDGE)))
                return true;
            if (condition == "maintenance" && current.Maintenance.State == "ready")
                return true;
            if (condition == "quicktest-ready" && current.Quicktest.State == "ready")
                return true;
        }
        return false;
    }

    private AgentSnapshotResponse BuildAgentSnapshotLocked(string agent, int clientProcessId,
        bool includeBuild)
    {
        agent = string.IsNullOrWhiteSpace(agent) ? "unknown-agent" : agent.Trim();
        AgentQuicktestSummary quicktest = BuildQuicktestSummaryLocked();
        AgentPlan plan = BuildAgentPlanLocked(agent, clientProcessId, quicktest);
        return new AgentSnapshotResponse
        {
            Epoch = state.AgentEpoch,
            RuntimeSlotId = runtimeSlotId,
            Sequence = state.AgentSequence,
            Generation = state.Generation,
            TargetGeneration = state.RestartPending || state.TargetGeneration > state.Generation
                ? state.TargetGeneration : null,
            Phase = state.Phase.ToString(),
            CoordinatorBuild = includeBuild ? AgentBuildIdentity.From(RunningBuildIdentity) : null,
            CoordinatorBuildMatchesPublished = includeBuild ? CoordinatorBuildMatchesPublished : null,
            ComponentBuilds = includeBuild ? BuildComponentBuildSnapshotLocked() : null,
            AcceptedProfile = BuildAcceptedProfileLocked(),
            PendingProfile = BuildPendingProfileLocked(),
            RequestingAgentLease = BuildAgentLeaseLocked(agent, clientProcessId),
            Maintenance = new AgentMaintenanceSummary
            {
                State = state.MaintenanceReady ? "ready" : state.SessionDirty ? "dirty" : "normal",
                SessionDirty = state.SessionDirty,
                ModsConfigOwnership = Bounded(state.ModsConfigOwnership, 48)
            },
            Quicktest = quicktest,
            RimBridgeEndpoint = BuildRimBridgeSummaryLocked(),
            Companion = BuildCompanionSummaryLocked(),
            CrashIsolation = BuildCrashIsolationSummaryLocked(),
            Failure = BuildFailureSummaryLocked(quicktest),
            NextAction = plan.NextAction,
            SafeActions = plan.SafeActions,
            BlockedActions = plan.BlockedActions,
            Blockers = plan.Blockers,
            ErrorCode = persistedStateLoadBlocked ? "PERSISTED_STATE_UNAVAILABLE" : null,
            Error = persistedStateLoadBlocked ? Bounded(state.Error, 256) : null
        };
    }

    private AgentComponentBuildSnapshot BuildComponentBuildSnapshotLocked()
    {
        string modPath = Path.Combine(root, "1.6", "Assemblies", "DevBridge2.dll");
        string bridgeToolsPath = ExpectedBridgeToolsPathLocked();
        DevBridgeBuildIdentity modBuild = DevBridgeBuildIdentity.FromAssemblyPath(modPath);
        DevBridgeBuildIdentity bridgeToolsBuild = DevBridgeBuildIdentity.FromAssemblyPath(bridgeToolsPath);
        CoordinatorBuildIdentity published = PublishedCoordinatorBuildIdentity;

        bool? modMatchesPublished = BuildMatches(published, modBuild);
        bool? bridgeToolsMatchesPublished = BuildMatches(published, bridgeToolsBuild);
        string requiredRefresh = "none";
        if (CoordinatorBuildMatchesPublished == false)
            requiredRefresh = "coordinator";
        else if (modMatchesPublished == false)
            requiredRefresh = "rimworld";
        else if (bridgeToolsMatchesPublished == false)
            requiredRefresh = "unknown";

        return new AgentComponentBuildSnapshot
        {
            Mod = new AgentComponentBuildSummary
            {
                Build = AgentBuildIdentity.From(modBuild),
                ArtifactSha256 = Sha256ForPath(modPath),
                BuildMatchesPublished = modMatchesPublished,
                LoadedStatus = File.Exists(modPath) ? "unknown-not-proven" : "not-deployed"
            },
            BridgeTools = new AgentComponentBuildSummary
            {
                Build = AgentBuildIdentity.From(bridgeToolsBuild),
                ArtifactSha256 = Sha256ForPath(bridgeToolsPath),
                BuildMatchesPublished = bridgeToolsMatchesPublished,
                LoadedStatus = File.Exists(bridgeToolsPath) ? "unknown-not-proven" : "not-deployed"
            },
            RequiredRefresh = requiredRefresh
        };
    }

    private string ExpectedBridgeToolsPathLocked()
    {
        DirectoryInfo modRoot = new(root);
        DirectoryInfo modsRoot = modRoot.Parent;
        DirectoryInfo rimWorldRoot = modsRoot?.Parent;
        if (modsRoot == null || rimWorldRoot == null ||
            !string.Equals(modsRoot.Name, "Mods", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.Combine(rimWorldRoot.FullName, "BridgeTools", modRoot.Name,
            "DevBridge2.BridgeTools.dll");
    }

    private static bool? BuildMatches(DevBridgeBuildIdentity published,
        DevBridgeBuildIdentity deployed)
    {
        if (published == null || deployed == null)
            return null;
        return string.Equals(published.ProductVersion, deployed.ProductVersion, StringComparison.Ordinal) &&
            string.Equals(published.SourceRevision, deployed.SourceRevision, StringComparison.Ordinal) &&
            published.Dirty == deployed.Dirty &&
            string.Equals(published.BuildConfiguration, deployed.BuildConfiguration,
                StringComparison.Ordinal);
    }

    private static string Sha256ForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            using System.Security.Cryptography.SHA256 sha256 =
                System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(File.ReadAllBytes(path)));
        }
        catch
        {
            return null;
        }
    }

    private AgentProfileSummary BuildAcceptedProfileLocked()
    {
        List<string> projects = state.RequestedProjects ?? new List<string>();
        return new AgentProfileSummary
        {
            Mode = Bounded(state.LaunchProfileMode ?? state.ProfileMode, 48),
            ProjectCount = projects.Count,
            Projects = BoundedStrings(projects, AgentMaxProjectCount, out bool truncated),
            ProjectsTruncated = truncated,
            ResolvedPackageCount = (state.ResolvedProjectPackageIds ?? new List<string>()).Count,
            Fingerprint = Bounded(state.ProfileFingerprint, 128),
            BaselineFingerprint = Bounded(state.BaselineFingerprint, 128)
        };
    }

    private AgentPendingProfileSummary BuildPendingProfileLocked()
    {
        bool pending = state.RestartPending || state.AggregateFreezePending ||
            state.FrozenTargetGeneration > state.Generation ||
            !string.IsNullOrWhiteSpace(state.FrozenProfileFingerprint);
        if (!pending)
            return new AgentPendingProfileSummary { Pending = false };

        List<string> projects = state.FrozenRequestedProjects ?? new List<string>();
        return new AgentPendingProfileSummary
        {
            Pending = true,
            Generation = state.FrozenTargetGeneration > 0 ? state.FrozenTargetGeneration :
                state.TargetGeneration > 0 ? state.TargetGeneration : null,
            ProjectCount = projects.Count,
            Projects = BoundedStrings(projects, AgentMaxProjectCount, out _),
            Fingerprint = Bounded(state.FrozenProfileFingerprint, 128),
            BaselineFingerprint = Bounded(state.FrozenBaselineFingerprint, 128)
        };
    }

    private AgentLeaseSummary BuildAgentLeaseLocked(string agent, int clientProcessId)
    {
        TestLease owned = (state.Leases ?? new List<TestLease>()).FirstOrDefault(lease =>
            lease != null && string.Equals(lease.Agent, agent, StringComparison.Ordinal) &&
            (clientProcessId <= 0 || lease.ClientProcessId == clientProcessId));
        if (owned != null)
        {
            return new AgentLeaseSummary
            {
                State = "held",
                LeaseId = Bounded(owned.Id, 96),
                Generation = owned.Generation,
                ExpiresUtc = LeaseActivityUtc(owned).Add(options.LeaseDuration)
            };
        }

        bool otherLease = (state.Leases ?? new List<TestLease>()).Any(lease => lease != null);
        return new AgentLeaseSummary { State = otherLease ? "blocked" : "available" };
    }

    private AgentQuicktestSummary BuildQuicktestSummaryLocked()
    {
        try
        {
            if (File.Exists(readinessPath))
            {
                ReadinessRecord readiness = JsonSerializer.Deserialize<ReadinessRecord>(
                    File.ReadAllText(readinessPath), CoordinatorSerialization.JsonOptions);
                int expectedGeneration = state.TargetGeneration > 0 ? state.TargetGeneration : state.Generation;
                if (readiness != null && readiness.Generation == expectedGeneration &&
                    readiness.ProcessId == state.ProcessId &&
                    string.Equals(readiness.LaunchId, state.LaunchId, StringComparison.Ordinal))
                    return new AgentQuicktestSummary { State = "ready" };
            }
        }
        catch
        {
            // The authoritative lifecycle state remains usable when an
            // optional readiness artifact is unavailable or malformed.
        }

        try
        {
            if (File.Exists(quicktestFailurePath))
            {
                QuicktestFailureRecord failure = JsonSerializer.Deserialize<QuicktestFailureRecord>(
                    File.ReadAllText(quicktestFailurePath), CoordinatorSerialization.JsonOptions);
                int expectedGeneration = state.TargetGeneration > 0 ? state.TargetGeneration : state.Generation;
                if (failure != null && failure.Generation == expectedGeneration &&
                    string.Equals(failure.LaunchId, state.LaunchId, StringComparison.Ordinal))
                    return new AgentQuicktestSummary
                    {
                        State = "failed",
                        FailureCode = Bounded(failure.FailureCode ?? QuicktestFailureArtifact.StableFailureCode, 96),
                        Evidence = "Runtime/quicktest-failure.json"
                    };
            }
        }
        catch
        {
            // See the readiness comment above.
        }

        string stateName = state.Phase switch
        {
            BridgePhase.LOADING or BridgePhase.RESTARTING or BridgePhase.WAITING_FOR_BRIDGE => "waiting",
            BridgePhase.ERROR when string.Equals(state.ErrorCode, QuicktestFailureArtifact.StableFailureCode,
                StringComparison.Ordinal) => "failed",
            _ => "idle"
        };
        return new AgentQuicktestSummary
        {
            State = stateName,
            FailureCode = stateName == "failed" ? QuicktestFailureArtifact.StableFailureCode : null,
            Evidence = stateName == "failed" ? "Runtime/state.json#terminalFailure" : null
        };
    }

    private AgentRimBridgeSummary BuildRimBridgeSummaryLocked()
    {
        RimBridgeIntegrationState bridge = state.RimBridge ?? new RimBridgeIntegrationState();
        string category = bridge.LifecycleState switch
        {
            RimBridgeLifecycleState.DISABLED => "disabled",
            RimBridgeLifecycleState.READY => "ready",
            RimBridgeLifecycleState.WAITING or RimBridgeLifecycleState.DISCOVERED => "waiting",
            RimBridgeLifecycleState.NOT_INSTALLED => "unavailable",
            RimBridgeLifecycleState.FAILED or RimBridgeLifecycleState.STALE => "failed",
            _ => "unknown"
        };
        return new AgentRimBridgeSummary
        {
            State = category,
            Mode = Bounded(bridge.ConfiguredMode, 24),
            ErrorCode = Bounded(bridge.ErrorCode, 96)
        };
    }

    private AgentCompanionSummary BuildCompanionSummaryLocked()
    {
        RimBridgeIntegrationState bridge = state.RimBridge ?? new RimBridgeIntegrationState();
        string category = bridge.CompanionVerified ? "verified" :
            bridge.CompanionAvailable ? "available" :
            string.Equals(bridge.CompanionErrorCode, RimBridgeIntegrationConstants.CompanionUnavailableCode,
                StringComparison.Ordinal) ? "unavailable" : "unknown";
        return new AgentCompanionSummary
        {
            State = category,
            Diagnostic = Bounded(RimBridgeCompanionDiagnostics.Code(bridge), 96)
        };
    }

    private AgentCrashIsolationSummary BuildCrashIsolationSummaryLocked()
    {
        if (!IsolationActiveLocked() || state.CrashIsolation == null)
            return null;
        CrashIsolationIncident incident = state.CrashIsolation;
        return new AgentCrashIsolationSummary
        {
            Active = true,
            IncidentId = Bounded(incident.IncidentId, 96),
            Stage = Bounded(incident.Stage, 48),
            Attempts = incident.Attempts?.Count ?? 0,
            LaunchesRemaining = Math.Max(0, incident.IsolationLaunchesRemaining),
            FailureCode = Bounded(incident.OriginalFailureCode ?? incident.CurrentAttemptFailureCode, 96)
        };
    }

    private AgentFailureSummary BuildFailureSummaryLocked(AgentQuicktestSummary quicktest)
    {
        CrashIsolationIncident incident = state.CrashIsolation;
        string code = IsolationActiveLocked() ? incident?.OriginalFailureCode : null;
        string phase = IsolationActiveLocked() ? incident?.OriginalFailurePhase : null;
        string evidence = IsolationActiveLocked() && !string.IsNullOrWhiteSpace(incident?.IncidentId)
            ? "Runtime/state.json#crashIsolation/" + Bounded(incident.IncidentId, 96)
            : null;
        code ??= state.TerminalFailureCode;
        phase ??= state.TerminalFailurePhase;
        if (string.IsNullOrWhiteSpace(code) && state.Phase == BridgePhase.ERROR)
            code = state.ErrorCode;
        if (string.IsNullOrWhiteSpace(code) && quicktest?.State == "failed")
            code = quicktest.FailureCode;
        if (string.IsNullOrWhiteSpace(code))
            return null;
        return new AgentFailureSummary
        {
            Code = Bounded(code, 96),
            Phase = Bounded(phase ?? state.Phase.ToString(), 48),
            Evidence = evidence ?? quicktest?.Evidence ?? "Runtime/state.json#terminalFailure",
            FailureFingerprint = Bounded(state.LatestFailureFingerprint, 96),
            SeenBefore = state.LatestFailureSeenBefore,
            Summary = Bounded(state.LatestFailureSummary, 256),
            EvidenceId = Bounded(state.LatestFailureEvidenceId, 64),
            DiagnosisReference = Bounded(state.LatestFailureDiagnosisReference, 160)
        };
    }

    private sealed class AgentPlan
    {
        internal string NextAction { get; init; }
        internal List<string> SafeActions { get; init; } = new();
        internal List<string> BlockedActions { get; init; } = new();
        internal List<string> Blockers { get; init; } = new();
    }

    private AgentPlan BuildAgentPlanLocked(string agent, int clientProcessId,
        AgentQuicktestSummary quicktest)
    {
        AgentLeaseSummary lease = BuildAgentLeaseLocked(agent, clientProcessId);
        bool leaseHeld = lease.State == "held";
        bool profileUnsafe = state.ExternalModsConfigMutation != null ||
            !string.IsNullOrWhiteSpace(state.ProfileConflict) ||
            !string.IsNullOrWhiteSpace(state.ProfileErrorCode);
        bool isolation = IsolationActiveLocked();
        bool rimBridgeBlocked = string.Equals(state.RimBridge?.ConfiguredMode, "required",
                StringComparison.OrdinalIgnoreCase) &&
            state.RimBridge?.LifecycleState != RimBridgeLifecycleState.READY;
        bool current = state.Phase == BridgePhase.READY && !state.RestartPending &&
            !state.RequiresNewProcess && !profileUnsafe && !isolation && !rimBridgeBlocked &&
            quicktest?.State != "failed";
        bool failed = state.Phase == BridgePhase.ERROR || state.TerminalFailureCode != null ||
            quicktest?.State == "failed";
        bool transitioning = state.RestartPending || state.Phase == BridgePhase.DRAINING ||
            state.Phase == BridgePhase.WAITING_FOR_BRIDGE || state.Phase == BridgePhase.RESTARTING ||
            state.Phase == BridgePhase.LOADING || state.Phase == BridgePhase.ISOLATING;

        List<string> blockers = new();
        if (profileUnsafe)
            blockers.Add(state.ExternalModsConfigMutation != null ? "external-mods-config" : "profile-conflict");
        if (isolation)
            blockers.Add("crash-isolation");
        if (rimBridgeBlocked)
            blockers.Add("rimbridge-not-ready");
        if (failed && !blockers.Contains("failure", StringComparer.Ordinal))
            blockers.Add(Bounded(state.ErrorCode ?? state.TerminalFailureCode ?? quicktest?.FailureCode ?? "failure", 96));
        if (!leaseHeld && (current || state.Phase == BridgePhase.STOPPED))
            blockers.Add("lease-not-held");
        if (state.MaintenanceReady)
            blockers.Add("maintenance");

        List<string> safe = new() { "wait-event", "inspect-evidence" };
        List<string> blocked = new();
        string next;
        if (isolation || transitioning)
            next = "wait-event";
        else if (profileUnsafe || rimBridgeBlocked || failed)
            next = "inspect-evidence";
        else if (state.MaintenanceReady)
            next = "publish-or-ensure-ready";
        else if (current)
            next = leaseHeld ? "run-tests" : "acquire-lease";
        else if (state.Phase == BridgePhase.STOPPED)
            next = leaseHeld ? "restart" : "acquire-lease";
        else
            next = "wait-event";

        if (state.MaintenanceReady && !profileUnsafe && !isolation && !failed)
            safe.Insert(0, "publish-or-ensure-ready");
        if (!leaseHeld && !profileUnsafe && !isolation && !failed &&
            (current || state.Phase == BridgePhase.STOPPED))
            safe.Insert(0, "acquire-lease");
        if (current && leaseHeld)
        {
            safe.Insert(0, "run-tests");
            safe.Insert(1, "restart");
        }
        else
        {
            blocked.Add("run-tests");
            blocked.Add("restart");
        }
        if (state.MaintenanceReady)
            blocked.Add("run-tests");
        if (profileUnsafe || isolation || rimBridgeBlocked || failed)
            blocked.Add("publish-or-ensure-ready");

        return new AgentPlan
        {
            NextAction = next,
            SafeActions = safe.Distinct(StringComparer.Ordinal).ToList(),
            BlockedActions = blocked.Distinct(StringComparer.Ordinal).ToList(),
            Blockers = blockers.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static List<string> BoundedStrings(IEnumerable<string> values, int maximum,
        out bool truncated)
    {
        List<string> source = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bounded(value, 96))
            .ToList();
        truncated = source.Count > maximum;
        return source.Take(maximum).ToList();
    }

    private static string Bounded(string value, int maximum) =>
        string.IsNullOrEmpty(value) || value.Length <= maximum ? value : value.Substring(0, maximum);

    internal AgentResponse CreateAgentJsonResponse(BridgeRequest request, int exitCode)
    {
        if (request.AgentResponse is AgentResponse response)
        {
            response.ExitCode = response.ExitCode == 0 && exitCode != 0 ? exitCode : response.ExitCode;
            return response;
        }

        string operation = request.Arguments?.FirstOrDefault()?.Trim().ToLowerInvariant();
        if (operation == "capabilities")
            return new AgentCapabilitiesResponse { ExitCode = exitCode };

        lock (gate)
        {
            if (!persistedStateLoadBlocked)
                SynchronizeLocked();
            AgentSnapshotResponse snapshot = BuildAgentSnapshotLocked(request.Agent,
                request.ClientProcessId, includeBuild: true);
            snapshot.ExitCode = exitCode;
            return snapshot;
        }
    }
}
