/* TOKENIFY — parties and live squad games.
 *
 * Everyone keeps counting with their own Live Counter. While you're in a party, each chest you add is
 * also sent to the server as an event with its own ID (tk_party_chest_add), which links it to the
 * party's running squad game. Events are idempotent, queued while offline and replayed in order, so a
 * retry or reconnect can never count a chest twice; removing a chest sends a removal for that exact
 * event. The squad totals shown here always come back from the server (live via Realtime, or polled). */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h, api = TK.api, P = TK.prob, F = TK.fmt;
  var party = null, me = null, watcher = null, watchedId = null, knownVersion = -1;
  var outbox = [], linked = [], sending = false, retryTimer = null, pollTimer = null, history = null, lastPrompted = null, fetching = null;
  var els = {};
  var TOKEN = 137500n;

  // ---------------- persistence (per account) ----------------
  function loadQueues() { outbox = U.storage.get('party.outbox', []) || []; linked = U.storage.get('party.linked', []) || []; }
  function saveQueues() { if (linked.length > 400) linked.splice(0, linked.length - 400); U.storage.set('party.outbox', outbox); U.storage.set('party.linked', linked); }

  // ---------------- helpers ----------------
  function av(u, size) { return TK.profile.avatar(u, size); }
  function chance(n) { return P.atLeastOne(P.model(BigInt(Math.max(0, Math.round(n))), 1n, TOKEN)); }
  function pct(n) { return F.prob(chance(n), { mode: 'percent', decimals: 3 }); }
  function fmtCount(n) { return Number.isInteger(n) ? U.fmtInt(n) : (Math.round(n * 10) / 10).toLocaleString(undefined, { maximumFractionDigits: 1 }); }
  function elapsed(from) { var ms = Math.max(0, api.now() - new Date(from).getTime()); return U.fmtClock ? U.fmtClock(ms) : Math.round(ms / 60000) + ' min'; }
  function btn(label, kind, onClick, cls) {
    var b = h('button', { type: 'button', class: (kind === 'gold' ? 'btn-gold' : 'btn-ghost' + (kind === 'danger' ? ' danger' : '')) + (cls ? ' ' + cls : '') }, [label]);
    b.addEventListener('click', function () { onClick(b); });
    return b;
  }
  async function act(b, fn, args, okMsg) {
    if (b) b.disabled = true;
    try {
      var r = await api.rpc(fn, args);
      if (!r.ok) { ui.toast(api.message(r.error)); return null; }
      if (r.party !== undefined) adopt(r.party); else refresh();
      if (okMsg) ui.toast(okMsg);
      return r;
    } catch (e) { ui.toast(api.message(e)); return null; }
    finally { if (b) b.disabled = false; }
  }
  function mine(g) { return g ? g.members.filter(function (m) { return m.is_me; })[0] || null : null; }
  function completenessLabel(c) {
    return { complete: 'Complete — everyone linked', reported: 'Includes manually reported counts', estimated: 'Includes estimates', partial: 'Partial — some teammates unknown', unconfirmed: 'Not confirmed',
      pending: 'Waiting for PC replays' }[c] || c;
  }
  // PC players on automatic tracking count with their replay, which arrives after the match: until then they
  // show as pending, never as zero.
  function countText(m) {
    if (!m) return '0';
    if (m.pending) return m.replay_state === 'processing' ? 'Processing…' : 'Awaiting replay';
    if (m.missing) return 'No replay';
    return U.fmtInt(m.chests || 0);
  }
  function srcTag(m) { return m ? h('span', { class: 'src-tag' + (m.source === 'replay' ? ' replay' : ''), text: m.source === 'replay' ? 'Replay' : 'Manual', title: m.source === 'replay' ? 'Counted from this player’s Fortnite replay' : 'Counted live in the Live Counter' }) : null; }
  function pendingNote(g) { return g && g.pending_count ? U.fmtInt(g.pending_count) + (g.pending_count === 1 ? ' player pending' : ' players pending') : ''; }

  // ---------------- state ----------------
  function adopt(p) {
    party = p || null;
    knownVersion = party ? party.version : -1;
    watchParty();
    renderAll();
    U.bus.emit('party', party);
  }
  async function refresh() {
    if (fetching) return fetching;
    fetching = (async function () {
      try { var r = await api.rpc('tk_party_state', {}); if (r && r.ok) adopt(r.party); }
      catch (e) { /* offline: keep showing the last known state */ }
      finally { fetching = null; }
    })();
    return fetching;
  }
  function watchParty() {
    var id = party ? party.id : null;
    if (id === watchedId) return;
    if (watcher) watcher.stop();
    watchedId = id;
    watcher = id ? api.watch('tk_party_signals', 'party_id', id, U.debounce(refresh, 120)) : null;
    schedulePoll();
  }
  // Polling fallback when live updates aren't connected: quick during a squad game, slow otherwise.
  function schedulePoll() {
    clearTimeout(pollTimer);
    if (!party) return;
    var live = watcher && watcher.live();
    var ms = live ? 45000 : (party.game ? 3000 : 15000);
    if (document.visibilityState === 'hidden') ms = Math.max(ms, live ? 60000 : 10000);
    pollTimer = setTimeout(function () { refresh().then(schedulePoll); }, ms);
  }

  // ---------------- chest events (outbox) ----------------
  function onChestAdded(info) {
    if (!party || !info || !info.gid) return;
    var ev = { op: 'add', id: U.uuid(), at: new Date(api.now()).toISOString(), sref: info.sid, gref: info.gid };
    linked.push({ id: ev.id, gid: info.gid, idx: info.idx, st: 'queued' });
    outbox.push(ev); saveQueues();
    var m = party.game && mine(party.game);           // optimistic: show it right away, confirmed by the server
    if (m && m.source !== 'replay') { m.chests = (m.chests || 0) + 1; party.game.known_total += 1; renderAll(); }
    flush();
  }
  function onChestRemoved(info) {
    if (!info || !info.gid || info.idx == null) return;
    var at = -1;
    for (var i = linked.length - 1; i >= 0; i--) { if (linked[i].gid === info.gid && linked[i].idx === info.idx) { at = i; break; } }
    if (at < 0) return;
    var entry = linked.splice(at, 1)[0];
    if (entry.st === 'unlinked') { saveQueues(); return; }
    var q = outbox.findIndex(function (o) { return o.op === 'add' && o.id === entry.id; });
    if (q >= 0 && !(sending && q === 0)) outbox.splice(q, 1);           // never sent: the two cancel out
    else outbox.push({ op: 'remove', id: entry.id });
    saveQueues();
    var m = party && party.game && mine(party.game);
    if (m && m.source !== 'replay' && m.chests > 0 && entry.st !== 'unlinked') { m.chests -= 1; party.game.known_total -= 1; renderAll(); }
    flush();
  }
  async function flush() {
    if (sending || !outbox.length || TK.auth.offline()) return;
    sending = true; clearTimeout(retryTimer);
    var delay = 0;
    try {
      while (outbox.length) {
        var op = outbox[0], r;
        try {
          r = op.op === 'add'
            ? await api.rpc('tk_party_chest_add', { p_event_id: op.id, p_opened_at: op.at, p_session_ref: op.sref, p_game_ref: op.gref })
            : await api.rpc('tk_party_chest_remove', { p_event_id: op.id });
        } catch (e) {
          if (e.kind === 'auth') { outbox.length = 0; saveQueues(); return; }
          delay = 5000; break;                                              // offline: keep the queue, retry later
        }
        if (!r.ok && r.error === 'rate_limited') { delay = 10000; break; }
        if (op.op === 'add') {
          var entry = linked.filter(function (x) { return x.id === op.id; })[0];
          if (entry) { entry.st = r.ok && r.linked ? 'linked' : 'unlinked'; entry.game = r.game_id || null; }
          if (r.ok && r.auto_started) ui.toast('Squad game ' + r.game_number + ' started with your chest.');
        }
        outbox.shift(); saveQueues();
      }
    } finally {
      sending = false;
      renderAll();
      if (delay) retryTimer = setTimeout(flush, delay);
      else if (!(watcher && watcher.live())) refresh();
    }
  }

  // ---------------- Live Counter squad panel (secondary to your own count) ----------------
  function squadPanel() {
    var box = els.squad; if (!box) return;
    box.hidden = false; box.innerHTML = '';
    if (!party) {
      box.className = 'trk-squad slim';
      box.appendChild(h('div', { class: 'sq-slim' }, [h('span', { class: 'sq-ico' }, []), h('span', { text: 'Playing with friends? Track your squad’s chests together.' }), h('a', { class: 'link-btn', href: '#party', text: 'Create or join a party' })]));
      box.querySelector('.sq-ico').innerHTML = '<svg class="i"><use href="#i-users"/></svg>';
      return;
    }
    box.className = 'trk-squad';
    var g = party.game, pend = outbox.filter(function (o) { return o.op === 'add'; }).length;
    var head = h('div', { class: 'sq-head' }, [h('span', { class: 'sq-title' }, [g ? 'Squad game ' + g.number : 'Party · ' + party.members.length + '/4']), g ? h('span', { class: 'live-pill' }, ['Live']) : null, h('a', { class: 'link-btn sq-view', href: '#party', text: 'View party' })]);
    box.appendChild(head);
    if (g) {
      var rows = h('ul', { class: 'sq-members' }, g.members.map(function (m) {
        var pres = (party.members.filter(function (x) { return x.id === m.account_id; })[0] || {}).presence;
        return h('li', { class: 'sq-m' + (m.is_me ? ' me' : '') + (m.left_at ? ' left' : '') }, [av(m, 'sm'), h('span', { class: 'sq-name', text: m.is_me ? 'You' : m.username }), m.left_at ? h('span', { class: 'fine', text: 'left' }) : h('span', { class: 'presence-dot p-' + (pres || 'offline'), title: pres || 'offline' }), srcTag(m), h('span', { class: 'sq-c num' + (m.pending || m.missing ? ' pending' : ''), text: countText(m) })]);
      }));
      box.appendChild(rows);
      box.appendChild(h('div', { class: 'sq-total' }, [h('div', {}, [h('span', { class: 'sq-l', text: g.pending_count ? 'Known chests · ' + pendingNote(g) : 'Known squad chests' }), h('span', { class: 'sq-v num', text: U.fmtInt(g.known_total) })]),
        h('div', {}, [h('span', { class: 'sq-l', text: 'Squad openings — drop chance' }), h('span', { class: 'sq-v num small', text: pct(g.known_total) })])]));
      if (party.is_leader) box.appendChild(h('div', { class: 'sq-actions' }, [btn('End squad game', 'ghost', function (b) { endGame(b, g); }, 'btn-sm')]));
    } else {
      box.appendChild(h('div', { class: 'sq-avatars' }, party.members.map(function (m) { var a = av(m, 'sm'); a.title = m.username; return a; })));
      var note = party.auto_next ? 'The next squad game starts with the first chest anyone opens.' : party.is_leader ? 'Start a squad game when your match begins.' : 'Waiting for ' + leaderName() + ' to start a squad game.';
      box.appendChild(h('p', { class: 'fine', text: note }));
      if (party.is_leader && !party.auto_next) box.appendChild(h('div', { class: 'sq-actions' }, [btn('Start squad game', 'gold', function (b) { act(b, 'tk_party_game_start', {}); }, 'btn-sm')]));
      assessPrompt(box);
    }
    if (pend) box.appendChild(h('p', { class: 'fine sq-pending', text: TK.auth.offline() || !navigator.onLine ? 'Offline — ' + pend + ' chest' + (pend === 1 ? '' : 's') + ' will be added to the squad when you reconnect.' : 'Syncing ' + pend + ' chest' + (pend === 1 ? '' : 's') + '…' }));
  }
  function leaderName() { var l = party && party.members.filter(function (m) { return m.is_leader; })[0]; return l ? l.username : 'the leader'; }
  function assessPrompt(box) {
    var lg = party && party.last_game;
    if (!lg || !lg.is_participant || lg.completeness !== 'unconfirmed' || lg.open_slots <= 0) return;
    if (api.now() - new Date(lg.ended_at).getTime() > 30 * 60000) return;
    box.appendChild(h('div', { class: 'sq-assess' }, [h('span', { text: 'Squad game ' + lg.number + ' saved: ' + U.fmtInt(lg.known_total) + ' known chests. Any teammates not on TOKENIFY?' }),
      h('div', { class: 'sq-actions' }, [btn('Add teammates', 'gold', function () { assess(lg); }, 'btn-sm'), btn('Everyone was linked', 'ghost', function (b) { act(b, 'tk_party_game_confirm', { p_game: lg.id }, 'Marked as a complete linked game.'); }, 'btn-sm')])]));
    if (lastPrompted !== lg.id) { lastPrompted = lg.id; }
  }
  async function endGame(b, g) {
    var r = await act(b, 'tk_party_game_end', { p_game: g.id }, 'Squad game ' + g.number + ' saved.');
    if (r && r.game && r.game.open_slots > 0 && r.game.completeness === 'unconfirmed') setTimeout(function () { refresh(); }, 50);
  }

  // ---------------- party page ----------------
  function renderPage() {
    var pane = els.page; if (!pane) return;
    pane.innerHTML = '';
    if (!party) { pane.appendChild(noParty()); return; }
    var g = party.game;
    // lobby
    var slots = h('div', { class: 'lobby' });
    var gm = {}; if (g) g.members.forEach(function (m) { gm[m.account_id] = m; });
    party.members.forEach(function (m) {
      var slot = h('div', { class: 'slot filled' + (m.is_me ? ' me' : '') + (m.is_leader ? ' has-crown' : '') }, [
        m.is_leader ? h('span', { class: 'crown', title: 'Party leader', 'aria-label': 'Party leader' }) : null,
        av(m, 'xl'), h('b', { class: 'slot-name', text: m.username + (m.is_me ? ' (you)' : '') }), TK.social.presenceTag(m.presence),
        g ? h('span', { class: 'slot-count' }, [h('span', { class: 'num' + (gm[m.id] && (gm[m.id].pending || gm[m.id].missing) ? ' pending' : ''), text: countText(gm[m.id]) }), h('small', {}, [gm[m.id] ? srcTag(gm[m.id]) : null, ' this game'])]) : null
      ]);
      var crown = slot.querySelector('.crown'); if (crown) crown.innerHTML = '<svg class="i"><use href="#i-crown"/></svg>';
      if (party.is_leader && !m.is_me) {
        var more = h('button', { type: 'button', class: 'icon-btn slot-more', 'aria-label': 'Options for ' + m.username }, ['⋯']);
        more.addEventListener('click', function () { memberMenu(m); });
        slot.appendChild(more);
      }
      slots.appendChild(slot);
    });
    for (var i = party.members.length; i < 4; i++) {
      var canInvite = party.is_leader || party.members_can_invite;
      var empty = h('button', { type: 'button', class: 'slot empty', disabled: !canInvite }, [h('span', { class: 'slot-plus' }), h('b', { text: canInvite ? 'Invite friend' : 'Open slot' })]);
      empty.querySelector('.slot-plus').innerHTML = '<svg class="i"><use href="#i-plus"/></svg>';
      if (canInvite) empty.addEventListener('click', pickFriend);
      slots.appendChild(empty);
    }
    var lobby = h('section', { class: 'panel party-lobby' }, [h('div', { class: 'panel-head' }, [h('h2', { class: 'panel-title', text: party.is_leader ? 'Your party' : leaderName() + '’s party' }), h('span', { class: 'fine', text: party.members.length + '/4 players' })]), slots]);
    if (party.invites.length) {
      lobby.appendChild(h('ul', { class: 'invites' }, party.invites.map(function (inv) {
        var mins = Math.max(1, Math.round((new Date(inv.expires_at) - api.now()) / 60000));
        var li = h('li', {}, [av(inv.to, 'sm'), h('span', { text: 'Invited ' + inv.to.username + ' · expires in ' + mins + ' min' })]);
        if (party.is_leader || inv.from_id === me.id) li.appendChild(btn('Cancel', 'ghost', function (b) { act(b, 'tk_party_invite_cancel', { p_invite: inv.id }); }, 'btn-sm'));
        return li;
      })));
    }
    pane.appendChild(lobby);
    // squad game
    pane.appendChild(gameCard());
    // leader tools + code + leave
    pane.appendChild(controls());
  }
  function noParty() {
    var wrap = h('div', { class: 'party-empty' });
    var createBtn = btn('Create party', 'gold', function (b) { act(b, 'tk_party_create', {}, 'Party created. Invite your friends!'); }, 'btn-xl');
    var code = h('input', { type: 'text', placeholder: 'XXXX-XXXX', maxlength: '9', autocomplete: 'off', autocapitalize: 'characters', spellcheck: 'false', 'aria-label': 'Party code' });
    var join = btn('Join', 'ghost', async function (b) {
      var v = code.value.trim(); if (!v) { code.focus(); return; }
      var r = await act(b, 'tk_party_join_code', { p_code: v }, 'You joined the party!');
      if (r) code.value = '';
    });
    code.addEventListener('keydown', function (e) { if (e.key === 'Enter') join.click(); });
    wrap.appendChild(h('section', { class: 'panel party-hero lux' }, [h('div', {}, [h('span', { class: 'eyebrow', text: 'Squads' }), h('h2', { class: 'party-hero-title', text: 'Track chests as a squad' }),
      h('p', { class: 'muted', text: 'Up to four players. Everyone counts their own chests. The squad total adds up live, and your personal count is never affected.' }), createBtn]),
      h('div', { class: 'party-join' }, [h('span', { class: 'field-label', text: 'Got a party code?' }), h('div', { class: 'join-row' }, [code, join])])]));
    var inv = TK.social.state() ? TK.social.state().party_invites : [];
    if (inv.length) {
      wrap.appendChild(h('section', { class: 'panel' }, [h('h2', { class: 'panel-title', text: 'Party invites' }), h('ul', { class: 'people' }, inv.map(function (i) {
        return h('li', { class: 'person' }, [av(i.from, 'md'), h('div', { class: 'person-main' }, [h('b', { class: 'person-name', text: i.from.username + ' invited you' }), h('span', { class: 'fine', text: i.members + '/4 players' })]),
          h('div', { class: 'person-actions' }, [btn('Join', 'gold', function () { TK.social.respondInvite(i, true); }, 'btn-sm'), btn('Decline', 'ghost', function () { TK.social.respondInvite(i, false); }, 'btn-sm')])]);
      }))]));
    }
    wrap.appendChild(historyButton());
    return wrap;
  }
  function gameCard() {
    var g = party.game, lg = party.last_game, card = h('section', { class: 'panel game-card' + (g ? ' is-live' : '') });
    if (g) {
      card.appendChild(h('div', { class: 'panel-head' }, [h('h2', { class: 'panel-title' }, ['Squad game ' + g.number + ' ', h('span', { class: 'live-pill', text: 'Live' })]), h('span', { class: 'fine game-clock', text: elapsed(g.started_at) })]));
      card.appendChild(h('div', { class: 'game-totals' }, [
        h('div', { class: 'gt main' }, [h('span', { class: 'gt-l', text: g.pending_count ? 'Known chests' : 'Known squad chests' }), h('span', { class: 'gt-v num', text: U.fmtInt(g.known_total) }),
          h('span', { class: 'fine', text: g.pending_count ? pendingNote(g) + ': PC replays are added when each match ends' : 'Recorded by ' + g.members.filter(function (m) { return !m.left_at; }).length + ' linked player' + (g.members.length === 1 ? '' : 's') })]),
        h('div', { class: 'gt' }, [h('span', { class: 'gt-l', text: 'Squad openings — drop chance' }), h('span', { class: 'gt-v num', text: pct(g.known_total) }), h('span', { class: 'fine', text: 'Chance of at least one Pirate Token drop in these openings' })])
      ]));
      card.appendChild(h('ul', { class: 'contrib' }, g.members.map(function (m) {
        var share = g.known_total && m.chests ? m.chests / g.known_total : 0;
        return h('li', { class: m.is_me ? 'me' : '' }, [av(m, 'sm'), h('span', { class: 'c-name', text: (m.is_me ? 'You' : m.username) + (m.left_at ? ' (left)' : m.former ? ' (former player)' : '') }), srcTag(m), h('span', { class: 'c-bar' }, [h('span', { style: { width: (share * 100).toFixed(1) + '%' } })]), h('span', { class: 'c-n num' + (m.pending || m.missing ? ' pending' : ''), text: countText(m) })]);
      })));
      if (party.is_leader) card.appendChild(h('div', { class: 'row-actions' }, [btn('End squad game', 'gold', function (b) { endGame(b, g); })]));
      else card.appendChild(h('p', { class: 'fine', text: 'Count your own chests in the Live Counter. ' + leaderName() + ' ends the squad game.' }));
    } else {
      card.appendChild(h('div', { class: 'panel-head' }, [h('h2', { class: 'panel-title', text: 'Squad game' }), h('span', { class: 'fine', text: party.auto_next ? 'Auto-start is on' : '' })]));
      card.appendChild(h('p', { class: 'muted', text: party.auto_next ? 'The next squad game starts with the first chest anyone in the party opens.' : party.is_leader ? 'Start a squad game when your match begins. Everyone then counts their own chests as usual.' : 'Waiting for ' + leaderName() + ' to start the next squad game.' }));
      if (party.is_leader && !party.auto_next) card.appendChild(h('div', { class: 'row-actions' }, [btn('Start squad game', 'gold', function (b) { act(b, 'tk_party_game_start', {}, 'Squad game started. Count your chests as usual!'); }, 'btn-xl')]));
      if (lg) card.appendChild(lastGameSummary(lg));
    }
    return card;
  }
  function lastGameSummary(lg) {
    var wrap = h('div', { class: 'last-game' }, [h('div', { class: 'field-label', text: 'Last squad game (' + lg.number + ')' }),
      h('div', { class: 'lg-line' }, [h('b', { class: 'num', text: U.fmtInt(lg.known_total) + ' known chests' }), lg.pending_count ? h('span', { text: ' · ' + pendingNote(lg) }) : null, lg.estimated_squad_total !== lg.known_total ? h('span', { text: ' · ≈' + fmtCount(Number(lg.estimated_squad_total)) + ' with teammates' }) : null, h('span', { class: 'tag sm comp-' + lg.completeness, text: completenessLabel(lg.completeness) })])]);
    if (lg.members.some(function (m) { return m.source === 'replay' || m.pending || m.missing; })) {
      wrap.appendChild(h('ul', { class: 'contrib slim' }, lg.members.map(function (m) {
        return h('li', { class: m.is_me ? 'me' : '' }, [av(m, 'sm'), h('span', { class: 'c-name', text: m.is_me ? 'You' : m.username }), srcTag(m), h('span', { class: 'c-n num' + (m.pending || m.missing ? ' pending' : ''), text: countText(m) })]);
      })));
    }
    var me = mine(lg);
    if (me && me.missing) {
      wrap.appendChild(h('div', { class: 'row-actions' }, [h('span', { class: 'fine', text: 'Your PC’s replay for this game never arrived.' }),
        btn('Use what I counted live (' + U.fmtInt(me.live_chests || 0) + ')', 'ghost', function (b) { act(b, 'tk_party_use_live_count', { p_game: lg.id }, 'Squad game ' + lg.number + ' now uses your live count.'); }, 'btn-sm')]));
    }
    if (lg.is_participant && lg.open_slots > 0) wrap.appendChild(h('div', { class: 'row-actions' }, [btn(lg.teammates.length ? 'Edit teammates' : 'Add teammates not on TOKENIFY', 'ghost', function () { assess(lg); }, 'btn-sm'),
      lg.completeness === 'unconfirmed' ? btn('Everyone was linked', 'ghost', function (b) { act(b, 'tk_party_game_confirm', { p_game: lg.id }, 'Marked as a complete linked game.'); }, 'btn-sm') : null].filter(Boolean)));
    return wrap;
  }
  function controls() {
    var sec = h('section', { class: 'panel party-controls' }, [h('h2', { class: 'panel-title', text: party.is_leader ? 'Leader tools' : 'Party' })]);
    if (party.is_leader) {
      var codeBox = h('div', { class: 'code-box' });
      var showCode = function (code, exp) {
        codeBox.innerHTML = '';
        codeBox.appendChild(h('div', { class: 'party-code' }, [h('span', { class: 'code-v mono', text: code }), h('span', { class: 'fine', text: 'Expires ' + U.fmtTime(exp) })]));
        codeBox.appendChild(h('div', { class: 'row-actions' }, [btn('Copy', 'ghost', function () { (navigator.clipboard ? navigator.clipboard.writeText(code) : Promise.reject()).then(function () { ui.toast('Party code copied.'); }, function () { ui.toast('Couldn’t copy. Read it out instead!'); }); }, 'btn-sm'),
          btn('Revoke', 'ghost', function (b) { act(b, 'tk_party_code_revoke', {}, 'Party code revoked.').then(function () { codeBox.innerHTML = ''; codeBox.appendChild(codeIntro()); }); }, 'btn-sm')]));
      };
      var codeIntro = function () {
        return h('div', {}, [h('p', { class: 'fine', text: party.code_active ? 'A code is active (until ' + U.fmtTime(party.code_expires_at) + '). Make a new one to see it again; the old one stops working.' : 'Let someone join without a friend request: share a code that works for 30 minutes.' }),
          btn(party.code_active ? 'New party code' : 'Create party code', 'ghost', async function (b) { b.disabled = true; try { var r = await api.rpc('tk_party_code_create', {}); if (r.ok) showCode(r.code, r.expires_at); else ui.toast(api.message(r.error)); } catch (e) { ui.toast(api.message(e)); } b.disabled = false; }, 'btn-sm')]);
      };
      codeBox.appendChild(codeIntro());
      sec.appendChild(h('div', { class: 'field-label', text: 'Party code' }));
      sec.appendChild(codeBox);
      sec.appendChild(settingToggle('Auto-start the next squad game', 'The first chest anyone opens after a game ends starts the next one.', party.auto_next, function (v) { return act(null, 'tk_party_settings', { p_auto_next: v }); }));
      sec.appendChild(settingToggle('Members can invite friends', 'Off: only you can invite.', party.members_can_invite, function (v) { return act(null, 'tk_party_settings', { p_members_can_invite: v }); }));
    }
    sec.appendChild(h('div', { class: 'row-actions' }, [historyButton(true),
      btn('Leave party', 'ghost', async function (b) { if (await ui.confirm({ title: 'Leave the party?', message: 'Your chests in any running squad game stay counted. Your own Live Counter isn’t affected.' + (party.is_leader && party.members.length > 1 ? ' Leadership passes to the next player.' : ''), confirmLabel: 'Leave' })) act(b, 'tk_party_leave', {}, 'You left the party.').then(refresh); }),
      party.is_leader && party.members.length > 1 ? btn('Disband', 'danger', async function (b) { if (await ui.confirm({ title: 'Disband the party?', message: 'Everyone leaves and any running squad game ends. Squad history is kept for all players.', confirmLabel: 'Disband', danger: true })) act(b, 'tk_party_disband', {}, 'Party disbanded.').then(refresh); }) : null].filter(Boolean)));
    return sec;
  }
  function settingToggle(label, sub, checked, onChange) {
    var i = h('input', { type: 'checkbox' }); i.checked = !!checked;
    i.addEventListener('change', function () { var v = i.checked; Promise.resolve(onChange(v)).then(function (r) { if (!r) i.checked = !v; }); });
    return h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: label }), h('span', { text: sub })]), h('label', { class: 'toggle' }, [i, h('span', { class: 'track' }), h('span', { class: 'sr-only', text: label })])]);
  }
  function memberMenu(m) {
    var md = ui.modal({ title: m.username, sub: 'Party member', body: h('div', { class: 'stack' }, [
      btn('Make party leader', 'ghost', async function (b) { md.close(); if (await ui.confirm({ title: 'Make ' + m.username + ' the leader?', message: 'They’ll control squad games and the party. You stay in the party.', confirmLabel: 'Make leader' })) act(b, 'tk_party_transfer', { p_account: m.id }, m.username + ' is now the party leader.'); }),
      btn('Remove from party', 'danger', async function (b) { md.close(); if (await ui.confirm({ title: 'Remove ' + m.username + '?', message: 'Their chests in the current squad game stay counted.', confirmLabel: 'Remove', danger: true })) act(b, 'tk_party_kick', { p_account: m.id }, m.username + ' was removed from the party.'); })
    ]), actions: [{ label: 'Close', kind: 'ghost' }] });
  }
  function pickFriend() {
    var st = TK.social.state(), inParty = {};
    party.members.forEach(function (m) { inParty[m.id] = 1; });
    party.invites.forEach(function (i) { inParty[i.to.id] = 2; });
    var list = (st ? st.friends : []).slice().sort(function (a, b) { return (a.presence === 'offline') - (b.presence === 'offline'); });
    var body = h('div', {});
    if (!list.length) body.appendChild(h('div', { class: 'empty' }, [h('b', { text: 'No friends yet' }), h('span', { text: 'Add friends by username first, or share a party code.' }), h('a', { class: 'link-btn', href: '#friends', text: 'Find friends' })]));
    else body.appendChild(h('ul', { class: 'people' }, list.map(function (f) {
      var state = inParty[f.id];
      return h('li', { class: 'person' }, [av(f, 'md'), h('div', { class: 'person-main' }, [h('b', { class: 'person-name', text: f.username }), TK.social.presenceTag(f.presence)]),
        h('div', { class: 'person-actions' }, [state === 1 ? h('span', { class: 'rel-tag ok', text: 'In party' }) : state === 2 ? h('span', { class: 'rel-tag', text: 'Invited' }) :
          btn('Invite', 'gold', function (b) { act(b, 'tk_party_invite', { p_account: f.id }, 'Invite sent to ' + f.username + '.').then(function (r) { if (r) { b.replaceWith(h('span', { class: 'rel-tag', text: 'Invited' })); } }); }, 'btn-sm')])]);
    })));
    ui.modal({ title: 'Invite to party', sub: (4 - party.members.length) + ' open slot' + (party.members.length === 3 ? '' : 's'), body: body, actions: [{ label: 'Done', kind: 'gold' }] });
  }

  // ---------------- after-game teammate assessment ----------------
  function assess(game) {
    var acc = TK.auth.account(), est = (acc.settings && acc.settings.est) || { barely: 25, some: 60, hunted: 100 };
    var myCount = (mine(game) || { chests: 0 }).chests;
    var rowsBox = h('div', { class: 'mates' }), rows = [], max = Math.min(3, game.open_slots);
    function addRow(t) {
      if (rows.length >= max) return;
      var slot = t ? t.slot : nextSlot();
      var label = h('input', { type: 'text', maxlength: '24', placeholder: 'Teammate ' + slot + ' (optional name)', 'aria-label': 'Teammate name' }); label.value = t && t.label ? t.label : '';
      var mode = t ? t.mode : 'estimated', level = t && t.estimate_level ? t.estimate_level : 'some';
      var exact = h('input', { type: 'number', min: '0', max: '999', step: '1', inputmode: 'numeric', 'aria-label': 'Chests opened', placeholder: '0' }); if (t && t.exact_count != null) exact.value = t.exact_count;
      var modeSeg = h('div', { class: 'seg', role: 'radiogroup', 'aria-label': 'How do you know?' }, [['exact', 'Exact'], ['estimated', 'Estimated'], ['unknown', 'Unknown']].map(function (o) { return h('button', { type: 'button', role: 'radio', 'aria-checked': String(mode === o[0]), 'data-v': o[0] }, [o[1]]); }));
      var levelSeg = h('div', { class: 'chips', role: 'radiogroup', 'aria-label': 'How much did they loot?' }, [['barely', 'Barely looted'], ['some', 'Some looting'], ['hunted', 'Hunted chests']].map(function (o) { return h('button', { type: 'button', role: 'radio', class: 'chip', 'aria-checked': String(level === o[0]), 'data-v': o[0] }, [o[1]]); }));
      var exactBox = h('div', { class: 'mate-exact' }, [h('label', { class: 'field-label', text: 'Chests opened (as reported)' }), exact]);
      var preview = h('span', { class: 'fine' });
      var estBox = h('div', { class: 'mate-est' }, [levelSeg, preview]);
      var unkBox = h('p', { class: 'fine', text: 'No count is invented: this game will be marked partial.' });
      function sync() {
        exactBox.hidden = mode !== 'exact'; estBox.hidden = mode !== 'estimated'; unkBox.hidden = mode !== 'unknown';
        preview.textContent = '≈ ' + fmtCount(Math.round(myCount * est[level] / 10) / 10) + ' chests (' + est[level] + '% of your ' + myCount + ').';
      }
      ui.bindRadios(modeSeg, 'data-v', function (v) { mode = v; sync(); });
      ui.bindRadios(levelSeg, 'data-v', function (v) { level = v; sync(); });
      var rm = h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Remove teammate', title: 'Remove' }, ['×']);
      var el = h('div', { class: 'mate' }, [h('div', { class: 'mate-top' }, [label, rm]), modeSeg, exactBox, estBox, unkBox]);
      var row = { slot: slot, el: el, get: function () { return { slot: slot, label: label.value.trim(), mode: mode, exact: exact.value === '' ? null : parseInt(exact.value, 10), level: level }; } };
      rm.addEventListener('click', function () { rows.splice(rows.indexOf(row), 1); el.remove(); addBtn.disabled = rows.length >= max; });
      rows.push(row); rowsBox.appendChild(el); sync();
      addBtn.disabled = rows.length >= max;
    }
    function nextSlot() { for (var s = 1; s <= 3; s++) if (!rows.some(function (r) { return r.slot === s; })) return s; return 3; }
    var addBtn = btn('Add teammate', 'ghost', function () { addRow(null); }, 'btn-sm');
    var msg = h('p', { class: 'msg-error', role: 'alert' });
    (game.teammates || []).forEach(function (t) { addRow(t); });
    if (!rows.length) addRow(null);
    var body = h('div', { class: 'assess' }, [
      h('p', { class: 'muted', text: 'Linked players: ' + game.members.map(function (m) { return (m.is_me ? 'You' : m.username) + ' ' + countText(m); }).join(' · ') + ' (' + game.known_total + ' known). Optional, and only for players who don’t use TOKENIFY.' }),
      rowsBox, h('div', {}, [addBtn]), msg,
      h('p', { class: 'fine', text: 'Exact counts are marked as manually reported. Estimates use your adjustable assumptions (Settings › Squad estimates). Linked players are never estimated.' })
    ]);
    var md = ui.modal({ title: 'Squad game ' + game.number + ' — teammates', sub: 'Up to ' + max + ' player' + (max === 1 ? '' : 's') + ' not on TOKENIFY', wide: true, body: body,
      actions: [{ label: 'Cancel', kind: 'ghost' }, { label: 'Save', kind: 'gold', keepOpen: true, onClick: async function (close) {
        msg.textContent = '';
        var vals = rows.map(function (r) { return r.get(); });
        var bad = vals.filter(function (v) { return v.mode === 'exact' && !(v.exact >= 0 && v.exact <= 999); })[0];
        if (bad) { msg.textContent = api.message('invalid_count'); return false; }
        try {
          var keep = {}; vals.forEach(function (v) { keep[v.slot] = 1; });
          for (var i = 0; i < (game.teammates || []).length; i++) { var t = game.teammates[i]; if (!keep[t.slot]) await api.rpc('tk_party_teammate_clear', { p_game: game.id, p_slot: t.slot }); }
          for (var j = 0; j < vals.length; j++) {
            var v = vals[j];
            var r = await api.rpc('tk_party_teammate_set', { p_game: game.id, p_slot: v.slot, p_label: v.label || null, p_mode: v.mode, p_exact: v.mode === 'exact' ? v.exact : null, p_level: v.mode === 'estimated' ? v.level : null });
            if (!r.ok) { msg.textContent = api.message(r.error); return false; }
          }
          if (!vals.length && game.completeness === 'unconfirmed') await api.rpc('tk_party_game_confirm', { p_game: game.id });
          close(); ui.toast('Squad game ' + game.number + ' updated.'); refresh(); history = null;
        } catch (e) { msg.textContent = api.message(e); }
        return false;
      } }] });
    return md;
  }

  // ---------------- squad history & stats ----------------
  function historyButton(small) { return btn('Squad history & stats', 'ghost', openHistory, small ? 'btn-sm' : ''); }
  async function openHistory() {
    var body = h('div', { class: 'history' }, [h('p', { class: 'muted', text: 'Loading…' })]);
    var md = ui.modal({ title: 'Squad history', sub: 'Shared games you played in', wide: true, body: body, actions: [{ label: 'Close', kind: 'gold' }] });
    try {
      var r = await api.rpc('tk_party_history', { p_limit: 25 });
      history = r;
      body.innerHTML = '';
      body.appendChild(statsBlock(r.stats));
      if (!r.games.length) { body.appendChild(h('div', { class: 'empty' }, [h('b', { text: 'No squad games yet' }), h('span', { text: 'Start a squad game from your party and they’ll show up here.' })])); return; }
      body.appendChild(h('ul', { class: 'hist-list' }, r.games.map(function (g) {
        var who = g.members.map(function (m) { return (m.is_me ? 'You' : m.username) + ' ' + countText(m); }).join(' · ');
        var mates = g.teammates.map(function (t) { return (t.label || 'Teammate ' + t.slot) + ': ' + (t.mode === 'exact' ? t.exact_count + ' (reported)' : t.mode === 'estimated' ? '≈' + fmtCount(Number(t.estimate_count)) + ' (estimated)' : 'unknown'); }).join(' · ');
        var li = h('li', { class: g.void ? 'void' : '' }, [h('div', { class: 'hl-top' }, [h('b', { text: 'Game ' + g.number }), h('span', { class: 'fine', text: new Date(g.started_at).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) }), h('span', { class: 'tag sm comp-' + (g.void ? 'void' : g.completeness), text: g.void ? 'Void' : completenessLabel(g.completeness) })]),
          h('div', { class: 'hl-mid' }, [h('span', { class: 'num hl-total', text: U.fmtInt(g.known_total) }), h('span', { text: ' known' }), g.estimated_squad_total !== g.known_total ? h('span', { class: 'fine', text: ' · ≈' + fmtCount(Number(g.estimated_squad_total)) + ' incl. teammates' }) : null]),
          h('div', { class: 'fine', text: who + (mates ? ' · ' + mates : '') })]);
        var acts = [];
        if (g.is_participant && g.open_slots > 0 && !g.void) acts.push(btn(g.teammates.length ? 'Edit teammates' : 'Add teammates', 'ghost', function () { md.close(); assess(g); }, 'btn-sm'));
        if (party && party.is_leader && party.id === g.party_id) acts.push(btn(g.void ? 'Restore game' : 'Void game', 'ghost', function (b) { act(b, 'tk_party_game_void', { p_game: g.id, p_void: !g.void }, g.void ? 'Game restored.' : 'Game voided (excluded from squad CPG).').then(function () { md.close(); openHistory(); }); }, 'btn-sm'));
        if (acts.length) li.appendChild(h('div', { class: 'row-actions' }, acts));
        return li;
      })));
    } catch (e) { body.innerHTML = ''; body.appendChild(h('p', { class: 'muted', text: api.message(e) })); }
  }
  function statsBlock(s) {
    var lc = s.linked.games ? s.linked.chests / s.linked.games : null, ec = s.estimated.games ? s.estimated.chests / s.estimated.games : null;
    var rate = s.linked_rate.games >= 3 && s.linked_rate.seconds > 0 ? s.linked_rate.chests / (s.linked_rate.seconds / 3600) : null;
    var eta = rate ? Number(TOKEN) / rate : null;
    var grid = h('div', { class: 'squad-stats' }, [
      h('div', { class: 'ss' }, [h('span', { class: 'ss-l', text: 'Linked squad CPG' }), h('span', { class: 'ss-v num', text: lc == null ? '—' : fmtCount(Math.round(lc * 10) / 10) }), h('span', { class: 'fine', text: s.linked.games ? 'From ' + s.linked.games + ' fully linked game' + (s.linked.games === 1 ? '' : 's') : 'Needs games where everyone used TOKENIFY' })]),
      h('div', { class: 'ss' }, [h('span', { class: 'ss-l', text: 'Estimated squad CPG' }), h('span', { class: 'ss-v num', text: ec == null ? '—' : '≈' + fmtCount(Math.round(ec * 10) / 10) }), h('span', { class: 'fine', text: s.estimated.games ? 'From ' + s.estimated.games + ' game' + (s.estimated.games === 1 ? '' : 's') + ' incl. reported & estimated teammates' : 'Add teammates after a game to see this' })]),
      h('div', { class: 'ss' }, [h('span', { class: 'ss-l', text: 'Squad-wide drop estimate' }), h('span', { class: 'ss-v num', text: eta == null ? '—' : (eta >= 48 ? '≈' + U.fmtInt(Math.round(eta / 24)) + ' days' : '≈' + fmtCount(Math.round(eta * 10) / 10) + ' h') }), h('span', { class: 'fine', text: eta == null ? 'Needs 3+ fully linked games of 5+ minutes' : 'Average squad play time per Pirate Token drop at your linked squad pace (' + U.fmtInt(Math.round(rate)) + ' chests/h)' })])
    ]);
    var notes = [];
    if (s.partial_games) notes.push(s.partial_games + ' partial game' + (s.partial_games === 1 ? '' : 's') + ' (unknown teammates) not included');
    if (s.unconfirmed_games) notes.push(s.unconfirmed_games + ' unconfirmed game' + (s.unconfirmed_games === 1 ? '' : 's') + ' not included');
    return h('div', {}, [grid, h('p', { class: 'fine', text: (notes.length ? notes.join(' · ') + '. ' : '') + 'Squad-wide figures describe drops anywhere in the squad, not your personal chance. Each chest is always exactly 1 in 137,500.' })]);
  }

  // ---------------- render everything that shows party data ----------------
  function renderAll() {
    squadPanel();
    if (TK.app && TK.app.view() === 'social' && TK.social.current() === 'party') renderPage();
    U.bus.emit('party-render', party);
  }

  TK.party = {
    init: function (acc) {
      me = acc;
      els = { squad: U.$('#trk-squad'), page: U.$('#soc-party') };
      loadQueues();
      if (TK.tracker) TK.tracker.on(function (what, model) {
        if (what === 'chest') onChestAdded(model.lastAdded);
        else if (what === 'chest-remove') onChestRemoved(model.lastRemoved);
      });
      refresh().then(function () { flush(); });
      window.addEventListener('online', function () { flush(); refresh(); });
      document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'visible') refresh(); schedulePoll(); });
      U.bus.on('realtime-status', function () { schedulePoll(); });
      setInterval(function () { var c = U.$('.game-clock'); if (c && party && party.game) c.textContent = elapsed(party.game.started_at); }, 1000);
      squadPanel();
    },
    onHeartbeat: function (partyId, version) {
      if ((party ? party.id : null) !== (partyId || null) || (party && version !== knownVersion)) refresh();
    },
    refresh: refresh, adopt: adopt, renderPage: renderPage, openHistory: openHistory,
    state: function () { return party; },
    pending: function () { return outbox.length; }
  };
})(window.TK = window.TK || {});
