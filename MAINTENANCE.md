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

## Coordinator artifact refresh

Publish or replace coordinator artifacts with RimWorld still running, then request a graceful coordinator
refresh:

```text
dotnet publish Source\Coordinator\DevBridge.Coordinator.csproj -c Release -r win-x64 --self-contained false -o Coordinator.next
DevBridge.cmd coordinator shutdown
# Replace the deployed Coordinator files with Coordinator.next after shutdown.
DevBridge.cmd status --json
```

`coordinator shutdown` completes the requester response and terminal marker before releasing the per-slot
mutex and pipe. It preserves `Runtime/state.json`, leases, and the current RimWorld process; it is the normal
way to reload the coordinator binary, environment variables, or configuration. A connected long-running
command may be drained and should reconnect rather than resubmit its accepted durable operation.

Use `stop <lease-id>` only for the separate assembly/ModsConfig maintenance workflow below. `stop` is the
operation that intentionally stops RimWorld and opens `maintenanceReady`; coordinator refresh alone never
stops RimWorld.

Build from the mod root:

```text
dotnet publish Source\Coordinator\DevBridge.Coordinator.csproj -c Release -r win-x64 --self-contained false -o Coordinator
dotnet build Source\Mod\DevBridge2.csproj -c Release -p:RimWorldManagedDir="<RimWorld-managed-dir>"
```

The mod build output is `1.6\Assemblies\DevBridge2.dll`.
`CHANGELOG.md`'s latest declared release is the version source of truth (`1.2.4`); the packaged mod
metadata and coordinator/mod assembly project versions are kept aligned with it.

## RimBridgeServer coexistence

DevBridge owns lifecycle and coordination state: it is authoritative for starting, stopping, restarting,
profiles, `ModsConfig.xml`, generations, maintenance, and test leases. RimBridgeServer owns live-game
inspection and control, including debug actions, screenshots, saves, and profiling. DevBridge does not
run GABS or delegate lifecycle ownership to it, and RimBridgeServer may still be started directly in its
standalone mode.

Integration is configured with `DEVBRIDGE_RIMBRIDGE_MODE=off|optional|required` (default `off`) and,
if needed, `DEVBRIDGE_PLAYER_LOG=<path>`. The base profile always resolves
`brrainz.rimbridgeserver` before writing ModsConfig; missing, ambiguous, or malformed metadata fails
profile resolution. Required mode additionally requires a same-launch endpoint; optional mode keeps
endpoint failures visible without blocking ordinary playable-map readiness; off mode disables DevBridge's
endpoint integration. This base-profile membership is separate from endpoint mode: RimBridgeServer remains
in the resolved profile even when endpoint integration is `off`, while the optional companion below only
strengthens diagnostics and generation identity evidence. Inspect with `DevBridge.cmd bridge status`. Obtain credentials only with the explicit
`DevBridge.cmd bridge endpoint` command (or its dedicated JSON response); ordinary status and persisted
integration metadata never include the token. The endpoint is discarded whenever process identity,
launch, generation, stop, restart, or maintenance identity changes. Player.log parsing starts after a
pre-launch boundary, uses the port RimBridge logged rather than a default, requires loopback, and has
a bounded verification window.

The optional `Source/BridgeTools/DevBridge2.BridgeTools.csproj` companion adds the authenticated,
read-only tool `devbridge/get_generation_context`. It reads the launch environment inherited by the
RimWorld process and `Runtime/state.json` fallback, then returns `launchId`, `generation`,
`profileFingerprint`, `baselineFingerprint`, `profileMode`, `processId`, `processStartUtcTicks`,
`devBridge2ModVersion`, and schema/error fields. It never returns the RimBridge token, writes state,
restarts/stops RimWorld, or edits ModsConfig. The core `DevBridge2.dll` has no RimBridgeServer SDK
reference. Use the repository-owned workflow after a coordinator refresh:

```text
DevBridge.cmd coordinator shutdown
powershell -NoProfile -ExecutionPolicy Bypass -File .\Publish-DevBridge.ps1 -Configuration Release -DeployCompanion -DeploymentRoot "<active DevBridge2 mod root>" -RimBridgeSdkPath "<host RimBridgeServer.Sdk.dll>"
DevBridge.cmd status --json
```

