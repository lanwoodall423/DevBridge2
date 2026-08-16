# DevBridge2 maintenance

## Engineering gate

Run the repository-owned offline validation command before submitting a change:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate.ps1
```

It restores and builds the Release Coordinator, Coordinator.Tests, and BridgeTools projects, runs the
complete offline coordinator suite (including version/schema consistency and coordinator lifecycle
regressions), checks diff whitespace, verifies that coordinator build products are not tracked, and
verifies that BridgeTools does not emit `RimBridgeServer.Sdk.dll`. GitHub Actions runs the same gate on
pull requests and pushes to `main`.

The standard gate deliberately does not build `Source\Mod\DevBridge2.csproj`: that project requires the
proprietary `Assembly-CSharp.dll` and `UnityEngine.CoreModule.dll` from a local RimWorld installation.
Mod compilation, live deployment, and host integration are separate release/local gates. For a local mod
build, provide the installed managed directory explicitly:

```powershell
dotnet build .\Source\Mod\DevBridge2.csproj -c Release -p:RimWorldManagedDir="<RimWorld root>\RimWorldWin64_Data\Managed"
```

Never commit generated contents from `Coordinator_build\`, `Coordinator\` (keep only `.gitkeep`),
`Runtime\`, `BridgeTools\`, `Source\**\bin\`, `Source\**\obj\`, or `1.6\Assemblies\` (keep only its
intentional placeholder). Never commit or deploy `RimBridgeServer.Sdk.dll`; the live RimBridge host
supplies it.

## Version, toolchain, and release policy

`Source\Directory.Build.props` is the single authoritative product-version source. Assembly versions
derive from that property. `About\About.xml` and the explicit release heading in `CHANGELOG.md` must
match it; the release script renders the package About metadata from that value rather than copying an
unverified version. Do not add product-version literals to tests or individual project files.

The repository pins the exact .NET SDK in `global.json` (`8.0.424`, no roll-forward). The only current
NuGet dependency is the compile-time `RimBridgeServer.Sdk` package for BridgeTools, locked in
`Source\BridgeTools\packages.lock.json`. To intentionally update dependencies, run
`scripts\validate.ps1 -UpdatePackages`, review the resolved version/content hash and compatibility
contract, then run the normal locked gate and the release dry run. CI and release use locked restore.

New protocol/hosting/diagnostic files must opt into nullable analysis with `#nullable enable` (or a
nullable-enabled project). The current incremental boundary is the GABP DTO contract and coordinator
IPC protocol; legacy Core, host, Mod, and test files remain nullable-disabled until migrated in focused
follow-up changes. Do not blanket-suppress nullable warnings. The remaining migration is:

- migrate Core lifecycle/persistence/diagnostic files in cohesive ownership slices;
- migrate host command/process glue after each slice has warning-clean tests;
- migrate net472 Mod/BridgeTools files only with framework-compatible annotations;
- remove project-level nullable-disabled settings when each slice is complete.

Run a deterministic package build with:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 `
  -RimWorldManagedDir "<RimWorld root>\RimWorldWin64_Data\Managed"
```

It requires a clean tree, reruns the locked validation gate, publishes the coordinator, rebuilds
BridgeTools, optionally builds the Mod when the managed assemblies are present, renders a clean
`artifacts\release\DevBridge2-<version>` directory, and writes `release-manifest.json` plus
`SHA256SUMS.txt`. A dirty inspection is allowed only with `-DryRun -AllowDirty`; its identity is marked
`.dirty` and must not be published. No release binaries are written into normal source directories.

## Build identity and coordinator IPC

Every running coordinator reports a `coordinatorBuild` object in `status --json`, `doctor --json`, and
the terminal result of every versioned named-pipe request. When a deployed `Coordinator\DevBridge.Coordinator.dll`
is present, the same responses also include `publishedCoordinatorBuild` and
`coordinatorBuildMatchesPublished`, so a running process can be compared with the published files without
opening them manually. The identity includes `productVersion`,
`informationalVersion`, `sourceRevision`, `dirty`, `buildConfiguration`, `processStartedUtc`, and the
coordinator protocol version/contract. Deployed mod and BridgeTools assemblies are reported as
`modBuild` and `bridgeToolsBuild` in the doctor component version section when they are present.
Published clean builds use the source revision in the informational version; local worktree builds are
marked with `+<revision>.dirty`, so a dirty build cannot present itself as the clean published revision.

