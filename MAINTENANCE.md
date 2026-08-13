# DevBridge2 maintenance

The coordinator is a small named-pipe server/client in `Source/Coordinator/Program.cs`. One server owns `Runtime/state.json`, active leases, and the RimWorld process. CLI invocations connect to that server, so a caller timing out does not cancel a pending restart.

The RimWorld assembly is `Source/Mod/DevBridge2Mod.cs`. It reads the launch identity and
`DEVBRIDGE_QUICKTEST_REQUESTED`, starts the built-in Dev Quicktest from the main menu after a normal
launch, and atomically writes `Runtime/readiness.json` once `GenScene.InPlayScene`, `Current.Game`, and
`Find.CurrentMap` are all available. The coordinator does not pass a command-line quicktest flag or a
save path.

The lease-holder maintenance path is deliberately explicit: `stop <lease-id>` verifies and terminates
the owned process, confirms that no matching installation process remains, and persists
`gameState=STOPPED`, `maintenanceReady=true`, and a dirty session while retaining the lease. The holder
may replace assemblies during that window and then run `ensure-ready <lease-id>`; no background path
launches while the window is held. A readiness timeout is only a wait failure (`READINESS_TIMEOUT`),
not a replacement-launch request, and retains the exact process identity for a later matching readiness
signal.

Build from the mod root:

```text
dotnet publish Source\Coordinator\DevBridge.Coordinator.csproj -c Release -r win-x64 --self-contained false -o Coordinator
dotnet build Source\Mod\DevBridge2.csproj -c Release -p:RimWorldManagedDir="<RimWorld-managed-dir>"
```

The mod build output is `1.6\Assemblies\DevBridge2.dll`.

## Operator workflow

Maintenance ownership is explicit and lease-bound. The owner acquires a lease with
`DevBridge.cmd test begin`, then runs `DevBridge.cmd stop <lease-id>`. A successful stop keeps the
same lease, leaves RimWorld stopped, and reports `gameState=STOPPED` with `maintenanceReady=true`.
Only that owner may perform the external build, edit, or protected assembly replacement and call
`DevBridge.cmd ensure-ready <lease-id>` afterward. Other owners cannot take the maintenance lease or
start a launch while the maintenance window is held. Dev Bridge never infers that external work is
finished; the lease holder must explicitly call `ensure-ready`.

The same owner request is idempotent while its operation is already accepted, and concurrent
conflicting requests are serialized or rejected by the authoritative coordinator. The coordinator
root and runtime slot are part of ownership; requests for another root or slot do not attach to this
session. Lease contention is a durable queue condition, not a short terminal timeout. If the owned
game is already absent, replacement launch proceeds without terminating anything and retains active
leases for the new generation. A lease heartbeat is required for work longer than 20 minutes; leases
without a heartbeat are reclaimed after that bounded lifetime so a timed-out wrapper cannot block the
runtime indefinitely. Readiness waits, launch attempts, and recovery actions retain finite budgets.

Process operations require the recorded process start identity as well as the PID, so a stale or
reused process is rejected. Replace and hash-check assemblies only after `stop` confirms the process
is gone; `ensure-ready` performs the single controlled relaunch and waits for normal startup. The
mod then requests built-in Dev Quicktest from the genuine main menu and reports readiness only after
a playable map is available. Normal launches do not use command-line Quicktest or save arguments.

Use one stable `DEVBRIDGE_AGENT` value across separate CLI invocations that renew or manage the same lease. For machine-readable output, append `--json` to `status`, `test begin`, `test renew <lease-id>`, `test end <lease-id>`,
`stop <lease-id>`, `ensure-ready <lease-id>`, `restart`, `wait-ready`, or `doctor`. Always inspect
both the native process exit code and the JSON `exitCode`: native `0` is successful completion,
native `2` is usage/request failure, native `4` is an operational refusal or bounded terminal
failure, and `doctor` returns native `1` when its checks fail. A successful status query can still
describe an `ERROR` runtime state.

`PROCESS_INSPECTION_AMBIGUOUS` remains fail-closed until `doctor` completes an authoritative census.
When that census proves that no matching RimWorld process exists and no lease or restart is active,
`doctor` clears the stale process identity and persists `STOPPED` without launching or terminating
anything. The operator must then issue a separate `DevBridge.cmd restart`. An incomplete census or any
matching process preserves the quarantine.
