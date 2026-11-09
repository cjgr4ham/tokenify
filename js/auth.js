/* TOKENIFY — accounts: restore the session on load (no login flash), log in, create an account with a
 * username + password, one-time recovery codes, password reset without email, and log out.
 * Passwords are only ever sent to the tk_login / tk_signup / tk_recover functions over HTTPS and are
 * checked against bcrypt hashes on the server; nothing about them is stored in the browser. */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h, api = TK.api;
  var LAST = 'device.lastAccount';
  var account = null, offline = false, onDone = null, els = {}, lostShown = false;
  var USERNAME_RE = /^[A-Za-z0-9](?:[A-Za-z0-9]|[_.-][A-Za-z0-9])*$/;

  function cacheAccount(a) { U.storage.set(LAST, a ? { id: a.id, username: a.username, avatar: a.avatar } : null); }
  function cached() { return U.storage.get(LAST, null); }
  function setAccount(a) { account = a; cacheAccount(a); U.bus.emit('account', a); }

  // ---------------- session restore ----------------
  async function restore() {
    var s = null;
    try {
      s = await api.session();
      if (!s) return null;
      var me = await api.rpc('tk_me', {}, { retry: true, quietAuth: true });
      if (me && me.ok) { offline = false; setAccount(me.account); return account; }
      return null;                 // the transport session isn't linked to an account (logged out / expired / revoked)
    } catch (e) {
      if (e.kind === 'network' && s) {
        var c = cached();
        if (c) { offline = true; account = c; return account; }   // keep tracking offline; everything syncs later
      }
      return null;
    }
  }

  // ---------------- small form helpers ----------------
  function field(o) {
    var input = h('input', { id: o.id, type: o.type || 'text', name: o.name || o.id, autocomplete: o.ac || 'off', autocapitalize: 'off', spellcheck: 'false',
      maxlength: o.max ? String(o.max) : null, placeholder: o.placeholder || '', 'aria-describedby': o.id + '-hint', required: true, inputmode: o.inputmode || null });
    var wrap = h('div', { class: 'auth-input' }, [input]);
    if (o.type === 'password') {
      var eye = h('button', { type: 'button', class: 'auth-eye', 'aria-label': 'Show password', 'aria-pressed': 'false', tabindex: '-1' });
      eye.innerHTML = '<svg class="i"><use href="#i-eye"/></svg>';
      eye.addEventListener('click', function () { var show = input.type === 'password'; input.type = show ? 'text' : 'password'; eye.setAttribute('aria-pressed', String(show)); eye.setAttribute('aria-label', show ? 'Hide password' : 'Show password'); eye.innerHTML = '<svg class="i"><use href="#i-' + (show ? 'eye-off' : 'eye') + '"/></svg>'; input.focus(); });
      wrap.appendChild(eye);
    }
    var hint = h('div', { class: 'auth-hint', id: o.id + '-hint', 'aria-live': 'polite' }, o.hint ? [o.hint] : []);
    return { row: h('div', { class: 'auth-field' }, [h('label', { for: o.id, text: o.label }), wrap, hint]), input: input, hint: hint };
  }
  function link(text, onClick) { var b = h('button', { type: 'button', class: 'link-btn' }, [text]); b.addEventListener('click', onClick); return b; }
  function render(title, sub, nodes) {
    els.body.innerHTML = '';
    var card = h('div', { class: 'auth-screen' }, [h('h1', { class: 'auth-title', text: title }), sub ? h('p', { class: 'auth-sub', text: sub }) : null].concat(nodes));
    els.body.appendChild(card);
    var first = card.querySelector('input'); if (first) setTimeout(function () { try { first.focus({ preventScroll: true }); } catch (e) { /* ignore */ } }, 60);
  }
  function busy(btn, on, label) {
    btn.disabled = on; btn.classList.toggle('is-busy', on);
    if (label) btn.querySelector('span').textContent = label;
  }
  function submitBtn(label) { return h('button', { class: 'btn-gold btn-xl auth-submit', type: 'submit' }, [h('span', { text: label })]); }
  function errorBox() { return h('p', { class: 'msg-error auth-error', role: 'alert' }); }
  function showError(box, codeOrErr) { box.textContent = api.message(codeOrErr); box.classList.remove('shake'); void box.offsetWidth; box.classList.add('shake'); }

  // Runs a sign-in style call on a transport session, retrying once on a brand-new session if the old
  // one went stale (expired, or tidied up by the server).
  async function withTransport(fn, args) {
    await api.transport(false);
    var r = await api.rpc(fn, args, { quietAuth: true });
    if (r && !r.ok && (r.error === 'session_stale' || r.error === 'already_signed_in')) {
      await api.transport(true);
      r = await api.rpc(fn, args, { quietAuth: true });
    }
    return r;
  }

  function validUsername(n) { return n.length >= 3 && n.length <= 16 && USERNAME_RE.test(n); }
  function passwordProblem(p, name) {
    if (p.length < 8) return 'password_short';
    if (p.length > 64) return 'password_long';
    if (name && name.length >= 3 && p.toLowerCase().indexOf(name.toLowerCase()) >= 0) return 'password_has_username';
    return null;
  }

  // ---------------- screens ----------------
  function showLogin(prefill) {
    var user = field({ id: 'auth-user', label: 'Username', ac: 'username', max: 16 }), pass = field({ id: 'auth-pass', label: 'Password', type: 'password', ac: 'current-password', max: 64 });
    if (prefill) user.input.value = prefill;
    var err = errorBox(), btn = submitBtn('Log in');
    var form = h('form', { class: 'auth-form', novalidate: true }, [user.row, pass.row, err, btn]);
    form.addEventListener('submit', async function (ev) {
      ev.preventDefault(); err.textContent = '';
      var n = user.input.value.trim(), p = pass.input.value;
      if (!n || !p) { showError(err, 'bad_credentials'); return; }
      busy(btn, true, 'Logging in…');
      try {
        var r = await withTransport('tk_login', { p_username: n, p_password: p, p_device: api.deviceLabel() });
        if (!r.ok) { showError(err, r.error); busy(btn, false, 'Log in'); pass.input.select(); return; }
        pass.input.value = '';
        finish(r.account, false);
      } catch (e) { showError(err, e); busy(btn, false, 'Log in'); }
    });
    render('Log in', 'Welcome back. Pick up where you left off.', [form, h('div', { class: 'auth-links' }, [
      link('Forgot password?', function () { showRecover(user.input.value.trim()); }),
      h('span', { class: 'auth-switch' }, ['New to TOKENIFY? ', link('Create an account', function () { showSignup(); })])
    ])]);
    setRoute('login');
  }

  function showSignup() {
    var user = field({ id: 'su-user', label: 'Username', ac: 'username', max: 16, hint: '3–16 letters or numbers. _ . - allowed between them.' });
    var pass = field({ id: 'su-pass', label: 'Password', type: 'password', ac: 'new-password', max: 64, hint: 'At least 8 characters.' });
    var conf = field({ id: 'su-conf', label: 'Confirm password', type: 'password', ac: 'new-password', max: 64 });
    var err = errorBox(), btn = submitBtn('Create account'), seq = 0, availability = null;
    var check = U.debounce(async function () {
      var n = user.input.value.trim(), mine = ++seq;
      availability = null;
      if (!n) { user.hint.className = 'auth-hint'; user.hint.textContent = '3–16 letters or numbers. _ . - allowed between them.'; return; }
      if (!validUsername(n)) { user.hint.className = 'auth-hint bad'; user.hint.textContent = n.length < 3 ? 'At least 3 characters.' : n.length > 16 ? 'At most 16 characters.' : 'Letters and numbers only, with single _ . or - between them.'; return; }
      user.hint.className = 'auth-hint'; user.hint.textContent = 'Checking…';
      try {
        var r = await api.rpc('tk_username_available', { p_username: n }, { quietAuth: true });
        if (mine !== seq) return;
        if (!r.ok) { user.hint.textContent = ''; return; }
        availability = r.available;
        user.hint.className = 'auth-hint ' + (r.available ? 'good' : 'bad');
        user.hint.textContent = r.available ? '✓ ' + n + ' is available' : '✗ That username is taken';
      } catch (e) { if (mine === seq) user.hint.textContent = ''; }
    }, 350);
    user.input.addEventListener('input', check);
    pass.input.addEventListener('input', function () {
      var pr = pass.input.value ? passwordProblem(pass.input.value, user.input.value.trim()) : null;
      pass.hint.className = 'auth-hint' + (pass.input.value ? (pr ? ' bad' : ' good') : '');
      pass.hint.textContent = !pass.input.value ? 'At least 8 characters.' : pr ? api.message(pr) : '✓ Looks good';
    });
    var form = h('form', { class: 'auth-form', novalidate: true }, [user.row, pass.row, conf.row, err, btn]);
    form.addEventListener('submit', async function (ev) {
      ev.preventDefault(); err.textContent = '';
      var n = user.input.value.trim(), p = pass.input.value;
      if (!validUsername(n)) { showError(err, 'username_invalid'); user.input.focus(); return; }
      if (availability === false) { showError(err, 'username_taken'); user.input.focus(); return; }
      var pr = passwordProblem(p, n); if (pr) { showError(err, pr); pass.input.focus(); return; }
      if (p !== conf.input.value) { err.textContent = 'The passwords don’t match.'; conf.input.focus(); return; }
      busy(btn, true, 'Creating account…');
      try {
        var r = await withTransport('tk_signup', { p_username: n, p_password: p, p_device: api.deviceLabel() });
        if (!r.ok) { showError(err, r.error); busy(btn, false, 'Create account'); return; }
        pass.input.value = conf.input.value = '';
        showCodes(r.recovery_codes, r.account.username, function () { finish(r.account, true); }, true);
      } catch (e) { showError(err, e); busy(btn, false, 'Create account'); }
    });
    render('Create your account', 'Just a username and a password. No email needed.', [form, h('div', { class: 'auth-links center' }, [
      h('span', { class: 'auth-switch' }, ['Already have an account? ', link('Log in.', function () { showLogin(user.input.value.trim()); })])
    ])]);
    setRoute('signup');
  }

  function showRecover(prefill) {
    var user = field({ id: 'rc-user', label: 'Username', ac: 'username', max: 16 });
    var code = field({ id: 'rc-code', label: 'Recovery code', placeholder: 'XXXX-XXXX-XXXX', max: 20, hint: 'One of the codes you saved when you created your account.' });
    var pass = field({ id: 'rc-pass', label: 'New password', type: 'password', ac: 'new-password', max: 64 });
    var conf = field({ id: 'rc-conf', label: 'Confirm new password', type: 'password', ac: 'new-password', max: 64 });
    if (prefill) user.input.value = prefill;
    var err = errorBox(), btn = submitBtn('Reset password');
    var form = h('form', { class: 'auth-form', novalidate: true }, [user.row, code.row, pass.row, conf.row, err, btn]);
    form.addEventListener('submit', async function (ev) {
      ev.preventDefault(); err.textContent = '';
      var n = user.input.value.trim(), c = code.input.value.trim(), p = pass.input.value;
      if (!n || c.replace(/[^A-Za-z0-9]/g, '').length !== 12) { showError(err, 'recovery_failed'); return; }
      var pr = passwordProblem(p, n); if (pr) { showError(err, pr); return; }
      if (p !== conf.input.value) { err.textContent = 'The passwords don’t match.'; return; }
      busy(btn, true, 'Resetting…');
      try {
        var r = await withTransport('tk_recover', { p_username: n, p_code: c, p_new_password: p, p_device: api.deviceLabel() });
        if (!r.ok) { showError(err, r.error); busy(btn, false, 'Reset password'); return; }
        finish(r.account, false, 'Password reset. Every other device was signed out. ' + r.codes_left + ' recovery code' + (r.codes_left === 1 ? '' : 's') + ' left.');
      } catch (e) { showError(err, e); busy(btn, false, 'Reset password'); }
    });
    render('Reset password', 'TOKENIFY doesn’t use email, so resetting needs one of your recovery codes.', [form, h('div', { class: 'auth-links center' }, [link('Back to log in', function () { showLogin(user.input.value.trim()); })])]);
    setRoute('login');
  }

  // Shows recovery codes once (after sign-up, or after making new ones from the account screen).
  function codesView(codes, username, onContinue, gate) {
    var grid = h('ol', { class: 'codes-grid' }, codes.map(function (c) { return h('li', { class: 'code mono', text: c }); }));
    var text = 'TOKENIFY recovery codes for ' + username + '\nCreated ' + new Date().toLocaleString() + '\nEach code works once. Use one on the "Forgot password?" screen.\n\n' + codes.join('\n') + '\n';
    var copyBtn = h('button', { type: 'button', class: 'btn-ghost' }, ['Copy codes']);
    copyBtn.addEventListener('click', function () {
      (navigator.clipboard && navigator.clipboard.writeText ? navigator.clipboard.writeText(text) : Promise.reject()).then(function () { ui.toast('Recovery codes copied.'); }, function () { ui.toast('Couldn’t copy. Download them or write them down instead.'); });
    });
    var dl = h('button', { type: 'button', class: 'btn-ghost' }, ['Download .txt']);
    dl.addEventListener('click', function () { U.download('tokenify-recovery-codes-' + username + '.txt', text, 'text/plain'); });
    var nodes = [h('p', { class: 'auth-note', text: 'TOKENIFY doesn’t use email, so these codes are the only way back in if you forget your password. Each works once. Keep them somewhere safe, like a password manager.' }),
      grid, h('div', { class: 'codes-actions' }, [copyBtn, dl])];
    if (gate) {
      var cb = h('input', { type: 'checkbox', id: 'codes-ok' }), cont = h('button', { class: 'btn-gold btn-xl auth-submit', type: 'button', disabled: true }, [h('span', { text: 'Continue to TOKENIFY' })]);
      cb.addEventListener('change', function () { cont.disabled = !cb.checked; });
      cont.addEventListener('click', onContinue);
      nodes.push(h('label', { class: 'codes-check', for: 'codes-ok' }, [cb, h('span', { text: 'I’ve saved my recovery codes' })]), cont);
    }
    return nodes;
  }
  function showCodes(codes, username, onContinue) { render('Save your recovery codes', null, codesView(codes, username, onContinue, true)); }

  // ---------------- flow ----------------
  function setRoute(r) { try { history.replaceState(null, '', '#' + r); } catch (e) { /* file:// */ } }
  function finish(acc, isNew, toastMsg) {
    offline = false;
    setAccount(acc);
    hide();
    if (onDone) onDone(acc, { isNew: isNew });
    if (toastMsg) setTimeout(function () { ui.toast(toastMsg, { duration: 6500 }); }, 900);
  }
  function show(modeName, done) {
    onDone = done;
    els.root = U.$('#auth-root'); els.body = U.$('#auth-body');
    document.body.classList.add('auth-mode');
    els.root.hidden = false;
    if (!api.configured()) { render('Accounts unavailable', null, [h('p', { class: 'auth-note', text: api.message('not_configured') })]); return; }
    if (modeName === 'signup') showSignup(); else if (modeName === 'recover') showRecover(); else showLogin();
  }
  // Clears the screens too, so recovery codes and typed values never linger in the page.
  function hide() { document.body.classList.remove('auth-mode'); if (els.root) els.root.hidden = true; if (els.body) els.body.innerHTML = ''; }

  async function logout(opts) {
    opts = opts || {};
    var id = account && account.id;
    try { await api.rpc('tk_logout', {}, { quietAuth: true }); } catch (e) { /* offline: the local session is dropped anyway */ }
    await api.dropTransport();
    if (opts.wipeLocal && id) {
      try {
        var prefix = TK.config.STORAGE_PREFIX + 'u.' + id + '.';
        Object.keys(localStorage).forEach(function (k) { if (k.indexOf(prefix) === 0) localStorage.removeItem(k); });
      } catch (e) { /* storage blocked */ }
    }
    cacheAccount(null);
    location.hash = 'login';
    location.reload();
  }

  // A request found the session dead (signed out elsewhere, password reset, expired or revoked).
  U.bus.on('auth-lost', function () {
    if (!account || lostShown || offline) return;
    lostShown = true;
    ui.modal({ title: 'You’ve been signed out', sub: 'Your session ended: you signed out on another device, reset your password, or it expired.', dismissible: false,
      body: '<p class="muted">Everything you tracked on this device is still here and will sync when you log back in.</p>',
      actions: [{ label: 'Log in again', kind: 'gold', onClick: async function () { await api.dropTransport(); cacheAccount(null); location.hash = 'login'; location.reload(); } }] });
  });

  TK.auth = {
    restore: restore, show: show, hide: hide, logout: logout, codesView: codesView,
    account: function () { return account; },
    offline: function () { return offline; },
    goOnline: function (acc) { offline = false; if (acc) setAccount(acc); },
    update: function (acc) { if (acc) setAccount(acc); },
    validUsername: validUsername, passwordProblem: passwordProblem
  };
})(window.TK = window.TK || {});
