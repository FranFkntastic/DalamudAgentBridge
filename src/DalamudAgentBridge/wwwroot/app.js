const elements = {
  bridgeSelect: document.querySelector('#bridgeSelect'),
  connectionState: document.querySelector('#connectionState'),
  identity: document.querySelector('#identity'),
  routeState: document.querySelector('#routeState'),
  manifestActions: document.querySelector('#manifestActions'),
  controlSurface: document.querySelector('#controlSurface'),
  activityLog: document.querySelector('#activityLog'),
  captureImage: document.querySelector('#captureImage'),
  captureMeta: document.querySelector('#captureMeta'),
  captureSurfaceSelect: document.querySelector('#captureSurfaceSelect'),
  pluginSurfaces: document.querySelector('#pluginSurfaces'),
};

let bridges = [];
let activeBridgeId = '';
let activeManifest = null;
let activeReviewId = '';
let captureObjectUrl = '';
const escapeHtml = value => String(value ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#039;');

function log(message, value) {
  const detail = value ? `\n${JSON.stringify(value, null, 2)}` : '';
  elements.activityLog.textContent = `[${new Date().toLocaleTimeString()}] ${message}${detail}\n\n${elements.activityLog.textContent}`;
}

async function readJson(response) {
  const body = await response.json();
  if (!response.ok) throw new Error(body.detail ?? body.message ?? `Bridge request failed (${response.status})`);
  return body;
}

async function discover() {
  bridges = await readJson(await fetch('/api/bridges', { cache: 'no-store' }));
  elements.bridgeSelect.innerHTML = bridges.map(bridge =>
    `<option value="${escapeHtml(bridge.id)}">${escapeHtml(bridge.pluginInternalName)} · ${escapeHtml(bridge.profileAlias ?? bridge.profileId)} · PID ${escapeHtml(bridge.processId)}</option>`).join('');
  if (!bridges.some(bridge => bridge.id === activeBridgeId)) activeBridgeId = bridges[0]?.id ?? '';
  elements.bridgeSelect.value = activeBridgeId;
  elements.connectionState.textContent = activeBridgeId ? 'Authenticated' : 'No bridge found';
  elements.connectionState.classList.toggle('online', Boolean(activeBridgeId));
  if (!activeBridgeId) return;
  await Promise.all([refreshManifest(), refreshSnapshot(), refreshReviewSurfaces(), refreshCaptureSurfaces(), refreshControls(), refreshPluginSurfaces()]);
}

async function refreshPluginSurfaces() {
  const bridge = bridges.find(value => value.id === activeBridgeId);
  if (!bridge) return;
  try {
    const profile = bridge.profileAlias ?? bridge.profileId ?? 'primary';
    const catalog = await readJson(await fetch(`/api/plugin-surfaces?profile=${encodeURIComponent(profile)}&processId=${encodeURIComponent(bridge.processId)}`, { cache: 'no-store' }));
    const plugins = (catalog.plugins ?? []).filter(plugin => plugin.surfaces?.length);
    if (!plugins.length) {
      elements.pluginSurfaces.innerHTML = '<span class="subtitle">No public or safely discoverable plugin UI surfaces are currently available.</span>';
      return;
    }
    elements.pluginSurfaces.innerHTML = plugins.map(plugin => {
      const surfaces = plugin.surfaces.map(surface =>
        `<span><small title="${escapeHtml(surface.id)}">${escapeHtml(surface.label)} · ${escapeHtml(surfaceProvenance(surface.provenance))} · ${surface.isOpen === true ? 'open' : surface.isOpen === false ? 'closed' : 'entry point'} · ${Number(surface.authority) === 1 ? 'reversible presentation' : 'read only'}</small>${Number(surface.authority) === 1 ? `<button data-surface-capture="${escapeHtml(surface.id)}" data-surface-plugin="${escapeHtml(plugin.internalName)}" class="secondary">Capture</button>` : ''}</span>`).join('');
      return `<div class="action-row"><div><strong>${escapeHtml(plugin.name)}</strong><small>${escapeHtml(plugin.internalName)} · ${escapeHtml(plugin.version)} · ${plugin.isLoaded ? 'loaded' : 'unloaded'}</small>${surfaces}</div></div>`;
    }).join('');
    elements.pluginSurfaces.querySelectorAll('[data-surface-capture]').forEach(button =>
      button.addEventListener('click', () => captureDiscoveredSurface(button.dataset.surfacePlugin, button.dataset.surfaceCapture, button)));
  } catch (error) {
    elements.pluginSurfaces.innerHTML = `<span class="subtitle">${escapeHtml(error.message)}</span>`;
  }
}

async function captureDiscoveredSurface(plugin, surfaceId, button) {
  const bridge = bridges.find(value => value.id === activeBridgeId);
  if (!bridge) return;
  button.disabled = true;
  try {
    const profile = bridge.profileAlias ?? bridge.profileId ?? 'primary';
    const response = await fetch(`/api/plugin-surfaces/${encodeURIComponent(surfaceId)}/captures?plugin=${encodeURIComponent(plugin)}&profile=${encodeURIComponent(profile)}&processId=${encodeURIComponent(bridge.processId)}`, { method: 'POST' });
    const body = await readJson(response);
    activeReviewId = body.receipt?.capture?.review?.id ?? '';
    await displayReview(body.imageUrl);
    const capture = body.receipt.capture.receipt;
    elements.captureMeta.textContent = `${plugin} · ${capture.width}×${capture.height} · presented, captured, restored`;
    log(body.message, body.receipt);
    await refreshPluginSurfaces();
  } catch (error) {
    elements.captureMeta.textContent = error.message;
    log(error.message);
  } finally {
    button.disabled = false;
  }
}

function surfaceProvenance(value) {
  return ['plugin declared', 'reviewed control', 'Dalamud public API', 'reflected window system'][Number(value)] ?? value;
}

async function refreshManifest() {
  if (!activeBridgeId) return;
  try {
    const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/manifest`, { cache: 'no-store' }));
    activeManifest = body.receipt;
  } catch (error) {
    activeManifest = null;
    elements.manifestActions.innerHTML = `<span class="subtitle">${escapeHtml(error.message)}</span>`;
    return;
  }
  const runtime = activeManifest.runtime;
  elements.identity.innerHTML = [
    ['Plugin', runtime.pluginInternalName], ['Version', runtime.assemblyVersion],
    ['Build', runtime.buildConfiguration], ['Process', runtime.processId],
    ['Profile', activeManifest.profileAlias], ['Protocol', activeManifest.protocolVersion],
    ['Runtime', runtime.runtimeInstanceId], ['DLL SHA-256', runtime.mainDllSha256],
  ].map(([label, value]) => `<div class="metric"><small>${escapeHtml(label)}</small><strong title="${escapeHtml(value)}">${escapeHtml(value)}</strong></div>`).join('');
  renderManifestActions(activeManifest.actions ?? []);
}

function renderManifestActions(actions) {
  if (!actions.length) {
    elements.manifestActions.innerHTML = '<span class="subtitle">This plugin advertises inspection surfaces, but no stable semantic action catalog.</span>';
    return;
  }
  elements.manifestActions.innerHTML = actions.map(action => {
    const properties = action.arguments?.properties ?? [];
    const inputs = properties.map(argument => {
      const key = escapeHtml(argument.name);
      if (Number(argument.kind) === 3 && Array.isArray(argument.allowedValues))
        return `<label>${key}<select data-action-argument="${key}">${argument.allowedValues.map(value => `<option>${escapeHtml(value)}</option>`).join('')}</select></label>`;
      const type = Number(argument.kind) === 1 ? 'number' : 'text';
      const placeholder = Number(argument.kind) === 4 ? 'Item name' : argument.required ? 'Required' : 'Optional';
      return `<label>${key}<input data-action-argument="${key}" type="${type}" placeholder="${escapeHtml(placeholder)}"></label>`;
    }).join('');
    return `<div class="action-row"><div><strong>${escapeHtml(action.label)}</strong><small>${escapeHtml(action.surfaceId)} · ${action.mutating ? 'changes state' : 'read only'}${action.completionOperationKind ? ` · ${escapeHtml(action.completionOperationKind)}` : ''}</small></div><div class="action-arguments">${inputs}</div><button data-manifest-action="${escapeHtml(action.id)}">Run</button></div>`;
  }).join('');
  elements.manifestActions.querySelectorAll('[data-manifest-action]').forEach(button =>
    button.addEventListener('click', () => invokeManifestAction(button.dataset.manifestAction, button)));
}

async function invokeManifestAction(actionId, button) {
  const bridge = bridges.find(value => value.id === activeBridgeId);
  const action = activeManifest?.actions?.find(value => value.id === actionId);
  if (!bridge || !action) return;
  const argumentsObject = {};
  button.closest('.action-row').querySelectorAll('[data-action-argument]').forEach(input => {
    if (input.value !== '') argumentsObject[input.dataset.actionArgument] = input.type === 'number' ? Number(input.value) : input.value;
  });
  button.disabled = true;
  try {
    const body = await readJson(await fetch(`/api/targets/${encodeURIComponent(bridge.profileAlias ?? bridge.profileId)}/${encodeURIComponent(bridge.pluginInternalName)}/actions`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ surfaceId: action.surfaceId, controlId: action.id, arguments: argumentsObject, waitForCompletion: true }),
    }));
    log(`${action.id} completed`, body);
    await Promise.all([refreshSnapshot(), refreshControls()]);
  } catch (error) { log(error.message); }
  finally { button.disabled = false; }
}

async function refreshSnapshot() {
  if (!activeBridgeId) return;
  const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/snapshot`, { cache: 'no-store' }));
  const truth = body.receipt?.truth ?? body.receipt;
  const capabilities = activeManifest?.capabilities?.map(value => `${value.id} v${value.version}`).join(' · ') ?? 'legacy snapshot';
  const status = truth.refreshActive ? 'Refresh running' : truth.transferActive ? 'Transfer running' : truth.route?.state ?? 'Ready';
  elements.routeState.innerHTML = `<strong>${escapeHtml(status)}</strong><br><span class="subtitle">${escapeHtml(capabilities)}</span><pre class="snapshot-json">${escapeHtml(JSON.stringify(truth, null, 2))}</pre>`;
}

