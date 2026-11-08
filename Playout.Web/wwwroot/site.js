const engine = '/api/engine/';
let tracks = [], playlists = [], loadedPlaylist = [], selected = null;
let loadedPlaylistName = '', syncingPlaylist = false;
let refreshingMeters = false;
const $ = selector => document.querySelector(selector);
const startupTime = Date.now();
let engineUnavailable = false;

function setAppError(message) {
  if (!message) return;
  $('#engine-status').textContent = message;
  $('#engine-status').classList.add('error');
}

function clearAppError() {
  $('#engine-status').classList.remove('error');
}

function renderEngineUnavailable() {
  const status = $('#engine-status');
  const graceElapsed = Date.now() - startupTime >= 5000;
  status.textContent = graceElapsed ? 'Engine nicht erreichbar' : 'Engine startet …';
  status.classList.toggle('error', graceElapsed);
}

async function request(url, options = {}) {
  const response = await fetch(url, options);
  if (response.ok) return response.status === 204 ? null : response.json();
  let message = `Fehler ${response.status}`;
  try { const json = await response.json(); message = json.error || (json.errors || []).join('\n') || message; } catch { }
  throw new Error(message);
}
function engineRequest(path, method = 'GET', body) {
  return request(engine + path, { method, headers: body ? { 'Content-Type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined });
}
function statusText(deck) { return deck ? `${deck.path ? deck.path.split(/[\\/]/).pop() : 'Kein Titel geladen'}\n${deck.state}${deck.error ? `\n${deck.error}` : ''}` : 'Nicht initialisiert'; }
function selectedTrack() { if (!selected) throw new Error('Bitte zuerst einen Titel ausw\u00e4hlen.'); return selected; }
function renderTracks() {
  const filter = $('#search').value.trim().toLocaleLowerCase(); const list = $('#tracks'); list.replaceChildren();
  tracks.filter(track => track.path.toLocaleLowerCase().includes(filter)).forEach(track => {
    const item = document.createElement('div'); item.className = 'track' + (selected === track ? ' selected' : '');
    item.innerHTML = `<strong></strong><small></small>`; item.querySelector('strong').textContent = track.title; item.querySelector('small').textContent = track.path;
    item.onclick = () => { selected = track; renderTracks(); };
    item.ondblclick = () => loadFreeDeck(track);
    item.oncontextmenu = event => showTrackMenu(event, track);
    list.append(item);
  });
  $('#library-status').textContent = `${list.childElementCount} / ${tracks.length} Titel`;
}
function renderPlaylists() {
  const list = $('#playlists'); list.replaceChildren();
  playlists.forEach(playlist => {
    const item = document.createElement('div'); item.className = 'track';
    item.textContent = playlist.name;
    item.ondblclick = () => loadPlaylist(playlist.name);
    list.append(item);
  });
}
function renderLoadedPlaylist() {
  const list = $('#loaded-playlist'); list.replaceChildren();
  loadedPlaylist.forEach(track => {
    const item = document.createElement('div'); item.className = 'track';
    item.innerHTML = '<strong></strong><small></small>';
    item.querySelector('strong').textContent = track.title;
    item.querySelector('small').textContent = track.path;
    item.oncontextmenu = event => showTrackMenu(event, track);
    list.append(item);
  });
  $('#loaded-playlist-name').textContent = loadedPlaylist.length ? `${loadedPlaylistName} · ${loadedPlaylist.length} Titel` : loadedPlaylistName || 'Keine Spielliste geladen';
}
async function loadPlaylist(name) {
  await run(async () => {
    const content = await request(`/api/playlists/content?name=${encodeURIComponent(name)}`);
    await request('/api/playlists/current', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name })
    });
    loadedPlaylist = content;
    loadedPlaylistName = name;
    renderLoadedPlaylist();
  });
}
async function syncLoadedPlaylist() {
  if (syncingPlaylist) return;
  syncingPlaylist = true;
  try {
    const current = await request('/api/playlists/current');
    if (current.name === loadedPlaylistName) return;
    if (!current.name) {
      loadedPlaylist = [];
      loadedPlaylistName = '';
    } else {
      loadedPlaylist = await request(`/api/playlists/content?name=${encodeURIComponent(current.name)}`);
      loadedPlaylistName = current.name;
    }
    renderLoadedPlaylist();
  } catch (error) { showError(error.message); }
  finally { syncingPlaylist = false; }
}
async function refreshPlaylists() {
  try {
    const [folder, result] = await Promise.all([request('/api/playlists/folder'), request('/api/playlists')]);
    playlists = result;
    await syncLoadedPlaylist();
    renderPlaylists();
    $('#playlists-status').textContent = folder.path ? `${playlists.length} Spiellisten · ${folder.path}` : 'Kein Spiellistenordner eingerichtet';
  } catch (error) { showError(error.message); }
}
async function refreshTracks() {
  try {
    const sources = await request('/api/sources');
    if (!sources.length) { tracks = []; renderTracks(); $('#library-status').textContent = 'Keine Musikquelle eingerichtet - bitte Einstellungen verwenden, um einen Ordner hinzuzuf\u00fcgen.'; return; }
    const result = await request('/api/tracks'); tracks = result.tracks; renderTracks();
    if (result.errors.length) showError(`${result.errors.length} Quelle(n) konnten nicht vollst\u00e4ndig gelesen werden.`);
  }
  catch (error) { showError(error.message); }
}
async function refreshStatus() {
  try {
    const status = await engineRequest('audio/status');
    engineUnavailable = false;
    clearAppError();
    $('#engine-status').textContent = status.initialized ? 'Engine: Audio aktiv' : 'Engine verbunden - Audio nicht gestartet';
    const deck = id => status.decks.find(item => item.deckId === id);
    $('#deck-1-status').textContent = statusText(deck('Deck 1')); $('#deck-2-status').textContent = statusText(deck('Deck 2'));
    const pfl = deck('PFL'); $('#pfl-status').textContent = pfl ? `${pfl.path ? pfl.path.split(/[\\/]/).pop() : 'Kein Titel'}: ${pfl.state}` : 'Nicht initialisiert';
  } catch {
    $('#engine-status').textContent = 'Engine nicht erreichbar';
    $('#deck-1-status').textContent = $('#deck-2-status').textContent = 'Engine nicht verbunden';
    $('#pfl-status').textContent = 'Engine nicht verbunden';
    setAppError('Die Engine ist nicht erreichbar. Bitte Engine und Routing prüfen.');
  }
}
function renderMeter(id, level) {
  const meter = $(`#${id}`);
  const normalized = Number.isFinite(level) ? Math.max(0, Math.min(1, level)) : 0;
  const percent = Math.round(normalized * 100);
  meter.setAttribute('aria-valuenow', String(percent));
  meter.querySelector('.web-meter-fill').style.height = `${percent}%`;
}
async function refreshMeters() {
  if (refreshingMeters) return;
  refreshingMeters = true;
  try {
    const levels = await engineRequest('audio/levels');
    const level = id => levels.find(item => item.deckId === id)?.level ?? 0;
    renderMeter('deck-1-meter', level('Deck 1'));
    renderMeter('deck-2-meter', level('Deck 2'));
  } catch {
    renderMeter('deck-1-meter', 0);
    renderMeter('deck-2-meter', 0);
  }
  finally { refreshingMeters = false; }
}
async function run(action) { try { await action(); await refreshStatus(); } catch (error) { showError(error.message); } }
async function loadFreeDeck(track) {
  await run(async () => { const status = await engineRequest('audio/status'); const deck = status.decks.find(item => (item.deckId === 'Deck 1' || item.deckId === 'Deck 2') && !item.path); if (!deck) throw new Error('Kein freier Ausspielschacht verf\u00fcgbar.'); await engineRequest(`decks/${encodeURIComponent(deck.deckId)}/load`, 'POST', { path: track.path }); await engineRequest(`decks/${encodeURIComponent(deck.deckId)}/play`, 'POST'); });
}
async function loadPfl() { const track = selectedTrack(); await run(async () => { await engineRequest('decks/PFL/load', 'POST', { path: track.path }); await engineRequest('decks/PFL/play', 'POST'); }); }
async function loadAndPlayDeck(deckId) {
  const track = selectedTrack();
  await run(async () => {
    const status = await engineRequest('audio/status');
    const deck = status.decks.find(item => item.deckId === deckId);
    if (deck?.state === 'Playing' && !confirm(`${deckId} spielt gerade. Titel ersetzen?`)) return;
    await engineRequest(`decks/${encodeURIComponent(deckId)}/load`, 'POST', { path: track.path });
    await engineRequest(`decks/${encodeURIComponent(deckId)}/play`, 'POST');
  });
}
function showError(message) {
  $('#settings-error').textContent = message;
  setAppError(message);
}
async function refreshSources() { const sources = await request('/api/sources'); const list = $('#sources'); list.replaceChildren(); sources.forEach(path => { const item = document.createElement('li'); item.className = 'source'; const label = document.createElement('span'); label.textContent = path; const remove = document.createElement('button'); remove.textContent = 'Entfernen'; remove.onclick = async () => { await run(async () => { await request(`/api/sources?path=${encodeURIComponent(path)}`, { method: 'DELETE' }); await refreshSources(); await refreshTracks(); }); }; item.append(label, remove); list.append(item); }); }