Coordinator named-pipe IPC is version 2. Requests, events, and terminal results are distinct JSON
envelopes correlated by `requestId`; finite commands produce one terminal result, while `wait-ready`,
`restart`, and test sessions may remain connected and emit events. The compatibility boundary is
intentionally bumped: v1 and other unsupported clients fail immediately with an incompatible-protocol
error and are not silently parsed. The version is sourced from
`DevBridgeSchemaVersions.CoordinatorProtocolMajor`; update clients and coordinators together. IPC
diagnostics never include RimBridge credentials. The server uses the .NET `CurrentUserOnly` named-pipe
option: the coordinator control plane is trusted only within the Windows account that launched it, and
is not a machine-wide or `Everyone` endpoint. The coordinator still validates every request and owns a
maximum of 16 concurrent clients.

IPC input and output are bounded before dispatch: the maximum frame is 256 KiB, a request is 128 KiB,
commands are 128 characters, there are at most 64 arguments, each argument is at most 4 KiB, event
messages are at most 16 KiB, JSON-buffered event output is capped at 1,024 messages/128 KiB, and result
payloads are at most 192 KiB. Stable errors include
`FRAME_TOO_LARGE`, `REQUEST_TOO_LARGE`, `COMMAND_TOO_LONG`, `ARGUMENT_COUNT_EXCEEDED`,
`ARGUMENT_TOO_LONG`, `REQUEST_METADATA_TOO_LARGE`, `INCOMPATIBLE_PROTOCOL`, and `OUTPUT_TOO_LARGE`.
Oversized or malformed frames do not enter coordinator state or launch logic.

### Coordinator operational trace

The coordinator writes best-effort JSON Lines to `Runtime/coordinator-events.jsonl`. Each entry has a
stable `timestampUtc` and `event`, plus bounded `requestId`, `operationId`, `command`, `runtimeSlotId`,
`generation`, `phase`, `durationMs`, `success`, `errorCode`, `category`, and (for the startup event) a
safe `buildIdentity` projection. The same IPC `requestId` is retained from request acceptance through
dispatch, persistence/lifecycle work, response serialization, and the terminal result. A recovery worker
uses an operation ID when no client request exists.

The active file is capped at 512 KiB. Rotation retains at most `coordinator-events.jsonl.1` through
`.3`, so the trace is bounded to the current file plus three retained files. Writes are serialized for
ordering, but diagnostics are fail-open: a write or rotation failure disables tracing for that coordinator
instance and never changes lifecycle state, persistence decisions, or process-safety behavior.

Use the trace when a finite command has an unexpected result or disconnect: locate its `requestId`, then
check that `command.dispatch.completed`, `state.save.completed`, and `ipc.terminal_result.write` occur
in the expected order. `lifecycle.phase.transition`, process launch/termination, history/manifest, and
shutdown events provide the surrounding boundary evidence. Trace entries intentionally contain no
RimBridge token, authorization secret, raw environment variable, argument list, tool payload, endpoint
credential, or raw exception message. Text projections are redacted and capped; route events contain
only a safe operation category and machine-readable error code.

### Deterministic model and crash-injection tests

The offline executable extends the case-based suite with five fixed state-machine seeds
(`13579BDF`, `2468ACE1`, `0051A7E5`, `C0FFEE42`, and `7F4A7C15`). The standard run executes 32
operations per seed (160 generated operations); `DEVBRIDGE_MODEL_STRESS=1` runs 72 operations per
seed for local stress testing. Operations include lease, project, lifecycle, recovery, readiness,
ModsConfig, generation-history, endpoint, companion, shutdown/restart, and malformed-input actions.
Every step checks process ownership cardinality, maintenance absence, READY evidence, monotonic
generations, target-generation validity, finite launch budgets, exact lease ownership, immutable
generation manifests, mutation-free rejected requests, secret absence, and one-result IPC completion.
Failures print the seed, step, and bounded operation/output trace so the case can be reproduced.

The same suite exposes inert-by-default coordinator fault points around durable state writes and
atomic replacement, process action/state persistence, STOPPED-before-result, terminal-result teardown,
history/manifest writes, ModsConfig transitions, project aggregate freezing, crash-isolation attempt
persistence, and graceful coordinator shutdown. Tests restart from the resulting Runtime directory and
assert the fail-closed recovery contract for restart, stop, ensure-ready, baseline capture/restore,
generated ModsConfig installation, generation history, project freeze, crash isolation, and endpoint
invalidation. Production options leave the injector unset; a diagnostic or persistence failure never
selects an unsafe fallback action.

