# Dalamud Agent Bridge handoff

Last updated: 2026-07-11

## Purpose

Dalamud Agent Bridge is a standalone private Dalamud plugin plus a loopback-only companion utility. Its intended development loop is:

1. discover a bridge-enabled plugin instance;
2. inspect its live state and plugin-owned control surface;
3. invoke explicitly registered semantic actions against the reviewed frame;
4. obtain an encrypted-at-rest visual review capture; and
5. iterate without depending on MarketMafioso or arbitrary desktop input.

This repository is deliberately independent of MarketMafioso. Reusable Dalamud-side contracts live in the sibling Franthropy repository.

## Repository layout and dependency order

- `src/DalamudAgentBridge.Plugin`: standalone Dalamud plugin and named-pipe host.
- `src/DalamudAgentBridge`: local ASP.NET utility and browser dashboard at `http://127.0.0.1:45831`.
- `tools/WgcProbe`: isolated Windows Graphics Capture proof of concept.
- sibling `../Franthropy/src/Franthropy.Dalamud`: shared bridge contracts, viewport geometry, and frame-valid semantic-control registry.

Clone Franthropy and DalamudAgentBridge beside one another. Build and land Franthropy changes before bridge changes. The plugin project accepts an explicit override when the sibling layout is unavailable:

```powershell
dotnet build .\src\DalamudAgentBridge.Plugin\DalamudAgentBridge.Plugin.csproj -p:FranthropyDalamudProject="C:\path\to\Franthropy.Dalamud.csproj"
```

## Implemented and verified

### Standalone plugin and utility

- The bridge plugin has its own DLL, manifest, configuration, command, window, discovery advertisement, and authenticated named-pipe host.
- A Debug plugin build deploys to the shared development-plugin directory by default:
  `F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\_deployed\DalamudAgentBridge`.
- The utility creates a private loopback Dalamud repository at `http://127.0.0.1:45831/repository/repo.json`.
- Dalamud auto-reloads a watched development plugin DLL; do not add a redundant reload mechanism.

### Review storage and privacy

- Captures and their metadata are separately protected at rest with Windows DPAPI for the current user.
- Plaintext PNG data is handled in memory and zeroed after storage or delivery.
- Review files expire after 30 minutes by default and can be deleted immediately.
- Image/API responses require the per-run local dashboard session and use no-store headers.
- The production path must never introduce a plaintext temporary screenshot as an intermediate artifact.

### Semantic control

- Plugins register controls that were actually rendered through Franthropy's `AgentBridgeUiReviewRegistry`.
- Invocations name a stable control ID and must include the current review-frame ID.
- Stale, expired, disabled, missing, duplicate, and replayed actions are rejected.
- Live verification toggled the standalone bridge's screenshot-handoff setting through this protocol and confirmed that replaying the same reviewed frame was rejected with HTTP 400.
- This is intentionally not arbitrary coordinate clicking or keyboard injection.

### Dalamud log observation

- `GET /api/bridges/{id}/logs` reads the `dalamud.log` belonging to the selected bridge instance's XIVLauncher profile.
- An omitted `cursor` returns a bounded recent tail. Supplying the returned `nextCursor` reads subsequent complete lines in order without one agent draining another agent's view.
- `limit` is clamped to 1-1000 entries, each read is capped at 1 MiB, incomplete trailing lines are withheld until complete, and file truncation/rotation explicitly resets an invalid cursor.
- The watcher is utility-owned, read-only, and does not require chat-window state or add filesystem access to an in-game plugin.

### Capture paths

- Plugin viewport readback exists for focused, plugin-owned capture.
- A foreground-only composited game-window capture and a two-minute queued request path exist in the utility. The queue waits until FFXIV is the foreground window instead of stealing focus.
- `tools/WgcProbe` proved that Windows Graphics Capture can capture FFXIV's main window while it is unfocused and another application remains foreground.
- Dalamud ImGui platform windows are separate native windows. Direct WGC capture of the bridge's secondary viewport handle failed with `E_INVALIDARG`.
- Pinning the bridge window to `ImGui.GetMainViewport()` made the bridge panel part of the main FFXIV window. A subsequent unfocused WGC capture of the main FFXIV handle included the complete bridge ImGui panel. This is the decisive feasibility proof.
- The utility now owns a production WGC service and authenticated review endpoint. It validates the advertised process and main-window ownership at request time, captures and encodes in memory with cancellation and a bounded timeout, feeds the buffer directly into `ReviewVault`, and zeroes it afterward.
- Live production-endpoint proof captured the primary bridge process at 1920x1080 without changing the foreground window. Its isolated vault contained only DPAPI-protected image and metadata files and no plaintext PNG.
- Probe plaintext images used during experimentation were deleted. The probe itself still accepts an output filename and therefore is not a production privacy-safe path.

