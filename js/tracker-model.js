/* TOKENIFY — Live Tracker data model.
 *
 * One authoritative record structure:
 *   game.ts      chest timestamps for that game (delta-encoded seconds) + game.adj manual corrections
 *   session.adj  chests recorded in a session but not assigned to any game
 *   importTotal  manually entered history from before tracking (no invented playtime)
 * Session and lifetime totals are always derived from those records (cached for O(1) updates and
 * re-verified on load). Voiding a game changes its statistics status only, never its chests.
 * Every mutation is saved immediately. Active time is accumulated from clock differences
 * (performance.now, cross-checked with the wall clock) and checkpointed every few seconds.
 */
(function (TK) {
  'use strict';
  var U = TK.util || {};
  var KEY = 'tracker.state', LIVE = 'tracker.live';
  var TOKEN_DEN = 137500;
  // Clock is swappable so the test suite can simulate time deterministically.
  var CLOCK = { wall: function () { return Date.now(); }, perf: function () { return (typeof performance !== 'undefined' ? performance.now() : Date.now()); } };
  var nowWall = function () { return CLOCK.wall(); };
  var nowPerf = function () { return CLOCK.perf(); };

  function uuid() { return U.uuid ? U.uuid() : 'id-' + Math.random().toString(36).slice(2) + Date.now().toString(36); }
  function blank() {
    return { v: 1, deviceId: uuid(), importTotal: 0, importedAt: null, sessions: {}, sessionOrder: [], games: {}, gameOrder: [], tokens: [], currentSid: null, currentGid: null, lastEndedGid: null, corrections: [], updatedAt: nowWall() };
  }
  // ---- compact chest timestamps ----
  function tsList(g) { if (!g.ts || !g.ts.length) return []; var out = [g.ts[0]]; for (var i = 1; i < g.ts.length; i++) out.push(out[i - 1] + g.ts[i]); return out; }
  function tsEncode(list) { if (!list.length) return []; var out = [list[0]]; for (var i = 1; i < list.length; i++) out.push(Math.max(0, list[i] - list[i - 1])); return out; }
  function tsLast(g) { if (!g.ts || !g.ts.length) return null; var s = 0; for (var i = 0; i < g.ts.length; i++) s += g.ts[i]; return s; }
  function gameChests(g) { return (g.ts ? g.ts.length : (g.archived || 0)) + (g.adj || 0); }

  function Model(storage) {
    this.store = storage || (U.storage);
    this.state = blank();
    this.listeners = [];
    this.lastPerf = null; this.lastWall = null; // running interval bookkeeping for the active session
    this.restored = null; this.excludedGap = null;
    this.readOnly = false;
  }
  Model.prototype.on = function (fn) { this.listeners.push(fn); };
  Model.prototype.emit = function (what) { var s = this; this.listeners.forEach(function (fn) { try { fn(what, s); } catch (e) { console.error(e); } }); };
  Model.prototype.save = function (what) {
    this.state.updatedAt = nowWall();
    if (this.store) this.store.set(KEY, this.state);
    this.emit(what || 'change');
  };
  Model.prototype.log = function (kind, text) {
    this.state.corrections.push({ t: nowWall(), kind: kind, text: text });
    if (this.state.corrections.length > 400) this.state.corrections.splice(0, this.state.corrections.length - 400);
  };

  // ---------------- load / recovery ----------------
  Model.prototype.load = function (opts) {
    opts = opts || {};
    var s = this.store ? this.store.get(KEY, null) : null;
    this.state = (s && s.v === 1) ? s : blank();
    this.recount();
    var sess = this.session();
    if (sess && sess.status === 'active') {
      // The page closed without pausing. Use the last trustworthy checkpoint, never the closed time.
      var live = this.store ? this.store.get(LIVE, null) : null;
      var before = sess.activeMs;
      if (live && live.sid === sess.id && live.activeMs > sess.activeMs) sess.activeMs = live.activeMs;
      var away = live ? Math.max(0, nowWall() - live.wall) : 0;
      if (opts.keepRunning && live && live.sid === sess.id) {
        sess.activeMs += away; this.markRunning();
        this.restored = { mode: 'running', awayMs: away, recoveredMs: sess.activeMs - before };
      } else if (opts.autoResume) {
        this.markRunning();
        this.restored = { mode: 'resumed', awayMs: away };
      } else {
        sess.status = 'paused';
        this.restored = { mode: 'paused', awayMs: away };
      }
      this.save('restore');
    } else if (sess && sess.status === 'paused') {
      this.restored = { mode: 'paused-earlier' };
    }
    return this;
  };
  Model.prototype.recount = function () {
    var st = this.state, self = this;
    Object.keys(st.sessions).forEach(function (sid) { st.sessions[sid].chests = st.sessions[sid].adj || 0; });
    st.gameOrder.forEach(function (gid) { var g = st.games[gid]; if (!g) return; g.chests = gameChests(g); if (st.sessions[g.sid]) st.sessions[g.sid].chests += g.chests; });
    st.sessionOrder = st.sessionOrder.filter(function (sid) { return st.sessions[sid]; });
    st.gameOrder = st.gameOrder.filter(function (gid) { return st.games[gid]; });
    // Sessions inferred from replays are always rebuilt from their matches: start, end, and time = the matches'
    // own lengths added up (never the gaps between them).
    st.sessionOrder.forEach(function (sid) {
      var s = st.sessions[sid]; if (!s.inferred) return;
      var games = st.gameOrder.map(function (gid) { return st.games[gid]; }).filter(function (g) { return g.sid === sid; });
      if (!games.length) return;
      s.start = Math.min.apply(null, games.map(function (g) { return g.start; }));
      s.end = Math.max.apply(null, games.map(function (g) { return g.end || g.start; }));
      s.activeMs = games.reduce(function (a, g) { return a + Math.max(0, (g.end || g.start) - g.start); }, 0);
      s.lastChestAt = s.end;
    });
    if (st.currentGid && (!st.games[st.currentGid] || st.games[st.currentGid].status !== 'active')) st.currentGid = null;
    if (st.currentSid && (!st.sessions[st.currentSid] || st.sessions[st.currentSid].status === 'completed')) st.currentSid = null;
    void self;
  };

  // ---------------- getters ----------------
  Model.prototype.session = function (sid) { return this.state.sessions[sid || this.state.currentSid] || null; };
  Model.prototype.game = function (gid) { return this.state.games[gid || this.state.currentGid] || null; };
  Model.prototype.status = function () { var s = this.session(); return s ? s.status : 'none'; };
  Model.prototype.sessionChests = function (sid) { var s = this.session(sid); return s ? s.chests : 0; };
  Model.prototype.gameChests = function (gid) { var g = this.game(gid); return g ? g.chests : 0; };
  Model.prototype.trackedTotal = function () { var st = this.state, t = 0; st.sessionOrder.forEach(function (sid) { t += st.sessions[sid].chests; }); return t; };
  Model.prototype.total = function () { return (this.state.importTotal || 0) + this.trackedTotal(); };
  Model.prototype.sessionNo = function (sid) { return this.state.sessionOrder.indexOf(sid) + 1; };
  Model.prototype.gameNo = function (gid) { return this.state.gameOrder.indexOf(gid) + 1; };
  Model.prototype.gamesOf = function (sid) { var st = this.state; return st.gameOrder.filter(function (gid) { return st.games[gid].sid === sid; }).map(function (gid) { return st.games[gid]; }); };
  Model.prototype.validGames = function () { var st = this.state; return st.gameOrder.map(function (gid) { return st.games[gid]; }).filter(function (g) { return g.status === 'completed'; }); };
  Model.prototype.activeMs = function (sid) {
    var s = this.session(sid); if (!s) return 0;
    var extra = 0;
    if (s.status === 'active' && s.id === this.state.currentSid && this.lastPerf != null) extra = Math.max(0, nowPerf() - this.lastPerf);
    return s.activeMs + extra;
  };

  // ---------------- clock ----------------
  Model.prototype.markRunning = function () { var s = this.session(); if (!s) return; s.status = 'active'; this.lastPerf = nowPerf(); this.lastWall = nowWall(); };
  // Called about once a second by the UI: folds elapsed time into the session, detecting suspensions.
  Model.prototype.tick = function () {
    var s = this.session(); if (!s || s.status !== 'active' || this.lastPerf == null) return null;
    var p = nowPerf(), w = nowWall(), dp = p - this.lastPerf, dw = w - this.lastWall, gap = null;
    var d = Math.max(0, Math.min(dp, dw + 1000));
    if (dw > 150000) { gap = dw; d = 0; } // >2.5 min without a tick: the device slept or the tab was frozen
    s.activeMs += d; this.lastPerf = p; this.lastWall = w;
    if (gap) { this.excludedGap = { ms: gap, sid: s.id }; this.log('time', 'Excluded ' + Math.round(gap / 60000) + ' min while the device appeared to be asleep'); this.save('gap'); }
    return gap;
  };
  Model.prototype.checkpoint = function () {
    var s = this.session(); if (!s || this.readOnly) return;
    if (s.status === 'active') this.tick();
    if (this.store) this.store.set(LIVE, { sid: s.id, activeMs: s.activeMs, wall: nowWall() });
  };
  Model.prototype.restoreGap = function () {
    var g = this.excludedGap, s = g && this.session(g.sid); if (!s) return;
    s.activeMs += g.ms; this.excludedGap = null; this.log('time', 'Added back ' + Math.round(g.ms / 60000) + ' min of excluded time'); this.save('time');
  };

  // ---------------- sessions ----------------
  Model.prototype.startSession = function () {
    var cur = this.session();
    if (cur && cur.status !== 'completed') return cur;
    var s = { id: uuid(), start: nowWall(), end: null, status: 'active', activeMs: 0, adj: 0, adjLog: [], timeLog: [], lastChestAt: null, chests: 0 };
    this.state.sessions[s.id] = s; this.state.sessionOrder.push(s.id);
    this.state.currentSid = s.id; this.state.currentGid = null; this.state.lastEndedGid = null;
    this.markRunning();
    this.restored = null;
    this.save('session-start');
    if (this.store) this.store.set(LIVE, { sid: s.id, activeMs: 0, wall: nowWall() });
    return s;
  };
  Model.prototype.pause = function () {
    var s = this.session(); if (!s || s.status !== 'active') return false;
    this.tick(); s.status = 'paused'; this.lastPerf = null;
    this.save('pause'); this.checkpoint();
    return true;
  };
  Model.prototype.resume = function () {
    var s = this.session(); if (!s || s.status !== 'paused') return false;
    this.markRunning(); this.restored = null;
    this.save('resume');
    return true;
  };
  // gameAction: 'finalize' (count the open game as completed) | 'incomplete' (keep it, excluded from CPG)
  Model.prototype.endSession = function (gameAction) {
    var s = this.session(); if (!s) return null;
    if (s.status === 'active') this.tick();
    var g = this.game();
    if (g && g.status === 'active') {
      g.end = nowWall();
      if (gameAction === 'finalize') { g.status = 'completed'; g.endKind = 'session-end'; }
      else { g.status = 'incomplete'; g.endKind = 'session-end'; }
    }
    s.status = 'completed'; s.end = nowWall();
    this.state.currentSid = null; this.state.currentGid = null; this.state.lastEndedGid = null; this.lastPerf = null; this.restored = null;
    this.save('session-end');
    if (this.store) this.store.remove(LIVE);
    return s;
  };

  // ---------------- chests ----------------
  // Returns { ok, game, autoStarted, autoGame } or { blocked: 'paused' }
  Model.prototype.addChest = function (opts) {
    opts = opts || {};
    var s = this.session(), autoStarted = false, autoGame = false;
    if (!s || s.status === 'completed') { s = this.startSession(); autoStarted = true; }
    if (s.status === 'paused') { if (!opts.autoResume) return { blocked: 'paused' }; this.resume(); }
    var g = this.game();
    var tSec = Math.floor(nowWall() / 1000);
    if (!g) { g = this._newGame(s, 'inferred', tSec * 1000); autoGame = true; }
    var list = g.ts || (g.ts = []);
    if (!list.length) list.push(tSec); else list.push(Math.max(0, tSec - tsLast(g)));
    g.chests += 1; s.chests += 1; s.lastChestAt = nowWall();
    // which chest this was (game + position), so listeners such as squad tracking can follow it
    this.lastAdded = { gid: g.id, sid: s.id, idx: list.length - 1, at: nowWall() };
    this.save('chest');
    return { ok: true, game: g, autoStarted: autoStarted, autoGame: autoGame };
  };
  Model.prototype._newGame = function (s, kind, startMs) {
    var g = { id: uuid(), sid: s.id, start: startMs || nowWall(), startKind: kind, end: null, endKind: null, status: 'active', ts: [], adj: 0, edits: [], chests: 0 };
    this.state.games[g.id] = g; this.state.gameOrder.push(g.id); this.state.currentGid = g.id;
    return g;
  };
  // Removes the most recent chest of the current session. Returns { ok, from: game|null, removedGame }
  Model.prototype.removeChest = function () {
    var s = this.session(); if (!s || s.chests <= 0) return { ok: false };
    var st = this.state, target = null;
    var cur = this.game();
    if (cur && cur.chests > 0) target = cur;
    else {
      var games = this.gamesOf(s.id).filter(function (x) { return x.chests > 0; });
      // most recent by last chest time
      games.sort(function (a, b) { return (tsLast(b) || 0) - (tsLast(a) || 0); });
      target = games[0] || null;
    }
    var removedGame = false;
    this.lastRemoved = target ? { gid: target.id, idx: target.ts && target.ts.length ? target.ts.length - 1 : null } : { gid: null, idx: null };
    if (target) {
      if (target.ts && target.ts.length) target.ts.pop(); else target.adj -= 1;
      target.chests -= 1; s.chests -= 1;
      if (target === cur && cur.chests === 0 && cur.startKind === 'inferred' && cur.status === 'active' && (!cur.adj)) {
        delete st.games[cur.id]; st.gameOrder = st.gameOrder.filter(function (id) { return id !== cur.id; }); st.currentGid = null; removedGame = true;
      }
    } else if (s.adj > 0) { s.adj -= 1; s.chests -= 1; }
    else return { ok: false };
    this.save('chest-remove');
    return { ok: true, from: removedGame ? null : target, removedGame: removedGame };
  };

  // ---------------- games ----------------
  Model.prototype.startGame = function () {
    var s = this.session();
    if (!s || s.status === 'completed') s = this.startSession();
    if (s.status === 'paused') this.resume();
    if (this.game()) return this.game();
    var g = this._newGame(s, 'explicit', nowWall());
    this.save('game-start');
    return g;
  };
  Model.prototype.endGame = function () {
    var g = this.game(); if (!g) return null;
    g.end = nowWall(); g.endKind = 'normal'; g.status = 'completed';
    this.state.currentGid = null; this.state.lastEndedGid = g.id;
    this.save('game-end');
    return g;
  };
  Model.prototype.canUndoEndGame = function () {
    var st = this.state, gid = st.lastEndedGid, g = st.games[gid];
    return !!(g && !st.currentGid && g.sid === st.currentSid && st.gameOrder[st.gameOrder.length - 1] === gid && g.status === 'completed');
  };
  Model.prototype.undoEndGame = function () {
    if (!this.canUndoEndGame()) return false;
    var g = this.state.games[this.state.lastEndedGid];
    g.status = 'active'; g.end = null; g.endKind = null;
    this.state.currentGid = g.id; this.state.lastEndedGid = null;
    this.log('game', 'Reopened game ' + this.gameNo(g.id) + ' (undo end)');
    this.save('game-undo');
    return true;
  };
  Model.prototype.voidGame = function (gid) {
    var g = this.state.games[gid]; if (!g || g.status === 'void') return false;
    g.prevStatus = g.status;
    if (g.status === 'active') { g.end = nowWall(); this.state.currentGid = null; }
    g.status = 'void';
    this.log('game', 'Voided game ' + this.gameNo(gid) + ' (' + g.chests + ' chests kept in totals)');
    this.save('game-void');
    return true;
  };
  Model.prototype.restoreGame = function (gid) {
    var g = this.state.games[gid]; if (!g || g.status !== 'void') return false;
    g.status = g.prevStatus === 'incomplete' ? 'incomplete' : 'completed';
    if (!g.end) g.end = (tsLast(g) || Math.floor(g.start / 1000)) * 1000;
    delete g.prevStatus;
    this.log('game', 'Restored game ' + this.gameNo(gid));
    this.save('game-restore');
    return true;
  };
  Model.prototype.markComplete = function (gid) { // turn an incomplete game into a valid completed one
    var g = this.state.games[gid]; if (!g || g.status !== 'incomplete') return false;
    g.status = 'completed'; this.log('game', 'Marked game ' + this.gameNo(gid) + ' as complete'); this.save('game-edit'); return true;
  };
  // edit: { chestDelta, start (ms), end (ms), reason }
  Model.prototype.editGame = function (gid, edit) {
    var g = this.state.games[gid]; if (!g) return { error: 'missing' };
    var s = this.state.sessions[g.sid];
    if (edit.chestDelta) {
      var d = Math.round(edit.chestDelta);
      if (g.chests + d < 0) return { error: 'A game cannot have fewer than 0 chests.' };
      g.adj = (g.adj || 0) + d; g.chests += d; if (s) s.chests += d;
      g.edits.push({ t: nowWall(), kind: 'chests', delta: d, reason: edit.reason || '' });
      this.log('chests', (d > 0 ? 'Added ' : 'Removed ') + Math.abs(d) + ' chest(s) ' + (d > 0 ? 'to' : 'from') + ' game ' + this.gameNo(gid) + (edit.reason ? ' — ' + edit.reason : ''));
    }
    if (edit.start != null && edit.start !== g.start) { if (g.end && edit.start > g.end) return { error: 'Start must be before end.' }; g.start = edit.start; g.startKind = 'edited'; g.edits.push({ t: nowWall(), kind: 'start' }); }
    if (edit.end != null && edit.end !== g.end) { if (edit.end < g.start) return { error: 'End must be after start.' }; g.end = edit.end; g.endKind = 'edited'; g.edits.push({ t: nowWall(), kind: 'end' }); }
    if (edit.start != null || edit.end != null) this.log('game', 'Edited times of game ' + this.gameNo(gid));
    this.save('game-edit');
    return { ok: true };
  };
  // Split game gid before chest index `at` (1..n-1). Chests are moved, never duplicated or dropped.
  Model.prototype.splitPreview = function (gid, at) {
    var g = this.state.games[gid], list = tsList(g);
    if (!g || at < 1 || at >= list.length) return null;
    return { a: { chests: at + (g.adj || 0), from: list[0], to: list[at - 1] }, b: { chests: list.length - at, from: list[at], to: list[list.length - 1] } };
  };
  Model.prototype.splitGame = function (gid, at) {
    var st = this.state, g = st.games[gid], list = tsList(g);
    if (!g || at < 1 || at >= list.length) return { error: 'Choose a split point between two chests.' };
    var first = list.slice(0, at), second = list.slice(at);
    var ng = { id: uuid(), sid: g.sid, start: second[0] * 1000, startKind: 'inferred', end: g.end, endKind: g.endKind, status: g.status === 'active' ? 'active' : (g.status === 'void' ? 'void' : 'completed'), ts: tsEncode(second), adj: 0, edits: [{ t: nowWall(), kind: 'split-from', from: gid }], chests: second.length };
    if (g.status === 'void') ng.prevStatus = g.prevStatus;
    g.ts = tsEncode(first); g.chests = first.length + (g.adj || 0);
    g.end = first[first.length - 1] * 1000; g.endKind = 'edited';
    if (g.status === 'active') { g.status = 'completed'; st.currentGid = ng.id; }
    g.edits.push({ t: nowWall(), kind: 'split', at: at });
    st.games[ng.id] = ng;
    st.gameOrder.splice(st.gameOrder.indexOf(gid) + 1, 0, ng.id);
    this.log('game', 'Split game ' + this.gameNo(gid) + ' into ' + first.length + ' + ' + second.length + ' chests');
    this.save('game-split');
    return { ok: true, newGame: ng };
  };
  Model.prototype.endGameAt = function (gid, endMs, kind) { var g = this.state.games[gid]; if (!g || g.status !== 'active') return; g.end = endMs; g.endKind = kind || 'inferred'; g.status = 'completed'; this.state.currentGid = null; this.state.lastEndedGid = gid; this.save('game-end'); };
  Model.prototype.gameTimes = function (gid) { var g = this.state.games[gid]; return g ? tsList(g).map(function (s) { return s * 1000; }) : []; };
  Model.prototype.gameLastChest = function (gid) { var g = this.state.games[gid]; var l = g && tsLast(g); return l ? l * 1000 : null; };

  // ---------------- automatic PC tracking (matches imported from Fortnite replays) ----------------
  // An imported match becomes a normal game, so totals, CPG, Last 10 and token chances need no special cases.
  // Its ids are derived from the server's ids, so every browser builds the very same game and session and
  // syncing never duplicates it. A match never adds on top of a game you counted by hand: that game is
  // reconciled instead (your manual count is kept for reference).
  Model.prototype.replayGame = function (matchId) {
    var st = this.state;
    for (var i = st.gameOrder.length - 1; i >= 0; i--) { var g = st.games[st.gameOrder[i]]; if (g && g.replay && g.replay.id === matchId) return g; }
    return null;
  };
  Model.prototype.pendingReplays = function () {
    var st = this.state;
    return st.gameOrder.map(function (gid) { return st.games[gid]; }).filter(function (g) { return g && g.replay && g.replay.status === 'pending'; });
  };
  function replayTimes(m) {
    var base = Math.floor(Date.parse(m.started_at) / 1000);
    return (m.chest_times || []).map(function (t) { return base + Math.floor(Number(t) || 0); }).sort(function (a, b) { return a - b; });
  }
  function setCounts(g, times, chests) {
    var effective = Math.max(0, Math.round(chests));
    g.ts = tsEncode(times);
    g.adj = effective - times.length;          // a correction made on the website shifts the count, keeps the times
    g.chests = gameChests(g);
  }
  function setReplayCounts(g, m) { setCounts(g, replayTimes(m), m.chests); }
  function replayMeta(m, extra) {
    var r = { id: m.id, key: m.match_key, v: m.updated_at, chests: m.chests, replayChests: m.replay_chests, corrected: m.corrected_chests,
      historical: !!m.historical, party: m.party ? m.party.link : null };
    Object.keys(extra || {}).forEach(function (k) { r[k] = extra[k]; });
    return r;
  }
  // mode: 'auto' (replay counts are final) or 'hybrid' (you count live; the replay then checks it).
  // Returns { action: 'created' | 'updated' | 'reconciled' | 'pending' | 'removed' | 'none', game }.
  Model.prototype.applyReplay = function (m, mode) {
    var st = this.state, g = this.replayGame(m.id), now = nowWall();
    var startMs = Date.parse(m.started_at), endMs = Date.parse(m.ended_at);
    if (!isFinite(startMs) || !isFinite(endMs)) return { action: 'none' };
    if (m.excluded) return g ? this.removeReplay(m.id) : { action: 'none' };
    if (g) {
      if (g.replay.v === m.updated_at) return { action: 'none', game: g };
      var keep = { manual: g.replay.manual, status: g.replay.status, decidedAt: g.replay.decidedAt, at: g.replay.at };
      if (g.replay.manual) keep.times = replayTimes(m);
      g.replay = replayMeta(m, keep);
      if (g.replay.status !== 'pending' && g.replay.status !== 'kept-manual') setReplayCounts(g, m);
      this.recount(); this.save('replay');
      return { action: 'updated', game: g };
    }
    // 1) a game you counted by hand during this match: reconcile, never add on top
    var lo = Math.floor(startMs / 1000) - 90, hi = Math.floor(endMs / 1000) + 90;
    var overlap = st.gameOrder.map(function (gid) { return st.games[gid]; }).filter(function (x) {
      if (!x || x.replay || x.status === 'void') return false;
      var t = tsList(x);
      if (t.length) return t.filter(function (s) { return s >= lo && s <= hi; }).length * 2 >= t.length;
      var xe = x.end || x.start;
      return x.start <= endMs && xe >= startMs;
    });
    if (overlap.length === 1) {
      var mg = overlap[0];
      mg.replay = replayMeta(m, { manual: { ts: (mg.ts || []).slice(), adj: mg.adj || 0, chests: mg.chests }, at: now,
        status: mode === 'hybrid' ? 'pending' : 'applied', times: replayTimes(m) });
      if (mode !== 'hybrid') {
        setReplayCounts(mg, m);
        mg.edits.push({ t: now, kind: 'replay', from: mg.replay.manual.chests, to: mg.chests });
        this.log('replay', 'Game ' + this.gameNo(mg.id) + ' checked against its Fortnite replay: ' + mg.replay.manual.chests + ' counted by hand, ' + mg.chests + ' in the replay');
      }
      if (mg.status === 'active') { mg.end = endMs; mg.endKind = 'replay'; mg.status = 'completed'; if (st.currentGid === mg.id) st.currentGid = null; st.lastEndedGid = mg.id; }
      this.recount(); this.save('replay');
      return { action: mode === 'hybrid' ? 'pending' : 'reconciled', game: mg };
    }
    // 2) otherwise its own game: in the session you were running at the time, or in a session inferred from replays
    var sid = null;
    st.sessionOrder.forEach(function (id) {
      var s = st.sessions[id];
      if (!s.inferred && !m.historical && s.start <= startMs + 60000 && (s.end == null || s.end >= endMs - 60000)) sid = id;
    });
    if (!sid) {
      sid = 'rs-' + m.auto_session;
      if (!st.sessions[sid]) {
        st.sessions[sid] = { id: sid, start: startMs, end: endMs, status: 'completed', activeMs: 0, adj: 0, adjLog: [], timeLog: [], lastChestAt: endMs, chests: 0,
          inferred: true, historical: !!m.historical };
        st.sessionOrder.push(sid);
        st.sessionOrder.sort(function (a, b) { return st.sessions[a].start - st.sessions[b].start; });
      }
    }
    var ng = { id: 'rp-' + m.id, sid: sid, start: startMs, startKind: 'replay', end: endMs, endKind: 'replay', status: 'completed', ts: [], adj: 0, edits: [], chests: 0,
      replay: replayMeta(m, { at: now, status: 'applied' }) };
    setReplayCounts(ng, m);
    st.games[ng.id] = ng;
    var at = st.gameOrder.length;
    while (at > 0 && st.games[st.gameOrder[at - 1]] && st.games[st.gameOrder[at - 1]].start > startMs) at--;
    st.gameOrder.splice(at, 0, ng.id);
    this.recount(); this.save('replay');
    return { action: 'created', game: ng };
  };
  // Hybrid: keep the replay's count (your manual one is kept for reference) or keep what you counted.
  Model.prototype.resolveReplay = function (gid, useReplay) {
    var g = this.state.games[gid]; if (!g || !g.replay || !g.replay.manual) return false;
    g.replay.status = useReplay ? 'applied' : 'kept-manual';
    g.replay.decidedAt = nowWall();
    if (useReplay) setCounts(g, g.replay.times || [], g.replay.chests);
    else { g.ts = g.replay.manual.ts.slice(); g.adj = g.replay.manual.adj; }
    g.edits.push({ t: nowWall(), kind: 'replay', from: g.replay.manual.chests, to: useReplay ? g.replay.chests : g.replay.manual.chests });
    this.recount();
    this.log('replay', 'Game ' + this.gameNo(gid) + ': ' + (useReplay ? 'replay count used (' + g.replay.chests + ', you counted ' + g.replay.manual.chests + ')' : 'kept your own count (' + g.replay.manual.chests + ', replay said ' + g.replay.chests + ')'));
    this.save('replay');
    return true;
  };
  // "Not my match": the game disappears; a game you had counted by hand gets its own count back.
  Model.prototype.removeReplay = function (matchId) {
    var st = this.state, g = this.replayGame(matchId); if (!g) return { action: 'none' };
    if (g.id.indexOf('rp-') === 0) {
      var sid = g.sid;
      delete st.games[g.id];
      st.gameOrder = st.gameOrder.filter(function (id) { return id !== g.id; });
      var s = st.sessions[sid];
      if (s && s.inferred && !st.gameOrder.some(function (id) { return st.games[id] && st.games[id].sid === sid; })) {
        delete st.sessions[sid]; st.sessionOrder = st.sessionOrder.filter(function (id) { return id !== sid; });
      }
    } else {
      if (g.replay.manual) { g.ts = g.replay.manual.ts; g.adj = g.replay.manual.adj; }
      delete g.replay;
    }
    this.recount(); this.save('replay');
    return { action: 'removed' };
  };

  // ---------------- corrections ----------------
  // target: 'game' (current or most recent game in the session) | 'session' (not assigned to a game)
  Model.prototype.adjustChests = function (delta, target, reason) {
    var s = this.session(); if (!s) return { error: 'Start a session first.' };
    delta = Math.round(delta); if (!delta) return { error: 'Enter a non-zero amount.' };
    if (s.chests + delta < 0) return { error: 'The session would go below zero chests.' };
    if (target === 'game') {
      var g = this.game() || this.gamesOf(s.id).slice(-1)[0];
      if (!g) { g = this._newGame(s, 'inferred', nowWall()); }
      if (g.chests + delta < 0) return { error: 'That game only has ' + g.chests + ' chests.' };
      g.adj = (g.adj || 0) + delta; g.chests += delta; s.chests += delta;
      g.edits.push({ t: nowWall(), kind: 'chests', delta: delta, reason: reason || '' });
      this.log('chests', (delta > 0 ? 'Added ' : 'Removed ') + Math.abs(delta) + ' chest(s) via correction (game ' + this.gameNo(g.id) + ')' + (reason ? ' — ' + reason : ''));
    } else {
      if (s.adj + delta < 0) return { error: 'Only ' + s.adj + ' unassigned chests can be removed.' };
      s.adj += delta; s.chests += delta; s.adjLog.push([nowWall(), delta, reason || '']);
      this.log('chests', (delta > 0 ? 'Added ' : 'Removed ') + Math.abs(delta) + ' unassigned chest(s) to session ' + this.sessionNo(s.id) + (reason ? ' — ' + reason : ''));
    }
    this.save('adjust');
    return { ok: true };
  };
  Model.prototype.correctTime = function (sid, deltaMs, reason) {
    var s = this.session(sid); if (!s) return { error: 'No such session.' };
    if (s.status === 'active' && s.id === this.state.currentSid) this.tick();
    if (s.activeMs + deltaMs < 0) return { error: 'Active time cannot go below zero.' };
    s.activeMs += deltaMs; s.timeLog.push([nowWall(), deltaMs, reason || '']);
    this.log('time', (deltaMs < 0 ? 'Removed ' : 'Added ') + Math.round(Math.abs(deltaMs) / 60000) + ' min of active time in session ' + this.sessionNo(s.id) + (reason ? ' — ' + reason : ''));
    this.save('time');
    return { ok: true };
  };
  // edit a completed session's chests (unassigned adjustment) with a reason
  Model.prototype.adjustSessionChests = function (sid, delta, reason) {
    var s = this.session(sid); if (!s) return { error: 'No such session.' };
    delta = Math.round(delta); if (!delta) return { error: 'Enter a non-zero amount.' };
    if (s.adj + delta < 0) return { error: 'Only ' + s.adj + ' manually added chests can be removed here; edit individual games instead.' };
    s.adj += delta; s.chests += delta; s.adjLog.push([nowWall(), delta, reason || '']);
    this.log('chests', 'Edited session ' + this.sessionNo(sid) + ' by ' + (delta > 0 ? '+' : '') + delta + ' chests' + (reason ? ' — ' + reason : ''));
    this.save('adjust');
    return { ok: true };
  };
  Model.prototype.setImportTotal = function (n) {
    n = Math.max(0, Math.round(n));
    var before = this.state.importTotal || 0;
    this.state.importTotal = n; this.state.importedAt = nowWall();
    this.log('import', 'Starting total set to ' + n + ' (was ' + before + ')');
    this.save('import');
  };

  // ---------------- Pirate Token finds (one per player) ----------------
  Model.prototype.tokenRecord = function () { return this.state.tokens.filter(function (t) { return !t.undone; })[0] || null; };
  Model.prototype.recordToken = function () {
    if (this.tokenRecord()) return { error: 'already' };
    var s = this.session(), g = this.game();
    var rec = { id: uuid(), t: nowWall(), sid: s ? s.id : null, gid: g ? g.id : null, sessionChests: s ? s.chests : 0, totalChests: this.total() };
    this.state.tokens.push(rec);
    this.log('token', 'Recorded a Pirate Token find at chest ' + rec.totalChests);
    this.save('token');
    return { ok: true, record: rec };
  };
  Model.prototype.undoToken = function (id) {
    var t = this.state.tokens.filter(function (x) { return x.id === id && !x.undone; })[0]; if (!t) return false;
    t.undone = true; this.log('token', 'Removed a Pirate Token record'); this.save('token'); return true;
  };
  Model.prototype.chestsSinceToken = function () { var t = this.tokenRecord(); return t ? Math.max(0, this.total() - t.totalChests) : this.total(); };

  // ---------------- statistics ----------------
  function cpgOf(games) { if (!games.length) return null; var c = 0; games.forEach(function (g) { c += g.chests; }); return { cpg: c / games.length, games: games.length, chests: c }; }
  Model.prototype.cpgAll = function () { return cpgOf(this.validGames()); };
  Model.prototype.cpgRecent = function (n) { var v = this.validGames(); return cpgOf(v.slice(Math.max(0, v.length - (n || 10)))); };
  Model.prototype.cpgPrevious = function (n) { n = n || 10; var v = this.validGames(); if (v.length <= n) return null; return cpgOf(v.slice(Math.max(0, v.length - 2 * n), v.length - n)); };
  Model.prototype.cpgSession = function (sid) { var sId = sid || this.state.currentSid; return cpgOf(this.validGames().filter(function (g) { return g.sid === sId; })); };
  Model.prototype.bestGame = function () { var v = this.validGames(); if (!v.length) return null; return v.reduce(function (a, b) { return b.chests > a.chests ? b : a; }); };
  Model.prototype.gameDuration = function (g) { if (!g.end) return null; return Math.max(0, g.end - g.start); };
  // Chests per hour. basis: 'session' (unpaused session time) | 'recent10' | 'recent30' | 'games' (recorded game time)
  Model.prototype.rate = function (basis) {
    var s = this.session(); if (!s) return { ready: false, reason: 'no-session' };
    var act = this.activeMs();
    if (basis === 'games') {
      var gs = this.validGames().filter(function (g) { return g.end && g.startKind !== 'inferred'; });
      var ms = 0, c = 0; gs.forEach(function (g) { ms += g.end - g.start; c += g.chests; });
      if (gs.length < 2 || ms < 5 * 60000) return { ready: false, reason: 'games', basis: basis };
      return { ready: true, perHour: c / (ms / 3600000), basis: basis, label: 'recorded game time (' + gs.length + ' games with exact starts)' };
    }
    if (basis === 'recent10' || basis === 'recent30') {
      var win = basis === 'recent10' ? 600000 : 1800000, since = nowWall() - win, count = 0, self = this;
      this.gamesOf(s.id).forEach(function (g) { self.gameTimes(g.id).forEach(function (t) { if (t >= since) count++; }); });
      var span = Math.min(win, act);
      if (span < 3 * 60000 || count < 3) return { ready: false, reason: 'warming', basis: basis };
      return { ready: true, perHour: count / (span / 3600000), basis: basis, label: 'last ' + (win / 60000) + ' minutes' };
    }
    if (act < 3 * 60000 || s.chests < 5) return { ready: false, reason: 'warming', basis: 'session' };
    return { ready: true, perHour: s.chests / (act / 3600000), basis: 'session', label: 'this session’s unpaused time' };
  };

  // ---------------- data management ----------------
  Model.prototype.exportJSON = function () { return JSON.stringify({ app: 'TOKENIFY', kind: 'live-tracker', exported: new Date().toISOString(), state: this.state }, null, 1); };
  Model.prototype.importJSON = function (text) {
    var obj; try { obj = JSON.parse(text); } catch (e) { return { error: 'That file is not valid JSON.' }; }
    var s = obj && obj.state; if (!s || s.v !== 1 || typeof s.sessions !== 'object' || !Array.isArray(s.gameOrder)) return { error: 'That file is not a TOKENIFY tracker export.' };
    this.state = s; this.lastPerf = null;
    var cur = this.session(); if (cur && cur.status === 'active') cur.status = 'paused';
    this.recount(); this.log('import', 'Imported tracker data from a file'); this.save('import');
    return { ok: true };
  };
  Model.prototype.replaceState = function (s, why) { this.state = s; this.recount(); var cur = this.session(); if (cur && cur.status === 'active' && this.lastPerf == null) this.markRunning(); this.save(why || 'replace'); };
  Model.prototype.resetAll = function () { this.state = blank(); this.lastPerf = null; this.restored = null; if (this.store) { this.store.remove(LIVE); } this.save('reset'); };
  // Archive per-chest timestamps of old games to keep storage bounded (counts are kept exactly).
  Model.prototype.compact = function (keepGames) {
    var st = this.state, keep = keepGames || 400, n = st.gameOrder.length, changed = false;
    for (var i = 0; i < n - keep; i++) { var g = st.games[st.gameOrder[i]]; if (g && g.ts && g.ts.length) { g.archived = g.ts.length; g.ts = null; changed = true; } }
    if (changed) this.save('compact');
  };

  Model.TOKEN_DEN = TOKEN_DEN;
  // Expected additional chests until the next token is always den/num = 137,500 (memoryless):
  // previous unsuccessful chests are never subtracted.
  Model.expectedChestsToToken = function () { return TOKEN_DEN; };
  Model.etaHours = function (perHour) { return perHour > 0 ? TOKEN_DEN / perHour : Infinity; };
  Model.clock = CLOCK;
  Model._ts = { list: tsList, encode: tsEncode };
  TK.TrackerModel = Model;
})(typeof window !== 'undefined' ? (window.TK = window.TK || {}) : {});
