/* TOKENIFY — keeps the personal Live Tracker in sync with the signed-in account.
 *
 * Sync model: one versioned snapshot per account (table tk_tracker_state), written only through
 * tk_tracker_push(), which accepts a write only if the caller has seen the latest version. On a
 * version conflict the client merges by record IDs (sessions, games, token finds) and retries, so
 * retries can never duplicate chests and nothing is silently overwritten. Changes made offline stay
 * saved in this browser (under this account) and are pushed when the connection is back.
 */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h, api = TK.api;
  var state = 'off', lastSync = null, lastError = '', pushTimer = null, busy = false, dirty = false, started = false;

  function meta() { return U.storage.get('sync.meta', { version: 0, linked: false }); }   // per account (scoped key)
  function setMeta(m) { U.storage.set('sync.meta', m); }
  function setState(s, err) {
    state = s; if (err != null) lastError = err;
    var dot = U.$('#sync-dot');
    if (dot) dot.setAttribute('data-state', s === 'synced' ? 'synced' : s === 'pending' || s === 'syncing' ? 'pending' : s === 'offline' ? 'offline' : s === 'error' ? 'error' : '');
    U.bus.emit('sync-state', s);
  }

  // ---------------- merge (ID-based union; never deletes) ----------------
  function clone(x) { return JSON.parse(JSON.stringify(x)); }
  function gChests(g) { return (g.ts ? g.ts.length : (g.archived || 0)) + (g.adj || 0); }
  var RANK = { active: 0, paused: 1, completed: 2, incomplete: 2, void: 3 };
  function pickGame(a, b) {
    // A game built from a replay import: the copy made from the newest server data (or the latest choice) wins.
    if (a.replay || b.replay) {
      var ka = a.replay ? String(a.replay.v || '') + '|' + String(a.replay.decidedAt || 0).padStart(15, '0') : '';
      var kb = b.replay ? String(b.replay.v || '') + '|' + String(b.replay.decidedAt || 0).padStart(15, '0') : '';
      if (ka !== kb) { var w = clone(kb > ka ? b : a); w.chests = gChests(w); return w; }
    }
    var la = a.ts ? a.ts.length : (a.archived || 0), lb = b.ts ? b.ts.length : (b.archived || 0);
    var base = lb > la ? b : a; // more recorded chests wins (one is a prefix of the other in practice)
    var out = clone(base);
    out.adj = Math.max(a.adj || 0, b.adj || 0) === (a.adj || 0) && (a.edits || []).length >= (b.edits || []).length ? (a.adj || 0) : (b.adj || 0);
    if ((RANK[b.status] || 0) > (RANK[out.status] || 0)) { out.status = b.status; out.end = b.end; out.endKind = b.endKind; }
    if ((RANK[a.status] || 0) > (RANK[out.status] || 0)) { out.status = a.status; out.end = a.end; out.endKind = a.endKind; }
    var seen = {}; out.edits = (a.edits || []).concat(b.edits || []).filter(function (e) { var k = e.t + ':' + e.kind; if (seen[k]) return false; seen[k] = 1; return true; });
    out.chests = gChests(out);
    return out;
  }
  function pickSession(a, b) {
    var out = clone((RANK[b.status] || 0) > (RANK[a.status] || 0) ? b : a);
    out.activeMs = Math.max(a.activeMs || 0, b.activeMs || 0);
    out.end = out.end || a.end || b.end;
    out.adj = (a.adjLog || []).length >= (b.adjLog || []).length ? (a.adj || 0) : (b.adj || 0);
    out.adjLog = (a.adjLog || []).length >= (b.adjLog || []).length ? (a.adjLog || []) : (b.adjLog || []);
    out.timeLog = (a.timeLog || []).length >= (b.timeLog || []).length ? (a.timeLog || []) : (b.timeLog || []);
    out.lastChestAt = Math.max(a.lastChestAt || 0, b.lastChestAt || 0) || null;
    return out;
  }
  function merge(local, remote) {
    if (!remote) return clone(local);
    var out = clone(local);
    Object.keys(remote.sessions || {}).forEach(function (id) { out.sessions[id] = out.sessions[id] ? pickSession(out.sessions[id], remote.sessions[id]) : clone(remote.sessions[id]); });
    Object.keys(remote.games || {}).forEach(function (id) { out.games[id] = out.games[id] ? pickGame(out.games[id], remote.games[id]) : clone(remote.games[id]); });
    out.sessionOrder = Object.keys(out.sessions).sort(function (x, y) { return out.sessions[x].start - out.sessions[y].start; });
    var pos = {}; (remote.gameOrder || []).forEach(function (id, i) { pos[id] = i; }); (local.gameOrder || []).forEach(function (id, i) { if (pos[id] == null) pos[id] = i + 0.5; });
    out.gameOrder = Object.keys(out.games).sort(function (x, y) { return (out.games[x].start - out.games[y].start) || (pos[x] - pos[y]); });
    var tok = {}; (local.tokens || []).concat(remote.tokens || []).forEach(function (t) { if (!tok[t.id]) tok[t.id] = clone(t); else if (t.undone) tok[t.id].undone = true; });
    out.tokens = Object.keys(tok).map(function (k) { return tok[k]; }).sort(function (x, y) { return x.t - y.t; });
    var live = out.tokens.filter(function (t) { return !t.undone; }); if (live.length > 1) live.slice(1).forEach(function (t) { t.undone = true; }); // one Pirate Token per player
    out.importTotal = Math.max(local.importTotal || 0, remote.importTotal || 0);
    var cs = {}; out.corrections = (local.corrections || []).concat(remote.corrections || []).filter(function (c) { var k = c.t + c.text; if (cs[k]) return false; cs[k] = 1; return true; }).sort(function (x, y) { return x.t - y.t; });
    // an open session on two devices: keep this device's pointer, other copies become paused
    Object.keys(out.sessions).forEach(function (id) { if (id !== out.currentSid && out.sessions[id].status === 'active') out.sessions[id].status = 'paused'; });
    return out;
  }
  function hasData(s) { return !!(s && ((s.sessionOrder && s.sessionOrder.length) || s.importTotal)); }
  function chestsIn(s) { var c = s.importTotal || 0; Object.keys(s.sessions || {}).forEach(function (k) { c += s.sessions[k].chests || 0; }); return c; }
  function summary(s) { return U.fmtInt(chestsIn(s)) + ' chests in ' + (s.sessionOrder || []).length + ' sessions'; }

  // ---------------- server I/O ----------------
  async function fetchRemote() {
    var r = await api.rpc('tk_tracker_pull', {}, { retry: true });
    return { state: r.state || null, version: Number(r.version || 0) };
  }
  async function pushState(expected, st) {
    var r = await api.rpc('tk_tracker_push', { p_expected: expected, p_state: st, p_device: api.deviceLabel() });
    if (r.ok) return { ok: true, version: Number(r.version) };
    if (r.error === 'conflict') return { ok: false, version: Number(r.version || 0), state: r.state || null };
    throw Object.assign(new Error(api.message(r.error)), { code: r.error });
  }
  async function flush() {
    if (!started || busy) { dirty = true; return; }
    if (TK.auth.offline()) { setState('offline'); dirty = true; return; }
    if (!navigator.onLine) { setState('offline'); dirty = true; return; }
    var m = meta(); if (!m.linked) { dirty = true; return; }
    busy = true; dirty = false; setState('syncing');
    try {
      for (var attempt = 0; attempt < 4; attempt++) {
        var res = await pushState(m.version, TK.tracker.state);
        if (res.ok) { m.version = res.version; setMeta(m); lastSync = Date.now(); setState('synced', ''); break; }
        // another device wrote first: merge theirs into ours and try again
        var remote = res.state ? res : await fetchRemote();
        if (remote.state) TK.tracker.replaceState(merge(TK.tracker.state, remote.state), 'sync-merge');
        m.version = remote.version; setMeta(m);
      }
    } catch (e) { dirty = true; setState(e.kind === 'network' ? 'offline' : 'error', e.message || String(e)); }
    finally { busy = false; if (dirty && state !== 'error' && state !== 'offline') schedule(); }
  }
  function schedule(ms) { clearTimeout(pushTimer); pushTimer = setTimeout(flush, ms || 2500); if (state === 'synced') setState('pending'); }
  async function pull() {
    if (!started || busy || TK.auth.offline()) return;
    var m = meta(); if (!m.linked) return;
    try {
      var remote = await fetchRemote();
      if (remote.state && remote.version > m.version) { TK.tracker.replaceState(merge(TK.tracker.state, remote.state), 'sync-pull'); m.version = remote.version; setMeta(m); schedule(); }
      lastSync = Date.now(); if (!dirty) setState('synced', '');
    } catch (e) { setState(e.kind === 'network' ? 'offline' : 'error', e.message || String(e)); }
  }
  // First sync of this account in this browser: combines both sides by record ID, overwriting neither.
  async function link() {
    var m = meta();
    if (m.linked) { await pull(); if (dirty) flush(); return; }
    setState('syncing');
    var remote; try { remote = await fetchRemote(); } catch (e) { setState(e.kind === 'network' ? 'offline' : 'error', e.message); return; }
    var local = TK.tracker.state;
    if (remote.state && !hasData(local)) TK.tracker.replaceState(remote.state, 'sync-adopt');
    else if (remote.state) TK.tracker.replaceState(merge(local, remote.state), 'sync-merge');
    setMeta({ version: remote.version, linked: true });
    lastSync = Date.now();
    if (!remote.state || hasData(local)) await flush(); else setState('synced', '');
  }

  // ---------------- data saved in this browser before accounts existed ----------------
  function legacyRaw(k) { try { return localStorage.getItem(U.storage.legacyKey(k)); } catch (e) { return null; } }
  function legacyData() {
    var out = {};
    try { var t = JSON.parse(legacyRaw('tracker.state') || 'null'); if (t && t.v === 1 && hasData(t)) out.tracker = t; } catch (e) { /* unreadable */ }
    try { var i = JSON.parse(legacyRaw('interactive') || 'null'); if (i && i.v === 1 && i.total > 0) out.interactive = i; } catch (e) { /* unreadable */ }
    try { var l = JSON.parse(legacyRaw('lab') || 'null'); if (l && l.v === 1 && l.runs && l.runs.length) out.lab = l; } catch (e) { /* unreadable */ }
    return (out.tracker || out.interactive || out.lab) ? out : null;
  }
  function retireLegacy() {
    ['tracker.state', 'tracker.live', 'tracker.owner', 'interactive', 'lab', 'sync.meta'].forEach(function (k) {
      try {
        var raw = localStorage.getItem(U.storage.legacyKey(k));
        if (raw != null) { localStorage.setItem(TK.config.STORAGE_PREFIX + 'legacy-imported.' + k, raw); localStorage.removeItem(U.storage.legacyKey(k)); }
      } catch (e) { /* storage blocked */ }
    });
  }
  function importLegacy() {
    var d = legacyData(); if (!d) return false;
    if (d.tracker) { TK.tracker.replaceState(merge(TK.tracker.state, d.tracker), 'import-legacy'); dirty = true; }
    if (d.interactive && !(TK.store.interactive.data().total > 0)) U.storage.set('interactive', d.interactive);
    if (d.lab && !TK.store.lab.runs().length) U.storage.set('lab', d.lab);
    retireLegacy();
    TK.store.load();
    U.bus.emit('interactive-changed', { reset: true }); U.bus.emit('lab-changed', null);
    if (meta().linked) flush();
    return true;
  }
  function offerLegacy(force) {
    var d = legacyData();
    if (!d) { if (force) ui.toast('There’s no earlier TOKENIFY data in this browser.'); return; }
    if (!force && U.storage.get('device.legacySnooze', 0) > Date.now()) return;
    var items = [];
    if (d.tracker) items.push(h('li', {}, ['Live Tracker: ' + summary(d.tracker)]));
    if (d.interactive) items.push(h('li', {}, ['Chest Simulator: ' + U.fmtInt(d.interactive.total) + ' simulated chests and your collection']));
    if (d.lab) items.push(h('li', {}, ['Probability Lab: ' + d.lab.runs.length + ' saved runs']));
    ui.modal({ title: 'Bring in your earlier data?', sub: 'This browser has TOKENIFY data from before accounts.',
      body: h('div', {}, [h('ul', { class: 'legacy-list' }, items), h('p', { class: 'fine', text: 'It’s added to ' + TK.auth.account().username + ' (tracker records are merged by ID, so nothing is counted twice) and removed from guest storage.' })]),
      actions: [{ label: 'Not now', kind: 'ghost', onClick: function () { U.storage.set('device.legacySnooze', Date.now() + 7 * 864e5); } },
        { label: 'Add to my account', kind: 'gold', onClick: function () { if (importLegacy()) ui.toast('Earlier data added to your account.'); } }] });
  }

  function statusText() {
    if (TK.auth.offline()) return 'Offline — saved on this device. It will sync to your account when you’re back online.';
    if (state === 'synced') return 'Synced to your account' + (lastSync ? ' ' + Math.max(0, Math.round((Date.now() - lastSync) / 60000)) + ' min ago' : '') + '.';
    if (state === 'pending' || state === 'syncing') return 'Saved on this device — syncing to your account…';
    if (state === 'offline') return 'Offline — saved on this device. It will sync when you’re back online.';
    if (state === 'error') return 'Saved on this device, but syncing failed: ' + lastError;
    return 'Saved on this device.';
  }

  TK.sync = {
    start: function () {
      if (started) return;
      started = true;
      setState(TK.auth.offline() ? 'offline' : 'pending');
      link().then(function () { setTimeout(function () { offerLegacy(false); }, 1200); });
      window.addEventListener('online', function () { flush(); });
      window.addEventListener('focus', function () { pull(); });
      setInterval(function () { if (navigator.onLine) { if (dirty) flush(); else pull(); } }, 60000);
      window.addEventListener('pagehide', function () { if (dirty) flush(); });
    },
    // called by the tracker on every change; chests are pushed within a couple of seconds
    notifyChange: function () { if (started) { dirty = true; schedule(); } },
    syncNow: function () { return flush().then(pull); },
    state: function () { return state; },
    statusText: statusText,
    offerLegacy: offerLegacy,
    hasLegacy: function () { return !!legacyData(); },
    _merge: merge
  };
})(window.TK = window.TK || {});
