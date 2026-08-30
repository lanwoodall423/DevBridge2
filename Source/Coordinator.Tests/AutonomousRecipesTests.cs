using System.Text;
using System.Text.Json;

namespace DevBridge.Coordinator;

internal static partial class OfflineTests
{
    private static readonly string SmokeRecipe = """
        {
          "schemaVersion": "devbridge-test-recipe/v1",
          "id": "quicktest-smoke",
          "description": "Verify the Dev Quicktest readiness evidence.",
          "projects": [],
          "inputs": { "quicktest": true },
          "requiresReady": true,
          "success": { "quicktestReady": true },
          "budget": { "timeoutSeconds": 300, "maxRimWorldLaunches": 1, "maxRecipeAttempts": 1 }
        }
        """;

    private static void TestRecipeParsingAndDiscovery()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        WriteRecipe(fixture, "quicktest-smoke", SmokeRecipe);

        Assert(RecipeCatalog.TryLoad(fixture.Root, out RecipeCatalog catalog,
                   out string errorCode, out string error) && catalog.Recipes.Count == 1 &&
               catalog.TryGet("quicktest-smoke", out TestRecipeDefinition recipe) &&
               recipe.Success.QuicktestReady && errorCode == null && error == null,
            "the repository recipe must parse into the bounded owned model");

        RecipeResponse list = ExecuteRecipe(fixture, "list");
        Assert(list is RecipeListResponse listResponse && listResponse.Recipes.Count == 1 &&
               listResponse.Recipes[0].Id == "quicktest-smoke",
            "recipe list must be compact and deterministic");

        RecipeResponse show = ExecuteRecipe(fixture, "show", "quicktest-smoke");
        Assert(show is RecipeShowResponse showResponse && showResponse.Recipe.Id == "quicktest-smoke" &&
               showResponse.Recipe.Operations.Count == 0,
            "recipe show must expose the parsed recipe without execution fields");

        RecipeResponse unknown = ExecuteRecipe(fixture, "show", "missing-recipe");
        Assert(unknown is RecipeShowResponse unknownShow && unknown.ExitCode != 0 &&
               unknownShow.ErrorCode == "TEST_RECIPE_NOT_FOUND",
            "unknown recipes must return a stable compact error");

        RecipeResponse correlated = ExecuteRecipe(fixture, "run", "quicktest-smoke",
            "--workflow-id", "rw-offline-correlation", "--max-rimworld-launches", "0");
        RecipeRunResponse correlatedRun = correlated as RecipeRunResponse;
        Assert(correlatedRun != null &&
               correlatedRun.WorkflowId == "rw-offline-correlation" &&
               !string.IsNullOrWhiteSpace(correlatedRun.RunId) &&
               correlatedRun.RunId.StartsWith("run-", StringComparison.Ordinal),
            "recipe runs must preserve workflow correlation and expose a bounded run identity");

        string correlatedJson = JsonSerializer.Serialize(correlatedRun, Program.JsonOptions);
        using JsonDocument correlatedDocument = JsonDocument.Parse(correlatedJson);
        JsonElement correlatedRoot = correlatedDocument.RootElement;
        string[] requiredFields = { "success", "recipe", "runId", "workflowId", "generation", "leaseId", "operations" };
        Assert(requiredFields.All(field => correlatedRoot.TryGetProperty(field, out _)) &&
               correlatedRoot.GetProperty("operations").ValueKind == JsonValueKind.Array,
            "recipe run JSON must retain the cross-stack required fields");

