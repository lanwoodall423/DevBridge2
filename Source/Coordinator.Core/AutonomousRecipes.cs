using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBridge2;

namespace DevBridge.Coordinator;

// Recipes are deliberately parsed into a small coordinator-owned model.  The
// model has no command-line, process, environment, or filesystem operation
// fields; the only external operation it can carry is a policy-checked
// RimBridge call.
internal sealed class TestRecipeDefinition
{
    internal string SchemaVersion { get; init; }
    internal string Id { get; init; }
    internal string Description { get; init; }
    internal List<string> Projects { get; init; } = new();
    internal List<TestInputAssignment> Inputs { get; init; } = new();
    internal bool RequiresReady { get; init; } = true;
    internal bool AllowsInGameMutation { get; init; }
    internal RecipeExpectation Success { get; init; } = new();
    internal List<RecipeOperationDefinition> Operations { get; init; } = new();
    internal RecipeBudgetDefinition Budget { get; init; } = new();
}

internal sealed class RecipeExpectation
{
    internal bool QuicktestReady { get; init; }
    internal bool CompanionVerified { get; init; }
    internal bool RimBridgeReady { get; init; }
}

internal sealed class RecipeOperationDefinition
{
    internal string ToolName { get; init; }
    internal JsonElement Arguments { get; init; }
    internal RecipeOperationExpectation Expectation { get; init; }
}

internal sealed class RecipeOperationExpectation
{
    internal bool ExpectedSuccess { get; init; } = true;
    internal List<RecipeAssertionDefinition> Assertions { get; init; } = new();
}

internal sealed class RecipeAssertionDefinition
{
    internal string Pointer { get; init; }
    internal bool? Exists { get; init; }
    internal JsonElement? ExpectedValue { get; init; }
    internal double? GreaterThan { get; init; }
    internal double? LessThan { get; init; }
}

internal sealed class RecipeBudgetDefinition
{
    internal int TimeoutSeconds { get; set; } = 300;
    internal int MaxRimWorldLaunches { get; set; } = 1;
    internal int MaxRecipeAttempts { get; set; } = 1;
    internal int MaxCoordinatorRefreshes { get; set; } = 4;
    internal bool StopOnRepeatedFailureFingerprint { get; set; } = true;
    internal int MaxRepeatedFailureCount { get; set; } = 1;
}

internal sealed class RecipeCatalog
{
    private const int MaxRecipes = 32;
    private readonly Dictionary<string, TestRecipeDefinition> recipes;

    private RecipeCatalog(Dictionary<string, TestRecipeDefinition> recipes)
    {
        this.recipes = recipes;
    }

    internal IReadOnlyList<TestRecipeDefinition> Recipes => recipes.Values
        .OrderBy(value => value.Id, StringComparer.Ordinal)
        .ToList();

    internal bool TryGet(string id, out TestRecipeDefinition recipe) =>
        recipes.TryGetValue(id ?? string.Empty, out recipe);

