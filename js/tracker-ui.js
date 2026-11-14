/* TOKENIFY — Live Tracker interface.
 * Minimal main screen; everything advanced lives behind "Details". Space = +1 chest (only on this
 * tab, never while typing or in dialogs, never on key auto-repeat). Only one browser tab owns the
 * tracker at a time so timers and counts can never be doubled. */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h, S = TK.settings, P = TK.prob, F = TK.fmt;
  var DEF = { trkBackspace: false, trkNKey: true, trkAnim: true, trkSound: false, trkAutoResumeOpen: false, trkKeepRunning: false, trkAutoResumeCount: false,
    trkGameIdle: 12, trkSessionIdle: 25, trkRateBasis: 'session', trkDecimals: 4, trkShowGames: true, trkTd: 'estimates', trkTrend: '25' };
  function opt(k) { var v = S.get(k); return v === undefined ? DEF[k] : v; }
  var model, els = {}, isOwner = true, TAB = U.uuid(), OWN = 'tracker.owner';
  var lastCount = null, prompted = { game: null, session: null }, chanceCache = {}, trendChart = null, openGame = null, openSession = null;

  // ---------------- helpers ----------------
  function digits(el, n) {
    var s = U.fmtInt(n);
    if (el.getAttribute('data-v') === s) return;
    el.setAttribute('data-v', s);
    el.innerHTML = s.split('').map(function (c) { return c === ',' ? '<span class="sep">,</span>' : '<span class="dg">' + c + '</span>'; }).join('');
    el.setAttribute('aria-label', s + ' chests this session');
  }
  function pct(pr) { return F.prob(pr, { mode: 'percent', decimals: opt('trkDecimals') }); }
  function chance(n) { return P.atLeastOne(P.model(BigInt(Math.max(0, n)), 1n, 137500n)); }
  function chestsFor(q) { if (!chanceCache[q]) chanceCache[q] = P.minTrials(1n, 137500n, 1, U.parsePercent(String(q))).n; return chanceCache[q]; }
  function span(ms) { return ms === Infinity ? '—' : (ms >= 48 * 3600000 ? U.fmtSpan(ms) : U.fmtSpan(ms, { hoursOnly: ms >= 3600000 })); }
  function saved() { U.setText(els.saved, isOwner ? 'Saved' : ''); clearTimeout(saved.t); saved.t = setTimeout(function () { U.setText(els.saved, ''); }, 1200); }

  // ---------------- tab ownership ----------------
  function readOwner() { return U.storage.get(OWN, null); }
  function claim() { U.storage.set(OWN, { tab: TAB, beat: Date.now() }); if (!isOwner) { isOwner = true; model.readOnly = false; reloadFromStorage(); } render(); }
  function checkOwner() {
    var o = readOwner();
    if (!o || o.tab === TAB || Date.now() - o.beat > 6500) { if (!isOwner || !o || o.tab !== TAB) claim(); return; }
    if (isOwner) { isOwner = false; model.readOnly = true; }
  }
  function reloadFromStorage() {
    var s = U.storage.get('tracker.state', null);
    if (s && s.v === 1) { model.state = s; model.recount(); var cur = model.session(); if (cur && cur.status === 'active' && isOwner && model.lastPerf == null) model.markRunning(); }
  }

  // On automatic PC tracking nobody starts a session by hand: matches arrive from the PC and are grouped into
  // sessions by themselves. With no session running, the counter then shows the newest of those, so the big
  // number is the chests of the player's latest play session rather than a bare 0.
  function autoView() {
    var pc = TK.pc && TK.pc.state ? TK.pc.state() : null;
    if (!pc || pc.tracking !== 'auto' || model.session()) return null;
    var s = model.state, best = null, lastGame = {};
    s.gameOrder.forEach(function (gid) { var g = s.games[gid]; if (g && g.status !== 'void') lastGame[g.sid] = g; });
    s.sessionOrder.forEach(function (id) {
      var x = s.sessions[id];
      // back-filled history is not "latest"; neither is a session started by hand and ended without a game
      if (!x || x.historical || !lastGame[id]) return;
      if (!best || (x.end || x.start) >= (best.end || best.start)) best = x;
    });
    return { session: best, game: best ? lastGame[best.id] : null };
  }

  // ---------------- main render ----------------
  function render() {
    if (!model || !els.count) return; // not wired up yet
    var st = model.status(), sess = model.session(), game = model.game();
    var auto = isOwner ? autoView() : null;
    els.status.setAttribute('data-state', !isOwner ? 'elsewhere' : st);
    U.setText(els.statusText, !isOwner ? 'Open in another tab' : auto ? 'Automatic — counted after each match' : st === 'active' ? 'Session active' : st === 'paused' ? 'Paused' : 'No session — ready');
    U.setText(els.sessionNo, sess ? 'Session ' + model.sessionNo(sess.id) : auto && auto.session ? 'Session ' + model.sessionNo(auto.session.id) : '');
    var count = auto && auto.session ? auto.session.chests : model.sessionChests();
    digits(els.count, count);
    if (els.countLabel) U.setText(els.countLabel, auto && auto.session ? 'Chests in your latest session · ' + U.fmtDate(auto.session.end || auto.session.start) : 'Chests this session');
    if (lastCount != null && count !== lastCount && opt('trkAnim') && !U.reducedMotion()) {
      els.count.classList.remove('bump', 'dip'); void els.count.offsetWidth; els.count.classList.add(count > lastCount ? 'bump' : 'dip');
    }
    lastCount = count;
    var blockedPlus = !isOwner || (st === 'paused' && !opt('trkAutoResumeCount'));
    els.plus.disabled = blockedPlus; els.minus.disabled = !isOwner || count <= 0;
    els.view.classList.toggle('is-paused', st === 'paused'); els.view.classList.toggle('is-elsewhere', !isOwner);
    els.hint.innerHTML = !isOwner ? 'Counting is paused in this tab to prevent double counting.' : st === 'paused' ? 'Paused — resume to keep counting' + (opt('trkAutoResumeCount') ? ' (or press <kbd>Space</kbd> to resume and count)' : '') : auto ? 'Your PC counts for you: each match is added a few seconds after it ends. Nothing to press.'
      : 'Press <kbd>Space</kbd> for every chest' + (opt('trkNKey') ? ' · <kbd>N</kbd> ends the game' : '');
    // game + CPG
    if (game) { U.setText(els.game, U.fmtInt(game.chests)); U.setText(els.gameL, 'Current game ' + model.gameNo(game.id)); }
    else if (auto && auto.game) { U.setText(els.game, U.fmtInt(auto.game.chests)); U.setText(els.gameL, 'Last game'); }
    else { U.setText(els.game, '—'); U.setText(els.gameL, sess ? 'Next chest starts game ' + (model.state.gameOrder.length + 1) : 'Current game'); }
    var all = model.cpgAll(), rec = model.cpgRecent(10);
    U.setText(els.cpgAll, all ? U.fmtNum(all.cpg, 1) : '—');
    U.setText(els.cpgRecent, rec ? U.fmtNum(rec.cpg, 1) : '—');
    U.setText(els.cpgRecentL, 'Last ' + (rec ? Math.min(10, rec.games) : 10) + ' CPG');
    if (all && rec && all.cpg > 0 && all.games > rec.games) {
      var d = (rec.cpg - all.cpg) / all.cpg * 100;
      els.cpgDelta.textContent = (d >= 0 ? '+' : '−') + U.fmtNum(Math.abs(d), 1) + '% vs all-time'; els.cpgDelta.className = 'tm-delta ' + (d >= 0 ? 'up' : 'down');
    } else { els.cpgDelta.textContent = ''; els.cpgDelta.className = 'tm-delta'; }
    U.setText(els.total, U.fmtInt(model.total()));
    // probabilities
    var pS = chance(count), pO = chance(model.total());
    U.setText(els.pSession, pct(pS)); els.pSession.title = F.oneIn(pS);
    U.setText(els.pOverall, pct(pO)); els.pOverall.title = F.oneIn(pO);
    renderEta();
    // buttons
    els.start.hidden = !(st === 'none' || st === 'completed');
    els.pause.hidden = st === 'none';
    els.end.hidden = st === 'none';
    els.pause.innerHTML = st === 'paused' ? '<svg class="i"><use href="#i-play"/></svg>Resume' : '<svg class="i"><use href="#i-pause"/></svg>Pause';
    els.pause.classList.toggle('primary-ghost', st === 'paused');
    els.start.disabled = els.pause.disabled = els.end.disabled = !isOwner;
    if (game) { els.endGame.disabled = !isOwner || st === 'paused'; els.endGame.innerHTML = 'End game &amp; ready next' + (opt('trkNKey') ? ' <kbd>N</kbd>' : ''); els.endGame.classList.remove('waiting'); }
    else { els.endGame.disabled = !isOwner || !sess || st === 'paused'; els.endGame.innerHTML = sess ? 'Start game timer now' : 'End game &amp; ready next'; els.endGame.classList.toggle('waiting', !!sess); }
    renderTime();
    renderBanner();
    renderRecon();
    if (els.details.open) renderDetails();
  }
  function renderTime() {
    var ms = 0, auto = isOwner ? autoView() : null;
    if (auto && auto.session) ms = model.activeMs(auto.session.id);   // an automatic session's time is its matches' own length
    else if (isOwner) ms = model.activeMs();
    else { var live = U.storage.get('tracker.live', null), s = model.session(); ms = s ? s.activeMs : 0; if (live && s && live.sid === s.id) ms = live.activeMs + (s.status === 'active' ? Math.max(0, Date.now() - live.wall) : 0); }
    U.setText(els.time, U.fmtClock(ms));
    if (els.timeL) U.setText(els.timeL, auto && auto.session && auto.session.inferred ? 'Time in matches' : 'Session time');
  }
  function renderEta() {
    var tok = model.tokenRecord();
    if (tok) {
      els.eta.textContent = 'Pirate Token found ✓';
      els.etaS.textContent = 'Recorded at chest #' + U.fmtInt(tok.totalChests) + ' on ' + U.fmtDate(tok.t) + '. Undo it from Details › Token finds if that was a mistake.';
      els.etaWrap.classList.add('found'); return;
    }
    els.etaWrap.classList.remove('found');
    var r = model.rate(opt('trkRateBasis'));
    if (!r.ready) {
      els.eta.textContent = model.session() ? 'Calculating your pace…' : '—';
      els.etaS.textContent = model.session() ? (r.reason === 'games' ? 'Needs at least two games with a manual start to use game time.' : 'Shown after about 3 minutes and 5 chests.') : 'Start a session and the estimate appears once your pace is known.';
      return;
    }
    var hours = TK.TrackerModel.etaHours(r.perHour);
    els.eta.textContent = span(hours * 3600000);
    var cpg = model.cpgAll(), games = cpg && cpg.cpg > 0 ? Math.round(137500 / cpg.cpg) : null;
    els.etaS.textContent = 'of active opening at ' + U.fmtNum(r.perHour, 0) + ' chests/hour (' + r.label + ')' + (opt('trkShowGames') && games ? ' · ≈ ' + U.fmtInt(games) + ' games at ' + U.fmtNum(cpg.cpg, 1) + ' CPG' : '');
  }
  function renderBanner() {
    var b = els.banner; b.innerHTML = ''; b.hidden = true; b.className = 'trk-banner';
    if (!isOwner) {
      b.hidden = false; b.classList.add('warn');
      b.append(h('div', { class: 'tb-text' }, [h('b', { text: 'The Live Tracker is open in another tab.' }), h('span', { text: ' Counting is paused here so nothing is counted twice.' })]));
      var take = h('button', { class: 'btn-ghost', type: 'button' }, ['Use this tab']); take.addEventListener('click', claim); b.appendChild(take); return;
    }
    var r = model.restored, s = model.session();
    if (r && s && (r.mode === 'paused' || r.mode === 'paused-earlier') && s.status === 'paused') {
      b.hidden = false; b.classList.add('restore');
      var g = model.game();
      b.append(h('div', { class: 'tb-text' }, [h('b', { text: 'Session restored' }), h('span', { class: 'num', text: ' · ' + U.fmtInt(s.chests) + ' chests · ' + U.fmtSpan(s.activeMs) + ' active' + (g ? ' · game ' + model.gameNo(g.id) + ' in progress (' + g.chests + ')' : '') }),
        r.mode === 'paused' && r.awayMs > 60000 ? h('small', { text: 'Time while TOKENIFY was closed (' + U.fmtSpan(r.awayMs) + ') was not counted.' }) : null]));
      var res = h('button', { class: 'btn-gold', type: 'button' }, ['Resume session']); res.addEventListener('click', function () { doResume(); });
      var end = h('button', { class: 'btn-ghost', type: 'button' }, ['End session']); end.addEventListener('click', endSessionFlow);
      b.append(h('div', { class: 'tb-actions' }, [res, end]));
      return;
    }
    if (prompted.show === 'game' && model.game()) {
      b.hidden = false; b.classList.add('prompt');
      b.append(h('div', { class: 'tb-text' }, [h('b', { text: 'Still in the same game?' }), h('span', { text: ' No chests for ' + opt('trkGameIdle') + '+ minutes.' })]));
      var yes = h('button', { class: 'btn-ghost', type: 'button' }, ['Yes, continue']); yes.addEventListener('click', function () { prompted.show = null; render(); });
      var endPrev = h('button', { class: 'btn-gold', type: 'button' }, ['End previous game']);
      endPrev.addEventListener('click', function () { var g = model.game(), last = model.gameLastChest(g.id) || Date.now(); model.endGameAt(g.id, last, 'inferred'); prompted.show = null; ui.toast('Game ended at its last chest (' + U.fmtTime(last) + ').', { action: { label: 'Undo', onClick: function () { model.undoEndGame(); } } }); });
      var review = h('button', { class: 'btn-ghost', type: 'button' }, ['Review game']); review.addEventListener('click', function () { prompted.show = null; openGame = model.state.currentGid; showDetails('games'); });
      b.append(h('div', { class: 'tb-actions' }, [yes, endPrev, review])); return;
    }
    if (prompted.show === 'session' && s && s.status === 'active') {
      b.hidden = false; b.classList.add('prompt');
      var idleMin = Math.round((Date.now() - (s.lastChestAt || s.start)) / 60000);
      b.append(h('div', { class: 'tb-text' }, [h('b', { text: 'Still tracking?' }), h('span', { text: ' Nothing recorded for ' + idleMin + ' minutes.' })]));
      var cont = h('button', { class: 'btn-ghost', type: 'button' }, ['Continue']); cont.addEventListener('click', function () { prompted.show = null; render(); });
      var pz = h('button', { class: 'btn-ghost', type: 'button' }, ['Pause']); pz.addEventListener('click', function () { prompted.show = null; model.pause(); });
      var pzIdle = h('button', { class: 'btn-gold', type: 'button' }, ['Pause & remove ' + idleMin + 'm idle']);
      pzIdle.addEventListener('click', function () { prompted.show = null; var since = Date.now() - (s.lastChestAt || s.start); model.pause(); model.correctTime(s.id, -Math.min(since, model.activeMs()), 'Idle time removed after "Still tracking?"'); });
      var en = h('button', { class: 'btn-ghost', type: 'button' }, ['End session']); en.addEventListener('click', function () { prompted.show = null; endSessionFlow(); });
      b.append(h('div', { class: 'tb-actions' }, [cont, pz, pzIdle, en]));
    }
  }

  // ---------------- actions ----------------
  function plus() {
    if (!isOwner) return;
    var r = model.addChest({ autoResume: opt('trkAutoResumeCount') });
    if (r.blocked) { ui.toast('Session paused — resume to keep counting.', { action: { label: 'Resume', onClick: doResume } }); return; }
    if (opt('trkSound')) TK.audio.play('tick');
    if (r.autoStarted) ui.toast('Session ' + model.sessionNo(model.state.currentSid) + ' started.');
    prompted.game = prompted.session = null; if (prompted.show) prompted.show = null;
    pulse(els.plus);
  }
  function minus() {
    if (!isOwner) return;
    var cur = model.game(), r = model.removeChest();
    if (!r.ok) return;
    if (opt('trkSound')) TK.audio.play('tickDown');
    if (r.from && (!cur || r.from.id !== cur.id)) ui.toast('Removed 1 chest from game ' + model.gameNo(r.from.id) + '.');
    pulse(els.minus);
  }
  function pulse(btn) { btn.classList.remove('hit'); void btn.offsetWidth; btn.classList.add('hit'); }
  function endGameOrStart() {
    if (!isOwner) return;
    if (model.game()) {
      var g = model.endGame();
      ui.toast('Game ' + model.gameNo(g.id) + ' saved — ' + U.fmtInt(g.chests) + (g.chests === 1 ? ' chest' : ' chests') + '. Next chest starts a new game.', { action: { label: 'Undo', onClick: function () { if (!model.undoEndGame()) ui.toast('That game can no longer be reopened.'); } } });
      TK.audio.play('success');
    } else if (model.session()) {
      model.startGame(); ui.toast('Game ' + model.gameNo(model.state.currentGid) + ' timer started.');
    }
  }
  function doResume() { if (model.resume()) { model.restored = null; render(); } }
  function togglePause() { if (model.status() === 'paused') doResume(); else if (model.pause()) ui.toast('Session paused. Your progress is saved.'); }
  function startSession() { model.startSession(); ui.toast('Session ' + model.sessionNo(model.state.currentSid) + ' started. Press Space for every chest.'); }
  function endSessionFlow() {
    var g = model.game();
    if (g && g.status === 'active') {
      ui.modal({ title: 'End session', sub: 'Game ' + model.gameNo(g.id) + ' is still in progress with ' + U.fmtInt(g.chests) + ' chests.', body: '<p class="muted">Choose how to save it. Either way its chests stay in your totals.</p>',
        actions: [{ label: 'Cancel', kind: 'ghost' }, { label: 'Keep as incomplete', kind: 'ghost', onClick: function () { finishSession('incomplete'); } }, { label: 'Finish game & end', kind: 'gold', autofocus: true, onClick: function () { finishSession('finalize'); } }] });
    } else {
      ui.confirm({ title: 'End this session?', message: 'It will be saved to your history. A new session starts fresh; your lifetime total carries on.', confirmLabel: 'End session' }).then(function (ok) { if (ok) finishSession('finalize'); });
    }
  }
  function finishSession(action) { var s = model.endSession(action); if (s) summary(s); }
  function summary(s) {
    var games = model.gamesOf(s.id), valid = games.filter(function (g) { return g.status === 'completed'; }), cpg = model.cpgSession(s.id);
    var perHour = s.activeMs > 60000 ? s.chests / (s.activeMs / 3600000) : null;
    var tokens = model.state.tokens.filter(function (t) { return !t.undone && t.sid === s.id; }).length;
    var body = h('div', { class: 'summary' }, [
      h('img', { class: 'summary-mark', src: 'assets/brand/compass.svg', alt: '' }),
      h('div', { class: 'summary-count num', text: U.fmtInt(s.chests) }), h('div', { class: 'summary-label', text: s.chests === 1 ? 'Chest opened' : 'Chests opened' }),
      h('dl', { class: 'summary-grid' }, [
        stat('Games played', U.fmtInt(valid.length) + (games.length > valid.length ? ' (+' + (games.length - valid.length) + ' not counted)' : '')),
        stat('Active time', U.fmtSpan(s.activeMs)),
        stat('Session CPG', cpg ? U.fmtNum(cpg.cpg, 1) : '—'),
        stat('Chests per hour', perHour != null ? U.fmtNum(perHour, 1) : '—'),
        stat('Token chance', pct(chance(s.chests))),
        stat('Tokens found', String(tokens))
      ])
    ]);
    ui.modal({ title: 'Session complete', className: 'summary-modal', body: body, actions: [{ label: 'View history', kind: 'ghost', onClick: function () { showDetails('sessions'); } }, { label: 'Back to tracker', kind: 'ghost' }, { label: 'Start new session', kind: 'gold', autofocus: true, onClick: startSession }] });
    TK.audio.play('success');
  }
  function stat(l, v) { return h('div', {}, [h('dt', { text: l }), h('dd', { class: 'num', text: v })]); }

  // ---------------- keyboard ----------------
  function onKey(e) {
    if (e.repeat) { if (e.code === 'Space' || e.key === ' ') e.preventDefault(); return e.code === 'Space' || e.key === ' '; }
    var t = e.target;
    if (U.isInteractive(t) && t !== document.body && !t.classList.contains('trk')) return false; // focused controls keep their own Space/Enter
    if (e.code === 'Space' || e.key === ' ') { e.preventDefault(); plus(); return true; }
    if (e.key === 'Backspace' && opt('trkBackspace')) { e.preventDefault(); minus(); return true; }
    if ((e.key === 'n' || e.key === 'N') && opt('trkNKey')) { e.preventDefault(); if (model.game()) endGameOrStart(); return true; }
    return false;
  }

  // ---------------- timers & lifecycle ----------------
  var lastTick = 0, lastCheckpoint = 0;
  function loop() {
    var now = Date.now();
    if (isOwner && now - lastTick >= 1000) {
      lastTick = now;
      var gap = model.tick();
      if (gap) ui.toast('Excluded ' + U.fmtSpan(gap) + ' while your device seemed to be asleep.', { duration: 9000, action: { label: 'Count it', onClick: function () { model.restoreGap(); } } });
      checkIdle();
    }
    if (isOwner && now - lastCheckpoint >= 5000) { lastCheckpoint = now; model.checkpoint(); U.storage.set(OWN, { tab: TAB, beat: now }); }
    if (!isOwner && now - lastCheckpoint >= 2000) { lastCheckpoint = now; checkOwner(); }
    renderTime();
  }
  function checkIdle() {
    var s = model.session(); if (!s || s.status !== 'active' || prompted.show) return;
    var idle = Date.now() - (s.lastChestAt || s.start);
    var gm = opt('trkGameIdle'), sm = opt('trkSessionIdle');
    if (model.game() && gm > 0 && idle > gm * 60000 && prompted.game !== s.lastChestAt) { prompted.game = s.lastChestAt; prompted.show = 'game'; render(); return; }
    if (sm > 0 && idle > sm * 60000 && prompted.session !== s.lastChestAt) { prompted.session = s.lastChestAt; prompted.show = 'session'; render(); }
  }

  // ---------------- details ----------------
  function showDetails(tab) { els.details.open = true; selectTd(tab); els.details.scrollIntoView({ behavior: U.reducedMotion() ? 'auto' : 'smooth', block: 'start' }); }
  function selectTd(name) {
    S.set('trkTd', name);
    U.$$('.td-tab', els.details).forEach(function (t) { var on = t.getAttribute('data-td') === name; t.setAttribute('aria-selected', on ? 'true' : 'false'); t.tabIndex = on ? 0 : -1; });
    U.$$('.td-pane', els.details).forEach(function (p) { p.hidden = p.getAttribute('data-pane') !== name; });
    renderDetails();
  }
  function renderDetails() {
    var name = opt('trkTd'), pane = els.details.querySelector('[data-pane="' + name + '"]');
    if (!pane) return;
    if (pane.contains(document.activeElement) && document.activeElement.tagName === 'INPUT') return; // don't clobber typing
    pane.innerHTML = '';
    ({ estimates: paneEstimates, games: paneGames, sessions: paneSessions, tokens: paneTokens, fix: paneFix, data: paneData })[name](pane);
  }
  function section(title, sub) { return h('div', { class: 'td-sec' }, [h('h3', { class: 'td-title', text: title }), sub ? h('p', { class: 'fine', text: sub }) : null]); }
  function seg(label, key, options, onChange) {
    var g = h('div', { class: 'seg', role: 'radiogroup', 'aria-label': label });
    options.forEach(function (o) { g.appendChild(h('button', { type: 'button', role: 'radio', 'aria-checked': String(String(opt(key)) === String(o[0])), 'data-v': o[0] }, [o[1]])); });
    ui.bindRadios(g, 'data-v', function (v) { S.set(key, isNaN(+v) || v === '' ? v : +v); if (onChange) onChange(v); render(); });
    return g;
  }
  function paneEstimates(p) {
    var r = model.rate(opt('trkRateBasis')), cpg = model.cpgAll();
    var head = section('Time estimates', 'How long, at your measured pace, until you would have a given chance of at least one future Pirate Token. Every chest stays 1 in 137,500 — past chests never shorten the wait.');
    p.appendChild(head);
    p.appendChild(h('div', { class: 'td-row' }, [h('span', { class: 'field-label', text: 'Pace based on' }), seg('Pace basis', 'trkRateBasis', [['session', 'Whole session'], ['recent10', 'Last 10 min'], ['recent30', 'Last 30 min'], ['games', 'Game time']])]));
    var table = h('table', { class: 'data-table' }, [h('thead', {}, [h('tr', {}, [h('th', { text: 'Chance of ≥1 future token' }), h('th', { text: 'Chests needed' }), h('th', { text: 'Active time' }), h('th', { text: 'Games (est.)' })])])]);
    var tb = h('tbody'); table.appendChild(tb);
    [25, 50, 75, 90, 95, 99].forEach(function (q) {
      var n = chestsFor(q);
      tb.appendChild(h('tr', {}, [h('td', { text: q + '%' }), h('td', { class: 'num', text: U.fmtInt(n) }), h('td', { class: 'num', text: r.ready ? span(n / r.perHour * 3600000) : '—' }), h('td', { class: 'num', text: cpg && cpg.cpg > 0 ? '≈ ' + U.fmtInt(Math.round(n / cpg.cpg)) : '—' })]));
    });
    tb.appendChild(h('tr', { class: 'expect-row' }, [h('td', { text: 'Average wait (expected)' }), h('td', { class: 'num', text: '137,500' }), h('td', { class: 'num', text: r.ready ? span(137500 / r.perHour * 3600000) : '—' }), h('td', { class: 'num', text: cpg && cpg.cpg > 0 ? '≈ ' + U.fmtInt(Math.round(137500 / cpg.cpg)) : '—' })]));
    p.appendChild(h('div', { class: 'table-scroll' }, [table]));
    p.appendChild(h('p', { class: 'fine', text: 'The average wait (137,500 chests) and the 50% point (' + U.fmtInt(chestsFor(50)) + ' chests) are different things: half of players get a token sooner than ' + U.fmtInt(chestsFor(50)) + ', but rare long droughts pull the average higher. Game counts divide by your average CPG, so they are estimates — chests per game vary.' }));
    var act = model.activeMs(), s = model.session();
    p.appendChild(h('dl', { class: 'kv kv-2' }, [
      stat('Chests per hour', r.ready ? U.fmtNum(r.perHour, 1) + ' (' + r.label + ')' : 'Calculating…'),
      stat('Chests per minute', r.ready ? U.fmtNum(r.perHour / 60, 2) : '—'),
      stat('Session elapsed active time', s ? U.fmtSpan(act) : '—'),
      stat('Measured game time', gameTimeText())
    ]));
  }
  function gameTimeText() { var ms = 0, n = 0; model.validGames().forEach(function (g) { if (g.end && g.startKind !== 'inferred') { ms += g.end - g.start; n++; } }); return n ? U.fmtSpan(ms) + ' over ' + n + ' games' : 'No games with a manual start yet'; }
  function badge(g) {
    if (g.status === 'void') return h('span', { class: 'gbadge void', text: 'Voided' });
    if (g.status === 'incomplete') return h('span', { class: 'gbadge inc', text: 'Incomplete' });
    if (g.status === 'active') return h('span', { class: 'gbadge live', text: 'In progress' });
    if (g.replay) {
      if (g.replay.status === 'pending') return h('span', { class: 'gbadge inc', text: 'Replay check' });
      return h('span', { class: 'gbadge replay', text: g.replay.manual ? (g.replay.status === 'kept-manual' ? 'Manual · replay checked' : 'Replay-checked') : 'Replay', title: 'From your PC’s Fortnite replay' });
    }
    return null;
  }
  // Hybrid counting: after a match the replay checks the game you counted by hand, and you choose which to keep.
  function renderRecon() {
    var box = els.recon; if (!box) return;
    var pend = isOwner ? model.pendingReplays() : [];
    box.innerHTML = ''; box.hidden = !pend.length;
    pend.slice(0, 3).forEach(function (g) {
      var mine = g.replay.manual.chests, rep = g.replay.chests, d = rep - mine;
      var use = h('button', { class: 'btn-gold btn-sm', type: 'button' }, ['Use replay (' + rep + ')']);
      var keep = h('button', { class: 'btn-ghost btn-sm', type: 'button' }, ['Keep mine (' + mine + ')']);
      use.addEventListener('click', function () { model.resolveReplay(g.id, true); ui.toast('Game ' + model.gameNo(g.id) + ' now counts ' + rep + ' chests. Your count is kept for reference.'); });
      keep.addEventListener('click', function () { model.resolveReplay(g.id, false); ui.toast('Kept your count for game ' + model.gameNo(g.id) + '.'); });
      box.appendChild(h('div', { class: 'recon-card' }, [
        h('div', { class: 'recon-text' }, [h('b', { text: 'Replay check · game ' + model.gameNo(g.id) }),
          h('span', { class: 'num', text: ' Manual: ' + mine + ' · Replay: ' + rep + ' · Difference: ' + (d > 0 ? '+' : d < 0 ? '−' : '±') + Math.abs(d) })]),
        h('div', { class: 'recon-actions' }, [use, keep])]));
    });
  }
  function paneGames(p) {
    var all = model.cpgAll(), rec = model.cpgRecent(10), prev = model.cpgPrevious(10), best = model.bestGame(), sess = model.cpgSession();
    p.appendChild(section('Chests per game', 'Voided, incomplete and in-progress games are left out of every CPG figure.'));
    var change = rec && prev && prev.cpg > 0 ? (rec.cpg - prev.cpg) / prev.cpg * 100 : null;
    p.appendChild(h('dl', { class: 'kv kv-3' }, [
      stat('All-time CPG', all ? U.fmtNum(all.cpg, 2) + ' (' + all.games + ' games)' : '—'),
      stat('Last ' + (rec ? Math.min(10, rec.games) : 10) + ' games', rec ? U.fmtNum(rec.cpg, 2) : '—'),
      stat('Previous 10 games', prev ? U.fmtNum(prev.cpg, 2) : '—'),
      stat('Change', change != null ? (change >= 0 ? '+' : '−') + U.fmtNum(Math.abs(change), 1) + '%' : '—'),
      stat('Best game', best ? U.fmtInt(best.chests) + ' (game ' + model.gameNo(best.id) + ')' : '—'),
      stat('This session', sess ? U.fmtNum(sess.cpg, 2) : '—')
    ]));
    var tw = h('div', { class: 'td-row' }, [h('span', { class: 'field-label', text: 'CPG trend' }), seg('Trend range', 'trkTrend', [['10', 'Last 10'], ['25', 'Last 25'], ['50', 'Last 50'], ['all', 'All']])]);
    p.appendChild(tw);
    var wrap = h('div', { class: 'chart-wrap chart-sm' }, [h('canvas', { role: 'img', 'aria-label': 'Chests per game for recent valid games, with a rolling average' })]);
    p.appendChild(wrap);
    var v = model.validGames(), lim = opt('trkTrend') === 'all' ? v.length : parseInt(opt('trkTrend'), 10), list = v.slice(Math.max(0, v.length - lim));
    trendChart = new TK.Chart(wrap.firstChild, { height: 210, xTitle: false, tipText: function (b) { return b.label; } });
    var bars = list.map(function (g) { var no = model.gameNo(g.id); return { x0: no, x1: no, v: g.chests, label: 'Game ' + no + ': ' + g.chests + ' chests' }; });
    var line = list.map(function (g, i) { var a = list.slice(Math.max(0, i - 4), i + 1), s = 0; a.forEach(function (x) { s += x.chests; }); return { x: model.gameNo(g.id), v: s / a.length }; });
    trendChart.set({ bars: bars, line: line.length > 1 ? line : null, yCount: true, empty: 'Finish a game to see your trend' }, false);
    p.appendChild(h('div', { class: 'legend' }, [h('span', { class: 'lg lg-bar', text: 'Chests in game' }), h('span', { class: 'lg lg-obs', text: '5-game rolling average' })]));
    // list
    p.appendChild(section('Game history'));
    var ul = h('ol', { class: 'glist' }), ids = model.state.gameOrder.slice().reverse().slice(0, 80);
    if (!ids.length) ul.appendChild(h('li', { class: 'muted', text: 'No games yet. Your first chest starts game 1.' }));
    ids.forEach(function (gid) { ul.appendChild(gameRow(model.state.games[gid])); });
    p.appendChild(ul);
  }
  function gameRow(g) {
    var no = model.gameNo(g.id), dur = model.gameDuration(g), open = openGame === g.id;
    var head = h('button', { type: 'button', class: 'grow-head', 'aria-expanded': String(open) }, [
      h('span', { class: 'g-no', text: 'Game ' + no }), h('span', { class: 'g-c num', text: g.status === 'void' ? U.fmtInt(g.chests) + ' chests (voided)' : U.fmtInt(g.chests) + (g.chests === 1 ? ' chest' : ' chests') }),
      h('span', { class: 'g-d muted', text: dur != null ? U.fmtSpan(dur) : (g.status === 'active' ? 'live' : '') }), badge(g)]);
    var li = h('li', { class: 'grow' + (g.status === 'void' ? ' is-void' : '') }, [head]);
    head.addEventListener('click', function () { openGame = open ? null : g.id; renderDetails(); });
    if (!open) return li;
    var quality = function (kind) { return kind === 'explicit' ? 'exact' : kind === 'edited' ? 'manually edited' : kind === 'inferred' ? 'inferred from first chest' : kind === 'session-end' ? 'session end' : kind === 'normal' ? 'exact' : '—'; };
    var body = h('div', { class: 'grow-body' }, [h('dl', { class: 'kv kv-2' }, [
      stat('Started', U.fmtDate(g.start) + ' (' + quality(g.startKind) + ')'), stat('Ended', g.end ? U.fmtDate(g.end) + ' (' + quality(g.endKind) + ')' : 'in progress'),
      stat('Duration', dur != null ? U.fmtSpan(dur) + (g.startKind === 'inferred' ? ' (from first chest)' : '') : '—'), stat('Session', 'Session ' + model.sessionNo(g.sid)),
      stat('Status', g.status === 'completed' ? 'Counts toward CPG' : g.status === 'void' ? 'Voided — excluded from CPG' : g.status === 'incomplete' ? 'Incomplete — excluded from CPG' : 'In progress'),
      g.replay ? stat('Source', 'Fortnite replay from your PC: ' + g.replay.replayChests + ' chests' + (g.replay.corrected != null ? ', corrected to ' + g.replay.corrected : '') + (g.replay.manual ? ' · you counted ' + g.replay.manual.chests : '') + (g.replay.historical ? ' · imported history' : ''))
        : stat('Data', (g.edits && g.edits.length ? g.edits.length + ' correction(s)' : 'as recorded') + (g.adj ? ' · ' + (g.adj > 0 ? '+' : '') + g.adj + ' manual' : ''))
    ])]);
    var acts = h('div', { class: 'grow-actions' });
    function act(label, fn, kind) { var b = h('button', { type: 'button', class: kind === 'danger' ? 'btn-ghost danger' : 'btn-ghost' }, [label]); b.addEventListener('click', fn); acts.appendChild(b); }
    if (g.status === 'void') act('Restore game', function () { model.restoreGame(g.id); ui.toast('Game ' + no + ' counts toward CPG again.'); });
    else act('Void game', function () { model.voidGame(g.id); ui.toast('Game ' + no + ' voided. Its chests stay in your totals.', { action: { label: 'Undo', onClick: function () { model.restoreGame(g.id); } } }); }, 'danger');
    if (g.status === 'incomplete') act('Mark complete', function () { model.markComplete(g.id); });
    if (g.replay && g.replay.status !== 'kept-manual') {
      // counts from a replay are corrected on the server, so every device and your squad see the same number
      act('Correct count…', function () { if (TK.pc) TK.pc.open('history'); });
    } else {
      act('Edit game', function () { editGame(g); });
      if (model.gameTimes(g.id).length >= 2) act('Split game', function () { splitGame(g); });
    }
    if (model.state.lastEndedGid === g.id && model.canUndoEndGame()) act('Undo game end', function () { model.undoEndGame(); });
    body.appendChild(acts); li.appendChild(body);
    return li;
  }
  function toLocalInput(ms) { var d = new Date(ms - new Date(ms).getTimezoneOffset() * 60000); return d.toISOString().slice(0, 16); }
  function fromLocalInput(v) { var t = new Date(v).getTime(); return isNaN(t) ? null : t; }
  function editGame(g) {
    var delta = h('input', { type: 'number', value: '0', step: '1', 'aria-label': 'Chests to add or remove' });
    var start = h('input', { type: 'datetime-local', value: toLocalInput(g.start), 'aria-label': 'Start time' });
    var end = h('input', { type: 'datetime-local', value: g.end ? toLocalInput(g.end) : '', 'aria-label': 'End time', disabled: g.status === 'active' });
    var reason = h('input', { type: 'text', placeholder: 'Optional note', 'aria-label': 'Reason' });
    var msg = h('p', { class: 'msg-error', role: 'alert' });
    var body = h('div', { class: 'form-grid' }, [
      h('div', { class: 'form-row' }, [h('label', { text: 'Add / remove chests' }), delta]), h('div', { class: 'form-row' }, [h('label', { text: 'Note' }), reason]),
      h('div', { class: 'form-row' }, [h('label', { text: 'Start' }), start]), h('div', { class: 'form-row' }, [h('label', { text: 'End' }), end]), msg]);
    ui.modal({ title: 'Edit game ' + model.gameNo(g.id), sub: 'Currently ' + g.chests + ' chests. Changes update the session, lifetime total and CPG.', body: body, actions: [{ label: 'Cancel', kind: 'ghost' }, { label: 'Save changes', kind: 'gold', onClick: function () {
      var r = model.editGame(g.id, { chestDelta: parseInt(delta.value, 10) || 0, start: fromLocalInput(start.value), end: end.value ? fromLocalInput(end.value) : null, reason: reason.value.trim() });
      if (r.error) { msg.textContent = r.error; return false; }
      ui.toast('Game ' + model.gameNo(g.id) + ' updated.');
    } }] });
  }
  function splitGame(g) {
    var times = model.gameTimes(g.id), at = Math.max(1, Math.floor(times.length / 2));
    var gapsIdx = 1, best = 0; for (var i = 1; i < times.length; i++) { if (times[i] - times[i - 1] > best) { best = times[i] - times[i - 1]; gapsIdx = i; } }
    at = gapsIdx;
    var list = h('div', { class: 'split-list', role: 'radiogroup', 'aria-label': 'Where the new game starts' });
    var preview = h('div', { class: 'split-preview' });
    function upd() { var pv = model.splitPreview(g.id, at); preview.innerHTML = ''; if (!pv) return; preview.append(h('div', {}, [h('b', { text: 'Game A: ' + pv.a.chests + ' chests' }), h('span', { class: 'muted', text: ' ' + U.fmtTime(pv.a.from * 1000) + '–' + U.fmtTime(pv.a.to * 1000) })]), h('div', {}, [h('b', { text: 'Game B: ' + pv.b.chests + ' chests' }), h('span', { class: 'muted', text: ' ' + U.fmtTime(pv.b.from * 1000) + '–' + U.fmtTime(pv.b.to * 1000) })])); }
    for (var k = 1; k < times.length; k++) {
      (function (idx) {
        var gapMin = Math.round((times[idx] - times[idx - 1]) / 60000);
        var b = h('button', { type: 'button', role: 'radio', class: 'split-opt', 'aria-checked': String(idx === at) }, ['Before chest ' + (idx + 1) + ' · ' + U.fmtTime(times[idx]) + (gapMin >= 2 ? ' · ' + gapMin + ' min gap' : '')]);
        b.addEventListener('click', function () { at = idx; U.$$('.split-opt', list).forEach(function (x) { x.setAttribute('aria-checked', String(x === b)); }); upd(); });
        list.appendChild(b);
      })(k);
    }
    upd();
    ui.modal({ title: 'Split game ' + model.gameNo(g.id), sub: 'Pick the first chest of the second game. Chests are moved, never duplicated or deleted. The largest gap is pre-selected.', body: h('div', {}, [list, h('h4', { class: 'field-label', text: 'Preview' }), preview]), wide: true,
      actions: [{ label: 'Cancel', kind: 'ghost' }, { label: 'Split game', kind: 'gold', onClick: function () { var r = model.splitGame(g.id, at); if (r.error) { ui.toast(r.error); return false; } ui.toast('Game split into two.'); } }] });
  }
  function paneSessions(p) {
    p.appendChild(section('Sessions', 'Completed sessions are only changed through Edit session, and every change is logged.'));
    var tot = 0, ms = 0, best = null, longest = null;
    model.state.sessionOrder.forEach(function (sid) { var s = model.state.sessions[sid]; tot += s.chests; ms += model.activeMs(sid); if (!best || s.chests > best.chests) best = s; if (!longest || model.activeMs(sid) > model.activeMs(longest.id)) longest = s; });
    var toks = model.state.tokens.filter(function (t) { return !t.undone; }).length;
    p.appendChild(h('dl', { class: 'kv kv-3' }, [stat('Sessions', U.fmtInt(model.state.sessionOrder.length)), stat('Active tracking time', U.fmtSpan(ms)), stat('Average chests/hour', ms > 60000 ? U.fmtNum(tot / (ms / 3600000), 1) : '—'),
      stat('Most chests in a session', best ? U.fmtInt(best.chests) : '—'), stat('Longest session', longest ? U.fmtSpan(model.activeMs(longest.id)) : '—'), stat('Tokens recorded', String(toks))]));
    var ul = h('ol', { class: 'glist' }), ids = model.state.sessionOrder.slice().reverse().slice(0, 60);
    if (!ids.length) ul.appendChild(h('li', { class: 'muted', text: 'No sessions yet.' }));
    ids.forEach(function (sid) { ul.appendChild(sessionRow(model.state.sessions[sid])); });
    p.appendChild(ul);
  }
  function sessionRow(s) {
    var no = model.sessionNo(s.id), open = openSession === s.id, act = model.activeMs(s.id);
    var toks = model.state.tokens.filter(function (t) { return !t.undone && t.sid === s.id; }).length;
    var head = h('button', { type: 'button', class: 'grow-head', 'aria-expanded': String(open) }, [h('span', { class: 'g-no', text: 'Session ' + no }), h('span', { class: 'g-c num', text: U.fmtInt(s.chests) + ' chests' }), h('span', { class: 'g-d muted', text: U.fmtSpan(act) + (s.inferred ? ' in matches' : '') + ' · ' + toks + (toks === 1 ? ' token' : ' tokens') }),
      s.status !== 'completed' ? h('span', { class: 'gbadge live', text: s.status === 'active' ? 'Active' : 'Paused' })
        : s.inferred ? h('span', { class: 'gbadge replay', text: s.historical ? 'Imported history' : 'Auto session', title: 'Grouped from matches your PC sent. Its time is the matches’ own length.' }) : null]);
    var li = h('li', { class: 'grow' }, [head]);
    head.addEventListener('click', function () { openSession = open ? null : s.id; renderDetails(); });
    if (!open) return li;
    var games = model.gamesOf(s.id), cpg = model.cpgSession(s.id);
    var body = h('div', { class: 'grow-body' }, [h('dl', { class: 'kv kv-2' }, [
      stat('Started', U.fmtDate(s.start)), stat('Ended', s.end ? U.fmtDate(s.end) : (s.status === 'paused' ? 'paused' : 'in progress')),
      stat('Active time', U.fmtSpan(act)), stat('Chests per hour', act > 60000 ? U.fmtNum(s.chests / (act / 3600000), 1) : '—'),
      stat('Games', String(games.length) + (cpg ? ' · CPG ' + U.fmtNum(cpg.cpg, 1) : '')), stat('Token chance (session)', pct(chance(s.chests)))
    ])]);
    var logs = (s.timeLog || []).map(function (x) { return 'Time ' + (x[1] < 0 ? '−' : '+') + U.fmtSpan(Math.abs(x[1])) + (x[2] ? ' — ' + x[2] : ''); }).concat((s.adjLog || []).map(function (x) { return 'Chests ' + (x[1] > 0 ? '+' : '') + x[1] + (x[2] ? ' — ' + x[2] : ''); }));
    if (logs.length) body.appendChild(h('ul', { class: 'mini-log' }, logs.map(function (t) { return h('li', { text: t }); })));
    var acts = h('div', { class: 'grow-actions' });
    var e = h('button', { type: 'button', class: 'btn-ghost' }, ['Edit session']); e.addEventListener('click', function () { editSession(s); }); acts.appendChild(e);
    if (s.status !== 'completed' && s.id === model.state.currentSid) { var en = h('button', { type: 'button', class: 'btn-ghost' }, ['End session']); en.addEventListener('click', endSessionFlow); acts.appendChild(en); }
    body.appendChild(acts); li.appendChild(body);
    return li;
  }
  function editSession(s) {
    var mins = h('input', { type: 'number', value: '0', step: '1', 'aria-label': 'Minutes to add or remove' });
    var chests = h('input', { type: 'number', value: '0', step: '1', 'aria-label': 'Chests to add or remove' });
    var reason = h('input', { type: 'text', placeholder: 'e.g. forgot to pause during dinner' });
    var msg = h('p', { class: 'msg-error', role: 'alert' });
    ui.modal({ title: 'Edit session ' + model.sessionNo(s.id), sub: 'Active time ' + U.fmtSpan(model.activeMs(s.id)) + ' · ' + s.chests + ' chests. Use negative numbers to remove.', body: h('div', { class: 'form-grid' }, [
      h('div', { class: 'form-row' }, [h('label', { text: 'Active time (minutes ±)' }), mins]), h('div', { class: 'form-row' }, [h('label', { text: 'Unassigned chests (±)' }), chests]),
      h('div', { class: 'form-row', style: { gridColumn: '1 / -1' } }, [h('label', { text: 'Reason (saved in history)' }), reason]), msg]),
      actions: [{ label: 'Cancel', kind: 'ghost' }, { label: 'Save', kind: 'gold', onClick: function () {
        var m = parseInt(mins.value, 10) || 0, c = parseInt(chests.value, 10) || 0, why = reason.value.trim();
        if (!m && !c) { msg.textContent = 'Nothing to change.'; return false; }
        if (m) { var r1 = model.correctTime(s.id, m * 60000, why); if (r1.error) { msg.textContent = r1.error; return false; } }
        if (c) { var r2 = model.adjustSessionChests(s.id, c, why); if (r2.error) { msg.textContent = r2.error; return false; } }
        ui.toast('Session ' + model.sessionNo(s.id) + ' updated.');
      } }] });
  }
  function paneTokens(p) {
    p.appendChild(section('Pirate Token finds', 'Record it here when you actually get a Pirate Token in Fortnite. You can only own one, so one find can be recorded (undo is available).'));
    var rec = model.tokenRecord();
    if (rec) {
      p.appendChild(h('div', { class: 'token-found' }, [h('img', { src: 'assets/tokens/pirate-token-160.png', alt: '' }), h('div', {}, [h('b', { text: 'Pirate Token found' }), h('div', { class: 'muted', text: U.fmtDate(rec.t) + ' · chest #' + U.fmtInt(rec.totalChests) + ' overall' + (rec.sid ? ' · session ' + model.sessionNo(rec.sid) + ' (' + rec.sessionChests + ' chests in)' : '') })])]));
      var undo = h('button', { class: 'btn-ghost danger', type: 'button' }, ['Remove this record']);
      undo.addEventListener('click', function () { ui.confirm({ title: 'Remove the token record?', message: 'Use this if it was recorded by mistake. Your chest counts are not affected.', confirmLabel: 'Remove', danger: true }).then(function (ok) { if (ok) model.undoToken(rec.id); }); });
      p.appendChild(undo);
      p.appendChild(h('p', { class: 'fine', text: 'Chests recorded since the find: ' + U.fmtInt(model.chestsSinceToken()) + '.' }));
    } else {
      var b = h('button', { class: 'btn-gold', type: 'button' }, ['I found a Pirate Token']);
      b.addEventListener('click', function () { ui.confirm({ title: 'Record a Pirate Token?', message: 'This saves the find with your current session and lifetime chest counts. Only record a token you actually received.', confirmLabel: 'Record it' }).then(function (ok) { if (ok && model.recordToken().ok) { TK.audio.play('chime'); celebrate(); } }); });
      p.appendChild(b);
      p.appendChild(h('p', { class: 'fine', text: 'Chests recorded so far: ' + U.fmtInt(model.total()) + '. The tracker never assumes a token was found because your cumulative chance is high.' }));
    }
  }
  function celebrate() {
    if (U.reducedMotion()) { ui.toast('Pirate Token recorded. Congratulations!'); return; }
    var c = h('div', { class: 'trk-celebrate', role: 'status' }, [h('img', { src: 'assets/tokens/pirate-token.webp', alt: '' }), h('b', { text: 'Pirate Token recorded!' })]);
    document.body.appendChild(c); setTimeout(function () { c.classList.add('out'); }, 2200); setTimeout(function () { c.remove(); }, 2700);
  }
  function paneFix(p) {
    p.appendChild(section('Adjust chests', 'Forgot to count some? Add (or remove) them in one go instead of pressing + repeatedly.'));
    var amt = h('input', { type: 'number', value: '1', step: '1', 'aria-label': 'Chests to add (negative removes)' });
    var target = h('select', { 'aria-label': 'Apply to' }, [h('option', { value: 'game', text: 'Current / latest game' }), h('option', { value: 'session', text: 'Session only (no game)' })]);
    var why = h('input', { type: 'text', placeholder: 'Optional note' });
    var go = h('button', { class: 'btn-gold', type: 'button' }, ['Apply']);
    var msg = h('p', { class: 'msg-error', role: 'alert' });
    go.addEventListener('click', function () { var r = model.adjustChests(parseInt(amt.value, 10) || 0, target.value, why.value.trim()); msg.textContent = r.error || ''; if (r.ok) ui.toast('Chests adjusted.'); });
    p.appendChild(h('div', { class: 'fix-row' }, [amt, target, why, go])); p.appendChild(msg);
    p.appendChild(section('Correct active time', 'Forgot to pause? Remove the minutes you were away (use a negative number). The change is logged.'));
    var mins = h('input', { type: 'number', value: '-10', step: '1', 'aria-label': 'Minutes' });
    var why2 = h('input', { type: 'text', placeholder: 'Reason' });
    var go2 = h('button', { class: 'btn-ghost', type: 'button' }, ['Apply to current session']);
    var msg2 = h('p', { class: 'msg-error', role: 'alert' });
    go2.addEventListener('click', function () { if (!model.session()) { msg2.textContent = 'No current session.'; return; } var r = model.correctTime(null, (parseInt(mins.value, 10) || 0) * 60000, why2.value.trim()); msg2.textContent = r.error || ''; if (r.ok) ui.toast('Active time corrected.'); });
    p.appendChild(h('div', { class: 'fix-row' }, [mins, why2, go2])); p.appendChild(msg2);
    p.appendChild(section('Set starting total', 'Already opened chests before using TOKENIFY? Enter that total. It counts toward your overall chance, never toward a session, and no playtime is invented for it.'));
    var imp = h('input', { type: 'text', inputmode: 'numeric', value: U.fmtInt(model.state.importTotal || 0), 'aria-label': 'Previous chests' });
    var go3 = h('button', { class: 'btn-ghost', type: 'button' }, ['Save starting total']);
    go3.addEventListener('click', function () { var r = U.parseCount(imp.value); if (!r.ok) { ui.toast(r.error); return; } model.setImportTotal(Number(r.value)); ui.toast('Starting total saved.'); });
    p.appendChild(h('div', { class: 'fix-row' }, [imp, go3]));
    p.appendChild(section('Correction history'));
    var log = h('ol', { class: 'mini-log' });
    model.state.corrections.slice(-30).reverse().forEach(function (c) { log.appendChild(h('li', {}, [h('span', { class: 'muted', text: U.fmtDate(c.t) + ' · ' }), c.text])); });
    if (!log.children.length) log.appendChild(h('li', { class: 'muted', text: 'No corrections yet.' }));
    p.appendChild(log);
  }
  function paneData(p) {
    p.appendChild(section('Your tracker data', U.storage.available ? 'Saved automatically in this browser after every change.' : 'Browser storage is unavailable: data lasts for this session only. Export it before closing.'));
    var row = h('div', { class: 'fix-row' });
    var ex = h('button', { class: 'btn-ghost', type: 'button' }, ['Export backup (JSON)']); ex.addEventListener('click', function () { U.download('tokenify-tracker-' + new Date().toISOString().slice(0, 10) + '.json', model.exportJSON(), 'application/json'); });
    var csv = h('button', { class: 'btn-ghost', type: 'button' }, ['Export games (CSV)']); csv.addEventListener('click', exportCsv);
    var file = h('input', { type: 'file', accept: '.json,application/json', class: 'sr-only', id: 'trk-import-file' });
    var im = h('button', { class: 'btn-ghost', type: 'button' }, ['Import backup']); im.addEventListener('click', function () { file.click(); });
    file.addEventListener('change', function () {
      var f = file.files[0]; if (!f) return; var rd = new FileReader();
      rd.onload = function () { ui.confirm({ title: 'Replace tracker data?', message: 'Importing replaces the tracker data in this browser with the backup. Export first if you want to keep the current data.', confirmLabel: 'Import', danger: true }).then(function (ok) { if (!ok) return; var r = model.importJSON(String(rd.result)); ui.toast(r.error || 'Backup imported.'); }); };
      rd.readAsText(f);
    });
    var rs = h('button', { class: 'btn-ghost danger', type: 'button' }, ['Reset tracker data']);
    rs.addEventListener('click', function () { ui.confirm({ title: 'Reset all tracker data?', message: 'Deletes every session, game and token record stored in this browser. This cannot be undone (cloud copies, if any, are not touched).', confirmLabel: 'Reset', danger: true }).then(function (ok) { if (ok) { model.resetAll(); ui.toast('Tracker data reset.'); } }); });
    row.append(ex, csv, im, file, rs); p.appendChild(row);
    p.appendChild(section('Account & sync'));
    var st = TK.sync ? TK.sync.statusText() : 'Accounts are not configured.';
    var acc = h('button', { class: 'btn-ghost', type: 'button' }, [TK.sync && TK.sync.user() ? 'Manage account' : 'Sign in or create an account']);
    acc.addEventListener('click', function () { if (TK.sync) TK.sync.openAccount(); });
    p.appendChild(h('p', { class: 'muted', text: st })); p.appendChild(acc);
  }
  function exportCsv() {
    var rows = [['game', 'session', 'status', 'chests', 'start', 'start_quality', 'end', 'end_quality', 'duration_minutes']];
    model.state.gameOrder.forEach(function (gid) { var g = model.state.games[gid]; var d = model.gameDuration(g); rows.push([model.gameNo(gid), model.sessionNo(g.sid), g.status, g.chests, new Date(g.start).toISOString(), g.startKind, g.end ? new Date(g.end).toISOString() : '', g.endKind || '', d != null ? (d / 60000).toFixed(1) : '']); });
    U.download('tokenify-games.csv', U.csv(rows), 'text/csv');
  }

  // ---------------- settings ----------------
  function openSettings() {
    function tog(label, sub, key) {
      var input = h('input', { type: 'checkbox' }); input.checked = !!opt(key);
      input.addEventListener('change', function () { S.set(key, input.checked); render(); });
      return h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: label }), sub ? h('span', { text: sub }) : null]), h('label', { class: 'toggle' }, [input, h('span', { class: 'track' }), h('span', { class: 'sr-only', text: label })])]);
    }
    function num(label, sub, key, min, max) {
      var input = h('input', { type: 'number', min: String(min), max: String(max), value: String(opt(key)), style: { width: '90px' }, 'aria-label': label });
      input.addEventListener('change', function () { var v = Math.max(min, Math.min(max, parseInt(input.value, 10) || 0)); input.value = v; S.set(key, v); });
      return h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: label }), sub ? h('span', { text: sub }) : null]), input]);
    }
    function segRow(label, sub, key, options) { return h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: label }), sub ? h('span', { text: sub }) : null]), seg(label, key, options)]); }
    var body = h('div', {}, [
      h('div', { class: 'settings-group-title', text: 'Counter' }),
      tog('Backspace removes a chest', 'Off by default to avoid accidental removals.', 'trkBackspace'),
      tog('N ends the game', 'Same as End game & ready next.', 'trkNKey'),
      tog('Counter animation', '', 'trkAnim'),
      tog('Click sound', 'A soft tick for every chest.', 'trkSound'),
      h('div', { class: 'settings-group-title', text: 'Timer' }),
      tog('Auto-resume when reopened', 'Otherwise a closed tracker comes back paused.', 'trkAutoResumeOpen'),
      tog('Count time while closed', 'Real-world elapsed time instead of active time. Pace will include time away.', 'trkKeepRunning'),
      tog('Space resumes a paused session', 'Pressing + while paused resumes and counts.', 'trkAutoResumeCount'),
      num('“Still in the same game?” after', 'Minutes without a chest (0 = never).', 'trkGameIdle', 0, 240),
      num('“Still tracking?” after', 'Minutes without a chest (0 = never).', 'trkSessionIdle', 0, 480),
      h('div', { class: 'settings-group-title', text: 'Statistics' }),
      segRow('Pace based on', 'Default: the whole session.', 'trkRateBasis', [['session', 'Session'], ['recent10', '10 min'], ['recent30', '30 min'], ['games', 'Games']]),
      segRow('Probability decimals', '', 'trkDecimals', [['2', '2'], ['4', '4'], ['6', '6']]),
      tog('Show games-to-token estimate', '', 'trkShowGames')
    ]);
    ui.modal({ title: 'Tracker settings', body: body, actions: [{ label: 'Done', kind: 'gold' }] });
  }

  // ---------------- init ----------------
  TK.trackerUI = {
    onKey: function (e) { return onKey(e); },
    // used by Home's RESUME TRACKING (only this tab may change the tracker if it owns it)
    resume: function () { if (isOwner && model && model.status() === 'paused') doResume(); },
    isOwner: function () { return isOwner; },
    init: function () {
      model = new TK.TrackerModel(U.storage);
      checkOwner();
      model.readOnly = !isOwner;
      model.load(isOwner ? { autoResume: opt('trkAutoResumeOpen'), keepRunning: opt('trkKeepRunning') } : {});
      TK.tracker = model;
      model.compact(400);
      var ids = { view: 'trk', status: 'trk-status', statusText: 'trk-status-text', sessionNo: 'trk-session-no', saved: 'trk-saved', banner: 'trk-banner', count: 'trk-count', countLabel: 'trk-count-label', plus: 'trk-plus', minus: 'trk-minus', hint: 'trk-hint',
        game: 'trk-game', gameL: 'trk-game-l', cpgAll: 'trk-cpg-all', cpgRecent: 'trk-cpg-recent', cpgRecentL: 'trk-cpg-recent-l', cpgDelta: 'trk-cpg-delta', time: 'trk-time', total: 'trk-total',
        pSession: 'trk-p-session', pOverall: 'trk-p-overall', eta: 'trk-eta', etaS: 'trk-eta-s', endGame: 'trk-endgame', start: 'trk-start', pause: 'trk-pause', end: 'trk-end', details: 'trk-details', settingsBtn: 'trk-settings-btn',
        recon: 'trk-recon' };
      Object.keys(ids).forEach(function (k) { els[k] = document.getElementById(ids[k]); });
      els.etaWrap = els.eta.parentElement;
      els.timeL = els.time.nextElementSibling;   // the label under the clock
      ui.tips.cumulative = 'Your cumulative chance of at least one Pirate Token over this many chests. Each individual chest is always exactly 1 in 137,500 — the next chest never becomes more likely.';
      ui.tips.eta = 'Average extra active time to get a token at your measured pace: 137,500 more chests ÷ your chests per hour. Past chests never shorten it, and it is an estimate, not a guarantee.';
      els.plus.addEventListener('click', plus); els.minus.addEventListener('click', minus);
      els.endGame.addEventListener('click', endGameOrStart);
      els.start.addEventListener('click', startSession); els.pause.addEventListener('click', togglePause); els.end.addEventListener('click', endSessionFlow);
      els.settingsBtn.addEventListener('click', openSettings);
      els.details.addEventListener('toggle', function () { if (els.details.open) selectTd(opt('trkTd')); });
      U.$$('.td-tab', els.details).forEach(function (t, i, all) {
        t.addEventListener('click', function () { selectTd(t.getAttribute('data-td')); });
        t.addEventListener('keydown', function (e) { var d = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0; if (!d) return; e.preventDefault(); var n = all[(i + d + all.length) % all.length]; n.focus(); selectTd(n.getAttribute('data-td')); });
      });
      model.on(function (what) { render(); if (what !== 'restore') saved(); if (TK.sync) TK.sync.notifyChange(); });
      U.bus.on('settings', function (e) { if (e && String(e.key).indexOf('trk') === 0) render(); });
      U.bus.on('pc', function () { render(); });   // tracking mode and imported matches arrive after the page has loaded
      U.bus.on('view', function (v) { if (v === 'tracker') { render(); if (!U.isInteractive(document.activeElement)) { try { els.view.focus({ preventScroll: true }); } catch (e) { /* ignore */ } } } });
      window.addEventListener('storage', function (e) {
        if (e.key === U.storage.key(OWN)) checkOwner();
        if (e.key === U.storage.key('tracker.state') && !isOwner) { reloadFromStorage(); render(); }
        if (e.key === U.storage.key('tracker.state') && isOwner && e.newValue && e.oldValue !== e.newValue) { /* another tab wrote while we own: take the newest */ reloadFromStorage(); render(); }
      });
      document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'hidden' && isOwner) model.checkpoint(); });
      window.addEventListener('pagehide', function () { if (!isOwner) return; model.checkpoint(); var o = readOwner(); if (o && o.tab === TAB) U.storage.remove(OWN); });
      setInterval(loop, 250);
      render();
    }
  };
})(window.TK = window.TK || {});
