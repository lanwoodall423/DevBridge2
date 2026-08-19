Project Type

This repository is a development tooling project. Apply the global development/tooling contract except where the tool itself is the system under test.

Tooling-Specific Rules

Do not blindly apply consumer-project orchestration rules to the tool being developed.

For the stack:

RimLiaison -> RimContext -> DevBridge2

When developing RimLiaison, use RimLiaison's declared bootstrap/self-test workflow rather than assuming an installed RimLiaison validates changed RimLiaison source.
When developing RimContext or DevBridge2, direct execution of that component is allowed when required by its repository test workflow.
Respect each layer's ownership; do not move responsibilities between layers merely to work around a failure.
Treat structured schemas, statuses, error codes, nextAction, identifiers, and freshness semantics as integration contracts.
Changes to cross-layer behavior should receive integration coverage with adjacent layers or representative consumers.

Use the repository's own bootstrap and validation instructions as authoritative for testing the tool itself.

The canonical post-edit command is `pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\validate.ps1`.
It automatically plans from Git changes and reports selected/skipped stages. Do not manually run every
matrix for a routine edit; use `-Full` or `-Conservative` only when the operator or the impact planner
requires a complete safe offline run. Invalid base/head context, unknown/rename/delete changes, and
build/package/runtime configuration must escalate conservatively. The live-stack workflow may use
`-InvariantsOnly` only for its preflight safety check before it builds and runs the owned live smoke.
