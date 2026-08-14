# DevBridge2

Use `DevBridge.cmd` from the installed mod root.

## Rules

- Do not launch RimWorld yourself.
- Do not kill or restart RimWorld yourself.
- Use `DevBridge.cmd` for all game coordination.
- Run `DevBridge.cmd test session` before interacting with the current game when the test may run
  longer than a short command. Keep that connected command attached to the test owner so DevBridge
  can renew the lease automatically.
- Test leases are shared: multiple agents may hold active leases and test the same ready generation concurrently.
- `test begin` remains available for short-lived work. Its lease expires about two minutes after the
  last heartbeat; use `DevBridge.cmd test renew <lease-id>` before `expiresUtc` if a connected session
  is not practical. Release a completed test with the exact `DevBridge.cmd test end <lease-id>` command.
- Maintenance ownership is exclusive: acquire a lease first, then use `stop <lease-id>` and keep that lease until `ensure-ready <lease-id>` completes.
- Register managed project intent before testing: `DevBridge.cmd project register <alias[,alias...]>`.
  Registrations are durable, owner/session-bound, renewed by heartbeat, and expire safely when the
  owner stops renewing. Use `project status` to inspect active, frozen, and queued registrations.
- Plain `DevBridge.cmd restart` is an aggregate launch. It uses the minimal control profile when there
  are no active project registrations, or the deterministic union and full dependency closure of all
  active registrations. It never preserves or implicitly launches a production ModsConfig.
- Explicit `restart --projects ...` remains compatibility syntax and adds the caller's request to the
  aggregate. Verify `status --json` before testing: it must show the frozen registration IDs/owners,
  project union, ordered package closure, and baseline/profile fingerprints that will run.
- `restart --legacy-production` is the unmistakable, human-only production compatibility path. It is
  never an automatic fallback, never used for project attribution, and cannot be combined with active
  project intent. Resolution, ownership, lease, maintenance, process-identity, or safety failures
  fail closed with no ModsConfig write and no launch.
- Release completed intent with `DevBridge.cmd project release <registration-id>`; release and expiry
  affect future generations only. A generation already frozen remains immutable.
- Before the first aggregate profile launch, DevBridge adopts the exact current ModsConfig bytes as a
  durable baseline only after successful metadata resolution. Explicit `mods capture-baseline` remains
  available for intentional baseline changes while RimWorld is stopped and no leases or restart are active.
- Waiting is normal. Do not abort a command just because another agent is testing.
- `restart` waits for active tests, restarts once, and continues automatically.
- Once `restart` is accepted, DevBridge owns it even if the requesting shell times out or disconnects; do not request it again. Use `DevBridge.cmd wait-ready` or `DevBridge.cmd status` to reconnect.
- Once `restart` is requested, existing tests may finish, but no new test lease is granted on the old generation.
- While the coordinator-owned game is running, restart remains durably queued until every active lease
  is released (or its approximately two-minute lease lifetime expires); normal contention has no short
  failure deadline.
- If the owned game is already absent, Dev Bridge launches the replacement without waiting on old-generation
  leases and preserves those leases for the ready replacement generation.
- `test begin` waits through a pending restart, then acquires a lease on the new generation.
- For assembly replacement, use `DevBridge.cmd stop <lease-id>`. Wait for `maintenanceReady=true` and
  `gameState=STOPPED`, replace and hash-check the assembly while RimWorld is stopped, then run
  `DevBridge.cmd ensure-ready <lease-id>`. The stop operation retains that lease; only its holder may
  ensure-ready or use `restart <lease-id>` from this maintenance state.
- A readiness timeout reports `READINESS_TIMEOUT` for that wait only. It retains the verified process
  identity and makes no replacement launch; a later holder-authorized `ensure-ready` may reuse the same
  process if it subsequently reports matching readiness.
- Use `DevBridge.cmd status` to understand what is happening.
- Use `DevBridge.cmd wait-ready` after an external command timeout or interruption.
- If `status` reports `PROCESS_INSPECTION_AMBIGUOUS`, close RimWorld through Steam and run
  `DevBridge.cmd doctor`. A complete census proving zero matching processes clears the stale quarantine
  to `STOPPED` without launching anything; then run the separately printed `DevBridge.cmd restart`.
- While crash isolation is active, poll `DevBridge.cmd status` (or `status --json`) only. Do not retry
  restart, edit ModsConfig.xml, or register, renew, or release project intent until isolation is terminal.
- Diagnostics show the agent/session identity beside leases. Set `DEVBRIDGE_AGENT` to choose an explicit identity; otherwise each CLI session gets a short automatic ID.
- Use the same stable `DEVBRIDGE_AGENT` value for `test session`, `test renew`, `test end`, `stop`, and
  `ensure-ready` commands that manage a lease acquired by an earlier CLI invocation.