Durable identifiers use the complete value for authorization, persistence, and equality. New leases are
`lease-` plus a 128-bit uppercase GUID; generated project registrations use a deterministic 96-bit
hash suffix; request IDs and launch IDs retain full GUID-width values. Existing short persisted lease
IDs remain valid until released or expired so operators do not lose an authorization capability during an
upgrade; they are never used as a prefix match. Explicit caller-supplied registration/ticket IDs and
content-derived launch request keys retain their existing semantics.

Runtime scope names are derived with separate helpers: `CanonicalizeRootPath` and `HashCanonicalPath`
normalize a root path, while `HashOpaqueIdentifier` hashes runtime slots without calling
`Path.GetFullPath`. New runtime slots are `slot-` plus 24 uppercase SHA-256 hex characters (96 bits),
and pipe/mutex names include the canonical root and use the same 96-bit practical hash length. A state
file containing the old 8-hex-character runtime slot is rejected before startup. The supported migration is:
use the older coordinator to perform a graceful `coordinator shutdown`, preserve `Runtime/state.json`, install
the current build, and run `DevBridge.cmd coordinator migrate-legacy-slot --json`. The migration acquires both
slot ownership mutexes, requires no live persisted RimWorld identity, active lease, or active lifecycle operation,
copies an exact `Runtime/state.json.legacy-slot-*.bak` backup, and atomically updates the top-level slot plus
matching scope tickets. It is idempotent and fails with a stable error code rather than silently rebinding an
ambiguous artifact.

The Coordinator source boundary is deliberate:

- `Source/Coordinator.Core/DevBridge.Coordinator.Core.csproj` owns transport-independent state,
  lifecycle, leases, persistence, profiles, recovery, generation history, ModsConfig ownership,
  process abstractions, diagnostics, and RimBridge integration logic.
- `Source/Coordinator/DevBridge.Coordinator.csproj` is the executable host. It owns `Program`, argument
  parsing, the named-pipe client/server, framing, and process startup glue, and references Core.
- `Source/Coordinator.Tests/DevBridge.Coordinator.Tests.csproj` references both assemblies. It must not
  link Coordinator implementation files with `Compile Include`; the BridgeTools contract sources linked
  there are intentional shared test support and are not Coordinator core.

One host server owns `Runtime/state.json`, active leases, and the RimWorld process. CLI invocations
connect to that server, so a caller timing out does not cancel a pending restart.

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

`coordinator shutdown` completes the requester response and terminal result before releasing the per-slot
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
`Source\Directory.Build.props` is the authoritative product-version source. The packaged mod metadata,
CHANGELOG release heading, and coordinator/mod/BridgeTools assembly versions are kept aligned with it.

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

### GABP/RimBridge compatibility contract

The supported client contract is centralized in
[`RimBridgeProtocolCompatibility.json`](RimBridgeProtocolCompatibility.json) and implemented by the
typed DTOs in `Source\Coordinator.Core\Integrations\RimBridge\RimBridgeProtocolContract.cs`.
The repository currently claims only the `gabp/1` envelope (GABP major 1), verified by offline wire
fixtures. It claims no live RimBridgeServer version until a local host smoke test has passed. The
BridgeTools compile contract is `RimBridgeServer.Sdk` `2.0.0`; that SDK is a compile-time host
override and is never bundled with the mod, coordinator, or BridgeTools output. The companion remains
optional, and endpoint-only operation remains supported.

The client intentionally keeps a small local DTO/framing implementation instead of adding
`Gabp.Runtime` as a runtime dependency: the coordinator is net8, the optional companion is net472 and
host-loaded, and the typed boundary keeps deployment independent of proprietary host assemblies.

The contract covers authenticated `session/hello` fields (`token`, `bridgeVersion`, `platform`,
`launchId`) and optional typed `clientInfo`, `tools/list` with `{}` and a `tools` array result,
`tools/call` with typed `name` and JSON-object `arguments`, GABP error mappings, Content-Length
framing limits, and response version/request correlation. Other GABP versions, malformed framing,
invalid envelopes, and mismatched response IDs are intentionally unsupported and fail boundedly.

When upgrading RimBridgeServer or its SDK, update the tested-version declaration and SDK version in
the JSON contract, update the typed fixtures for any shape change, run the complete
`scripts\validate.ps1` gate (including BridgeTools compilation), and perform the optional local live
smoke test against the exact host assembly before claiming that host version as supported. A version
change without matching metadata and contract tests is rejected by the validation script. Do not add
the proprietary host assembly to source control or CI.

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
