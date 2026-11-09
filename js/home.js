/* TOKENIFY — Home: a personal dashboard built only from your real saved data (the Live Counter model,
 * your party and friends). Visiting Home never starts, resumes or changes a session by itself. */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h, P = TK.prob, F = TK.fmt;
  var els = {}, acc = null, queued = false;

  function chance(n) { return P.atLeastOne(P.model(BigInt(Math.max(0, n)), 1n, 137500n)); }
  function span(ms) { if (!ms) return '0m'; return U.fmtSpan(ms, { hoursOnly: ms >= 3600000 && ms < 48 * 3600000 }); }
  function cpgText(c) { return c ? (Math.round(c.cpg * 10) / 10).toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) : '—'; }
  function totalActive(m) { var t = 0; m.state.sessionOrder.forEach(function (sid) { t += m.activeMs(sid); }); return t; }

  function hero(m) {
    var s = m.session(), st = m.status();
    U.setText(els.name, acc.username);
    var fresh = acc.created_at && Date.now() - new Date(acc.created_at).getTime() < 30 * 60000 && !m.total();
    U.setText(els.greet, fresh ? 'Welcome aboard,' : 'Welcome back,');
    var label = 'Open Live Counter', line;
    if (st === 'active' || st === 'paused') {
      label = 'Resume tracking';
      line = (st === 'active' ? 'Session ' + m.sessionNo(s.id) + ' in progress' : 'Session ' + m.sessionNo(s.id) + ' is paused') + ' · ' + span(m.activeMs()) + ' tracked · ' + U.fmtInt(s.chests) + ' chest' + (s.chests === 1 ? '' : 's') + ' this session';
    } else line = m.total() ? 'No session running. Your next chest starts one.' : 'Start your first session: press Space (or +) in the Live Counter for every chest you open.';
    U.setText(els.primaryL, label);
    U.setText(els.session, line);
    var tok = m.tokenRecord();
    U.setText(els.tagline, tok ? 'You found the Pirate Token! Every chest from here is still 1 in 137,500.' : 'Ready to hunt the Pirate Token?');
  }
  function stats(m) {
    var total = m.total(), s = m.session();
    U.setText(els.total, U.fmtInt(total));
    U.setText(els.totalS, s && s.status !== 'completed' && s.chests ? '+' + U.fmtInt(s.chests) + ' this session' : m.state.importTotal ? 'Includes ' + U.fmtInt(m.state.importTotal) + ' entered from before tracking' : 'Recorded in the Live Counter');
    var all = m.cpgAll(), rec = m.cpgRecent(10);
    U.setText(els.cpg, cpgText(all)); U.setText(els.cpgS, all ? 'Over ' + all.games + ' game' + (all.games === 1 ? '' : 's') : 'Finish a game to see it');
    U.setText(els.cpg10, cpgText(rec)); U.setText(els.cpg10S, rec ? 'Last ' + rec.games + ' game' + (rec.games === 1 ? '' : 's') : 'Most recent games');
    U.setText(els.chance, F.prob(chance(total), { mode: 'percent', decimals: 3 }));
    U.setText(els.time, span(totalActive(m)));
    U.setText(els.games, U.fmtInt(m.validGames().length));
  }
  function partyBox() {
    var box = els.party; if (!box) return;
    box.innerHTML = '';
    var p = TK.party && TK.party.state(), soc = TK.social && TK.social.state();
    if (!p) {
      var online = soc ? soc.friends.filter(function (f) { return f.presence !== 'offline'; }).length : 0;
      box.appendChild(h('p', { class: 'muted', text: soc && soc.friends.length ? online + ' of ' + soc.friends.length + ' friend' + (soc.friends.length === 1 ? '' : 's') + ' online. Squad up to count chests together.' : 'Track your squad’s chests together: everyone counts their own, and the total adds up live.' }));
      box.appendChild(h('div', { class: 'home-party-actions' }, [h('a', { class: 'btn-gold btn-sm', href: '#party' }, ['Create party']), h('a', { class: 'btn-ghost btn-sm', href: '#friends' }, [soc && soc.friends.length ? 'Invite friends' : 'Add friends'])]));
      U.setText(els.partyLink, 'Party page');
      return;
    }
    U.setText(els.partyLink, 'View party');
    var leader = p.members.filter(function (m) { return m.is_leader; })[0];
    var connected = p.members.filter(function (m) { return m.presence !== 'offline'; }).length;
    box.appendChild(h('div', { class: 'hp-members' }, p.members.map(function (m) {
      var a = TK.profile.avatar(m, 'md'); a.title = m.username + ' — ' + (m.presence === 'offline' ? 'offline' : 'connected');
      return h('span', { class: 'hp-m p-' + m.presence }, [a, h('span', { class: 'presence-dot p-' + m.presence, 'aria-hidden': 'true' })]);
    }).concat(Array.from({ length: 4 - p.members.length }, function () { return h('span', { class: 'hp-m empty', 'aria-hidden': 'true' }); }))));
    box.appendChild(h('p', { class: 'fine', text: (leader ? leader.username + '’s party' : 'Party') + ' · ' + connected + ' of ' + p.members.length + ' connected' }));
    if (p.game) box.appendChild(h('div', { class: 'hp-game' }, [h('span', { class: 'live-pill', text: 'Live' }), h('span', { text: 'Squad game ' + p.game.number + ' · ' }), h('b', { class: 'num', text: U.fmtInt(p.game.known_total) }), h('span', { text: ' known squad chests' })]));
    else box.appendChild(h('p', { class: 'muted hp-idle', text: p.auto_next ? 'Next squad game starts with the first chest.' : 'No squad game running.' }));
  }
  function activity(m) {
    var box = els.activity; if (!box) return;
    box.innerHTML = '';
    var order = m.state.sessionOrder.slice().reverse(), shown = 0;
    order.some(function (sid) {
      var s = m.state.sessions[sid]; if (!s) return false;
      var games = m.validGames().filter(function (g) { return g.sid === sid; }), c = m.cpgSession(sid);
      var live = s.status === 'active' || s.status === 'paused';
      box.appendChild(h('div', { class: 'act' + (live ? ' live' : '') }, [
        h('div', { class: 'act-top' }, [h('b', { text: live ? (s.status === 'active' ? 'Current session' : 'Paused session') : 'Last session' }), h('span', { class: 'fine', text: U.fmtDate(s.start) })]),
        h('div', { class: 'act-line' }, [h('span', { class: 'num act-big', text: U.fmtInt(s.chests) }), h('span', { text: ' chests · ' + games.length + ' game' + (games.length === 1 ? '' : 's') + (c ? ' · ' + cpgText(c) + ' CPG' : '') + ' · ' + span(m.activeMs(sid)) })])
      ]));
      shown++;
      return shown >= (live ? 2 : 1);
    });
    var tok = m.tokenRecord();
    if (tok) box.appendChild(h('div', { class: 'act token' }, [h('img', { src: 'assets/tokens/pirate-token-160.png', alt: '', width: '36', height: '36' }), h('div', {}, [h('b', { text: 'Pirate Token found!' }), h('span', { class: 'fine', text: 'At chest #' + U.fmtInt(tok.totalChests) + ' · ' + U.fmtDate(tok.t) })])]));
    if (!shown && !tok) box.appendChild(h('div', { class: 'empty' }, [h('b', { text: 'No activity yet' }), h('span', { text: 'Your sessions, games and token finds will appear here.' }), h('a', { class: 'btn-gold btn-sm', href: '#tracker' }, ['Start tracking'])]));
  }
  function render() {
    queued = false;
    var m = TK.tracker; if (!m || !acc) return;
    hero(m); stats(m); partyBox(); activity(m);
  }
  function soon() { if (queued) return; queued = true; requestAnimationFrame(render); }

  TK.home = {
    init: function (account) {
      acc = account;
      els.greet = U.$('#home-greet');
      ['name', 'tagline', 'primary', 'primaryL', 'session', 'total', 'totalS', 'cpg', 'cpgS', 'cpg10', 'cpg10S', 'chance', 'time', 'games', 'party', 'partyLink', 'activity'].forEach(function (k) {
        var id = { name: 'home-name', tagline: 'home-tagline', primary: 'home-primary', primaryL: 'home-primary-l', session: 'home-session', total: 'hs-total', totalS: 'hs-total-s', cpg: 'hs-cpg', cpgS: 'hs-cpg-s', cpg10: 'hs-cpg10', cpg10S: 'hs-cpg10-s',
          chance: 'hs-chance', time: 'hs-time', games: 'hs-games', party: 'home-party-body', partyLink: 'home-party-link', activity: 'home-activity-body' }[k];
        els[k] = document.getElementById(id);
      });
      ui.tips.homeChance = 'Your cumulative chance of at least one Pirate Token across every chest you’ve recorded: 1 − (1 − 1/137,500)^chests. Each new chest is still exactly 1 in 137,500; past chests never make the next one more likely.';
      els.primary.addEventListener('click', function () { if (TK.tracker && TK.tracker.status() === 'paused' && TK.trackerUI) setTimeout(function () { TK.trackerUI.resume(); }, 0); });
      if (TK.tracker) TK.tracker.on(function () { if (TK.app.view() === 'home') soon(); });
      U.bus.on('view', function (v) { if (v === 'home') render(); });
      U.bus.on('party', function () { if (TK.app.view() === 'home') soon(); });
      U.bus.on('party-render', function () { if (TK.app.view() === 'home') soon(); });
      U.bus.on('social', function () { if (TK.app.view() === 'home') soon(); });
      U.bus.on('account', function (a) { if (a) { acc = a; soon(); } });
      setInterval(function () { if (TK.app.view() === 'home' && TK.tracker && TK.tracker.status() === 'active') hero(TK.tracker); }, 15000);
      render();
    }
  };
})(window.TK = window.TK || {});
