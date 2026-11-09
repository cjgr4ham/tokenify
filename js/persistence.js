/* TOKENIFY — local persistence: settings, interactive (Open Chests) records, laboratory records.
 * Interactive and laboratory data are stored under separate keys and are never mixed.
 * Large batches are stored as aggregates; only the most recent openings keep individual entries.
 */
(function (TK) {
  'use strict';
  var U = TK.util, S = U.storage;

  // ---------------- settings ----------------
  var DEFAULTS = {
    theme: 'dark', sound: true, volume: 0.6, speed: 'normal', quality: 'high', reducedMotion: 'system',
    precision: 'standard', probFormat: 'percent',
    labN: '1,000,000', labMode: 'fast', openCount: 1,
    rangeC: '95', revQ: '50', revK: '1', aimK: '3', ivA: '3', ivB: '8', mcN: '100,000', mcE: '1,000',
    explorer: 'pirate-token', distTails: false, distLog: false, lastView: 'open', introSeen: false
  };
  var settings = Object.assign({}, DEFAULTS, S.get('settings', {}));
  TK.settings = {
    get: function (k) { return k in settings ? settings[k] : DEFAULTS[k]; },
    set: function (k, v) {
      if (settings[k] === v) return;
      settings[k] = v; S.set('settings', settings);
      U.bus.emit('settings', { key: k, value: v });
    },
    all: function () { return Object.assign({}, settings); },
    reset: function () { settings = Object.assign({}, DEFAULTS); S.set('settings', settings); U.bus.emit('settings', { key: '*' }); }
  };

  // ---------------- interactive openings ----------------
  var HIST_MAX = 250, JACKPOT_MAX = 300;
  function blankInteractive() { return { v: 1, total: 0, counts: {}, first: {}, last: {}, hist: [], jackpots: [], created: Date.now() }; }
  var inter = S.get('interactive', null);
  if (!inter || inter.v !== 1) inter = blankInteractive();

  TK.store = TK.store || {};
  function owns(id) { return (inter.counts[id] || 0) > 0; }
  TK.store.interactive = {
    data: function () { return inter; },
    owns: owns,
    ownedMap: function () { var m = {}; TK.rewards.outcomes.forEach(function (o) { if (o.unique && owns(o.id)) m[o.id] = true; }); return m; },
    // Draw n chests from the exact table, apply the one-of-each rule in draw order, record, and
    // return [{ outcome (received), rolled (drawn), converted, no }].
    drawAndRecord: function (n) {
      var now = Date.now(), res = [], unique = TK.config.RULES.uniqueCosmetics;
      inter.converted = inter.converted || {};
      for (var i = 0; i < n; i++) {
        var rolled = TK.rewards.draw(), got = rolled, converted = false;
        if (unique && rolled.unique && owns(rolled.id)) { got = TK.rewards.ordinary; converted = true; inter.converted[rolled.id] = (inter.converted[rolled.id] || 0) + 1; }
        inter.total += 1;
        var no = inter.total;
        inter.counts[got.id] = (inter.counts[got.id] || 0) + 1;
        if (!inter.first[got.id]) inter.first[got.id] = no;
        inter.last[got.id] = no;
        inter.hist.push(converted ? [no, got.index, now, rolled.index] : [no, got.index, now]);
        if (TK.config.TIERS[got.tier].order >= 3) inter.jackpots.push({ no: no, id: got.id, t: now });
        res.push({ outcome: got, rolled: rolled, converted: converted, no: no });
      }
      if (inter.hist.length > HIST_MAX) inter.hist.splice(0, inter.hist.length - HIST_MAX);
      if (inter.jackpots.length > JACKPOT_MAX) inter.jackpots.splice(0, inter.jackpots.length - JACKPOT_MAX);
      S.set('interactive', inter);
      U.bus.emit('interactive-changed', { results: res });
      return res;
    },
    count: function (id) { return inter.counts[id] || 0; },
    convertedTotal: function () { var c = inter.converted || {}; return Object.keys(c).reduce(function (a, k) { return a + c[k]; }, 0); },
    sinceLast: function (id) { return inter.last[id] ? inter.total - inter.last[id] : null; },
    reset: function () { inter = blankInteractive(); S.set('interactive', inter); U.bus.emit('interactive-changed', { reset: true }); }
  };

  // ---------------- laboratory runs ----------------
  var LAB_MAX = 60;
  var lab = S.get('lab', null);
  if (!lab || lab.v !== 1) lab = { v: 1, runs: [], totals: { runs: 0, chests: '0', tokens: 0 } };
  TK.store.lab = {
    runs: function () { return lab.runs; },
    totals: function () { return lab.totals; },
    add: function (run) {
      run.id = run.id || U.uuid();
      lab.runs.unshift(run);
      if (lab.runs.length > LAB_MAX) lab.runs.length = LAB_MAX;
      lab.totals.runs += 1;
      lab.totals.chests = (BigInt(lab.totals.chests) + BigInt(run.n)).toString();
      lab.totals.tokens += run.tokens || 0;
      S.set('lab', lab);
      U.bus.emit('lab-changed', run);
      return run;
    },
    clear: function () { lab = { v: 1, runs: [], totals: { runs: 0, chests: '0', tokens: 0 } }; S.set('lab', lab); U.bus.emit('lab-changed', null); }
  };
})(window.TK = window.TK || {});
