const elements = {
  bridgeSelect: document.querySelector('#bridgeSelect'), connectionState: document.querySelector('#connectionState'),
  identity: document.querySelector('#identity'), routeState: document.querySelector('#routeState'),
  proofReceipt: document.querySelector('#proofReceipt'), activityLog: document.querySelector('#activityLog'),
  captureImage: document.querySelector('#captureImage'), captureMeta: document.querySelector('#captureMeta'),
  controlSurface: document.querySelector('#controlSurface'),
};
let bridges = [];
let activeBridgeId = '';
let captureObjectUrl = '';
let activeReviewId = '';
const escapeHtml = value => String(value ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#039;');

function log(message, value) {
  const stamp = new Date().toLocaleTimeString();
  const detail = value ? `\n${JSON.stringify(value, null, 2)}` : '';
  elements.activityLog.textContent = `[${stamp}] ${message}${detail}\n\n${elements.activityLog.textContent}`;
}

async function discover() {
  const response = await fetch('/api/bridges');
  bridges = await response.json();
  elements.bridgeSelect.innerHTML = bridges.map(bridge => `<option value="${escapeHtml(bridge.id)}">${escapeHtml(bridge.pluginName)} · PID ${escapeHtml(bridge.processId)}</option>`).join('');
  if (!bridges.some(bridge => bridge.id === activeBridgeId)) activeBridgeId = bridges[0]?.id ?? '';
  elements.bridgeSelect.value = activeBridgeId;
  elements.connectionState.textContent = activeBridgeId ? 'Connected' : 'No bridge found';
  elements.connectionState.classList.toggle('online', Boolean(activeBridgeId));
  if (activeBridgeId) await refreshSnapshot();
  if (activeBridgeId) await refreshReviewSurfaces();
  if (activeBridgeId) await refreshControls();
}

async function refreshReviewSurfaces() {
  const container = document.querySelector('#tabButtons');
  if (!activeBridgeId) { container.innerHTML = ''; return; }
  const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/review-surfaces`, { cache: 'no-store' });
  const body = await response.json();
  if (!response.ok || !body.success) {
    container.innerHTML = `<span class="subtitle">${escapeHtml(body.message ?? body.detail ?? 'This plugin does not advertise review surfaces.')}</span>`;
    return;
  }
  const surfaces = Array.isArray(body.receipt) ? [...body.receipt].sort((left, right) => Number(left.order) - Number(right.order)) : [];
  container.innerHTML = surfaces.map(surface => `<button data-review-surface="${escapeHtml(surface.target)}">${escapeHtml(surface.label)}</button>`).join('');
  container.querySelectorAll('[data-review-surface]').forEach(button =>
    button.addEventListener('click', () => captureScreen(false, button.dataset.reviewSurface, button)));
}

async function refreshSnapshot() {
  if (!activeBridgeId) return;
  const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/snapshot`);
  const body = await response.json();
  if (!response.ok || !body.success) throw new Error(body.detail ?? body.message ?? 'Snapshot failed');
  renderState(body.receipt);
}

function renderState(receipt) {
  const truth = receipt.truth ?? receipt;
  const route = truth.route;
  const isMmf = Boolean(route);
  document.querySelectorAll('.mmf-only').forEach(element => { element.hidden = !isMmf; });
  elements.identity.innerHTML = [
    ['Plugin', truth.pluginVersion], ['Character', truth.characterName || 'Unavailable'],
    ['World', truth.currentWorld || 'Unavailable'], ['Process', truth.processId],
  ].map(([label, value]) => `<div class="metric"><small>${escapeHtml(label)}</small><strong>${escapeHtml(value)}</strong></div>`).join('');
  if (!isMmf) {
    const capabilities = Array.isArray(truth.capabilities) ? truth.capabilities.join(' · ') : 'snapshot only';
    elements.routeState.innerHTML = `<strong>${escapeHtml(truth.hostKind ?? 'Bridge host')}</strong><br><span class="subtitle">Capabilities: ${escapeHtml(capabilities)} · Screenshots: ${truth.screenshotsEnabled ? 'enabled' : 'disabled'}</span>`;
    document.querySelector('#captureScreen').textContent = 'Capture plugin window';
    return;
  }
  elements.routeState.innerHTML = `<strong>${escapeHtml(route.state)}</strong> · ${escapeHtml(route.statusMessage)}<br><span class="subtitle">Active stop: ${escapeHtml(route.activeWorld ?? 'None')} · Operation: ${escapeHtml(route.activeOperationKind ?? 'None')} / ${escapeHtml(route.activeOperationPhase ?? 'None')}</span>`;
  document.querySelector('#captureScreen').textContent = 'Capture MMF window';
}