async function refreshReviewSurfaces() {
  const container = document.querySelector('#tabButtons');
  if (!activeBridgeId) { container.innerHTML = ''; return; }
  try {
    const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/review-surfaces`, { cache: 'no-store' }));
    const surfaces = [...(body.receipt ?? [])].sort((left, right) => Number(left.order) - Number(right.order));
    container.innerHTML = surfaces.map(surface => `<button data-review-command="${escapeHtml(surface.command)}" data-review-target="${escapeHtml(surface.target)}">${escapeHtml(surface.label)}</button>`).join('');
    container.querySelectorAll('[data-review-command]').forEach(button =>
      button.addEventListener('click', () => openReviewSurface(button.dataset.reviewCommand, button.dataset.reviewTarget, button)));
  } catch (error) { container.innerHTML = `<span class="subtitle">${escapeHtml(error.message)}</span>`; }
}

async function openReviewSurface(command, target, button) {
  button.disabled = true;
  try {
    const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/commands/${encodeURIComponent(command)}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ target }),
    }));
    log(body.message, body.receipt);
    await Promise.all([refreshControls(), refreshSnapshot()]);
  } catch (error) { log(error.message); }
  finally { button.disabled = false; }
}

async function refreshControls() {
  if (!activeBridgeId) return;
  try {
    const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/controls`, { cache: 'no-store' }));
    const surface = body.receipt;
    const controls = surface?.controls ?? [];
    if (!controls.length) {
      elements.controlSurface.className = 'privacy-note';
      elements.controlSurface.textContent = 'No actionable controls are currently rendered. Open a declared review surface, then refresh.';
      return;
    }
    elements.controlSurface.className = 'control-surface';
    elements.controlSurface.innerHTML = controls.map(control => `<button data-review-control="${escapeHtml(control.id)}" data-frame-id="${escapeHtml(surface.frameId)}" ${control.enabled ? '' : 'disabled'}>${escapeHtml(control.label)}<small>${escapeHtml(controlKind(control.kind))} · ${escapeHtml(control.value ?? (control.selected ? 'Selected' : 'Ready'))}</small></button>`).join('');
    elements.controlSurface.querySelectorAll('[data-review-control]').forEach(button =>
      button.addEventListener('click', () => invokeControl(button.dataset.reviewControl, Number(button.dataset.frameId))));
  } catch (error) { elements.controlSurface.textContent = error.message; }
}

