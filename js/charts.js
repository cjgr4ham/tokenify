/* TOKENIFY — canvas charts (distribution bars, Monte Carlo histogram, CPG trend).
 * Charts only draw the numbers they are given; all probabilities come from TK.prob. */
(function (TK) {
  'use strict';
  var U = TK.util;
  // Palette comes from CSS custom properties so charts follow the dark / light theme.
  var C = {};
  function readPalette() {
    var cs = getComputedStyle(document.documentElement);
    function v(name, d) { var x = cs.getPropertyValue(name).trim(); return x || d; }
    C.grid = v('--chart-grid', 'rgba(160,180,220,.1)'); C.axis = v('--chart-axis', 'rgba(200,210,230,.55)'); C.text = v('--chart-text', '#9aa4bb');
    C.bar = [v('--chart-bar-1', '#ffe9a3'), v('--chart-bar-2', '#f2b53a'), v('--chart-bar-3', '#b97510')];
    C.barDim = v('--chart-bar-dim', 'rgba(242,181,58,.32)'); C.hi = v('--chart-hi', '#fff3c4'); C.iv = v('--chart-band', 'rgba(255,214,120,.12)');
    C.mean = v('--chart-mean', '#ffd36b'); C.obs = v('--chart-obs', '#4fe0c2'); C.dot = v('--chart-dot', '#8fd3ff'); C.line = v('--chart-line', '#4fe0c2');
    C.ink = v('--chart-label-ink', '#081018'); C.ring = v('--chart-dot-ring', '#0b1222');
  }
  readPalette();
  var charts = [];
  U.bus.on('theme', function () { readPalette(); charts.forEach(function (c) { if (c.shown) { c.shown = rethemeMarkers(c.shown); c.data = rethemeMarkers(c.data); c.draw(); } }); });
  // markers store colours by role so they can be re-coloured when the theme changes
  function rethemeMarkers(d) { if (!d || !d.markers) return d; d.markers.forEach(function (m) { if (m.role) m.color = C[m.role]; }); return d; }
  function niceStep(range, target) {
    var raw = range / Math.max(1, target), mag = Math.pow(10, Math.floor(Math.log10(raw))), f = raw / mag;
    return (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) * mag;
  }
  function fmtPct(v) {
    var p = v * 100;
    if (p === 0) return '0%';
    if (p >= 10) return p.toFixed(0) + '%';
    if (p >= 1) return p.toFixed(1) + '%';
    if (p >= 0.01) return p.toFixed(2) + '%';
    return TK.fmt.sci(p, 2) + '%';
  }

  function Chart(canvas, opts) {
    this.canvas = canvas; this.ctx = canvas.getContext('2d'); this.opts = opts || {};
    this.data = null; this.shown = null; this.hover = -1; this.cursor = -1; this.anim = 0; this.w = 0; this.h = 0; this.dpr = 1;
    this.tip = this.opts.tip || null;
    var self = this;
    charts.push(this);
    if (window.ResizeObserver) new ResizeObserver(function () { self.resize(); }).observe(canvas.parentElement);
    window.addEventListener('resize', function () { self.resize(); });
    canvas.addEventListener('mousemove', function (e) { var r = canvas.getBoundingClientRect(); self.onHover(e.clientX - r.left, e.clientY - r.top, e); });
    canvas.addEventListener('mouseleave', function () { self.hover = -1; self.hideTip(); self.draw(); });
    canvas.addEventListener('click', function () { if (self.hover >= 0 && self.opts.onPick) self.opts.onPick(self.data.bars[self.hover]); });
    if (this.opts.keyboard) {
      canvas.tabIndex = 0;
      canvas.addEventListener('keydown', function (e) {
        if (!self.data || !self.data.bars.length) return;
        var n = self.data.bars.length;
        if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') { e.preventDefault(); self.cursor = U.clamp((self.cursor < 0 ? self.peak() : self.cursor) + (e.key === 'ArrowRight' ? 1 : -1), 0, n - 1); self.hover = self.cursor; self.showTipFor(self.cursor); self.draw(); }
        else if ((e.key === 'Enter' || e.key === ' ') && self.cursor >= 0 && self.opts.onPick) { e.preventDefault(); self.opts.onPick(self.data.bars[self.cursor]); }
      });
      canvas.addEventListener('blur', function () { self.cursor = -1; self.hover = -1; self.hideTip(); self.draw(); });
    }
    this.resize();
  }
  Chart.prototype.peak = function () { var b = this.data.bars, best = 0; for (var i = 1; i < b.length; i++) if (b[i].v > b[best].v) best = i; return best; };
  Chart.prototype.resize = function () {
    var r = this.canvas.parentElement.getBoundingClientRect();
    this.dpr = Math.min(2, window.devicePixelRatio || 1);
    this.w = Math.max(200, r.width); this.h = Math.max(160, this.opts.height || r.height || 300);
    this.canvas.style.width = this.w + 'px'; this.canvas.style.height = this.h + 'px';
    this.canvas.width = Math.round(this.w * this.dpr); this.canvas.height = Math.round(this.h * this.dpr);
    this.draw();
  };
  // data: { bars:[{x0,x1,v,label,tip}], dots:[{x,v}], markers:[{x,label,color,dash,top}], band:{from,to}, highlight:{from,to}, log, xTitle, yTitle, obsBar }
  Chart.prototype.set = function (data, animate) {
    var prev = this.shown;
    this.data = data;
    var canTween = animate !== false && prev && prev.bars.length === data.bars.length && !U.reducedMotion() && !data.log && !prev.log;
    var self = this;
    cancelAnimationFrame(this.anim);
    if (!canTween) { this.shown = data; this.draw(); return; }
    var from = prev.bars.map(function (b) { return b.v; }), start = performance.now(), dur = 320;
    var step = function (t) {
      var k = Math.min(1, (t - start) / dur), e = 1 - Math.pow(1 - k, 3);
      self.shown = Object.assign({}, data, { bars: data.bars.map(function (b, i) { return Object.assign({}, b, { v: from[i] + (b.v - from[i]) * e }); }) });
      self.draw();
      if (k < 1) self.anim = requestAnimationFrame(step); else { self.shown = data; self.draw(); }
    };
    this.anim = requestAnimationFrame(step);
  };
  Chart.prototype.layout = function () {
    var compact = this.w < 420;
    return { l: compact ? 44 : 56, r: 14, t: this.opts.top != null ? this.opts.top : 30, b: this.opts.xTitle === false ? 26 : 40 };
  };
  Chart.prototype.scales = function () {
    var d = this.shown, L = this.layout(), W = this.w - L.l - L.r, H = this.h - L.t - L.b;
    var x0 = d.bars.length ? d.bars[0].x0 - 0.5 : 0, x1 = d.bars.length ? d.bars[d.bars.length - 1].x1 + 0.5 : 1;
    if (d.xMin != null) x0 = Math.min(x0, d.xMin - 0.5); if (d.xMax != null) x1 = Math.max(x1, d.xMax + 0.5);
    var vmax = 0, vminPos = Infinity;
    d.bars.forEach(function (b) { if (b.v > vmax) vmax = b.v; if (b.v > 0 && b.v < vminPos) vminPos = b.v; });
    (d.dots || []).forEach(function (p) { if (p.v > vmax) vmax = p.v; if (p.v > 0 && p.v < vminPos) vminPos = p.v; });
    if (vmax <= 0) vmax = 1;
    var sx = function (x) { return L.l + (x - x0) / (x1 - x0) * W; };
    var sy, ticks = [];
    if (d.log) {
      var top = Math.ceil(Math.log10(vmax)), bot = Math.max(top - (d.decades || 12), Math.floor(Math.log10(Math.max(vminPos, 1e-300))));
      if (bot >= top) bot = top - 1;
      sy = function (v) { if (v <= 0) return L.t + H; var lv = Math.log10(v); return L.t + H - Math.max(0, (lv - bot) / (top - bot)) * H; };
      var stepD = Math.max(1, Math.ceil((top - bot) / 5));
      for (var e = bot; e <= top; e += stepD) ticks.push({ v: Math.pow(10, e), label: e >= -2 ? fmtPct(Math.pow(10, e)) : '10' + TK.fmt.sup(e + 2) + '%' });
    } else {
      var ymax = vmax * 1.12, st = niceStep(ymax, 4);
      sy = function (v) { return L.t + H - v / ymax * H; };
      for (var v = 0; v <= ymax + 1e-15; v += st) ticks.push({ v: v, label: d.yCount ? U.fmtCompact(v) : fmtPct(v) });
    }
    return { L: L, W: W, H: H, x0: x0, x1: x1, sx: sx, sy: sy, ticks: ticks };
  };
  Chart.prototype.draw = function () {
    var ctx = this.ctx, d = this.shown, dpr = this.dpr;
    ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    if (!d) return;
    if (!d.bars.length) { ctx.fillStyle = C.text; ctx.font = '600 14px Barlow, sans-serif'; ctx.textAlign = 'center'; ctx.fillText(d.empty || 'No data', this.w / 2, this.h / 2); return; }
    var s = this.scales(), L = s.L, self = this;
    // grid + y labels
    ctx.font = '600 11px "Barlow Condensed", sans-serif'; ctx.textAlign = 'right'; ctx.textBaseline = 'middle';
    s.ticks.forEach(function (t) { var y = Math.round(s.sy(t.v)) + 0.5; ctx.strokeStyle = C.grid; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(L.l, y); ctx.lineTo(self.w - L.r, y); ctx.stroke(); ctx.fillStyle = C.text; ctx.fillText(t.label, L.l - 8, y); });
    // band (central interval)
    if (d.band) { var bx0 = s.sx(d.band.from - 0.5), bx1 = s.sx(d.band.to + 0.5); ctx.fillStyle = C.iv; ctx.fillRect(bx0, L.t, bx1 - bx0, s.H); }
    // bars
    var n = d.bars.length, gap = n > 60 ? 0 : n > 30 ? 1 : 2;
    var grad = ctx.createLinearGradient(0, L.t, 0, L.t + s.H); grad.addColorStop(0, C.bar[0]); grad.addColorStop(0.45, C.bar[1]); grad.addColorStop(1, C.bar[2]);
    d.bars.forEach(function (b, i) {
      var x0 = s.sx(b.x0 - 0.5) + gap / 2, x1 = s.sx(b.x1 + 0.5) - gap / 2, y = s.sy(b.v), base = L.t + s.H;
      var inHi = d.highlight && b.x1 >= d.highlight.from && b.x0 <= d.highlight.to;
      var isObs = d.obsBar != null && d.obsBar >= b.x0 && d.obsBar <= b.x1;
      ctx.fillStyle = isObs ? C.obs : (d.highlight ? (inHi ? grad : C.barDim) : grad);
      if (i === self.hover) ctx.fillStyle = isObs ? '#9ff5e3' : C.hi;
      var hgt = Math.max(b.v > 0 ? 1 : 0, base - y);
      ctx.fillRect(x0, base - hgt, Math.max(1, x1 - x0), hgt);
    });
    // dots (theoretical overlay)
    (d.dots || []).forEach(function (p) { var x = s.sx(p.x), y = s.sy(p.v); ctx.fillStyle = C.dot; ctx.strokeStyle = C.ring; ctx.lineWidth = 1.5; ctx.beginPath(); ctx.arc(x, y, 3.5, 0, Math.PI * 2); ctx.fill(); ctx.stroke(); });
    if (d.line) { ctx.strokeStyle = C.line; ctx.lineWidth = 2.2; ctx.beginPath(); d.line.forEach(function (p, i) { var x = s.sx(p.x), y = s.sy(p.v); if (i) ctx.lineTo(x, y); else ctx.moveTo(x, y); }); ctx.stroke(); }
    // x axis + labels
    ctx.strokeStyle = C.axis; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(L.l, L.t + s.H + 0.5); ctx.lineTo(this.w - L.r, L.t + s.H + 0.5); ctx.stroke();
    var span = s.x1 - s.x0, xt = niceStep(span, Math.max(3, Math.floor(s.W / 70))); if (xt < 1) xt = 1;
    ctx.fillStyle = C.text; ctx.textAlign = 'center'; ctx.textBaseline = 'top';
    for (var x = Math.ceil(s.x0 / xt) * xt; x <= s.x1; x += xt) { var px = s.sx(x); if (px < L.l - 1 || px > this.w - L.r + 1) continue; ctx.fillText(U.fmtCompact(x), px, L.t + s.H + 6); ctx.strokeStyle = C.axis; ctx.beginPath(); ctx.moveTo(px + 0.5, L.t + s.H); ctx.lineTo(px + 0.5, L.t + s.H + 4); ctx.stroke(); }
    if (this.opts.xTitle !== false && d.xTitle) { ctx.fillStyle = '#6f7a93'; ctx.font = '700 11px "Barlow Condensed", sans-serif'; ctx.fillText(d.xTitle.toUpperCase(), L.l + s.W / 2, this.h - 14); }
    // markers
    var labelsDrawn = [];
    (d.markers || []).forEach(function (m) {
      if (m.x == null || !isFinite(m.x)) return;
      var px = s.sx(m.x); if (px < L.l - 2 || px > self.w - L.r + 2) return;
      ctx.strokeStyle = m.color; ctx.lineWidth = m.width || 2; ctx.setLineDash(m.dash ? [5, 4] : []);
      ctx.beginPath(); ctx.moveTo(px + 0.5, L.t - 4); ctx.lineTo(px + 0.5, L.t + s.H); ctx.stroke(); ctx.setLineDash([]);
      if (m.label) {
        ctx.font = '800 11px "Barlow Condensed", sans-serif';
        var tw = ctx.measureText(m.label).width + 12, lx = U.clamp(px - tw / 2, L.l, self.w - L.r - tw), ly = L.t - 24;
        labelsDrawn.forEach(function (o) { if (Math.abs(o.x - lx) < (tw + o.w) / 2 + 2 && o.y === ly) ly -= 0; });
        if (labelsDrawn.some(function (o) { return lx < o.x + o.w && lx + tw > o.x && o.y === ly; })) ly += s.H * 0 + 18;
        labelsDrawn.push({ x: lx, y: ly, w: tw });
        ctx.fillStyle = m.color; roundRect(ctx, lx, ly, tw, 17, 5); ctx.fill();
        ctx.fillStyle = C.ink; ctx.textAlign = 'left'; ctx.textBaseline = 'middle'; ctx.fillText(m.label, lx + 6, ly + 9);
      }
    });
  };
  function roundRect(ctx, x, y, w, h, r) { ctx.beginPath(); ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + h, r); ctx.arcTo(x + w, y + h, x, y + h, r); ctx.arcTo(x, y + h, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath(); }
  Chart.prototype.onHover = function (mx, my) {
    if (!this.shown || !this.shown.bars.length) return;
    var s = this.scales(), idx = -1, bars = this.shown.bars;
    for (var i = 0; i < bars.length; i++) { var x0 = s.sx(bars[i].x0 - 0.5), x1 = s.sx(bars[i].x1 + 0.5); if (mx >= x0 && mx < x1) { idx = i; break; } }
    if (idx !== this.hover) { this.hover = idx; this.draw(); }
    if (idx >= 0) this.showTipFor(idx, mx, my); else this.hideTip();
  };
  Chart.prototype.showTipFor = function (idx, mx, my) {
    if (!this.tip || !this.data) return;
    var b = this.data.bars[idx]; if (!b) return;
    var text = this.opts.tipText ? this.opts.tipText(b) : (b.label + ': ' + fmtPct(b.v));
    this.tip.textContent = text; this.tip.hidden = false;
    var s = this.scales(); if (mx == null) { mx = s.sx((b.x0 + b.x1) / 2); my = s.sy(b.v); }
    var tw = this.tip.offsetWidth, th = this.tip.offsetHeight;
    this.tip.style.left = U.clamp(mx - tw / 2, 4, this.w - tw - 4) + 'px';
    this.tip.style.top = Math.max(4, my - th - 14) + 'px';
  };
  Chart.prototype.hideTip = function () { if (this.tip) this.tip.hidden = true; };

  TK.Chart = Chart;
  TK.chartColors = C;
  TK.chartFmtPct = fmtPct;
})(window.TK = window.TK || {});
