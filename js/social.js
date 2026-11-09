/* TOKENIFY — friends: username search, requests, presence, blocking, in-app notifications.
 * State comes from tk_social_state(); changes arrive through a live signal (Supabase Realtime) with a
 * heartbeat fallback, so nothing here is simulated or stored only in the browser. */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h, api = TK.api;
  var me = null, data = null, sub = 'party', els = {}, watcher = null, beatTimer = null, known = { account: -1, party: -1 }, seen = null, refreshing = null;
  var PRESENCE = { online: 'Online', tracking: 'Tracking', in_party: 'In party', offline: 'Offline' };

  function av(u, size) { return TK.profile.avatar(u, size); }
  function presenceTag(p) { return h('span', { class: 'presence p-' + (p || 'offline') }, [h('span', { class: 'presence-dot', 'aria-hidden': 'true' }), PRESENCE[p] || 'Offline']); }
  function smallBtn(label, kind, onClick, extra) {
    var b = h('button', { type: 'button', class: (kind === 'gold' ? 'btn-gold' : 'btn-ghost' + (kind === 'danger' ? ' danger' : '')) + ' btn-sm' + (extra ? ' ' + extra : '') }, [label]);
    b.addEventListener('click', function () { onClick(b); });
    return b;
  }
  async function act(btn, fn, args, okMsg) {
    if (btn) btn.disabled = true;
    try {
      var r = await api.rpc(fn, args);
      if (!r.ok) { ui.toast(api.message(r.error)); if (btn) btn.disabled = false; return null; }
      if (okMsg) ui.toast(typeof okMsg === 'function' ? okMsg(r) : okMsg);
      refresh();
      return r;
    } catch (e) { ui.toast(api.message(e)); if (btn) btn.disabled = false; return null; }
  }

  // ---------------- state ----------------
  async function refresh() {
    if (refreshing) return refreshing;
    refreshing = (async function () {
      try {
        var r = await api.rpc('tk_social_state', {});
        if (!r || !r.ok) return;
        notifyNew(r);
        data = r; known.account = r.version;
        render();
        U.bus.emit('social', data);
      } catch (e) { /* offline: keep the last state; the heartbeat retries */ }
      finally { refreshing = null; }
    })();
    return refreshing;
  }
  function notifyNew(r) {
    if (!seen) { seen = { req: {}, inv: {} }; r.incoming.forEach(function (x) { seen.req[x.id] = 1; }); r.party_invites.forEach(function (x) { seen.inv[x.id] = 1; }); return; }
    r.incoming.forEach(function (x) {
      if (seen.req[x.id]) return; seen.req[x.id] = 1;
      ui.toast(x.user.username + ' sent you a friend request.', { action: { label: 'View', onClick: function () { location.hash = 'requests'; } }, duration: 8000 });
      TK.audio.play('ui');
    });
    r.party_invites.forEach(function (x) {
      if (seen.inv[x.id]) return; seen.inv[x.id] = 1;
      ui.toast(x.from.username + ' invited you to their party.', { action: { label: 'Join', onClick: function () { respondInvite(x, true); } }, duration: 12000 });
      TK.audio.play('coin');
    });
  }
  async function heartbeat() {
    if (!me || TK.auth.offline()) return;
    var status = TK.tracker && TK.tracker.status() === 'active' ? 'tracking' : 'online';
    try {
      var r = await api.rpc('tk_heartbeat', { p_status: status }, { quietAuth: false });
      if (!r || !r.ok) return;
      if (r.account_version !== known.account) refresh();
      if (TK.party) TK.party.onHeartbeat(r.party_id, r.party_version);
    } catch (e) { /* offline or signed out: handled elsewhere */ }
  }
  function scheduleBeat() {
    clearTimeout(beatTimer);
    beatTimer = setTimeout(function () { heartbeat().then(scheduleBeat); }, document.visibilityState === 'hidden' ? 60000 : 30000);
  }

  // ---------------- rendering ----------------
  function badge() {
    var n = data ? data.notifications : 0;
    [els.badge, els.reqBadge].forEach(function (b) { if (!b) return; b.hidden = !n; b.textContent = n > 9 ? '9+' : String(n); });
    els.socialBtn.setAttribute('title', n ? 'Friends & party (' + n + ' new)' : 'Friends & party');
    U.setText(els.friendsN, data && data.friends.length ? '(' + data.friends.length + ')' : '');
  }
  function render() { badge(); renderFriends(); renderRequests(); }

  function friendRow(f) {
    var party = TK.party && TK.party.state();
    var row = h('li', { class: 'person' }, [av(f, 'md'), h('div', { class: 'person-main' }, [h('b', { class: 'person-name', text: f.username }), f.same_party ? h('span', { class: 'tag tag-theory sm', text: 'In your party' }) : presenceTag(f.presence)])]);
    var actions = h('div', { class: 'person-actions' });
    if (party && !f.same_party && (party.is_leader || party.members_can_invite) && party.members.length < 4) {
      var pending = party.invites.some(function (i) { return i.to.id === f.id; });
      actions.appendChild(pending ? h('span', { class: 'fine', text: 'Invited' }) : smallBtn('Invite to party', 'gold', function (b) { act(b, 'tk_party_invite', { p_account: f.id }, 'Party invite sent to ' + f.username + '.').then(function () { if (TK.party) TK.party.refresh(); }); }));
    }
    var more = h('button', { type: 'button', class: 'icon-btn more-btn', 'aria-label': 'More options for ' + f.username, title: 'More' }, ['⋯']);
    more.addEventListener('click', function () { friendMenu(f); });
    actions.appendChild(more);
    row.appendChild(actions);
    return row;
  }
  function friendMenu(f) {
    var m = ui.modal({ title: f.username, sub: 'Friends since ' + new Date(f.since).toLocaleDateString(), body: h('div', { class: 'stack' }, [
      smallBtn('Remove friend', 'ghost', async function (b) { m.close(); if (await ui.confirm({ title: 'Remove ' + f.username + '?', message: 'You can send a new request later. Shared squad history is kept.', confirmLabel: 'Remove' })) act(null, 'tk_friend_remove', { p_account: f.id }, 'Removed ' + f.username + '.'); }),
      smallBtn('Block', 'danger', async function () { m.close(); confirmBlock(f); })
    ]), actions: [{ label: 'Close', kind: 'ghost' }] });
  }
  async function confirmBlock(u) {
    var party = TK.party && TK.party.state(), together = party && party.members.some(function (m) { return m.id === u.id; });
    var msg = 'They won’t be able to find you, send you requests or invite you. They aren’t told.' + (together ? (party.is_leader ? ' They’ll be removed from your party.' : ' You’ll leave your current party.') : '');
    if (!(await ui.confirm({ title: 'Block ' + u.username + '?', message: msg, confirmLabel: 'Block', danger: true }))) return;
    var r = await act(null, 'tk_block', { p_account: u.id }, 'Blocked ' + u.username + '.');
    if (r && r.party_action && TK.party) TK.party.refresh();
  }

  function searchResult(u) {
    var actions = h('div', { class: 'person-actions' });
    if (u.relation === 'none') actions.appendChild(smallBtn('Add friend', 'gold', function (b) { act(b, 'tk_friend_request', { p_account: u.id }, function (r) { return r.status === 'friends' ? 'You and ' + u.username + ' are now friends!' : 'Friend request sent to ' + u.username + '.'; }).then(runSearch); }));
    else if (u.relation === 'outgoing') { actions.appendChild(h('span', { class: 'rel-tag', text: 'Request sent' })); actions.appendChild(smallBtn('Cancel', 'ghost', function (b) { act(b, 'tk_friend_cancel', { p_request: u.request_id }).then(runSearch); })); }
    else if (u.relation === 'incoming') actions.appendChild(smallBtn('Accept request', 'gold', function (b) { act(b, 'tk_friend_respond', { p_request: u.request_id, p_accept: true }, 'You and ' + u.username + ' are now friends!').then(runSearch); }));
    else if (u.relation === 'friends') actions.appendChild(h('span', { class: 'rel-tag ok', text: 'Friends' }));
    else if (u.relation === 'blocked') { actions.appendChild(h('span', { class: 'rel-tag bad', text: 'Blocked' })); actions.appendChild(smallBtn('Unblock', 'ghost', function (b) { act(b, 'tk_unblock', { p_account: u.id }, 'Unblocked ' + u.username + '.').then(runSearch); })); }
    return h('li', { class: 'person' }, [av(u, 'md'), h('div', { class: 'person-main' }, [h('b', { class: 'person-name', text: u.username })]), actions]);
  }
  var searchSeq = 0;
  async function runSearch() {
    var q = els.search ? els.search.value.trim() : '', mine = ++searchSeq;
    if (!els.results) return;
    if (q.length < 2) { els.results.innerHTML = ''; els.results.hidden = true; return; }
    els.results.hidden = false;
    els.results.innerHTML = '<li class="fine pad">Searching…</li>';
    try {
      var r = await api.rpc('tk_user_search', { p_query: q });
      if (mine !== searchSeq) return;
      els.results.innerHTML = '';
      if (!r.ok) { els.results.appendChild(h('li', { class: 'fine pad', text: api.message(r.error) })); return; }
      if (!r.results.length) { els.results.appendChild(h('li', { class: 'fine pad', text: 'No players found for “' + q + '”.' })); return; }
      r.results.forEach(function (u) { els.results.appendChild(searchResult(u)); });
    } catch (e) { if (mine === searchSeq) { els.results.innerHTML = ''; els.results.appendChild(h('li', { class: 'fine pad', text: api.message(e) })); } }
  }
  function renderFriends() {
    var pane = els.friendsPane; if (!pane || !data) return;
    if (!els.search) {
      els.search = h('input', { type: 'search', id: 'friend-search', placeholder: 'Search username', autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', maxlength: '16', 'aria-label': 'Search for a player by username' });
      els.results = h('ul', { class: 'people results', 'aria-live': 'polite', hidden: true });
      els.search.addEventListener('input', U.debounce(runSearch, 260));
      var box = h('div', { class: 'search-box' }); box.innerHTML = '<svg class="i"><use href="#i-search"/></svg>'; box.appendChild(els.search);
      els.friendsList = h('div', {});
      pane.appendChild(h('section', { class: 'panel soc-search' }, [h('h2', { class: 'panel-title', text: 'Add friends' }), h('p', { class: 'panel-sub', text: 'Find other TOKENIFY players by their username.' }), box, els.results]));
      pane.appendChild(els.friendsList);
    }
    var list = els.friendsList; list.innerHTML = '';
    var online = data.friends.filter(function (f) { return f.presence !== 'offline'; }).length;
    var sec = h('section', { class: 'panel' }, [h('div', { class: 'panel-head' }, [h('h2', { class: 'panel-title', text: 'Friends' }), h('span', { class: 'fine', text: data.friends.length ? online + ' of ' + data.friends.length + ' online' : '' })])]);
    if (!data.friends.length) sec.appendChild(h('div', { class: 'empty' }, [h('b', { text: 'No friends yet' }), h('span', { text: 'Search for a username above to send your first request.' })]));
    else sec.appendChild(h('ul', { class: 'people' }, data.friends.map(friendRow)));
    list.appendChild(sec);
    if (data.blocked.length) {
      var d = h('details', { class: 'panel blocked-list' }, [h('summary', { text: 'Blocked players (' + data.blocked.length + ')' })]);
      d.appendChild(h('ul', { class: 'people' }, data.blocked.map(function (b) {
        return h('li', { class: 'person' }, [av(b.user, 'sm'), h('div', { class: 'person-main' }, [h('b', { class: 'person-name', text: b.user.username })]), h('div', { class: 'person-actions' }, [smallBtn('Unblock', 'ghost', function (btn) { act(btn, 'tk_unblock', { p_account: b.user.id }, 'Unblocked ' + b.user.username + '.'); })])]);
      })));
      list.appendChild(d);
    }
  }

  async function respondInvite(inv, accept) {
    var party = TK.party && TK.party.state();
    if (accept && party) {
      var ok = await ui.confirm({ title: 'Switch parties?', message: 'You’ll leave your current party to join ' + inv.from.username + '’s. Your own chest counts are never affected.', confirmLabel: 'Join party' });
      if (!ok) return;
    }
    var r = await act(null, 'tk_party_invite_respond', { p_invite: inv.id, p_accept: accept }, accept ? 'You joined ' + (inv.leader ? inv.leader.username : inv.from.username) + '’s party!' : null);
    if (r && accept && TK.party) { TK.party.adopt(r.party); location.hash = 'party'; }
  }
  function renderRequests() {
    var pane = els.reqPane; if (!pane || !data) return;
    pane.innerHTML = '';
    var any = false;
    if (data.party_invites.length) {
      any = true;
      pane.appendChild(h('section', { class: 'panel' }, [h('h2', { class: 'panel-title', text: 'Party invites' }), h('ul', { class: 'people' }, data.party_invites.map(function (i) {
        var mins = Math.max(1, Math.round((new Date(i.expires_at) - api.now()) / 60000));
        return h('li', { class: 'person' }, [av(i.from, 'md'), h('div', { class: 'person-main' }, [h('b', { class: 'person-name', text: i.from.username + ' invited you to their party' }), h('span', { class: 'fine', text: i.members + '/4 players · expires in ' + mins + ' min' })]),
          h('div', { class: 'person-actions' }, [smallBtn('Join', 'gold', function () { respondInvite(i, true); }), smallBtn('Decline', 'ghost', function () { respondInvite(i, false); })])]);
      }))]));
    }
    if (data.incoming.length) {
      any = true;
      pane.appendChild(h('section', { class: 'panel' }, [h('h2', { class: 'panel-title', text: 'Incoming requests' }), h('ul', { class: 'people' }, data.incoming.map(function (r) {
        return h('li', { class: 'person' }, [av(r.user, 'md'), h('div', { class: 'person-main' }, [h('b', { class: 'person-name', text: r.user.username }), h('span', { class: 'fine', text: 'Wants to be friends' })]),
          h('div', { class: 'person-actions' }, [smallBtn('Accept', 'gold', function (b) { act(b, 'tk_friend_respond', { p_request: r.id, p_accept: true }, 'You and ' + r.user.username + ' are now friends!'); }), smallBtn('Decline', 'ghost', function (b) { act(b, 'tk_friend_respond', { p_request: r.id, p_accept: false }); })])]);
      }))]));
    }
    if (data.outgoing.length) {
      any = true;
      pane.appendChild(h('section', { class: 'panel' }, [h('h2', { class: 'panel-title', text: 'Sent requests' }), h('ul', { class: 'people' }, data.outgoing.map(function (r) {
        return h('li', { class: 'person' }, [av(r.user, 'md'), h('div', { class: 'person-main' }, [h('b', { class: 'person-name', text: r.user.username }), h('span', { class: 'fine', text: 'Request sent' })]),
          h('div', { class: 'person-actions' }, [smallBtn('Cancel', 'ghost', function (b) { act(b, 'tk_friend_cancel', { p_request: r.id }); })])]);
      }))]));
    }
    if (!any) pane.appendChild(h('section', { class: 'panel' }, [h('div', { class: 'empty' }, [h('b', { text: 'You’re all caught up' }), h('span', { text: 'Friend requests and party invites will appear here.' })])]));
  }

  // ---------------- tabs ----------------
  function select(name) {
    sub = ['party', 'friends', 'requests'].indexOf(name) >= 0 ? name : 'party';
    U.$$('.view-social .subtab').forEach(function (b) { var on = b.getAttribute('data-soc') === sub; b.setAttribute('aria-selected', on ? 'true' : 'false'); b.tabIndex = on ? 0 : -1; });
    els.partyPane.hidden = sub !== 'party'; els.friendsPane.hidden = sub !== 'friends'; els.reqPane.hidden = sub !== 'requests';
    if (location.hash !== '#' + sub) { try { history.replaceState(null, '', '#' + sub); } catch (e) { /* ignore */ } }
    if (sub === 'party' && TK.party) TK.party.renderPage();
    if (sub !== 'party' && data) render();
    refresh();
  }

  TK.social = {
    init: function (acc) {
      me = acc;
      els = { badge: U.$('#social-badge'), reqBadge: U.$('#soc-req-badge'), socialBtn: U.$('#btn-social'), friendsN: U.$('#soc-friends-n'),
        partyPane: U.$('#soc-party'), friendsPane: U.$('#soc-friends'), reqPane: U.$('#soc-requests') };
      var tabs = U.$$('.view-social .subtab');
      tabs.forEach(function (b, i) {
        b.addEventListener('click', function () { select(b.getAttribute('data-soc')); });
        b.addEventListener('keydown', function (e) { var d = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0; if (!d) return; e.preventDefault(); var n = tabs[(i + d + tabs.length) % tabs.length]; n.focus(); select(n.getAttribute('data-soc')); });
      });
      refresh().then(heartbeat);
      scheduleBeat();
      watcher = api.watch('tk_account_signals', 'account_id', acc.id, U.debounce(refresh, 150));
      document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'visible') { heartbeat(); scheduleBeat(); } });
      window.addEventListener('online', function () { heartbeat(); refresh(); });
      U.bus.on('party', function () { if (data) renderFriends(); });
      if (TK.tracker) TK.tracker.on(function (what) { if (what === 'session-start' || what === 'pause' || what === 'resume' || what === 'session-end') heartbeat(); });
    },
    refresh: refresh, select: select,
    current: function () { return sub; },
    state: function () { return data; },
    respondInvite: respondInvite,
    presenceTag: presenceTag
  };
})(window.TK = window.TK || {});
