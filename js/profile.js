/* TOKENIFY — your account: the header avatar menu, profile picture, privacy, password, recovery codes,
 * signed-in devices and account deletion. Every change goes through a tk_* function that re-checks the
 * session (and, for sensitive actions, your current password) on the server. */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h, api = TK.api;
  var AV = {}; TK.config.AVATARS.forEach(function (a) { AV[a.id] = a; });
  var menu, btn, open = false;

  // ---------------- avatars ----------------
  function avatar(src, size) {
    var id = typeof src === 'string' ? src : (src && src.avatar) || 'compass';
    var a = AV[id] || AV.compass;
    var el = h('span', { class: 'avatar' + (size ? ' av-' + size : ''), style: { '--ring': a.ring } });
    var img = h('img', { src: a.art, alt: '', width: '64', height: '64', decoding: 'async', draggable: 'false' });
    img.addEventListener('error', function () { if (img.src.indexOf('compass.svg') < 0) img.src = 'assets/brand/compass.svg'; });
    el.appendChild(img);
    return el;
  }
  function paintHeader(acc) {
    var slot = U.$('#me-avatar'); if (!slot || !acc) return;
    var a = AV[acc.avatar] || AV.compass;
    slot.style.setProperty('--ring', a.ring);
    slot.innerHTML = '';
    var img = h('img', { src: a.art, alt: '', width: '36', height: '36', draggable: 'false' });
    img.addEventListener('error', function () { if (img.src.indexOf('compass.svg') < 0) img.src = 'assets/brand/compass.svg'; });
    slot.appendChild(img);
    btn.title = acc.username + ' — account';
  }

  // ---------------- header menu ----------------
  function item(icon, label, onClick, cls) {
    var b = h('button', { type: 'button', role: 'menuitem', class: 'menu-item' + (cls ? ' ' + cls : ''), tabindex: '-1' });
    b.innerHTML = '<svg class="i"><use href="#i-' + icon + '"/></svg>';
    b.appendChild(h('span', { text: label }));
    b.addEventListener('click', function () { closeMenu(); onClick(); });
    return b;
  }
  function buildMenu() {
    var acc = TK.auth.account(); if (!acc) return;
    menu.innerHTML = '';
    menu.appendChild(h('div', { class: 'menu-head' }, [avatar(acc, 'md'), h('div', {}, [h('b', { text: acc.username }), h('span', { class: 'menu-sync', text: TK.sync ? TK.sync.statusText() : '' })])]));
    menu.appendChild(item('user', 'Account & security', function () { openAccount(); }));
    menu.appendChild(item('users', 'Friends & party', function () { location.hash = 'party'; }));
    menu.appendChild(item('gear', 'Settings', function () { TK.app.openSettings(); }));
    if (TK.sync && TK.sync.hasLegacy()) menu.appendChild(item('download', 'Import earlier data', function () { TK.sync.offerLegacy(true); }));
    menu.appendChild(h('div', { class: 'menu-sep', role: 'separator' }));
    menu.appendChild(item('logout', 'Log out', confirmLogout, 'danger'));
  }
  function items() { return U.$$('.menu-item', menu); }
  function openMenu() {
    buildMenu(); menu.hidden = false; open = true; btn.setAttribute('aria-expanded', 'true');
    var first = items()[0]; if (first) first.focus();
  }
  function closeMenu(refocus) { if (!open) return; menu.hidden = true; open = false; btn.setAttribute('aria-expanded', 'false'); if (refocus) btn.focus(); }

  function confirmLogout() {
    var cb = h('input', { type: 'checkbox', id: 'lo-wipe' });
    ui.modal({ title: 'Log out?', sub: 'Your tracker is saved to your account, so you can log back in on any device.',
      body: h('label', { class: 'check-row', for: 'lo-wipe' }, [cb, h('span', { text: 'Also remove my data from this browser (for shared computers)' })]),
      actions: [{ label: 'Cancel', kind: 'ghost' }, { label: 'Log out', kind: 'gold', onClick: function () {
        if (TK.sync && TK.sync.state() !== 'synced' && cb.checked) { ui.toast('Some changes haven’t synced yet, so this browser’s copy was kept.'); TK.auth.logout({ wipeLocal: false }); return; }
        TK.auth.logout({ wipeLocal: cb.checked });
      } }] });
  }

  // ---------------- account dialog ----------------
  function section(title, icon, nodes) {
    var head = h('h3', { class: 'acct-h' }); head.innerHTML = '<svg class="i"><use href="#i-' + icon + '"/></svg>'; head.appendChild(document.createTextNode(title));
    return h('section', { class: 'acct-sec' }, [head].concat(nodes));
  }
  function input(label, type, ac) {
    var i = h('input', { type: type, autocomplete: ac, maxlength: '64', 'aria-label': label });
    return { row: h('div', { class: 'form-row' }, [h('label', { text: label }), i]), input: i };
  }
  function toggle(label, sub, checked, onChange) {
    var i = h('input', { type: 'checkbox' }); i.checked = !!checked;
    i.addEventListener('change', function () { onChange(i.checked, i); });
    return h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: label }), h('span', { text: sub })]), h('label', { class: 'toggle' }, [i, h('span', { class: 'track' }), h('span', { class: 'sr-only', text: label })])]);
  }
  async function saveProfile(args, okMsg) {
    try {
      var r = await api.rpc('tk_profile_update', args);
      if (!r.ok) { ui.toast(api.message(r.error)); return false; }
      TK.auth.update(r.account); if (okMsg) ui.toast(okMsg); return true;
    } catch (e) { ui.toast(api.message(e)); return false; }
  }
  function ago(t) {
    var s = Math.max(0, (Date.now() - new Date(t).getTime()) / 1000);
    return s < 90 ? 'active now' : s < 3600 ? Math.round(s / 60) + ' min ago' : s < 86400 ? Math.round(s / 3600) + ' h ago' : Math.round(s / 86400) + ' days ago';
  }

  function openAccount() {
    var acc = TK.auth.account(); if (!acc) return;
    // profile picture
    var grid = h('div', { class: 'avatar-grid', role: 'radiogroup', 'aria-label': 'Profile picture' });
    TK.config.AVATARS.forEach(function (a) {
      var b = h('button', { type: 'button', role: 'radio', class: 'avatar-pick', 'aria-checked': String(acc.avatar === a.id), 'data-id': a.id, title: a.name }, [avatar(a.id, 'lg'), h('span', { class: 'sr-only', text: a.name })]);
      grid.appendChild(b);
    });
    ui.bindRadios(grid, 'data-id', function (id) { saveProfile({ p_avatar: id }); });
    var profile = section('Profile', 'user', [
      h('div', { class: 'acct-id' }, [h('b', { text: acc.username }), h('span', { class: 'fine', text: 'Member since ' + new Date(acc.created_at).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' }) })]),
      h('div', { class: 'field-label', text: 'Profile picture' }), grid
    ]);
    // privacy
    var privacy = section('Privacy', 'eye', [
      toggle('Show when I’m online', 'Friends see Online / Tracking / In party. Your party always sees whether you’re connected.', acc.show_online, function (v, el) { saveProfile({ p_show_online: v }).then(function (ok) { if (!ok) el.checked = !v; }); }),
      toggle('Let players find me by search', 'When off, only someone who types your exact username can find you.', acc.searchable, function (v, el) { saveProfile({ p_searchable: v }).then(function (ok) { if (!ok) el.checked = !v; }); }),
      h('p', { class: 'fine', text: 'Other players can see your username, profile picture and (in a shared squad game) your chests in that game. Never your password, recovery codes, settings or session history.' })
    ]);
    // password
    var cur = input('Current password', 'password', 'current-password'), np = input('New password', 'password', 'new-password'), nc = input('Confirm new password', 'password', 'new-password');
    var pwMsg = h('p', { class: 'msg-error', role: 'alert' });
    var pwBtn = h('button', { type: 'button', class: 'btn-ghost' }, ['Change password']);
    pwBtn.addEventListener('click', async function () {
      pwMsg.textContent = ''; pwMsg.className = 'msg-error';
      var pr = TK.auth.passwordProblem(np.input.value, acc.username);
      if (!cur.input.value) { pwMsg.textContent = 'Enter your current password.'; return; }
      if (pr) { pwMsg.textContent = api.message(pr); return; }
      if (np.input.value !== nc.input.value) { pwMsg.textContent = 'The new passwords don’t match.'; return; }
      pwBtn.disabled = true;
      try {
        var r = await api.rpc('tk_password_change', { p_current: cur.input.value, p_new: np.input.value });
        if (!r.ok) pwMsg.textContent = api.message(r.error);
        else { cur.input.value = np.input.value = nc.input.value = ''; pwMsg.className = 'msg-ok'; pwMsg.textContent = 'Password changed. Every other device was signed out.'; loadSessions(); }
      } catch (e) { pwMsg.textContent = api.message(e); }
      pwBtn.disabled = false;
    });
    // recovery codes
    var codesInfo = h('p', { class: 'muted', text: 'Checking…' });
    var codesBtn = h('button', { type: 'button', class: 'btn-ghost' }, ['Make new codes']);
    codesBtn.addEventListener('click', regenerateCodes);
    api.rpc('tk_me', {}).then(function (r) { if (r && r.ok) codesInfo.textContent = r.recovery_codes_left + ' of 8 recovery codes unused. Making new ones replaces all old codes.'; }).catch(function () { codesInfo.textContent = ''; });
    // devices
    var devices = h('div', { class: 'device-list' }, [h('p', { class: 'muted', text: 'Loading…' })]);
    var othersBtn = h('button', { type: 'button', class: 'btn-ghost' }, ['Sign out all other devices']);
    othersBtn.addEventListener('click', async function () {
      if (!(await ui.confirm({ title: 'Sign out other devices?', message: 'Every other browser signed in to ' + acc.username + ' will need to log in again.', confirmLabel: 'Sign them out' }))) return;
      try { var r = await api.rpc('tk_sessions_revoke_others', {}); if (r.ok) { ui.toast('Other devices signed out.'); loadSessions(); } } catch (e) { ui.toast(api.message(e)); }
    });
    async function loadSessions() {
      try {
        var r = await api.rpc('tk_sessions_list', {});
        devices.innerHTML = '';
        (r.sessions || []).forEach(function (s) {
          var row = h('div', { class: 'device' }, [h('div', {}, [h('b', { text: s.device || 'Unknown browser' }), s.current ? h('span', { class: 'tag tag-theory sm', text: 'This device' }) : null,
            h('span', { class: 'fine', text: 'Signed in ' + new Date(s.created_at).toLocaleDateString() + ' · ' + (s.current ? 'active now' : ago(s.last_seen_at)) })])]);
          if (!s.current) {
            var out = h('button', { type: 'button', class: 'btn-ghost danger btn-sm' }, ['Sign out']);
            out.addEventListener('click', async function () { try { var x = await api.rpc('tk_session_revoke', { p_session: s.id }); if (x.ok) { ui.toast('Signed out ' + (s.device || 'that device') + '.'); loadSessions(); } } catch (e) { ui.toast(api.message(e)); } });
            row.appendChild(out);
          }
          devices.appendChild(row);
        });
      } catch (e) { devices.innerHTML = ''; devices.appendChild(h('p', { class: 'muted', text: api.message(e) })); }
    }
    loadSessions();
    var security = section('Security', 'shield', [
      h('div', { class: 'field-label', text: 'Password' }), h('div', { class: 'form-grid' }, [cur.row, h('div'), np.row, nc.row]), pwMsg, h('div', {}, [pwBtn]),
      h('div', { class: 'field-label', text: 'Recovery codes' }), codesInfo, h('div', {}, [codesBtn]),
      h('div', { class: 'field-label', text: 'Signed-in devices' }), devices, h('div', {}, [othersBtn])
    ]);
    // data + danger zone
    var del = h('button', { type: 'button', class: 'btn-ghost danger' }, ['Delete account…']);
    del.addEventListener('click', deleteAccount);
    var dataNodes = [h('p', { class: 'fine', text: 'Your Live Counter syncs to your account. Export a copy any time from Live Counter › Details › Data.' })];
    if (TK.sync && TK.sync.hasLegacy()) { var imp = h('button', { type: 'button', class: 'btn-ghost' }, ['Import earlier data from this browser']); imp.addEventListener('click', function () { TK.sync.offerLegacy(true); }); dataNodes.push(h('div', {}, [imp])); }
    var danger = section('Your data', 'download', dataNodes.concat([h('div', { class: 'danger-zone' }, [h('div', {}, [h('b', { text: 'Delete account' }), h('span', { class: 'fine', text: 'Permanently deletes your account, tracker, friends and settings. Squad games you played keep your chest counts under your old name.' })]), del])]));
    ui.modal({ title: 'Account', sub: acc.username, wide: true, className: 'acct-modal', body: h('div', { class: 'acct' }, [profile, privacy, security, danger]), actions: [{ label: 'Done', kind: 'gold' }] });
  }

  function regenerateCodes() {
    var pw = input('Your password', 'password', 'current-password'), msg = h('p', { class: 'msg-error', role: 'alert' });
    ui.modal({ title: 'Make new recovery codes', sub: 'Your old codes stop working immediately.', body: h('div', {}, [pw.row, msg]),
      actions: [{ label: 'Cancel', kind: 'ghost' }, { label: 'Make new codes', kind: 'gold', keepOpen: true, onClick: async function (close) {
        msg.textContent = '';
        try {
          var r = await api.rpc('tk_recovery_codes_regenerate', { p_password: pw.input.value });
          if (!r.ok) { msg.textContent = api.message(r.error); return false; }
          close();
          ui.modal({ title: 'Your new recovery codes', sub: 'Shown once. Save them now.', body: h('div', { class: 'codes-modal' }, TK.auth.codesView(r.recovery_codes, TK.auth.account().username, null, false)), actions: [{ label: 'I’ve saved them', kind: 'gold' }] });
        } catch (e) { msg.textContent = api.message(e); }
        return false;
      } }] });
  }

  function deleteAccount() {
    var acc = TK.auth.account();
    var pw = input('Your password', 'password', 'current-password'), name = h('input', { type: 'text', autocomplete: 'off', 'aria-label': 'Type your username' }), msg = h('p', { class: 'msg-error', role: 'alert' });
    ui.modal({ title: 'Delete your account?', sub: 'This can’t be undone.', alert: true,
      body: h('div', { class: 'modal-body' }, [h('p', { class: 'muted', text: 'Deletes ' + acc.username + ', your synced tracker, friends, requests and settings, and signs out every device.' }),
        pw.row, h('div', { class: 'form-row' }, [h('label', { text: 'Type ' + acc.username + ' to confirm' }), name]), msg]),
      actions: [{ label: 'Cancel', kind: 'ghost' }, { label: 'Delete forever', kind: 'danger', keepOpen: true, onClick: async function () {
        msg.textContent = '';
        if (name.value.trim().toLowerCase() !== acc.username.toLowerCase()) { msg.textContent = 'Type your username exactly to confirm.'; return false; }
        try {
          var r = await api.rpc('tk_account_delete', { p_password: pw.input.value });
          if (!r.ok) { msg.textContent = api.message(r.error); return false; }
          await api.dropTransport();
          try { var prefix = TK.config.STORAGE_PREFIX + 'u.' + acc.id + '.'; Object.keys(localStorage).forEach(function (k) { if (k.indexOf(prefix) === 0) localStorage.removeItem(k); }); localStorage.removeItem(TK.config.STORAGE_PREFIX + 'device.lastAccount'); } catch (e) { /* storage blocked */ }
          location.hash = 'signup'; location.reload();
        } catch (e) { msg.textContent = api.message(e); }
        return false;
      } }] });
  }

  TK.profile = {
    avatar: avatar,
    openAccount: openAccount,
    init: function (acc) {
      btn = U.$('#btn-profile'); menu = U.$('#profile-menu');
      paintHeader(acc);
      U.bus.on('account', paintHeader);
      btn.addEventListener('click', function (e) { e.stopPropagation(); if (open) closeMenu(); else openMenu(); });
      document.addEventListener('click', function (e) { if (open && !menu.contains(e.target)) closeMenu(); });
      menu.addEventListener('keydown', function (e) {
        var list = items(), i = list.indexOf(document.activeElement);
        if (e.key === 'Escape') { e.preventDefault(); closeMenu(true); }
        else if (e.key === 'ArrowDown') { e.preventDefault(); list[(i + 1) % list.length].focus(); }
        else if (e.key === 'ArrowUp') { e.preventDefault(); list[(i - 1 + list.length) % list.length].focus(); }
        else if (e.key === 'Tab') closeMenu();
      });
    }
  };
})(window.TK = window.TK || {});
