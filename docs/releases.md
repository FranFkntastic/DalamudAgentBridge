# Release Process

DAB uses semantic version tags such as `v0.3.0`. A complete release contains:

- `DalamudAgentBridge-utility-win-x64.zip` — the loopback dashboard and HTTP
  utility;
- `dab-mcp-win-x64.zip` — the MCP stdio server;
- `DalamudAgentBridge-plugin.zip` — the in-game connector package; and
- SHA-256 checksums for every archive.

## Automated artifacts

The `release.yml` workflow builds the utility and MCP bundles on GitHub's
Windows runner. Those projects depend only on the focused
`Franthropy.AgentBridge` project and do not require a game installation.

## Complete maintainer release

The connector must compile against the current Dalamud development
installation. On a verified Windows development machine:

```powershell
.\tools\Build-Release.ps1 -Version 0.3.0
```

The script builds all three deliverables, writes checksums, and leaves the
release directory under ignored `artifacts`. Review those files before creating
or updating the GitHub release:

```powershell
gh release upload v0.3.0 .\artifacts\release\v0.3.0\* --clobber
gh release edit v0.3.0 --draft=false
```

The tag workflow creates a draft—not a public incomplete release—because a
GitHub-hosted runner does not have the maintainer's current Dalamud development
installation needed to compile the connector.

Tag only a commit on `main`. The tag version must match the plugin version and
manifest assembly version. Release notes must call out changes to protocol,
capability, action, snapshot, or safety behavior.
