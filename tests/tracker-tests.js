/* Live Tracker data-model tests (deterministic clock + in-memory storage). */
(function () {
  'use strict';
  var test = T.test, assert = T.assert, close = T.close;
  var M = TK.TrackerModel, P = TK.prob;

  function memStore() {
    var d = {};
    return { data: d, get: function (k, def) { return k in d ? JSON.parse(d[k]) : def; }, set: function (k, v) { d[k] = JSON.stringify(v); }, remove: function (k) { delete d[k]; } };
  }
  var fake = { wall: 1700000000000, perf: 1000 };
  function useFakeClock() { M.clock.wall = function () { return fake.wall; }; M.clock.perf = function () { return fake.perf; }; }
  function advance(ms) { fake.wall += ms; fake.perf += ms; }
  function realClock() { M.clock.wall = function () { return Date.now(); }; M.clock.perf = function () { return performance.now(); }; }
  function fresh() { useFakeClock(); var store = memStore(); var m = new M(store); m.load(); return { m: m, store: store }; }
  function chests(m, n) { for (var i = 0; i < n; i++) { advance(20000); m.tick(); m.addChest(); } }

  test('Live Tracker', 'plus adds exactly one and auto-starts a session and game', function () {
    var t = fresh(), m = t.m;
    var r = m.addChest();
    assert(r.ok && r.autoStarted && r.autoGame, 'auto-start');
    assert(m.sessionChests() === 1 && m.total() === 1 && m.gameChests() === 1);
    chests(m, 9);
    assert(m.sessionChests() === 10 && m.total() === 10 && m.gameChests() === 10);
    realClock();
  });
  test('Live Tracker', 'minus removes exactly one and never goes negative', function () {
    var t = fresh(), m = t.m;
    chests(m, 2);
    assert(m.removeChest().ok && m.sessionChests() === 1 && m.total() === 1);
    var r = m.removeChest();
    assert(r.ok && r.removedGame, 'removing the only chest of an auto-started game removes that empty game');
    assert(m.sessionChests() === 0 && !m.game());
    assert(!m.removeChest().ok, 'cannot go below zero');
    assert(m.sessionChests() === 0 && m.total() === 0);
    realClock();
  });
  test('Live Tracker', 'rapid presses are all counted', function () {
    var t = fresh(), m = t.m;
    for (var i = 0; i < 500; i++) m.addChest();
    assert(m.sessionChests() === 500 && m.total() === 500 && m.gameChests() === 500);
    realClock();
  });
  test('Live Tracker', 'counts persist and reload exactly', function () {
    var t = fresh(), m = t.m;
    chests(m, 37); m.endGame(); chests(m, 5);
    var m2 = new M(t.store); m2.load();
    assert(m2.total() === 42 && m2.sessionChests() === 42, 'reloaded totals');
    assert(m2.state.gameOrder.length === 2 && m2.validGames().length === 1);
    realClock();
  });
  test('Live Tracker', 'pause stops time, resume continues it', function () {
    var t = fresh(), m = t.m;
    m.startSession(); advance(60000); m.tick();
    close(m.activeMs(), 60000, 1e-9);
    m.pause(); advance(600000);
    close(m.activeMs(), 60000, 1e-9, 'paused time not counted');
    assert(m.addChest().blocked === 'paused', 'counting is blocked while paused by default');
    m.resume(); advance(30000); m.tick();
    close(m.activeMs(), 90000, 1e-9, 'resumed');
    realClock();
  });
  test('Live Tracker', 'closing the tab restores the session as paused without the time away', function () {
    var t = fresh(), m = t.m;
    m.startSession(); chests(m, 6); m.checkpoint();
    var at = m.activeMs();
    advance(3 * 3600000); // browser closed for three hours
    var m2 = new M(t.store); m2.load();
    assert(m2.status() === 'paused' && m2.restored && m2.restored.mode === 'paused', 'restored paused');
    close(m2.activeMs(), at, 1e-9, 'time away excluded');
    assert(m2.sessionChests() === 6, 'count restored');
    realClock();
  });
  test('Live Tracker', 'a long device sleep is excluded from active time', function () {
    var t = fresh(), m = t.m;
    m.startSession(); advance(10000); m.tick();
    advance(40 * 60000); var gap = m.tick();
    assert(gap >= 40 * 60000, 'gap detected');
    close(m.activeMs(), 10000, 1e-9, 'gap not counted');
    m.restoreGap(); close(m.activeMs(), 10000 + 40 * 60000, 1e-9, 'user can add it back');
    realClock();
  });
  test('Live Tracker', 'new sessions start at zero and keep the lifetime total', function () {
    var t = fresh(), m = t.m;
    chests(m, 12); m.endSession('finalize');
    m.startSession();
    assert(m.sessionChests() === 0 && m.total() === 12, 'session reset, lifetime kept');
    chests(m, 3);
    assert(m.total() === 15 && m.state.sessionOrder.length === 2);
    realClock();
  });
  test('Live Tracker', 'ending a session with a game in progress respects the chosen option', function () {
    var t = fresh(), m = t.m;
    chests(m, 10); m.endGame(); chests(m, 4);
    m.endSession('incomplete');
    var v = m.validGames(); assert(v.length === 1 && v[0].chests === 10, 'incomplete game excluded from CPG');
    assert(m.total() === 14, 'but its chests still count');
    var t2 = fresh(), m2 = t2.m; chests(m2, 4); m2.endSession('finalize');
    assert(m2.validGames().length === 1, 'finalised game counts');
    realClock();
  });
  test('Live Tracker', 'CPG: all-time, last 10 (or fewer), previous 10, zero-chest games', function () {
    var t = fresh(), m = t.m;
    var plan = [30, 40, 50, 0, 20];
    plan.forEach(function (c) { if (c === 0) { m.startGame(); m.endGame(); } else { chests(m, c); m.endGame(); } });
    var all = m.cpgAll(); close(all.cpg, 140 / 5, 1e-12, 'all-time includes the legitimate zero-chest game'); assert(all.games === 5);
    var rec = m.cpgRecent(10); assert(rec.games === 5, 'fewer than 10 games -> uses what exists');
    for (var i = 0; i < 12; i++) { chests(m, 10); m.endGame(); }
    var r10 = m.cpgRecent(10); close(r10.cpg, 10, 1e-12); assert(r10.games === 10);
    var prev = m.cpgPrevious(10); assert(prev && prev.games === 7, 'previous window');
    chests(m, 99); // game in progress
    close(m.cpgRecent(10).cpg, 10, 1e-12, 'in-progress game excluded');
    realClock();
  });
  test('Live Tracker', 'ending a game does not create empty games; next chest starts the next game', function () {
    var t = fresh(), m = t.m;
    chests(m, 5); m.endGame(); m.endGame(); m.endGame();
    assert(m.state.gameOrder.length === 1, 'repeated End Game is harmless');
    m.addChest(); assert(m.state.gameOrder.length === 2 && m.game().startKind === 'inferred', 'auto-started with inferred start');
    realClock();
  });
  test('Live Tracker', 'voiding excludes a game from CPG without touching chest totals; restore reinstates it', function () {
    var t = fresh(), m = t.m;
    chests(m, 10); var g1 = m.endGame(); chests(m, 30); m.endGame();
    m.voidGame(g1.id);
    assert(m.total() === 40, 'totals unchanged'); close(m.cpgAll().cpg, 30, 1e-12);
    m.restoreGame(g1.id); close(m.cpgAll().cpg, 20, 1e-12); assert(m.total() === 40);
    realClock();
  });
  test('Live Tracker', 'splitting a combined game moves chests without duplicating or losing any', function () {
    var t = fresh(), m = t.m;
    chests(m, 8); advance(10 * 60000); chests(m, 5); var g = m.endGame();
    var prev = m.splitPreview(g.id, 8); assert(prev.a.chests === 8 && prev.b.chests === 5, 'preview');
    var r = m.splitGame(g.id, 8); assert(r.ok);
    assert(m.total() === 13 && m.sessionChests() === 13, 'no chests lost');
    var v = m.validGames(); assert(v.length === 2 && v[0].chests === 8 && v[1].chests === 5, 'two valid games');
    close(m.cpgAll().cpg, 6.5, 1e-12);
    realClock();
  });
  test('Live Tracker', 'an accidental game end can be undone', function () {
    var t = fresh(), m = t.m;
    chests(m, 4); m.endGame();
    assert(m.canUndoEndGame() && m.undoEndGame(), 'undo');
    chests(m, 2); assert(m.gameChests() === 6 && m.state.gameOrder.length === 1);
    realClock();
  });
  test('Live Tracker', 'only one Pirate Token can be recorded; it can be undone', function () {
    var t = fresh(), m = t.m;
    chests(m, 3);
    assert(m.recordToken().ok); assert(m.recordToken().error === 'already', 'second find refused');
    chests(m, 7); assert(m.chestsSinceToken() === 7);
    m.undoToken(m.tokenRecord().id); assert(!m.tokenRecord() && m.recordToken().ok, 'undo then record again');
    realClock();
  });
  test('Live Tracker', 'imported history adds to the lifetime total only', function () {
    var t = fresh(), m = t.m;
    m.setImportTotal(12000); chests(m, 350);
    assert(m.total() === 12350 && m.sessionChests() === 350);
    realClock();
  });
  test('Live Tracker', 'corrections update the right totals and are logged', function () {
    var t = fresh(), m = t.m;
    chests(m, 10);
    assert(m.adjustChests(20, 'game', 'forgot to count').ok); assert(m.gameChests() === 30 && m.total() === 30);
    assert(m.adjustChests(-5, 'session').error, 'cannot remove unassigned chests that do not exist');
    assert(m.adjustChests(4, 'session').ok && m.sessionChests() === 34 && m.gameChests() === 30);
    var before = m.activeMs();
    assert(m.correctTime(null, -60000, 'forgot to pause').ok, 'time correction');
    close(m.activeMs(), before - 60000, 1e-9, 'time reduced by exactly one minute');
    assert(m.sessionChests() === 34, 'time corrections never change chest counts');
    assert(m.correctTime(null, -1e9).error, 'cannot go below zero');
    assert(m.state.corrections.length >= 3, 'corrections logged');
    realClock();
  });
  test('Live Tracker', 'pace, ETA and probabilities use the exact 1-in-137,500 model', function () {
    var t = fresh(), m = t.m;
    m.startSession();
    for (var i = 0; i < 120; i++) { advance(30000); m.tick(); m.addChest(); } // 120 chests in 60 minutes
    var r = m.rate('session'); assert(r.ready, 'rate ready'); close(r.perHour, 120, 1e-9);
    assert(M.expectedChestsToToken() === 137500, 'memoryless expectation');
    close(M.etaHours(r.perHour), 137500 / 120, 1e-12, 'ETA = 137,500 / pace');
    m.setImportTotal(100000); assert(M.expectedChestsToToken() === 137500, 'past chests are never subtracted');
    var pr = P.atLeastOne(P.model(BigInt(m.sessionChests()), 1n, 137500n)).p;
    var exact = TK.BF.ONE.sub(TK.BF.fromRatio(137499n, 137500n).pow(120n)).toNumber();
    close(pr, exact, 1e-14, 'session chance = 1 - (137499/137500)^120 (vs 320-bit exact)');
    realClock();
  });
  // ---------------- matches imported from Fortnite replays (automatic PC tracking) ----------------
  var MIN = 60000;
  function match(o) {
    o = o || {};
    var start = o.start != null ? o.start : fake.wall - 30 * MIN, len = o.len || 18 * MIN, n = o.chests != null ? o.chests : 37;
    return { id: o.id || 'm-' + Math.random().toString(36).slice(2), match_key: 'fn:x', started_at: new Date(start).toISOString(), ended_at: new Date(start + len).toISOString(),
      chests: o.corrected != null ? o.corrected : n, replay_chests: n, corrected_chests: o.corrected != null ? o.corrected : null,
      chest_times: o.times || Array.from({ length: n }, function (_, i) { return 20 + i * 25; }), historical: !!o.historical, excluded: !!o.excluded,
      auto_session: o.auto || 'auto-1', updated_at: o.v || '2026-11-10T10:00:00.000Z', party: null };
  }
  test('Replay imports', 'an imported match becomes one game in an automatic session', function () {
    var m = fresh().m, x = match({ chests: 37 });
    var r = m.applyReplay(x, 'auto');
    assert(r.action === 'created' && r.game.id === 'rp-' + x.id, 'game id derived from the match id');
    assert(m.total() === 37 && m.validGames().length === 1 && m.cpgAll().cpg === 37, 'totals and CPG include it');
    var s = m.state.sessions[r.game.sid];
    assert(s.inferred && s.status === 'completed' && s.activeMs === 18 * MIN, 'inferred session: time = the match length, never invented');
    assert(m.applyReplay(x, 'auto').action === 'none' && m.total() === 37, 'the same match again changes nothing');
    realClock();
  });
  test('Replay imports', 'matches close together share a session; its time never includes the gaps', function () {
    var m = fresh().m, t0 = fake.wall - 300 * MIN;
    m.applyReplay(match({ start: t0, chests: 10, auto: 'A' }), 'auto');
    m.applyReplay(match({ start: t0 + 40 * MIN, chests: 12, auto: 'A' }), 'auto');
    var sids = m.state.sessionOrder;
    assert(sids.length === 1 && m.state.sessions[sids[0]].activeMs === 36 * MIN, 'two 18-minute matches, 40 minutes apart = 36 minutes');
    assert(m.total() === 22 && m.cpgRecent(10).cpg === 11, 'CPG over both games');
    realClock();
  });
  test('Replay imports', 'a correction on the website updates the game, keeping its chest times', function () {
    var m = fresh().m, x = match({ chests: 37 });
    m.applyReplay(x, 'auto');
    var y = match({ id: x.id, chests: 37, corrected: 38, v: '2026-11-10T11:00:00.000Z' });
    assert(m.applyReplay(y, 'auto').action === 'updated', 'newer server copy applied');
    var g = m.replayGame(x.id);
    assert(m.total() === 38 && g.chests === 38 && g.ts.length === 37 && g.adj === 1, 'corrected to 38; 37 timed chests kept');
    realClock();
  });
  test('Replay imports', 'manual 35 + replay 37 of the same match is never 72 (automatic: replay wins, manual kept)', function () {
    var m = fresh().m, start = fake.wall;
    m.startGame(); chests(m, 35); m.endGame();                      // 35 counted by hand, 20 s apart, during the match
    var r = m.applyReplay(match({ start: start - 60000, len: 16 * MIN, chests: 37 }), 'auto');
    assert(r.action === 'reconciled', 'reconciled with the hand-counted game');
    assert(m.total() === 37 && m.validGames().length === 1, 'total 37, still one game');
    assert(r.game.replay.manual.chests === 35, 'the manual count is kept for reference');
    realClock();
  });
  test('Replay imports', 'hybrid: the replay checks your count and you choose', function () {
    var m = fresh().m, start = fake.wall;
    m.startGame(); chests(m, 35); m.endGame();
    var x = match({ start: start - 60000, len: 16 * MIN, chests: 37 });
    var r = m.applyReplay(x, 'hybrid');
    assert(r.action === 'pending' && m.total() === 35 && m.pendingReplays().length === 1, 'waits for your choice; nothing added');
    m.resolveReplay(r.game.id, true);
    assert(m.total() === 37 && !m.pendingReplays().length, 'use replay: 37');
    m.resolveReplay(r.game.id, false);
    assert(m.total() === 35, 'keep mine: back to 35');
    realClock();
  });
  test('Replay imports', 'a match during your running session goes into that session', function () {
    var m = fresh().m;
    m.startSession(); advance(25 * MIN); m.tick();
    var r = m.applyReplay(match({ start: fake.wall - 20 * MIN, len: 15 * MIN, chests: 9 }), 'auto');
    assert(r.game.sid === m.state.currentSid && m.sessionChests() === 9, 'added to the live session (the headline count goes up)');
    realClock();
  });
  test('Replay imports', 'history imports never land in the current session', function () {
    var m = fresh().m;
    m.startSession(); advance(25 * MIN); m.tick();
    var r = m.applyReplay(match({ start: fake.wall - 20 * MIN, len: 15 * MIN, chests: 9, historical: true, auto: 'H' }), 'auto');
    assert(r.game.sid !== m.state.currentSid && m.state.sessions[r.game.sid].historical && m.sessionChests() === 0, 'separate history session');
    realClock();
  });
  test('Replay imports', '"not my match" removes it (and its empty automatic session); a hand-counted game gets its own count back', function () {
    var m = fresh().m, x = match({ chests: 12 });
    m.applyReplay(x, 'auto');
    m.applyReplay(match({ id: x.id, chests: 12, excluded: true, v: '2026-11-10T12:00:00.000Z' }), 'auto');
    assert(m.total() === 0 && !m.state.sessionOrder.length && !m.state.gameOrder.length, 'removed with its session');
    var start = fake.wall; m.startGame(); chests(m, 5); m.endGame();
    var y = match({ start: start - 60000, len: 10 * MIN, chests: 7 });
    m.applyReplay(y, 'auto'); assert(m.total() === 7, 'reconciled to 7');
    m.removeReplay(y.id); assert(m.total() === 5 && !m.validGames()[0].replay, 'back to the 5 counted by hand');
    realClock();
  });
  test('Replay imports', 'two browsers that both applied a match merge into one game; the newest server copy wins', function () {
    var a = fresh().m, x = match({ chests: 20 });
    a.applyReplay(x, 'auto');
    var b = fresh().m; b.state = JSON.parse(JSON.stringify(a.state)); b.recount();
    b.applyReplay(match({ id: x.id, chests: 20, corrected: 21, v: '2026-11-10T13:00:00.000Z' }), 'auto');
    var merged = TK.sync._merge(a.state, b.state), c = fresh().m;
    c.replaceState(merged);
    assert(c.validGames().length === 1 && c.total() === 21, 'one game, corrected count');
    var back = TK.sync._merge(b.state, a.state), d = fresh().m; d.replaceState(back);
    assert(d.total() === 21, 'same result whichever side merges');
    realClock();
  });
  test('Live Tracker', 'export and import round-trip without loss', function () {
    var t = fresh(), m = t.m;
    chests(m, 9); m.endGame(); chests(m, 2);
    var json = m.exportJSON(); var t2 = fresh(), m2 = t2.m;
    assert(m2.importJSON(json).ok); assert(m2.total() === 11 && m2.validGames().length === 1 && m2.status() === 'paused');
    assert(m2.importJSON('{"nope":1}').error, 'rejects foreign files');
    realClock();
  });
})();
