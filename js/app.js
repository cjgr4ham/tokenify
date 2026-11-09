/* TOKENIFY — application shell: sign-in gate, boot, navigation and routes, keyboard routing, settings.
 * Routes (hash based, so the static site works on any host):
 *   #home  #tracker[/sessions|games|tokens|estimates]  #simulator[/collection]  #probability
 *   #party  #friends  #requests  #login  #signup  #settings and #pc (dialogs over the current page)
 * Old links keep working: #open → simulator, #lab → probability, #collection → simulator/collection. */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h;
  var VIEWS = ['home', 'tracker', 'simulator', 'probability', 'social'];
  var TABS = ['home', 'tracker', 'simulator', 'probability'];
  var ALIAS = { open: 'simulator', lab: 'probability', collection: 'simulator/collection', party: 'social/party', friends: 'social/friends', requests: 'social/requests', '': 'home' };
  var current = null, simSub = 'open', started = false, account = null;

  function parse(hash) {
    var r = String(hash || '').replace(/^#\/?/, '');
    if (Object.prototype.hasOwnProperty.call(ALIAS, r)) r = ALIAS[r];
    var parts = r.split('/'), view = parts[0], sub = parts[1] || null;
    if (VIEWS.indexOf(view) < 0) { view = 'home'; sub = null; }
    return { view: view, sub: sub };
  }
  function hashFor(view, sub) {
    if (view === 'social') return '#' + (sub || 'party');
    return '#' + view + (sub ? '/' + sub : '');
  }

  function show(view, opts) {
    opts = opts || {};
    if (!started) return;
    var sub = opts.sub || null;
    if (VIEWS.indexOf(view) < 0) view = 'home';
    var changed = current !== view;
    current = view;
    VIEWS.forEach(function (v) { var sec = document.getElementById('view-' + v); if (sec) sec.hidden = v !== view; });
    TABS.forEach(function (v) { var tab = document.getElementById('tab-' + v), on = v === view; tab.setAttribute('aria-selected', on ? 'true' : 'false'); tab.tabIndex = on || (view === 'social' && v === 'home') ? 0 : -1; });
    U.$('#btn-social').classList.toggle('on', view === 'social');
    document.body.setAttribute('data-view', view);
    if (view === 'simulator') simTab(sub === 'collection' ? 'collection' : sub === 'open' ? 'open' : simSub, true);
    if (view === 'social' && TK.social) TK.social.select(sub || 'party');
    if (view === 'tracker' && sub) openTrackerPane(sub);
    var want = hashFor(view, view === 'simulator' ? (simSub === 'collection' ? 'collection' : null) : view === 'social' ? (TK.social ? TK.social.current() : sub) : null);
    if (!opts.fromHash && location.hash !== want) { try { history.replaceState(null, '', want); } catch (e) { location.hash = want; } }
    if (changed || opts.force) U.bus.emit('view', view);
    if (opts.focusPanel) { var p = document.getElementById('view-' + view); p.setAttribute('tabindex', '-1'); p.focus({ preventScroll: true }); }
    if (changed && !opts.keepScroll) window.scrollTo({ top: 0, behavior: 'auto' });
  }
  function go(hash) { var r = parse(hash); show(r.view, { sub: r.sub, force: true }); }
  TK.app = { show: show, go: go, view: function () { return current; }, simSub: function () { return simSub; } };

  // Chest Simulator: OPEN CHESTS | COLLECTION
  function simTab(which, quiet) {
    simSub = which === 'collection' ? 'collection' : 'open';
    U.$$('.sim-subnav .subtab').forEach(function (b) { var on = b.getAttribute('data-sim') === simSub; b.setAttribute('aria-selected', on ? 'true' : 'false'); b.tabIndex = on ? 0 : -1; });
    U.$('#sim-open').hidden = simSub !== 'open';
    U.$('#sim-collection').hidden = simSub !== 'collection';
    if (!quiet) { var want = simSub === 'collection' ? '#simulator/collection' : '#simulator'; try { history.replaceState(null, '', want); } catch (e) { /* ignore */ } }
    U.bus.emit('sim-tab', simSub);
    if (simSub === 'collection') U.bus.emit('view', 'collection');      // collection renders on this
  }
  function openTrackerPane(name) {
    var d = U.$('#trk-details'), tab = U.$('.td-tab[data-td="' + name + '"]');
    if (!d || !tab) return;
    d.open = true;
    setTimeout(function () { tab.click(); d.scrollIntoView({ behavior: U.reducedMotion() ? 'auto' : 'smooth', block: 'start' }); }, 30);
  }

  function initTabs() {
    var tabs = TABS.map(function (v) { return document.getElementById('tab-' + v); });
    tabs.forEach(function (t, i) {
      t.addEventListener('click', function () { show(t.getAttribute('data-view')); });
      t.addEventListener('keydown', function (e) {
        var d = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
        if (e.key === 'Home') d = -i; if (e.key === 'End') d = tabs.length - 1 - i;
        if (!d) return; e.preventDefault();
        var n = tabs[(i + d + tabs.length) % tabs.length]; n.focus(); show(n.getAttribute('data-view'), { keepScroll: true });
      });
    });
    var subs = U.$$('.sim-subnav .subtab');
    subs.forEach(function (b, i) {
      b.addEventListener('click', function () { simTab(b.getAttribute('data-sim')); });
      b.addEventListener('keydown', function (e) { var d = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0; if (!d) return; e.preventDefault(); var n = subs[(i + d + subs.length) % subs.length]; n.focus(); simTab(n.getAttribute('data-sim')); });
    });
    window.addEventListener('hashchange', function () {
      var raw = location.hash.replace(/^#\/?/, '');
      if (raw === 'login' || raw === 'signup') { show('home'); return; }   // already signed in
      if (dialogRoute(raw)) return;
      var r = parse(location.hash); show(r.view, { sub: r.sub, fromHash: true });
    });
  }
  // #settings and #pc open a dialog over the page you're on (the companion's "Open TOKENIFY" links to #pc).
  function dialogRoute(raw) {
    if (raw !== 'settings' && raw !== 'pc') return false;
    var back = hashFor(current || 'home', null);
    try { history.replaceState(null, '', back); } catch (e) { /* ignore */ }
    if (!current) show('home', { force: true });
    setTimeout(function () { if (raw === 'pc' && TK.pc) TK.pc.open('pc'); else openSettings(); }, 0);
    return true;
  }

  // ---------- global keyboard routing ----------
  function initKeys() {
    document.addEventListener('keydown', function (e) {
      if (!started || e.defaultPrevented || ui.isModalOpen()) return;
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      if (document.querySelector('.cine-global')) return;
      var t = e.target;
      if (U.isTypingTarget(t)) return;
      if (current === 'tracker' && TK.trackerUI && TK.trackerUI.onKey(e)) return;
      if (current === 'simulator' && simSub === 'open' && TK.openChests) {
        if (e.key === 'Enter' && U.isInteractive(t) && t.id !== 'chest' && t.id !== 'btn-open') return; // let focused controls handle Enter
        TK.openChests.onKey(e);
      }
    });
  }

  // ---------- sound ----------
  function initSound() {
    var btn = U.$('#btn-sound');
    function sync() { var on = TK.settings.get('sound') !== false; btn.setAttribute('aria-pressed', on ? 'true' : 'false'); btn.title = on ? 'Sound on' : 'Sound off'; TK.audio.setEnabled(on); }
    btn.addEventListener('click', function () { TK.settings.set('sound', !(TK.settings.get('sound') !== false)); sync(); if (TK.settings.get('sound')) TK.audio.play('ui'); });
    TK.audio.setVolume(TK.settings.get('volume'));
    sync();
  }

  // ---------- reduced motion ----------
  function applyMotion() { document.documentElement.classList.toggle('reduce-motion', U.reducedMotion()); }

  // ---------- theme (dark / light / system) ----------
  var lightMq = window.matchMedia ? window.matchMedia('(prefers-color-scheme: light)') : null;
  function resolvedTheme() {
    var t = TK.settings.get('theme') || 'dark';
    if (t === 'system') t = lightMq && lightMq.matches ? 'light' : 'dark';
    return t === 'light' ? 'light' : 'dark';
  }
  function applyTheme() {
    var t = resolvedTheme(), root = document.documentElement;
    if (root.getAttribute('data-theme') !== t) root.setAttribute('data-theme', t);
    var meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.setAttribute('content', t === 'light' ? '#f7efdd' : '#0a1020');
    U.bus.emit('theme', t);
  }
  TK.app.theme = resolvedTheme;

  // ---------- settings dialog ----------
  function radioRow(label, sub, key, options) {
    var wrap = h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: label }), sub ? h('span', { text: sub }) : null])]);
    var seg = h('div', { class: 'seg', role: 'radiogroup', 'aria-label': label });
    options.forEach(function (o) { seg.appendChild(h('button', { type: 'button', role: 'radio', 'aria-checked': String(TK.settings.get(key) === o[0]), 'data-v': o[0] }, [o[1]])); });
    ui.bindRadios(seg, 'data-v', function (v) { TK.settings.set(key, v); });
    wrap.appendChild(seg);
    return wrap;
  }
  function toggleRow(label, sub, checked, onChange) {
    var input = h('input', { type: 'checkbox' }); input.checked = !!checked;
    input.addEventListener('change', function () { onChange(input.checked); });
    return h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: label }), sub ? h('span', { text: sub }) : null]), h('label', { class: 'toggle' }, [input, h('span', { class: 'track' }), h('span', { class: 'sr-only', text: label })])]);
  }
  function estimatesGroup() {
    var acc = TK.auth.account() || {}, est = (acc.settings && acc.settings.est) || { barely: 25, some: 60, hunted: 100 };
    var box = h('div', {}), inputs = {};
    [['barely', 'Barely looted'], ['some', 'Some looting'], ['hunted', 'Hunted chests']].forEach(function (r) {
      var i = h('input', { type: 'number', min: '0', max: '200', step: '5', inputmode: 'decimal', 'aria-label': r[1] + ' percentage', class: 'pct-input' }); i.value = est[r[0]];
      inputs[r[0]] = i;
      box.appendChild(h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: r[1] }), h('span', { text: 'Teammate’s chests as a % of yours' })]), h('div', { class: 'pct-wrap' }, [i, h('span', { text: '%' })])]));
    });
    var save = U.debounce(async function () {
      var v = {}; Object.keys(inputs).forEach(function (k) { v[k] = parseFloat(inputs[k].value); });
      if (Object.keys(v).some(function (k) { return !(v[k] >= 0 && v[k] <= 200); })) return;
      try { var r = await TK.api.rpc('tk_profile_update', { p_estimates: v }); if (r.ok) { TK.auth.update(r.account); ui.toast('Estimate assumptions saved.'); } else ui.toast(TK.api.message(r.error)); }
      catch (e) { ui.toast(TK.api.message(e)); }
    }, 700);
    Object.keys(inputs).forEach(function (k) { inputs[k].addEventListener('input', save); });
    box.appendChild(h('p', { class: 'fine', text: 'Used only when you estimate a teammate who doesn’t use TOKENIFY. These are adjustable assumptions, not Fortnite statistics.' }));
    return box;
  }
  function openSettings() {
    var body = h('div', {});
    body.appendChild(h('div', { class: 'settings-group-title', text: 'Appearance' }));
    body.appendChild(radioRow('Theme', 'Light is a sunlit parchment look; System follows your device.', 'theme', [['dark', 'Dark'], ['light', 'Light'], ['system', 'System']]));
    body.appendChild(h('div', { class: 'settings-group-title', text: 'Sound' }));
    body.appendChild(toggleRow('Sound effects', 'Synthesised in your browser. Off by default until you interact.', TK.settings.get('sound') !== false, function (v) { TK.settings.set('sound', v); TK.audio.setEnabled(v); U.$('#btn-sound').setAttribute('aria-pressed', v ? 'true' : 'false'); }));
    var vol = h('input', { type: 'range', min: '0', max: '1', step: '0.05', 'aria-label': 'Volume' }); vol.value = TK.settings.get('volume');
    vol.addEventListener('input', function () { TK.settings.set('volume', parseFloat(vol.value)); TK.audio.setVolume(parseFloat(vol.value)); });
    vol.addEventListener('change', function () { TK.audio.play('coin'); });
    body.appendChild(h('div', { class: 'setting' }, [h('div', { class: 'setting-l' }, [h('b', { text: 'Volume' })]), h('div', { style: { width: '180px' } }, [vol])]));
    body.appendChild(h('div', { class: 'settings-group-title', text: 'Animation' }));
    body.appendChild(radioRow('Animation speed', 'Changes presentation only — never the odds.', 'speed', [['cinematic', 'Cinematic'], ['normal', 'Normal'], ['fast', 'Fast']]));
    body.appendChild(radioRow('Effects quality', 'Fewer particles on slower devices.', 'quality', [['high', 'High'], ['medium', 'Medium'], ['low', 'Low']]));
    body.appendChild(radioRow('Reduced motion', 'No shaking or flashing when on.', 'reducedMotion', [['system', 'System'], ['on', 'On'], ['off', 'Off']]));
    body.appendChild(h('div', { class: 'settings-group-title', text: 'Squad estimates' }));
    body.appendChild(estimatesGroup());
    if (TK.pc) {
      body.appendChild(h('div', { class: 'settings-group-title', text: 'Automatic PC tracking' }));
      body.appendChild(TK.pc.settingsSection());
    }
    body.appendChild(h('div', { class: 'settings-group-title', text: 'Your data' }));
    body.appendChild(h('p', { class: 'fine', text: 'Your Live Counter syncs to your account. Simulator openings and Probability Lab runs are hypothetical and stay in this browser, separate from your real tracker.' }));
    function resetBtn(label, message, fn) {
      var b = h('button', { class: 'btn-ghost danger', type: 'button' }, [label]);
      b.addEventListener('click', function () { ui.confirm({ title: label + '?', message: message, confirmLabel: 'Reset', danger: true }).then(function (ok) { if (ok) { fn(); ui.toast(label + ' — done.'); } }); });
      return b;
    }
    body.appendChild(h('div', { style: { display: 'flex', flexWrap: 'wrap', gap: '8px' } }, [
      resetBtn('Reset simulator statistics', 'Clears simulated chests, your collection and recent history in this browser. Your Live Counter and Lab runs are kept.', function () { TK.store.interactive.reset(); }),
      resetBtn('Reset laboratory history', 'Clears saved Probability Lab runs in this browser.', function () { TK.store.lab.clear(); })
    ]));
    ui.modal({ title: 'Settings', body: body, actions: [{ label: 'Done', kind: 'gold' }] });
  }
  TK.app.openSettings = openSettings;

  // ---------- loader ----------
  var loader = null, fill = null, ltext = null, progress = 0.05;
  function setP(p) { progress = Math.max(progress, p); fill.style.width = Math.round(progress * 100) + '%'; }
  function hideLoader() {
    if (!loader || loader.classList.contains('done')) return;
    setP(1);
    loader.classList.add('done'); document.body.classList.remove('booting');
    var brand = U.$('.brand');
    if (brand && !U.reducedMotion() && started) { brand.classList.add('intro'); setTimeout(function () { brand.classList.add('sheen'); }, 900); setTimeout(function () { brand.classList.remove('intro', 'sheen'); }, 3200); }
  }
  function showLoader(text) {
    if (!loader) return;
    progress = 0.05; fill.style.width = '5%';
    ltext.textContent = text || 'Loading…';
    loader.classList.remove('done'); document.body.classList.add('booting');
  }

  // ---------- start the signed-in app ----------
  function start(acc, info) {
    account = acc; info = info || {};
    U.storage.setScope(acc.id);
    TK.store.load();
    started = true;
    document.body.classList.add('signed-in');
    var t0 = performance.now(), tasks = [];
    try { tasks.push(TK.openChests.init(function (p) { setP(0.15 + p * 0.75); })); } catch (e) { console.error(e); }
    ['lab', 'trackerUI', 'collection', 'profile', 'social', 'party', 'pc', 'home'].forEach(function (m) {
      try { if (TK[m] && TK[m].init) TK[m].init(acc); } catch (e) { console.error('[tokenify] ' + m + ' failed to start', e); }
    });
    try { TK.sync.start(); } catch (e) { console.error(e); }
    var raw = location.hash.replace(/^#\/?/, '');
    // signing in always lands on Home; a restored session keeps the page you had open
    var r = (info.fromAuth || raw === 'login' || raw === 'signup') ? { view: 'home', sub: null } : parse(location.hash);
    show(r.view, { sub: r.sub, fromHash: false, force: true });
    if (raw === 'settings' || raw === 'pc') dialogRoute(raw);
    // The landing pages don't need the chest artwork; the simulator waits for it (at most a few seconds).
    var wait = r.view === 'simulator' ? Promise.race([Promise.all(tasks), U.wait(7000)]) : Promise.resolve();
    if (document.fonts && document.fonts.ready) wait = Promise.all([wait, Promise.race([document.fonts.ready, U.wait(1500)])]);
    wait.then(function () { setTimeout(hideLoader, Math.max(0, 450 - (performance.now() - t0))); });
    if (TK.auth.offline()) ui.toast('You’re offline. Tracking still works and will sync when you’re back.', { duration: 6000 });
    if (!U.storage.available) ui.toast('Browser storage is unavailable — your progress will last for this session only.', { duration: 7000 });
  }

  // ---------- boot ----------
  function boot() {
    loader = U.$('#loader'); fill = U.$('#loader-fill'); ltext = U.$('#loader-text');
    applyMotion(); applyTheme();
    if (window.matchMedia) { var mq = window.matchMedia('(prefers-reduced-motion: reduce)'); if (mq.addEventListener) mq.addEventListener('change', applyMotion); }
    if (lightMq && lightMq.addEventListener) lightMq.addEventListener('change', function () { if (TK.settings.get('theme') === 'system') applyTheme(); });
    U.bus.on('settings', function (e) { if (e.key === 'reducedMotion' || e.key === '*') applyMotion(); if (e.key === 'theme' || e.key === '*') applyTheme(); });
    ui.initTooltips();
    ui.blurOnPointer(document.body);
    initTabs(); initKeys(); initSound();
    U.bus.on('storage-error', function () { ui.toast('Couldn’t save to browser storage (it may be full). Export your tracker data from Details › Data.', { duration: 8000 }); });
    window.addEventListener('error', function (e) { console.error('[tokenify]', e.message); });
    ltext.textContent = 'Signing you in…'; setP(0.08);
    TK.auth.restore().then(function (acc) {
      setP(0.15);
      if (acc) { start(acc, {}); return; }
      hideLoader();
      var raw = location.hash.replace(/^#\/?/, '');
      TK.auth.show(raw === 'signup' ? 'signup' : 'login', function (a, info) {
        showLoader('Loading your treasure…');
        start(a, { isNew: info.isNew, fromAuth: true });
      });
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})(window.TK = window.TK || {});