function renderProof(receipt) {
  elements.proofReceipt.innerHTML = [
    ['Proof ID', receipt.proofId], ['Revision', receipt.revision], ['Challenge', receipt.challenge || '(none)'],
    ['Presented', receipt.presentedInGame ? 'Yes' : 'No'], ['Proof SHA-256', receipt.proofSha256], ['Truth SHA-256', receipt.truthSha256],
  ].map(([label, value]) => `<div><dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd></div>`).join('');
}

function renderReceipt(receipt) { renderState(receipt); if (receipt.truth) renderProof(receipt); }

async function refreshControls() {
  if (!activeBridgeId) return;
  const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/controls`, { cache: 'no-store' });
  const body = await response.json();
  if (!response.ok || !body.success) {
    elements.controlSurface.textContent = body.message ?? body.detail ?? 'This plugin does not expose a registered control surface.';
    return;
  }
  renderControls(body.receipt);
}

function renderControls(surface) {
  const controls = Array.isArray(surface?.controls) ? surface.controls : [];
  if (!controls.length) {
    elements.controlSurface.className = 'privacy-note';
    elements.controlSurface.textContent = 'No actionable controls are currently rendered. Open the plugin window, then refresh.';
    return;
  }
  elements.controlSurface.className = 'control-surface';
  elements.controlSurface.innerHTML = controls.map(control => `<button data-review-control="${escapeHtml(control.id)}" data-frame-id="${escapeHtml(surface.frameId)}" ${control.enabled ? '' : 'disabled'}>${escapeHtml(control.label)}<small>${escapeHtml(controlKind(control.kind))} · ${escapeHtml(control.value ?? (control.selected ? 'Selected' : 'Ready'))}</small></button>`).join('');
  elements.controlSurface.querySelectorAll('[data-review-control]').forEach(button => button.addEventListener('click', () => invokeControl(button.dataset.reviewControl, Number(button.dataset.frameId))));
}

function controlKind(kind) { return ['Button', 'Toggle', 'Input', 'Select'][Number(kind)] ?? 'Control'; }

async function invokeControl(controlId, frameId) {
  const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/controls/${encodeURIComponent(controlId)}/invoke`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ frameId }) });
  const body = await response.json();
  log(`control ${controlId}: ${body.message ?? body.detail ?? response.status}`, body.receipt);
  if (!response.ok || !body.success) throw new Error(body.message ?? body.detail ?? 'Control action failed');
  await new Promise(resolve => setTimeout(resolve, 100));
  await refreshControls();
  await refreshSnapshot();
}

async function command(commandName, payload = {}) {
  if (!activeBridgeId) throw new Error('No active bridge instance');
  const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/commands/${commandName}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
  const body = await response.json();
  log(`${commandName}: ${body.message ?? body.detail ?? response.status}`, body.receipt);
  if (!response.ok || !body.success) throw new Error(body.message ?? body.detail ?? `${commandName} failed`);
  if (body.receipt) renderReceipt(body.receipt);
  return body;
}