$('#settings-button').onclick = async () => {
  $('#settings-error').textContent = '';
  $('#settings-dialog').showModal();
  try {
    await refreshSources();
    const folder = await request('/api/playlists/folder');
    $('#playlist-folder').value = folder.path;
  } catch (error) { showError(error.message); }
};
$('#add-source').onclick = async () => { const path = $('#source-path').value.trim(); if (!path) return; await run(async () => { await request('/api/sources', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path }) }); $('#source-path').value = ''; await refreshSources(); await refreshTracks(); }); };
$('#scan').onclick = refreshTracks;
$('#save-playlist-folder').onclick = async () => {
  const path = $('#playlist-folder').value.trim();
  await run(async () => {
    const folder = await request('/api/playlists/folder', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path })
    });
    $('#playlist-folder').value = folder.path;
    await refreshPlaylists();
  });
};
$('#audio-start').onclick = () => run(() => engineRequest('audio/initialize', 'POST'));
$('#audio-stop').onclick = () => run(() => engineRequest('audio/shutdown', 'POST'));
$('#pfl-stop').onclick = () => run(() => engineRequest('decks/PFL/stop', 'POST'));
$('#next-track').onclick = () => { $('#track-menu').classList.remove('show'); loadFreeDeck(selectedTrack()); };
$('#preview').onclick = () => { $('#track-menu').classList.remove('show'); loadPfl(); };
$('#play-deck-1').onclick = () => { $('#track-menu').classList.remove('show'); loadAndPlayDeck('Deck 1'); };
$('#play-deck-2').onclick = () => { $('#track-menu').classList.remove('show'); loadAndPlayDeck('Deck 2'); };
$('#search-tab').onclick = () => {
  $('#search-tab').classList.add('active'); $('#playlists-tab').classList.remove('active');
  $('#search-view').hidden = false; $('#playlists-view').hidden = true;
};
$('#playlists-tab').onclick = () => {
  $('#playlists-tab').classList.add('active'); $('#search-tab').classList.remove('active');
  $('#search-view').hidden = true; $('#playlists-view').hidden = false;
  refreshPlaylists();
};
document.addEventListener('click', () => $('#track-menu').classList.remove('show'));
document.querySelectorAll('[data-deck]').forEach(button => button.onclick = () => run(() => engineRequest(`decks/${encodeURIComponent(button.dataset.deck)}/${button.dataset.action}`, 'POST')));
$('#search').oninput = renderTracks;

window.addEventListener('error', event => setAppError(`JavaScript-Fehler: ${event.message}`));
window.addEventListener('unhandledrejection', event => setAppError(`Fehler: ${event.reason?.message ?? event.reason ?? 'Unbekannt'}`));
window.setTimeout(() => { if (engineUnavailable) renderEngineUnavailable(); }, 5000);

async function bootstrap() {
  try
  {
    await refreshTracks();
    await refreshPlaylists();
    await refreshStatus();
    await refreshMeters();
    setInterval(async () => { await refreshStatus(); await syncLoadedPlaylist(); }, 2000);
    setInterval(refreshMeters, 80);
  }
  catch (error)
  {
    showError(error.message ?? String(error));
  }
}

bootstrap();