- Append `--json` to `status`, `test begin`, `test renew <lease-id>`, `test end <lease-id>`, `restart`,
  `wait-ready`, or `doctor` for one machine-readable JSON result. `test session` is intentionally a
  connected text stream; do not use `--json` with it.

## Normal workflow

```text
set DEVBRIDGE_AGENT=agent-a
set DEVBRIDGE_SESSION=agent-a-session
DevBridge.cmd project register horticulture,aquaculture
DevBridge.cmd project status
DevBridge.cmd test session # keep this connected in a second terminal/background task
# interact with RimWorld and test the mod
DevBridge.cmd test end <lease-id> # from another command with the same DEVBRIDGE_AGENT; this ends the session

# after rebuilding a mod:
DevBridge.cmd restart
DevBridge.cmd wait-ready
DevBridge.cmd status --json # verify frozen registrations, closure, order, and fingerprints
DevBridge.cmd test begin
# test the rebuilt mod
DevBridge.cmd test end <printed-id>
DevBridge.cmd project release <registration-id>
```

`test session`, `test begin`, and `restart` may stay running while they wait. A caller timeout does not
cancel a queued restart, and lease contention does not turn it into a terminal error. The coordinator
owns the RimWorld process. If a waiting command disconnects, the accepted restart is still owned by
DevBridge: reconnect with `DevBridge.cmd wait-ready` or `DevBridge.cmd status`; do not end the task just
because DevBridge is waiting.
It launches normally, then the DevBridge2 mod activates RimWorld's built-in Dev Quicktest from the main
menu; no command-line quicktest mode or save is used. The readiness mod reports only after a playable map
is loaded.

The readiness wait defaults to six minutes, based on observed full-modlist startup with a modest margin.
Set `DEVBRIDGE_READINESS_TIMEOUT_SECONDS` for a deterministic local override; this is a fixed deadline,
not an adaptive retry policy.

Useful diagnostics:

```text
DevBridge.cmd status
DevBridge.cmd wait-ready
DevBridge.cmd doctor
DevBridge.cmd mods status --json
```

Runtime state and logs are kept in `Runtime`. A lease expires approximately two minutes after its last
heartbeat. `test session` is the recommended long-running path: its named-pipe connection is the lease
owner, and the coordinator heartbeats only while that connection is alive. Cancelling, disconnecting, or
crashing that owner stops heartbeats; no detached heartbeat process remains. For short-lived clients,
`test renew <lease-id>` resets the timer without changing the lease generation.

While crash isolation is active, poll `DevBridge.cmd status` or `status --json` only. Do not retry,
edit ModsConfig.xml, or mutate project registrations until the incident is terminal. A failed
resolution, metadata graph, ownership check, lease/maintenance check, process identity check, or
launch safety check is fail-closed: DevBridge makes no ModsConfig write and no launch.

### Aggregate project launches

Register project intent, restart or await the accepted generation, verify inclusion, then begin a test:

```text
set DEVBRIDGE_AGENT=agent-a
set DEVBRIDGE_SESSION=agent-a-session
DevBridge.cmd project register horticulture,aquaculture
DevBridge.cmd project status
DevBridge.cmd restart
DevBridge.cmd wait-ready
DevBridge.cmd status --json
DevBridge.cmd test begin
DevBridge.cmd test end <lease-id>
DevBridge.cmd project release <registration-id>
```

Supported aliases are `deferred-reality`, `insight-canvas`, `knowledge-framework`, `frontier`,
`aquaculture`, `horticulture`, and `wildlife`. A profile contains the always-on baseline, requested
project roots, and the complete recursively resolved dependency closure. Dependencies precede their
dependents; `loadBefore`/`loadAfter` constraints are honored, shared dependencies are written once,
and cycles or missing, ambiguous, and malformed metadata fail before `ModsConfig.xml` changes or
RimWorld launches. `ferny.loadthemlast` is never injected.

Baseline capture and restore are allowed only with no active lease, pending restart, or RimWorld
process. Restore reproduces the captured bytes atomically. If `ModsConfig.xml` has an unexpected user
edit, DevBridge refuses to overwrite it; intentionally changed lists must be captured explicitly after
the edit. A request arriving after the freeze is reported as queued for the next aggregate
generation and cannot replace the accepted profile for that generation. A conflicting owner or
unsafe pending request is rejected with `PROFILE_CONFLICT`.

### Machine-readable lease contract

Lease objects in `--json` responses expose exact coordinator-clock values and numeric retry timing:

```json
{
  "id": "T001",
  "agent": "agent-a",
  "lastHeartbeatUtc": "2026-08-11T16:00:00Z",
  "expiresUtc": "2026-08-11T16:02:00Z",
  "retryAfterSeconds": 120
}
```

Waiting responses also expose `restartQueued`, `nextLeaseExpirationUtc`, and top-level numeric
`retryAfterSeconds`. Agents should use those fields rather than parsing display text. `staleIn` is not
part of the machine-readable lease contract.

