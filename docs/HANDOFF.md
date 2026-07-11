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

### Capture paths

- Plugin viewport readback exists for focused, plugin-owned capture.
- A foreground-only composited game-window capture and a two-minute queued request path exist in the utility. The queue waits until FFXIV is the foreground window instead of stealing focus.
- `tools/WgcProbe` proved that Windows Graphics Capture can capture FFXIV's main window while it is unfocused and another application remains foreground.
- Dalamud ImGui platform windows are separate native windows. Direct WGC capture of the bridge's secondary viewport handle failed with `E_INVALIDARG`.
- Pinning the bridge window to `ImGui.GetMainViewport()` made the bridge panel part of the main FFXIV window. A subsequent unfocused WGC capture of the main FFXIV handle included the complete bridge ImGui panel. This is the decisive feasibility proof.
- Probe plaintext images used during experimentation were deleted. The probe itself still accepts an output filename and therefore is not a production privacy-safe path.

## Current experimental behavior

`Plugin.DrawCore` currently forces the bridge window onto the main viewport and positions it at the main viewport work area's upper-left corner. This was added to prove background capture and is not yet a polished capture transaction. It may interfere with normal user positioning and should not silently become the permanent window policy.

The utility's production capture endpoint still uses the foreground compositor path. WGC has not yet been integrated into the encrypted review vault.

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

## Next work

Follow [UNFOCUSED_CAPTURE_PLAN.md](UNFOCUSED_CAPTURE_PLAN.md). The immediate task is to turn the WGC proof into a production capture engine that sends pixels directly into `ReviewVault`, then replace the permanent viewport pin with a bounded capture transaction that restores the user's prior window behavior.

## Suggested starting prompt for a new task

> Continue DalamudAgentBridge from `docs/HANDOFF.md` and `docs/UNFOCUSED_CAPTURE_PLAN.md`. First verify both the sibling Franthropy repository and this repository are clean and on the expected branches. Then implement the next incomplete checkpoint without weakening the documented security invariants. Use focused tests/builds followed by proof on the primary Dalamud instance. Do not reintroduce MMF coupling or plaintext screenshot files.
