/* TOKENIFY — exact reward generator.
 *
 * 1. Each outcome's probability is an exact rational from TK.config (Gizmo members = (1/127)/5).
 * 2. All fractions are put over one common denominator D (BigInt LCM) to get integer weights.
 * 3. Ordinary Loot receives exactly D - (sum of special weights).
 * 4. A draw takes ceil(log2 D) uniformly random bits from crypto.getRandomValues and rejects
 *    values >= D (rejection sampling, no modulo bias). The accepted value v in [0, D) selects
 *    the outcome whose cumulative weight interval contains v.
 * No state is carried between draws: no pity, no streak logic, no forced results.
 */
(function (TK) {
  'use strict';
  var cfg = TK.config;

  function gcd(a, b) { a = a < 0n ? -a : a; b = b < 0n ? -b : b; while (b) { var t = a % b; a = b; b = t; } return a; }
  function lcm(a, b) { return a / gcd(a, b) * b; }
  function reduce(n, d) { var g = gcd(n, d) || 1n; return [n / g, d / g]; }
  function bitLen(x) { return x === 0n ? 0 : x.toString(2).length; }

  // ---- outcomes in draw order (rarest first, ordinary last) ----
  var outcomes = [];
  cfg.REWARDS.forEach(function (r) {
    var f = reduce(BigInt(r.num), BigInt(r.den));
    outcomes.push(Object.assign({}, r, { group: null, numB: f[0], denB: f[1] }));
  });
  var G = cfg.GIZMO_GROUP, members = BigInt(G.members.length);
  G.members.forEach(function (g) {
    var f = reduce(BigInt(G.num), BigInt(G.den) * members); // (num/den) / members, exact
    outcomes.push(Object.assign({ tier: 'uncommon', kind: 'Gizmo' }, g, { rank: G.rank, group: G.id, numB: f[0], denB: f[1] }));
  });
  var specials = outcomes.slice();
  var D = specials.reduce(function (acc, o) { return lcm(acc, o.denB); }, 1n);
  var specialTotal = 0n;
  specials.forEach(function (o) { o.weight = o.numB * (D / o.denB); specialTotal += o.weight; });
  var ordW = D - specialTotal;
  var ordF = reduce(ordW, D);
  var ordinary = Object.assign({}, cfg.ORDINARY, { group: null, numB: ordF[0], denB: ordF[1], weight: ordW });
  outcomes.push(ordinary);
  outcomes.forEach(function (o, i) {
    o.index = i;
    o.num = Number(o.numB); o.den = Number(o.denB);
    o.prob = Number(o.weight) / Number(D);
    o.tierInfo = cfg.TIERS[o.tier];
  });
  var byId = {}; outcomes.forEach(function (o) { byId[o.id] = o; });
  var gizmoIds = G.members.map(function (m) { return m.id; });
  var groups = {};
  groups[G.id] = { id: G.id, name: G.name, short: G.short, num: G.num, den: G.den, numB: BigInt(G.num), denB: BigInt(G.den), members: gizmoIds, tier: 'uncommon' };

  // ---- cumulative thresholds split into hi/lo words for a BigInt-free hot path ----
  var BITS = bitLen(D - 1n);
  if (BITS > 64) throw new Error('Common denominator exceeds 64 bits; widen the sampler.');
  var HI_BITS = Math.max(0, BITS - 32);
  var HI_MASK = HI_BITS >= 32 ? 0xFFFFFFFF : ((1 << HI_BITS) >>> 0) - 1;
  var cum = [], acc = 0n;
  outcomes.forEach(function (o) { acc += o.weight; cum.push(acc); });
  var T_HI = cum.map(function (c) { return Number(c >> 32n); });
  var T_LO = cum.map(function (c) { return Number(c & 0xFFFFFFFFn); });
  var D_HI = Number(D >> 32n), D_LO = Number(D & 0xFFFFFFFFn);

  // ---- randomness ----
  var buf = new Uint32Array(2048), pos = buf.length;
  function u32() { if (pos >= buf.length) { crypto.getRandomValues(buf); pos = 0; } return buf[pos++]; }
  var stats = { draws: 0, rejections: 0 };
  // Map a candidate value v = hi*2^32 + lo to an outcome index, or -1 when v >= D (rejected).
  function classify(hi, lo) {
    if (hi > D_HI || (hi === D_HI && lo >= D_LO)) return -1;
    for (var i = 0; i < T_HI.length; i++) {
      if (hi < T_HI[i] || (hi === T_HI[i] && lo < T_LO[i])) return i;
    }
    return -1;
  }
  function drawIndex() {
    for (;;) {
      var hi = HI_BITS ? (u32() & HI_MASK) >>> 0 : 0, lo = u32();
      var i = classify(hi, lo);
      if (i < 0) { stats.rejections++; continue; } // v >= D: reject and draw again
      stats.draws++;
      return i;
    }
  }
  // Reference implementation with BigInt (used by tests to cross-check the fast path)
  function indexOfValue(v) { for (var i = 0; i < cum.length; i++) if (v < cum[i]) return i; return -1; }

  // Exact Bernoulli(num/den) parameters for 32-bit words: reject u >= k*den, success iff u < num*k.
  function bernoulliParams(num, den) {
    num = BigInt(num); den = BigInt(den);
    if (den > 4294967296n) throw new Error('den too large for 32-bit Bernoulli sampler');
    var k = 4294967296n / den;
    return { limit: Number(k * den), thresh: Number(num * k), num: Number(num), den: Number(den) };
  }

  function validate() {
    var checks = [];
    function add(label, pass, detail) { checks.push({ label: label, pass: !!pass, detail: detail || '' }); }
    var sum = outcomes.reduce(function (a, o) { return a + o.weight; }, 0n);
    add('All weights are non-negative', outcomes.every(function (o) { return o.weight >= 0n; }));
    add('Weights sum to the common denominator D', sum === D, 'D = ' + D.toString());
    function exactly(id, n, d) { var o = byId[id]; return o.weight * BigInt(d) === D * BigInt(n); }
    add('Weekly Gold Outfit is exactly 1/1,562,500', exactly('gold-outfit', 1, 1562500));
    add('Pirate Token is exactly 1/137,500', exactly('pirate-token', 1, 137500));
    add('Vehicle & Decal is exactly 1/7,813', exactly('vehicle-decal', 1, 7813));
    add('Pickaxe or Back Bling is exactly 1/568', exactly('pickaxe-backbling', 1, 568));
    add('Jam Track is exactly 1/391', exactly('jam-track', 1, 391));
    add('Loading Screen is exactly 1/272', exactly('loading-screen', 1, 272));
    add('Each Gizmo is exactly 1/635', gizmoIds.every(function (id) { return exactly(id, 1, 635); }));
    var gz = gizmoIds.reduce(function (a, id) { return a + byId[id].weight; }, 0n);
    add('Five Gizmos together are exactly 1/127', gz * 127n === D);
    add('Gold Outfit is rarer than the Pirate Token', byId['gold-outfit'].weight < byId['pirate-token'].weight);
    add('Pirate Token is the second-rarest outcome', outcomes.filter(function (o) { return o.weight < byId['pirate-token'].weight; }).length === 1);
    add('Ordinary Loot is the exact remainder', ordW === D - specialTotal && ordW > 0n, ordF[0] + '/' + ordF[1]);
    add('Sampler range fits the random word width', BITS <= 64, BITS + ' bits');
    return checks;
  }

  // Interactive ownership rule: exact probability of *receiving* each outcome when the ids in
  // `owned` (unique cosmetics already in the collection) are converted to Ordinary Loot.
  function effectiveProbs(owned) {
    var res = {}, extra = 0n;
    outcomes.forEach(function (o) {
      if (o.unique && cfg.RULES.uniqueCosmetics && owned && owned[o.id]) { res[o.id] = { num: 0n, den: 1n, prob: 0 }; extra += o.weight; }
    });
    outcomes.forEach(function (o) {
      if (res[o.id]) return;
      var w = o.weight + (o.id === ordinary.id ? extra : 0n), f = reduce(w, D);
      res[o.id] = { num: f[0], den: f[1], prob: Number(w) / Number(D) };
    });
    return res;
  }

  TK.rewards = {
    outcomes: outcomes, specials: specials, ordinary: ordinary, byId: byId, groups: groups, gizmoIds: gizmoIds,
    effectiveProbs: effectiveProbs,
    D: D, specialTotal: specialTotal,
    sampler: { BITS: BITS, HI_BITS: HI_BITS, HI_MASK: HI_MASK, T_HI: T_HI, T_LO: T_LO, D_HI: D_HI, D_LO: D_LO },
    cum: cum, indexOfValue: indexOfValue, classify: classify,
    draw: function () { return outcomes[drawIndex()]; },
    drawMany: function (n) { var out = new Array(n); for (var i = 0; i < n; i++) out[i] = outcomes[drawIndex()]; return out; },
    drawIndex: drawIndex,
    bernoulliParams: bernoulliParams,
    validate: validate,
    stats: stats,
    // per-chest probability descriptor for any outcome id or group id
    probOf: function (id) {
      if (groups[id]) return { num: groups[id].numB, den: groups[id].denB, name: groups[id].name };
      var o = byId[id]; return { num: o.numB, den: o.denB, name: o.name };
    }
  };
})(window.TK = window.TK || {});
