namespace DevBridge.Coordinator;

internal static class RimBridgeOperationCategories
{
    internal const string ReadOnly = "read-only";
    internal const string InGameMutation = "in-game-mutation";
    internal const string ProfileMutation = "profile-mutation";
    internal const string LifecycleMutation = "lifecycle-mutation";
}
internal sealed class RimBridgePolicyDecision
{
    internal bool Allowed { get; init; }
    internal string Category { get; init; }
    internal string ErrorCode { get; init; }
    internal string Reason { get; init; }
}

internal static class RimBridgeOperationPolicy
{
    internal static string CategoryFor(string toolName)
    {
        string normalized = (toolName ?? string.Empty).Trim();
        if (string.Equals(normalized, "rimworld/set_mod_enabled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "rimworld/reorder_mod", StringComparison.OrdinalIgnoreCase))
            return RimBridgeOperationCategories.ProfileMutation;

        if (string.Equals(normalized, "rimworld/start", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "rimworld/restart", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "rimworld/stop", StringComparison.OrdinalIgnoreCase))
            return RimBridgeOperationCategories.LifecycleMutation;

        if (!normalized.StartsWith("rimworld/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("get", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("list", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("read", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("inspect", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("status", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("screenshot", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("devbridge/", StringComparison.OrdinalIgnoreCase))
            return RimBridgeOperationCategories.ReadOnly;

        return RimBridgeOperationCategories.InGameMutation;
    }

    internal static RimBridgePolicyDecision Evaluate(string toolName,
        RimBridgePolicyState policy, bool hasValidLease)
    {
        string normalized = (toolName ?? string.Empty).Trim();
        string category = CategoryFor(normalized);
        bool explicitlyBlocked = (policy?.BlockedOperations ?? new List<string>())
            .Any(value => string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase));

        if (explicitlyBlocked || category == RimBridgeOperationCategories.ProfileMutation ||
            category == RimBridgeOperationCategories.LifecycleMutation)
        {
            return new RimBridgePolicyDecision
            {
                Allowed = false,
                Category = category,
                ErrorCode = "RIMBRIDGE_OPERATION_BLOCKED_BY_DEVBRIDGE_POLICY",
                Reason = category == RimBridgeOperationCategories.ProfileMutation
                    ? "persistent ModsConfig/profile changes belong to DevBridge profile maintenance"
                    : category == RimBridgeOperationCategories.LifecycleMutation
                        ? "RimWorld lifecycle changes belong to DevBridge lifecycle control"
                        : "the operation is listed in the durable DevBridge blocked-operation policy"
            };
        }

        if (!hasValidLease)
        {
            return new RimBridgePolicyDecision
            {
                Allowed = false,
                Category = category,
                ErrorCode = "RIMBRIDGE_LEASE_REQUIRED",
                Reason = "a current DevBridge test session/lease is required for routed RimBridge operations"
            };
        }

        return new RimBridgePolicyDecision
        {
            Allowed = true,
            Category = category
        };
    }
}
