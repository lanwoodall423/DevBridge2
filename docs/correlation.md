# Workflow correlation

DevBridge2 accepts an optional --workflow-id <id> on test recipe run. RimTest owns generation
of this bounded caller context; DevBridge does not reinterpret it as a lease, runId, generation,
launch, or authorization identity.

The value is copied to the additive workflowId field on
devbridge-test-recipe-run/v1. Routed recipe operations also expose optional operationId,
workflowId, generation, and launchId. A routed JSON result includes workflowId in its
route/provenance when the caller supplied one. DevBridge copies an operationId only when
RimBridge explicitly supplies it in result metadata; the GABP envelope request ID is not promoted
to an operation ID.

The fields are omitted when unavailable. Existing clients and old responses remain valid. The
coordinator bounds and validates supplied workflow values, and downstream consumers reject
explicit workflow or generation mismatches rather than guessing.
RimTest may retry once without the optional request argument when an older parser explicitly
returns TEST_RECIPE_USAGE; that refusal occurs before coordinator mutation.

The value is request-scoped and is not added to durable lifecycle state or a duplicate run store.
For owner lookup, use the returned recipe runId, generation, leaseId, evidenceId, and
DevBridge.cmd status --json / DevBridge.cmd evidence show <id> --json; use
DevBridge.cmd history diagnose <generation> for generation comparisons.
