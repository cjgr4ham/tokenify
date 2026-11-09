/* TOKENIFY — application shell: boot, navigation, keyboard routing, settings. */
(function (TK) {
  'use strict';
  var U = TK.util, ui = TK.ui, h = U.h;
  var VIEWS = ['open', 'lab', 'tracker', 'collection'];
  var current = null;

  function show(view, opts) {
    opts = opts || {};
    if (VIEWS.indexOf(view) < 0) view = 'open';
    if (current === view) return;
    current = view;
    VIEWS.forEach(function (v) {
      var sec = document.getElementById('view-' + v), tab = document.getElementById('tab-' + v), on = v === view;
      sec.hidden = !on; tab.setAttribute('aria-selected', on ? 'true' : 'false'); tab.tabIndex = on ? 0 : -1;
    });
    document.body.setAttribute('data-view', view);
    TK.settings.set('lastView', view);
    if (!opts.fromHash && location.hash !== '#' + view) { try { history.replaceState(null, '', '#' + view); } catch (e) { location.hash = view; } }
    U.bus.emit('view', view);
    if (opts.focusPanel) { var p = document.getElementById('view-' + view); p.setAttribute('tabindex', '-1'); p.focus({ preventScroll: true }); }
    if (!opts.keepScroll) window.scrollTo({ top: 0, behavior: 'auto' });
  }
  TK.app = { show: show, view: function () { return current; } };

  function initTabs() {
    var tabs = U.$$('.tab');
    tabs.forEach(function (t, i) {
      t.addEventListener('click', function () { show(t.getAttribute('data-view')); });
      t.addEventListener('keydown', function (e) {
        var d = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
        if (e.key === 'Home') d = -i; if (e.key === 'End') d = tabs.length - 1 - i;
        if (!d) return; e.preventDefault();
        var n = tabs[(i + d + tabs.length) % tabs.length]; n.focus(); show(n.getAttribute('data-view'), { keepScroll: true });
      });
    });
    window.addEventListener('hashchange', function () { show(location.hash.slice(1), { fromHash: true }); });
  }

  // ---------- global keyboard routing ----------
  function initKeys() {
    document.addEventListener('keydown', function (e) {
      if (e.defaultPrevented || ui.isModalOpen()) return;
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      if (document.querySelector('.cine-global')) return;
      var t = e.target;
      if (U.isTypingTarget(t)) return;
      if (current === 'tracker' && TK.trackerUI && TK.trackerUI.onKey(e)) return;
      if (current === 'open' && TK.openChests) {
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
    body.appendChild(h('div', { class: 'settings-group-title', text: 'Your data' }));
    body.appendChild(h('p', { class: 'fine', text: U.storage.available ? 'Saved in this browser. Interactive openings, laboratory runs and Live Tracker records are stored separately.' : 'Browser storage is unavailable here, so data lasts for this session only.' }));
    function resetBtn(label, message, fn) {
      var b = h('button', { class: 'btn-ghost danger', type: 'button' }, [label]);
      b.addEventListener('click', function () { ui.confirm({ title: label + '?', message: message, confirmLabel: 'Reset', danger: true }).then(function (ok) { if (ok) { fn(); ui.toast(label + ' — done.'); } }); });
      return b;
    }
    body.appendChild(h('div', { style: { display: 'flex', flexWrap: 'wrap', gap: '8px' } }, [
      resetBtn('Reset interactive statistics', 'Clears chests opened, your collection and recent history on this device. Laboratory and tracker data are kept.', function () { TK.store.interactive.reset(); }),
      resetBtn('Reset laboratory history', 'Clears saved Probability Lab runs. Your collection and tracker are kept.', function () { TK.store.lab.clear(); }),
      resetBtn('Reset Live Tracker data', 'Deletes every tracked session, game and token find stored on this device (cloud copies are not touched).', function () { if (TK.tracker) TK.tracker.resetAll(); }),
      resetBtn('Reset all local data', 'Erases everything TOKENIFY stores in this browser: settings, collection, laboratory runs and tracker records.', function () {
        TK.store.interactive.reset(); TK.store.lab.clear(); if (TK.tracker) TK.tracker.resetAll(); TK.settings.reset(); applyMotion();
      })
    ]));
    ui.modal({ title: 'Settings', body: body, actions: [{ label: 'Done', kind: 'gold' }] });
  }

  // ---------- boot ----------
  function boot() {
    var fill = U.$('#loader-fill'), text = U.$('#loader-text'), loader = U.$('#loader');
    var progress = 0.05;
    function setP(p) { progress = Math.max(progress, p); fill.style.width = Math.round(progress * 100) + '%'; }
    applyMotion(); applyTheme();
    if (window.matchMedia) { var mq = window.matchMedia('(prefers-reduced-motion: reduce)'); if (mq.addEventListener) mq.addEventListener('change', applyMotion); }
    if (lightMq && lightMq.addEventListener) lightMq.addEventListener('change', function () { if (TK.settings.get('theme') === 'system') applyTheme(); });
    U.bus.on('settings', function (e) { if (e.key === 'reducedMotion' || e.key === '*') applyMotion(); if (e.key === 'theme' || e.key === '*') applyTheme(); });
    ui.initTooltips();
    ui.blurOnPointer(document.body);
    initTabs(); initKeys(); initSound();
    U.$('#btn-settings').addEventListener('click', openSettings);
    U.$('#btn-account').addEventListener('click', function () { if (TK.sync) TK.sync.openAccount(); });
    var start = performance.now();
    var tasks = [];
    try { tasks.push(TK.openChests.init(function (p) { setP(0.1 + p * 0.75); })); } catch (e) { console.error(e); }
    ['lab', 'trackerUI', 'collection'].forEach(function (m) { try { if (TK[m] && TK[m].init) TK[m].init(); } catch (e) { console.error('[tokenify] ' + m + ' failed to start', e); } });
    try { if (TK.sync && TK.sync.init) TK.sync.init(); } catch (e) { console.error(e); }
    if (document.fonts && document.fonts.ready) tasks.push(Promise.race([document.fonts.ready, U.wait(2500)]));
    text.textContent = 'Loading the chest…';
    Promise.race([Promise.all(tasks), U.wait(7000)]).then(function () {
      setP(1);
      var wait = Math.max(0, 650 - (performance.now() - start));
      setTimeout(function () {
        loader.classList.add('done'); document.body.classList.remove('booting');
        var brand = U.$('.brand');
        if (!U.reducedMotion()) { brand.classList.add('intro'); setTimeout(function () { brand.classList.add('sheen'); }, 900); setTimeout(function () { brand.classList.remove('intro', 'sheen'); }, 3200); }
        if (!U.storage.available) ui.toast('Browser storage is unavailable — your progress will last for this session only.', { duration: 7000 });
      }, wait);
    });
    var initial = location.hash.slice(1);
    show(VIEWS.indexOf(initial) >= 0 ? initial : (TK.settings.get('lastView') || 'open'), { fromHash: VIEWS.indexOf(initial) >= 0 });
    U.bus.on('storage-error', function () { ui.toast('Couldn’t save to browser storage (it may be full). Export your tracker data from Details › Data.', { duration: 8000 }); });
    window.addEventListener('error', function (e) { console.error('[tokenify]', e.message); });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})(window.TK = window.TK || {});
