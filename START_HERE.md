# DevBridge2

Use `DevBridge.cmd` from the installed mod root.

## Rules

- Do not launch RimWorld yourself.
- Do not kill or restart RimWorld yourself.
- Use `DevBridge.cmd` for all game coordination.
- Run `DevBridge.cmd test begin` before interacting with the current game.
- Test leases are shared: multiple agents may hold active leases and test the same ready generation concurrently.
- Run the exact `DevBridge.cmd test end <lease-id>` command printed by `test begin` afterward.
- Maintenance ownership is exclusive: acquire a lease first, then use `stop <lease-id>` and keep that lease until `ensure-ready <lease-id>` completes.
- Use `DevBridge.cmd restart` after a build requires a fresh RimWorld process.
- Waiting is normal. Do not abort a command just because another agent is testing.
- `restart` waits for active tests, restarts once, and continues automatically.
- Once `restart` is accepted, DevBridge owns it even if the requesting shell times out or disconnects; do not request it again. Use `DevBridge.cmd wait-ready` or `DevBridge.cmd status` to reconnect.
- Once `restart` is requested, existing tests may finish, but no new test lease is granted on the old generation.
- The restart completes only after every active lease has been released (or the conservative stale-lease timeout expires).
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
- Diagnostics show the agent/session identity beside leases. Set `DEVBRIDGE_AGENT` to choose an explicit identity; otherwise each CLI session gets a short automatic ID.
- Append `--json` to `status`, `test begin`, `test end <lease-id>`, `restart`, `wait-ready`, or `doctor` for one machine-readable JSON result.

## Normal workflow

```text
DevBridge.cmd test begin
# interact with RimWorld and test the mod
DevBridge.cmd test end <lease-id> # use the exact ID printed above

# after rebuilding a mod:
DevBridge.cmd restart
DevBridge.cmd test begin
# test the rebuilt mod
DevBridge.cmd test end <printed-id>
```

`test begin` and `restart` may stay running while they wait. The coordinator owns the RimWorld process.
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
```

Runtime state and logs are kept in `Runtime`. A lease abandoned for about one hour is reclaimed automatically.

## Maintenance workflow

Maintenance is an explicit owner-held session for a build, edit, or assembly replacement:

1. An agent acquires a lease with `DevBridge.cmd test begin`; `stop` then transitions that lease into exclusive maintenance ownership.
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
different root or slot is denied. Leases, readiness waits, launch attempts, and recovery actions are
bounded. Abandoned leases are reclaimed after their bounded lifetime, and exhausted readiness or
launch budgets become terminal failures rather than retry loops.

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
