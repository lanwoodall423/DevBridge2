# Dev Bridge release notes

## 1.2.2

- Keeps lease-blocked restarts durably queued instead of converting normal contention into a
  terminal 30-second `WAITING_FOR_BRIDGE_EXPIRED` failure.
- If the coordinator-owned RimWorld process is already absent, an active lease no longer blocks the
  replacement launch; the lease survives and advances to the ready replacement generation.
- Automatically resumes legacy `WAITING_FOR_BRIDGE_EXPIRED` state when no launch was attempted,
  preserving the finite launch budget and fail-closed process-identity checks.

## 1.2.1

- Fixes a recovery deadlock where a persisted `PROCESS_INSPECTION_AMBIGUOUS` quarantine survived after
  RimWorld had closed and caused every later restart to be refused.
- `doctor` now clears that quarantine only after one complete authoritative census proves that zero
  matching RimWorld processes exist, no lease is held, and no restart is active.
- Recovery persists `STOPPED`, clears the stale PID/start identity, and performs no termination or
  launch. A separate explicit `restart` remains required.
- Incomplete inspection or any matching process continues to fail closed.

## 1.2.0

The headline feature is an explicit maintenance-session workflow for safe, coordinated mod work.

### Maintenance workflow

- An agent acquires a lease, stops RimWorld through `DevBridge.cmd stop <lease-id>`, and retains ownership while performing external build, edit, or assembly-replacement work.
- Other agents cannot take over the maintenance session or relaunch the game while it is held.
- The owner calls `DevBridge.cmd ensure-ready <lease-id>` when the external work is complete; Dev Bridge performs one controlled relaunch, waits for readiness, and supports the post-relaunch built-in Dev Quicktest path.
- The owner verifies the result and releases the lease with `DevBridge.cmd test end <lease-id>`.
- Completion is explicit: Dev Bridge does not infer that external work has finished.

### Supporting improvements

- Exclusive maintenance leases and one-owner launch coordination.
- Deduplication and serialization of concurrent requests.
- Bounded lease, launch, readiness, and recovery budgets with durable timeout recovery.
- Correct authoritative runtime-slot and coordinator-root routing.
- Incompatible-process draining and identity-checked replacement.
- Guarded post-relaunch Quicktest activation after the genuine main-menu lifecycle.
- Correct propagation of `DevBridge.cmd` native exit codes alongside structured JSON results.

### Upgrade note

Existing callers do not need to change command names. Existing status, test, restart, readiness, and
doctor commands remain available. Callers that perform external build or assembly-replacement work
must use the explicit lease-held `stop <lease-id>` → work → `ensure-ready <lease-id>` workflow and
must release the lease with `test end`; completion is not inferred automatically.
