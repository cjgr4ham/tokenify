/* TOKENIFY — lightweight canvas particle system.
 * Sprites are pre-rendered once; particles are pooled; the animation loop only runs while
 * particles are alive. Quality setting scales particle counts. */
(function (TK) {
  'use strict';
  var QUALITY = { high: 1, medium: 0.55, low: 0.25 };
  var CAP = { high: 900, medium: 500, low: 220 };

  function FX(canvas) {
    this.canvas = canvas; this.ctx = canvas.getContext('2d');
    this.parts = []; this.pool = []; this.raf = 0; this.last = 0; this.w = 0; this.h = 0; this.dpr = 1;
    this.quality = TK.settings.get('quality') || 'high';
    this.glowCache = {}; this.starCache = {};
    this.coin = this._makeCoin();
    var self = this;
    this.resize();
    if (window.ResizeObserver) new ResizeObserver(function () { self.resize(); }).observe(canvas);
    else window.addEventListener('resize', function () { self.resize(); });
    TK.util.bus.on('settings', function (e) { if (e.key === 'quality' || e.key === '*') self.quality = TK.settings.get('quality'); });
  }
  FX.prototype.resize = function () {
    var r = this.canvas.getBoundingClientRect();
    this.dpr = Math.min(2, window.devicePixelRatio || 1);
    this.w = r.width; this.h = r.height;
    this.canvas.width = Math.max(1, Math.round(r.width * this.dpr)); this.canvas.height = Math.max(1, Math.round(r.height * this.dpr));
  };
  FX.prototype._makeCoin = function () {
    var s = 64, c = document.createElement('canvas'); c.width = c.height = s; var g = c.getContext('2d');
    var gr = g.createRadialGradient(s * 0.38, s * 0.34, 2, s / 2, s / 2, s / 2);
    gr.addColorStop(0, '#fff6c8'); gr.addColorStop(0.45, '#ffd45a'); gr.addColorStop(0.85, '#d18a15'); gr.addColorStop(1, '#8a5208');
    g.fillStyle = gr; g.beginPath(); g.arc(s / 2, s / 2, s / 2 - 1, 0, Math.PI * 2); g.fill();
    g.strokeStyle = 'rgba(120,70,5,.8)'; g.lineWidth = 3; g.beginPath(); g.arc(s / 2, s / 2, s / 2 - 9, 0, Math.PI * 2); g.stroke();
    g.fillStyle = 'rgba(255,255,255,.7)'; g.beginPath(); g.ellipse(s * 0.36, s * 0.3, 7, 4, -0.6, 0, Math.PI * 2); g.fill();
    return c;
  };
  FX.prototype._glow = function (color) {
    if (this.glowCache[color]) return this.glowCache[color];
    var s = 64, c = document.createElement('canvas'); c.width = c.height = s; var g = c.getContext('2d');
    var gr = g.createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s / 2);
    gr.addColorStop(0, 'rgba(255,255,255,1)'); gr.addColorStop(0.18, color); gr.addColorStop(1, 'rgba(0,0,0,0)');
    g.fillStyle = gr; g.fillRect(0, 0, s, s);
    return (this.glowCache[color] = c);
  };
  FX.prototype._star = function (color) {
    if (this.starCache[color]) return this.starCache[color];
    var s = 64, c = document.createElement('canvas'); c.width = c.height = s; var g = c.getContext('2d');
    var gr = g.createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s / 2);
    gr.addColorStop(0, 'rgba(255,255,255,0.9)'); gr.addColorStop(0.3, color); gr.addColorStop(1, 'rgba(0,0,0,0)');
    g.fillStyle = gr; g.globalAlpha = 0.5; g.beginPath(); g.arc(s / 2, s / 2, s / 2, 0, Math.PI * 2); g.fill(); g.globalAlpha = 1;
    g.fillStyle = '#fffbe8'; g.beginPath();
    var cx = s / 2, cy = s / 2, R = s / 2 - 2, r = 4;
    for (var i = 0; i < 8; i++) { var a = i * Math.PI / 4 - Math.PI / 2, rad = i % 2 ? r : (i % 4 === 0 ? R : R * 0.62); g.lineTo(cx + Math.cos(a) * rad, cy + Math.sin(a) * rad); }
    g.closePath(); g.fill();
    return (this.starCache[color] = c);
  };
  FX.prototype._spawn = function () { return this.pool.pop() || {}; };
  FX.prototype._add = function (p) {
    if (this.parts.length >= CAP[this.quality]) return;
    this.parts.push(p);
    if (!this.raf) { var self = this; this.last = performance.now(); this.raf = requestAnimationFrame(function (t) { self._tick(t); }); }
  };
  function rnd(a, b) { return a + Math.random() * (b - a); }
  function pick(arr) { return arr[Math.floor(Math.random() * arr.length)]; }
  FX.prototype.n = function (count) { return Math.max(1, Math.round(count * QUALITY[this.quality])); };

  // burst({ x, y, count, type, colors, speed:[a,b], angle:[a,b] (deg, 0 = right, -90 = up), spread, gravity, life:[a,b], size:[a,b], drag, spin })
  FX.prototype.burst = function (o) {
    var n = this.n(o.count || 20);
    for (var i = 0; i < n; i++) {
      var p = this._spawn();
      var ang = (o.angle ? rnd(o.angle[0], o.angle[1]) : rnd(0, 360)) * Math.PI / 180, sp = rnd(o.speed ? o.speed[0] : 100, o.speed ? o.speed[1] : 400);
      p.type = o.type || 'glow'; p.color = o.colors ? pick(o.colors) : '#ffd45a';
      p.x = o.x + rnd(-(o.jitter || 0), o.jitter || 0); p.y = o.y + rnd(-(o.jitter || 0), o.jitter || 0);
      p.vx = Math.cos(ang) * sp; p.vy = Math.sin(ang) * sp; p.g = o.gravity != null ? o.gravity : 600; p.drag = o.drag != null ? o.drag : 0.9;
      p.life = rnd(o.life ? o.life[0] : 0.6, o.life ? o.life[1] : 1.2); p.age = 0;
      p.size = rnd(o.size ? o.size[0] : 6, o.size ? o.size[1] : 14); p.size2 = o.shrink === false ? p.size : p.size * (o.endScale != null ? o.endScale : 0.3);
      p.rot = rnd(0, Math.PI * 2); p.vr = rnd(-1, 1) * (o.spin != null ? o.spin : 6); p.orbit = null; p.fadeIn = o.fadeIn || 0.05;
      this._add(p);
    }
  };
  // particles converge on a point (anticipation / suction)
  FX.prototype.implode = function (o) {
    var n = this.n(o.count || 30);
    for (var i = 0; i < n; i++) {
      var p = this._spawn(), a = rnd(0, Math.PI * 2), r = rnd(o.radius * 0.6, o.radius), life = rnd(o.life ? o.life[0] : 0.5, o.life ? o.life[1] : 0.9);
      p.type = o.type || 'spark'; p.color = o.colors ? pick(o.colors) : '#ffd45a';
      p.x = o.x + Math.cos(a) * r; p.y = o.y + Math.sin(a) * r * 0.75;
      p.vx = (o.x - p.x) / life; p.vy = (o.y - p.y) / life; p.g = 0; p.drag = 1;
      p.life = life; p.age = -rnd(0, o.stagger || 0); p.size = rnd(o.size ? o.size[0] : 2, o.size ? o.size[1] : 5); p.size2 = p.size * 0.6;
      p.rot = 0; p.vr = 0; p.orbit = null; p.fadeIn = 0.25;
      this._add(p);
    }
  };
  // particles orbiting a centre on a tilted ellipse
  FX.prototype.orbit = function (o) {
    var n = this.n(o.count || 24);
    for (var i = 0; i < n; i++) {
      var p = this._spawn();
      p.type = o.type || 'star'; p.color = o.colors ? pick(o.colors) : '#ffe27a';
      p.orbit = { cx: o.x, cy: o.y, r: rnd(o.radius * 0.8, o.radius * 1.15), a: rnd(0, Math.PI * 2), w: rnd(o.speed * 0.7, o.speed * 1.3) * (Math.random() < 0.5 ? 1 : 1), tilt: o.tilt || 0.38 };
      p.x = o.x; p.y = o.y; p.vx = p.vy = 0; p.g = 0; p.drag = 1;
      p.life = rnd(o.life ? o.life[0] : 2.5, o.life ? o.life[1] : 4); p.age = -rnd(0, o.stagger || 0.6);
      p.size = rnd(o.size ? o.size[0] : 5, o.size ? o.size[1] : 11); p.size2 = p.size * 0.5; p.rot = rnd(0, 6); p.vr = rnd(-2, 2); p.fadeIn = 0.15;
      this._add(p);
    }
  };
  // gentle falling sparkles from the top edge
  FX.prototype.rain = function (o) {
    var n = this.n(o.count || 40);
    for (var i = 0; i < n; i++) {
      var p = this._spawn();
      p.type = o.type || 'star'; p.color = o.colors ? pick(o.colors) : '#ffe27a';
      p.x = rnd(0, this.w); p.y = rnd(-40, -5); p.vx = rnd(-20, 20); p.vy = rnd(40, 110); p.g = 20; p.drag = 1;
      p.life = rnd(2.5, 4.5); p.age = -rnd(0, o.stagger || 2); p.size = rnd(3, 8); p.size2 = p.size * 0.6; p.rot = rnd(0, 6); p.vr = rnd(-3, 3); p.orbit = null; p.fadeIn = 0.1;
      this._add(p);
    }
  };
  FX.prototype.clear = function () { while (this.parts.length) this.pool.push(this.parts.pop()); this.ctx.setTransform(1, 0, 0, 1, 0, 0); this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height); };
  FX.prototype.active = function () { return this.parts.length > 0; };
  FX.prototype._tick = function (t) {
    var dt = Math.min(0.05, (t - this.last) / 1000); this.last = t;
    var ctx = this.ctx, parts = this.parts, dpr = this.dpr;
    // additive glow washes out on the bright light-theme stage; use normal blending there
    var glowOp = document.documentElement.getAttribute('data-theme') === 'light' && !this.forceAdditive ? 'source-over' : 'lighter';
    ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    for (var i = parts.length - 1; i >= 0; i--) {
      var p = parts[i];
      p.age += dt;
      if (p.age >= p.life) { this.pool.push(parts[i]); parts[i] = parts[parts.length - 1]; parts.pop(); continue; }
      if (p.age < 0) continue;
      if (p.orbit) {
        var ob = p.orbit; ob.a += ob.w * dt;
        p.x = ob.cx + Math.cos(ob.a) * ob.r; p.y = ob.cy + Math.sin(ob.a) * ob.r * ob.tilt;
      } else {
        var dr = Math.pow(p.drag, dt * 6);
        p.vx *= dr; p.vy = p.vy * dr + p.g * dt;
        p.x += p.vx * dt; p.y += p.vy * dt;
      }
      p.rot += p.vr * dt;
      var k = p.age / p.life, a = Math.min(1, p.age / p.fadeIn) * (k > 0.7 ? (1 - k) / 0.3 : 1);
      var s = p.size + (p.size2 - p.size) * k;
      if (p.type === 'coin') {
        ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = Math.min(1, a * 1.2);
        ctx.save(); ctx.translate(p.x, p.y); ctx.rotate(p.rot * 0.3); ctx.scale(Math.max(0.12, Math.abs(Math.cos(p.rot))), 1);
        ctx.drawImage(this.coin, -s, -s, s * 2, s * 2); ctx.restore();
      } else if (p.type === 'spark') {
        ctx.globalCompositeOperation = glowOp; ctx.globalAlpha = a;
        var len = Math.min(40, Math.hypot(p.vx, p.vy) * 0.045 + 2);
        var ang = Math.atan2(p.vy, p.vx);
        ctx.strokeStyle = p.color; ctx.lineWidth = s * 0.5; ctx.lineCap = 'round';
        ctx.beginPath(); ctx.moveTo(p.x, p.y); ctx.lineTo(p.x - Math.cos(ang) * len, p.y - Math.sin(ang) * len); ctx.stroke();
        ctx.drawImage(this._glow(p.color), p.x - s, p.y - s, s * 2, s * 2);
      } else if (p.type === 'star') {
        ctx.globalCompositeOperation = glowOp; ctx.globalAlpha = a;
        ctx.save(); ctx.translate(p.x, p.y); ctx.rotate(p.rot); ctx.drawImage(this._star(p.color), -s, -s, s * 2, s * 2); ctx.restore();
      } else if (p.type === 'confetti') {
        ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = a;
        ctx.save(); ctx.translate(p.x, p.y); ctx.rotate(p.rot); ctx.scale(1, Math.abs(Math.sin(p.rot * 1.7)) + 0.15);
        ctx.fillStyle = p.color; ctx.fillRect(-s / 2, -s / 4, s, s / 2); ctx.restore();
      } else { // glow / ember
        ctx.globalCompositeOperation = glowOp; ctx.globalAlpha = a;
        ctx.drawImage(this._glow(p.color), p.x - s, p.y - s, s * 2, s * 2);
      }
    }
    ctx.globalAlpha = 1; ctx.globalCompositeOperation = 'source-over';
    if (parts.length) { var self = this; this.raf = requestAnimationFrame(function (tt) { self._tick(tt); }); }
    else { this.raf = 0; ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, this.canvas.width, this.canvas.height); }
  };
  // Map a point given as fractions of an element's box into this canvas's coordinate space.
  FX.prototype.pointOf = function (el, fx, fy) {
    var a = el.getBoundingClientRect(), b = this.canvas.getBoundingClientRect();
    return { x: a.left - b.left + a.width * fx, y: a.top - b.top + a.height * fy };
  };
  TK.FX = FX;
})(window.TK = window.TK || {});
