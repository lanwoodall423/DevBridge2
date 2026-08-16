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

    private static RecipeResponse ExecuteRecipe(Fixture fixture, params string[] arguments)
    {
        BridgeRequest request = Request("test", "recipe-agent", 991, new[] { "recipe" }.Concat(arguments).ToArray());
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
