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

## Durable aggregate project launches

Register managed project intent before a restart. A plain `DevBridge.cmd restart` always selects the
aggregate contract: the minimal control profile when no active registrations exist, or the deterministic
union and complete dependency closure of every active registration. It never implicitly launches the
production ModsConfig:

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

`restart --projects ...` is compatibility syntax: it registers the caller's request into the same
aggregate and cannot replace or omit other active registrations. `restart --legacy-production` is the
sole explicit human production compatibility path; it is never an automatic fallback or a source of
project attribution and cannot be combined with active intent.

Registrations are durable and owner/session-bound. Renew them with `project renew <registration-id>`
while work continues, and release them explicitly when finished. Expiry and release affect future
generations only. Once a generation freezes, its registration IDs/owners, aliases, resolved project
packages, ordered closure, baseline/profile fingerprints, target generation, and launch owner/request
key are immutable. A late registration is reported in `queuedProjectIntents` and must wait for the next
restart. Verify `frozenRegistrations`, `requestedProjects`, `resolvedMods`, and both fingerprints before
calling `test begin`; test begin is denied when the caller's active registrations are missing.

If the baseline sidecar is absent, the first successful aggregate resolution adopts the exact current
ModsConfig bytes as the durable baseline. `mods capture-baseline` remains an explicit operator action
for intentionally changing that baseline while RimWorld is stopped and no lease or restart is active.

Aliases are `deferred-reality`, `insight-canvas`, `knowledge-framework`, `frontier`, `aquaculture`,
`horticulture`, and `wildlife`. Every managed profile always includes the baseline tooling and
`lan.devbridge2`, then adds requested roots and their full transitive dependency closure. Installed
`About.xml` metadata supplies dependency and load-order constraints. Dependencies precede dependents,
shared dependencies are deduplicated, and missing, ambiguous, malformed, or cyclic graphs fail before
any config write or launch. `ferny.loadthemlast` is never added.

`Runtime/ModsConfig.baseline.xml` is a byte-for-byte recoverable copy. DevBridge records the baseline
and generated hashes in `Runtime/state.json`, writes atomically, and refuses to overwrite an unexpected
user edit. Refreshing the baseline is an intentional `mods capture-baseline` action after the user
has stopped RimWorld and changed the list. `mods restore-baseline` is also explicit and is refused while
leases, a restart, or a RimWorld process exist.

The accepted aliases, resolved package IDs, exact ordered mod list, and SHA-256 profile fingerprint
are persisted with the restart generation. A request arriving after the freeze is queued for the
next generation and cannot replace the pending accepted request; an incompatible owner or unsafe
pending request reports `PROFILE_CONFLICT`.

## Operator workflow

Maintenance ownership is explicit and lease-bound. The owner acquires a lease with
`DevBridge.cmd test session` (or `test begin` for short work), then runs `DevBridge.cmd stop <lease-id>`
from a second command using the same stable agent identity. A successful stop keeps the
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
leases for the new generation. A test lease expires approximately two minutes after its last heartbeat.
Use the connected `DevBridge.cmd test session` command for long-running work: the coordinator renews
only while that named-pipe session is connected, so cancellation, disconnect, or crash stops renewal
without leaving a detached heartbeat process. Short-lived clients may use `test renew <lease-id>` before
`expiresUtc`. Readiness waits, launch attempts, and recovery actions retain finite budgets.

Process operations require the recorded process start identity as well as the PID, so a stale or
reused process is rejected. Replace and hash-check assemblies only after `stop` confirms the process
is gone; `ensure-ready` performs the single controlled relaunch and waits for normal startup. The
mod then requests built-in Dev Quicktest from the genuine main menu and reports readiness only after
a playable map is available. Normal launches do not use command-line Quicktest or save arguments.

Use one stable `DEVBRIDGE_AGENT` value across separate CLI invocations that renew, end, or manage the same lease; both lease ID and agent identity are required. For machine-readable output, append `--json` to `status`, `test begin`, `test renew <lease-id>`, `test end <lease-id>`,
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

When restart is accepted, DevBridge owns it durably even if the requesting shell disconnects. Waiting
is normal: reconnect with `DevBridge.cmd wait-ready` or `status` and keep the agent task alive; do not
end the task merely because the coordinator is waiting on another agent's lease. JSON waiting responses
include `restartQueued`, `nextLeaseExpirationUtc`, and numeric `retryAfterSeconds`. Each lease includes
`lastHeartbeatUtc`, `expiresUtc`, and numeric `retryAfterSeconds`; consumers must not parse a display
string such as `staleIn`.

Example lease projection:

```json
{"id":"T001","agent":"agent-a","lastHeartbeatUtc":"2026-08-11T16:00:00Z","expiresUtc":"2026-08-11T16:02:00Z","retryAfterSeconds":120}
```

`status --json` and `mods status --json` expose `launchProfileMode`, resolver `profileMode`, current and
frozen generations, active/frozen/queued registrations and owners, missing projects, exact ordered
packages, all fingerprints, `aggregateGenerations`, and the next action. During crash isolation the
next action is polling only: do not retry restart, edit ModsConfig.xml, or mutate registrations.
Failures in resolution, metadata, ownership, lease/maintenance, process identity, or launch safety
are fail-closed with zero ModsConfig writes and zero launches.
