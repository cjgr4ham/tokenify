/* TOKENIFY — connection to the backend (Supabase): one client, RPC calls with clear errors, the server
 * clock, and live-update subscriptions. Every request goes through the tk_* database functions, which
 * check the caller's session themselves; nothing here is trusted for security. */
(function (TK) {
  'use strict';
  var U = TK.util, cfg = TK.config.SUPABASE || {};
  var LIB = 'js/vendor/supabase-2.118.0.js';
  var client = null, libPromise = null, offset = 0;

  function configured() { return !!(cfg.url && cfg.anonKey); }
  function ApiError(kind, code, message, status) { var e = new Error(message || code); e.kind = kind; e.code = code; e.status = status; return e; }
  function netErr(m) { return ApiError('network', 'network', m || 'Network error'); }
  function isNetMsg(m) { return /Failed to fetch|NetworkError|Load failed|fetch failed|ERR_INTERNET|network/i.test(String(m || '')); }

  function loadLib() {
    if (window.supabase && window.supabase.createClient) return Promise.resolve();
    if (libPromise) return libPromise;
    libPromise = new Promise(function (resolve, reject) {
      var sc = document.createElement('script'); sc.src = LIB; sc.async = true;
      sc.onload = function () { if (window.supabase && window.supabase.createClient) resolve(); else { libPromise = null; reject(netErr('The sign-in library did not load.')); } };
      sc.onerror = function () { libPromise = null; reject(netErr('Could not load the sign-in library (offline?).')); };
      document.head.appendChild(sc);
    });
    return libPromise;
  }

  async function getClient() {
    if (client) return client;
    if (!configured()) throw ApiError('config', 'not_configured', 'Accounts are not configured for this copy of TOKENIFY.');
    await loadLib();
    client = window.supabase.createClient(cfg.url, cfg.anonKey, {
      auth: { persistSession: true, autoRefreshToken: true, detectSessionInUrl: false },
      realtime: { params: { eventsPerSecond: 5 } }
    });
    return client;
  }

  async function session() {
    var c = await getClient();
    var r = await c.auth.getSession();
    if (r && r.error && isNetMsg(r.error.message)) throw netErr(r.error.message);
    return r && r.data ? r.data.session : null;
  }

  function mapAuthError(err) {
    var m = String(err && (err.message || err.msg || err.error_description) || err);
    if (/anonymous sign-ins are disabled/i.test(m)) return ApiError('server', 'signin_unavailable', m);
    if (/rate limit|too many/i.test(m)) return ApiError('server', 'rate_limited', m);
    if (isNetMsg(m) || (err && err.status === 0)) return netErr(m);
    return ApiError('server', 'auth_error', m);
  }

  // The browser's transport session (a throwaway anonymous Supabase session) used for signing in,
  // signing up and recovery. fresh=true discards whatever is there first.
  async function transport(fresh) {
    var c = await getClient();
    if (fresh) { try { await c.auth.signOut({ scope: 'local' }); } catch (e) { /* nothing to discard */ } }
    var s = fresh ? null : await session();
    if (s) return s;
    var r = await c.auth.signInAnonymously();
    if (r.error) throw mapAuthError(r.error);
    return r.data.session;
  }

  async function dropTransport() {
    if (!client) return;
    try { await client.auth.signOut({ scope: 'local' }); } catch (e) { /* already gone */ }
  }

  // Calls a tk_* function. Expected failures come back as { ok: false, error } (never thrown); network
  // problems throw kind 'network'; a dead session throws kind 'auth' and announces 'auth-lost'.
  async function rpc(fn, args, opts) {
    opts = opts || {};
    var c = await getClient();
    var attempts = opts.retry ? 3 : 1, last = null;
    for (var i = 0; i < attempts; i++) {
      try {
        var r = await c.rpc(fn, args || {});
        if (r.error) {
          var msg = r.error.message || '';
          if (/TK:not_signed_in/.test(msg) || r.error.code === '42501' || r.status === 401 || r.error.code === 'PGRST301') {
            if (!opts.quietAuth) U.bus.emit('auth-lost', fn);
            throw ApiError('auth', 'not_signed_in', 'You are signed out.', r.status);
          }
          if (!r.status || isNetMsg(msg)) throw netErr(msg);
          throw ApiError('server', r.error.code || 'server_error', msg, r.status);
        }
        var d = r.data;
        if (d && d.server_time) setServerTime(d.server_time);
        return d;
      } catch (e) {
        last = e && e.kind ? e : netErr(String(e && e.message || e));
        if (last.kind !== 'network' || i === attempts - 1) throw last;
        await U.wait(700 * (i + 1));
      }
    }
    throw last;
  }

  function setServerTime(iso) { var t = Date.parse(iso); if (!isNaN(t)) offset = t - Date.now(); }

  // Live updates: listens for changes to one row of a signal table. Falls back silently (the caller
  // also polls) when realtime is unavailable, e.g. on the local test backend.
  function watch(table, column, value, onChange) {
    var state = { live: false, channel: null, stopped: false };
    var api = { stop: function () { state.stopped = true; if (state.channel && client) { try { client.removeChannel(state.channel); } catch (e) { /* gone */ } } }, live: function () { return state.live; } };
    if (!cfg.realtime || !value) return api;
    (async function () {
      try {
        var c = await getClient(), s = await session();
        if (!s || state.stopped) return;
        try { await c.realtime.setAuth(s.access_token); } catch (e) { /* older realtime clients set it themselves */ }
        state.channel = c.channel('tk:' + table + ':' + value)
          .on('postgres_changes', { event: '*', schema: 'public', table: table, filter: column + '=eq.' + value }, function () { onChange(); })
          .subscribe(function (status) { state.live = status === 'SUBSCRIBED'; U.bus.emit('realtime-status', { table: table, status: status }); });
      } catch (e) { state.live = false; }
    })();
    return api;
  }

  function deviceLabel() {
    var ua = navigator.userAgent || '';
    var b = /Edg\//.test(ua) ? 'Edge' : /OPR\//.test(ua) ? 'Opera' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
    var o = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Android/.test(ua) ? 'Android' : /Windows/.test(ua) ? 'Windows' : /Mac OS X/.test(ua) ? 'Mac' : /CrOS/.test(ua) ? 'ChromeOS' : /Linux/.test(ua) ? 'Linux' : '';
    return b + (o ? ' on ' + o : '');
  }

  var MESSAGES = {
    network: 'Can’t reach TOKENIFY’s servers. Check your connection and try again.',
    not_configured: 'Accounts aren’t configured for this copy of TOKENIFY.',
    signin_unavailable: 'Signing in is temporarily unavailable. Please try again later.',
    rate_limited: 'Too many requests. Please wait a minute and try again.',
    too_many_attempts: 'Too many failed attempts. Wait 15 minutes, or reset your password with a recovery code.',
    bad_credentials: 'Wrong username or password.',
    bad_password: 'That isn’t your current password.',
    username_invalid: 'Usernames are 3–16 letters or numbers, and may use _ . or - between them.',
    username_taken: 'That username is taken.',
    password_short: 'Use at least 8 characters.',
    password_long: 'Use at most 64 characters.',
    password_has_username: 'Your password can’t contain your username.',
    password_common: 'That password is too common. Pick something harder to guess.',
    recovery_failed: 'That username and recovery code don’t match an unused code.',
    not_signed_in: 'You’re signed out. Please log in again.',
    already_in_party: 'You’re already in a party. Leave it first.',
    party_full: 'That party is full (4 players).',
    party_gone: 'That party no longer exists.',
    invalid_code: 'That party code isn’t valid (or has expired).',
    not_leader: 'Only the party leader can do that.',
    not_friends: 'You can only invite friends.',
    unavailable: 'That player isn’t available.',
    not_found: 'That no longer exists. It may have been cancelled.',
    expired: 'That invite has expired.',
    no_party: 'You’re not in a party.',
    self: 'That’s you!',
    friend_limit: 'Friend list is full.',
    too_many_pending: 'Too many pending requests. Wait for some answers first.',
    not_participant: 'Only players from that game can change it.',
    game_active: 'Wait until the squad game has ended.',
    no_slot: 'A squad has at most four players.',
    invalid_count: 'Enter a whole number between 0 and 999.',
    invalid_estimates: 'Use percentages between 0 and 200.',
    session_stale: 'Your sign-in session expired. Please try again.',
    already_signed_in: 'You’re already signed in.'
  };
  function message(codeOrErr) {
    var code = typeof codeOrErr === 'string' ? codeOrErr : (codeOrErr && (codeOrErr.code || codeOrErr.error)) || '';
    return MESSAGES[code] || (codeOrErr && codeOrErr.message) || 'Something went wrong. Please try again.';
  }

  TK.api = {
    configured: configured, client: getClient, session: session, transport: transport, dropTransport: dropTransport,
    rpc: rpc, watch: watch, deviceLabel: deviceLabel, message: message, local: !!cfg.local, realtime: !!cfg.realtime,
    now: function () { return Date.now() + offset; }, serverOffset: function () { return offset; }
  };
})(window.TK = window.TK || {});
