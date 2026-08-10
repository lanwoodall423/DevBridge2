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
- Once `restart` is requested, existing tests may finish, but no new test lease is granted on the old generation.
- The restart completes only after every active lease has been released (or the conservative stale-lease timeout expires).
- `test begin` waits through a pending restart, then acquires a lease on the new generation.
- Use `DevBridge.cmd status` to understand what is happening.
- Use `DevBridge.cmd wait-ready` after an external command timeout or interruption.

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

`test begin` and `restart` may stay running while they wait. The coordinator owns the RimWorld process and launches it with `-quicktest`. The readiness mod reports only after a playable map is loaded.

Useful diagnostics:

```text
DevBridge.cmd status
DevBridge.cmd wait-ready
DevBridge.cmd doctor
```

Runtime state and logs are kept in `Runtime`. A lease abandoned for about one hour is reclaimed automatically.
