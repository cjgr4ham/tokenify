/* TOKENIFY — procedural sound design (Web Audio API). Every sound is synthesised at runtime;
 * nothing plays until the user has interacted with the page, and everything respects mute/volume. */
(function (TK) {
  'use strict';
  var ctx = null, master = null, reverb = null, noiseBuf = null, failed = false;
  var enabled = TK.settings.get('sound') !== false;
  var volume = TK.settings.get('volume');

  function impulse(seconds, decay) {
    var rate = ctx.sampleRate, len = Math.floor(rate * seconds), buf = ctx.createBuffer(2, len, rate);
    for (var c = 0; c < 2; c++) { var d = buf.getChannelData(c); for (var i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, decay); }
    return buf;
  }
  function ensure() {
    if (failed) return null;
    if (!ctx) {
      var AC = window.AudioContext || window.webkitAudioContext;
      if (!AC) { failed = true; return null; }
      try { ctx = new AC(); } catch (e) { failed = true; return null; }
      var comp = ctx.createDynamicsCompressor();
      comp.threshold.value = -16; comp.knee.value = 8; comp.ratio.value = 5; comp.attack.value = 0.003; comp.release.value = 0.22;
      master = ctx.createGain(); master.gain.value = enabled ? volume : 0;
      master.connect(comp); comp.connect(ctx.destination);
      noiseBuf = ctx.createBuffer(1, ctx.sampleRate * 2, ctx.sampleRate);
      var nd = noiseBuf.getChannelData(0); for (var i = 0; i < nd.length; i++) nd[i] = Math.random() * 2 - 1;
      reverb = ctx.createConvolver(); reverb.buffer = impulse(2.2, 2.6);
      var rg = ctx.createGain(); rg.gain.value = 0.28; reverb.connect(rg); rg.connect(master);
    }
    if (ctx.state === 'suspended') { try { ctx.resume(); } catch (e) { /* ignore */ } }
    return ctx;
  }
  // unlock on the first real interaction (browser autoplay policies)
  function unlock() { ensure(); window.removeEventListener('pointerdown', unlock, true); window.removeEventListener('keydown', unlock, true); }
  window.addEventListener('pointerdown', unlock, true);
  window.addEventListener('keydown', unlock, true);

  function env(g, t0, a, peak, hold, rel) {
    g.gain.setValueAtTime(0.0001, t0);
    g.gain.exponentialRampToValueAtTime(Math.max(0.0002, peak), t0 + a);
    if (hold) g.gain.setValueAtTime(Math.max(0.0002, peak), t0 + a + hold);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + a + hold + rel);
  }
  function tone(o) {
    var t0 = ctx.currentTime + (o.at || 0), a = o.a || 0.005, hold = o.hold || 0, rel = o.dur || 0.3;
    var osc = ctx.createOscillator(); osc.type = o.type || 'sine';
    osc.frequency.setValueAtTime(o.f, t0);
    if (o.f2) osc.frequency.exponentialRampToValueAtTime(o.f2, t0 + a + hold + rel * (o.glide || 1));
    if (o.detune) osc.detune.value = o.detune;
    var g = ctx.createGain(); env(g, t0, a, o.gain || 0.2, hold, rel);
    var node = osc;
    if (o.filter) { var f = ctx.createBiquadFilter(); f.type = o.filter; f.frequency.setValueAtTime(o.ff || 1200, t0); if (o.ff2) f.frequency.exponentialRampToValueAtTime(o.ff2, t0 + a + hold + rel); f.Q.value = o.q || 0.8; osc.connect(f); node = f; }
    node.connect(g); g.connect(master); if (o.rev) g.connect(reverb);
    osc.start(t0); osc.stop(t0 + a + hold + rel + 0.05);
  }
  function noise(o) {
    var t0 = ctx.currentTime + (o.at || 0), a = o.a || 0.003, hold = o.hold || 0, rel = o.dur || 0.2;
    var s = ctx.createBufferSource(); s.buffer = noiseBuf;
    var f = ctx.createBiquadFilter(); f.type = o.filter || 'bandpass';
    f.frequency.setValueAtTime(o.ff || 2000, t0); if (o.ff2) f.frequency.exponentialRampToValueAtTime(o.ff2, t0 + a + hold + rel); f.Q.value = o.q || 1;
    var g = ctx.createGain(); env(g, t0, a, o.gain || 0.2, hold, rel);
    s.connect(f); f.connect(g); g.connect(master); if (o.rev) g.connect(reverb);
    s.start(t0, Math.random() * 1.4); s.stop(t0 + a + hold + rel + 0.05);
  }
  function bell(f, at, gain, dur) {
    [[1, 1], [2.76, 0.45], [5.4, 0.22], [8.93, 0.1]].forEach(function (p) { tone({ f: f * p[0], at: at, gain: gain * p[1], dur: dur / p[0] * 1.4, a: 0.002, rev: true }); });
  }
  var NOTE = function (n) { return 440 * Math.pow(2, (n - 69) / 12); }; // MIDI -> Hz

  var S = {
    click: function () { noise({ ff: 2600, q: 2, gain: 0.25, dur: 0.035 }); tone({ f: 150, f2: 70, gain: 0.35, dur: 0.11, type: 'sine' }); },
    rattle: function (o) {
      o = o || {}; var dur = o.dur || 0.4, n = Math.round(dur * (o.rate || 22));
      for (var i = 0; i < n; i++) {
        var at = (i / n) * dur + Math.random() * 0.015, g = (o.gain || 0.08) * (o.crescendo ? (0.35 + 0.65 * i / n) : 1);
        noise({ at: at, ff: 3200 + Math.random() * 3000, q: 6, gain: g, dur: 0.03 });
        tone({ at: at, f: 1600 + Math.random() * 1400, type: 'triangle', gain: g * 0.5, dur: 0.04 });
      }
    },
    creak: function () {
      tone({ f: 190, f2: 120, type: 'sawtooth', filter: 'bandpass', ff: 900, q: 7, gain: 0.08, dur: 0.28, a: 0.02 });
      noise({ ff: 500, ff2: 2400, q: 0.9, gain: 0.16, dur: 0.24, a: 0.03 });
    },
    coin: function (o) {
      o = o || {}; var base = o.f || 1975;
      [0, 0.07].forEach(function (at, i) {
        var f = base * (i ? 1.19 : 1) * (0.98 + Math.random() * 0.04);
        tone({ at: at, f: f, gain: 0.16, dur: 0.38, a: 0.001, rev: true });
        tone({ at: at, f: f * 1.5, gain: 0.08, dur: 0.22, a: 0.001 });
        tone({ at: at, f: f * 2.67, gain: 0.04, dur: 0.1, a: 0.001 });
      });
    },
    sparkle: function (o) {
      o = o || {}; var notes = [84, 88, 91, 93, 96, 100, 103], n = o.n || 5;
      for (var i = 0; i < n; i++) tone({ at: (o.at || 0) + i * (o.gap || 0.045) + Math.random() * 0.02, f: NOTE(notes[Math.floor(Math.random() * notes.length)]), gain: (o.gain || 0.07), dur: 0.22, a: 0.002, rev: true });
    },
    whoosh: function (o) { o = o || {}; noise({ ff: o.from || 300, ff2: o.to || 3200, q: 0.7, gain: o.gain || 0.2, a: o.a || 0.08, dur: o.dur || 0.35 }); },
    gizmo: function () {
      [72, 76, 79, 84].forEach(function (n, i) { tone({ at: i * 0.055, f: NOTE(n), type: 'square', filter: 'lowpass', ff: 2600, gain: 0.07, dur: 0.12 }); });
      S.sparkle({ at: 0.18, n: 4 });
    },
    rare: function () { bell(NOTE(84), 0, 0.12, 1.4); bell(NOTE(88), 0.09, 0.1, 1.4); bell(NOTE(91), 0.18, 0.1, 1.6); S.sparkle({ at: 0.25, n: 5 }); },
    epic: function () {
      S.whoosh({ from: 200, to: 4000, gain: 0.18, dur: 0.4 });
      [60, 64, 67, 71].forEach(function (n) { tone({ f: NOTE(n), type: 'sawtooth', filter: 'lowpass', ff: 600, ff2: 3000, gain: 0.05, a: 0.04, hold: 0.25, dur: 0.9, rev: true }); });
      bell(NOTE(91), 0.12, 0.1, 1.6); bell(NOTE(95), 0.22, 0.09, 1.8); S.sparkle({ at: 0.3, n: 7 });
    },
    build: function (o) { // rising tension
      o = o || {}; var d = o.dur || 1.4;
      noise({ ff: 180, ff2: 3600, q: 1.2, gain: 0.16, a: d * 0.85, dur: 0.12 });
      tone({ f: 90, f2: 360, type: 'sawtooth', filter: 'lowpass', ff: 300, ff2: 1600, gain: 0.07, a: d * 0.9, dur: 0.1 });
      S.rattle({ dur: d, rate: 26, gain: 0.07, crescendo: true });
    },
    impact: function (o) {
      o = o || {}; var g = o.gain || 1;
      tone({ f: 120, f2: 36, gain: 0.55 * g, dur: 0.8, a: 0.002 });
      noise({ filter: 'lowpass', ff: 900, ff2: 120, gain: 0.45 * g, dur: 0.45 });
      noise({ filter: 'highpass', ff: 5200, gain: 0.12 * g, dur: 1.4, rev: true });
    },
    chime: function () {
      [84, 88, 91, 95, 96, 100].forEach(function (n, i) { bell(NOTE(n), i * 0.085, 0.085, 2); });
      S.sparkle({ at: 0.5, n: 9, gap: 0.06 });
    },
    heartbeat: function () { tone({ f: 62, f2: 45, gain: 0.5, dur: 0.16 }); tone({ at: 0.2, f: 58, f2: 42, gain: 0.38, dur: 0.18 }); },
    fanfare: function () {
      var chords = [[60, 64, 67, 72], [65, 69, 72, 77], [67, 71, 74, 79], [72, 76, 79, 84, 88]], lens = [0.32, 0.32, 0.32, 1.6];
      var at = 0;
      chords.forEach(function (c, i) {
        c.forEach(function (n) {
          tone({ at: at, f: NOTE(n), type: 'sawtooth', filter: 'lowpass', ff: 700, ff2: 3600, q: 1.2, gain: 0.05, a: 0.03, hold: lens[i] * 0.6, dur: lens[i] * 0.9, rev: true });
          tone({ at: at, f: NOTE(n) * 1.003, type: 'square', filter: 'lowpass', ff: 1800, gain: 0.018, a: 0.03, hold: lens[i] * 0.5, dur: lens[i] });
        });
        noise({ at: at, filter: 'lowpass', ff: 260, gain: 0.3, dur: 0.25 });
        at += lens[i];
      });
      bell(NOTE(96), at - 1.4, 0.08, 2.4); S.sparkle({ at: at - 1.3, n: 12, gap: 0.07 });
    },
    tick: function () { tone({ f: 1350, gain: 0.07, dur: 0.045, a: 0.001 }); noise({ ff: 4200, q: 3, gain: 0.04, dur: 0.02 }); },
    tickDown: function () { tone({ f: 640, gain: 0.07, dur: 0.06, a: 0.001 }); },
    success: function () { [76, 81, 88].forEach(function (n, i) { tone({ at: i * 0.07, f: NOTE(n), gain: 0.08, dur: 0.25, rev: true }); }); },
    ui: function () { tone({ f: 980, gain: 0.04, dur: 0.05, a: 0.001 }); }
  };

  TK.audio = {
    play: function (name, opts) {
      if (!enabled || failed) return;
      if (!ensure()) return;
      var fn = S[name]; if (!fn) return;
      try { fn(opts); } catch (e) { /* audio must never break the app */ }
    },
    setEnabled: function (on) { enabled = !!on; if (master) master.gain.setTargetAtTime(enabled ? volume : 0, ctx.currentTime, 0.02); },
    setVolume: function (v) { volume = Math.max(0, Math.min(1, v)); if (master && enabled) master.gain.setTargetAtTime(volume, ctx.currentTime, 0.02); },
    isEnabled: function () { return enabled; },
    available: function () { return !failed && !!(window.AudioContext || window.webkitAudioContext); }
  };
})(window.TK = window.TK || {});
