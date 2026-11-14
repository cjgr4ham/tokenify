/* TOKENIFY — Automatic PC tracking.
 * TOKENIFY Chest Tracker (a small Windows app, formerly "TOKENIFY Companion") reads the replay Fortnite saves after each match and sends one summary
 * per match to your account. This module links PCs with one-time codes, shows their status, lets you choose how
 * your chests are counted (by hand, automatically, or both), applies imported matches to the Live Counter, and
 * keeps the import history with corrections. Imported counts arrive after each match, never during it. */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h, api = TK.api;
  var DOWNLOAD = 'https://github.com/cjgr4ham/tokenify/releases/latest/download/TOKENIFY-Tracker.exe';
  var RELEASES = 'https://github.com/cjgr4ham/tokenify/releases/latest';
  var KIND = { regular: 'regular', rare: 'rare', buried: 'buried', poster: 'sprite poster', relic: 'sprite vault', unrecorded: 'type not recorded', other: 'other' };

  var acc = null, st = null;           // tk_pc_tracking_state
  var server = {};                     // match id -> newest copy from the server
  var since = null;                    // newest updated_at fetched so far
  var busy = null, pollTimer = null, pairing = null, open = null, hidePrompt = false;

  function mode() { return (st && st.tracking) || 'manual'; }
  function onWindows() { return /Windows/i.test(navigator.userAgent || '') && !/Windows Phone/i.test(navigator.userAgent || ''); }
  function ago(iso) {
    if (!iso) return 'never';
    var s = Math.max(0, (api.now() - Date.parse(iso)) / 1000);
    if (s < 60) return 'just now';
    if (s < 3600) return Math.round(s / 60) + ' min ago';
    if (s < 86400) return Math.round(s / 3600) + ' h ago';
    return U.fmtDate(Date.parse(iso));
  }
  function stateText(d) {
    var s = (d && d.status) || {};
    if (!d) return 'Not connected';
    if (s.folder === 'missing') return 'Replays off / folder not found';
    return { in_match: 'Match in progress', processing: 'Processing replay', syncing: 'Syncing', up_to_date: 'Up to date', needs_attention: 'Needs attention',
      paused: 'Paused on the PC', waiting: 'Waiting for match' }[s.state] || 'Connected';
  }
  function bestDevice() {
    var ds = (st && st.devices) || [];
    return ds.slice().sort(function (a, b) { return Date.parse(b.last_seen_at || 0) - Date.parse(a.last_seen_at || 0); })[0] || null;
  }
  function queued() { return ((st && st.devices) || []).reduce(function (a, d) { return a + ((d.status && d.status.queued) || 0); }, 0); }
  function emit() { U.bus.emit('pc', st); renderChip(); renderHome(); if (open) open.render(); }

  // ------------------------------------------------------------------ server state
  async function refresh() {
    if (!acc || TK.auth.offline()) return st;
    try {
      var r = await api.rpc('tk_pc_tracking_state', {});
      if (r && r.ok) { st = r; emit(); }
    } catch (e) { /* offline: keep what we have */ }
    return st;
  }

  // Fetches imported matches changed since the last fetch and applies them to the Live Counter.
  async function syncImports(full) {
    if (busy) return busy;
    busy = (async function () {
      try {
        var from = full ? null : since, more = true, changed = [];
        while (more) {
          var r = await api.rpc('tk_replay_matches', from ? { p_since: from } : {});
          if (!r || !r.ok) return;
          if (st) st.tracking = r.tracking;
          r.matches.forEach(function (m) { if (!server[m.id] || server[m.id].updated_at !== m.updated_at) changed.push(m); server[m.id] = m; if (!since || m.updated_at > since) since = m.updated_at; });
          more = r.more && r.matches.length > 0;
          from = since;
        }
        apply(full ? Object.keys(server).map(function (k) { return server[k]; }) : changed, !full);
      } catch (e) { /* retried on the next signal */ }
      finally { busy = null; }
    })();
    return busy;
  }

  // Applies server copies to the Live Counter (idempotent: the same match always maps to the same game).
  function apply(list, announce) {
    var m = TK.tracker; if (!m || !list.length) return;
    if (m.readOnly) return;                                   // another tab owns the tracker; it applies them
    var md = mode(), created = [], pending = [], reconciled = [];
    list.slice().sort(function (a, b) { return Date.parse(a.started_at) - Date.parse(b.started_at); }).forEach(function (x) {
      var g = m.replayGame(x.id);
      if (md === 'manual' && !g) return;                      // manual counting: imports are kept on the server, not added
      var r = m.applyReplay(x, md === 'manual' ? 'auto' : md);
      if (r.action === 'created' && !x.historical) created.push(x);
      if (r.action === 'pending') pending.push(r.game);
      if (r.action === 'reconciled') reconciled.push(r.game);
    });
    if (!announce) return;
    created.forEach(function (x) { ui.toast(U.fmtInt(x.chests) + ' chests from your PC’s last match were added' + (x.party && x.party.number ? ' (squad game ' + x.party.number + ')' : '') + '.', { action: { label: 'View', onClick: function () { location.hash = 'tracker'; } } }); });
    reconciled.forEach(function (g) { ui.toast('Replay check for game ' + m.gameNo(g.id) + ': ' + g.replay.manual.chests + ' counted by hand, ' + g.replay.chests + ' in the replay. The replay count is used; your count is kept for reference.', { duration: 9000 }); });
    pending.forEach(function (g) { ui.toast('Replay ready for game ' + m.gameNo(g.id) + ': you counted ' + g.replay.manual.chests + ', the replay says ' + g.replay.chests + '.', { duration: 10000, action: { label: 'Review', onClick: function () { location.hash = 'tracker'; } } }); });
  }

  // After two browsers merge their histories, re-apply what the server says (a removed match stays removed).
  function reconcileAll() {
    var m = TK.tracker; if (!m || m.readOnly) return;
    var list = Object.keys(server).map(function (k) { return server[k]; }).filter(function (x) {
      var g = m.replayGame(x.id); return x.excluded ? !!g : !!g && g.replay.v !== x.updated_at;
    });
    if (list.length) apply(list, false);
  }

  // ------------------------------------------------------------------ Live Counter chip + Home line
  function renderChip() {
    var el = document.getElementById('trk-pc'); if (!el) return;
    var d = bestDevice();
    if (!d) { el.hidden = true; return; }
    el.hidden = false;
    var s = (d.status && d.status.state) || '';
    el.className = 'trk-pc ' + (d.online ? 'on' : 'off') + (s === 'needs_attention' || (d.status && d.status.folder === 'missing') ? ' warn' : '') + (s === 'processing' || s === 'syncing' ? ' busy' : '');
    var last = st.last_match;
    el.innerHTML = '';
    el.append(h('span', { class: 'pc-dot', 'aria-hidden': 'true' }), h('span', { class: 'pc-l', text: d.online ? 'PC: ' + stateText(d) : 'PC offline' }),
      last ? h('span', { class: 'pc-m num', text: ' · last match ' + U.fmtInt(last.chests) }) : null);
    el.title = 'Automatic PC tracking: ' + (d.online ? stateText(d) : 'the tracker on your PC hasn’t checked in for a while') + (last ? '. Last match: ' + last.chests + ' chests, ' + ago(last.ended_at) + '.' : '') + ' Click for details.';
  }
  function renderHome() {
    var el = document.getElementById('home-pc'); if (!el) return;
    el.innerHTML = '';
    var d = bestDevice();
    if (d) {
      el.hidden = false; el.className = 'home-pc connected';
      var last = st.last_match;
      el.append(h('span', { class: 'pc-dot' + (d.online ? ' on' : ''), 'aria-hidden': 'true' }), h('b', { text: 'REPLAY TRACKING CONNECTED' }),
        h('span', { class: 'fine', text: last ? ' · last match ' + U.fmtInt(last.chests) + ' chests, ' + ago(last.ended_at) : ' · waiting for your next match' }));
      var b = h('button', { type: 'button', class: 'link-btn' }, ['Manage']); b.addEventListener('click', function () { openPanel(); }); el.appendChild(b);
      return;
    }
    var dismissed = false; try { dismissed = U.storage.get('pc.promptHidden', false); } catch (e) { /* ignore */ }
    if (hidePrompt || dismissed || !onWindows() || !st) { el.hidden = true; return; }
    el.hidden = false; el.className = 'home-pc prompt';
    el.append(h('b', { text: 'Playing on this PC? ' }), h('span', { text: 'Let TOKENIFY count your chests automatically after each match.' }));
    var go = h('button', { type: 'button', class: 'btn-gold btn-sm' }, ['Set up automatic tracking']); go.addEventListener('click', function () { openPanel(); });
    var no = h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Hide this' }); no.innerHTML = '<svg class="i"><use href="#i-close"/></svg>';
    no.addEventListener('click', function () { hidePrompt = true; try { U.storage.set('pc.promptHidden', true); } catch (e) { /* ignore */ } renderHome(); });
    el.append(go, no);
  }

  // ------------------------------------------------------------------ the panel
  function btn(label, kind, fn, cls) {
    var b = h('button', { type: 'button', class: (kind === 'gold' ? 'btn-gold' : 'btn-ghost' + (kind === 'danger' ? ' danger' : '')) + (cls ? ' ' + cls : '') }, [label]);
    b.addEventListener('click', function () { fn(b); });
    return b;
  }
  async function act(b, fn, args, okMsg) {
    if (b) b.disabled = true;
    try {
      var r = await api.rpc(fn, args || {});
      if (!r.ok) { ui.toast(api.message(r.error)); return null; }
      if (okMsg) ui.toast(okMsg);
      return r;
    } catch (e) { ui.toast(api.message(e)); return null; }
    finally { if (b) b.disabled = false; }
  }

  function openPanel(tab) {
    if (open) { open.select(tab || 'pc'); return; }
    var tabs = [['pc', 'Your PC'], ['history', 'Import history'], ['how', 'How it works']], current = tab || 'pc';
    var bar = h('div', { class: 'pc-tabs', role: 'tablist' }), pane = h('div', { class: 'pc-pane' });
    tabs.forEach(function (t, i) {
      var b = h('button', { type: 'button', role: 'tab', class: 'subtab', 'data-t': t[0], 'data-autofocus': i === 0 ? '' : null }, [t[1]]);
      b.addEventListener('click', function () { current = t[0]; render(); });
      bar.appendChild(b);
    });
    var body = h('div', { class: 'pc-panel' }, [bar, pane]);
    function render() {
      U.$$('.subtab', bar).forEach(function (b) { b.setAttribute('aria-selected', String(b.getAttribute('data-t') === current)); });
      pane.innerHTML = '';
      pane.appendChild(current === 'history' ? historyPane() : current === 'how' ? howPane() : pcPane());
    }
    var m = ui.modal({ title: 'Automatic PC tracking', sub: 'Count your chests from Fortnite replays on a Windows PC: no buttons to press while you play.', body: body, wide: true, className: 'pc-modal',
      onClose: function () { open = null; stopPairing(); } });
    open = { render: render, select: function (t) { current = t; render(); }, close: m.close };
    render();
    refresh();
    syncImports(false);
  }

  // ---- Your PC
  function pcPane() {
    var p = h('div', {});
    var devices = (st && st.devices) || [];
    // status
    if (devices.length) {
      devices.forEach(function (d) {
        var s = d.status || {};
        var card = h('div', { class: 'pc-device' + (d.online ? ' on' : '') }, [
          h('div', { class: 'pc-dev-top' }, [h('span', { class: 'pc-dot' + (d.online ? ' on' : ''), 'aria-hidden': 'true' }), h('b', { text: d.name }), h('span', { class: 'tag sm', text: d.online ? stateText(d) : 'Offline' })]),
          h('dl', { class: 'kv kv-3' }, [
            kv('Last sync', d.last_sync_at ? ago(d.last_sync_at) : 'not yet'),
            kv('Last match', s.last_match_chests != null ? U.fmtInt(s.last_match_chests) + ' chests' : (st.last_match ? U.fmtInt(st.last_match.chests) + ' chests' : '—')),
            kv('Queued matches', U.fmtInt(s.queued || 0)),
            kv('Replays folder', s.folder === 'missing' ? 'Not found — turn on Record Replays' : s.folder === 'empty' ? 'Found, no replays yet' : s.folder === 'unreadable' ? 'Can’t be read' : s.folder === 'ok' ? 'Found' : '—'),
            kv('Tracker', d.app_version ? 'v' + d.app_version : '—'),
            kv('Linked', U.fmtDate(Date.parse(d.created_at)))
          ]),
          h('div', { class: 'row-actions' }, [
            btn('Rescan', 'ghost', function (b) { act(b, 'tk_device_rescan', { p_device: d.id }, 'Asked ' + d.name + ' to scan its replays.'); }, 'btn-sm'),
            btn('Rename', 'ghost', function () { renameDevice(d); }, 'btn-sm'),
            btn('Disconnect device', 'danger', function (b) {
              ui.confirm({ title: 'Disconnect ' + d.name + '?', message: 'It stops sending matches at once. Matches it already sent stay in your history. You can link it (or another PC) again any time.', confirmLabel: 'Disconnect', danger: true })
                .then(function (ok) { if (ok) act(b, 'tk_device_revoke', { p_device: d.id }, d.name + ' disconnected.').then(refresh); });
            }, 'btn-sm')
          ])
        ]);
        p.appendChild(card);
      });
    } else {
      p.appendChild(h('div', { class: 'pc-empty' }, [h('b', { text: 'NOT CONNECTED' }), h('span', { text: 'Connect your PC to count chests automatically after matches.' })]));
    }
    // setup
    p.appendChild(h('h3', { class: 'pc-h', text: devices.length ? 'Add or replace a PC' : 'Set it up (about 2 minutes)' }));
    var steps = h('ol', { class: 'pc-steps' }, [
      h('li', {}, [h('b', { text: 'Turn on replays in Fortnite: ' }), 'Settings → Game → Replays → Record Replays → On.']),
      h('li', {}, [h('b', { text: 'Download TOKENIFY Chest Tracker ' }), 'and run it (one file, no install, no admin rights). From then on it counts by itself and shows your total and chests per game in a small overlay while you play.']),
      h('li', {}, [h('b', { text: 'Connect it to this account: ' }), 'press LINK DEVICE below and type the code into the tracker.']),
      h('li', {}, [h('b', { text: 'Play Fortnite normally.' })]),
      h('li', {}, [h('b', { text: 'Your chest totals update after each match.' })])
    ]);
    p.appendChild(steps);
    var dl = h('a', { class: 'btn-gold', href: DOWNLOAD, rel: 'noopener' }, ['Download the tracker']);
    p.appendChild(h('div', { class: 'row-actions' }, [dl, btn('Link device', devices.length ? 'ghost' : 'gold', function (b) { startPairing(b); })]));
    p.appendChild(h('p', { class: 'fine', html: 'Windows may warn that the app is from an unknown publisher: it isn’t code-signed yet. Choose <b>More info → Run anyway</b> only if you downloaded it from this button. Source code, licence and checksums: <a href="' + RELEASES + '" target="_blank" rel="noopener">release page</a>.' }));
    p.appendChild(h('p', { class: 'fine', text: 'Already using TOKENIFY Companion? The Chest Tracker replaces it. Download it and run it once: it closes the old app, keeps this PC’s link and your match history, and adds the overlay.' }));
    var codeBox = h('div', { class: 'pc-code-box', id: 'pc-code-box' });
    p.appendChild(codeBox);
    if (pairing) showCode(codeBox);
    // counting mode
    p.appendChild(h('h3', { class: 'pc-h', text: 'How your chests are counted' }));
    var modes = [['manual', 'By hand', 'You press + (or Space) for every chest. Imported matches are kept but not added.'],
      ['auto', 'Automatically', 'Your PC’s replays are the count. Pressing + during a match is checked against the replay, never added twice.'],
      ['hybrid', 'Both', 'Count by hand as usual; after each match the replay checks your count and you choose which to keep.']];
    var seg = h('div', { class: 'pc-modes', role: 'radiogroup', 'aria-label': 'How your chests are counted' });
    modes.forEach(function (o) {
      var b = h('button', { type: 'button', role: 'radio', class: 'pc-mode', 'aria-checked': String(mode() === o[0]) }, [h('b', { text: o[1] }), h('span', { text: o[2] })]);
      b.addEventListener('click', function () {
        act(b, 'tk_tracking_settings', { p_mode: o[0] }).then(function (r) { if (r) { st.tracking = r.tracking; emit(); if (r.tracking !== 'manual') syncImports(true); } });
      });
      seg.appendChild(b);
    });
    p.appendChild(seg);
    var gap = h('select', { 'aria-label': 'Group matches into one session when they are this close' });
    [10, 20, 30, 45, 60, 90].forEach(function (n) { var o = h('option', { value: String(n), text: n + ' minutes' }); if (st && st.auto_gap === n) o.selected = true; gap.appendChild(o); });
    gap.addEventListener('change', function () { act(null, 'tk_tracking_settings', { p_auto_gap: Number(gap.value) }, 'Saved.').then(function (r) { if (r) st.auto_gap = r.auto_gap; }); });
    p.appendChild(h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: 'Session grouping' }),
      h('span', { class: 'fine', text: 'Matches your PC records while no session is running are grouped into an automatic session when they are this close. Its time is the matches’ own length, never the gaps.' })]), gap]));
    // Fortnite accounts
    p.appendChild(h('h3', { class: 'pc-h', text: 'Fortnite accounts' }));
    var profs = (st && st.profiles) || [];
    if (!profs.length) p.appendChild(h('p', { class: 'muted', text: 'None yet. The tracker asks which Fortnite account is yours the first time it reads a replay.' }));
    profs.forEach(function (f) {
      p.appendChild(h('div', { class: 'pc-prof' }, [h('b', { text: f.label || 'Fortnite account' }), h('span', { class: 'fine', text: ' · linked ' + U.fmtDate(Date.parse(f.created_at)) + (f.last_match_at ? ' · last match ' + ago(f.last_match_at) : '') }),
        btn('Unlink', 'ghost', function (b) {
          ui.confirm({ title: 'Unlink ' + (f.label || 'this Fortnite account') + '?', message: 'Its future matches won’t be counted for you, and another TOKENIFY account could link it. Matches already imported stay (use “Not my match” in Import history to remove them).', confirmLabel: 'Unlink', danger: true })
            .then(function (ok) { if (ok) act(b, 'tk_replay_profile_unlink', { p_player_hash: f.player_hash }, 'Unlinked.').then(refresh); });
        }, 'btn-sm')]));
    });
    p.appendChild(h('p', { class: 'fine', text: 'TOKENIFY stores only a one-way hash of your Fortnite account id (and its display name, for this list), so one Fortnite account can only ever count for one TOKENIFY account.' }));
    return p;
  }
  function kv(l, v) { return h('div', {}, [h('dt', { text: l }), h('dd', { text: v })]); }

  function renameDevice(d) {
    var input = h('input', { type: 'text', maxlength: '40', value: d.name, 'aria-label': 'PC name' });
    ui.modal({ title: 'Rename PC', body: h('div', { class: 'field' }, [input]), actions: [{ label: 'Cancel', kind: 'ghost' }, { label: 'Save', kind: 'gold', onClick: function (close, b) {
      act(b, 'tk_device_rename', { p_device: d.id, p_name: input.value.trim() }, 'Renamed.').then(function (r) { if (r) { refresh(); close(); } });
      return false;
    } }] });
  }

  // ---- pairing: a one-time 8-character code, 10 minutes, typed into the companion
  async function startPairing(b) {
    var r = await act(b, 'tk_device_pair_start', {});
    if (!r) return;
    var before = ((st && st.devices) || []).map(function (d) { return d.id; });
    pairing = { code: r.code, expires: Date.parse(r.expires_at), before: before };
    var box = document.getElementById('pc-code-box'); if (box) showCode(box);
    clearInterval(pollTimer);
    pollTimer = setInterval(async function () {
      if (!pairing || api.now() > pairing.expires) { stopPairing(); if (open) open.render(); return; }
      var s = await refresh();
      var fresh = s && s.devices.filter(function (d) { return pairing.before.indexOf(d.id) < 0; })[0];
      if (fresh) { stopPairing(); ui.toast('Connected! ' + fresh.name + ' will now count your chests after each match.'); TK.audio && TK.audio.play('coin'); if (open) open.render(); }
      else { var bx = document.getElementById('pc-code-box'); if (bx) showCode(bx); }
    }, 3000);
  }
  function stopPairing() { pairing = null; clearInterval(pollTimer); pollTimer = null; }
  function showCode(box) {
    box.innerHTML = '';
    if (!pairing) return;
    var left = Math.max(0, pairing.expires - api.now());
    box.appendChild(h('div', { class: 'pc-code' }, [h('span', { class: 'fine', text: 'Type this code into TOKENIFY Chest Tracker' }), h('span', { class: 'code-v mono', text: pairing.code }),
      h('span', { class: 'fine', text: 'Works once · expires in ' + Math.ceil(left / 60000) + ' min · waiting for your PC…' })]));
  }

  // ---- Import history
  function historyPane() {
    var p = h('div', {});
    var list = Object.keys(server).map(function (k) { return server[k]; }).sort(function (a, b) { return Date.parse(b.started_at) - Date.parse(a.started_at); });
    p.appendChild(h('p', { class: 'fine', text: 'Every match your PC sent, newest first. A correction is kept next to the replay’s own count, and a newer companion never overwrites it.' + (mode() === 'manual' ? ' You count by hand, so these are not added to your Live Counter.' : '') }));
    if (!list.length) { p.appendChild(h('p', { class: 'muted', text: 'No imported matches yet.' })); return p; }
    var ul = h('ul', { class: 'pc-hist' });
    list.slice(0, 200).forEach(function (x) { ul.appendChild(historyRow(x)); });
    p.appendChild(ul);
    return p;
  }
  function historyRow(x) {
    var b = x.breakdown || {}, parts = [];
    Object.keys(KIND).forEach(function (k) { if (b[k]) parts.push(b[k] + ' ' + KIND[k]); });
    var notes = [];
    if (b.destroyed) notes.push(b.destroyed + ' broken open');
    if (b.reopened) notes.push(b.reopened + ' refilled');
    if (b.unverified) notes.push(b.unverified + ' new chest type');
    var tags = [];
    if (x.excluded) tags.push(h('span', { class: 'tag sm', text: 'Not counted (not your match)' }));
    if (x.corrected_chests != null) tags.push(h('span', { class: 'tag sm comp-reported', text: 'Corrected from ' + x.replay_chests }));
    if (x.historical) tags.push(h('span', { class: 'tag sm', text: 'History' }));
    if (x.partial) tags.push(h('span', { class: 'tag sm comp-partial', text: 'Partly read' }));
    if (x.party && x.party.link) tags.push(h('span', { class: 'tag sm' + (x.party.link === 'ambiguous' || x.party.link === 'conflict' ? ' comp-partial' : ''), text: partyText(x.party) }));
    var li = h('li', { class: 'pc-h-row' + (x.excluded ? ' excluded' : '') }, [
      h('div', { class: 'pc-h-top' }, [h('b', { class: 'num', text: U.fmtInt(x.chests) + (x.chests === 1 ? ' chest' : ' chests') }), h('span', { class: 'fine', text: U.fmtDate(Date.parse(x.started_at)) + ' · ' + U.fmtSpan(Date.parse(x.ended_at) - Date.parse(x.started_at)) })].concat(tags)),
      h('div', { class: 'fine', text: (parts.join(', ') || 'no chests') + (notes.length ? ' (' + notes.join(', ') + ')' : '') + ' · ' + (x.device || 'PC') + ' · parser ' + x.parser_version + (x.has_match_id ? '' : ' · no Fortnite match id') })
    ]);
    var acts = h('div', { class: 'row-actions' });
    if (!x.excluded) acts.appendChild(btn('Correct count', 'ghost', function () { correct(x); }, 'btn-sm'));
    acts.appendChild(btn(x.excluded ? 'Count it again' : 'Not my match', 'ghost', function (bb) {
      var go = function () { act(bb, 'tk_replay_exclude', { p_match: x.id, p_excluded: !x.excluded }, x.excluded ? 'Counted again.' : 'Removed from your counts.').then(function (r) { if (r) syncImports(false).then(function () { if (open) open.render(); }); }); };
      if (x.excluded) go(); else ui.confirm({ title: 'Not your match?', message: 'It’s removed from your totals, CPG and squad games (you can count it again later).', confirmLabel: 'Remove' }).then(function (ok) { if (ok) go(); });
    }, 'btn-sm'));
    if (!x.excluded && !x.historical) acts.appendChild(btn(x.party && x.party.link && x.party.link !== 'detached' ? 'Squad game…' : 'Link to squad game…', 'ghost', function () { linkParty(x); }, 'btn-sm'));
    li.appendChild(acts);
    return li;
  }
  function partyText(pt) {
    return { 'match-id': 'Squad game ' + pt.number + ' (same Fortnite match)', 'time-window': 'Squad game ' + pt.number, confirmed: 'Squad game ' + pt.number + ' (confirmed)',
      ambiguous: 'Which squad game? Choose', conflict: 'Overlaps squad game ' + (pt.number || '') + ': choose', detached: 'Not a squad game' }[pt.link] || '';
  }
  function correct(x) {
    var n = h('input', { type: 'number', min: '0', max: '400', step: '1', value: String(x.chests), 'aria-label': 'Corrected chest count' });
    var why = h('input', { type: 'text', maxlength: '120', placeholder: 'Optional: why (e.g. a chest the replay missed)', 'aria-label': 'Reason' });
    ui.modal({ title: 'Correct this match', className: 'pc-correct', sub: 'The replay counted ' + x.replay_chests + '. Your correction updates your totals, CPG and squad game, and is never overwritten by a newer companion.', body: h('div', {}, [h('div', { class: 'field' }, [h('label', { text: 'Chests you opened' }), n]), h('div', { class: 'field' }, [h('label', { text: 'Why (optional)' }), why])]),
      actions: [x.corrected_chests != null ? { label: 'Use the replay’s count', kind: 'ghost', onClick: function (close, b) { act(b, 'tk_replay_correct', { p_match: x.id, p_chests: null }, 'Back to the replay’s count.').then(function (r) { if (r) { syncImports(false); close(); } }); return false; } } : null,
        { label: 'Cancel', kind: 'ghost' },
        { label: 'Save', kind: 'gold', onClick: function (close, b) {
          var v = Math.round(Number(n.value));
          if (!isFinite(v) || v < 0 || v > 400) { ui.toast('Enter a number from 0 to 400.'); return false; }
          act(b, 'tk_replay_correct', { p_match: x.id, p_chests: v, p_reason: why.value }, 'Corrected to ' + v + '.').then(function (r) { if (r) { syncImports(false).then(function () { if (open) open.render(); }); close(); } });
          return false;
        } }].filter(Boolean) });
  }
  async function linkParty(x) {
    var r = await act(null, 'tk_replay_party_candidates', { p_match: x.id });
    if (!r) return;
    var body = h('div', {});
    if (!r.games.length) body.appendChild(h('p', { class: 'muted', text: 'You weren’t in a squad game around this match.' }));
    r.games.forEach(function (g) {
      var label = 'Squad game ' + g.number + ' · ' + new Date(g.started_at).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }) + (g.ended_at ? '–' + new Date(g.ended_at).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }) : ' (running)');
      body.appendChild(h('div', { class: 'pc-cand' }, [h('span', { text: label }), g.linked_here ? h('span', { class: 'tag sm', text: 'Linked' }) : btn(g.other_replay ? 'Use this match instead' : 'This one', 'gold', function (b) {
        act(b, 'tk_replay_attach', { p_match: x.id, p_game: g.id, p_replace: g.other_replay }, 'Linked to squad game ' + g.number + '.').then(function (rr) { if (rr) { m.close(); syncImports(false).then(function () { if (open) open.render(); }); } });
      }, 'btn-sm')]));
    });
    var m = ui.modal({ title: 'Which squad game was this?', sub: 'Your count is added to that game for the whole squad. One match per player per squad game.', body: body,
      actions: [x.party && x.party.link && x.party.link !== 'detached' ? { label: 'Not a squad game', kind: 'ghost', onClick: function (close, b) { act(b, 'tk_replay_detach', { p_match: x.id }, 'Unlinked.').then(function (rr) { if (rr) { syncImports(false).then(function () { if (open) open.render(); }); close(); } }); return false; } } : null,
        { label: 'Close', kind: 'ghost' }].filter(Boolean) });
  }

  function howPane() {
    return h('div', { class: 'pc-how' }, [
      h('p', { text: 'TOKENIFY Chest Tracker is a small Windows app. Fortnite saves a replay of every match on your PC; when a match ends, the tracker reads that file and finds the chests you opened: searched, or broken open, each counted once. It shows your total and chests per game in a small overlay and, once linked, sends TOKENIFY one short summary per match.' }),
      h('h3', { class: 'pc-h', text: 'What it sends' }),
      h('p', { text: 'Chests opened and when (seconds into the match), chest types, match start and end, Fortnite’s match id (so squad-mates’ results line up), the game and parser versions, and a one-way hash of your Fortnite account id. Never the replay file, other players’ data, your passwords, or anything else from the PC.' }),
      h('h3', { class: 'pc-h', text: 'Good to know' }),
      h('ul', { class: 'pc-notes' }, [
        h('li', { text: 'Only matches from this season count' + (st && st.season_start ? ' (since ' + U.fmtDate(Date.parse(st.season_start)) + ')' : '') + ': the Pirate Token didn’t exist before, so older replays are never read or sent.' }),
        h('li', { text: 'Counts arrive after each match, never during it: Fortnite only makes a replay readable once the match is over.' }),
        h('li', { text: 'A Fortnite update can change the replay format. If matches stop arriving, update the tracker; it keeps unread replays and reads them after the update.' }),
        h('li', { text: 'Your replay only covers your own chests. Each PC player in a squad adds their own; Switch and console players keep counting here.' }),
        h('li', { text: 'Counts come from your PC’s replay files: they are replay-derived, not verified by Epic.' }),
        h('li', { text: 'A chest the replay shows disappearing without saying who broke it (this can happen when several are smashed at once) is listed in the tracker as "ambiguous". It is not counted unless you add it yourself.' }),
        h('li', { text: 'The tracker only reads replay files. It never touches the Fortnite program, its memory, its files or your input. Its overlay is an ordinary always-on-top window; Fortnite has to be in Windowed Fullscreen for it to show.' }),
        h('li', { text: 'TOKENIFY Chest Tracker is an independent fan tool, not made, reviewed or endorsed by Epic Games. Epic does not say which third-party tools are allowed, so nobody can promise that any tool is risk-free. Open source (GPL-3.0).' })
      ])
    ]);
  }

  // ------------------------------------------------------------------ Settings modal section
  function settingsSection() {
    var box = h('div', { class: 'pc-settings' });
    function draw() {
      box.innerHTML = '';
      var d = bestDevice();
      if (d) {
        box.appendChild(h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: 'CONNECTED' + (st.devices.length > 1 ? ' · ' + st.devices.length + ' PCs' : '') }),
          h('span', { class: 'fine', text: 'Last sync: ' + ago(d.last_sync_at) + ' · Last match: ' + (st.last_match ? U.fmtInt(st.last_match.chests) + ' chests' : '—') + ' · Queued matches: ' + U.fmtInt(queued()) })])]));
        box.appendChild(h('div', { style: { display: 'flex', flexWrap: 'wrap', gap: '8px' } }, [
          btn('View import history', 'ghost', function () { openPanel('history'); }),
          btn('Rescan', 'ghost', function (b) { act(b, 'tk_device_rescan', { p_device: d.id }, 'Asked your PC to scan its replays.'); }),
          btn('Disconnect device', 'danger', function () { openPanel('pc'); })
        ]));
      } else {
        box.appendChild(h('p', { class: 'fine', text: 'Connect your PC to count chests automatically after matches.' }));
        box.appendChild(h('div', { style: { display: 'flex', flexWrap: 'wrap', gap: '8px' } }, [h('a', { class: 'btn-gold', href: DOWNLOAD, rel: 'noopener' }, ['Download the tracker']), btn('Link device', 'ghost', function () { openPanel('pc'); })]));
      }
    }
    draw();
    refresh().then(draw);
    return box;
  }

  // ------------------------------------------------------------------ init
  TK.pc = {
    init: function (account) {
      acc = account;
      var chip = document.getElementById('trk-pc');
      if (chip) chip.addEventListener('click', function () { openPanel('pc'); });
      refresh().then(function () { if (mode() !== 'manual' || (st && st.matches)) syncImports(true); });
      // the account signal (pairing, uploads, status changes) arrives through the social refresh
      U.bus.on('social', U.debounce(function () { refresh(); syncImports(false); }, 400));
      if (TK.tracker) TK.tracker.on(function (what) { if (what === 'sync-merge') reconcileAll(); });
      window.addEventListener('online', function () { refresh(); syncImports(false); });
      setInterval(function () { if (document.visibilityState === 'visible') { refresh(); syncImports(false); } }, 120000);
    },
    open: openPanel,
    settingsSection: settingsSection,
    state: function () { return st; },
    refresh: refresh,
    syncImports: syncImports
  };
})(window.TK = window.TK || {});
