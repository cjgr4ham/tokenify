/* Probability engine tests: exact weights, numerics, identities, edge cases, reverse calculator. */
(function () {
  'use strict';
  var test = T.test, assert = T.assert, close = T.close;
  var P = TK.prob, BF = TK.BF, R = TK.rewards, U = TK.util, LN10 = Math.LN10;

  // ---------- exact rational helpers ----------
  function binom(n, k) { if (k < 0n || k > n) return 0n; if (k > n - k) k = n - k; var r = 1n; for (var i = 1n; i <= k; i++) r = r * (n - k + i) / i; return r; }
  function exactPmf(n, k, num, den) { var dq = den - num; return [binom(n, k) * (num ** k) * (dq ** (n - k)), den ** n]; }
  function exactCdf(n, k, num, den) { var s = 0n; for (var j = 0n; j <= k; j++) s += binom(n, j) * (num ** j) * ((den - num) ** (n - j)); return [s, den ** n]; }
  function ratio(a, b) { return BF.fromBigInt(a).div(BF.fromBigInt(b)); }

  // ---------- exact drop table ----------
  test('Exact drop table', 'engine self-validation passes every check', function () {
    var bad = R.validate().filter(function (c) { return !c.pass; });
    assert(bad.length === 0, 'failed: ' + bad.map(function (c) { return c.label; }).join('; '));
    return R.validate().length + ' checks';
  });
  test('Exact drop table', 'common denominator is the exact LCM', function () {
    assert(R.D === 1893779250756250000n, 'D = ' + R.D);
    [1562500n, 137500n, 7813n, 568n, 391n, 272n, 635n, 127n].forEach(function (d) { assert(R.D % d === 0n, 'not divisible by ' + d); });
  });
  test('Exact drop table', 'Gold Outfit is exactly 1 in 1,562,500', function () { var o = R.byId['gold-outfit']; assert(o.weight * 1562500n === R.D && o.numB === 1n && o.denB === 1562500n); });
  test('Exact drop table', 'Pirate Token is exactly 1 in 137,500 (not 1 in 1,562,500)', function () {
    var o = R.byId['pirate-token']; assert(o.weight * 137500n === R.D && o.denB === 137500n);
    assert(o.weight !== R.byId['gold-outfit'].weight);
  });
  test('Exact drop table', 'each Gizmo is exactly 1/635 and together exactly 1/127', function () {
    var tot = 0n;
    R.gizmoIds.forEach(function (id) { var o = R.byId[id]; assert(o.weight * 635n === R.D, id); assert(o.numB === 1n && o.denB === 635n, id + ' fraction'); tot += o.weight; });
    assert(tot * 127n === R.D, 'sum of gizmos');
    assert(R.gizmoIds.length === 5, 'five gizmos');
  });
  test('Exact drop table', 'every special reward has its exact listed fraction', function () {
    [['vehicle-decal', 7813n], ['pickaxe-backbling', 568n], ['jam-track', 391n], ['loading-screen', 272n]].forEach(function (x) {
      assert(R.byId[x[0]].weight * x[1] === R.D, x[0]);
    });
  });
  test('Exact drop table', 'all outcomes sum to exactly 1 with Ordinary Loot as remainder', function () {
    var sum = R.outcomes.reduce(function (a, o) { return a + o.weight; }, 0n);
    assert(sum === R.D);
    assert(R.ordinary.weight === 1863470262613539641n, 'ordinary weight ' + R.ordinary.weight);
    assert(R.outcomes.length === 12, 'one outcome per reward; no extra standalone Gizmo outcome');
    return 'Ordinary = ' + R.ordinary.numB + '/' + R.ordinary.denB;
  });

  // ---------- numerics ----------
  test('Numerics', 'stirlerr table matches log-factorials (integers and halves)', function () {
    for (var n = 1; n <= 15; n++) {
      var lf = 0; for (var i = 2; i <= n; i++) lf += Math.log(i);
      close(P._stirlerr(n), lf - (n + 0.5) * Math.log(n) + n - 0.5 * Math.log(2 * Math.PI), 1e-10, 'n=' + n);
    }
    for (var m = 0; m <= 14; m++) {
      var x = m + 0.5, l2 = 0, l1 = 0;
      for (var a = 2; a <= 2 * m + 2; a++) l2 += Math.log(a);
      for (var b = 2; b <= m + 1; b++) l1 += Math.log(b);
      var lg = l2 - (m + 1) * Math.log(4) - l1 + 0.5 * Math.log(Math.PI);
      close(P._SFERR[2 * m + 1], lg - (x + 0.5) * Math.log(x) + x - 0.5 * Math.log(2 * Math.PI), 1e-9, 'x=' + x);
    }
  });
  test('Numerics', 'PMF matches exact rational arithmetic (small n, several p)', function () {
    var cases = 0, worst = 0;
    var ps = [[1n, 137500n], [1n, 127n], [1n, 1562500n], [1n, 2n], [1n, 3n], [7n, 10n]];
    [1n, 2n, 5n, 13n, 40n, 137n, 600n].forEach(function (n) {
      ps.forEach(function (pp) {
        var m = P.model(n, pp[0], pp[1]);
        var ks = []; for (var k = 0n; k <= n; k += (n > 40n ? n / 23n : 1n)) ks.push(k); ks.push(n);
        ks.forEach(function (k) {
          var ex = exactPmf(n, k, pp[0], pp[1]);
          var lpExact = ratio(ex[0], ex[1]).log10();
          var got = P.logPmf(m, Number(k)) / LN10;
          if (ex[0] === 0n) { assert(got === -Infinity); return; }
          var diff = Math.abs(got - lpExact) * LN10; // ~ relative error of the probability
          worst = Math.max(worst, diff / Math.max(1, Math.abs(lpExact) / 300));
          assert(diff < 1e-12 * Math.max(1, Math.abs(lpExact)), 'n=' + n + ' k=' + k + ' p=' + pp.join('/') + ' log10 exact ' + lpExact + ' got ' + got);
          cases++;
        });
      });
    });
    return cases + ' exact comparisons';
  });
  test('Numerics', 'CDF matches exact rational sums (n up to 300)', function () {
    [[25n, 1n, 2n], [300n, 1n, 127n], [120n, 1n, 137500n], [64n, 1n, 3n]].forEach(function (c) {
      var n = c[0], m = P.model(n, c[1], c[2]);
      for (var k = 0n; k < n; k += (n / 9n || 1n)) {
        var ex = exactCdf(n, k, c[1], c[2]);
        var exv = ratio(ex[0], ex[1]), exc = BF.ONE.sub(exv);
        var got = P.cdf(m, Number(k));
        close(got.p, exv.toNumber(), 1e-13, 'cdf n=' + n + ' k=' + k);
        if (exc.toNumber() > 1e-300) close(got.q, exc.toNumber(), 1e-12, 'complement n=' + n + ' k=' + k);
      }
    });
  });
  test('Numerics', 'P(at least one) for 1,000,000 chests equals 1 - (137499/137500)^1000000', function () {
    var m = P.model(1000000n, 1n, 137500n);
    var z = BF.fromRatio(137499n, 137500n).pow(1000000n);
    close(P.zero(m).p, z.toNumber(), 1e-14, 'P(0)');
    close(P.atLeastOne(m).p, BF.ONE.sub(z).toNumber(), 1e-14, 'P(>=1)');
    return 'P(>=1) = ' + BF.ONE.sub(z).toFixed(20);
  });
  test('Numerics', 'double engine agrees with 320-bit series at n = 10^9', function () {
    var n = 1000000000n, m = P.model(n, 1n, 137500n);
    [6900, 7272, 7273, 7600].forEach(function (k) {
      var hp = TK.hp.cdf(n, 1n, 137500n, BigInt(k), 1e7), d = P.cdf(m, k);
      close(d.p, hp.value.toNumber(), 1e-12, 'cdf k=' + k);
      close(d.q, hp.comp.toNumber(), 1e-11, 'complement k=' + k);
    });
  });
  test('Numerics', 'tiny tail probabilities keep their logarithm instead of becoming 0', function () {
    var m = P.model(1000n, 1n, 137500n), pr = P.pmf(m, 1000);
    assert(pr.p === 0, 'underflows as a double');
    close(pr.lp / LN10, 1000 * Math.log10(1 / 137500), 1e-12, 'log10 P(X=1000)');
    var s = TK.fmt.prob(pr, { mode: 'sci', precision: 'standard' });
    assert(/10⁻⁵¹³⁹/.test(s), 'scientific output ' + s);
    var big = P.model(10n ** 20n, 1n, 137500n), one = P.atLeastOne(big);
    assert(one.p === 1 && isFinite(one.lq) && one.lq < -1e14, 'complement log kept: ' + one.lq);
  });

  // ---------- identities ----------
  test('Identities', 'P(X=0) + P(X>=1) = 1', function () {
    [0n, 1n, 10n, 137500n, 1000000n, 1000000000n, 1000000000000n].forEach(function (n) {
      var m = P.model(n, 1n, 137500n), a = P.zero(m), b = P.atLeastOne(m);
      close(a.p + b.p, 1, 1e-15, 'n=' + n);
      if (a.p > 1e-300) close(a.p, b.q, 1e-13, 'complement n=' + n);
    });
  });
  test('Identities', 'P(X<=k) + P(X>k) = 1', function () {
    [[1000000n, 1n, 137500n], [50000000n, 1n, 137500n], [10000n, 1n, 127n], [3000n, 1n, 2n]].forEach(function (c) {
      var m = P.model(c[0], c[1], c[2]);
      [0, 1, 2, 5, Math.floor(m.np), Math.ceil(m.np) + 3, Math.floor(m.np * 2) + 4].forEach(function (k) {
        var a = P.cdf(m, k), b = P.gt(m, k);
        close(a.p + b.p, 1, 1e-14, 'n=' + c[0] + ' k=' + k);
      });
    });
  });
  test('Identities', 'PMF sums to 1 over all possible counts', function () {
    [[1n, 1n, 137500n], [7n, 1n, 2n], [60n, 1n, 127n], [400n, 1n, 3n], [2000n, 1n, 137500n]].forEach(function (c) {
      var m = P.model(c[0], c[1], c[2]), terms = [];
      for (var k = 0; k <= m.n; k++) terms.push(P.pmf(m, k).p);
      terms.sort(function (a, b) { return a - b; });
      var s = terms.reduce(function (a, b) { return a + b; }, 0);
      close(s, 1, 1e-13, 'n=' + c[0]);
    });
  });
  test('Identities', 'P(X=k) is zero outside 0..n', function () {
    var m = P.model(25n, 1n, 137500n);
    assert(P.pmf(m, -1).p === 0 && P.pmf(m, 26).p === 0 && P.pmf(m, 2.5).p === 0);
  });
  test('Identities', 'P(a<=X<=b) = P(X<=b) - P(X<a)', function () {
    var m = P.model(2000000n, 1n, 137500n);
    [[3, 8], [0, 14], [10, 30], [15, 15], [0, 0], [20, 60]].forEach(function (ab) {
      var direct = P.between(m, ab[0], ab[1]).p, viaCdf = P.cdf(m, ab[1]).p - P.cdf(m, ab[0] - 1).p;
      close(direct, viaCdf, 1e-11, ab.join('..'));
    });
  });

  // ---------- edge cases ----------
  test('Edge cases', 'zero chests', function () {
    var m = P.model(0n, 1n, 137500n);
    assert(P.zero(m).p === 1 && P.atLeastOne(m).p === 0 && P.meanExact(m).num === 0n);
  });
  test('Edge cases', 'one chest', function () {
    var m = P.model(1n, 1n, 137500n);
    close(P.atLeastOne(m).p, 1 / 137500, 1e-15); close(P.pmf(m, 1).p, 1 / 137500, 1e-15);
  });
  test('Edge cases', 'desired tokens: zero, equal to and greater than the chest count', function () {
    var m = P.model(3n, 1n, 137500n);
    assert(P.sf(m, 0).p === 1, 'at least 0');
    close(P.pmf(m, 3).p, Math.pow(1 / 137500, 3), 1e-13, 'k = n');
    assert(P.pmf(m, 4).p === 0 && P.sf(m, 4).p === 0, 'k > n');
    assert(P.minTrials(1n, 137500n, 0, U.parsePercent('90')).n === 0, 'zero tokens needs zero chests');
  });
  test('Edge cases', 'chest counts above 2^53 are parsed exactly', function () {
    var r = U.parseCount('1e20'); assert(r.ok && r.value === 10n ** 20n);
    var r2 = U.parseCount('123,456,789,012,345,678,901'); assert(r2.ok && r2.value === 123456789012345678901n);
    var e = P.meanExact(P.model(r2.value, 1n, 137500n));
    assert(e.num * 137500n === r2.value * e.den, 'exact expectation');
  });
  test('Edge cases', 'invalid, negative, empty and fractional inputs are rejected', function () {
    assert(!U.parseCount('-5').ok && !U.parseCount('12.5').ok && !U.parseCount('').ok && !U.parseCount('abc').ok && !U.parseCount('1e').ok);
    assert(U.parseCount('1,000,000').value === 1000000n && U.parseCount('2.5m').value === 2500000n && U.parseCount('1e6').value === 1000000n && U.parseCount('10k').value === 10000n);
    assert(U.parseCount('2.5').ok === false, '2.5 chests');
  });
  test('Edge cases', 'probability targets of 0% and 100%', function () {
    assert(P.minTrials(1n, 137500n, 1, U.parsePercent('0')).n === 0);
    assert(P.minTrials(1n, 137500n, 1, U.parsePercent('100')).impossible === true);
  });
  test('Edge cases', 'targets very close to 0% and 100% have exact boundaries', function () {
    ['0.0001', '99.9999', '99.99999'].forEach(function (pct) {
      var t = U.parsePercent(pct), r = P.minTrials(1n, 137500n, 1, t);
      var comp = BF.fromRatio(t.den - t.num, t.den), base = BF.fromRatio(137499n, 137500n);
      assert(base.pow(BigInt(r.n)).cmp(comp) <= 0, pct + ': n=' + r.n + ' should reach target');
      assert(r.n === 0 || base.pow(BigInt(r.n - 1)).cmp(comp) > 0, pct + ': n-1 should not');
    });
  });

  // ---------- reverse calculator ----------
  test('Reverse calculator', 'one-token presets give the exact minimum chest count', function () {
    var out = [];
    ['10', '25', '50', '75', '90', '95', '99', '99.9'].forEach(function (pct) {
      var t = U.parsePercent(pct), r = P.minTrials(1n, 137500n, 1, t);
      var comp = BF.fromRatio(t.den - t.num, t.den), base = BF.fromRatio(137499n, 137500n);
      assert(base.pow(BigInt(r.n)).cmp(comp) <= 0, pct + '% not reached at n=' + r.n);
      assert(base.pow(BigInt(r.n - 1)).cmp(comp) > 0, pct + '% already reached at n-1=' + (r.n - 1));
      out.push(pct + '%→' + r.n);
    });
    return out.join(', ');
  });
  test('Reverse calculator', 'three tokens at 90% uses the binomial tail (verified exactly)', function () {
    var t = U.parsePercent('90'), r = P.minTrials(1n, 137500n, 3, t);
    var comp = BF.fromRatio(t.den - t.num, t.den);
    var at = TK.hp.cdf(BigInt(r.n), 1n, 137500n, 2n, 1e6), before = TK.hp.cdf(BigInt(r.n - 1), 1n, 137500n, 2n, 1e6);
    assert(at.value.cmp(comp) <= 0 && before.value.cmp(comp) > 0, 'boundary');
    assert(r.n !== 3 * 137500, 'not simply k x 137,500');
    return 'n = ' + r.n;
  });
  test('Reverse calculator', 'works for other rewards (Gold Outfit, any Gizmo)', function () {
    var t = U.parsePercent('50');
    var g = P.minTrials(1n, 1562500n, 1, t), z = P.minTrials(1n, 127n, 1, t);
    assert(g.n > 1000000 && z.n === 88, 'gold ' + g.n + ' gizmo ' + z.n);
  });

  // ---------- prediction intervals / quantiles ----------
  test('Prediction intervals', 'central intervals satisfy the discrete equal-tail definition', function () {
    [10000n, 1000000n, 100000000n, 10000000000n].forEach(function (n) {
      var m = P.model(n, 1n, 137500n);
      [[0.95, 0.05], [0.8, 0.2], [0.99, 0.01]].forEach(function (cc) {
        var iv = P.centralInterval(m, cc[0], cc[1]), h = cc[1] / 2;
        assert(iv.below.p < h + 1e-15, 'lower tail n=' + n);
        assert(iv.above.p <= h + 1e-15, 'upper tail n=' + n);
        assert(iv.lo === 0 || P.cdf(m, iv.lo).p >= h - 1e-15, 'lo minimal n=' + n);
        assert(P.sf(m, iv.hi).p > h - 1e-15, 'hi minimal n=' + n);
        assert(iv.coverage.p >= cc[0] - 1e-12, 'coverage n=' + n);
      });
    });
  });
  test('Prediction intervals', 'quantile is the smallest k with P(X<=k) >= alpha', function () {
    var m = P.model(5000000n, 1n, 137500n);
    [0.001, 0.025, 0.3, 0.5, 0.8, 0.975, 0.999].forEach(function (a) {
      var k = P.quantile(m, a);
      assert(P.cdf(m, k).p >= a - 1e-15, 'reaches ' + a);
      assert(k === 0 || P.cdf(m, k - 1).p < a + 1e-15, 'minimal ' + a);
    });
  });

  // ---------- formatting ----------
  test('Formatting', 'exact expected value and repeating decimal', function () {
    var e = P.meanExact(P.model(1000000n, 1n, 137500n));
    assert(e.num === 80n && e.den === 11n, 'E = 80/11');
    var d = TK.fmt.rationalDecimal(80n, 11n, 20); assert(d.period === '27' && d.int === '7');
  });
  test('Formatting', 'values below the display resolution show an honest bound', function () {
    var s = TK.fmt.prob(P.probFromLog(-40), { mode: 'percent', precision: 'standard' });
    assert(s.indexOf('<') === 0, s);
    var s2 = TK.fmt.prob(P.probFromLogComplement(-40), { mode: 'percent', precision: 'standard' });
    assert(s2.indexOf('>') === 0, s2);
  });
  test('Formatting', 'BigFloat decimal output is correctly rounded', function () {
    assert(BF.fromRatio(1n, 3n).toFixed(30) === '0.333333333333333333333333333333');
    assert(BF.fromRatio(2n, 3n).toFixed(5) === '0.66667');
    var s = BF.fromRatio(1n, 137500n).toSci(10); assert(s.mant === '7272727273' && s.exp === -6, JSON.stringify(s));
    var t = BF.fromRatio(137499n, 137500n).pow(1000000n).toSci(25);
    return '(137499/137500)^1e6 = ' + t.mant[0] + '.' + t.mant.slice(1) + 'e' + t.exp;
  });
})();