document.querySelector('#refreshBridges').addEventListener('click', () => discover().catch(error => log(error.message)));
document.querySelector('#refreshSnapshot').addEventListener('click', () => refreshSnapshot().catch(error => log(error.message)));
document.querySelector('#refreshControls').addEventListener('click', () => refreshControls().catch(error => log(error.message)));
elements.bridgeSelect.addEventListener('change', event => {
  activeBridgeId = event.target.value;
  refreshSnapshot().catch(error => log(error.message));
  refreshReviewSurfaces().catch(error => log(error.message));
});
document.querySelectorAll('[data-command]').forEach(button => button.addEventListener('click', () => command(button.dataset.command).then(refreshSnapshot).catch(error => log(error.message))));
document.querySelector('#captureProof').addEventListener('click', async () => { const challenge = `bridge-ui-${Date.now()}`; try { await command('capture-proof', { challenge }); await new Promise(resolve => setTimeout(resolve, 500)); await command('get-proof'); } catch (error) { log(error.message); } });
async function captureScreen(fullViewport, target = null, trigger = null) {
  if (!activeBridgeId) return;
  const button = trigger ?? (fullViewport ? document.querySelector('#captureContext') : document.querySelector('#captureScreen'));
  button.disabled = true;
  try {
    const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/captures`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ fullViewport, target }) });
    const body = await response.json();
    if (!response.ok || !body.success) throw new Error(body.detail ?? body.message ?? 'Capture failed');
    const receipt = body.receipt;
    activeReviewId = body.review?.id ?? '';
    await displayReview(body.imageUrl);
    elements.captureMeta.textContent = `${receipt.scope} · ${receipt.width}×${receipt.height} · ${new Date(receipt.capturedAtUtc).toLocaleString()} · SHA-256 ${receipt.sha256}`;
    log(target ? `review-control ${target}: ${body.message}` : `capture-screen: ${body.message}`);
    await refreshSnapshot();
  } catch (error) { log(error.message); elements.captureMeta.textContent = error.message; }
  finally { button.disabled = false; }
}

async function captureCompositedWindow() {
  if (!activeBridgeId) return;
  const button = document.querySelector('#captureComposited');
  button.disabled = true;
  try {
    const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/composited-capture-requests`, { method: 'POST' });
    const body = await response.json();
    if (!response.ok || !body.success) throw new Error(body.detail ?? body.message ?? 'Composited capture could not be queued');
    elements.captureMeta.textContent = 'Waiting for the FFXIV client to become foreground…';
    const result = await awaitCompositedCapture(body.request);
    activeReviewId = result.review?.id ?? '';
    await displayReview(result.imageUrl);
    const receipt = result.receipt;
    elements.captureMeta.textContent = `${receipt.scope} · ${receipt.width}×${receipt.height} · ${new Date(receipt.capturedAtUtc).toLocaleString()} · SHA-256 ${receipt.sha256}`;
    log(`composited capture: ${body.message}`);
  } catch (error) { log(error.message); elements.captureMeta.textContent = error.message; }
  finally { button.disabled = false; }
}

