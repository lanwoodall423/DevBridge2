Project Type

This repository is a development tooling project. Apply the global development/tooling contract except where the tool itself is the system under test.

Tooling-Specific Rules

Do not blindly apply consumer-project orchestration rules to the tool being developed.

For the stack:

```text
RimLiaison -> RimContext -> RimLiaison.Runtime -> RimBridgeServer
```

When developing RimLiaison, use RimLiaison's declared bootstrap/self-test workflow rather than
assuming an installed RimLiaison validates changed RimLiaison source. When developing DevBridge2,
direct execution of this component is allowed when required by its repository test workflow.
DevBridge2 source remains a separate repository and modular implementation boundary, but its
installed runtime, release package, and lifecycle services are RimLiaison-owned internal
components. It has no independent production promotion, fingerprint, or agent release path.
Respect each layer's ownership; do not move responsibilities between layers merely to work around
a failure. Treat structured schemas, statuses, error codes, nextAction, identifiers, and freshness
semantics as integration contracts.

Project metadata boundary:

- `RimLiaison` owns the production product, qualification, promotion, and reliability identity.
- `RimLiaison.Runtime` owns lifecycle, deployment, generations, leases, and runtime execution
  through this repository's modular implementation.
- `RimBridgeServer` remains a separate game-side control boundary.
- Production project identity belongs to the owning repository's `.rimdev/stack.json`; do not add
  or retain production catalogs in `DevelopmentProjects`.
- `DevelopmentProjects` may contain only explicitly classified `fixture`, `test`, `internal`, or
  `example` descriptors, each with `productionEligible: false`.
- RimLiaison supplies a temporary execution contract derived from the owning manifest. This
  component must fail closed on an unclassified production descriptor with
  `EXTERNAL_PRODUCTION_DESCRIPTOR_IN_TOOLING`.
- Keep repository-relative package rules explicit: include the whole runtime mod package and
  exclude `.rimdev`, source, build, and diagnostic state.

The canonical post-edit command is `pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate.ps1`.
It automatically plans from Git changes and reports selected/skipped stages. Do not manually run every
matrix for a routine edit; use `-Full` or `-Conservative` only when the operator or the impact planner
requires a complete safe offline run. Invalid base/head context, unknown/rename/delete changes, and
build/package/runtime configuration must escalate conservatively. The live-stack workflow may use
`-InvariantsOnly` only for its preflight safety check before it builds and runs the owned live smoke.
