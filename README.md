# Dalamud Agent Bridge

Local control and inspection utility for bridge-enabled Dalamud plugins, with
HTTP, command-line, and MCP surfaces designed for development agents.

The utility discovers named-pipe bridge advertisements under the current user's XIVLauncher plugin-config directory, then exposes a loopback-only dashboard and HTTP API. Access tokens never leave the machine and are never returned by its API.

## Contents

- [Prerequisites](#prerequisites)
- [Install](#install)
- [Run](#run)
- [Safety boundary](#safety-boundary)
- [Situation and navigation](#situation-and-navigation)
- [Specialist cockpit](#specialist-cockpit)
- [Screenshot privacy](#screenshot-privacy)
- [Frame-validated control](#frame-validated-control)
- [Plugin lifecycle](#plugin-lifecycle)
- [Connect an agent](#connect-an-agent)
- [Releases](#releases)
- [Protocol convention](#protocol-convention)
- [Contributing](#contributing)

## Prerequisites

- Windows 10 version 1809 or newer.
- The .NET 8 SDK for the utility, CLI, and MCP server.
- The .NET 10 SDK is additionally required for connector development.
- A development Dalamud installation and sibling checkout of
  [Franthropy](https://github.com/FranFkntastic/Franthropy) are needed only
  when building the in-game connector from source.

Connector development expects this layout:

```text
FFXIV-Development/
  DalamudAgentBridge/
  Franthropy/
```

Set `FranthropyDalamudProject` at build time if the repositories live elsewhere.
The utility, CLI, MCP server, and their tests consume the published
`Franthropy.AgentBridge` package and build from a standalone DAB checkout.

## Install

Add the FranFkntastic custom repository URL under Dalamud Settings →
Experimental → Custom Plugin Repositories:

```text
https://raw.githubusercontent.com/FranFkntastic/DalamudPlugins/main/pluginmaster.json
```

Install **Dalamud Agent Bridge** from the Plugin Installer, then use `/dab` or
the plugin's configuration button to inspect the connector and configure
optional screenshot handoff. Screenshot access remains disabled until enabled
there; agent navigation and specialist automation are separate permissions and
also default to disabled.

Download the matching Windows utility or MCP bundle from
[GitHub Releases](https://github.com/FranFkntastic/DalamudAgentBridge/releases).
The in-game connector discovers the local utility through its authenticated
current-user bridge; no port, token, or consumer-plugin configuration is required.

## Run

```powershell
.\Run-Bridge.ps1
```

Open `http://127.0.0.1:45831`.

Use `-NoBuild` after the first run when no source files have changed.

Each named utility instance builds into its own output directory, so a running
primary bridge cannot lock another instance's deployment. For an additional
XIVLauncher profile:

```powershell
.\Run-Bridge.ps1 -InstanceName secondary -Port 45832 -PluginConfigRoot "C:\path\to\profile\pluginConfigs"
```

Pass `-BuildPluginRepository` when developing the connector to also create a
private, loopback-only Dalamud repository at
`http://127.0.0.1:45831/repository/repo.json`. Add that URL under Dalamud
Settings → Experimental → Custom Plugin Repositories. The repository contains
your locally built plugin package and does not require a GitHub token.

## Safety boundary

- Binds to `127.0.0.1` only.
- Requires a per-run, HTTP-only local dashboard session for every bridge API and image request.
- Reads DPAPI-protected bridge access tokens locally; never returns them to clients.
- Enforces its own command allowlist in addition to each plugin's allowlist.
- Exposes pre-player title, lobby, character-list, queue, and error text with `RenderedAddon` provenance; `begin-login` delegates low-level lobby mechanics to Lifestream and never receives account credentials.
- Game agents, client state, packets, and plugin IPC may supply capability and diagnostics, but rendered identity/confirmation plus the final plugin snapshot remain required at consequential boundaries. A disagreement must fail closed instead of silently choosing one source.
- Plugin lifecycle actions address an exact installed-plugin internal name, refuse to manage the bridge itself, and verify the resulting Dalamud state before returning.
- Local-build replacement validates the source manifest and optional DLL hash, backs up the installed files, and rolls back if replacement or reload fails.
- Unsupported plugins expose only read-only inventory and reversible window
  presentation. Mutating actions remain limited to explicitly declared,
  frame-reviewed semantic controls.

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

## Situation and navigation

`bridge_situation` gives an agent one bounded, timestamped view of the current
character: territory and map, world and map coordinates, resources and casting,
active condition flags, target and focus target, party, the nearest 48 objects
within 100 yalms, visible decision-oriented game UI, the newest 20 chat lines,
and DAB-owned navigation progress. It uses public Dalamud observations and
rendered addon text; raw addresses and arbitrary reflected game memory are not
part of the schema.

`bridge_navigate` accepts an exact current territory ID and finite world-space
X/Y/Z coordinates, then delegates pathing to vnavmesh. DAB owns at most one
request at a time, records its starting and best distance, deadline and last
progress time, refuses unsafe client states or territory changes, and exposes
status and guarded cancellation through `bridge_navigation` and
`bridge_navigation_cancel`. The permission can only be enabled by the user in
the in-game `/dab` window; an agent cannot turn it on through a reviewed action.

## Specialist cockpit

`bridge_specialists` discovers DAB's reviewed adapters for installed
Questionable, AutoDuty, Henchman, and Lifestream versions. It reports each
plugin's availability, compatibility and busy state together with typed
capabilities, argument constraints, risk labels, and the current DAB-owned
operation. Discovery and observation are read-only even when specialist
automation is disabled.

`bridge_specialist_start` accepts only a capability ID returned by that catalog
and parameters declared by its schema. DAB does not expose arbitrary IPC names,
reflection-based invocation, slash-command fallback, or plugin configuration
mutation through this surface. The initial reviewed capabilities are one
explicit Questionable quest, an explicit AutoDuty territory path, Henchman's
published On A Boat and On Your Mark tasks, and typed Lifestream aethernet or
world travel.

Specialist automation has its own default-off permission in `/dab`. Every
accepted request receives an operation ID, deadline, latest plugin observation,
and terminal state. `bridge_specialist_cancel` can guard cancellation with that
ID. Navigation and specialist operations share one DAB gameplay-control lease,
so two agent-issued controllers are refused rather than allowed to fight;
specialist work started outside DAB is reported as externally busy and is never
stolen. Individual plugins still execute according to their existing user
configuration and may perform consequential gameplay such as duties, quests,
teleports, or world travel.

## Screenshot privacy

`bridge_capture_clip` reuses the screenshot permission and encrypted handoff to
sample 2-12 ordered full-viewport frames over at most 60 seconds. Every frame is
paired with the contemporaneous situation snapshot, which lets an agent
correlate visible geometry or loading failures with position, conditions, and
vnavmesh progress. The result is a bounded diagnostic clip, not an indefinite
stream or desktop capture.

Screenshot capture is disabled by default and must be explicitly enabled from
the connector's in-game `/dab` window. Plugin-surface capture requires a
short-lived presentation transaction and captures only the presented plugin
window—not the full desktop.

The plugin encodes the crop in memory, protects the short handoff with Windows DPAPI for the current user, and never writes plaintext PNGs or sidecar metadata. The utility verifies the handoff, immediately deletes it, then stores both the image and its review metadata as separate DPAPI-encrypted files under the current user's local application-data directory. By default a review capture expires after 30 minutes; the authenticated local dashboard decrypts it only in memory for browser delivery. It can be cleared immediately from the dashboard. Browser delivery uses no-store headers and a Blob URL that is revoked when replaced or closed.

This reduces accidental persistence and unauthorised local web access; it does not protect against a compromised Windows user account, a malicious local process, browser extensions with local access, or downstream systems that may retain pixels after display.

## Frame-validated control

Plugins may expose their own rendered ImGui controls through the shared `AgentBridgeUiReviewRegistry`. Each control has a stable ID, visible bounds, value and enabled state, and is actionable only through its named semantic action. The utility must supply the current review-frame ID; the registry rejects stale, disabled, missing, or replayed controls. This is deliberately not arbitrary coordinate clicking or keyboard injection.

For visual testing of unsupported third-party plugins, the user can separately enable bounded surface input in `/dab`. `bridge_surface_interact_capture` then presents one discovered `IWindow` under a runtime-bound lease, maps normalized pointer coordinates only into that window, delivers a short move/click/scroll/drag/text/navigation-key sequence through ImGui's own input queue, captures the settled window, and restores its prior state. It never emits Win32 input, activates the desktop, or targets native FFXIV UI; stale leases, runtime changes, competing sequences, invalid coordinates, and oversized input are refused.

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

## Connect an agent

The `dab-mcp` executable exposes DAB's bridge, manifest, snapshot, log,
capture, deployment, and reviewed-action tools over MCP stdio. Follow the
[MCP setup guide](docs/mcp-setup.md) for copy-paste Codex CLI and
`config.toml` registration, verification, and approval defaults.

## Releases

Tagged releases provide Windows x64 utility and MCP bundles. Building the
in-game connector still requires a local Dalamud development installation;
maintainers can produce the complete release with
`tools/Build-Release.ps1`. See [docs/releases.md](docs/releases.md) for the
artifact contract and release procedure.

## Protocol convention

Bridge-enabled plugins publish `agent-bridge/discovery-<pid>.json` beneath their plugin config directory and store `AgentBridgeAccessToken` in the adjacent plugin configuration JSON. Each named-pipe request and response is one JSON line.

## Contributing

Pull requests are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), target
`main`, and keep live-client testing separate from
source-only verification. Security-sensitive reports belong in GitHub's private
vulnerability reporting flow described in [SECURITY.md](SECURITY.md).

DAB is licensed under the [GNU General Public License v3.0](LICENSE).
