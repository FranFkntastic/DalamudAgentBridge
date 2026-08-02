# Contributing to Dalamud Agent Bridge

Thanks for helping make agent-assisted Dalamud development safer and more
useful. Bug reports, documentation improvements, protocol proposals, focused
tests, and implementation pull requests are welcome.

## Before You Start

Open an issue before making a large protocol, security-boundary, or architecture
change. Small fixes can go directly to a pull request.

The utility, CLI, MCP server, and protocol tests build from this repository
alone using the published `Franthropy.AgentBridge` package. Clone
[Franthropy](https://github.com/FranFkntastic/Franthropy) beside this repository
only when changing the in-game connector, which consumes the patch-sensitive
`Franthropy.Dalamud` adapter as source:

```text
FFXIV-Development/
  DalamudAgentBridge/
  Franthropy/
```

The build accepts `FranthropyDalamudProject` when you need a different layout.

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

Use the smallest source-only check that proves the change. Relevant focused
tests live in `tests/DalamudAgentBridge.Tests`. Live client actions, slash
commands, screenshots, plugin reloads, and game-state mutation are never implied
by a test request; describe any live verification separately and obtain the
client owner's permission first.

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
