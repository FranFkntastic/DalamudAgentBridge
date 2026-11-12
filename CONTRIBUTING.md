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
FFXIV-Development/
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

Use the smallest source-only check that proves the change; do not run broad
suites by default. CI builds the CLI and MCP server and publishes the utility
and MCP server. There is currently no retained automated test suite.

Write or expand a test only when its actual assertions signal a specific problem
that would be too difficult for the agent to diagnose or repair on its own AND
would seriously frustrate the user. Explain both conditions before authoring,
including the plausible unadvised edit to maintained code, the assertion that
would fail, and the serious user harm it detects. Ordinary coverage, easy fixes,
security or race labels, source-token checks, coverage targets, and workflow
habit are not sufficient. This authorship rule does not independently authorize
deleting existing tests or bypassing configured gates.

Live client actions, slash
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
