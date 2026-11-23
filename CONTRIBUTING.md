# Contributing to Dalamud Agent Bridge

Thanks for helping make agent-assisted Dalamud development safer and more
useful. Bug reports, documentation improvements, protocol proposals, and
implementation pull requests are welcome.

## Before You Start

Open an issue before making a large protocol, security-boundary, or architecture
change. Small fixes can go directly to a pull request.

The utility, CLI, and MCP server build from this repository
alone using the published `Franthropy.AgentBridge` package. Clone
[Franthropy](https://github.com/FranFkntastic/Franthropy) beside this repository
only when changing the in-game connector, which consumes the patch-sensitive
`Franthropy.Dalamud` adapter as source:

```text
workspace/
  DalamudAgentBridge/
  Franthropy/
```

The build accepts `FranthropyDalamudProject` when you need a different layout.
Before building the connector, check out the exact revision recorded in
`Franthropy.commit` and leave that checkout clean. The connector build verifies
this receipt automatically, while utility and MCP work remains independent of
the sibling checkout.

## Branch and Pull Request Flow

Create your branch from `main` and target `main` in the pull request.
Keep each change focused enough to review as one safety or capability decision.
Explain what changed, why it belongs in DAB or Franthropy, and which checks
proved it.

Never commit game logs, crash bundles, screenshots, access tokens, discovery
advertisements, plugin configuration, or machine-specific paths. The repository
ignores `*.tspack`, but contributors remain responsible for reviewing staged
content and history before pushing.

## Verification

CI builds the CLI and MCP server and publishes the utility and MCP bundles.
Verify your changes by building the affected projects, and note in your pull
request what was checked via source builds versus tested in-game.

Changes to frame-reviewed controls must preserve stable semantic IDs,
current-frame validation, expiry, replay resistance, rendered-state checks, and
explicit enabled-state checks. New agent actions should return structured
receipts instead of relying on pixels or timing.

## Shared Boundaries

Product-neutral Dalamud primitives used by multiple plugins belong in
Franthropy. DAB owns its transport, authentication, discovery aggregation,
agent-facing commands, and safety policy. Product-specific workflows and
consequential game actions remain in their owning plugin.

Do not add arbitrary reflection invocation, coordinate input, inferred mutation,
or permissive fallbacks for unsupported plugins. Read-only observation may be
discovered dynamically, but mutation must remain explicitly declared and
reviewable.
