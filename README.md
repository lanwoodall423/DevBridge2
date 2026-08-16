# DevBridge2

DevBridge2 is a Windows/.NET developer coordinator for RimWorld. It owns safe local lifecycle
operations, test leases, readiness evidence, ModsConfig profiles, generation history, recovery, and
optional authenticated RimBridgeServer routing. It is designed for multiple agents sharing one local
RimWorld installation; it does not replace RimBridgeServer's live-game tools.

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
durable state.

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

There is currently no explicit root `LICENSE` file; licensing must be resolved before a public release.