function controlKind(kind) { return ['Button', 'Toggle', 'Input', 'Select', 'Reveal', 'Hover'][Number(kind)] ?? 'Control'; }

async function invokeControl(controlId, frameId) {
  try {
    const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/controls/${encodeURIComponent(controlId)}/invoke`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ frameId }),
    }));
    log(`${controlId}: ${body.message}`, body.receipt);
    await Promise.all([refreshControls(), refreshSnapshot()]);
  } catch (error) { log(error.message); }
}

async function refreshCaptureSurfaces() {
  const button = document.querySelector('#captureUnfocused');
  if (!activeBridgeId) { button.disabled = true; return; }
  try {
    const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/capture-surfaces`, { cache: 'no-store' }));
    const surfaces = body.receipt ?? [];
    elements.captureSurfaceSelect.innerHTML = surfaces.map(surface => `<option value="${escapeHtml(surface.id)}" ${surface.isDefault ? 'selected' : ''}>${escapeHtml(surface.label)}</option>`).join('');
    elements.captureSurfaceSelect.hidden = surfaces.length < 2;
    button.disabled = surfaces.length === 0;
  } catch (error) { button.disabled = true; elements.captureSurfaceSelect.innerHTML = ''; }
}

async function captureScreen(fullViewport, target = null, trigger = null) {
  const button = trigger ?? (fullViewport ? document.querySelector('#captureContext') : document.querySelector('#captureScreen'));
  button.disabled = true;
  try {
    const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/captures`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ fullViewport, target }),
    }));
    activeReviewId = body.review?.id ?? '';
    await displayReview(body.imageUrl);
    const receipt = body.receipt;
    elements.captureMeta.textContent = `${receipt.scope} · ${receipt.width}×${receipt.height} · ${new Date(receipt.capturedAtUtc).toLocaleString()} · SHA-256 ${receipt.sha256}`;
    log(body.message, receipt);
  } catch (error) { elements.captureMeta.textContent = error.message; log(error.message); }
  finally { button.disabled = false; }
}

async function captureUnfocusedReview() {
  const button = document.querySelector('#captureUnfocused');
  button.disabled = true;
  try {
    const target = elements.captureSurfaceSelect.value;
    const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/unfocused-review-capture-requests${target ? `?target=${encodeURIComponent(target)}` : ''}`, { method: 'POST' }));
    const result = await awaitCapture(`/api/unfocused-review-capture-requests/${encodeURIComponent(body.request.requestId)}`, body.request);
    activeReviewId = result.review?.id ?? '';
    await displayReview(result.imageUrl);
    elements.captureMeta.textContent = `${result.receipt.scope} · ${result.receipt.width}×${result.receipt.height} · ${result.receipt.captureMethod}`;
    log(result.message, result.receipt);
  } catch (error) { elements.captureMeta.textContent = error.message; log(error.message); }
  finally { button.disabled = false; }
}

