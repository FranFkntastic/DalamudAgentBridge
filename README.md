# Dalamud Agent Bridge

Local control and inspection utility for bridge-enabled Dalamud plugins.

The utility discovers named-pipe bridge advertisements under the current user's XIVLauncher plugin-config directory, then exposes a loopback-only dashboard and HTTP API. Access tokens never leave the machine and are never returned by its API.

## Run

```powershell
.\Run-Bridge.ps1
```

Open `http://127.0.0.1:45831`.

Use `-NoBuild` after the first run when no source files have changed.

Each named utility instance builds into its own output directory, so a running primary bridge cannot lock the secondary bridge's deployment. For a multibox instance:

```powershell
.\Run-Bridge.ps1 -InstanceName secondary -Port 45832 -PluginConfigRoot "$env:APPDATA\XIVLauncher-Multibox-2\pluginConfigs"
```

The normal build also creates a private, loopback-only Dalamud repository at
`http://127.0.0.1:45831/repository/repo.json`. Add that URL under Dalamud Settings → Experimental → Custom Plugin Repositories. The source repository remains private; no GitHub token is placed in Dalamud configuration.

## Safety boundary

- Binds to `127.0.0.1` only.
- Requires a per-run, HTTP-only local dashboard session for every bridge API and image request.
- Reads DPAPI-protected bridge access tokens locally; never returns them to clients.
- Enforces its own command allowlist in addition to each plugin's allowlist.
- Exposes pre-player title, lobby, character-list, queue, and error text with `RenderedAddon` provenance; `begin-login` delegates low-level lobby mechanics to Lifestream and never receives account credentials.
- Game agents, client state, packets, and plugin IPC may supply capability and diagnostics, but rendered identity/confirmation plus the final plugin snapshot remain required at consequential boundaries. A disagreement must fail closed instead of silently choosing one source.
- Plugin lifecycle actions address an exact installed-plugin internal name, refuse to manage the bridge itself, and verify the resulting Dalamud state before returning.
- Local-build replacement validates the source manifest and optional DLL hash, backs up the installed files, and rolls back if replacement or reload fails.
- MMF currently exposes state/window/tab control, proof capture, input diagnostics, and route stop. It does not expose route start or purchase commands.

The in-game connector also supports `get-login-ui` and `begin-login` on its authenticated named pipe. Use `tools/Invoke-InGameBridge.ps1`; a login target is written as `Character Name@Home World`, and success means only that Lifestream accepted the work. The caller must still prove the rendered character selection and the eventual logged-in character/world/build.

`send-chat` accepts one slash command and never accepts plain chat text. DAB gives
registered Dalamud plugin commands first refusal, then forwards an unhandled slash
command to the game's native command shell; its receipt identifies the `plugin` or
`native` route. `get-chat-log` can then confirm local command output such as `/echo`
without screen automation. Before native fallback, DAB switches ambient chat to the
user-reserved Cross-world Linkshell 2 sink. Explicit chat commands and every alias
derived from all four localized current `TextCommand` sheets are rejected, as are
every command referenced by the current `Emote` data; local-only `/echo` remains
available.

## Screenshot privacy

Screenshot capture is disabled by default in MMF and must be explicitly enabled in that plugin's local configuration. A capture fails closed unless MMF itself is currently rendered, and only its current window rectangle is captured—not the full game viewport.

The plugin encodes the crop in memory, protects the short handoff with Windows DPAPI for the current user, and never writes plaintext PNGs or sidecar metadata. The utility verifies the handoff, immediately deletes it, then stores both the image and its review metadata as separate DPAPI-encrypted files under the current user's local application-data directory. By default a review capture expires after 30 minutes; the authenticated local dashboard decrypts it only in memory for browser delivery. It can be cleared immediately from the dashboard. Browser delivery uses no-store headers and a Blob URL that is revoked when replaced or closed.

This reduces accidental persistence and unauthorised local web access; it does not protect against a compromised Windows user account, a malicious local process, browser extensions with local access, or downstream systems that may retain pixels after display.

## Frame-validated control

Plugins may expose their own rendered ImGui controls through the shared `AgentBridgeUiReviewRegistry`. Each control has a stable ID, visible bounds, value and enabled state, and is actionable only through its named semantic action. The utility must supply the current review-frame ID; the registry rejects stale, disabled, missing, or replayed controls. This is deliberately not arbitrary coordinate clicking or keyboard injection.

Agents that already know a stable control ID should use `GET /api/bridges/{bridgeId}/controls/{controlId}`. It returns only that control with the current reviewed frame ID and expiry, so the same small response supports guarded invocation and status polling without serializing the plugin's complete control surface.

`POST /api/bridges/{bridgeId}/control-presentations` opens an advertised surface and returns up to sixteen requested controls from one current frame. `POST /api/bridges/{bridgeId}/control-actions` presents one control and invokes that exact reviewed frame in one request; it retains the same enabled-state, expiry, and replay checks as the two-request path.

## Plugin lifecycle

The standalone connector manages installed plugins without automating the Plugin Installer window:

- `GET /api/bridges/{bridgeId}/plugins`
- `POST /api/bridges/{bridgeId}/plugins/{internalName}/enable`
- `POST /api/bridges/{bridgeId}/plugins/{internalName}/disable`
- `POST /api/bridges/{bridgeId}/plugins/{internalName}/local-build`

The local-build request accepts `sourceDirectory`, optional `expectedCurrentVersion` and `expectedMainDllSha256` guards, `enableAfterReplacement`, and `preserveInstalledManifest` (default `true`). Preserving the installed manifest keeps a hotfix build attached to the package version Dalamud already resolved. It replaces an already installed plugin only; installing a previously unknown repository plugin remains outside this control surface.

Dev-plugin bootstrap is a separate, narrower surface: `POST /api/bridges/{bridgeId}/plugins/{internalName}/install-dev` (or `install-dev-plugin` on the named pipe) registers a plugin that already exists on disk under the target profile's own `devPlugins` directory. The caller supplies an internal name only; the bridge resolves the assembly beneath that root, verifies the manifest matches the requested name, registers Dalamud's supported watched-location entry, scans, loads, and verifies the resulting state. Arbitrary paths and repository installs remain outside this surface.

For repeated development, `tools\Test-Bridge.ps1` reuses a successful test result only while the source and test-assembly hashes remain unchanged. `tools\Restart-BridgeUtility.ps1` builds through a staging directory, safely restarts a named loopback utility, verifies its DLL hash and listening port, and keeps the complete build and runtime logs under ignored `artifacts` storage.

## Protocol convention

Bridge-enabled plugins publish `agent-bridge/discovery-<pid>.json` beneath their plugin config directory and store `AgentBridgeAccessToken` in the adjacent plugin configuration JSON. Each named-pipe request and response is one JSON line.

## Development handoff

See [docs/HANDOFF.md](docs/HANDOFF.md) for the current verified runtime state, repository boundaries, and continuation instructions. The remaining unfocused-capture work is checkpointed in [docs/UNFOCUSED_CAPTURE_PLAN.md](docs/UNFOCUSED_CAPTURE_PLAN.md).