    internal static bool TryLoad(string root, out RecipeCatalog catalog,
        out string errorCode, out string error)
    {
        catalog = null;
        errorCode = null;
        error = null;
        string directory = Path.Combine(root, "TestRecipes");
        if (!Directory.Exists(directory))
        {
            errorCode = "TEST_RECIPE_DIRECTORY_MISSING";
            error = "The repository-owned TestRecipes directory is missing.";
            return false;
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            errorCode = "TEST_RECIPE_CATALOG_UNAVAILABLE";
            error = "The repository-owned TestRecipes directory could not be read.";
            return false;
        }

        if (paths.Length > MaxRecipes)
        {
            errorCode = "TEST_RECIPE_CATALOG_TOO_LARGE";
            error = "The TestRecipes catalog exceeds the coordinator safety bound.";
            return false;
        }

        Dictionary<string, TestRecipeDefinition> parsed =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (!TryParseFile(path, out TestRecipeDefinition recipe,
                    out errorCode, out error))
                return false;
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), recipe.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "TEST_RECIPE_ID_FILENAME_MISMATCH";
                error = "Recipe file names must match their declared id.";
                return false;
            }
            if (!parsed.TryAdd(recipe.Id, recipe))
            {
                errorCode = "TEST_RECIPE_DUPLICATE_ID";
                error = "The TestRecipes catalog contains a duplicate recipe id.";
                return false;
            }
        }

        catalog = new RecipeCatalog(parsed);
        return true;
    }

    private static bool TryParseFile(string path, out TestRecipeDefinition recipe,
        out string errorCode, out string error)
    {
        recipe = null;
        errorCode = null;
        error = null;
        try
        {
            FileInfo info = new(path);
            if (info.Length > 128 * 1024)
            {
                errorCode = "TEST_RECIPE_TOO_LARGE";
                error = "Recipe files are limited to 128 KiB.";
                return false;
            }
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16 });
            return TryParse(document.RootElement, out recipe, out errorCode, out error);
        }
        catch (JsonException)
        {
            errorCode = "TEST_RECIPE_INVALID_JSON";
            error = "A recipe file contains invalid JSON.";
            return false;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            errorCode = "TEST_RECIPE_READ_FAILED";
            error = "A recipe file could not be read.";
            return false;
        }
    }

    private static bool TryParse(JsonElement root, out TestRecipeDefinition recipe,
        out string errorCode, out string error)
    {
        recipe = null;
        errorCode = null;
        error = null;
        if (root.ValueKind != JsonValueKind.Object)
            return Failure("TEST_RECIPE_ROOT_INVALID", "A recipe must be a JSON object.",
                out errorCode, out error);

        if (!TryString(root, "schemaVersion", required: true, out string schema,
                out errorCode, out error))
            return false;
        bool isV1 = string.Equals(schema, DevBridgeSchemaVersions.TestRecipe, StringComparison.Ordinal);
        bool isV2 = string.Equals(schema, DevBridgeSchemaVersions.TestRecipeV2Contract, StringComparison.Ordinal);
        if (!isV1 && !isV2)
            return Failure("TEST_RECIPE_SCHEMA_UNSUPPORTED",
                "Only devbridge-test-recipe/v1 and /v2 recipes are supported.",
                out errorCode, out error);

        string[] allowed = isV2
            ? new[] { "schemaVersion", "id", "description", "projects", "inputs",
                "requiresReady", "success", "operations", "timeoutSeconds", "budget",
                "allowInGameMutation" }
            : new[] { "schemaVersion", "id", "description", "projects", "inputs",
                "requiresReady", "success", "operations", "timeoutSeconds", "budget" };
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                return Failure("TEST_RECIPE_UNSUPPORTED_FIELD",
                    "Recipe fields are limited to explicit DevBridge-owned concepts.",
                    out errorCode, out error);
        }

        if (!TryString(root, "id", required: true, out string id, out errorCode, out error))
            return false;
        if (id.Length > 64 || id.Length == 0 ||
            id.Any(value => !(char.IsLetterOrDigit(value) || value == '-' || value == '_' || value == '.')) ||
            !char.IsLetterOrDigit(id[0]))
            return Failure("TEST_RECIPE_ID_INVALID", "Recipe ids must be bounded simple identifiers.",
                out errorCode, out error);
        if (!TryString(root, "description", required: false, out string description,
                out errorCode, out error))
            return false;
        if ((description?.Length ?? 0) > 256)
            return Failure("TEST_RECIPE_DESCRIPTION_INVALID", "Recipe descriptions are bounded.",
                out errorCode, out error);

        List<string> projects = new();
        if (root.TryGetProperty("projects", out JsonElement projectsElement))
        {
            if (projectsElement.ValueKind != JsonValueKind.Array || projectsElement.GetArrayLength() > 32)
                return Failure("TEST_RECIPE_PROJECTS_INVALID", "projects must be a bounded string array.",
                    out errorCode, out error);
            foreach (JsonElement value in projectsElement.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(value.GetString()))
                    return Failure("TEST_RECIPE_PROJECTS_INVALID", "projects must contain non-empty strings.",
                        out errorCode, out error);
                projects.Add(value.GetString().Trim());
            }
        }

        List<TestInputAssignment> inputs = new();
        if (root.TryGetProperty("inputs", out JsonElement inputsElement))
        {
            if (inputsElement.ValueKind != JsonValueKind.Object || inputsElement.EnumerateObject().Count() > 8)
                return Failure("TEST_RECIPE_INPUTS_INVALID", "inputs must be a bounded scalar object.",
                    out errorCode, out error);
            foreach (JsonProperty property in inputsElement.EnumerateObject().OrderBy(value => value.Name,
                         StringComparer.Ordinal))
            {
                if (!TryScalar(property.Value, out string value))
                    return Failure("TEST_RECIPE_INPUTS_INVALID", "recipe inputs must be scalar values.",
                        out errorCode, out error);
                try
                {
                    inputs.Add(TestGenerationInputs.ParseCommandAssignment(property.Name + "=" + value));
                }
                catch (ProfileException exception)
                {
                    return Failure(exception.Code, exception.Message, out errorCode, out error);
                }
            }
        }

        bool requiresReady = true;
        if (root.TryGetProperty("requiresReady", out JsonElement readyElement))
        {
            if (readyElement.ValueKind != JsonValueKind.True && readyElement.ValueKind != JsonValueKind.False)
                return Failure("TEST_RECIPE_READINESS_INVALID", "requiresReady must be boolean.",
                    out errorCode, out error);
            requiresReady = readyElement.GetBoolean();
        }

        bool allowsInGameMutation = false;
        if (root.TryGetProperty("allowInGameMutation", out JsonElement mutationElement))
        {
            if (!isV2 || (mutationElement.ValueKind != JsonValueKind.True &&
                    mutationElement.ValueKind != JsonValueKind.False))
                return Failure("TEST_RECIPE_MUTATION_OPT_IN_INVALID",
                    "allowInGameMutation must be a boolean and is available only in v2 recipes.",
                    out errorCode, out error);
            allowsInGameMutation = mutationElement.GetBoolean();
        }

        RecipeExpectation success = new() { QuicktestReady = requiresReady };
        if (root.TryGetProperty("success", out JsonElement successElement))
        {
            if (successElement.ValueKind != JsonValueKind.Object)
                return Failure("TEST_RECIPE_SUCCESS_INVALID", "success must be an evidence object.",
                    out errorCode, out error);
            foreach (JsonProperty property in successElement.EnumerateObject())
            {
                if (property.Name != "quicktestReady" && property.Name != "companionVerified" &&
                    property.Name != "rimBridgeReady")
                    return Failure("TEST_RECIPE_UNSUPPORTED_FIELD", "success contains an unsupported evidence field.",
                        out errorCode, out error);
                if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                    return Failure("TEST_RECIPE_SUCCESS_INVALID", "success evidence values must be boolean.",
                        out errorCode, out error);
            }
            success = new RecipeExpectation
            {
                QuicktestReady = BooleanProperty(successElement, "quicktestReady", requiresReady),
                CompanionVerified = BooleanProperty(successElement, "companionVerified", false),
                RimBridgeReady = BooleanProperty(successElement, "rimBridgeReady", false)
            };
        }

        List<RecipeOperationDefinition> operations = new();
        if (root.TryGetProperty("operations", out JsonElement operationsElement))
        {
            if (operationsElement.ValueKind != JsonValueKind.Array || operationsElement.GetArrayLength() > 8)
                return Failure("TEST_RECIPE_OPERATIONS_INVALID", "operations must be a bounded array.",
                    out errorCode, out error);
            foreach (JsonElement operation in operationsElement.EnumerateArray())
            {
                if (operation.ValueKind != JsonValueKind.Object)
                    return Failure("TEST_RECIPE_OPERATIONS_INVALID", "each operation must be an object.",
                        out errorCode, out error);
                foreach (JsonProperty property in operation.EnumerateObject())
                    if (property.Name != "tool" && property.Name != "arguments" &&
                        (!isV2 || property.Name != "expect"))
                        return Failure("TEST_RECIPE_UNSUPPORTED_FIELD", "operations contain an unsupported field.",
                            out errorCode, out error);
                if (!TryString(operation, "tool", true, out string tool, out errorCode, out error))
                    return false;
                if (!operation.TryGetProperty("arguments", out JsonElement arguments) ||
                    arguments.ValueKind != JsonValueKind.Object)
                    return Failure("TEST_RECIPE_OPERATIONS_INVALID", "operation arguments must be a JSON object.",
                        out errorCode, out error);
                if (JsonSerializer.Serialize(arguments, CoordinatorSerialization.JsonOptions).Length > 4096)
                    return Failure("TEST_RECIPE_OPERATIONS_INVALID", "operation arguments are bounded.",
                        out errorCode, out error);
                string category = RimBridgeOperationPolicy.CategoryFor(tool);
                if (category == RimBridgeOperationCategories.ProfileMutation ||
                    category == RimBridgeOperationCategories.LifecycleMutation)
                    return Failure(isV2 ? "TEST_RECIPE_RIMBRIDGE_FORBIDDEN" : "TEST_RECIPE_RIMBRIDGE_MUTATION",
                        isV2 ? "profile and lifecycle RimBridge operations are never allowed in recipes." :
                            "recipes may call only policy-approved read-only/debug RimBridge tools.",
                        out errorCode, out error);
                if (category == RimBridgeOperationCategories.InGameMutation && !isV2)
                    return Failure("TEST_RECIPE_RIMBRIDGE_MUTATION",
                        "recipes may call only policy-approved read-only/debug RimBridge tools.",
                        out errorCode, out error);
                if (category == RimBridgeOperationCategories.InGameMutation && !allowsInGameMutation)
                    return Failure("TEST_RECIPE_IN_GAME_MUTATION_OPT_IN_REQUIRED",
                        "v2 recipes must explicitly allow in-game mutation operations.",
                        out errorCode, out error);
                RecipeOperationExpectation expectation = new();
                if (isV2 && operation.TryGetProperty("expect", out JsonElement expectElement) &&
                    !TryParseOperationExpectation(expectElement, out expectation, out errorCode, out error))
                    return false;
                operations.Add(new RecipeOperationDefinition
                {
                    ToolName = tool,
                    Arguments = arguments.Clone(),
                    Expectation = isV2 ? expectation : null
                });
            }
        }

        RecipeBudgetDefinition budget = new();
        if (root.TryGetProperty("timeoutSeconds", out JsonElement timeoutElement))
        {
            if (!TryBoundedInt(timeoutElement, 1, 900, out int timeout))
                return Failure("TEST_RECIPE_BUDGET_INVALID", "timeoutSeconds must be between 1 and 900.",
                    out errorCode, out error);
            budget.TimeoutSeconds = timeout;
        }
        if (root.TryGetProperty("budget", out JsonElement budgetElement))
        {
            if (budgetElement.ValueKind != JsonValueKind.Object)
                return Failure("TEST_RECIPE_BUDGET_INVALID", "budget must be an object.",
                    out errorCode, out error);
            foreach (JsonProperty property in budgetElement.EnumerateObject())
            {
                if (property.Name != "timeoutSeconds" && property.Name != "maxRimWorldLaunches" &&
                    property.Name != "maxRecipeAttempts" && property.Name != "maxCoordinatorRefreshes" &&
                    property.Name != "stopOnRepeatedFailureFingerprint" &&
                    property.Name != "maxRepeatedFailureCount")
                    return Failure("TEST_RECIPE_UNSUPPORTED_FIELD", "budget contains an unsupported field.",
                        out errorCode, out error);
            }
            if (budgetElement.TryGetProperty("timeoutSeconds", out timeoutElement))
            {
                if (root.TryGetProperty("timeoutSeconds", out _ ) ||
                    !TryBoundedInt(timeoutElement, 1, 900, out int budgetTimeout))
                    return Failure("TEST_RECIPE_BUDGET_INVALID", "timeoutSeconds may be declared only once.",
                        out errorCode, out error);
                budget.TimeoutSeconds = budgetTimeout;
            }
            if (!ReadBudgetInt(budgetElement, "maxRimWorldLaunches", 0, 8,
                    budget.MaxRimWorldLaunches, out int maxLaunches, out errorCode, out error) ||
                !ReadBudgetInt(budgetElement, "maxRecipeAttempts", 1, 8,
                    budget.MaxRecipeAttempts, out int maxAttempts, out errorCode, out error) ||
                !ReadBudgetInt(budgetElement, "maxCoordinatorRefreshes", 0, 32,
                    budget.MaxCoordinatorRefreshes, out int maxRefreshes, out errorCode, out error))
                return false;
            budget.MaxRimWorldLaunches = maxLaunches;
            budget.MaxRecipeAttempts = maxAttempts;
            budget.MaxCoordinatorRefreshes = maxRefreshes;
            if (budgetElement.TryGetProperty("stopOnRepeatedFailureFingerprint", out JsonElement stopElement))
            {
                if (stopElement.ValueKind != JsonValueKind.True && stopElement.ValueKind != JsonValueKind.False)
                    return Failure("TEST_RECIPE_BUDGET_INVALID", "stopOnRepeatedFailureFingerprint must be boolean.",
                        out errorCode, out error);
                budget.StopOnRepeatedFailureFingerprint = stopElement.GetBoolean();
            }
            if (!ReadBudgetInt(budgetElement, "maxRepeatedFailureCount", 0, 8,
                    budget.MaxRepeatedFailureCount, out int maxRepeated, out errorCode, out error))
                return false;
            budget.MaxRepeatedFailureCount = maxRepeated;
        }

        try
        {
            TestGenerationInputs.Normalize(inputs, ModProfile.ProjectsMode);
            ModProfileResolver.CanonicalAliases(projects);
        }
        catch (ProfileException exception)
        {
            return Failure(exception.Code, exception.Message, out errorCode, out error);
        }
        recipe = new TestRecipeDefinition
        {
            SchemaVersion = schema,
            Id = id,
            Description = description ?? string.Empty,
            Projects = ModProfileResolver.CanonicalAliases(projects).ToList(),
            Inputs = inputs,
            RequiresReady = requiresReady,
            AllowsInGameMutation = allowsInGameMutation,
            Success = success,
            Operations = operations,
            Budget = budget
        };
        return true;
    }

    private static bool TryParseOperationExpectation(JsonElement element,
        out RecipeOperationExpectation expectation, out string errorCode, out string error)
    {
        expectation = null;
        errorCode = null;
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
            return Failure("TEST_RECIPE_EXPECTATION_INVALID", "operation expect must be an object.",
                out errorCode, out error);

        foreach (JsonProperty property in element.EnumerateObject())
            if (property.Name != "success" && property.Name != "assertions")
                return Failure("TEST_RECIPE_UNSUPPORTED_FIELD",
                    "operation expectations contain an unsupported field.", out errorCode, out error);

        bool expectedSuccess = true;
        if (element.TryGetProperty("success", out JsonElement success))
        {
            if (success.ValueKind != JsonValueKind.True && success.ValueKind != JsonValueKind.False)
                return Failure("TEST_RECIPE_EXPECTATION_INVALID", "expect.success must be boolean.",
                    out errorCode, out error);
            expectedSuccess = success.GetBoolean();
        }

        List<RecipeAssertionDefinition> assertions = new();
        if (element.TryGetProperty("assertions", out JsonElement assertionsElement))
        {
            if (assertionsElement.ValueKind != JsonValueKind.Array ||
                assertionsElement.GetArrayLength() > 4)
                return Failure("TEST_RECIPE_ASSERTIONS_INVALID",
                    "operation assertions must be a bounded array of at most four items.",
                    out errorCode, out error);
            foreach (JsonElement assertion in assertionsElement.EnumerateArray())
            {
                if (!TryParseAssertion(assertion, out RecipeAssertionDefinition parsed,
                        out errorCode, out error))
                    return false;
                assertions.Add(parsed);
            }
        }
        expectation = new RecipeOperationExpectation
        {
            ExpectedSuccess = expectedSuccess,
            Assertions = assertions
        };
        return true;
    }

    private static bool TryParseAssertion(JsonElement element, out RecipeAssertionDefinition assertion,
        out string errorCode, out string error)
    {
        assertion = null;
        errorCode = null;
        error = null;
        if (element.ValueKind != JsonValueKind.Object)
            return Failure("TEST_RECIPE_ASSERTION_INVALID", "an assertion must be an object.",
                out errorCode, out error);
        foreach (JsonProperty property in element.EnumerateObject())
            if (property.Name != "pointer" && property.Name != "exists" && property.Name != "equals" &&
                property.Name != "greaterThan" && property.Name != "lessThan")
                return Failure("TEST_RECIPE_UNSUPPORTED_FIELD", "assertions contain an unsupported field.",
                    out errorCode, out error);

        if (!TryString(element, "pointer", required: true, out string pointer,
                out errorCode, out error))
            return false;
        if (pointer.Length > 256 || (pointer.Length > 0 && pointer[0] != '/'))
            return Failure("TEST_RECIPE_ASSERTION_INVALID",
                "assertion pointers must be bounded JSON Pointers.", out errorCode, out error);

        bool hasExists = element.TryGetProperty("exists", out JsonElement exists);
        if (hasExists && exists.ValueKind != JsonValueKind.True && exists.ValueKind != JsonValueKind.False)
            return Failure("TEST_RECIPE_ASSERTION_INVALID", "assertion exists must be boolean.",
                out errorCode, out error);
        bool hasEquals = element.TryGetProperty("equals", out JsonElement equals);
        if (hasEquals && !IsScalar(equals))
            return Failure("TEST_RECIPE_ASSERTION_INVALID", "assertion equals must be scalar.",
                out errorCode, out error);
        bool hasGreaterThan = element.TryGetProperty("greaterThan", out JsonElement greaterThan);
        if (hasGreaterThan && !TryFiniteDouble(greaterThan, out _))
            return Failure("TEST_RECIPE_ASSERTION_INVALID", "assertion greaterThan must be finite numeric.",
                out errorCode, out error);
        bool hasLessThan = element.TryGetProperty("lessThan", out JsonElement lessThan);
        if (hasLessThan && !TryFiniteDouble(lessThan, out _))
            return Failure("TEST_RECIPE_ASSERTION_INVALID", "assertion lessThan must be finite numeric.",
                out errorCode, out error);
        int checks = (hasExists ? 1 : 0) + (hasEquals ? 1 : 0) +
            (hasGreaterThan ? 1 : 0) + (hasLessThan ? 1 : 0);
        if (checks != 1)
            return Failure("TEST_RECIPE_ASSERTION_INVALID",
                "each assertion must select exactly one bounded comparison.", out errorCode, out error);

        assertion = new RecipeAssertionDefinition
        {
            Pointer = pointer,
            Exists = hasExists ? exists.GetBoolean() : null,
            ExpectedValue = hasEquals ? equals.Clone() : null,
            GreaterThan = hasGreaterThan ? greaterThan.GetDouble() : null,
            LessThan = hasLessThan ? lessThan.GetDouble() : null
        };
        return true;
    }

    private static bool IsScalar(JsonElement element) => element.ValueKind == JsonValueKind.Null ||
        element.ValueKind == JsonValueKind.String || element.ValueKind == JsonValueKind.True ||
        element.ValueKind == JsonValueKind.False || element.ValueKind == JsonValueKind.Number;

    private static bool TryFiniteDouble(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value) &&
            !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool ReadBudgetInt(JsonElement parent, string name, int min, int max,
        int defaultValue, out int target, out string errorCode, out string error)
    {
        target = defaultValue;
        errorCode = null;
        error = null;
        if (!parent.TryGetProperty(name, out JsonElement value))
            return true;
        if (!TryBoundedInt(value, min, max, out target))
            return Failure("TEST_RECIPE_BUDGET_INVALID", name + " is outside the coordinator budget bound.",
                out errorCode, out error);
        return true;
    }

    private static bool TryString(JsonElement parent, string name, bool required, out string value,
        out string errorCode, out string error)
    {
        value = null;
        errorCode = null;
        error = null;
        if (!parent.TryGetProperty(name, out JsonElement element))
        {
            if (!required)
                return true;
            return Failure("TEST_RECIPE_REQUIRED_FIELD", "A required recipe field is missing.",
                out errorCode, out error);
        }
        if (element.ValueKind != JsonValueKind.String)
            return Failure("TEST_RECIPE_FIELD_INVALID", "A recipe string field has the wrong type.",
                out errorCode, out error);
        value = element.GetString()?.Trim();
        if (required && string.IsNullOrWhiteSpace(value))
            return Failure("TEST_RECIPE_REQUIRED_FIELD", "A required recipe field is empty.",
                out errorCode, out error);
        return true;
    }

    private static bool TryScalar(JsonElement element, out string value)
    {
        value = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString();
                return !string.IsNullOrWhiteSpace(value);
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = element.GetBoolean() ? "true" : "false";
                return true;
            case JsonValueKind.Number when element.TryGetInt32(out int number):
                value = number.ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    private static bool TryBoundedInt(JsonElement element, int min, int max, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) &&
            value >= min && value <= max;
    }

    private static bool BooleanProperty(JsonElement parent, string name, bool fallback) =>
        parent.TryGetProperty(name, out JsonElement value) ? value.GetBoolean() : fallback;

    private static bool Failure(string code, string message, out string errorCode, out string error)
    {
        errorCode = code;
        error = message;
        return false;
    }
}

