# Dalamud Agent Bridge

Local control and inspection utility for bridge-enabled Dalamud plugins.

The utility discovers named-pipe bridge advertisements under the current user's XIVLauncher plugin-config directory, then exposes a loopback-only dashboard and HTTP API. Access tokens never leave the machine and are never returned by its API.

## Run

```powershell
.\Run-Bridge.ps1
```

Open `http://127.0.0.1:45831`.

Use `-NoBuild` after the first run when no source files have changed.

The normal build also creates a private, loopback-only Dalamud repository at
`http://127.0.0.1:45831/repository/repo.json`. Add that URL under Dalamud Settings → Experimental → Custom Plugin Repositories. The source repository remains private; no GitHub token is placed in Dalamud configuration.

## Safety boundary

- Binds to `127.0.0.1` only.
- Requires a per-run, HTTP-only local dashboard session for every bridge API and image request.
- Reads DPAPI-protected bridge access tokens locally; never returns them to clients.
- Enforces its own command allowlist in addition to each plugin's allowlist.
- MMF currently exposes state/window/tab control, proof capture, input diagnostics, and route stop. It does not expose route start or purchase commands.

## Screenshot privacy

Screenshot capture is disabled by default in MMF and must be explicitly enabled in that plugin's local configuration. A capture fails closed unless MMF itself is currently rendered, and only its current window rectangle is captured—not the full game viewport.

The plugin encodes the crop in memory, protects the short handoff with Windows DPAPI for the current user, and never writes plaintext PNGs or sidecar metadata. The utility verifies and imports the image, immediately deletes the protected handoff file, and keeps the raw PNG only in memory for one single-use delivery or 45 seconds. Browser delivery uses no-store headers and a Blob URL that is revoked when replaced or closed.

This reduces accidental persistence and unauthorised local web access; it does not protect against a compromised Windows user account, a malicious local process, browser extensions with local access, or downstream systems that may retain pixels after display.

## Protocol convention

Bridge-enabled plugins publish `agent-bridge/discovery-<pid>.json` beneath their plugin config directory and store `AgentBridgeAccessToken` in the adjacent plugin configuration JSON. Each named-pipe request and response is one JSON line.
