# DevBridge2

Use the bridge at `C:\Games\Steam\steamapps\common\RimWorld\Mods\DevBridge2`.

## Rules

- Do not launch RimWorld yourself.
- Do not kill or restart RimWorld yourself.
- Use `DevBridge.cmd` for all game coordination.
- Run `DevBridge.cmd test begin` before interacting with the current game.
- Test leases are shared: multiple agents may hold active leases and test the same ready generation concurrently.
- Run the exact `DevBridge.cmd test end <lease-id>` command printed by `test begin` afterward.
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
DevBridge.cmd test end A7F3       # use the exact ID printed above

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