internal abstract class RecipeResponse
{
    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ExitCode { get; set; }
}

internal sealed class RecipeListResponse : RecipeResponse
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.TestRecipeList;
    [JsonPropertyName("recipes")] public List<RecipeListItem> Recipes { get; init; } = new();
    [JsonPropertyName("errorCode")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ErrorCode { get; init; }
    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Error { get; init; }
}

internal sealed class RecipeListItem
{
    [JsonPropertyName("id")] public string Id { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; }
}

internal sealed class RecipeShowResponse : RecipeResponse
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.TestRecipeShow;
    [JsonPropertyName("recipe")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RecipeInfo Recipe { get; init; }
    [JsonPropertyName("errorCode")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ErrorCode { get; init; }
    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Error { get; init; }
}

internal sealed class RecipeInfo
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; }
    [JsonPropertyName("id")] public string Id { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; }
    [JsonPropertyName("projects")] public List<string> Projects { get; init; } = new();
    [JsonPropertyName("inputs")] public Dictionary<string, string> Inputs { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("requiresReady")] public bool RequiresReady { get; init; }
    [JsonPropertyName("allowInGameMutation")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowsInGameMutation { get; init; }
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; init; }
    [JsonPropertyName("success")] public RecipeSuccessInfo Success { get; init; }
    [JsonPropertyName("operations")] public List<RecipeOperationInfo> Operations { get; init; } = new();
    [JsonPropertyName("budget")] public RecipeBudgetInfo Budget { get; init; }
}

internal sealed class RecipeSuccessInfo
{
    [JsonPropertyName("quicktestReady")] public bool QuicktestReady { get; init; }
    [JsonPropertyName("companionVerified")] public bool CompanionVerified { get; init; }
    [JsonPropertyName("rimBridgeReady")] public bool RimBridgeReady { get; init; }
}

internal sealed class RecipeOperationInfo
{
    [JsonPropertyName("tool")] public string Tool { get; init; }
    [JsonPropertyName("arguments")] public JsonElement Arguments { get; init; }
    [JsonPropertyName("expect")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecipeOperationExpectationInfo Expectation { get; init; }
}

internal sealed class RecipeOperationExpectationInfo
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("assertions")] public List<RecipeAssertionInfo> Assertions { get; init; } = new();
}