The script publishes the coordinator, rebuilds the companion, and deploys exactly
`<RimWorld root>\BridgeTools\DevBridge2\DevBridge2.BridgeTools.dll`, derived from the validated active
mod root. For a live host, pass its matching `RimBridgeServer.Sdk.dll` with `-RimBridgeSdkPath` (or set
`DEVBRIDGE_RIMBRIDGE_SDK_PATH`); this is a compile-time reference only. It removes the obsolete nested
`<active mod root>\BridgeTools` companion directory, verifies the deployed hash, and refuses to copy
`RimBridgeServer.Sdk.dll`; RimBridgeServer supplies that host SDK. For a source-only build use
`-CompanionOnly` without `-DeployCompanion`. An unresolved or non-DevBridge2 deployment root fails
before copying. The coordinator uses endpoint-only operation when the companion is absent, and otherwise
authenticates with GABP and requires matching launch ID, generation, and PID. A mismatch is
`RIMBRIDGE_COMPANION_IDENTITY_MISMATCH` and fails closed; an unavailable tool is visible as
`RIMBRIDGE_COMPANION_UNAVAILABLE` without blocking optional endpoint integration. Doctor distinguishes
`BRIDGETOOLS_ASSEMBLY_NOT_DISCOVERED`, `BRIDGETOOLS_WRONG_LOCATION`, `BRIDGETOOLS_LOAD_FAILED`, and
`BRIDGETOOLS_STALE_BINARY`; status additionally reports bounded companion categories such as
`TOOL_NOT_REGISTERED`, `TOOL_CALL_FAILED`, `GENERATION_CONTEXT_INCOMPLETE`, and
`ENDPOINT_UNAVAILABLE`. These categories never contain credentials or tokens.

### Routed RimBridge workflow

For live-game tools, acquire a normal DevBridge test lease and route the call through the coordinator:

```text
DevBridge.cmd test begin
DevBridge.cmd bridge tools --lease <lease-id> --json
DevBridge.cmd bridge call rimworld/get_game_state {"include":"colonists"} --lease <lease-id> --json
```

Before forwarding, DevBridge validates the active launch/generation, PID and process-start identity,
same-generation loopback endpoint, optional companion identity, policy, and lease. It creates a fresh
authenticated GABP session per call with the current generation token; credentials are not reused after
authentication failure and are never printed in status, errors, or provenance. Route results retain
generation, launch, profile, PID, endpoint, tool, timestamp, and opaque tool evidence metadata.

Persistent `rimworld/set_mod_enabled` and `rimworld/reorder_mod`, plus lifecycle tools, are centrally
denied with `RIMBRIDGE_OPERATION_BLOCKED_BY_DEVBRIDGE_POLICY`; use the exclusive `stop <lease-id>` →
edit/profile or baseline reconciliation → `ensure-ready <lease-id>` workflow instead. A missing bridge,
stale identity, authentication failure, tool-not-found, timeout, or protocol error is reported as a
bounded route failure and never starts an automatic restart. `bridge policy` and `status --json` expose
the policy without credentials. Routing is optional and preserves direct live-game RimBridge use when
DevBridge integration is off or the endpoint is unavailable.

### ModsConfig ownership boundary

RimBridgeServer remains appropriate for live-game observation and interaction. DevBridge alone owns
`ModsConfig.xml`, the enabled-mod set, mod order, accepted profile, generation identity, lifecycle,
maintenance, and test leases. Therefore `rimworld/set_mod_enabled` and `rimworld/reorder_mod` must not be
used to persist changes while a DevBridge generation is owned. Route those changes through the explicit
DevBridge baseline/profile workflow; this is an integration ownership rule, not a defect in RimBridgeServer.

The policy is visible with `DevBridge.cmd bridge policy` and in `status`, `doctor`, and `mods status` JSON
as `rimBridgePolicy`, `modsConfigMutationAuthority`, and `externalModsConfigMutation`. The optional
read-only companion tool `devbridge/get_control_policy` returns the same owner/generation/frozen-profile
contract without credentials or mutation methods. `CONTROLLED_FROZEN` denotes accepted generated content;
`DEVBRIDGE_TRANSITION` denotes an authorized DevBridge rewrite; `EXTERNAL_MUTATED` is fail-closed evidence;
and `NOT_GENERATION_OWNED` means no accepted generation currently owns the file.

An unexpected change produces `PROFILE_EXTERNAL_MUTATION` with generation, launch ID, expected and observed
fingerprints, and detection time. DevBridge does not absorb the changed bytes, update the accepted profile,
or repeatedly restart. The current generation is no longer trustworthy; run `DevBridge.cmd mods status`,
stop/clear any active work as required, and use explicit `mods capture-baseline` or `mods restore-baseline`
maintenance reconciliation before launching again.

## Durable aggregate project launches

Register managed project intent before a restart. A plain `DevBridge.cmd restart` always selects the
aggregate contract: the minimal control profile when no active registrations exist, or the deterministic
union and complete dependency closure of every active registration. It never implicitly launches the
production ModsConfig:

Use aggregate-first coordination: register immediately even when other project intents or test leases
are active. Do not wait for an exclusive profile; active tests delay a replacement launch or test start,
not registration. Reserve a project-only run for baseline reproduction, isolation after a combined
failure, or a known incompatibility.

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
`horticulture`, and `wildlife`. Every managed profile always includes the baseline tooling,
`lan.devbridge2`, and `brrainz.rimbridgeserver`, then adds requested roots and their full transitive
dependency closure. Installed
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

