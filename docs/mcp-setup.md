# Connect DAB to Codex

DAB's MCP server is a local stdio executable. Codex starts it on demand; the
server then discovers authenticated bridge advertisements under the current
Windows user's XIVLauncher profiles.

## Prerequisites

1. Install or build a DAB release and locate `dab-mcp.exe`.
2. Run the DAB utility and install the in-game connector through its
   loopback-only repository.
3. Start FFXIV with Dalamud and confirm the connector is loaded.

MCP registration does not start FFXIV, install plugins, or grant desktop
control.

## Register with the Codex CLI

In PowerShell, replace the path with the extracted release location:

```powershell
codex mcp add dab -- "C:\Tools\DAB\dab-mcp.exe"
codex mcp list
```

Restart the Codex client after adding the server. In the Codex terminal UI,
run `/mcp` to confirm `dab` is connected.

The ChatGPT desktop app, Codex CLI, and Codex IDE extension share MCP
configuration on the same Codex host. They can also add the same executable
through **Settings → MCP servers → Add server**, using the **STDIO** transport.

## Register with config.toml

Codex reads global MCP configuration from `~/.codex/config.toml`. A trusted
repository may instead use `.codex/config.toml` for project-scoped setup.

```toml
[mcp_servers.dab]
command = "C:\\Tools\\DAB\\dab-mcp.exe"
startup_timeout_sec = 20
tool_timeout_sec = 300
required = true
default_tools_approval_mode = "writes"
```

`writes` allows read-only inspection to remain quiet while asking before tools
that are not marked read-only. Use `prompt` when every DAB tool should require
confirmation.

## First useful calls

An agent should begin with:

1. `bridge_list` to discover live profiles and plugin bridges.
2. `bridge_health` to prove the exact loaded assembly identity.
3. `bridge_manifest` to read the selected plugin's versioned capabilities.
4. `bridge_snapshot`, `bridge_logs`, or `bridge_surfaces` for read-only
   inspection.

Only use `bridge_act` with a manifest-declared semantic action. Unsupported
plugins may expose read-only or reversibly presented window surfaces through
bounded discovery, but reflection never grants arbitrary method invocation,
coordinate input, or inferred mutation.

## Troubleshooting

- If Codex reports that the server exited, run `dab-mcp.exe` from PowerShell
  and inspect stderr for a missing runtime or dependency.
- If `bridge_list` is empty, verify that the in-game connector is loaded under
  the same Windows account and XIVLauncher profile.
- If a bridge is stale, restart or reload only the affected plugin and check
  `bridge_health` again.
- If startup exceeds ten seconds on a busy machine, keep
  `startup_timeout_sec = 20` or raise it deliberately.

Never put bridge tokens in `config.toml`. DAB reads current-user protected
tokens from the local plugin configuration and does not return them through
MCP.