internal sealed class RecipeAssertionInfo
{
    [JsonPropertyName("pointer")] public string Pointer { get; init; }
    [JsonPropertyName("exists")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Exists { get; init; }
    [JsonPropertyName("equals")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ExpectedValue { get; init; }
    [JsonPropertyName("greaterThan")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? GreaterThan { get; init; }
    [JsonPropertyName("lessThan")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LessThan { get; init; }
}

internal sealed class RecipeBudgetInfo
{
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; init; }
    [JsonPropertyName("maxRimWorldLaunches")] public int MaxRimWorldLaunches { get; init; }
    [JsonPropertyName("maxRecipeAttempts")] public int MaxRecipeAttempts { get; init; }
    [JsonPropertyName("maxCoordinatorRefreshes")] public int MaxCoordinatorRefreshes { get; init; }
    [JsonPropertyName("stopOnRepeatedFailureFingerprint")] public bool StopOnRepeatedFailureFingerprint { get; init; }
    [JsonPropertyName("maxRepeatedFailureCount")] public int MaxRepeatedFailureCount { get; init; }
}

internal sealed class RecipePlanStep
{
    [JsonPropertyName("action")] public string Action { get; init; }
    [JsonPropertyName("reasonCode")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ReasonCode { get; init; }
    [JsonPropertyName("condition")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Condition { get; init; }
    [JsonPropertyName("recipe")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Recipe { get; init; }
}

internal sealed class RecipePlanData
{
    internal string Recipe { get; init; }
    internal string ProfileFingerprint { get; init; }
    internal List<TestInputValue> TestInputs { get; init; } = new();
    internal bool AlreadySatisfied { get; init; }
    internal int EstimatedRimWorldLaunches { get; init; }
    internal List<RecipePlanStep> Steps { get; init; } = new();
    internal string NextAction { get; init; }
    internal List<string> BlockedBy { get; init; } = new();
    internal string ErrorCode { get; init; }
    internal string Error { get; init; }
}

internal sealed class RecipePlanResponse : RecipeResponse
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.TestRecipePlan;
    [JsonPropertyName("recipe")] public string Recipe { get; init; }
    [JsonPropertyName("alreadySatisfied")] public bool AlreadySatisfied { get; init; }
    [JsonPropertyName("estimatedRimWorldLaunches")] public int EstimatedRimWorldLaunches { get; init; }
    [JsonPropertyName("steps")] public List<RecipePlanStep> Steps { get; init; } = new();
    [JsonPropertyName("nextAction")] public string NextAction { get; init; }
    [JsonPropertyName("blockedBy")] public List<string> BlockedBy { get; init; } = new();
    [JsonPropertyName("errorCode")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ErrorCode { get; init; }
    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Error { get; init; }

    internal static RecipePlanResponse From(RecipePlanData plan, int exitCode = 0) => new()
    {
        ExitCode = exitCode,
        Recipe = plan.Recipe,
        AlreadySatisfied = plan.AlreadySatisfied,
        EstimatedRimWorldLaunches = plan.EstimatedRimWorldLaunches,
        Steps = plan.Steps,
        NextAction = plan.NextAction,
        BlockedBy = plan.BlockedBy,
        ErrorCode = plan.ErrorCode,
        Error = plan.Error
    };
}

internal sealed class AgentRecipePlanResponse : AgentResponse
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.AgentPlan;
    [JsonPropertyName("recipe")] public string Recipe { get; init; }
    [JsonPropertyName("alreadySatisfied")] public bool AlreadySatisfied { get; init; }
    [JsonPropertyName("estimatedRimWorldLaunches")] public int EstimatedRimWorldLaunches { get; init; }
    [JsonPropertyName("steps")] public List<RecipePlanStep> Steps { get; init; } = new();
    [JsonPropertyName("nextAction")] public string NextAction { get; init; }
    [JsonPropertyName("blockedBy")] public List<string> BlockedBy { get; init; } = new();
    [JsonPropertyName("errorCode")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ErrorCode { get; init; }
    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Error { get; init; }

    internal static AgentRecipePlanResponse From(RecipePlanData plan, int exitCode = 0) => new()
    {
        ExitCode = exitCode,
        Recipe = plan.Recipe,
        AlreadySatisfied = plan.AlreadySatisfied,
        EstimatedRimWorldLaunches = plan.EstimatedRimWorldLaunches,
        Steps = plan.Steps,
        NextAction = plan.NextAction,
        BlockedBy = plan.BlockedBy,
        ErrorCode = plan.ErrorCode,
        Error = plan.Error
    };
}

internal sealed class RecipeBudgetResult
{
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; init; }
    [JsonPropertyName("maxRimWorldLaunches")] public int MaxRimWorldLaunches { get; init; }
    [JsonPropertyName("maxRecipeAttempts")] public int MaxRecipeAttempts { get; init; }
    [JsonPropertyName("maxCoordinatorRefreshes")] public int MaxCoordinatorRefreshes { get; init; }
    [JsonPropertyName("stopOnRepeatedFailureFingerprint")] public bool StopOnRepeatedFailureFingerprint { get; init; }
    [JsonPropertyName("maxRepeatedFailureCount")] public int MaxRepeatedFailureCount { get; init; }
    [JsonPropertyName("launchesConsumed")] public int LaunchesConsumed { get; init; }
    [JsonPropertyName("recipeAttemptsConsumed")] public int RecipeAttemptsConsumed { get; init; }
    [JsonPropertyName("coordinatorRefreshesConsumed")] public int CoordinatorRefreshesConsumed { get; init; }
}

internal sealed class RecipeOperationResult
{
    [JsonPropertyName("tool")] public string Tool { get; init; }
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("expectedSuccess")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ExpectedSuccess { get; init; }
    [JsonPropertyName("errorCode")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ErrorCode { get; init; }
    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Error { get; init; }
    [JsonPropertyName("evidence")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Evidence { get; init; }
    [JsonPropertyName("result")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? Result { get; init; }
    [JsonPropertyName("assertions")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RecipeAssertionResult> Assertions { get; init; }
}

internal sealed class RecipeAssertionResult
{
    [JsonPropertyName("pointer")] public string Pointer { get; init; }
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; init; }
}

internal sealed class RecipeRunResponse : RecipeResponse
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = DevBridgeSchemaVersions.TestRecipeRun;
    [JsonPropertyName("recipe")] public string Recipe { get; init; }
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("generation")] public int Generation { get; init; }
    [JsonPropertyName("restartRequired")] public bool RestartRequired { get; init; }
    [JsonPropertyName("launchesConsumed")] public int LaunchesConsumed { get; init; }
    [JsonPropertyName("leaseId")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string LeaseId { get; init; }
    [JsonPropertyName("evidence")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Evidence { get; init; }
    [JsonPropertyName("failureFingerprint")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string FailureFingerprint { get; init; }
    [JsonPropertyName("finalNextAction")] public string FinalNextAction { get; init; }
    [JsonPropertyName("budget")] public RecipeBudgetResult Budget { get; init; }
    [JsonPropertyName("operations")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<RecipeOperationResult> Operations { get; init; }
    [JsonPropertyName("errorCode")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string ErrorCode { get; init; }
    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Error { get; init; }
}

internal sealed partial class CoordinatorState
{
    private static readonly Action<string> SilentRecipeEmit = _ => { };
    private const int CoordinatorRecipeMaxLaunches = 1;
    private const int CoordinatorRecipeMaxAttempts = 1;
    private const int CoordinatorRecipeMaxRefreshes = 8;
    private const int CoordinatorRecipeMaxTimeoutSeconds = 900;

    private static bool IsPureRecipeCommand(BridgeRequest request)
    {
        IReadOnlyList<string> arguments = request?.Arguments ?? new List<string>();
        if (!string.Equals(request?.Command, "test", StringComparison.OrdinalIgnoreCase) ||
            arguments.Count < 2 || !string.Equals(arguments[0], "recipe",
                StringComparison.OrdinalIgnoreCase))
            return false;
        string operation = arguments[1]?.Trim();
        return string.Equals(operation, "list", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, "show", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, "plan", StringComparison.OrdinalIgnoreCase);
    }

    private int TestRecipe(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit, Func<bool> connected)
    {
        if (arguments.Count < 2)
        {
            emit("Usage: DevBridge.cmd test recipe list|show <id>|plan <id>|run <id> [budget options]");
            return 2;
        }
        string operation = arguments[1]?.Trim().ToLowerInvariant();
        return operation switch
        {
            "list" => RecipeList(arguments, request),
            "show" => RecipeShow(arguments, request),
            "plan" => RecipePlan(arguments, request),
            "run" => RecipeRun(arguments, request, emit, connected),
            _ => RecipeUsage(request, "unknown recipe operation")
        };
    }

    private int RecipeList(IReadOnlyList<string> arguments, BridgeRequest request)
    {
        if (!RecipeCatalog.TryLoad(root, out RecipeCatalog catalog, out string code, out string error))
        {
            request.RecipeResponse = new RecipeListResponse { ErrorCode = code, Error = error, ExitCode = 4 };
            return 4;
        }
        if (arguments.Skip(2).Any(value => !string.Equals(value, "--json", StringComparison.OrdinalIgnoreCase)))
            return RecipeUsage(request, "recipe list accepts no options");
        request.RecipeResponse = new RecipeListResponse
        {
            Recipes = catalog.Recipes.Select(value => new RecipeListItem
            {
                Id = value.Id,
                Description = value.Description
            }).ToList()
        };
        return 0;
    }

    private int RecipeShow(IReadOnlyList<string> arguments, BridgeRequest request)
    {
        if (arguments.Count < 3 || string.IsNullOrWhiteSpace(arguments[2]) || arguments.Count > 4 ||
            (arguments.Count == 4 && !string.Equals(arguments[3], "--json", StringComparison.OrdinalIgnoreCase)))
            return RecipeUsage(request, "recipe show requires one recipe id");
        if (!RecipeCatalog.TryLoad(root, out RecipeCatalog catalog, out string code, out string error))
        {
            request.RecipeResponse = new RecipeShowResponse { ErrorCode = code, Error = error, ExitCode = 4 };
            return 4;
        }
        if (!catalog.TryGet(arguments[2].Trim(), out TestRecipeDefinition recipe))
        {
            request.RecipeResponse = new RecipeShowResponse { ErrorCode = "TEST_RECIPE_NOT_FOUND",
                Error = "The requested recipe is not present in the repository-owned catalog.", ExitCode = 4 };
            return 4;
        }
        request.RecipeResponse = new RecipeShowResponse { Recipe = RecipeInfoFor(recipe) };
        return 0;
    }

    private int RecipePlan(IReadOnlyList<string> arguments, BridgeRequest request)
    {
        if (arguments.Count < 3 || string.IsNullOrWhiteSpace(arguments[2]) || arguments.Count > 4 ||
            (arguments.Count == 4 && !string.Equals(arguments[3], "--json", StringComparison.OrdinalIgnoreCase)))
            return RecipeUsage(request, "recipe plan requires one recipe id");
        RecipePlanData plan = BuildRecipePlan(arguments[2].Trim(), request);
        int exitCode = plan.ErrorCode == null ? 0 : 4;
        request.RecipeResponse = RecipePlanResponse.From(plan, exitCode);
        return exitCode;
    }

    private int RecipeUsage(BridgeRequest request, string detail)
    {
        request.RecipeResponse = new RecipeListResponse
        {
            ErrorCode = "TEST_RECIPE_USAGE",
            Error = detail,
            ExitCode = 2
        };
        return 2;
    }

    private RecipePlanData BuildRecipePlan(string id, BridgeRequest request)
    {
        if (!RecipeCatalog.TryLoad(root, out RecipeCatalog catalog, out string code, out string error))
            return FailedRecipePlan(id, code, error);
        if (!catalog.TryGet(id, out TestRecipeDefinition recipe))
            return FailedRecipePlan(id, "TEST_RECIPE_NOT_FOUND", "The requested recipe is not present in the repository-owned catalog.");

        int resolutionExit = ResolveProjectPlan(BuildRecipeResolutionArguments(recipe), request, SilentRecipeEmit);
        ProjectResolutionResult resolution = request.ProjectResolutionResult;
        if (resolutionExit != 0 || resolution == null || !resolution.Success)
            return FailedRecipePlan(recipe.Id, resolution?.ErrorCode ?? "TEST_RECIPE_PLAN_FAILED",
                resolution?.Errors?.FirstOrDefault()?.Message ?? "The recipe profile could not be planned.");

        bool ready = false;
        bool quicktestReady = false;
        bool companionReady = false;
        bool rimBridgeReady = false;
        bool profileMatches = false;
        bool pendingMatch = false;
        string failureCode = null;
        lock (gate)
        {
            // This is intentionally a planning snapshot.  Do not synchronize,
            // prune, refresh policy, save, or otherwise adopt state here.
            ready = state.Phase == BridgePhase.READY && !state.RestartPending;
            AgentQuicktestSummary quicktest = BuildQuicktestSummaryLocked();
            quicktestReady = quicktest.State == "ready";
            companionReady = state.RimBridge?.CompanionVerified == true;
            rimBridgeReady = state.RimBridge?.LifecycleState == RimBridgeLifecycleState.READY;
            profileMatches = string.Equals(resolution.ProfileFingerprint, state.ProfileFingerprint,
                StringComparison.Ordinal) &&
                (state.RequestedProjects ?? new List<string>()).SequenceEqual(recipe.Projects,
                    StringComparer.Ordinal) &&
                TestGenerationInputs.AreEquivalent(resolution.TestInputs, state.TestInputs);
            pendingMatch = state.RestartPending && string.Equals(resolution.ProfileFingerprint,
                state.FrozenProfileFingerprint, StringComparison.Ordinal) &&
                (state.FrozenRequestedProjects ?? new List<string>()).SequenceEqual(recipe.Projects,
                    StringComparer.Ordinal) &&
                TestGenerationInputs.AreEquivalent(resolution.TestInputs, state.FrozenTestInputs);
            failureCode = state.ErrorCode ?? state.TerminalFailureCode;
        }

        bool alreadySatisfied = (!recipe.RequiresReady || ready) && profileMatches &&
            (!recipe.Success.QuicktestReady || quicktestReady) &&
            (!recipe.Success.CompanionVerified || companionReady) &&
            (!recipe.Success.RimBridgeReady || rimBridgeReady);
        List<string> blocked = new();
        if (resolution.WouldRequireRestart || !profileMatches)
            blocked.Add("GENERATION_INPUT_MISMATCH");
        if (recipe.RequiresReady && !ready && !pendingMatch)
            blocked.Add(failureCode ?? "NOT_READY");
        if (recipe.Success.CompanionVerified && !companionReady)
            blocked.Add("RIMBRIDGE_COMPANION_REQUIRED");
        if (recipe.Success.RimBridgeReady && !rimBridgeReady)
            blocked.Add("RIMBRIDGE_NOT_READY");

        List<RecipePlanStep> steps = new();
        int estimatedLaunches = 0;
        if (!alreadySatisfied)
        {
            if (!pendingMatch && (!profileMatches || (recipe.RequiresReady && !ready)))
            {
                estimatedLaunches = 1;
                steps.Add(new RecipePlanStep
                {
                    Action = "restart",
                    ReasonCode = resolution.WouldRequireRestart || !profileMatches
                        ? "GENERATION_INPUT_MISMATCH" : "NOT_READY"
                });
            }
            if (recipe.RequiresReady)
                steps.Add(new RecipePlanStep { Action = "wait-event", Condition = "ready" });
            steps.Add(new RecipePlanStep { Action = "run-recipe", Recipe = recipe.Id });
        }

        return new RecipePlanData
        {
            Recipe = recipe.Id,
            ProfileFingerprint = resolution.ProfileFingerprint,
            TestInputs = TestGenerationInputs.CloneValues(resolution.TestInputs),
            AlreadySatisfied = alreadySatisfied,
            EstimatedRimWorldLaunches = estimatedLaunches,
            Steps = steps,
            NextAction = alreadySatisfied ? "none" : steps.FirstOrDefault()?.Action ?? "run-recipe",
            BlockedBy = blocked.Distinct(StringComparer.Ordinal).OrderBy(value => value,
                StringComparer.Ordinal).ToList()
        };
    }

    private static RecipePlanData FailedRecipePlan(string id, string code, string error) => new()
    {
        Recipe = id,
        AlreadySatisfied = false,
        EstimatedRimWorldLaunches = 0,
        NextAction = "inspect-evidence",
        BlockedBy = new List<string> { code },
        ErrorCode = code,
        Error = error
    };

    private int RecipeRun(IReadOnlyList<string> arguments, BridgeRequest request,
        Action<string> emit, Func<bool> connected)
    {
        if (!TryParseRecipeRun(arguments, out string id, out RecipeCallerBudget callerBudget,
                out string parseCode, out string parseError))
        {
            request.RecipeResponse = RecipeRunFailure(id, parseCode, parseError, null, 0, 0,
                "inspect-evidence", callerBudget: null);
            return 2;
        }

        RecipePlanData plan = BuildRecipePlan(id, request);
        if (plan.ErrorCode != null)
        {
            request.RecipeResponse = RecipeRunFailure(id, plan.ErrorCode, plan.Error, null, 0, 0,
                "inspect-evidence", null);
            return 4;
        }
        RecipeCatalog.TryLoad(root, out RecipeCatalog catalog, out _, out _);
        catalog.TryGet(id, out TestRecipeDefinition recipe);
        EffectiveRecipeBudget budget = EffectiveBudget(recipe.Budget, callerBudget);
        RecipeBudgetResult budgetResult = new()
        {
            TimeoutSeconds = budget.TimeoutSeconds,
            MaxRimWorldLaunches = budget.MaxRimWorldLaunches,
            MaxRecipeAttempts = budget.MaxRecipeAttempts,
            MaxCoordinatorRefreshes = budget.MaxCoordinatorRefreshes,
            StopOnRepeatedFailureFingerprint = budget.StopOnRepeatedFailureFingerprint,
            MaxRepeatedFailureCount = budget.MaxRepeatedFailureCount,
            RecipeAttemptsConsumed = 0
        };
        if (budget.StopOnRepeatedFailureFingerprint && budget.MaxRepeatedFailureCount > 0)
        {
            FailureOccurrenceSummary repeated = null;
            lock (gate)
            {
                repeated = FindEquivalentRecipeFailureLocked(id, plan.ProfileFingerprint,
                    plan.TestInputs, budget.MaxRepeatedFailureCount);
            }
            if (repeated != null)
            {
                request.RecipeResponse = RecipeRepeatedFailure(id, repeated, budgetResult);
                return 4;
            }
        }
        if (budget.MaxRecipeAttempts < 1)
        {
            request.RecipeResponse = RecipeRunFailure(id, "AUTONOMOUS_BUDGET_EXHAUSTED",
                "The effective recipe-attempt budget is zero; no durable action was attempted.",
                null, 0, 0, "run-recipe", budgetResult);
            return 4;
        }
        budgetResult = WithAttemptConsumed(budgetResult);

        long deadline = Stopwatch.GetTimestamp() +
            (long)(budget.TimeoutSeconds * (double)Stopwatch.Frequency);
        bool BudgetAvailable() => connected() && Stopwatch.GetTimestamp() <= deadline;
        int initialGeneration;
        bool pendingBefore;
        HashSet<string> existingRegistrations;
        lock (gate)
        {
            initialGeneration = state.Generation;
            pendingBefore = state.RestartPending;
            existingRegistrations = ActiveRecipeRegistrationIdsLocked(request, recipe.Projects);
        }

        bool restartRequired = !plan.AlreadySatisfied;
        int launchesConsumed = 0;
        bool leavePendingForRecovery = false;
        string leaseId = null;
        List<RecipeOperationResult> operationResults = new();
        try
        {
            if (restartRequired)
            {
                bool matchingPending = pendingBefore && plan.EstimatedRimWorldLaunches == 0;
                if (!matchingPending && budget.MaxRimWorldLaunches < 1)
                    return SetRecipeFailure(request, id, "AUTONOMOUS_BUDGET_EXHAUSTED",
                        "The effective RimWorld-launch budget is zero; no restart was attempted.",
                        initialGeneration, restartRequired, launchesConsumed, null, budgetResult,
                        "restart", null, plan);
                BridgeRequest restartRequest = RecipeLifecycleRequest(request,
                    BuildRecipeRestartArguments(recipe));
                int restartResult = Restart(restartRequest, emit, BudgetAvailable);
                if (!pendingBefore && restartResult == 0)
                    launchesConsumed = 1;
                lock (gate)
                    leavePendingForRecovery = state.RestartPending;
                if (restartResult != 0)
                {
                    budgetResult = WithConsumed(budgetResult, launchesConsumed, 0);
                    return SetRecipeFailure(request, id,
                        !BudgetAvailable() ? "AUTONOMOUS_BUDGET_EXHAUSTED" :
                            restartRequest.TestInputErrorCode ?? "RECIPE_RESTART_FAILED",
                        !BudgetAvailable() ? "The recipe budget expired. The accepted lifecycle operation was left to recover safely." :
                            "The recipe restart did not reach READY.",
                        CurrentGenerationForRecipe(), restartRequired, launchesConsumed, null, budgetResult,
                        leavePendingForRecovery ? "wait-event" : "inspect-evidence", null, plan);
                }
            }

            if (!BudgetAvailable())
                return SetRecipeFailure(request, id, "AUTONOMOUS_BUDGET_EXHAUSTED",
                    "The recipe budget expired before lease acquisition; no unsafe cleanup or restart was attempted.",
                    CurrentGenerationForRecipe(), restartRequired, launchesConsumed, null,
                    WithConsumed(budgetResult, launchesConsumed, 0), "wait-event", null, plan);

            if (recipe.RequiresReady || recipe.Operations.Count > 0)
            {
                TestLease lease = null;
                int beginResult = BeginLease(request, SilentRecipeEmit, connected,
                    acquired: value => lease = value, budgetAvailable: BudgetAvailable);
                if (beginResult != 0 || lease == null)
                {
                    lock (gate)
                        leavePendingForRecovery = state.RestartPending;
                    return SetRecipeFailure(request, id,
                        !BudgetAvailable() ? "AUTONOMOUS_BUDGET_EXHAUSTED" : "RECIPE_LEASE_FAILED",
                        !BudgetAvailable() ? "The recipe budget expired while waiting for a safe lease boundary." :
                            "The required DevBridge test lease could not be acquired.",
                        CurrentGenerationForRecipe(), restartRequired, launchesConsumed, null,
                        WithConsumed(budgetResult, launchesConsumed, 0),
                        leavePendingForRecovery ? "wait-event" : "acquire-lease", null, plan);
                }
                leaseId = lease.Id;
            }

            foreach (RecipeOperationDefinition operation in recipe.Operations)
            {
                if (!BudgetAvailable())
                    return SetRecipeFailure(request, id, "AUTONOMOUS_BUDGET_EXHAUSTED",
                        operation.Expectation == null
                            ? "The recipe budget expired before the next read-only operation."
                            : "The recipe budget expired before the next RimBridge operation.",
                        CurrentGenerationForRecipe(), restartRequired, launchesConsumed, leaseId,
                        WithConsumed(budgetResult, launchesConsumed, 0),
                        "wait-event", operationResults, plan);
                List<string> callArguments = new() { operation.ToolName,
                    JsonSerializer.Serialize(operation.Arguments, CoordinatorSerialization.JsonOptions) };
                if (!string.IsNullOrWhiteSpace(leaseId))
                {
                    callArguments.Add("--lease");
                    callArguments.Add(leaseId);
                }
                int operationExit = BridgeCallCommand(callArguments, request, SilentRecipeEmit);
                RimBridgeRouteResult route = request.RimBridgeRouteResult;
                RecipeOperationResult operationResult = EvaluateRecipeOperation(operation, operationExit, route);
                operationResults.Add(operationResult);
                if (!operationResult.Success)
                    return SetRecipeFailure(request, id, operationResult.ErrorCode ?? "RECIPE_OPERATION_FAILED",
                        operationResult.Error ?? "The recipe operation did not satisfy its bounded expectation.",
                        CurrentGenerationForRecipe(), restartRequired, launchesConsumed, leaseId,
                        WithConsumed(budgetResult, launchesConsumed, 0),
                        "inspect-evidence", operationResults, plan);
            }

            RecipeRunResponse result = BuildRecipeSuccess(id, recipe, request, restartRequired,
                launchesConsumed, leaseId, budgetResult, operationResults, plan);
            request.RecipeResponse = result;
            return result.Success ? 0 : 4;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(leaseId))
                EndLease(request, new[] { "end", leaseId }, SilentRecipeEmit);
            lock (gate)
                leavePendingForRecovery |= state.RestartPending;
            if (!leavePendingForRecovery)
                ReleaseRecipeRegistrations(request, recipe, existingRegistrations);
        }
    }

    private RecipeRunResponse BuildRecipeSuccess(string id, TestRecipeDefinition recipe,
        BridgeRequest request, bool restartRequired, int launchesConsumed, string leaseId,
        RecipeBudgetResult budget, List<RecipeOperationResult> operations, RecipePlanData plan)
    {
        bool success;
        int generation;
        string nextAction;
        string failure = null;
        lock (gate)
        {
            generation = state.Generation;
            AgentQuicktestSummary quicktest = BuildQuicktestSummaryLocked();
            success = (!recipe.RequiresReady || (state.Phase == BridgePhase.READY && !state.RestartPending)) &&
                (!recipe.Success.QuicktestReady || quicktest.State == "ready") &&
                (!recipe.Success.CompanionVerified || state.RimBridge?.CompanionVerified == true) &&
                (!recipe.Success.RimBridgeReady || state.RimBridge?.LifecycleState == RimBridgeLifecycleState.READY) &&
                operations.All(value => value.Success);
            if (!success)
                failure = state.ErrorCode ?? quicktest.FailureCode ?? "RECIPE_EVIDENCE_MISMATCH";
            nextAction = success ? "status" :
                state.RestartPending ? "wait-event" : "inspect-evidence";
        }
        string failureFingerprint = success ? null : RecordRecipeFailure(id, failure,
            "The recipe did not produce all expected structured evidence.", generation,
            plan?.ProfileFingerprint, plan?.TestInputs);
        return new RecipeRunResponse
        {
            Recipe = id,
            Success = success,
            Generation = generation,
            RestartRequired = restartRequired,
            LaunchesConsumed = launchesConsumed,
            LeaseId = leaseId,
            Evidence = success ? "Runtime/readiness.json" : null,
            FailureFingerprint = success ? null : failureFingerprint ?? failure,
            FinalNextAction = nextAction,
            Budget = WithConsumed(budget, launchesConsumed, 0),
            Operations = operations.Count == 0 ? null : operations,
            ErrorCode = success ? null : failure,
            Error = success ? null : "The recipe did not produce all expected structured evidence."
        };
    }

    private int SetRecipeFailure(BridgeRequest request, string id, string code, string error,
        int generation, bool restartRequired, int launchesConsumed, string leaseId,
        RecipeBudgetResult budget, string nextAction, List<RecipeOperationResult> operations,
        RecipePlanData plan = null)
    {
        string failureFingerprint = null;
        if (plan != null && !string.Equals(code, "AUTONOMOUS_BUDGET_EXHAUSTED",
                StringComparison.Ordinal))
            failureFingerprint = RecordRecipeFailure(id, code, error, generation,
                plan.ProfileFingerprint, plan.TestInputs);
        request.RecipeResponse = new RecipeRunResponse
        {
            ExitCode = 4,
            Recipe = id,
            Success = false,
            Generation = generation,
            RestartRequired = restartRequired,
            LaunchesConsumed = launchesConsumed,
            LeaseId = leaseId,
            FailureFingerprint = failureFingerprint ?? code,
            FinalNextAction = nextAction,
            Budget = budget,
            Operations = operations,
            ErrorCode = code,
            Error = error
        };
        return 4;
    }

    private static RecipeRunResponse RecipeRunFailure(string id, string code, string error,
        string leaseId, int generation, int launchesConsumed, string nextAction,
        RecipeBudgetResult callerBudget)
    {
        return new RecipeRunResponse
        {
            ExitCode = 4,
            Recipe = id,
            Success = false,
            Generation = generation,
            LaunchesConsumed = launchesConsumed,
            LeaseId = leaseId,
            FailureFingerprint = code,
            FinalNextAction = nextAction,
            Budget = callerBudget,
            ErrorCode = code,
            Error = error
        };
    }

    private static RecipeRunResponse RecipeRepeatedFailure(string id,
        FailureOccurrenceSummary occurrence, RecipeBudgetResult budget)
    {
        return new RecipeRunResponse
        {
            ExitCode = 4,
            Recipe = id,
            Success = false,
            Generation = occurrence?.LastSeenGeneration ?? 0,
            FailureFingerprint = occurrence?.FailureFingerprint,
            Evidence = occurrence?.EvidenceId,
            FinalNextAction = "inspect-evidence",
            Budget = budget,
            ErrorCode = "AUTONOMOUS_REPEATED_FAILURE",
            Error = "An equivalent recipe reproduction already reached the configured repeated-failure limit; no new launch or operation was attempted."
        };
    }

    private static RecipeBudgetResult WithConsumed(RecipeBudgetResult value, int launches, int refreshes) => new()
    {
        TimeoutSeconds = value?.TimeoutSeconds ?? 0,
        MaxRimWorldLaunches = value?.MaxRimWorldLaunches ?? 0,
        MaxRecipeAttempts = value?.MaxRecipeAttempts ?? 0,
        MaxCoordinatorRefreshes = value?.MaxCoordinatorRefreshes ?? 0,
        StopOnRepeatedFailureFingerprint = value?.StopOnRepeatedFailureFingerprint ?? true,
        MaxRepeatedFailureCount = value?.MaxRepeatedFailureCount ?? 0,
        LaunchesConsumed = launches,
        RecipeAttemptsConsumed = value?.RecipeAttemptsConsumed ?? 0,
        CoordinatorRefreshesConsumed = refreshes
    };

    private static RecipeBudgetResult WithAttemptConsumed(RecipeBudgetResult value) => new()
    {
        TimeoutSeconds = value?.TimeoutSeconds ?? 0,
        MaxRimWorldLaunches = value?.MaxRimWorldLaunches ?? 0,
        MaxRecipeAttempts = value?.MaxRecipeAttempts ?? 0,
        MaxCoordinatorRefreshes = value?.MaxCoordinatorRefreshes ?? 0,
        StopOnRepeatedFailureFingerprint = value?.StopOnRepeatedFailureFingerprint ?? true,
        MaxRepeatedFailureCount = value?.MaxRepeatedFailureCount ?? 0,
        LaunchesConsumed = value?.LaunchesConsumed ?? 0,
        RecipeAttemptsConsumed = 1,
        CoordinatorRefreshesConsumed = value?.CoordinatorRefreshesConsumed ?? 0
    };

    private int CurrentGenerationForRecipe()
    {
        lock (gate)
            return state.Generation;
    }

    private static RecipeOperationResult EvaluateRecipeOperation(RecipeOperationDefinition operation,
        int operationExit, RimBridgeRouteResult route)
    {
        bool isV2 = operation.Expectation != null;
        RecipeOperationExpectation expectation = operation.Expectation ?? new RecipeOperationExpectation();
        bool actualSuccess = operationExit == 0 && route?.Success == true;
        bool success = actualSuccess == expectation.ExpectedSuccess;
        List<RecipeAssertionResult> assertions = null;
        if (success && expectation.Assertions.Count > 0)
        {
            assertions = new List<RecipeAssertionResult>();
            foreach (RecipeAssertionDefinition assertion in expectation.Assertions)
                assertions.Add(EvaluateRecipeAssertion(assertion, route?.Payload));
            success = assertions.All(value => value.Success);
        }

        string errorCode = null;
        string error = null;
        if (!success)
        {
            if (actualSuccess != expectation.ExpectedSuccess)
            {
                errorCode = expectation.ExpectedSuccess
                    ? route?.ErrorCode ?? "RECIPE_OPERATION_FAILED"
                    : "RECIPE_EXPECTED_FAILURE_NOT_RETURNED";
                error = expectation.ExpectedSuccess
                    ? isV2 ? "The RimBridge operation did not succeed as expected."
                        : "A policy-approved read-only recipe operation failed."
                    : "The RimBridge operation succeeded when failure was expected.";
            }
            else
            {
                errorCode = "RECIPE_ASSERTION_FAILED";
                error = "A bounded RimBridge result assertion failed.";
            }
        }
        return new RecipeOperationResult
        {
            Tool = operation.ToolName,
            Success = success,
            ExpectedSuccess = isV2 ? expectation.ExpectedSuccess : null,
            ErrorCode = errorCode,
            Error = error,
            Evidence = route == null ? null : "generation/" + route.Generation + "/rimbridge",
            Result = BoundRecipePayload(route?.Payload),
            Assertions = assertions
        };
    }

    private static RecipeAssertionResult EvaluateRecipeAssertion(RecipeAssertionDefinition assertion,
        JsonElement? payload)
    {
        JsonElement selected = default;
        bool found = payload.HasValue && TrySelectJsonPointer(payload.Value, assertion.Pointer,
            out selected);
        bool success;
        string error = null;
        if (assertion.Exists.HasValue)
            success = found == assertion.Exists.Value;
        else if (!found)
        {
            success = false;
            error = "The selected result field was not present.";
        }
        else if (assertion.ExpectedValue.HasValue)
        {
            success = ScalarEquals(selected, assertion.ExpectedValue.Value);
            if (!success)
                error = "The selected scalar did not match the expected value.";
        }
        else if (assertion.GreaterThan.HasValue)
        {
            success = TryFiniteDouble(selected, out double value) && value > assertion.GreaterThan.Value;
            if (!success)
                error = "The selected numeric value was not greater than expected.";
        }
        else
        {
            success = TryFiniteDouble(selected, out double value) && value < assertion.LessThan.Value;
            if (!success)
                error = "The selected numeric value was not less than expected.";
        }
        if (!success && error == null)
            error = "The selected field existence did not match the expectation.";
        return new RecipeAssertionResult { Pointer = assertion.Pointer, Success = success, Error = error };
    }

    private static bool TrySelectJsonPointer(JsonElement root, string pointer, out JsonElement value)
    {
        value = default;
        if (pointer.Length == 0)
        {
            value = root;
            return true;
        }
        if (pointer[0] != '/')
            return false;
        JsonElement current = root;
        string[] segments = pointer.Split('/');
        if (segments.Length > 17)
            return false;
        for (int index = 1; index < segments.Length; index++)
        {
            string segment = segments[index].Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out current))
                    return false;
            }
            else if (current.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out int item) &&
                item >= 0 && item < current.GetArrayLength())
            {
                current = current[item];
            }
            else
                return false;
        }
        value = current;
        return true;
    }

    private static bool ScalarEquals(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind)
            return actual.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number &&
                TryFiniteDouble(actual, out _) && TryFiniteDouble(expected, out _) &&
                actual.GetDouble() == expected.GetDouble();
        return actual.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.String => string.Equals(actual.GetString(), expected.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => actual.GetBoolean() == expected.GetBoolean(),
            JsonValueKind.Number => TryFiniteDouble(actual, out double actualNumber) &&
                TryFiniteDouble(expected, out double expectedNumber) && actualNumber == expectedNumber,
            _ => false
        };
    }

    private static bool TryFiniteDouble(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value) &&
            !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static JsonElement? BoundRecipePayload(JsonElement? payload)
    {
        if (!payload.HasValue)
            return null;
        string json = JsonSerializer.Serialize(payload.Value, CoordinatorSerialization.JsonOptions);
        return json.Length <= 4096 ? payload.Value.Clone() : null;
    }

    private HashSet<string> ActiveRecipeRegistrationIdsLocked(BridgeRequest request,
        IReadOnlyList<string> projects)
    {
        string owner = StableProjectOwner(request);
        string session = StableProjectSession(request);
        return new HashSet<string>((state.ProjectIntents ?? new List<ProjectIntentRegistration>())
            .Where(value => value != null && value.Status == "ACTIVE" && value.Owner == owner &&
                value.SessionId == session && (projects.Count == 0 ||
                    value.RequestedProjects.Any(project => projects.Contains(project, StringComparer.Ordinal))))
            .Select(value => value.Id), StringComparer.OrdinalIgnoreCase);
    }

    private void ReleaseRecipeRegistrations(BridgeRequest request, TestRecipeDefinition recipe,
        HashSet<string> existing)
    {
        List<string> release;
        lock (gate)
        {
            release = (state.ProjectIntents ?? new List<ProjectIntentRegistration>())
                .Where(value => value != null && value.Status == "ACTIVE" &&
                    !existing.Contains(value.Id) &&
                    ProjectIntentOwnedBy(value, request) &&
                    recipe.Projects.Any(project => value.RequestedProjects.Contains(project,
                        StringComparer.Ordinal)))
                .Select(value => value.Id).ToList();
        }
        foreach (string id in release)
            ReleaseProjectIntent(new[] { "release", id }, request, SilentRecipeEmit);
    }

    private static BridgeRequest RecipeLifecycleRequest(BridgeRequest request, List<string> arguments) => new()
    {
        ProtocolVersion = request.ProtocolVersion,
        RequestId = request.RequestId,
        Type = request.Type,
        Command = "restart",
        Arguments = arguments,
        Agent = request.Agent,
        ClientProcessId = request.ClientProcessId,
        Json = request.Json,
        RuntimeSlotId = request.RuntimeSlotId,
        CoordinatorRoot = request.CoordinatorRoot,
        TicketId = request.TicketId,
        GoalId = request.GoalId,
        WakeId = request.WakeId,
        McpRequestId = request.McpRequestId,
        SessionId = request.SessionId
    };

    private static List<string> BuildRecipeResolutionArguments(TestRecipeDefinition recipe)
    {
        List<string> result = new() { "resolve", recipe.Projects.Count == 0 ? "none" : string.Join(",", recipe.Projects) };
        foreach (TestInputAssignment input in recipe.Inputs.OrderBy(value => value.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            result.Add("--input");
            result.Add(input.Name + "=" + input.Value);
        }
        return result;
    }

    private static List<string> BuildRecipeRestartArguments(TestRecipeDefinition recipe)
    {
        List<string> result = new() { "--projects", recipe.Projects.Count == 0 ? "none" : string.Join(",", recipe.Projects) };
        foreach (TestInputAssignment input in recipe.Inputs.OrderBy(value => value.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            result.Add("--input");
            result.Add(input.Name + "=" + input.Value);
        }
        return result;
    }

    private static RecipeInfo RecipeInfoFor(TestRecipeDefinition recipe) => new()
    {
        SchemaVersion = recipe.SchemaVersion,
        Id = recipe.Id,
        Description = recipe.Description,
        Projects = recipe.Projects.ToList(),
        Inputs = recipe.Inputs.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal),
        RequiresReady = recipe.RequiresReady,
        AllowsInGameMutation = recipe.SchemaVersion == DevBridgeSchemaVersions.TestRecipeV2Contract
            ? recipe.AllowsInGameMutation : null,
        TimeoutSeconds = recipe.Budget.TimeoutSeconds,
        Success = new RecipeSuccessInfo
        {
            QuicktestReady = recipe.Success.QuicktestReady,
            CompanionVerified = recipe.Success.CompanionVerified,
            RimBridgeReady = recipe.Success.RimBridgeReady
        },
        Operations = recipe.Operations.Select(value => new RecipeOperationInfo
        {
            Tool = value.ToolName,
            Arguments = value.Arguments,
            Expectation = recipe.SchemaVersion == DevBridgeSchemaVersions.TestRecipeV2Contract
                ? new RecipeOperationExpectationInfo
                {
                    Success = value.Expectation.ExpectedSuccess,
                    Assertions = value.Expectation.Assertions.Select(assertion => new RecipeAssertionInfo
                    {
                        Pointer = assertion.Pointer,
                        Exists = assertion.Exists,
                        ExpectedValue = assertion.ExpectedValue,
                        GreaterThan = assertion.GreaterThan,
                        LessThan = assertion.LessThan
                    }).ToList()
                } : null
        }).ToList(),
        Budget = new RecipeBudgetInfo
        {
            TimeoutSeconds = recipe.Budget.TimeoutSeconds,
            MaxRimWorldLaunches = recipe.Budget.MaxRimWorldLaunches,
            MaxRecipeAttempts = recipe.Budget.MaxRecipeAttempts,
            MaxCoordinatorRefreshes = recipe.Budget.MaxCoordinatorRefreshes,
            StopOnRepeatedFailureFingerprint = recipe.Budget.StopOnRepeatedFailureFingerprint,
            MaxRepeatedFailureCount = recipe.Budget.MaxRepeatedFailureCount
        }
    };

    private sealed class RecipeCallerBudget
    {
        internal int? TimeoutSeconds { get; init; }
        internal int? MaxRimWorldLaunches { get; init; }
        internal int? MaxRecipeAttempts { get; init; }
        internal int? MaxCoordinatorRefreshes { get; init; }
        internal bool? StopOnRepeatedFailureFingerprint { get; init; }
        internal int? MaxRepeatedFailureCount { get; init; }
    }

    private sealed class EffectiveRecipeBudget
    {
        internal int TimeoutSeconds { get; init; }
        internal int MaxRimWorldLaunches { get; init; }
        internal int MaxRecipeAttempts { get; init; }
        internal int MaxCoordinatorRefreshes { get; init; }
        internal bool StopOnRepeatedFailureFingerprint { get; init; }
        internal int MaxRepeatedFailureCount { get; init; }
    }

    private static EffectiveRecipeBudget EffectiveBudget(RecipeBudgetDefinition recipe,
        RecipeCallerBudget caller)
    {
        int coordinatorTimeout = CoordinatorRecipeMaxTimeoutSeconds;
        int maxConfiguredTimeout = coordinatorTimeout;
        int timeout = Math.Min(maxConfiguredTimeout,
            Math.Min(recipe.TimeoutSeconds, caller.TimeoutSeconds ?? recipe.TimeoutSeconds));
        return new EffectiveRecipeBudget
        {
            TimeoutSeconds = Math.Max(1, timeout),
            MaxRimWorldLaunches = Math.Min(CoordinatorRecipeMaxLaunches,
                Math.Min(recipe.MaxRimWorldLaunches, caller.MaxRimWorldLaunches ?? recipe.MaxRimWorldLaunches)),
            MaxRecipeAttempts = Math.Min(CoordinatorRecipeMaxAttempts,
                Math.Min(recipe.MaxRecipeAttempts, caller.MaxRecipeAttempts ?? recipe.MaxRecipeAttempts)),
            MaxCoordinatorRefreshes = Math.Min(CoordinatorRecipeMaxRefreshes,
                Math.Min(recipe.MaxCoordinatorRefreshes, caller.MaxCoordinatorRefreshes ?? recipe.MaxCoordinatorRefreshes)),
            StopOnRepeatedFailureFingerprint = caller.StopOnRepeatedFailureFingerprint ??
                recipe.StopOnRepeatedFailureFingerprint,
            MaxRepeatedFailureCount = Math.Min(8,
                Math.Min(recipe.MaxRepeatedFailureCount, caller.MaxRepeatedFailureCount ??
                    recipe.MaxRepeatedFailureCount))
        };
    }

    private static bool TryParseRecipeRun(IReadOnlyList<string> arguments, out string id,
        out RecipeCallerBudget budget, out string errorCode, out string error)
    {
        id = arguments.Count > 2 ? arguments[2]?.Trim() : null;
        budget = new RecipeCallerBudget();
        errorCode = null;
        error = null;
        if (string.IsNullOrWhiteSpace(id))
            return RecipeParseFailure("TEST_RECIPE_USAGE", "recipe run requires one recipe id.",
                out errorCode, out error);
        int? timeout = null, launches = null, attempts = null, refreshes = null, repeated = null;
        bool? stop = null;
        HashSet<string> seenOptions = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 3; index < arguments.Count; index++)
        {
            string option = arguments[index]?.Trim() ?? string.Empty;
            if (string.Equals(option, "--json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (option is "--timeout-seconds" or "--budget-seconds" or "--max-rimworld-launches" or
                "--max-recipe-attempts" or "--max-coordinator-refreshes" or "--max-repeated-failure-count")
            {
                string optionKey = option == "--budget-seconds" ? "--timeout-seconds" : option;
                if (!seenOptions.Add(optionKey))
                    return RecipeParseFailure("TEST_RECIPE_BUDGET_INVALID", "a caller budget option may be declared only once.",
                        out errorCode, out error);
                if (++index >= arguments.Count || !int.TryParse(arguments[index], NumberStyles.None,
                        CultureInfo.InvariantCulture, out int parsed))
                    return RecipeParseFailure("TEST_RECIPE_BUDGET_INVALID", "budget options require invariant integers.",
                        out errorCode, out error);
                switch (option)
                {
                    case "--timeout-seconds":
                    case "--budget-seconds": timeout = parsed; break;
                    case "--max-rimworld-launches": launches = parsed; break;
                    case "--max-recipe-attempts": attempts = parsed; break;
                    case "--max-coordinator-refreshes": refreshes = parsed; break;
                    case "--max-repeated-failure-count": repeated = parsed; break;
                }
                continue;
            }
            if (string.Equals(option, "--stop-on-repeated-failure-fingerprint", StringComparison.OrdinalIgnoreCase))
            {
                if (!seenOptions.Add("--repeated-failure-policy"))
                    return RecipeParseFailure("TEST_RECIPE_BUDGET_INVALID", "a repeated-failure policy may be declared only once.",
                        out errorCode, out error);
                stop = true;
                continue;
            }
            if (string.Equals(option, "--continue-on-repeated-failure-fingerprint", StringComparison.OrdinalIgnoreCase))
            {
                if (!seenOptions.Add("--repeated-failure-policy"))
                    return RecipeParseFailure("TEST_RECIPE_BUDGET_INVALID", "a repeated-failure policy may be declared only once.",
                        out errorCode, out error);
                stop = false;
                continue;
            }
            return RecipeParseFailure("TEST_RECIPE_USAGE", "unknown recipe run option.",
                out errorCode, out error);
        }
        if (!BoundedCaller(timeout, 1, 900) || !BoundedCaller(launches, 0, 8) ||
            !BoundedCaller(attempts, 0, 8) || !BoundedCaller(refreshes, 0, 32) ||
            !BoundedCaller(repeated, 0, 8))
            return RecipeParseFailure("TEST_RECIPE_BUDGET_INVALID", "a caller budget is outside its bound.",
                out errorCode, out error);
        budget = new RecipeCallerBudget
        {
            TimeoutSeconds = timeout,
            MaxRimWorldLaunches = launches,
            MaxRecipeAttempts = attempts,
            MaxCoordinatorRefreshes = refreshes,
            StopOnRepeatedFailureFingerprint = stop,
            MaxRepeatedFailureCount = repeated
        };
        return true;
    }

    private static bool BoundedCaller(int? value, int min, int max) =>
        !value.HasValue || value.Value >= min && value.Value <= max;

    private static bool RecipeParseFailure(string code, string message, out string errorCode, out string error)
    {
        errorCode = code;
        error = message;
        return false;
    }

    internal RecipeResponse CreateRecipeJsonResponse(BridgeRequest request, int exitCode)
    {
        if (request.RecipeResponse != null)
        {
            request.RecipeResponse.ExitCode = request.RecipeResponse.ExitCode == 0 && exitCode != 0
                ? exitCode : request.RecipeResponse.ExitCode;
            return request.RecipeResponse;
        }
        return new RecipeListResponse
        {
            ExitCode = exitCode,
            ErrorCode = "TEST_RECIPE_RESPONSE_MISSING",
            Error = "The recipe command did not produce its dedicated response."
        };
    }
}