async function captureCompositedWindow() {
  const button = document.querySelector('#captureComposited');
  button.disabled = true;
  try {
    const body = await readJson(await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/composited-capture-requests`, { method: 'POST' }));
    const result = await awaitCapture(`/api/composited-capture-requests/${encodeURIComponent(body.request.requestId)}`, body.request);
    activeReviewId = result.review?.id ?? '';
    await displayReview(result.imageUrl);
    elements.captureMeta.textContent = `${result.receipt.scope} · ${result.receipt.width}×${result.receipt.height}`;
  } catch (error) { elements.captureMeta.textContent = error.message; log(error.message); }
  finally { button.disabled = false; }
}

async function awaitCapture(url, request) {
  while (new Date(request.expiresAtUtc) > new Date()) {
    elements.captureMeta.textContent = request.message;
    await new Promise(resolve => setTimeout(resolve, 100));
    const body = await readJson(await fetch(url, { cache: 'no-store' }));
    request = body.request;
    if (request.state === 'completed') return request;
    if (request.state === 'failed' || request.state === 'expired') throw new Error(request.message);
  }
  throw new Error('Capture request expired before completion.');
}

async function displayReview(imageUrl) {
  const response = await fetch(imageUrl, { cache: 'no-store' });
  if (!response.ok) throw new Error('Saved capture is unavailable.');
  if (captureObjectUrl) URL.revokeObjectURL(captureObjectUrl);
  captureObjectUrl = URL.createObjectURL(await response.blob());
  elements.captureImage.src = captureObjectUrl;
  elements.captureImage.classList.add('ready');
}

async function restoreLatestReview() {
  const reviews = await readJson(await fetch('/api/reviews', { cache: 'no-store' }));
  const latest = reviews[0];
  if (!latest) return;
  activeReviewId = latest.id;
  await displayReview(`/api/reviews/${encodeURIComponent(latest.id)}.png`);
  elements.captureMeta.textContent = `${latest.receipt.scope} · ${latest.receipt.width}×${latest.receipt.height} · encrypted until ${new Date(latest.expiresAtUtc).toLocaleTimeString()}`;
}

document.querySelector('#refreshBridges').addEventListener('click', () => discover().catch(error => log(error.message)));
document.querySelector('#refreshSnapshot').addEventListener('click', () => refreshSnapshot().catch(error => log(error.message)));
document.querySelector('#refreshControls').addEventListener('click', () => refreshControls().catch(error => log(error.message)));
document.querySelector('#refreshPluginSurfaces').addEventListener('click', () => refreshPluginSurfaces().catch(error => log(error.message)));
elements.bridgeSelect.addEventListener('change', event => { activeBridgeId = event.target.value; discover().catch(error => log(error.message)); });
document.querySelector('#captureScreen').addEventListener('click', () => captureScreen(false));
document.querySelector('#captureContext').addEventListener('click', () => captureScreen(true));
document.querySelector('#captureComposited').addEventListener('click', captureCompositedWindow);
document.querySelector('#captureUnfocused').addEventListener('click', captureUnfocusedReview);
document.querySelector('#clearCapture').addEventListener('click', async () => {
  if (activeReviewId) await fetch(`/api/reviews/${encodeURIComponent(activeReviewId)}`, { method: 'DELETE' });
  activeReviewId = '';
  if (captureObjectUrl) URL.revokeObjectURL(captureObjectUrl);
  captureObjectUrl = '';
  elements.captureImage.removeAttribute('src');
  elements.captureImage.classList.remove('ready');
  elements.captureMeta.textContent = 'Saved capture cleared.';
});
window.addEventListener('beforeunload', () => { if (captureObjectUrl) URL.revokeObjectURL(captureObjectUrl); });
const events = new EventSource('/api/events');
events.addEventListener('bridges', () => discover().catch(error => log(error.message)));
events.onerror = () => { elements.connectionState.textContent = 'Reconnecting'; };
discover().then(restoreLatestReview).catch(error => log(error.message));
setInterval(() => { if (activeBridgeId) Promise.all([refreshSnapshot(), refreshControls()]).catch(error => log(error.message)); }, 1500);
