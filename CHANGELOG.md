# Dev Bridge release notes

## Unreleased

- Adds strictly opt-in mod profiles with `restart --projects none|alias[,alias...]` and the
  `mods status`, `mods capture-baseline`, and `mods restore-baseline` commands.
- Captures a durable byte-for-byte user ModsConfig baseline, tracks generated ownership and hashes,
  writes profiles atomically only after lease/process draining, and refuses unexpected external edits.
- Resolves installed mod metadata case-insensitively through arbitrary dependency depth, honors
  dependency and load-order constraints, deduplicates shared dependencies, detects cycles, and never
  injects `ferny.loadthemlast`.
- Persists immutable accepted profile roots, ordered package IDs, and deterministic fingerprints, and
  exposes the profile contract through status JSON. Existing unprofiled launches retain their behavior.

## 1.2.4

- Replaces the long stale-lease window with an approximately two-minute renewable lease.
- Adds connected `DevBridge.cmd test session` heartbeats over the existing named pipe; the coordinator
  stops renewing when that owner disconnects, is cancelled, or crashes, with no detached heartbeat process.
- Keeps accepted restarts durably queued and reports `lastHeartbeatUtc`, `expiresUtc`, and numeric
  `retryAfterSeconds` in machine-readable diagnostics, including the next blocking lease expiration.
- Requires both lease ID and stable agent identity for `test renew` and `test end`.
- Documents that waiting is normal and agents should reconnect with `wait-ready` rather than end their task.

## 1.2.3

- Reclaims test leases after a bounded period without a heartbeat, preventing a timed-out runtime-test wrapper
  from blocking every later restart.
- Adds `DevBridge.cmd test renew <lease-id>` for long-running test and maintenance workflows.
- Keeps status, doctor, wait-ready, and lease cleanup responsive while a restart waits on active tests.
- Authorizes later lease-management CLI calls by lease ID and stable agent identity instead of the
  short-lived client process ID.

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
