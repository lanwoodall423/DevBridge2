using RimBridgeServer.Sdk;

namespace DevBridge2.BridgeTools;

public sealed class DevBridgeGenerationTools
{
    [Tool("devbridge/get_generation_context",
        Title = "Get DevBridge generation context",
        Description = "Read the current DevBridge launch and generation identity without changing game or coordinator state.",
        ResultDescription = "A token-free DevBridge generation identity snapshot.",
        RequiresAuth = true)]
    [ToolResponse("success", "boolean", "Whether a complete DevBridge context was available")]
    [ToolResponse("available", "boolean", "Whether the identity fields can be used")]
    [ToolResponse("schemaVersion", "string", "Generation-context response schema", Always = true)]
    [ToolResponse("launchId", "string", "Coordinator launch identifier", Nullable = true)]
    [ToolResponse("generation", "integer", "DevBridge generation", Nullable = true)]
    [ToolResponse("profileFingerprint", "string", "Accepted profile fingerprint", Nullable = true)]
    [ToolResponse("baselineFingerprint", "string", "Baseline fingerprint", Nullable = true)]
    [ToolResponse("profileMode", "string", "DevBridge profile mode", Nullable = true)]
    [ToolResponse("processId", "integer", "Current RimWorld process ID", Nullable = true)]
    [ToolResponse("processStartUtcTicks", "integer", "Current process start identity when available", Nullable = true)]
    [ToolResponse("devBridge2ModVersion", "string", "DevBridge2 mod version", Nullable = true)]
    [ToolResponse("rimBridgeIntegrationSchemaVersion", "string", "DevBridge/RimBridge integration schema", Always = true)]
    [ToolResponse("errorCode", "string", "Bounded context error code", Nullable = true)]
    [ToolResponse("error", "string", "Bounded context diagnostic", Nullable = true)]
    public DevBridgeGenerationContextPayload GetGenerationContext()
    {
        return DevBridgeGenerationContext.Read();
    }

    [Tool("devbridge/get_control_policy",
        Title = "Get DevBridge control policy",
        Description = "Read the DevBridge/RimBridge ownership boundary and conflicting operations without changing game or coordinator state.",
        ResultDescription = "A token-free, read-only DevBridge control policy snapshot.",
        RequiresAuth = true)]
    [ToolResponse("success", "boolean", "Whether the policy snapshot was available")]
    [ToolResponse("available", "boolean", "Whether the policy can be used")]
    [ToolResponse("schemaVersion", "string", "Control-policy response schema", Always = true)]
    [ToolResponse("readOnly", "boolean", "Always true; this tool exposes no mutation method", Always = true)]
    [ToolResponse("lifecycleOwner", "string", "Owner of RimWorld lifecycle operations")]
    [ToolResponse("modsConfigOwner", "string", "Owner of ModsConfig.xml and persisted enabled/order state")]
    [ToolResponse("generationOwner", "string", "Owner of generation identity")]
    [ToolResponse("currentGeneration", "integer", "Current DevBridge generation")]
    [ToolResponse("generationOwned", "boolean", "Whether an accepted generation is currently trusted")]
    [ToolResponse("profileFrozen", "boolean", "Whether the accepted profile is frozen and trusted")]
    [ToolResponse("modsConfigMutationAuthority", "string", "Durable ModsConfig mutation authority")]
    [ToolResponse("blockedOperations", "array", "RimBridge operations agents must not invoke directly while DevBridge owns the generation")]
    [ToolResponse("operationCategories", "object", "Operation classification")]
    [ToolResponse("externalMutation", "object", "Non-secret external-mutation evidence", Nullable = true)]
    [ToolResponse("errorCode", "string", "Bounded policy error code", Nullable = true)]
    [ToolResponse("error", "string", "Bounded policy diagnostic", Nullable = true)]
    public DevBridgeControlPolicyPayload GetControlPolicy()
    {
        return DevBridgeControlPolicy.Read();
    }
}
