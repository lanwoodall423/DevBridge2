# DevBridge2

DevBridge2 is a Windows/.NET developer coordinator for RimWorld. It owns safe local lifecycle
operations, test leases, readiness evidence, ModsConfig profiles, generation history, recovery, and
optional authenticated RimBridgeServer routing. It is designed for multiple agents sharing one local
RimWorld installation; it does not replace RimBridgeServer's live-game tools.
When RimLiaison is present, it is the normal agent entry point. DevBridge2 remains the sole lifecycle
owner: agents must not start RimWorld independently through GABS, and profile/ModsConfig mutations
remain with DevBridge2 while it owns a generation.

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

## Build and release

The standard offline gate is:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate.ps1
```

It performs locked restore, Release builds, the complete offline suite, artifact checks, and the
BridgeTools SDK exclusion check. To intentionally update the NuGet lock file, run
`scripts\validate.ps1 -UpdatePackages`, review `Source\BridgeTools\packages.lock.json`, and rerun the
normal gate.

The deterministic package entrypoint is:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 `
  -RimWorldManagedDir "<RimWorld root>\RimWorldWin64_Data\Managed"
```

It refuses a dirty source tree, checks version/changelog consistency, reruns validation, builds the
coordinator and companion, builds the Mod when the managed directory is available, and writes an
ignored `artifacts\release\DevBridge2-<version>` directory with a manifest and SHA-256 checksums.
`-DryRun -AllowDirty` is for local inspection only and marks the resulting identity `.dirty`.

The package contains only the Mod metadata/runtime assembly when available, coordinator runtime files,
the optional companion DLL, wrapper, compatibility contract, and concise documentation. Source,
`bin`/`obj`, Runtime state, PDBs, and proprietary SDK/game assemblies are excluded.

More operational guidance is in [`START_HERE.md`](START_HERE.md) and [`MAINTENANCE.md`](MAINTENANCE.md).
The ownership and state model is summarized in [`docs/architecture.md`](docs/architecture.md).
The optional cross-stack workflow correlation contract is summarized in [`docs/correlation.md`](docs/correlation.md).

When working from a RimLiaison target repository, the normal loop is simply edit, run
`rimliaison affected --run --json`, inspect the result, and edit again. RimLiaison automatically invokes
the owner transaction for build-relevant changes. Inspect `artifactFreshness` before treating a
source-change PASS as valid; `loadedArtifactFreshnessProven: false` is a fail-closed result, not a
successful Quicktest.

DevBridge2's independent Windows validation includes the complete deterministic fake/process-host
E2E suite. The no-RimWorld cross-stack contract gate is owned by RimLiaison and consumes pinned
DevBridge2 revisions through the versioned recipe, generation, and artifact-freshness envelopes;
it is a composition check, not a replacement for this repository's lifecycle tests or the
self-hosted real-RimWorld smoke.

There is currently no explicit root `LICENSE` file; licensing must be resolved before a public release.