        RecipeRunResponse emptyResponse = new()
        {
            Recipe = "quicktest-smoke",
            FinalNextAction = "inspect-evidence",
            Budget = new RecipeBudgetResult()
        };
        using JsonDocument emptyDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(emptyResponse, Program.JsonOptions));
        JsonElement emptyRoot = emptyDocument.RootElement;
        Assert(requiredFields.All(field => emptyRoot.TryGetProperty(field, out _)) &&
               emptyRoot.GetProperty("runId").ValueKind == JsonValueKind.Null &&
               emptyRoot.GetProperty("workflowId").ValueKind == JsonValueKind.Null &&
               emptyRoot.GetProperty("leaseId").ValueKind == JsonValueKind.Null &&
               emptyRoot.GetProperty("operations").ValueKind == JsonValueKind.Array,
            "recipe run JSON must retain required fields when values are empty");

        WriteRecipe(fixture, "quicktest-smoke", SmokeRecipe.Replace(
            "\"schemaVersion\": \"devbridge-test-recipe/v1\"",
            "\"schemaVersion\": \"devbridge-test-recipe/v9\"", StringComparison.Ordinal));
        Assert(!RecipeCatalog.TryLoad(fixture.Root, out _, out errorCode, out _) &&
               errorCode == "TEST_RECIPE_SCHEMA_UNSUPPORTED",
            "unsupported recipe schemas must fail closed");

        WriteRecipe(fixture, "quicktest-smoke", SmokeRecipe.Replace(
            "\"budget\":", "\"shell\": \"powershell\", \"budget\":", StringComparison.Ordinal));
        Assert(!RecipeCatalog.TryLoad(fixture.Root, out _, out errorCode, out _) &&
               errorCode == "TEST_RECIPE_UNSUPPORTED_FIELD",
            "shell and arbitrary command injection fields must be rejected");
    }

    private static void TestExplicitProjectRecipeFile()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        string projectRecipeDirectory = Path.Combine(Path.GetTempPath(),
            "devbridge-project-recipe-" + Guid.NewGuid().ToString("N"));
        string projectRecipePath = Path.Combine(projectRecipeDirectory, "quicktest-smoke.json");
        try
        {
            Directory.CreateDirectory(projectRecipeDirectory);
            File.WriteAllText(projectRecipePath, SmokeRecipe);
            BridgeRequest request = Request("test", "recipe-agent", 991,
                "recipe", "show", "quicktest-smoke", "--recipe-file", projectRecipePath);
            request.Json = true;
            List<string> output = new();
            int exitCode = fixture.State.Execute(request, output.Add, () => true);
            RecipeResponse show = fixture.State.CreateRecipeJsonResponse(request, exitCode);
            Assert(show is RecipeShowResponse response &&
                   response.ExitCode == 0 &&
                   response.Recipe?.Id == "quicktest-smoke",
                "explicit project-owned recipe files must bypass the central catalog (type=" +
                    show.GetType().Name + ", errorCode=" +
                    (show is RecipeShowResponse showFailure ? showFailure.ErrorCode :
                        show is RecipeListResponse listFailure ? listFailure.ErrorCode : null) +
                    ", error=" + (show is RecipeShowResponse showError ? showError.Error :
                        show is RecipeListResponse listError ? listError.Error : null) +
                    ", output=" + string.Join("|", output) + ")");
        }
        finally
        {
            try { Directory.Delete(projectRecipeDirectory, recursive: true); } catch { }
        }
    }

    private static void TestV2RecipeContractIsExplicitAndBounded()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        string v2 = """
            {
              "schemaVersion": "devbridge-test-recipe/v2",
              "id": "behavioral-fixture",
              "description": "Bounded behavioral fixture.",
              "projects": [],
              "inputs": {},
              "requiresReady": true,
              "allowInGameMutation": true,
              "success": { "quicktestReady": true },
              "operations": [
                {
                  "tool": "rimworld/fixture_mutate",
                  "arguments": { "value": "ok" },
                  "expect": {
                    "success": true,
                    "assertions": [
                      { "pointer": "/value", "equals": "ok" },
                      { "pointer": "/count", "greaterThan": 0 }
                    ]
                  }
                }
              ]
            }
            """;
        WriteRecipe(fixture, "behavioral-fixture", v2);
        Assert(RecipeCatalog.TryLoad(fixture.Root, out RecipeCatalog catalog,
                   out string errorCode, out string error) &&
               catalog.TryGet("behavioral-fixture", out TestRecipeDefinition recipe) &&
               recipe.SchemaVersion == "devbridge-test-recipe/v2" &&
               recipe.AllowsInGameMutation && recipe.Operations.Count == 1 &&
               recipe.Operations[0].Expectation.ExpectedSuccess &&
               recipe.Operations[0].Expectation.Assertions.Count == 2 &&
               errorCode == null && error == null,
            "v2 must parse its explicit mutation opt-in and bounded assertions");

        string noOptIn = v2.Replace("\"allowInGameMutation\": true,", "", StringComparison.Ordinal);
        WriteRecipe(fixture, "behavioral-fixture", noOptIn);
        Assert(!RecipeCatalog.TryLoad(fixture.Root, out _, out errorCode, out _) &&
               errorCode == "TEST_RECIPE_IN_GAME_MUTATION_OPT_IN_REQUIRED",
            "v2 in-game mutation must require an explicit opt-in");

        string v1Mutation = v2.Replace("devbridge-test-recipe/v2", "devbridge-test-recipe/v1",
            StringComparison.Ordinal);
        WriteRecipe(fixture, "behavioral-fixture", v1Mutation);
        Assert(!RecipeCatalog.TryLoad(fixture.Root, out _, out errorCode, out _) &&
               errorCode == "TEST_RECIPE_UNSUPPORTED_FIELD",
            "v1 must not acquire v2 mutation fields silently");

        string profileMutation = v2.Replace("rimworld/fixture_mutate", "rimworld/set_mod_enabled",
            StringComparison.Ordinal);
        WriteRecipe(fixture, "behavioral-fixture", profileMutation);
        Assert(!RecipeCatalog.TryLoad(fixture.Root, out _, out errorCode, out _) &&
               errorCode == "TEST_RECIPE_RIMBRIDGE_FORBIDDEN",
            "profile mutation must remain forbidden in v2 recipes");
    }

    private static void TestRecipeRouteFailurePreservesDiagnostic()
    {
        RecipeOperationDefinition operation = new()
        {
            ToolName = "rimworld/get_game_state",
            Expectation = new RecipeOperationExpectation { ExpectedSuccess = true }
        };
        RimBridgeRouteResult route = new()
        {
            ToolName = operation.ToolName,
            Success = false,
            ErrorCode = "RIMBRIDGE_PROTOCOL_ERROR",
            Error = "RimBridge closed the routed connection before completing the request.",
            Generation = 14,
            LaunchId = "launch-route-failure"
        };

        RecipeOperationResult result = CoordinatorState.EvaluateRecipeOperation(operation, 4, route);
        Assert(!result.Success && result.ErrorCode == route.ErrorCode &&
               result.Error == route.Error && result.Generation == route.Generation &&
               result.LaunchId == route.LaunchId,
            "recipe failures must preserve the routed diagnostic instead of replacing it with a generic message");
    }

    private static void TestSharedTransitionRecoveryPolicyIsStrictAndBounded()
    {
        Assert(RimBridgeTransitionRecoveryPolicy.IsTransitionFailureCode(
                   "RIMBRIDGE_ENDPOINT_STALE") &&
               RimBridgeTransitionRecoveryPolicy.IsTransitionFailureCode(
                   "RIMBRIDGE_PROCESS_IDENTITY_MISMATCH") &&
               RimBridgeTransitionRecoveryPolicy.IsTransitionFailureCode(
                   "RIMBRIDGE_PROTOCOL_ERROR"),
            "shared-transition recovery must recognize only the documented route failure family");
        Assert(!RimBridgeTransitionRecoveryPolicy.IsTransitionFailureCode(
                   "RIMBRIDGE_AUTH_FAILED") &&
               !RimBridgeTransitionRecoveryPolicy.IsTransitionFailureCode(
                   "RIMBRIDGE_INVALID_ARGUMENTS"),
            "authentication and argument failures must never enter transition recovery");
        Assert(RimBridgeTransitionRecoveryPolicy.HasAuthoritativeEvidence(
                   "RIMBRIDGE_PROTOCOL_ERROR", 4, 5, 5, false) &&
               RimBridgeTransitionRecoveryPolicy.HasAuthoritativeEvidence(
                   "RIMBRIDGE_ENDPOINT_STALE", 4, 4, 5, true),
            "a later accepted generation or queued target must authorize observation");
        Assert(!RimBridgeTransitionRecoveryPolicy.HasAuthoritativeEvidence(
                   "RIMBRIDGE_PROTOCOL_ERROR", 4, 4, 4, false) &&
               !RimBridgeTransitionRecoveryPolicy.HasAuthoritativeEvidence(
                   "RIMBRIDGE_ENDPOINT_STALE", 4, 3, 4, false),
            "a protocol interruption without authoritative transition evidence must remain fatal");
        Assert(RimBridgeTransitionRecoveryPolicy.CanReplay(
                   RimBridgeOperationCategories.ReadOnly) &&
               !RimBridgeTransitionRecoveryPolicy.CanReplay(
                   RimBridgeOperationCategories.InGameMutation) &&
               !RimBridgeTransitionRecoveryPolicy.CanReplay(
                   RimBridgeOperationCategories.LifecycleMutation),
            "only read-only operations may be automatically replayed");
    }
    private static void TestRecipePlanningIsPureAndBounded()
    {
        using Fixture fixture = Fixture.ReadyWithoutLease();
        WriteRecipe(fixture, "quicktest-smoke", SmokeRecipe);
        string statePath = Path.Combine(fixture.Root, "Runtime", "state.json");
        byte[] stateBefore = File.ReadAllBytes(statePath);
        int launchesBefore = fixture.Adapter.LaunchCalls;

        RecipeResponse recipePlan = ExecuteRecipe(fixture, "plan", "quicktest-smoke");
        AgentRecipePlanResponse agentPlan = ExecuteAgentPlan(fixture, "quicktest-smoke");
        Assert(recipePlan is RecipePlanResponse plan && plan.EstimatedRimWorldLaunches == 1 &&
               !plan.AlreadySatisfied && agentPlan.ExitCode == 0 &&
               agentPlan.EstimatedRimWorldLaunches == 1,
            "an unsatisfied recipe plan must report exactly one required launch");
        Assert(File.ReadAllBytes(statePath).SequenceEqual(stateBefore) &&
               fixture.Adapter.LaunchCalls == launchesBefore &&
               ReadPersistedState(fixture.Root).Leases.Count == 0,
            "recipe and agent planning must not save state, acquire leases, or launch");
    }

    private static void TestRecipeAlreadySatisfiedAvoidsRestart()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "recipe satisfaction setup must capture the baseline");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(Request("restart", "agent", 1,
                       "--projects", "none", "--input", "quicktest=true"), _ => { }, () => true) == 0,
            "recipe satisfaction setup must create a ready generation");
        WriteRecipe(setup.Fixture, "quicktest-smoke", SmokeRecipe);
        int launches = setup.Fixture.Adapter.LaunchCalls;

        RecipeResponse planResponse = ExecuteRecipe(setup.Fixture, "plan", "quicktest-smoke");
        Assert(planResponse is RecipePlanResponse plan && plan.AlreadySatisfied &&
               plan.EstimatedRimWorldLaunches == 0,
            "a complete ready generation must have a zero-launch plan");

        RecipeResponse response = ExecuteRecipe(setup.Fixture, "run", "quicktest-smoke");
        Assert(response is RecipeRunResponse run && run.Success && !run.RestartRequired &&
               run.LaunchesConsumed == 0 && setup.Fixture.Adapter.LaunchCalls == launches,
            "a complete ready generation must run with zero replacement launches");

        response = ExecuteRecipe(setup.Fixture, "run", "quicktest-smoke");
        Assert(response is RecipeRunResponse duplicate && duplicate.Success &&
               !duplicate.RestartRequired && duplicate.LaunchesConsumed == 0 &&
               setup.Fixture.Adapter.LaunchCalls == launches,
            "repeating an already completed recipe must not create a duplicate launch");
    }

    private static void TestRecipeRunUsesOneLaunchAndEnforcesBudget()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "recipe launch setup must capture the baseline");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        WriteRecipe(setup.Fixture, "quicktest-smoke", SmokeRecipe);
        RecipeResponse response = ExecuteRecipe(setup.Fixture, "run", "quicktest-smoke",
            "--max-rimworld-launches", "0");
        Assert(response is RecipeRunResponse blocked && !blocked.Success &&
               blocked.ErrorCode == "AUTONOMOUS_BUDGET_EXHAUSTED" &&
               blocked.Budget.MaxRimWorldLaunches == 0 && blocked.LaunchesConsumed == 0 &&
               setup.Fixture.Adapter.LaunchCalls == 0,
            "caller launch budget zero must stop before any restart or lease mutation");

        response = ExecuteRecipe(setup.Fixture, "run", "quicktest-smoke");
        Assert(response is RecipeRunResponse run && run.Success && run.RestartRequired &&
               run.LaunchesConsumed == 1 && setup.Fixture.Adapter.LaunchCalls == 1,
            "a bounded recipe run must request exactly one launch when required");
    }

    private static void TestSuccessfulRecipeRetiresEquivalentFailureGuard()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "failure retirement setup must capture the baseline");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(Request("restart", "recipe-agent", 991,
                       "--projects", "none", "--input", "quicktest=true"), _ => { }, () => true) == 0,
            "failure retirement setup must create a ready generation");
        WriteRecipe(setup.Fixture, "quicktest-smoke", SmokeRecipe);

        const string sourceFingerprint =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        PersistedState current = ReadPersistedState(setup.Fixture.Root);
        string failureFingerprint = setup.Fixture.State.RecordRecipeFailure(
            "quicktest-smoke",
            "RECIPE_RESTART_FAILED",
            "Synthetic prior restart failure.",
            current.Generation,
            current.ProfileFingerprint,
            [new TestInputValue { Name = "quicktest", Value = "true" }],
            sourceFingerprint);
        FailureOccurrenceSummary occurrence = ReadPersistedState(setup.Fixture.Root)
            .FailureOccurrences
            .Single(value => value.FailureFingerprint == failureFingerprint);
        string evidenceId = occurrence.EvidenceId;

        Assert(
            setup.Fixture.State.FindEquivalentRecipeFailureLocked(
                "quicktest-smoke",
                current.ProfileFingerprint,
                [new TestInputValue { Name = "quicktest", Value = "true" }],
                1,
                sourceFingerprint) is not null,
            "synthetic failure must match the equivalent recipe context before recovery");
        RecipeResponse response = ExecuteRecipe(
            setup.Fixture,
            "run",
            "quicktest-smoke",
            "--source-fingerprint",
            sourceFingerprint);
        Assert(
            response is RecipeRunResponse run && run.Success,
            "a successful equivalent recipe must retire the repeated-failure guard");

        PersistedState persisted = ReadPersistedState(setup.Fixture.Root);
        Assert(
            !persisted.FailureOccurrences.Any(value =>
                value.FailureFingerprint == failureFingerprint),
            "retired failures must leave the active repeated-failure guard");
        Assert(
            !string.IsNullOrWhiteSpace(evidenceId) &&
            File.Exists(Path.Combine(
                setup.Fixture.Root,
                "Runtime",
                "evidence",
                evidenceId + ".json")),
            "retiring the guard must preserve historical evidence");
        Assert(
            string.IsNullOrWhiteSpace(persisted.LatestFailureFingerprint),
            "successful recovery must clear the active latest-failure projection");
    }

    private static void TestRecipeRunBudgetCannotWeakenCoordinatorLimit()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "strict budget setup must capture the baseline");
        setup.Fixture.Adapter.ReadyOnLaunch = false;
        WriteRecipe(setup.Fixture, "quicktest-smoke", SmokeRecipe.Replace(
            "\"maxRimWorldLaunches\": 1", "\"maxRimWorldLaunches\": 8", StringComparison.Ordinal));
        RecipeResponse response = ExecuteRecipe(setup.Fixture, "run", "quicktest-smoke",
            "--max-rimworld-launches", "8", "--timeout-seconds", "5");
        Assert(response is RecipeRunResponse run && run.Budget != null &&
               run.Budget.MaxRimWorldLaunches == 1 && run.Budget.TimeoutSeconds <= 900,
            "caller and recipe budgets must remain capped by coordinator safety bounds");
        Assert(ReadPersistedState(setup.Fixture.Root).Leases.Count == 0,
            "budget exhaustion must not leave an owned test lease behind");
    }

    private static void TestSuppliedLeaseRefusalDoesNotPoisonRepeatedGuard()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "supplied-lease setup must capture the baseline");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(Request("restart", "recipe-agent", 991,
                       "--projects", "none", "--input", "quicktest=true"), _ => { }, () => true) == 0,
            "supplied-lease setup must create the ready control generation");

        string incompatibleRecipe = SmokeRecipe.Replace("\"projects\": []",
            "\"projects\": [\"frontier\"]", StringComparison.Ordinal);
        WriteRecipe(setup.Fixture, "quicktest-smoke", incompatibleRecipe);
        BridgeRequest begin = Request("test", "recipe-agent", 991, "begin");
        Assert(setup.Fixture.State.Execute(begin, _ => { }, () => true) == 0,
            "the recipe agent must acquire the test lease");
        string leaseId = ReadPersistedState(setup.Fixture.Root).Leases.Single().Id;

        RecipeResponse first = ExecuteRecipe(setup.Fixture, "run", "quicktest-smoke", "--lease", leaseId);
        RecipeResponse second = ExecuteRecipe(setup.Fixture, "run", "quicktest-smoke", "--lease", leaseId);
        Assert(first is RecipeRunResponse firstRun && !firstRun.Success &&
               firstRun.ErrorCode == "RECIPE_SUPPLIED_LEASE_REQUIRES_READY" &&
               firstRun.LaunchesConsumed == 0 && firstRun.LeaseId == leaseId,
            "an incompatible supplied lease must expose the exact precondition refusal");
        Assert(second is RecipeRunResponse secondRun && !secondRun.Success &&
               secondRun.ErrorCode == "RECIPE_SUPPLIED_LEASE_REQUIRES_READY" &&
               secondRun.ErrorCode != "AUTONOMOUS_REPEATED_FAILURE" &&
               secondRun.LaunchesConsumed == 0 && secondRun.LeaseId == leaseId,
            "repeating a supplied-lease precondition refusal must not enter the repeated-failure guard");

        PersistedState persisted = ReadPersistedState(setup.Fixture.Root);
        FailureOccurrenceSummary occurrence = persisted.FailureOccurrences.FirstOrDefault(value =>
            value?.RecipeId == "QUICKTEST-SMOKE" &&
            value.ErrorCode == "RECIPE_SUPPLIED_LEASE_REQUIRES_READY");
        Assert(occurrence != null && occurrence.OccurrenceCount >= 2 &&
               !string.IsNullOrWhiteSpace(occurrence.EvidenceId),
            "the refusal must remain bounded diagnostic evidence while staying guard-ineligible");
    }

    private static void TestLegacySuppliedLeaseEvidenceDoesNotTriggerRepeatedGuard()
    {
        using ProfileSetup setup = ProfileSetup.Create();
        Assert(setup.CaptureBaseline(), "legacy evidence setup must capture the baseline");
        setup.Fixture.Adapter.ReadyOnLaunch = true;
        Assert(setup.Fixture.State.Execute(Request("restart", "recipe-agent", 991,
                       "--projects", "none", "--input", "quicktest=true"), _ => { }, () => true) == 0,
            "legacy evidence setup must create the ready control generation");
        WriteRecipe(setup.Fixture, "quicktest-smoke", SmokeRecipe);

        PersistedState current = ReadPersistedState(setup.Fixture.Root);
        Assert(!string.IsNullOrWhiteSpace(current.ProfileFingerprint),
            "legacy evidence setup must have a current profile fingerprint");
        List<TestInputValue> inputs = new()
        {
            new TestInputValue { Name = "quicktest", Value = "true" }
        };
        string fingerprint = setup.Fixture.State.RecordRecipeFailure(
            "quicktest-smoke", "RECIPE_SUPPLIED_LEASE_REQUIRES_READY",
            "A supplied lease cannot authorize an autonomous restart.", current.Generation,
            current.ProfileFingerprint, inputs);
        PersistedState legacy = ReadPersistedState(setup.Fixture.Root);
        FailureOccurrenceSummary occurrence = legacy.FailureOccurrences.Single(value =>
            value.FailureFingerprint == fingerprint);
        string evidenceId = occurrence.EvidenceId;
        occurrence.ErrorCode = null;
        setup.Fixture.WriteState(legacy);
        setup.Fixture.State = setup.Fixture.Reload();

        RecipeResponse response = ExecuteRecipe(setup.Fixture, "run", "quicktest-smoke");
        Assert(response is RecipeRunResponse run && run.Success && run.ErrorCode == null &&
               run.LaunchesConsumed == 0,
            "a legacy supplied-lease refusal must be retired from guard eligibility using its evidence code");
        Assert(!string.IsNullOrWhiteSpace(evidenceId) &&
               File.Exists(Path.Combine(setup.Fixture.Root, "Runtime", "evidence", evidenceId + ".json")),
            "retiring guard eligibility must preserve the historical evidence record");
    }

    private static RecipeResponse ExecuteRecipe(Fixture fixture, params string[] arguments)
    {
        BridgeRequest request = Request("test", "recipe-agent", 991, new[] { "recipe" }.Concat(arguments).ToArray());
        request.RequestId = CoordinatorIpcProtocol.NewRequestId();
        request.Json = true;
        int exitCode = fixture.State.Execute(request, _ => { }, () => true);
        return fixture.State.CreateRecipeJsonResponse(request, exitCode);
    }

    private static AgentRecipePlanResponse ExecuteAgentPlan(Fixture fixture, string recipeId)
    {
        BridgeRequest request = Request("agent", "recipe-agent", 991, "plan", "--recipe", recipeId);
        request.Json = true;
        int exitCode = fixture.State.Execute(request, _ => { }, () => true);
        return fixture.State.CreateAgentJsonResponse(request, exitCode) as AgentRecipePlanResponse;
    }

    private static void WriteRecipe(Fixture fixture, string id, string json)
    {
        string directory = Path.Combine(fixture.Root, "TestRecipes");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json, new UTF8Encoding(false));
    }
}