### Read-only project resolution

Use the resolver as a planning API before creating project intent:

```text
DevBridge.cmd project resolve horticulture,aquaculture --json
```

The response's `projectResolution` object includes canonical aliases, requested package IDs, exact
resolved order, dependency/load-order edges, deterministic fingerprints, per-mod provenance,
pinned-generation comparison, warnings, errors, and next actions. Add `--explain` for a compact
human-readable provenance view. Provenance explains control/tooling roots, project roots,
dependencies, load-order relationships, official content, and other required baseline components.

Resolve performs the same meaningful resolver work used by a real launch but is strictly no-mutation:
it does not create registrations, change leases, write `ModsConfig.xml` or the baseline, queue a
restart, increment generation, or launch/stop RimWorld. The safe workflow is: resolve with JSON,
inspect the exact closure and fingerprint, register, restart/wait-ready, then verify the frozen
generation and order in `status --json`.

Comparison uses the immutable accepted-generation manifest and reports packages added/removed, order
and project-intent changes, fingerprint change, and restart requirement. `status` and `doctor` expose
`currentGenerationTrust` and `nextGenerationConfig`; a `VALID` current generation remains manageable
when future aliases or metadata are invalid, while a new generation requiring bad configuration is
refused before any launch or ModsConfig write.

### Declared generation test inputs

Dev Bridge supports only bounded, typed inputs for its existing built-in Quicktest behavior. The
planning command accepts repeated assignments such as:

```text
DevBridge.cmd project resolve horticulture --input quicktest=true --input quicktestTimeoutSeconds=45 --input quicktestVariant=builtin-dev --json
```

The declarations are `quicktest` (boolean), `quicktestTimeoutSeconds` (integer 5--120), and
`quicktestVariant` (`builtin-dev` or `disabled`). Normalization is case-insensitive where
appropriate and is part of the prospective profile fingerprint. The accepted normalized values
are copied into the frozen profile, immutable generation manifest, semantic history, and status.
They control only the Dev Bridge-owned Quicktest activation path; there is no raw argv passthrough,
shell expansion, caller-selected environment key, arbitrary environment entry, save argument, or
generic game-launch flag. Secret-shaped values are not a supported input surface.

Inputs are validated before ModsConfig writes and process launch. A request for an already pending
generation must match its frozen inputs; otherwise it fails with `TEST_INPUT_CONFLICT` and leaves
the pending generation untouched. A changed input after acceptance is evaluated only for the next
generation, and resolve JSON reports the normalized values, input comparison, fingerprint, and
restart requirement. This makes incompatible concurrent intent deterministic and preserves the
accepted manifest as evidence.

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

### Authoritative doctor audit

`DevBridge.cmd doctor` and `doctor --json` are the authoritative forensic audit. The audit runs
coordinator, process, ModsConfig, profile, generation, lease/maintenance, readiness, recovery,
version/schema, and permission checks independently and reports all findings in one invocation;
an error in one component does not short-circuit the others. After a refusal or bounded failure,
collect the machine-readable state first, then the audit:

```text
DevBridge.cmd status --json
DevBridge.cmd doctor --json
```

Doctor JSON is `devbridge-doctor/v1` with stable `schemaVersion`, `healthy`, `findings`, `components`,
`operationalState`, and `nextActions`. Findings carry severity, code, message, component, details,
and safe structured actions. Output ordering is deterministic, actions are centralized and
lease-parameterized where required, and no diagnostic action blindly kills a process, edits
ModsConfig, or restarts an active generation. Secret-shaped values are redacted from doctor and
ordinary status diagnostics. Use only the returned `nextActions`; native doctor exit code `0`
means healthy and `1` means at least one `ERROR` finding.

### Generation manifests and history

An accepted generation is the point at which the coordinator has validated process identity,
readiness, and the frozen profile. It then atomically persists the semantic history record and
creates the immutable `Runtime/generations/<generation>.json` manifest. The manifest pins the
accepted timestamp, launch identity, profile and exact resolved order, registration evidence,
ModsConfig fingerprints, process/readiness evidence, and component schemas. It is not a live view of
`state.json` and is never updated after acceptance.

Use `DevBridge.cmd history`, `history show <generation>`, and `history last-good` for text or stable
JSON views. A failed launch has a terminal semantic record but no accepted manifest. A later
`STOPPED` result updates an already accepted generation without clearing last-known-good. Duplicate
callbacks and reconnects are idempotent. Treat `GENERATION_HISTORY_CORRUPT` or
`GENERATION_MANIFEST_CORRUPT` as an integrity incident: run `doctor --json`, preserve both files,
and do not hand-edit or retry a launch loop.
