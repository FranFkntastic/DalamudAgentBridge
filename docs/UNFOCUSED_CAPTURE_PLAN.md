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

## Checkpoint 1: production WGC engine

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

## Checkpoint 2: capture transaction protocol

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

## Checkpoint 3: orchestrated unfocused review capture

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

## Checkpoint 4: reusable plugin adoption

- Move only generic transaction contracts and framework-thread UI-review machinery into Franthropy.
- Keep WGC, DPAPI review storage, HTTP endpoints, and browser UI in DalamudAgentBridge.
- Provide a small adapter pattern for bridge-enabled plugins to expose named windows and semantic controls.
- Adopt it in MarketMafioso only after the standalone bridge loop is proven.

Review gate:

- no MarketMafioso reference exists in the standalone plugin or Franthropy;
- plugin adapters expose only explicit, reviewed capabilities;
- archived real diagnostic runs remain the evidence basis for MMF-specific automation tests.

## Deferred capabilities

- General desktop mouse/keyboard control.
- Hidden or synthetic rendering of arbitrary third-party plugin windows.
- Remote access or non-loopback operation.
- Continuous recording or background screenshot polling.
- Automatic MMF route start or purchase actions.

These require separate threat and interaction designs and are not implied by this plan.
