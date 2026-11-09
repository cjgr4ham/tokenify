/* Random-generation tests: exact sampler mapping + statistical consistency of real draws.
 * Statistical tests use p-value thresholds of 1e-4..1e-5, so a correct generator fails them
 * only extremely rarely; a rare reward not appearing in a short run is never treated as a bug. */
(function () {
  'use strict';
  var test = T.test, assert = T.assert;
  var P = TK.prob, R = TK.rewards;
  var S = R.sampler;

  function u32() { return crypto.getRandomValues(new Uint32Array(1))[0]; }

  test('Sampler', 'fast hi/lo classifier agrees with the BigInt reference everywhere', function () {
    var vals = [0n, 1n, R.D - 1n, R.D, R.D + 1n, (1n << BigInt(S.BITS)) - 1n];
    R.cum.forEach(function (c) { vals.push(c - 1n, c, c + 1n); });
    for (var i = 0; i < 30000; i++) vals.push((BigInt((u32() & S.HI_MASK) >>> 0) << 32n) | BigInt(u32()));
    var checked = 0;
    vals.forEach(function (v) {
      if (v < 0n || v >= (1n << BigInt(S.BITS))) return;
      var fast = R.classify(Number(v >> 32n), Number(v & 0xFFFFFFFFn));
      var ref = v >= R.D ? -1 : R.indexOfValue(v);
      assert(fast === ref, 'mismatch at v=' + v + ' fast=' + fast + ' ref=' + ref);
      checked++;
    });
    return checked + ' values incl. every interval boundary';
  });
  test('Sampler', 'rejection region is exactly [D, 2^bits) — no modulo bias', function () {
    assert(R.classify(S.D_HI, S.D_LO) === -1, 'D rejected');
    var dm1 = R.D - 1n; assert(R.classify(Number(dm1 >> 32n), Number(dm1 & 0xFFFFFFFFn)) === R.outcomes.length - 1, 'D-1 is the last outcome');
    return 'acceptance rate ' + (Number(R.D) / Math.pow(2, S.BITS) * 100).toFixed(2) + '%';
  });
  test('Sampler', 'Bernoulli parameters give exactly num/den for every reward', function () {
    var list = R.outcomes.filter(function (o) { return o.id !== 'ordinary'; }).map(function (o) { return [o.numB, o.denB, o.id]; });
    list.push([1n, 127n, 'any-gizmo']);
    list.forEach(function (x) {
      var bp = R.bernoulliParams(x[0], x[1]);
      assert(BigInt(bp.thresh) * x[1] === BigInt(bp.limit) * x[0], x[2]);
      assert(bp.limit <= 4294967296 && bp.limit > 4294967296 - Number(x[1]), x[2] + ' limit');
    });
    var t = R.bernoulliParams(1n, 137500n);
    return 'token: success if u < ' + t.thresh + ', reject if u >= ' + t.limit;
  });

  function fullJob(n) {
    var job = TK.compute.sim.create({ kind: 'full', n: n, T_HI: S.T_HI, T_LO: S.T_LO, D_HI: S.D_HI, D_LO: S.D_LO, HI_MASK: S.HI_MASK });
    while (!job.finished) job.run(1 << 22);
    return job.result();
  }
  var fullCache = null;
  function getFull() { if (!fullCache) fullCache = fullJob(6000000); return fullCache; }

  test('Simulation', 'full reward table passes a chi-square goodness-of-fit test (6,000,000 chests)', function () {
    var r = getFull(), N = r.n, o = R.outcomes;
    assert(r.counts.reduce(function (a, b) { return a + b; }, 0) === N, 'every chest produced exactly one outcome');
    var obs = [], exp = [];
    // pool the three rarest so every expected count is large enough
    var poolIdx = ['gold-outfit', 'pirate-token', 'vehicle-decal'].map(function (id) { return R.byId[id].index; });
    obs.push(poolIdx.reduce(function (a, i) { return a + r.counts[i]; }, 0));
    exp.push(poolIdx.reduce(function (a, i) { return a + o[i].prob * N; }, 0));
    o.forEach(function (x, i) { if (poolIdx.indexOf(i) < 0) { obs.push(r.counts[i]); exp.push(x.prob * N); } });
    var cs = T.chiSquare(obs, exp);
    assert(cs.p > 1e-4, 'chi2=' + cs.chi2.toFixed(2) + ' df=' + cs.df + ' p=' + cs.p);
    return 'chi²=' + cs.chi2.toFixed(2) + ', df=' + cs.df + ', p=' + cs.p.toFixed(3);
  });
  test('Simulation', 'Gizmo drops are uniform across the five Gizmos', function () {
    var r = getFull(), c = R.gizmoIds.map(function (id) { return r.counts[R.byId[id].index]; });
    var tot = c.reduce(function (a, b) { return a + b; }, 0);
    var cs = T.chiSquare(c, c.map(function () { return tot / 5; }));
    assert(cs.p > 1e-4, 'p=' + cs.p);
    return c.join(' / ') + ' (p=' + cs.p.toFixed(3) + ')';
  });
  test('Simulation', 'token fast mode count is consistent with Binomial(n, 1/137,500)', function () {
    var bp = R.bernoulliParams(1n, 137500n), n = 200000000;
    var job = TK.compute.sim.create({ kind: 'bernoulli', n: n, limit: bp.limit, thresh: bp.thresh });
    while (!job.finished) job.run(1 << 23);
    var k = job.result().successes, m = P.model(BigInt(n), 1n, 137500n);
    var two = 2 * Math.min(P.cdf(m, k).p, P.sf(m, k).p);
    assert(two > 1e-5, 'k=' + k + ' two-sided p=' + two);
    return n.toLocaleString() + ' chests → ' + k + ' tokens (expected ' + (n / 137500).toFixed(2) + ', two-sided p=' + Math.min(1, two).toFixed(3) + ')';
  });
  test('Simulation', 'repeated experiments follow the exact binomial distribution', function () {
    var bp = R.bernoulliParams(1n, 137500n), n = 137500, E = 2500;
    var job = TK.compute.sim.create({ kind: 'experiments', n: n, experiments: E, limit: bp.limit, thresh: bp.thresh });
    while (!job.finished) job.run(1 << 23);
    var counts = job.result().counts, m = P.model(BigInt(n), 1n, 137500n);
    var obs = [0, 0, 0, 0, 0], exp = [];
    counts.forEach(function (c) { obs[Math.min(4, c)]++; });
    for (var k = 0; k < 4; k++) exp.push(P.pmf(m, k).p * E);
    exp.push(P.sf(m, 4).p * E);
    var cs = T.chiSquare(obs, exp);
    assert(cs.p > 1e-4, 'p=' + cs.p + ' obs=' + obs.join(','));
    return 'counts 0..4+: ' + obs.join(', ') + ' (p=' + cs.p.toFixed(3) + ')';
  });
  test('Simulation', 'consecutive interactive draws show no dependence (no pity, no streaks)', function () {
    var N = 300000, d = R.drawMany(N), x = new Float64Array(N), mean = 0;
    for (var i = 0; i < N; i++) { x[i] = d[i].id === 'ordinary' ? 0 : 1; mean += x[i]; }
    mean /= N;
    var num = 0, den = 0; for (var j = 0; j < N; j++) { den += (x[j] - mean) * (x[j] - mean); if (j) num += (x[j] - mean) * (x[j - 1] - mean); }
    var r = num / den;
    assert(Math.abs(r) < 6 / Math.sqrt(N), 'lag-1 correlation ' + r);
    return 'lag-1 correlation ' + r.toFixed(5);
  });
  test('Simulation', 'background worker returns exactly the requested number of draws', async function () {
    var bp = R.bernoulliParams(1n, 137500n);
    var h = TK.compute.runSim({ kind: 'bernoulli', n: 12345678, limit: bp.limit, thresh: bp.thresh });
    var r = await h.promise;
    assert(r.n === 12345678, 'n');
    return (h.usingWorker ? 'web worker' : 'main-thread fallback') + ', ' + r.ms + ' ms';
  });
  test('Simulation', 'a running simulation can be cancelled', async function () {
    var bp = R.bernoulliParams(1n, 137500n), cancelled = false;
    var h = TK.compute.runSim({ kind: 'bernoulli', n: 2000000000, limit: bp.limit, thresh: bp.thresh });
    setTimeout(function () { h.cancel(); }, 120);
    try { await h.promise; } catch (e) { cancelled = !!e.cancelled; }
    assert(cancelled, 'should reject with cancelled');
  });
})();