async function captureUnfocusedReview() {
  if (!activeBridgeId) return;
  const button = document.querySelector('#captureUnfocused');
  button.disabled = true;
  try {
    elements.captureMeta.textContent = 'Preparing a frame-confirmed plugin presentation…';
    const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/unfocused-review-capture-requests`, { method: 'POST' });
    const body = await response.json();
    if (!response.ok || !body.success) throw new Error(body.detail ?? body.message ?? 'Unfocused review capture could not start');
    const result = await awaitUnfocusedCapture(body.request);
    activeReviewId = result.review?.id ?? '';
    await displayReview(result.imageUrl);
    const receipt = result.receipt;
    elements.captureMeta.textContent = `${receipt.scope} · ${receipt.width}×${receipt.height} · ${receipt.captureMethod} · frame ${receipt.frameId} · ${new Date(receipt.capturedAtUtc).toLocaleString()}`;
    log(`unfocused review: ${result.message}`, receipt);
    await refreshSnapshot();
  } catch (error) { log(error.message); elements.captureMeta.textContent = error.message; }
  finally { button.disabled = false; }
}

async function awaitUnfocusedCapture(request) {
  while (new Date(request.expiresAtUtc) > new Date()) {
    elements.captureMeta.textContent = request.message;
    await new Promise(resolve => setTimeout(resolve, 100));
    const response = await fetch(`/api/unfocused-review-capture-requests/${encodeURIComponent(request.requestId)}`, { cache: 'no-store' });
    const body = await response.json();
    if (!response.ok || !body.success) throw new Error(body.detail ?? body.message ?? 'Unfocused review capture was lost');
    request = body.request;
    if (request.state === 'completed') return request;
    if (request.state === 'failed') throw new Error(request.message);
  }
  throw new Error('Unfocused review capture expired before completion');
}

async function awaitCompositedCapture(request) {
  while (new Date(request.expiresAtUtc) > new Date()) {
    await new Promise(resolve => setTimeout(resolve, 150));
    const response = await fetch(`/api/composited-capture-requests/${encodeURIComponent(request.requestId)}`, { cache: 'no-store' });
    const body = await response.json();
    if (!response.ok || !body.success) throw new Error(body.detail ?? body.message ?? 'Queued composited capture was lost');
    request = body.request;
    if (request.state === 'completed') return request;
    if (request.state === 'failed' || request.state === 'expired') throw new Error(request.message);
  }
  throw new Error('Composited capture request expired before FFXIV became foreground');
}

async function displayReview(imageUrl) {
  const imageResponse = await fetch(imageUrl, { cache: 'no-store' });
  if (!imageResponse.ok) throw new Error('Saved capture is unavailable');
  const imageBlob = await imageResponse.blob();
  if (captureObjectUrl) URL.revokeObjectURL(captureObjectUrl);
  captureObjectUrl = URL.createObjectURL(imageBlob);
  elements.captureImage.src = captureObjectUrl;
  elements.captureImage.classList.add('ready');
}

async function restoreLatestReview() {
  const response = await fetch('/api/reviews', { cache: 'no-store' });
  if (!response.ok) return;
  const reviews = await response.json();
  const latest = reviews[0];
  if (!latest) return;
  activeReviewId = latest.id;
  await displayReview(`/api/reviews/${encodeURIComponent(latest.id)}.png`);
  const receipt = latest.receipt;
  elements.captureMeta.textContent = `${receipt.scope} · ${receipt.width}×${receipt.height} · ${new Date(receipt.capturedAtUtc).toLocaleString()} · encrypted local review until ${new Date(latest.expiresAtUtc).toLocaleTimeString()}`;
}
document.querySelector('#captureScreen').addEventListener('click', () => captureScreen(false));
document.querySelector('#captureContext').addEventListener('click', () => captureScreen(true));
document.querySelector('#captureComposited').addEventListener('click', captureCompositedWindow);
document.querySelector('#captureUnfocused').addEventListener('click', captureUnfocusedReview);
document.querySelector('#clearCapture').addEventListener('click', async () => {
  if (!activeReviewId) return;
  const response = await fetch(`/api/reviews/${encodeURIComponent(activeReviewId)}`, { method: 'DELETE' });
  if (!response.ok && response.status !== 404) return log('Could not clear the saved capture');
  activeReviewId = '';
  if (captureObjectUrl) URL.revokeObjectURL(captureObjectUrl);
  captureObjectUrl = '';
  elements.captureImage.removeAttribute('src');
  elements.captureImage.classList.remove('ready');
  elements.captureMeta.textContent = 'Saved capture cleared.';
});
window.addEventListener('beforeunload', () => { if (captureObjectUrl) URL.revokeObjectURL(captureObjectUrl); });
discover().then(restoreLatestReview).catch(error => log(error.message));
setInterval(() => { refreshSnapshot().catch(error => log(error.message)); refreshControls().catch(error => log(error.message)); }, 1500);
