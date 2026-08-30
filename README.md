# RimLiaison.Runtime

This repository contains the modular Windows/.NET runtime component used by RimLiaison for
RimWorld lifecycle, leases, readiness, deployment, generations, recovery, and runtime execution.
The source remains separate from RimLiaison to preserve module ownership and focused validation.
Its installed package lives under `RimWorld\Mods` for technical reasons, but it is not an
independent production product: RimLiaison owns its qualification, single production fingerprint,
promotion, and ordinary agent workflow. It does not replace the separate RimBridgeServer game-side
control boundary.

When RimLiaison is present, it is the only ordinary agent entry point. DevBridge commands below are
component diagnostics and development operations; human production administration uses the
RimLiaison `doctor`, `status`, `reset`, `recover`, qualification, and promotion commands.

## Support and requirements

- RimWorld **1.6**.
- Windows with the .NET SDK pinned by [`global.json`](global.json): **8.0.424** for the coordinator and
  offline tooling.
- A local RimWorld installation for the Mod build and live integration. Its proprietary managed
  assemblies are never committed or required by the standard offline gate.
- The optional BridgeTools companion compiles against `RimBridgeServer.Sdk` `2.0.0`, but the runtime
  SDK assembly is supplied by the RimBridgeServer host and is never bundled.

## Quick start

From the repository root:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate.ps1
DevBridge.cmd status --json
DevBridge.cmd restart
DevBridge.cmd wait-ready
DevBridge.cmd test begin
```

Use `DevBridge.cmd test end <lease-id>` when the test lease is finished. Use `doctor --json` after a
refusal or unexpected state. `coordinator shutdown` gracefully reloads the host while preserving
durable state. If an older build reports a legacy runtime slot, first run that build's
`coordinator shutdown`, then run `DevBridge.cmd coordinator migrate-legacy-slot --json` with the current
build. The migration is guarded, creates an exact state backup, and atomically updates the namespace.

## Real RimWorld compatibility gate

The canonical unattended end-to-end gate is the DevBridge-owned smoke script:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\live-stack-smoke.ps1 -Json
```

It is a self-hosted Windows operation. It first runs `-Plan -Json` checks, then uses the dedicated
`DevelopmentProjects\live-stack-fixture.json` and existing `mod-test.ps1` transaction to build the
deterministic, net472-loadable fixture into staging, hash and deploy it into the active declared
project mod's RimWorld `1.6\Assemblies` path when needed, and establish a verified generation. It
then runs the `live-stack-smoke` semantic recipe,
RimLiaison capability discovery, a bounded RimLiaison UI target/screenshot capture, and the controlled
`live-stack-diagnostic` recipe. The resulting operation is ingested through RimError and must retain
the operation, workflow, and generation identities. Cleanup ends the owner lease and verifies that it
is absent. No step launches GABS, edits `ModsConfig.xml`, or treats `_quarantine` as an installed mod.

The command returns one compact JSON object and writes `Runtime\live-stack-smoke-last.json` by
default. A successful run records only its exact RimWorld/RimBridgeServer/SDK/DevBridge2 tuple in
`RimBridgeProtocolCompatibility.json`; an unavailable or failed run leaves compatibility claims
unchanged. Use `-AllowUiSkip` only on a deliberately non-visual host; the default gate requires UI
evidence. `-Plan -Json` is safe for prerequisite checks and is also what the self-hosted workflow
uses before building or launching anything.

The manual GitHub Actions entry point is `.github/workflows/live-stack-smoke.yml` and requires a
self-hosted runner labeled `Windows` and `rimworld`, with `RIMWORLD_ROOT` set to the installed game
root and the active `brrainz.rimbridgeserver` mod directly under `RimWorld\Mods`. Ordinary hosted CI
uses only `scripts\validate.ps1` and never claims live compatibility.

## Build and component packaging

The canonical impact-aware offline gate is:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate.ps1
```

It inspects the Git change set and selects the minimum safe restore/build/test stages. The
deterministic `scripts\release.ps1` entrypoint produces an internal runtime component package for
RimLiaison integration and local inspection. It does not publish a standalone production identity;
RimLiaison qualification and promotion are the only production release path.

The package contains the Mod metadata/runtime assembly when available, coordinator runtime files,
the optional companion DLL, wrapper, protocol contract, and concise documentation. Source,
`bin`/`obj`, Runtime state, PDBs, and proprietary SDK/game assemblies are excluded.

More operational guidance is in [`START_HERE.md`](START_HERE.md) and [`MAINTENANCE.md`](MAINTENANCE.md).
The ownership and state model is summarized in [`docs/architecture.md`](docs/architecture.md).
The optional cross-stack workflow correlation contract is summarized in
[`docs/correlation.md`](docs/correlation.md).


When working from a RimLiaison target repository, the only ordinary agent loop is:
`rimliaison affected --run --fail-fast --json`. RimLiaison invokes the internal runtime transaction
for build-relevant changes and owns the production evidence. Inspect `artifactFreshness` before
treating a source-change PASS as valid; `loadedArtifactFreshnessProven: false` is fail-closed.

This repository's validation covers the runtime component's deterministic fake/process-host
behavior. RimLiaison owns the cross-stack contract, production qualification, promotion, and
artifact-freshness proof; the self-hosted real-RimWorld smoke remains a separate component
integration gate.

There is currently no explicit root `LICENSE` file; licensing must be resolved before a public release.
