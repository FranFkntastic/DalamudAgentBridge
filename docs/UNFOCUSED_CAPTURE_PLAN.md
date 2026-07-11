# Unfocused capture implementation plan

Last updated: 2026-07-11

## Outcome

Allow the local utility to request and review a plugin-inclusive FFXIV capture while the user continues working in another application. The operation must not activate FFXIV, inject desktop input, or persist plaintext pixels.

## Evidence already established

- Foreground GDI/compositor capture can include the game and ImGui layer but requires FFXIV to be foreground.
- Queueing that capture avoids focus theft but cannot complete until the user naturally focuses FFXIV.
- WGC captures the main FFXIV window while it is unfocused.
- The bridge ImGui window normally occupies a separate Dalamud viewport/native window, so capturing only the main FFXIV window omits it.
- Direct WGC creation for that secondary viewport failed with `E_INVALIDARG`.
- Forcing the bridge window onto the ImGui main viewport caused an unfocused WGC capture of the main FFXIV window to include the complete bridge panel.

The remaining work is integration and transaction design, not another search for basic feasibility.

## Checkpoint 1: production WGC engine (completed 2026-07-11)

- Extract the useful WGC mechanics from `tools/WgcProbe` into a utility-owned service.
- Resolve the target from the advertised process ID and validate the native main-window handle at request time.
- Return encoded bytes and dimensions in memory; do not accept an output filename.
- Add cancellation, bounded timeout, frame-size validation, disposal, and useful typed failures.
- Feed the returned buffer directly to `ReviewVault`, then zero it.
- Keep the probe isolated until the production service has equivalent diagnostic value.

Review gate:

- unit-test target validation and failure mapping where practical;
- Release-build the utility and probe;
- prove no plaintext capture file is created by the production endpoint.

Implemented in `WindowsGraphicsCaptureService` and the authenticated
`POST /api/bridges/{id}/wgc-captures` endpoint. The service resolves and verifies the
advertised process main window at request time, applies an eight-second bounded timeout,
validates target and frame dimensions, maps failures to typed codes, returns PNG bytes only
in memory, and zeroes buffers after vault storage or an incomplete read.

Live proof against the primary standalone bridge captured a 1920x1080 frame while the
foreground window handle remained unchanged. The isolated review vault contained only the
DPAPI-protected image and metadata files and no plaintext PNG.

## Checkpoint 2: capture transaction protocol (completed 2026-07-11)

- Add a narrowly named bridge command that requests a temporary main-viewport capture presentation.
- On the Dalamud framework thread, open/uncollapse the target plugin window and render it into the main viewport.
- Return an explicit ready frame/transaction identifier only after the registered UI and capture region were rendered there.
- Do not use arbitrary window focus, desktop input, or sleeps as synchronization.
- Give the transaction a short expiration and an explicit completion/cancel path.
- Preserve enough prior state to restore ordinary window placement and open/collapsed behavior after success, failure, timeout, or cancellation.

Review gate:

- stale or mismatched transaction identifiers fail closed;
- cleanup runs on every terminal path;
- the user's foreground application does not change;
- repeated requests cannot leave the plugin window permanently pinned.

Implemented as authenticated `begin-capture-presentation`,
`complete-capture-presentation`, and `cancel-capture-presentation` commands for the
explicit `bridge.main-window` target. Readiness is tied to a completed registered review
frame. The short-lived transaction preserves and restores open/collapsed state, rejects
stale identifiers, and removes the former permanent viewport pin.

## Checkpoint 3: orchestrated unfocused review capture (completed 2026-07-11)

- Have the utility begin a capture transaction through the authenticated named pipe.
- Wait for the plugin's ready frame, then capture the main FFXIV window with the production WGC engine.
- Store receipt and image in `ReviewVault`, including capture method, process ID, dimensions, timestamp, target plugin, and transaction/frame identifier.
- Complete the transaction and restore plugin UI state.
- Surface explicit progress and failure state in the dashboard.

Review gate:

- capture succeeds while Codex or another application remains foreground;
- the requested plugin panel is legible and correctly scaled in full-window context;
- authentication, no-store delivery, DPAPI-at-rest storage, retention, deletion, and buffer zeroing remain intact;
- no plaintext PNG exists after the run.

Implemented as a queued authenticated workflow with explicit `preparing`, `capturing`,
`storing`, `restoring`, `completed`, and `failed` dashboard states. Receipts include WGC
method, process, dimensions, target plugin, transaction identifier, and reviewed frame.
Failure deletes any newly stored review and cancels the presentation; plugin-side expiry is
the cleanup backstop if the pipe becomes unavailable.

## Checkpoint 4: reusable plugin adoption (completed 2026-07-11)

- Move only generic transaction contracts and framework-thread UI-review machinery into Franthropy.
- Keep WGC, DPAPI review storage, HTTP endpoints, and browser UI in DalamudAgentBridge.
- Provide a small adapter pattern for bridge-enabled plugins to expose named windows and semantic controls.
- Adopt it in MarketMafioso only after the standalone bridge loop is proven.

Review gate:

- no MarketMafioso reference exists in the standalone plugin or Franthropy;
- plugin adapters expose only explicit, reviewed capabilities;
- archived real diagnostic runs remain the evidence basis for MMF-specific automation tests.

Generic transaction contracts and the frame/expiry/restoration coordinator now live in
Franthropy. The standalone plugin is the first explicit adapter. WGC, DPAPI storage, HTTP,
and dashboard code remain utility-local, and MarketMafioso has not been coupled into either
the standalone plugin or Franthropy.

After the standalone proof passed, MarketMafioso `local-dev` adopted the same shared
coordinator and frame-valid registry through its own explicit adapter. MMF exposes only its
named main window and rendered tab selections; route start, purchases, credentials, unlock
keys, arbitrary input, and coordinate actions remain outside the adapter.

## Outcome verification

Live end-to-end proof on the primary standalone bridge captured a 1920x1080
plugin-inclusive FFXIV frame while another application remained foreground. The complete
Agent Bridge panel was legible in the authenticated dashboard review. The plugin window was
closed before and after the transaction, the vault contained only DPAPI-protected image and
metadata files, and no plaintext PNG was created.

## Deferred capabilities

- General desktop mouse/keyboard control.
- Hidden or synthetic rendering of arbitrary third-party plugin windows.
- Remote access or non-loopback operation.
- Continuous recording or background screenshot polling.
- Automatic MMF route start or purchase actions.

These require separate threat and interaction designs and are not implied by this plan.
