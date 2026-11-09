/* TOKENIFY — optional accounts & cloud sync (Supabase Auth + Postgres with row-level security).
 *
 * Works only when js/config.js has a Supabase URL and anon (public) key; otherwise the app runs in
 * guest mode and this module just explains how to enable accounts. Passwords go straight to
 * Supabase Auth and are never stored or logged by TOKENIFY.
 *
 * Sync model: one versioned snapshot per user (table tracker_state), written only through the
 * push_tracker_state() function, which accepts a write only if the caller has seen the latest
 * version. On a version conflict the client merges by record IDs (sessions, games, token finds)
 * and retries, so retries can never duplicate chests and nothing is silently overwritten.
 * Local changes made offline stay queued in the browser and are pushed when back online.
 */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h;
  var cfg = TK.config.SUPABASE || {}, client = null, user = null, state = 'off', lastSync = null, lastError = '', pushTimer = null, busy = false, dirty = false;
  var LIB = 'https://cdn.jsdelivr.net/npm/@supabase/supabase-js@2.45.4/dist/umd/supabase.min.js';
  function configured() { return !!(cfg.url && cfg.anonKey); }
  function meta() { return U.storage.get('sync.meta', { userId: null, version: 0, linked: false }); }
  function setMeta(m) { U.storage.set('sync.meta', m); }
  function setState(s, err) { state = s; if (err != null) lastError = err; var dot = U.$('#sync-dot'); if (dot) dot.setAttribute('data-state', s === 'synced' ? 'synced' : s === 'pending' || s === 'syncing' ? 'pending' : s === 'offline' ? 'offline' : s === 'error' ? 'error' : ''); U.bus.emit('sync-state', s); }

  function loadLib() {
    return new Promise(function (resolve, reject) {
      if (window.supabase && window.supabase.createClient) return resolve();
      var sc = document.createElement('script'); sc.src = LIB; sc.async = true; sc.crossOrigin = 'anonymous';
      sc.onload = function () { window.supabase && window.supabase.createClient ? resolve() : reject(new Error('Supabase library missing')); };
      sc.onerror = function () { reject(new Error('Could not load the Supabase library (offline?)')); };
      document.head.appendChild(sc);
    });
  }

  // ---------------- merge (ID-based union; never deletes) ----------------
  function clone(x) { return JSON.parse(JSON.stringify(x)); }
  function gChests(g) { return (g.ts ? g.ts.length : (g.archived || 0)) + (g.adj || 0); }
  var RANK = { active: 0, paused: 1, completed: 2, incomplete: 2, void: 3 };
  function pickGame(a, b) {
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
  function summary(s) { var c = s.importTotal || 0; Object.keys(s.sessions || {}).forEach(function (k) { c += s.sessions[k].chests || 0; }); return U.fmtInt(c) + ' chests in ' + (s.sessionOrder || []).length + ' sessions'; }

  // ---------------- server I/O ----------------
  async function fetchRemote() {
    var r = await client.from('tracker_state').select('state, version').eq('user_id', user.id).maybeSingle();
    if (r.error) throw r.error;
    return r.data ? { state: r.data.state, version: Number(r.data.version) } : null;
  }
  async function pushState(expected, st) {
    var r = await client.rpc('push_tracker_state', { expected_version: expected, new_state: st, device: st.deviceId || 'web' });
    if (r.error) throw r.error;
    var row = Array.isArray(r.data) ? r.data[0] : r.data;
    return { ok: !!row.ok, version: Number(row.version), state: row.state };
  }
  async function flush() {
    if (!client || !user || busy) { dirty = true; return; }
    var m = meta(); if (!m.linked || m.userId !== user.id) return;
    if (!navigator.onLine) { setState('offline'); return; }
    busy = true; dirty = false; setState('syncing');
    try {
      for (var attempt = 0; attempt < 4; attempt++) {
        var st = TK.tracker.state, res = await pushState(m.version, st);
        if (res.ok) { m.version = res.version; setMeta(m); lastSync = Date.now(); setState('synced', ''); break; }
        // someone else wrote first: merge theirs into ours and try again
        var remote = res.state ? { state: res.state, version: res.version } : await fetchRemote();
        TK.tracker.replaceState(merge(TK.tracker.state, remote.state), 'sync-merge');
        m.version = remote.version; setMeta(m);
      }
    } catch (e) { setState(navigator.onLine ? 'error' : 'offline', e.message || String(e)); dirty = true; }
    finally { busy = false; if (dirty && state !== 'error' && state !== 'offline') schedule(); }
  }
  function schedule() { clearTimeout(pushTimer); pushTimer = setTimeout(flush, 4000); if (state === 'synced') setState('pending'); }
  async function pull() {
    if (!client || !user || busy) return;
    var m = meta(); if (!m.linked || m.userId !== user.id) return;
    try {
      var remote = await fetchRemote();
      if (remote && remote.version > m.version) { TK.tracker.replaceState(merge(TK.tracker.state, remote.state), 'sync-pull'); m.version = remote.version; setMeta(m); schedule(); }
      lastSync = Date.now(); if (state !== 'pending') setState('synced', '');
    } catch (e) { setState(navigator.onLine ? 'error' : 'offline', e.message || String(e)); }
  }
  // First sign-in on this device: link local data and the account without overwriting either.
  async function link() {
    setState('syncing');
    var remote; try { remote = await fetchRemote(); } catch (e) { setState('error', e.message || String(e)); return; }
    var local = TK.tracker.state, m = meta();
    if (m.linked && m.userId === user.id) { await pull(); if (dirty) flush(); return; }
    if (!remote) {
      if (!hasData(local)) { var r0 = await pushState(0, local); setMeta({ userId: user.id, version: r0.version, linked: true }); setState('synced', ''); lastSync = Date.now(); return; }
      ui.modal({ title: 'Save your tracker to this account?', sub: 'This browser has guest data: ' + summary(local) + '. The account is new and empty.', body: '<p class="muted">Uploading keeps everything you tracked as a guest and syncs it from now on.</p>',
        actions: [{ label: 'Not now', kind: 'ghost', onClick: function () { setState('pending', 'Guest data not uploaded yet'); } },
          { label: 'Upload guest data', kind: 'gold', onClick: async function () { try { var r = await pushState(0, TK.tracker.state); setMeta({ userId: user.id, version: r.version, linked: true }); lastSync = Date.now(); setState('synced', ''); ui.toast('Guest data saved to your account.'); } catch (e) { setState('error', e.message); } } }] });
      return;
    }
    if (!hasData(local)) { TK.tracker.replaceState(remote.state, 'sync-adopt'); setMeta({ userId: user.id, version: remote.version, linked: true }); lastSync = Date.now(); setState('synced', ''); ui.toast('Your saved tracker was loaded from your account.'); return; }
    ui.modal({ title: 'Combine tracker data?', sub: 'Your account has ' + summary(remote.state) + '. This browser has ' + summary(local) + '.',
      body: '<p class="muted">Merging keeps every session, game and token find from both (nothing is overwritten). Or use only the account’s data — this browser’s copy is exported to a file first.</p>',
      actions: [{ label: 'Use account data only', kind: 'ghost', onClick: function () { U.download('tokenify-tracker-local-backup.json', TK.tracker.exportJSON(), 'application/json'); TK.tracker.replaceState(remote.state, 'sync-adopt'); setMeta({ userId: user.id, version: remote.version, linked: true }); setState('synced', ''); } },
        { label: 'Merge both', kind: 'gold', onClick: function () { TK.tracker.replaceState(merge(local, remote.state), 'sync-merge'); setMeta({ userId: user.id, version: remote.version, linked: true }); flush(); } }] });
  }

  // ---------------- account UI ----------------
  function field(label, type, ac) { var i = h('input', { type: type, autocomplete: ac, required: true }); return { row: h('div', { class: 'form-row' }, [h('label', { text: label }), i]), input: i }; }
  function emailOk(e) { return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(e); }
  function openAccount() {
    if (!configured()) {
      ui.modal({ title: 'Accounts & sync', sub: 'You’re in guest mode: everything is saved in this browser.',
        body: '<p>Accounts are optional and need a free Supabase project, which only the site owner can set up:</p><ol class="setup-steps"><li>Create a project at supabase.com and run <code>supabase/schema.sql</code> in its SQL editor.</li><li>Copy the project URL and the <b>anon public</b> key into <code>js/config.js</code>.</li><li>Serve TOKENIFY over http(s) (e.g. <code>start.bat</code>) and add that address to Supabase Auth › URL configuration.</li></ol><p class="fine">Full steps are in README.md. Until then, use Details › Data in the Live Tracker to export a backup.</p>',
        actions: [{ label: 'OK', kind: 'gold' }] });
      return;
    }
    if (!client) { ui.toast(lastError || 'Connecting to the account service…'); return; }
    if (user) return accountMenu();
    authForm('login');
  }
  function accountMenu() {
    var st = statusText();
    var body = h('div', {}, [h('p', {}, ['Signed in as ', h('b', { text: user.email })]), h('p', { class: 'muted', text: st })]);
    ui.modal({ title: 'Your account', body: body, actions: [
      { label: 'Delete account', kind: 'danger', onClick: function () { deleteAccount(); } },
      { label: 'Log out', kind: 'ghost', onClick: async function () { await client.auth.signOut(); user = null; setState('signed-out'); ui.toast('Logged out. Your data stays in this browser.'); } },
      { label: 'Sync now', kind: 'gold', onClick: function () { flush().then(pull); } }] });
  }
  function authForm(mode) {
    var email = field('Email', 'email', 'email'), pass = field('Password', 'password', mode === 'signup' ? 'new-password' : 'current-password'), conf = field('Confirm password', 'password', 'new-password');
    var msg = h('p', { class: 'msg-error', role: 'alert' });
    var body = h('form', { class: 'modal-body', novalidate: true }, [email.row, mode !== 'reset' ? pass.row : null, mode === 'signup' ? conf.row : null, msg]);
    var links = h('div', { class: 'auth-links' });
    function link(label, to) { var b = h('button', { type: 'button', class: 'link-btn' }, [label]); b.addEventListener('click', function () { api.close(); authForm(to); }); links.appendChild(b); }
    if (mode === 'login') { link('Create an account', 'signup'); link('Forgot password?', 'reset'); }
    else link('Back to log in', 'login');
    body.appendChild(links);
    async function submit() {
      msg.textContent = ''; msg.className = 'msg-error';
      var e = email.input.value.trim();
      if (!emailOk(e)) { msg.textContent = 'Enter a valid email address.'; return false; }
      try {
        if (mode === 'reset') {
          var rr = await client.auth.resetPasswordForEmail(e, { redirectTo: location.origin + location.pathname + '#tracker' });
          if (rr.error) throw rr.error; msg.className = 'msg-ok'; msg.textContent = 'If that address has an account, a reset link is on its way.'; return false;
        }
        var p = pass.input.value;
        if (mode === 'signup') {
          if (p.length < 8) { msg.textContent = 'Use at least 8 characters.'; return false; }
          if (p !== conf.input.value) { msg.textContent = 'Passwords don’t match.'; return false; }
          var su = await client.auth.signUp({ email: e, password: p, options: { emailRedirectTo: location.origin + location.pathname + '#tracker' } });
          if (su.error) throw su.error;
          if (!su.data.session) { msg.className = 'msg-ok'; msg.textContent = 'Check your inbox to confirm your email, then log in.'; return false; }
        } else {
          var si = await client.auth.signInWithPassword({ email: e, password: p });
          if (si.error) throw si.error;
        }
        api.close(); ui.toast('Signed in as ' + e + '.');
      } catch (err) { msg.textContent = friendly(err); }
      return false;
    }
    body.addEventListener('submit', function (ev) { ev.preventDefault(); submit(); });
    var api = ui.modal({ title: mode === 'signup' ? 'Create account' : mode === 'reset' ? 'Reset password' : 'Log in', sub: mode === 'signup' ? 'Sync your Live Tracker across devices.' : mode === 'reset' ? 'We’ll email you a secure reset link.' : 'Welcome back.', body: body,
      actions: [{ label: 'Cancel', kind: 'ghost' }, { label: mode === 'signup' ? 'Create account' : mode === 'reset' ? 'Send link' : 'Log in', kind: 'gold', keepOpen: true, onClick: function () { submit(); return false; } }] });
  }
  function friendly(err) {
    var m = (err && (err.message || err.error_description)) || String(err);
    if (/Invalid login credentials/i.test(m)) return 'That email and password don’t match.';
    if (/Email not confirmed/i.test(m)) return 'Please confirm your email first (check your inbox).';
    if (/already registered/i.test(m)) return 'An account with this email already exists. Try logging in.';
    if (/rate limit/i.test(m)) return 'Too many attempts. Please wait a minute and try again.';
    if (/Failed to fetch|NetworkError/i.test(m)) return 'Can’t reach the account service. Check your connection.';
    return m;
  }
  function openReset() {
    var pass = field('New password', 'password', 'new-password'), conf = field('Confirm password', 'password', 'new-password'), msg = h('p', { class: 'msg-error', role: 'alert' });
    ui.modal({ title: 'Choose a new password', body: h('div', {}, [pass.row, conf.row, msg]), actions: [{ label: 'Save password', kind: 'gold', keepOpen: true, onClick: async function (close) {
      if (pass.input.value.length < 8) { msg.textContent = 'Use at least 8 characters.'; return false; }
      if (pass.input.value !== conf.input.value) { msg.textContent = 'Passwords don’t match.'; return false; }
      var r = await client.auth.updateUser({ password: pass.input.value });
      if (r.error) { msg.textContent = friendly(r.error); return false; }
      close(); ui.toast('Password updated.');
    } }] });
  }
  function deleteAccount() {
    ui.confirm({ title: 'Delete your account?', message: 'This permanently deletes your account and its cloud copy of the tracker. Data saved in this browser stays until you reset it.', confirmLabel: 'Delete account', danger: true }).then(async function (ok) {
      if (!ok) return;
      var r = await client.rpc('delete_my_account');
      if (r.error) { ui.toast('Couldn’t delete the account: ' + friendly(r.error)); return; }
      await client.auth.signOut(); user = null; setMeta({ userId: null, version: 0, linked: false }); setState('signed-out'); ui.toast('Account deleted.');
    });
  }
  function statusText() {
    if (!configured()) return 'Guest mode — saved in this browser only. Accounts aren’t configured for this copy of TOKENIFY.';
    if (!user) return 'Guest mode — saved in this browser. Sign in to back up and sync.';
    if (state === 'synced') return 'Synced to your account' + (lastSync ? ' ' + Math.max(0, Math.round((Date.now() - lastSync) / 60000)) + ' min ago' : '') + '.';
    if (state === 'pending' || state === 'syncing') return 'Saved locally — syncing to your account…' + (lastError ? ' (' + lastError + ')' : '');
    if (state === 'offline') return 'Offline — changes are saved locally and will sync when you’re back online.';
    if (state === 'error') return 'Saved locally, but sync failed: ' + lastError;
    return 'Signed in.';
  }

  TK.sync = {
    init: async function () {
      setState('off');
      if (!configured()) return;
      try { await loadLib(); } catch (e) { setState('error', e.message); return; }
      try {
        client = window.supabase.createClient(cfg.url, cfg.anonKey, { auth: { persistSession: true, autoRefreshToken: true, detectSessionInUrl: true } });
      } catch (e) { setState('error', e.message); return; }
      client.auth.onAuthStateChange(function (event, session) {
        user = session ? session.user : null;
        if (event === 'PASSWORD_RECOVERY') openReset();
        if (event === 'SIGNED_IN' && user) setTimeout(link, 0);
        if (event === 'SIGNED_OUT') setState('signed-out');
      });
      var r = await client.auth.getSession();
      user = r.data && r.data.session ? r.data.session.user : null;
      if (user) link(); else setState('signed-out');
      window.addEventListener('online', function () { if (user) flush(); });
      window.addEventListener('focus', function () { if (user) pull(); });
      setInterval(function () { if (user && navigator.onLine) { if (dirty) flush(); else pull(); } }, 60000);
    },
    notifyChange: function () { if (client && user && meta().linked) { dirty = true; schedule(); } },
    openAccount: openAccount,
    statusText: statusText,
    user: function () { return user; },
    configured: configured,
    _merge: merge
  };
})(window.TK = window.TK || {});
