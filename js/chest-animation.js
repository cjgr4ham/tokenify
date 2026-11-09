/* TOKENIFY — chest animation & reveal choreography.
 * Uses the real sprite-sheet frames (open / close / shake / turn) extracted into assets/chest,
 * layered with Web Animations for squash, stretch and shake, a particle canvas, light flashes
 * and the sprite sheet's own effect sprites (beam, burst, vortex).
 * The outcome is always decided BEFORE any animation starts; skipping only changes presentation.
 */
(function (TK) {
  'use strict';
  var U = TK.util, cfg = TK.config, h = U.h;
  var ORDER = function (tier) { return cfg.TIERS[tier].order; };

  // ---------- skippable sequence clock ----------
  function Seq() { this.skipped = false; this.cancelled = false; this.timers = []; }
  Seq.prototype.wait = function (ms) {
    var self = this;
    if (this.skipped || this.cancelled || ms <= 0) return Promise.resolve();
    return new Promise(function (res) { var id = setTimeout(function () { self.timers = self.timers.filter(function (t) { return t.id !== id; }); res(); }, ms); self.timers.push({ id: id, res: res }); });
  };
  Seq.prototype.skip = function () { this.skipped = true; this.timers.forEach(function (t) { clearTimeout(t.id); t.res(); }); this.timers = []; };
  Seq.prototype.cancel = function () { this.cancelled = true; this.skip(); };

  // ---------- timing ----------
  var BASE = {
    common:    { press: 85, shake: 150, frame: 40, hold: 0, amp: 2 },
    uncommon:  { press: 95, shake: 430, frame: 46, hold: 0, amp: 3 },
    rare:      { press: 100, shake: 640, frame: 48, hold: 90, amp: 3.6 },
    epic:      { press: 110, shake: 960, frame: 44, hold: 150, amp: 4.6 },
    legendary: { press: 120, shake: 1500, frame: 30, hold: 190, amp: 5.6 },
    mythic:    { press: 140, shake: 2400, frame: 24, hold: 520, amp: 7 }
  };
  function timing(tier, multi) {
    var speed = cfg.SPEED[TK.settings.get('speed')] || 1, rm = U.reducedMotion();
    var k = speed;
    if (ORDER(tier) >= 4) k = Math.max(speed, 0.62);
    if (rm) k *= 0.7;
    var b = BASE[tier], t = {};
    Object.keys(b).forEach(function (key) { t[key] = key === 'amp' ? b[key] : Math.round(b[key] * k); });
    if (multi) { t.shake = Math.round(Math.min(t.shake, 380) * k); t.frame = Math.round(32 * k); t.hold = 0; }
    t.k = k; t.rm = rm;
    return t;
  }
  function tierColor(o) { return o.tier === 'uncommon' && o.accent ? o.accent : cfg.TIERS[o.tier].color; }

  function shakeFrames(dur, amp) {
    var frames = [], steps = Math.max(6, Math.round(dur / 42));
    for (var i = 0; i <= steps; i++) {
      var p = i / steps, a = amp * (0.3 + 0.7 * p);
      var r = (i % 2 ? 1 : -1) * a * (0.55 + Math.random() * 0.45), x = (Math.random() - 0.5) * a * 1.6, y = -Math.random() * a * 0.6;
      frames.push({ transform: 'translate(' + x.toFixed(2) + 'px,' + y.toFixed(2) + 'px) rotate(' + r.toFixed(2) + 'deg)', offset: p });
    }
    frames[frames.length - 1].transform = 'none';
    return frames;
  }

  // ---------- Chest controller ----------
  function Chest(el) {
    this.el = el; // { stage, wrap, button, frames, aura, canvas, reveal, flash, rays, dust, skip }
    this.fx = new TK.FX(el.canvas);
    this.imgs = {}; this.current = null; this.seq = null; this.busy = false; this.idleTimer = 0; this.closeTimer = 0; this.cine = null;
    this.onSkipState = null;
  }
  Chest.prototype.load = function (onProgress) {
    var names = [], self = this;
    ['open', 'close', 'turn'].forEach(function (s) { for (var i = 0; i < 8; i++) names.push(s + '-0' + i); });
    for (var j = 0; j < 3; j++) names.push('shake-0' + j);
    var done = 0, first = null;
    // idle frame first so the page can appear quickly; the rest stream in behind it
    names.sort(function (a, b) { return (a === 'open-00' ? -1 : 0) - (b === 'open-00' ? -1 : 0); });
    this.allReady = Promise.all(names.map(function (n) {
      var p = new Promise(function (res) {
        var im = new Image(); im.alt = ''; im.draggable = false; im.decoding = 'async';
        im.onload = im.onerror = function () { done++; if (onProgress) onProgress(done / names.length); res(); };
        im.src = 'assets/chest/' + n + '.webp';
        self.imgs[n] = im; self.el.frames.appendChild(im);
      });
      if (n === 'open-00') first = p;
      return p;
    }));
    return first.then(function () { self.show('open-00'); self.toIdle(); });
  };
  Chest.prototype.show = function (name) {
    if (this.current === name) return;
    var n = this.imgs[name]; if (!n) return;
    if (this.current && this.imgs[this.current]) this.imgs[this.current].classList.remove('on');
    n.classList.add('on'); this.current = name;
  };
  Chest.prototype.setAura = function (color, strength) {
    var a = this.el.aura;
    a.style.setProperty('--aura', color ? color.replace(')', ',0.55)').replace('rgb(', 'rgba(') : 'rgba(190,120,255,0.35)');
    if (color && color[0] === '#') a.style.setProperty('--aura', hexA(color, 0.6));
    a.style.opacity = strength == null ? '' : String(strength);
    a.style.transform = strength > 0.9 ? 'translate(-50%,-50%) scale(1.25)' : '';
  };
  function hexA(hex, a) { var n = parseInt(hex.slice(1), 16); return 'rgba(' + (n >> 16 & 255) + ',' + (n >> 8 & 255) + ',' + (n & 255) + ',' + a + ')'; }

  Chest.prototype.toIdle = function () {
    var self = this;
    this.busy = false;
    this.el.button.classList.add('idle');
    this.el.stage.removeAttribute('data-glow');
    this.setAura(null, null);
    clearTimeout(this.idleTimer);
    var hop = function () {
      if (self.busy || U.reducedMotion() || document.hidden) { self.idleTimer = setTimeout(hop, 8000); return; }
      self.el.wrap.classList.remove('hop'); void self.el.wrap.offsetWidth; self.el.wrap.classList.add('hop');
      setTimeout(function () { self.el.wrap.classList.remove('hop'); }, 950);
      self.idleTimer = setTimeout(hop, 9000 + Math.random() * 7000);
    };
    this.idleTimer = setTimeout(hop, 7000 + Math.random() * 5000);
  };
  Chest.prototype.leaveIdle = function () { this.el.button.classList.remove('idle'); this.el.wrap.classList.remove('hop'); clearTimeout(this.idleTimer); };

  Chest.prototype.flash = function (ms, alpha) {
    if (U.reducedMotion()) return;
    var f = this.el.flash;
    f.style.setProperty('--flash-ms', ms + 'ms'); f.style.opacity = '';
    f.style.filter = alpha < 1 ? 'opacity(' + alpha + ')' : '';
    f.classList.remove('go'); void f.offsetWidth; f.classList.add('go');
  };
  Chest.prototype.shakeStage = function (amp, ms) {
    if (U.reducedMotion()) return;
    var kf = shakeFrames(ms, amp).map(function (f) { return { transform: f.transform.replace(/rotate\([^)]*\)/, ''), offset: f.offset }; });
    [this.el.wrap, this.el.rays].forEach(function (el) { if (el) el.animate(kf, { duration: ms, easing: 'linear' }); });
  };
  Chest.prototype.mouth = function () { return this.fx.pointOf(this.el.frames, 0.5, 0.6); };

  // effect sprite from the sprite sheet (beam / burst / vortex), animated with WAAPI
  Chest.prototype.sprite = function (name, opts) {
    if (U.reducedMotion() && !opts.allowReduced) return null;
    var img = h('img', { class: 'fx-sprite', src: 'assets/chest/fx-' + name + '.webp', alt: '' });
    var m = this.mouth(), w = opts.w || 200, hgt = opts.h || 200;
    Object.assign(img.style, { position: 'absolute', left: (m.x - w / 2) + 'px', top: (m.y - hgt * (opts.anchorY != null ? opts.anchorY : 0.5)) + 'px', width: w + 'px', height: hgt + 'px', zIndex: opts.z || 3, pointerEvents: 'none', mixBlendMode: 'screen', transformOrigin: opts.origin || '50% 50%' });
    this.el.stage.appendChild(img);
    var anim = img.animate(opts.frames, { duration: opts.ms || 800, easing: opts.easing || 'cubic-bezier(.16,1,.3,1)', fill: 'forwards' });
    anim.onfinish = function () { img.remove(); };
    return img;
  };

  Chest.prototype.quickClose = async function (seq) {
    this.clearReveal(true);
    if (!this.current || this.current === 'open-00') return;
    if (U.reducedMotion() || this.current.indexOf('open-0') !== 0 && this.current.indexOf('close-0') !== 0) { this.show('open-00'); return; }
    var names = ['close-02', 'close-04', 'close-06', 'close-07'];
    for (var i = 0; i < names.length; i++) { this.show(names[i]); await seq.wait(26); if (seq.cancelled) return; }
    this.show('open-00');
  };
  Chest.prototype.clearReveal = function (instant) {
    clearTimeout(this.closeTimer);
    var r = this.el.reveal;
    U.$$('.rv', r).forEach(function (c) { if (instant) c.remove(); else { c.classList.add('leaving'); setTimeout(function () { c.remove(); }, 260); } });
    if (this.cine) { this.cine.remove(); this.cine = null; }
    U.$$('.fx-sprite', this.el.stage).forEach(function (s) { s.remove(); });
    this.el.stage.classList.remove('dimmed');
    document.body.classList.remove('jackpot-dark');
  };
  Chest.prototype.scheduleClose = function (ms) {
    var self = this;
    clearTimeout(this.closeTimer);
    this.closeTimer = setTimeout(async function () {
      if (self.busy) return;
      U.$$('.rv', self.el.reveal).forEach(function (c) { c.classList.add('leaving'); setTimeout(function () { c.remove(); }, 260); });
      self.el.stage.classList.remove('dimmed');
      var s = new Seq();
      if (!U.reducedMotion() && self.current && self.current.indexOf('open') === 0) {
        var names = ['close-01', 'close-02', 'close-03', 'close-04', 'close-05', 'close-06', 'close-07'];
        for (var i = 0; i < names.length; i++) { if (self.busy) return; self.show(names[i]); await s.wait(55); }
        self.el.button.animate([{ transform: 'scale(1.05,.94)' }, { transform: 'scale(.98,1.02)' }, { transform: 'none' }], { duration: 320, easing: 'ease-out' });
      }
      if (self.busy) return;
      self.show('open-00'); self.toIdle();
    }, ms);
  };

  // ---------- phases ----------
  Chest.prototype.press = function (T) {
    TK.audio.play('click');
    if (T.rm) return;
    this.el.button.animate([{ transform: 'none' }, { transform: 'scale(1.07,.9)', offset: 0.55 }, { transform: 'scale(.98,1.03)' }], { duration: T.press * 1.8, easing: 'cubic-bezier(.3,.7,.4,1)' });
  };
  Chest.prototype.anticipate = async function (seq, o, T) {
    var tier = o.tier, ord = ORDER(tier), color = tierColor(o), self = this;
    if (ord >= 1) this.setAura(color, ord >= 4 ? 1 : 0.7 + ord * 0.06);
    if (ord >= 2) this.el.stage.setAttribute('data-glow', '');
    if (T.shake <= 0) return;
    var anim = T.rm ? null : this.el.button.animate(shakeFrames(T.shake, T.amp), { duration: T.shake, easing: 'linear' });
    if (ord >= 4) TK.audio.play('build', { dur: T.shake / 1000 });
    else TK.audio.play('rattle', { dur: Math.max(0.12, T.shake / 1000), gain: 0.05 + ord * 0.012, crescendo: ord >= 2 });
    var cycle = ['shake-00', 'shake-01', 'shake-02', 'shake-01'], t = 0, i = 0, nextBeat = 0, nextImplode = 0;
    if (tier === 'mythic') { this.el.stage.classList.add('dimmed'); document.body.classList.add('jackpot-dark'); }
    while (t < T.shake && !seq.skipped) {
      if (!T.rm) this.show(cycle[i++ % 4]);
      if (ord >= 4 && t >= nextImplode) { var m = this.mouth(); this.fx.implode({ x: m.x, y: m.y - 10, radius: 230, count: 18, type: 'spark', colors: ['#ffe9a3', '#ffc94a', '#fff6d6'], life: [0.45, 0.8], size: [2, 4] }); nextImplode = t + 260; }
      if (tier === 'mythic' && t >= nextBeat) { TK.audio.play('heartbeat'); this.setAura(color, 0.75 + 0.25 * (t / T.shake)); nextBeat = t + Math.max(380, 760 - 420 * (t / T.shake)); }
      var step = Math.max(40, Math.round(88 - 44 * (t / T.shake)));
      await seq.wait(step); t += step;
    }
    if (anim) anim.cancel();
    this.show('open-00');
    if (T.hold && !seq.skipped) {
      if (ord >= 4) { // the moment of stillness before the burst
        var m2 = this.mouth(); this.fx.burst({ x: m2.x, y: m2.y - 6, count: 3, type: 'glow', colors: ['#fff6d6'], speed: [0, 5], gravity: 0, life: [T.hold / 1000, T.hold / 1000 + 0.2], size: [10, 26], endScale: 2.2 });
      }
      await seq.wait(T.hold);
    }
    void self;
  };
  Chest.prototype.impact = function (o, multi) {
    var tier = o.tier, ord = ORDER(tier), color = tierColor(o), m = this.mouth(), fx = this.fx;
    var up = [-125, -55];
    if (ord === 0) {
      fx.burst({ x: m.x, y: m.y, count: 16, type: 'coin', speed: [260, 520], angle: up, gravity: 1100, life: [0.75, 1.15], size: [8, 12], spin: 9, shrink: false });
      fx.burst({ x: m.x, y: m.y, count: 10, type: 'glow', colors: ['#ffe6a0', '#ffd26a'], speed: [60, 200], gravity: -40, life: [0.4, 0.8], size: [8, 18] });
      this.flash(320, 0.35);
      TK.audio.play('coin');
    } else if (ord === 1) {
      fx.burst({ x: m.x, y: m.y, count: 26, type: 'spark', colors: [color, '#ffffff'], speed: [240, 560], angle: [-160, -20], gravity: 500, life: [0.5, 0.9], size: [3, 6] });
      fx.burst({ x: m.x, y: m.y, count: 10, type: 'star', colors: [color], speed: [80, 260], angle: up, gravity: 100, life: [0.7, 1.2], size: [8, 15] });
      fx.burst({ x: m.x, y: m.y, count: 6, type: 'coin', speed: [220, 420], angle: up, gravity: 1000, life: [0.7, 1], size: [7, 10], shrink: false });
      this.flash(380, 0.55);
      TK.audio.play('gizmo');
    } else if (ord <= 3) {
      fx.burst({ x: m.x, y: m.y, count: ord === 3 ? 44 : 30, type: 'star', colors: [color, '#ffffff', '#ffe9a3'], speed: [120, 460], angle: [-170, -10], gravity: 160, life: [0.8, 1.5], size: [8, 16] });
      fx.burst({ x: m.x, y: m.y, count: ord === 3 ? 34 : 22, type: 'spark', colors: [color, '#fff'], speed: [300, 700], gravity: 400, life: [0.5, 1], size: [3, 6] });
      if (ord === 3) fx.burst({ x: m.x, y: m.y, count: 12, type: 'coin', speed: [260, 520], angle: up, gravity: 1000, life: [0.8, 1.2], size: [8, 12], shrink: false });
      this.flash(ord === 3 ? 620 : 480, ord === 3 ? 0.9 : 0.7);
      if (ord === 3) this.shakeStage(5, 300);
      this.sprite('burst', { w: 420, h: 290, anchorY: 0.62, z: 1, ms: 900, frames: [{ opacity: 0, transform: 'scale(.4)' }, { opacity: 1, transform: 'scale(1.05)', offset: 0.35 }, { opacity: 0, transform: 'scale(1.35)' }] });
      TK.audio.play(ord === 3 ? 'epic' : 'rare');
    } else {
      var big = tier === 'mythic' ? 1.6 : 1;
      fx.burst({ x: m.x, y: m.y, count: 46 * big, type: 'coin', speed: [320, 760], angle: [-150, -30], gravity: 1100, life: [0.9, 1.6], size: [8, 13], spin: 10, shrink: false });
      fx.burst({ x: m.x, y: m.y, count: 60 * big, type: 'spark', colors: ['#fff6d6', '#ffd45a', '#ffb93f'], speed: [380, 900], gravity: 350, life: [0.6, 1.2], size: [3, 7] });
      fx.burst({ x: m.x, y: m.y, count: 34 * big, type: 'star', colors: ['#ffe58a', '#ffffff'], speed: [150, 520], gravity: 140, life: [1, 1.8], size: [9, 18] });
      if (tier === 'mythic') fx.burst({ x: m.x, y: m.y, count: 50, type: 'confetti', colors: ['#ffe58a', '#ffd045', '#fff3c4', '#e9a42c'], speed: [300, 700], angle: [-150, -30], gravity: 420, life: [1.6, 2.6], size: [8, 13], spin: 12, shrink: false });
      this.flash(tier === 'mythic' ? 1100 : 760, 1);
      this.shakeStage(tier === 'mythic' ? 11 : 7, tier === 'mythic' ? 650 : 420);
      this.sprite('beam', { w: 150, h: 520, anchorY: 0.96, origin: '50% 100%', z: 3, ms: 1300, frames: [{ opacity: 0, transform: 'scaleY(.1)' }, { opacity: 1, transform: 'scaleY(1.15)', offset: 0.25 }, { opacity: 0.9, transform: 'scaleY(1)', offset: 0.6 }, { opacity: 0, transform: 'scaleY(1.05)' }] });
      this.sprite('burst', { w: 520, h: 360, anchorY: 0.6, z: 1, ms: 1100, frames: [{ opacity: 0, transform: 'scale(.3)' }, { opacity: 1, transform: 'scale(1.1)', offset: 0.3 }, { opacity: 0, transform: 'scale(1.5)' }] });
      TK.audio.play('impact', { gain: tier === 'mythic' ? 1.25 : 1 });
    }
    if (multi) return;
  };
  Chest.prototype.openLid = async function (seq, o, T, multi) {
    TK.audio.play('creak');
    var names = ['open-01', 'open-02', 'open-03', 'open-04', 'open-05', 'open-06', 'open-07'];
    for (var i = 0; i < names.length; i++) {
      this.show(names[i]);
      if (i === 4) this.impact(o, multi);
      await seq.wait(T.frame);
      if (seq.skipped) return;
    }
    if (!T.rm) this.el.button.animate([{ transform: 'scale(1,1)' }, { transform: 'scale(.965,1.06)', offset: 0.35 }, { transform: 'scale(1.015,.99)', offset: 0.7 }, { transform: 'none' }], { duration: 420, easing: 'ease-out' });
  };

  // ---------- reveal ----------
  Chest.prototype.card = function (res, instant) {
    var o = res.outcome, card = TK.chestUI.revealCard(res);
    if (instant) card.classList.add('instant');
    this.el.reveal.appendChild(card);
    return card;
  };
  Chest.prototype.revealStandard = async function (seq, res, T) {
    var o = res.outcome, ord = ORDER(o.tier);
    if (ord >= 2) this.el.stage.classList.add('dimmed');
    this.card(res, false);
    if (ord >= 1) { var m = this.mouth(); this.fx.burst({ x: m.x, y: m.y - 120, count: 8 + ord * 4, type: 'star', colors: [tierColor(o), '#fff'], speed: [40, 160], gravity: -20, life: [0.8, 1.6], size: [5, 10] }); }
    await seq.wait(Math.round(320 * T.k));
  };

  function cineDom(kind, res) {
    var o = res.outcome, isOutfit = kind === 'outfit';
    var item = h('div', { class: 'cine-item' }, [TK.ui.img(o, { alt: o.name })]);
    var odds = isOutfit ? '1 in 1,562,500 per chest · ' + TK.chestUI.pct(o) : '1 in 137,500 per chest · ' + TK.chestUI.pct(o);
    var collect = h('button', { class: 'btn-gold', type: 'button' }, ['Collect']);
    var banner = h('div', { class: 'cine-banner' }, [
      h('span', { class: 'cine-kicker', text: isOutfit ? 'Mythic · Weekly Gold Outfit' : 'Legendary · Pirate Shop Access' }),
      h('span', { class: 'cine-title', text: isOutfit ? 'Ultimate jackpot — Gold Outfit!' : 'Pirate Token found!' }),
      h('span', { class: 'cine-odds num', text: odds + ' · chest #' + U.fmtInt(res.no) }),
      h('div', { class: 'cine-actions' }, [collect])
    ]);
    var root = h('div', { class: 'cine cine-' + kind + (isOutfit ? ' cine-global' : ''), role: 'dialog', 'aria-label': isOutfit ? 'Ultimate jackpot: Gold Outfit' : 'Pirate Token found' }, [
      h('div', { class: 'cine-backdrop' }),
      h('div', { class: 'cine-rays' }, [h('div', { class: 'cine-rays-spin' })]),
      isOutfit ? h('canvas', { class: 'cine-fx' }) : null,
      h('div', { class: 'cine-core' }, [
        h('div', { class: 'cine-halo' }),
        isOutfit ? h('img', { class: 'cine-vortex', src: 'assets/chest/fx-vortex.webp', alt: '' }) : null,
        h('div', { class: 'cine-ring r2' }), h('div', { class: 'cine-ring r1' }), h('div', { class: 'cine-ring r3' }),
        item
      ]),
      banner
    ]);
    return { root: root, collect: collect, item: item };
  }
  Chest.prototype.cinematic = async function (seq, res, T, kind) {
    var self = this, dom = cineDom(kind, res), root = dom.root;
    (kind === 'outfit' ? document.body : this.el.stage).appendChild(root);
    this.cine = root;
    var gfx = kind === 'outfit' ? new TK.FX(root.querySelector('.cine-fx')) : this.fx;
    if (kind === 'outfit') gfx.forceAdditive = true; // dark cinematic backdrop in both themes
    var done = new Promise(function (resolve) {
      dom.collect.addEventListener('click', function () { resolve('collect'); });
      root.addEventListener('keydown', function (e) { if (e.key === 'Escape' || e.key === 'Enter') { e.preventDefault(); resolve('collect'); } });
    });
    var k = T.k;
    root.classList.add('go');
    if (kind === 'outfit') TK.audio.play('impact', { gain: 0.8 });
    await seq.wait(Math.round(260 * k));
    root.classList.add('item-in');
    await seq.wait(Math.round(900 * k));
    var c = gfx.pointOf(dom.item, 0.5, 0.5), r = dom.item.getBoundingClientRect().width;
    gfx.orbit({ x: c.x, y: c.y, radius: r * 0.62, speed: 1.4, count: kind === 'outfit' ? 38 : 26, colors: ['#ffe58a', '#fff6d6', '#ffc94a'], life: [3, 5], size: [4, 9], tilt: 0.36 });
    root.classList.add('rings');
    if (kind === 'outfit') { gfx.burst({ x: c.x, y: c.y, count: 70, type: 'star', colors: ['#fff6d6', '#ffe58a'], speed: [200, 700], gravity: 60, life: [1, 2], size: [8, 18] }); }
    await seq.wait(Math.round((kind === 'outfit' ? 1100 : 700) * k));
    root.classList.add('banner-in');
    TK.audio.play(kind === 'outfit' ? 'fanfare' : 'chime');
    gfx.rain({ count: kind === 'outfit' ? 70 : 34, colors: ['#ffe58a', '#fff6d6'], stagger: 2.5 });
    if (seq.skipped) this.finalCine(root);
    setTimeout(function () { try { dom.collect.focus({ preventScroll: true }); } catch (e) { /* ignore */ } }, 500);
    var how = await done;
    root.style.transition = 'opacity .35s ease'; root.style.opacity = '0';
    setTimeout(function () { root.remove(); }, 360);
    if (this.cine === root) this.cine = null;
    if (kind === 'outfit') gfx.clear();
    document.body.classList.remove('jackpot-dark');
    this.el.stage.classList.remove('dimmed');
    return how;
  };
  Chest.prototype.finalCine = function (root) { root.classList.add('go', 'item-in', 'rings', 'banner-in', 'final'); };

  // ---------- public: single opening ----------
  Chest.prototype.play = async function (res) {
    if (this.seq) this.seq.cancel();
    var seq = this.seq = new Seq(), o = res.outcome, T = timing(o.tier), self = this;
    this.busy = true; this.leaveIdle(); clearTimeout(this.closeTimer);
    this.fx.clear();
    if (this.allReady) await this.allReady;
    await this.quickClose(seq);
    if (seq.cancelled) return;
    var ord = ORDER(o.tier);
    this.setSkippable(ord >= 2);
    this.press(T);
    await seq.wait(T.press);
    if (!seq.skipped) await this.anticipate(seq, o, T);
    if (!seq.skipped) await this.openLid(seq, o, T);
    if (seq.cancelled) return;
    if (seq.skipped) { this.show('open-07'); this.el.button.getAnimations().forEach(function (a) { a.cancel(); }); this.fx.clear(); }
    TK.chestUI.onRevealed(res);
    if (ord >= 4) {
      this.setSkippable(true);
      var kind = o.tier === 'mythic' ? 'outfit' : 'token';
      if (seq.skipped) { // build the cinematic straight into its final state
        var seq2 = new Seq(); seq2.skip();
        this.setSkippable(false);
        await this.cinematic(seq2, res, T, kind);
      } else {
        var wrap = this.cinematic(seq, res, T, kind);
        await wrap;
      }
      this.setSkippable(false);
      if (seq.cancelled) return;
      this.card(res, true);
      this.busy = false;
      this.scheduleClose(9000);
      return;
    }
    if (seq.skipped) this.card(res, true); else await this.revealStandard(seq, res, T);
    this.setSkippable(false);
    if (seq.cancelled) return;
    this.busy = false;
    this.scheduleClose(ord === 0 ? 5200 : 7500);
  };
  // ---------- public: multi opening (10 / 100). Shows the chest burst; the grid is drawn by the page. ----------
  Chest.prototype.playMulti = async function (results) {
    if (this.seq) this.seq.cancel();
    var seq = this.seq = new Seq();
    var best = results.reduce(function (a, r) { return ORDER(r.outcome.tier) > ORDER(a.outcome.tier) ? r : a; }, results[0]);
    var T = timing(best.outcome.tier, true);
    this.busy = true; this.leaveIdle(); clearTimeout(this.closeTimer); this.fx.clear();
    if (this.allReady) await this.allReady;
    await this.quickClose(seq);
    if (seq.cancelled) return;
    this.setSkippable(true);
    this.press(T); await seq.wait(T.press);
    var tierForGlow = ORDER(best.outcome.tier) >= 4 ? { tier: 'epic' } : best.outcome; // never spoil a jackpot with gold light
    if (!seq.skipped) await this.anticipate(seq, Object.assign({}, tierForGlow, { tier: ORDER(best.outcome.tier) >= 4 ? 'epic' : best.outcome.tier }), T);
    if (!seq.skipped) await this.openLid(seq, ORDER(best.outcome.tier) >= 4 ? { tier: 'epic' } : best.outcome, T, true);
    this.setSkippable(false);
    if (seq.cancelled) return;
    this.show('open-07');
    this.busy = false;
    return seq.skipped;
  };
  Chest.prototype.cinematicFor = async function (res) {
    var T = timing(res.outcome.tier);
    this.busy = true;
    var how = await this.cinematic(new Seq(), res, T, res.outcome.tier === 'mythic' ? 'outfit' : 'token');
    this.busy = false;
    return how;
  };
  Chest.prototype.skip = function () { if (this.seq) this.seq.skip(); };
  Chest.prototype.setSkippable = function (on) { if (this.el.skip) this.el.skip.hidden = !on; };
  Chest.prototype.afterMulti = function () { this.scheduleClose(600); };

  TK.Chest = Chest;
  TK.Seq = Seq;
})(window.TK = window.TK || {});