## Current capture behavior

Ordinary plugin draws no longer force the bridge window onto the main viewport. The authenticated unfocused-review workflow starts a short-lived, frame-confirmed presentation transaction for the explicit bridge window, captures the main FFXIV window with WGC, stores it directly in the encrypted review vault, and completes or cancels the transaction to restore prior open/collapsed behavior.

The dashboard reports each workflow stage and displays the authenticated no-store review. The reusable transaction state machine lives in Franthropy; WGC, DPAPI storage, HTTP endpoints, and browser UI remain utility-owned.

## Security invariants

Any continuation must preserve these constraints:

1. Bind only to loopback and authenticate every bridge, image, and control request.
2. Never expose bridge access tokens through the HTTP API or dashboard.
3. Never persist plaintext screenshots, metadata, access tokens, or decryption keys.
4. Put captured bytes directly into `ReviewVault`; zero mutable plaintext buffers afterward.
5. Keep capture disabled by default and require plugin-side opt-in.
6. Use plugin-owned semantic actions with frame validation; do not add general mouse, keyboard, or coordinate injection.
7. Fail explicitly when required state is absent, stale, unfocused, or unsupported.
8. Keep capture scope and UI state visible in receipts and diagnostics.

## Build and verification

From the Franthropy sibling repository:

```powershell
dotnet test .\Franthropy.sln -c Release
```

From this repository:

```powershell
dotnet build .\DalamudAgentBridge.slnx -c Release
dotnet build .\tools\WgcProbe\WgcProbe.csproj -c Release
git diff --check
```

To deploy the watched development DLL:

```powershell
dotnet build .\src\DalamudAgentBridge.Plugin\DalamudAgentBridge.Plugin.csproj -c Debug
```

To build the private plugin package and run the utility:

```powershell
.\Run-Bridge.ps1
```

For a runtime review, confirm all of the following rather than relying on a build alone:

- the primary FFXIV instance advertises a live standalone bridge;
- `/api/bridges` reports the expected process and capabilities;
- the control surface reports a current frame and rejects a replayed invocation;
- an unfocused capture includes the ImGui panel without activating or foregrounding FFXIV;
- the review can be displayed through the authenticated endpoint;
- no plaintext PNG remains on disk after the test.

Process IDs and native window handles are runtime values. Discover them fresh; never record them as durable configuration.

## Outcome achieved

All checkpoints in [UNFOCUSED_CAPTURE_PLAN.md](UNFOCUSED_CAPTURE_PLAN.md) are complete. Live proof on the primary standalone bridge captured a legible 1920x1080 plugin-inclusive review while the foreground application remained unchanged, restored the previously closed plugin window to closed, and left only DPAPI-protected vault files with no plaintext PNG.

MarketMafioso `local-dev` now adopts the shared transaction and review registry. Live MMF
proof captured its main window at 1920x1080 while another application remained foreground,
restored the previously closed window to closed, exposed eight rendered tab controls,
accepted one frame-valid invocation, rejected its replay with HTTP 400, and left no
plaintext PNG. See MMF's `docs/agentic-ui-development.md` for the contributor workflow and
intentional capability boundary.

## Suggested starting prompt for a new task

> Continue DalamudAgentBridge from `docs/HANDOFF.md` and `docs/UNFOCUSED_CAPTURE_PLAN.md`. First verify both the sibling Franthropy repository and this repository are clean and on the expected branches. Then implement the next incomplete checkpoint without weakening the documented security invariants. Use focused tests/builds followed by proof on the primary Dalamud instance. Do not reintroduce MMF coupling or plaintext screenshot files.