For example, a status response while an accepted restart is waiting can contain:

```json
{
  "restartQueued": true,
  "nextLeaseExpirationUtc": "2026-08-11T16:02:00Z",
  "retryAfterSeconds": 60
}
```

Profile status also exposes the exact accepted profile:

```json
{
  "profileMode": "projects",
  "requestedProjects": ["horticulture", "aquaculture"],
  "resolvedProjectPackageIds": ["lan.aquaculture.fishing", "lan.horticulture.novelseeds"],
  "resolvedMods": ["zetrith.prepatcher", "brrainz.harmony", "lan.devbridge2"],
  "profileFingerprint": "<sha256>",
  "baselineFingerprint": "<sha256>",
  "modsConfigOwnership": "DEVBRIDGE_GENERATED",
  "profileConflict": null
}
```

`launchProfileMode` is `aggregate-minimal-control` for a no-project aggregate, `aggregate-projects`
for an aggregate with project intent, and `explicit-human-legacy` only for `--legacy-production`.
`profileMode` remains the resolver mode (`baseline`, `projects`, or explicit `legacy`). The
`resolvedMods` example is abbreviated; status returns the complete activeMods order. The response also
exposes `frozenRegistrations` and `queuedProjectIntents` so agents can verify inclusion before testing.

## Maintenance workflow

Maintenance is an explicit owner-held session for a build, edit, or assembly replacement:

1. An agent acquires a lease with `DevBridge.cmd test session` (or `test begin` plus manual renewals); `stop` then transitions that lease into exclusive maintenance ownership.
2. The owner runs `DevBridge.cmd stop <lease-id>` and waits for `gameState=STOPPED`, `maintenanceReady=true`, and `leaseState=HELD`.
3. RimWorld remains stopped while the owner performs the external build, edit, replacement, and hash verification.
4. Other agents cannot take the maintenance lease or relaunch the game during that window.
5. The owner explicitly signals completion with `DevBridge.cmd ensure-ready <lease-id>`.
6. Dev Bridge performs one controlled relaunch, requests built-in Dev Quicktest after the normal main-menu lifecycle, and waits for playable-map readiness.
7. The owner verifies the result, then releases ownership with `DevBridge.cmd test end <lease-id>`.

Dev Bridge does not infer that external work has finished. The lease holder must call
`ensure-ready`; do not wait for an automatic relaunch.

The same owner/session is idempotent for an already accepted stop, ensure-ready, or launch request;
it does not create a second launch. Conflicting owners are rejected or serialized against the
authoritative coordinator for the selected coordinator root and runtime slot. A request routed to a
different root or slot is denied. Lease contention waits durably; readiness waits, launch attempts,
and recovery actions remain bounded. Abandoned leases are reclaimed after their bounded lifetime,
and exhausted readiness or launch budgets become terminal failures rather than retry loops.

Process control is identity-safe: a PID is accepted only with its recorded process start identity, so
a stale or reused process cannot be stopped or treated as the owned RimWorld process. Assembly
replacement is protected by the maintenance window; replace and hash-check the assembly only after
`stop` confirms `STOPPED`, and let the lease holder call `ensure-ready` afterward.

Every normal launch omits command-line Quicktest and save arguments. After relaunch, the mod activates
the built-in Dev Quicktest from the genuine main menu and reports readiness only after a playable map
exists. A terminal failure such as `READINESS_TIMEOUT` is a bounded result: inspect `status` or
`doctor` and follow the printed next action instead of building a retry loop.

### Operator example

```text
set DEVBRIDGE_AGENT=agent-a
DevBridge.cmd test begin --json
# Save the returned leaseId as <lease-id>.
DevBridge.cmd test renew <lease-id> --json # only for a short-lived client without test session
DevBridge.cmd stop <lease-id> --json
# Confirm gameState=STOPPED, maintenanceReady=true, and leaseState=HELD.
# Perform the build/edit/assembly replacement and verify its hash while RimWorld is stopped.
DevBridge.cmd ensure-ready <lease-id> --json
DevBridge.cmd wait-ready --json
# Verify status/doctor and the result of the work.
DevBridge.cmd test end <lease-id> --json
```

If a command reaches a terminal failure, stop the workflow, inspect `DevBridge.cmd status --json`
and `DevBridge.cmd doctor --json`, and follow the reported next action. Do not retry the failed
command in a loop. The native process exit code is separate from structured JSON: `0` means the
command completed successfully, `2` indicates usage/request failure, `4` indicates an operational
refusal or bounded terminal failure, and `doctor` uses `1` when its checks fail. With `--json`, parse
the JSON fields such as `success`, `state`, `errorCode`, and `exitCode`, while still honoring the
native CLI exit code; a successful status query can report that the runtime itself is in `ERROR`.
