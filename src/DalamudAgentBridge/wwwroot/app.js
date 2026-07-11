const elements = {
  bridgeSelect: document.querySelector('#bridgeSelect'),
  connectionState: document.querySelector('#connectionState'),
  identity: document.querySelector('#identity'),
  routeState: document.querySelector('#routeState'),
  proofReceipt: document.querySelector('#proofReceipt'),
  activityLog: document.querySelector('#activityLog'),
  captureImage: document.querySelector('#captureImage'),
  captureMeta: document.querySelector('#captureMeta'),
};
let bridges = [];
let activeBridgeId = '';
let captureObjectUrl = '';
const escapeHtml = value => String(value ?? '')
  .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
  .replaceAll('"', '&quot;').replaceAll("'", '&#039;');

const tabs = ['Overview', 'Inventory Reporter', 'Workshop Logistics', 'Restock', 'Market Acquisition', 'Diagnostics', 'Settings', 'Status'];
document.querySelector('#tabButtons').innerHTML = tabs.map(tab => `<button data-tab="${escapeHtml(tab)}">${escapeHtml(tab)}</button>`).join('');

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
}

async function refreshSnapshot() {
  if (!activeBridgeId) return;
  const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/snapshot`);
  const body = await response.json();
  if (!response.ok || !body.success) throw new Error(body.detail ?? body.message ?? 'Snapshot failed');
  renderState(body.receipt);
}

function renderState(receipt) {
  const truth = receipt.truth;
  const route = truth.route;
  elements.identity.innerHTML = [
    ['Plugin', truth.pluginVersion],
    ['Character', truth.characterName || 'Unavailable'],
    ['World', truth.currentWorld || 'Unavailable'],
    ['Process', truth.processId],
  ].map(([label, value]) => `<div class="metric"><small>${escapeHtml(label)}</small><strong>${escapeHtml(value)}</strong></div>`).join('');
  elements.routeState.innerHTML = `<strong>${escapeHtml(route.state)}</strong> · ${escapeHtml(route.statusMessage)}<br><span class="subtitle">Active stop: ${escapeHtml(route.activeWorld ?? 'None')} · Operation: ${escapeHtml(route.activeOperationKind ?? 'None')} / ${escapeHtml(route.activeOperationPhase ?? 'None')}</span>`;
}

function renderProof(receipt) {
  elements.proofReceipt.innerHTML = [
    ['Proof ID', receipt.proofId],
    ['Revision', receipt.revision],
    ['Challenge', receipt.challenge || '(none)'],
    ['Presented', receipt.presentedInGame ? 'Yes' : 'No'],
    ['Proof SHA-256', receipt.proofSha256],
    ['Truth SHA-256', receipt.truthSha256],
  ].map(([label, value]) => `<div><dt>${escapeHtml(label)}</dt><dd>${escapeHtml(value)}</dd></div>`).join('');
}

function renderReceipt(receipt) {
  renderState(receipt);
  renderProof(receipt);
}

async function command(commandName, payload = {}) {
  if (!activeBridgeId) throw new Error('No active bridge instance');
  const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/commands/${commandName}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload),
  });
  const body = await response.json();
  log(`${commandName}: ${body.message ?? body.detail ?? response.status}`, body.receipt);
  if (!response.ok || !body.success) throw new Error(body.message ?? body.detail ?? `${commandName} failed`);
  if (body.receipt) renderReceipt(body.receipt);
  return body;
}

document.querySelector('#refreshBridges').addEventListener('click', () => discover().catch(error => log(error.message)));
document.querySelector('#refreshSnapshot').addEventListener('click', () => refreshSnapshot().catch(error => log(error.message)));
elements.bridgeSelect.addEventListener('change', event => { activeBridgeId = event.target.value; refreshSnapshot().catch(error => log(error.message)); });
document.querySelectorAll('[data-command]').forEach(button => button.addEventListener('click', () => command(button.dataset.command).then(refreshSnapshot).catch(error => log(error.message))));
document.querySelectorAll('[data-tab]').forEach(button => button.addEventListener('click', () => captureScreen(false, button.dataset.tab, button)));
document.querySelector('#captureProof').addEventListener('click', async () => {
  const challenge = `bridge-ui-${Date.now()}`;
  try { await command('capture-proof', { challenge }); await new Promise(resolve => setTimeout(resolve, 500)); await command('get-proof'); } catch (error) { log(error.message); }
});
async function captureScreen(fullViewport, target = null, trigger = null) {
  if (!activeBridgeId) return;
  const button = trigger ?? (fullViewport ? document.querySelector('#captureContext') : document.querySelector('#captureScreen'));
  button.disabled = true;
  try {
    const response = await fetch(`/api/bridges/${encodeURIComponent(activeBridgeId)}/captures`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ fullViewport, target }),
    });
    const body = await response.json();
    if (!response.ok || !body.success) throw new Error(body.detail ?? body.message ?? 'Capture failed');
    const receipt = body.receipt;
    const imageResponse = await fetch(body.imageUrl, { cache: 'no-store' });
    if (!imageResponse.ok) throw new Error('Capture image delivery expired before it could be displayed');
    const imageBlob = await imageResponse.blob();
    if (captureObjectUrl) URL.revokeObjectURL(captureObjectUrl);
    captureObjectUrl = URL.createObjectURL(imageBlob);
    elements.captureImage.src = captureObjectUrl;
    elements.captureImage.classList.add('ready');
    elements.captureMeta.textContent = `${receipt.scope} · ${receipt.width}×${receipt.height} · ${new Date(receipt.capturedAtUtc).toLocaleString()} · SHA-256 ${receipt.sha256}`;
    log(target ? `review-control ${target}: ${body.message}` : `capture-screen: ${body.message}`);
    await refreshSnapshot();
  } catch (error) {
    log(error.message);
    elements.captureMeta.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}
document.querySelector('#captureScreen').addEventListener('click', () => captureScreen(false));
document.querySelector('#captureContext').addEventListener('click', () => captureScreen(true));
window.addEventListener('beforeunload', () => { if (captureObjectUrl) URL.revokeObjectURL(captureObjectUrl); });

discover().catch(error => log(error.message));
setInterval(() => refreshSnapshot().catch(error => log(error.message)), 1500);
