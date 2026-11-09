/* TOKENIFY — shared utilities (DOM, storage, parsing, formatting, events). */
(function (TK) {
  'use strict';

  var U = {};

  // ---------- DOM ----------
  U.$ = function (sel, root) { return (root || document).querySelector(sel); };
  U.$$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };
  U.esc = function (s) {
    return String(s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; });
  };
  U.h = function (tag, attrs, children) {
    var el = document.createElement(tag);
    if (attrs) Object.keys(attrs).forEach(function (k) {
      var v = attrs[k];
      if (v == null || v === false) return;
      if (k === 'class') el.className = v;
      else if (k === 'text') el.textContent = v;
      else if (k === 'html') el.innerHTML = v;
      else if (k.slice(0, 2) === 'on' && typeof v === 'function') el.addEventListener(k.slice(2), v);
      else if (k === 'style' && typeof v === 'object') Object.keys(v).forEach(function (sk) {
        if (v[sk] == null || v[sk] === '') return;
        if (sk.slice(0, 2) === '--') el.style.setProperty(sk, v[sk]); else el.style[sk] = v[sk];
      });
      else el.setAttribute(k, v === true ? '' : v);
    });
    (children || []).forEach(function (c) { if (c != null) el.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
    return el;
  };
  U.setText = function (el, text) { if (el && el.textContent !== text) el.textContent = text; };

  // ---------- storage (never throws; falls back to memory) ----------
  var memory = {};
  var lsOk = (function () {
    try { var k = '__tk_probe__'; window.localStorage.setItem(k, '1'); window.localStorage.removeItem(k); return true; } catch (e) { return false; }
  })();
  U.storage = {
    available: lsOk,
    key: function (k) { return TK.config.STORAGE_PREFIX + k; },
    getRaw: function (k) {
      var key = this.key(k);
      if (lsOk) { try { return window.localStorage.getItem(key); } catch (e) { /* fall through */ } }
      return Object.prototype.hasOwnProperty.call(memory, key) ? memory[key] : null;
    },
    setRaw: function (k, v) {
      var key = this.key(k);
      memory[key] = v;
      if (lsOk) { try { window.localStorage.setItem(key, v); return true; } catch (e) { U.bus.emit('storage-error', e); return false; } }
      return false;
    },
    get: function (k, fallback) {
      var raw = this.getRaw(k);
      if (raw == null) return fallback;
      try { return JSON.parse(raw); } catch (e) { return fallback; }
    },
    set: function (k, v) { return this.setRaw(k, JSON.stringify(v)); },
    remove: function (k) {
      var key = this.key(k); delete memory[key];
      if (lsOk) { try { window.localStorage.removeItem(key); } catch (e) { /* ignore */ } }
    }
  };

  // ---------- ids ----------
  U.uuid = function () {
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    var b = new Uint8Array(16); crypto.getRandomValues(b);
    b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128;
    var hx = Array.prototype.map.call(b, function (x) { return (x + 256).toString(16).slice(1); }).join('');
    return hx.slice(0, 8) + '-' + hx.slice(8, 12) + '-' + hx.slice(12, 16) + '-' + hx.slice(16, 20) + '-' + hx.slice(20);
  };

  // ---------- parsing ----------
  // Parse a non-negative whole number of chests. Accepts "1,000,000", "1 000 000", "1e6", "2.5m", "10k".
  // Returns { ok, value: BigInt } or { ok:false, error }.
  U.parseCount = function (input, opts) {
    opts = opts || {};
    var s = String(input == null ? '' : input).trim().toLowerCase().replace(/[\s,_']/g, '');
    if (s === '') return { ok: false, error: opts.emptyMessage || 'Enter a number.' , empty: true };
    if (s[0] === '-') return { ok: false, error: 'The number can’t be negative.' };
    if (s[0] === '+') s = s.slice(1);
    var mult = 1n, m;
    var suffix = { k: 3, m: 6, b: 9, t: 12 };
    if ((m = s.match(/^([0-9]*\.?[0-9]+)([kmbt])$/))) { s = m[1]; mult = 10n ** BigInt(suffix[m[2]]); }
    var exp = 0;
    if ((m = s.match(/^([0-9]*\.?[0-9]+)e\+?([0-9]+)$/))) { s = m[1]; exp = parseInt(m[2], 10); if (exp > 400) return { ok: false, error: 'That number is too large to handle.' }; }
    if (!/^[0-9]*\.?[0-9]+$/.test(s) && !/^[0-9]+\.?$/.test(s)) return { ok: false, error: 'Use digits only, e.g. 1,000,000.' };
    var parts = s.split('.');
    var intPart = parts[0] || '0', frac = (parts[1] || '').replace(/0+$/, '');
    var scale = 10n ** BigInt(exp);
    var numer = BigInt(intPart + frac) * mult * scale;
    var denom = 10n ** BigInt(frac.length);
    if (numer % denom !== 0n) return { ok: false, error: 'Chest counts must be whole numbers.' };
    return { ok: true, value: numer / denom };
  };

  // Parse a percentage like "99.9" or "99.9%" into an exact rational (BigInt num/den, value in [0,1]).
  U.parsePercent = function (input) {
    var s = String(input == null ? '' : input).trim().replace(/%$/, '').replace(/,/g, '.').trim();
    if (s === '') return { ok: false, error: 'Enter a percentage.', empty: true };
    if (!/^[0-9]*\.?[0-9]+$/.test(s) && !/^[0-9]+\.?$/.test(s)) return { ok: false, error: 'Enter a percentage between 0 and 100.' };
    var parts = s.split('.');
    var frac = (parts[1] || '');
    var num = BigInt((parts[0] || '0') + frac);
    var den = 100n * 10n ** BigInt(frac.length);
    if (num > den) return { ok: false, error: 'A probability can’t exceed 100%.' };
    return { ok: true, num: num, den: den, value: Number(num) / Number(den), text: s };
  };

  // ---------- formatting ----------
  U.groupDigits = function (str) {
    var neg = str[0] === '-'; if (neg) str = str.slice(1);
    var out = str.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    return (neg ? '-' : '') + out;
  };
  U.fmtInt = function (x) {
    if (typeof x === 'bigint') return U.groupDigits(x.toString());
    if (typeof x === 'string') return U.groupDigits(x);
    if (!isFinite(x)) return String(x);
    return U.groupDigits(Math.round(x).toString());
  };
  U.fmtNum = function (x, decimals) {
    if (!isFinite(x)) return x > 0 ? '∞' : x < 0 ? '-∞' : '—';
    var s = Math.abs(x).toFixed(decimals);
    var p = s.split('.');
    return (x < 0 ? '-' : '') + U.groupDigits(p[0]) + (p[1] ? '.' + p[1] : '');
  };
  U.fmtCompact = function (x) {
    if (!isFinite(x)) return '—';
    var a = Math.abs(x);
    if (a >= 1e12) return (x / 1e12).toFixed(a >= 1e13 ? 0 : 1) + 'T';
    if (a >= 1e9) return (x / 1e9).toFixed(a >= 1e10 ? 0 : 1) + 'B';
    if (a >= 1e6) return (x / 1e6).toFixed(a >= 1e7 ? 0 : 1) + 'M';
    if (a >= 1e4) return (x / 1e3).toFixed(0) + 'K';
    return U.fmtInt(x);
  };
  // 01:02:03 (hours keep counting past 24)
  U.fmtClock = function (ms) {
    if (!isFinite(ms) || ms < 0) ms = 0;
    var t = Math.floor(ms / 1000), h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), s = t % 60;
    return (h < 10 ? '0' : '') + h + ':' + (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
  };
  // "39h 12m", "11d 11h", "4m 05s"
  U.fmtSpan = function (ms, opts) {
    opts = opts || {};
    if (!isFinite(ms)) return '—';
    if (ms < 0) ms = 0;
    var s = Math.round(ms / 1000);
    var d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
    if (opts.hoursOnly) { var hh = Math.floor(s / 3600); return hh + 'h ' + (m < 10 ? '0' : '') + m + 'm'; }
    if (d >= 1) return d + 'd ' + h + 'h';
    if (h >= 1) return h + 'h ' + (m < 10 ? '0' : '') + m + 'm';
    if (m >= 1) return m + 'm ' + (sec < 10 ? '0' : '') + sec + 's';
    return sec + 's';
  };
  U.fmtDate = function (t) {
    var d = new Date(t);
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' }) + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  };
  U.fmtTime = function (t) { return new Date(t).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); };

  // ---------- timing helpers ----------
  U.debounce = function (fn, ms) {
    var t = null;
    var d = function () { var a = arguments, self = this; clearTimeout(t); t = setTimeout(function () { fn.apply(self, a); }, ms); };
    d.cancel = function () { clearTimeout(t); };
    return d;
  };
  U.clamp = function (v, a, b) { return v < a ? a : v > b ? b : v; };
  U.lerp = function (a, b, t) { return a + (b - a) * t; };
  U.ease = {
    outCubic: function (t) { return 1 - Math.pow(1 - t, 3); },
    inCubic: function (t) { return t * t * t; },
    inOutCubic: function (t) { return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2; },
    outBack: function (t) { var c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.pow(t - 1, 3) + c1 * Math.pow(t - 1, 2); },
    outQuint: function (t) { return 1 - Math.pow(1 - t, 5); },
    outElastic: function (t) { if (t === 0 || t === 1) return t; return Math.pow(2, -10 * t) * Math.sin((t * 10 - 0.75) * (2 * Math.PI / 3)) + 1; }
  };
  U.wait = function (ms) { return new Promise(function (r) { setTimeout(r, ms); }); };
  U.reducedMotion = function () {
    var pref = TK.settings && TK.settings.get ? TK.settings.get('reducedMotion') : 'system';
    if (pref === 'on') return true;
    if (pref === 'off') return false;
    return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  };

  // ---------- events ----------
  U.bus = (function () {
    var map = {};
    return {
      on: function (ev, fn) { (map[ev] = map[ev] || []).push(fn); return function () { map[ev] = (map[ev] || []).filter(function (f) { return f !== fn; }); }; },
      emit: function (ev, data) { (map[ev] || []).slice().forEach(function (fn) { try { fn(data); } catch (e) { console.error('[bus]', ev, e); } }); }
    };
  })();

  // ---------- accessibility ----------
  U.announce = function (msg, assertive) {
    var el = document.getElementById(assertive ? 'sr-alert' : 'sr-status');
    if (!el) return;
    el.textContent = '';
    setTimeout(function () { el.textContent = msg; }, 30);
  };

  // ---------- files ----------
  U.download = function (filename, text, mime) {
    try {
      var blob = new Blob([text], { type: mime || 'text/plain' });
      var a = document.createElement('a');
      a.href = URL.createObjectURL(blob); a.download = filename;
      document.body.appendChild(a); a.click();
      setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
      return true;
    } catch (e) { return false; }
  };
  U.csv = function (rows) {
    return rows.map(function (r) { return r.map(function (c) { var s = String(c == null ? '' : c); return /[",\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; }).join(','); }).join('\n');
  };

  // Is the event target a place where typing/space has its own meaning?
  U.isTypingTarget = function (el) {
    if (!el || el === document.body) return false;
    if (el.isContentEditable) return true;
    var tag = el.tagName;
    if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
    if (tag === 'INPUT') { var t = (el.type || 'text').toLowerCase(); return ['button', 'submit', 'reset', 'checkbox', 'radio', 'range', 'color', 'file', 'image'].indexOf(t) < 0 || t === 'range'; }
    return false;
  };
  U.isInteractive = function (el) {
    if (!el || el === document.body) return false;
    return !!el.closest('button, a[href], input, select, textarea, [role="button"], [role="switch"], [role="tab"], [role="slider"], [contenteditable="true"], summary, [tabindex]:not([tabindex="-1"])');
  };

  TK.util = U;
})(window.TK = window.TK || {});
