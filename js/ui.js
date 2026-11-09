/* TOKENIFY — UI primitives: modal dialogs, confirm, toasts, tooltips, segmented controls. */
(function (TK) {
  'use strict';
  var U = TK.util, h = U.h;
  var ui = {};
  var openModals = [];

  function focusables(root) {
    return U.$$('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"]), summary', root)
      .filter(function (el) { return el.offsetParent !== null || el === document.activeElement; });
  }

  // modal({ title, sub, body, actions:[{label, kind, onClick}], wide, dismissible, onClose, className })
  ui.modal = function (opts) {
    var root = document.getElementById('modal-root');
    var opener = document.activeElement;
    var titleId = 'm-' + Math.random().toString(36).slice(2);
    var closeBtn = h('button', { class: 'icon-btn modal-close', type: 'button', 'aria-label': 'Close' }, []);
    closeBtn.innerHTML = '<svg class="i"><use href="#i-close"/></svg>';
    var body = h('div', { class: 'modal-body' });
    if (typeof opts.body === 'string') body.innerHTML = opts.body; else if (opts.body) body.appendChild(opts.body);
    var actions = h('div', { class: 'modal-actions' });
    var dialog = h('div', { class: 'modal' + (opts.wide ? ' wide' : '') + (opts.className ? ' ' + opts.className : ''), role: opts.alert ? 'alertdialog' : 'dialog', 'aria-modal': 'true', 'aria-labelledby': titleId }, [
      opts.dismissible === false ? null : closeBtn,
      h('h2', { class: 'modal-title', id: titleId, text: opts.title || '' }),
      opts.sub ? h('p', { class: 'modal-sub', text: opts.sub }) : null,
      body, (opts.actions && opts.actions.length) ? actions : null
    ]);
    var backdrop = h('div', { class: 'modal-backdrop' }, [dialog]);
    var closed = false;
    function close(result) {
      if (closed) return; closed = true;
      backdrop.remove();
      openModals = openModals.filter(function (m) { return m !== api; });
      document.removeEventListener('keydown', onKey, true);
      if (opts.onClose) opts.onClose(result);
      if (opener && opener.focus && document.contains(opener)) { try { opener.focus({ preventScroll: true }); } catch (e) { /* ignore */ } }
    }
    (opts.actions || []).forEach(function (a) {
      var b = h('button', { type: 'button', class: a.kind === 'gold' ? 'btn-gold' : ('btn-ghost' + (a.kind === 'danger' ? ' danger' : '')) }, [a.label]);
      b.addEventListener('click', function () { var r = a.onClick ? a.onClick(close, b) : undefined; if (r !== false && !a.keepOpen) close(a.value); });
      if (a.autofocus) b.setAttribute('data-autofocus', '');
      actions.appendChild(b);
    });
    closeBtn.addEventListener('click', function () { close(null); });
    backdrop.addEventListener('mousedown', function (e) { if (e.target === backdrop && opts.dismissible !== false) close(null); });
    function onKey(e) {
      if (openModals[openModals.length - 1] !== api) return;
      if (e.key === 'Escape' && opts.dismissible !== false) { e.preventDefault(); e.stopPropagation(); close(null); return; }
      if (e.key === 'Tab') {
        var f = focusables(dialog); if (!f.length) return;
        var first = f[0], last = f[f.length - 1];
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      }
      e.stopPropagation(); // keep global shortcuts (Space, N, Enter) away while a dialog is open
    }
    document.addEventListener('keydown', onKey, true);
    root.appendChild(backdrop);
    var api = { close: close, el: dialog, body: body };
    openModals.push(api);
    setTimeout(function () {
      var af = dialog.querySelector('[data-autofocus]') || dialog.querySelector('input, select, textarea') || focusables(dialog).filter(function (x) { return x !== closeBtn; })[0] || closeBtn;
      if (af) af.focus();
    }, 30);
    return api;
  };
  ui.isModalOpen = function () { return openModals.length > 0; };

  ui.confirm = function (o) {
    return new Promise(function (resolve) {
      var done = false;
      ui.modal({
        title: o.title || 'Are you sure?', sub: o.message, alert: true,
        body: o.body || null,
        actions: [
          { label: o.cancelLabel || 'Cancel', kind: 'ghost', onClick: function () { done = true; resolve(false); } },
          { label: o.confirmLabel || 'Confirm', kind: o.danger ? 'danger' : 'gold', autofocus: !o.danger, onClick: function () { done = true; resolve(true); } }
        ],
        onClose: function () { if (!done) resolve(false); }
      });
    });
  };

  // ---------- toasts ----------
  ui.toast = function (message, o) {
    o = o || {};
    var wrap = document.getElementById('toasts');
    var t = h('div', { class: 'toast', role: 'status' }, [h('span', { class: 'toast-msg', text: message })]);
    var timer;
    function remove() { clearTimeout(timer); t.classList.add('out'); setTimeout(function () { t.remove(); }, 260); }
    if (o.action) {
      var b = h('button', { type: 'button' }, [o.action.label]);
      b.addEventListener('click', function () { o.action.onClick(); remove(); });
      t.appendChild(b);
    }
    wrap.appendChild(t);
    while (wrap.children.length > 3) wrap.firstChild.remove();
    timer = setTimeout(remove, o.duration || (o.action ? 6000 : 3200));
    return { close: remove };
  };

  // ---------- tooltips: any element with data-tip="key" or data-tip-text ----------
  ui.tips = {};
  ui.initTooltips = function () {
    var tip = document.getElementById('tooltip'), current = null;
    function show(el) {
      var key = el.getAttribute('data-tip'), text = el.getAttribute('data-tip-text') || (key && ui.tips[key]) || '';
      if (typeof text === 'function') text = text();
      if (!text) return;
      current = el; tip.textContent = text; tip.hidden = false;
      var r = el.getBoundingClientRect(), tr = tip.getBoundingClientRect();
      var x = Math.min(window.innerWidth - tr.width - 10, Math.max(10, r.left + r.width / 2 - tr.width / 2));
      var y = r.top - tr.height - 10; if (y < 10) y = r.bottom + 10;
      tip.style.left = x + 'px'; tip.style.top = y + 'px';
    }
    function hide() { current = null; tip.hidden = true; }
    document.addEventListener('mouseover', function (e) { var el = e.target.closest('[data-tip],[data-tip-text]'); if (el && el !== current) show(el); else if (!el && current) hide(); });
    document.addEventListener('focusin', function (e) { var el = e.target.closest('[data-tip],[data-tip-text]'); if (el) show(el); });
    document.addEventListener('focusout', hide);
    document.addEventListener('scroll', hide, true);
    document.addEventListener('click', function (e) { var el = e.target.closest('[data-tip],[data-tip-text]'); if (el && el.classList.contains('info-btn')) { if (current === el) hide(); else show(el); } });
  };

  // ---------- segmented radio groups ----------
  // bindRadios(container, attr, onChange): buttons with role=radio inside container; arrow-key navigation.
  ui.bindRadios = function (container, attr, onChange) {
    var buttons = U.$$('[role="radio"]', container);
    function select(btn, focus) {
      buttons.forEach(function (b) { var on = b === btn; b.setAttribute('aria-checked', on ? 'true' : 'false'); b.tabIndex = on ? 0 : -1; b.classList.toggle('on', on && b.classList.contains('chip')); });
      if (focus) btn.focus();
      onChange(btn.getAttribute(attr), btn);
    }
    buttons.forEach(function (b, i) {
      b.tabIndex = b.getAttribute('aria-checked') === 'true' ? 0 : -1;
      b.addEventListener('click', function () { select(b); });
      b.addEventListener('keydown', function (e) {
        var d = (e.key === 'ArrowRight' || e.key === 'ArrowDown') ? 1 : (e.key === 'ArrowLeft' || e.key === 'ArrowUp') ? -1 : 0;
        if (!d) return; e.preventDefault();
        select(buttons[(i + d + buttons.length) % buttons.length], true);
      });
    });
    return {
      set: function (val) { var b = buttons.filter(function (x) { return x.getAttribute(attr) === String(val); })[0]; if (b) { buttons.forEach(function (x) { var on = x === b; x.setAttribute('aria-checked', on ? 'true' : 'false'); x.tabIndex = on ? 0 : -1; x.classList.toggle('on', on && x.classList.contains('chip')); }); } },
      clear: function () { buttons.forEach(function (x) { x.setAttribute('aria-checked', 'false'); x.classList.remove('on'); }); if (buttons[0]) buttons[0].tabIndex = 0; }
    };
  };

  // Blur buttons after pointer clicks so Space/Enter shortcuts don't re-trigger them.
  ui.blurOnPointer = function (root) {
    root.addEventListener('pointerup', function (e) {
      var b = e.target.closest('button'); if (b && e.pointerType !== '') setTimeout(function () { if (document.activeElement === b) b.blur(); }, 0);
    });
  };

  // Reward artwork with automatic fallback to the bundled SVG if the image cannot load.
  ui.art = function (o, small) { return (small && o.artSmall) ? o.artSmall : o.art; };
  ui.img = function (o, opts) {
    opts = opts || {};
    var im = document.createElement('img');
    im.alt = opts.alt != null ? opts.alt : o.name; im.decoding = 'async'; im.draggable = false;
    if (opts.className) im.className = opts.className;
    if (opts.size) { im.width = opts.size; im.height = opts.size; }
    var fb = o.fallback || 'assets/brand/compass.svg';
    im.addEventListener('error', function onErr() { if (im.src.indexOf(fb) < 0) im.src = fb; else im.removeEventListener('error', onErr); });
    im.src = ui.art(o, opts.small);
    return im;
  };
  // Global safety net: any reward <img> that fails falls back to its SVG (data-fallback attribute).
  document.addEventListener('error', function (e) {
    var t = e.target; if (t && t.tagName === 'IMG' && t.dataset && t.dataset.fallback && t.src.indexOf(t.dataset.fallback) < 0) t.src = t.dataset.fallback;
  }, true);

  TK.ui = ui;
})(window.TK = window.TK || {});
