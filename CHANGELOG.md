# Dev Bridge release notes

## Unreleased
- Bounds doctor and status projections at the v2 IPC payload limit. The root cause was the
  unbounded `generationHistory.records` collection: the local history artifact had 288 records
  and was 151,419 bytes before the rest of the doctor response was added; the published doctor
  response is now 61,943 bytes with the same state. Responses now retain deterministic recent
  samples, report total/truncated collection metadata, cap findings and diagnostic text, and
  return a truthful small `OUTPUT_TOO_LARGE` envelope if an unexpected oversized result still
  reaches IPC; same-version failures no longer receive version-mismatch remediation.

- Hardens the canonical identity contract with a durable installation/owner ID,
  explicit coordinator, lifecycle-generation, and RimWorld PID/start identities,
  transitional restart states, stale-state diagnostics, and duplicate-root findings
  in status/readiness JSON and `doctor --json`.

- Adds a bounded declared test-input surface for the existing built-in Quicktest path. `project
  resolve` accepts normalized boolean, integer, and string-enum inputs for Quicktest behavior;
  values participate in profile/generation fingerprints, frozen state, immutable manifests,
  history, and status. Invalid or unknown values fail before mutation, and incompatible pending
  requests return `TEST_INPUT_CONFLICT`. Arbitrary argv, shell fragments, environment names, and
  environment entries remain intentionally unsupported.

- Adds the pure `project resolve <alias[,alias...]> [--json] [--explain]` planning API with
  canonical aliases, exact dependency/load-order closure, deterministic fingerprints, per-mod
  provenance, pinned-generation comparison, and machine-readable failures. It does not mutate
  leases, registrations, baseline/ModsConfig, generation, or RimWorld; status and Doctor distinguish
  a valid current generation from invalid future configuration.

- Adds immutable accepted-generation manifests and atomic semantic history with current, previous,
  and last-known-good queries through `history`, `history show`, and `history last-good`; history is
  restart-safe, duplicate-free, corruption-visible, and excludes credentials and raw exceptions.

- Adds strictly opt-in mod profiles with `restart --projects none|alias[,alias...]` and the
  `mods status`, `mods capture-baseline`, and `mods restore-baseline` commands.
- Captures a durable byte-for-byte user ModsConfig baseline, tracks generated ownership and hashes,
  writes profiles atomically only after lease/process draining, and refuses unexpected external edits.
- Resolves installed mod metadata case-insensitively through arbitrary dependency depth, honors
  dependency and load-order constraints, deduplicates shared dependencies, detects cycles, and never
  injects `ferny.loadthemlast`.
- Persists immutable accepted profile roots, ordered package IDs, and deterministic fingerprints, and
  exposes the profile contract through status JSON. Existing unprofiled launches retain their behavior.
- Adds opt-in RimBridgeServer coexistence with required/optional/off profile participation, launch-bound
  standalone Player.log discovery, process/generation identity invalidation, token-safe status, and the
  explicit `bridge endpoint` command. DevBridge remains the lifecycle authority and does not manage GABS.
- Adds the optional RimBridgeServer companion tool `devbridge/get_generation_context`, which exposes the
  inherited DevBridge generation identity read-only and lets the coordinator reject companion launch,
  generation, or PID mismatches without adding an SDK dependency to the core mod.
- Adds a durable DevBridge/RimBridgeServer mutation-ownership policy. DevBridge detects unexpected
  `ModsConfig.xml` changes as `PROFILE_EXTERNAL_MUTATION`, preserves generation/profile fingerprints as
  evidence, blocks replacement launches until explicit maintenance reconciliation, and exposes the
  authority/evidence/policy through status, `bridge policy`, and the read-only companion tool
  `devbridge/get_control_policy`. RimBridge live-game operations remain supported; persistent mod enable
  and reorder changes are routed through DevBridge's profile workflow.
- Adds optional DevBridge-controlled RimBridge routing through isolated GABP client calls: lease-bound
  `bridge tools`/`bridge call` commands validate launch, generation, process, endpoint, policy, and
  companion identity, attach token-free provenance, preserve opaque tool evidence, and never auto-restart
  after a failed call. Persistent mod/order and lifecycle operations are centrally blocked with a
  deterministic policy error; off/unavailable routing remains fail-closed and direct RimBridge use remains
  supported.

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
