/* TOKENIFY — probability engine.
 *
 * X ~ Binomial(n, p) with p an exact rational num/den.
 *
 * Double-precision engine (default):
 *   - PMF via Loader's saddle-point formula (stirlerr + bd0), with n*p carried as an exact
 *     integer part + fraction so k - np never loses digits. Relative accuracy ~1e-15.
 *   - Tails are always summed on the side that does NOT contain the mode, outward from the
 *     boundary, with exact re-anchoring every 64 terms. The other side is the exact complement.
 *   - Every probability is returned as { p, q, lp, lq } (value, complement and their logs), so
 *     tiny values never silently become 0 and values near 1 keep their digits.
 * High-precision engine (TK_HpFactory): BigInt binary floating point (320-bit mantissa),
 *   exact rational recurrences. Used for the "High Precision" display mode.
 */
(function (TK) {
  'use strict';
  var LN_2PI = 1.837877066409345483560659472811;
  var LN10 = Math.LN10;
  // stirlerr(n/2) for n = 0..30 (Loader 2000 / R nmath). Index 0 is a placeholder.
  var SFERR = [0.0,
    0.1534264097200273452913848, 0.0810614667953272582196702, 0.0548141210519176538961390, 0.0413406959554092940938221,
    0.03316287351993628748511048, 0.02767792568499833914878929, 0.02374616365629749597132920, 0.02079067210376509311152277,
    0.01848845053267318523077934, 0.01664469118982119216319487, 0.01513497322191737887351255, 0.01387612882307074799874573,
    0.01281046524292022692424986, 0.01189670994589177009505572, 0.01110455975820691732662991, 0.010411265261972096497478567,
    0.009799416126158803298389475, 0.009255462182712732917728637, 0.008768700134139385462952823, 0.008330563433362871256469318,
    0.007934114564314020547248100, 0.007573675487951840794972024, 0.007244554301320383179543912, 0.006942840107209529865664152,
    0.006665247032707682442354394, 0.006408994188004207068439631, 0.006171712263039457647532867, 0.005951370112758847735624416,
    0.005746216513010115682023589, 0.005554733551962801371038690];
  var S0 = 1 / 12, S1 = 1 / 360, S2 = 1 / 1260, S3 = 1 / 1680, S4 = 1 / 1188;

  function stirlerr(n) {
    if (n <= 15) {
      var nn2 = n + n;
      if (nn2 !== Math.floor(nn2)) throw new Error('stirlerr: non-integer argument');
      return SFERR[nn2];
    }
    var nn = n * n;
    if (n > 500) return (S0 - S1 / nn) / n;
    if (n > 80) return (S0 - (S1 - S2 / nn) / nn) / n;
    if (n > 35) return (S0 - (S1 - (S2 - S3 / nn) / nn) / nn) / n;
    return (S0 - (S1 - (S2 - (S3 - S4 / nn) / nn) / nn) / nn) / n;
  }
  // x*log(x/M) + M - x, where D = x - M is supplied accurately by the caller.
  function bd0d(x, M, D) {
    if (Math.abs(D) < 0.1 * (x + M)) {
      var v = D / (x + M), s = D * v;
      if (Math.abs(s) < Number.MIN_VALUE) return s;
      var ej = 2 * x * v; v = v * v;
      for (var j = 1; j < 1000; j++) {
        ej *= v;
        var s1 = s + ej / (2 * j + 1);
        if (s1 === s) return s1;
        s = s1;
      }
      return s;
    }
    return x * Math.log1p(D / M) - D;
  }

  function gcd(a, b) { a = a < 0n ? -a : a; b = b < 0n ? -b : b; while (b) { var t = a % b; a = b; b = t; } return a; }
  function toBig(x) { return typeof x === 'bigint' ? x : BigInt(x); }

  var P = {};
  P.TooManyTerms = function (terms) { this.name = 'TooManyTerms'; this.terms = terms; this.message = 'Calculation needs more than ' + terms + ' terms'; };
  function limit() { return (TK.config && TK.config.LIMITS && TK.config.LIMITS.sumMaxTerms) || 6e7; }

  // ----- model -----
  P.model = function (nIn, numIn, denIn) {
    var nBig = toBig(nIn), numB = toBig(numIn), denB = toBig(denIn);
    if (nBig < 0n) throw new Error('n must be >= 0');
    if (numB < 0n || denB <= 0n || numB > denB) throw new Error('invalid probability');
    var g = gcd(numB, denB) || 1n; numB /= g; denB /= g;
    var dqB = denB - numB;
    var prod = nBig * numB, Q = prod / denB, R = prod % denB;
    var modeBig = (nBig + 1n) * numB / denB; if (modeBig > nBig) modeBig = nBig;
    var num = Number(numB), den = Number(denB), dq = Number(dqB);
    var p = num / den, q = dq / den;
    var np = Number(Q) + Number(R) / den;
    return {
      nBig: nBig, n: Number(nBig), numB: numB, denB: denB, dqB: dqB, num: num, den: den, dq: dq,
      p: p, q: q, npInt: Number(Q), npFrac: Number(R) / den, np: np,
      mode: Number(modeBig), modeBig: modeBig,
      exactN: nBig <= 9007199254740991n,
      log1mp: Math.log1p(-p),
      variance: np * q, sd: Math.sqrt(np * q)
    };
  };

  // ----- PMF -----
  P.logPmf = function (m, k) {
    var n = m.n;
    if (k < 0 || k > n || k !== Math.floor(k)) return -Infinity;
    if (m.num === 0) return k === 0 ? 0 : -Infinity;
    if (m.dq === 0) return k === n ? 0 : -Infinity;
    if (k === 0) return n === 0 ? 0 : n * m.log1mp;
    if (k === n) return n * Math.log(m.p);
    var D1 = (k - m.npInt) - m.npFrac;
    var nq = n - m.np;
    var lc = stirlerr(n) - stirlerr(k) - stirlerr(n - k) - bd0d(k, m.np, D1) - bd0d(n - k, nq, -D1);
    var lf = LN_2PI + Math.log(k) + Math.log1p(-k / n);
    return lc - 0.5 * lf;
  };

  // ----- probability value objects -----
  function probFromLog(lp) {
    if (!(lp < 0)) return { p: 1, q: 0, lp: 0, lq: -Infinity };
    if (lp === -Infinity) return { p: 0, q: 1, lp: -Infinity, lq: 0 };
    var p = Math.exp(lp), q = -Math.expm1(lp);
    var lq = lp < -0.6931471805599453 ? Math.log1p(-p) : Math.log(q);
    return { p: p, q: q, lp: lp, lq: lq };
  }
  function probFromLogComplement(lq) { var r = probFromLog(lq); return { p: r.q, q: r.p, lp: r.lq, lq: r.lp }; }
  P.probFromLog = probFromLog;
  P.probFromLogComplement = probFromLogComplement;
  P.ZERO = { p: 0, q: 1, lp: -Infinity, lq: 0 };
  P.ONE = { p: 1, q: 0, lp: 0, lq: -Infinity };
  function logAdd(a, b) { if (a === -Infinity) return b; if (b === -Infinity) return a; var mx = Math.max(a, b); return mx + Math.log1p(Math.exp(Math.min(a, b) - mx)); }
  P.logAdd = logAdd;

  // Sum P(j) for j from `start` moving away from the mode (dir = +1 up, -1 down), stopping at `stop`
  // (inclusive) or when terms become negligible. Returns ln(sum).
  function tailLog(m, start, dir, stop) {
    var L0 = P.logPmf(m, start);
    if (L0 === -Infinity) return -Infinity;
    var sum = 1, t = 1, j = start, steps = 0, n = m.n, num = m.num, dq = m.dq, lim = limit();
    while (dir > 0 ? j < stop : j > stop) {
      if (dir > 0) { t *= ((n - j) * num) / ((j + 1) * dq); j++; }
      else { t *= (j * dq) / ((n - j + 1) * num); j--; }
      steps++;
      if ((steps & 63) === 0) t = Math.exp(P.logPmf(m, j) - L0);
      sum += t;
      if (t <= sum * 1e-17) break;
      if (steps > lim) throw new P.TooManyTerms(lim);
    }
    return L0 + Math.log(sum);
  }

  P.pmf = function (m, k) { return probFromLog(P.logPmf(m, k)); };
  // P(X <= k)
  P.cdf = function (m, k) {
    if (k < 0) return P.ZERO;
    if (k >= m.n) return P.ONE;
    if (k < m.mode) return probFromLog(tailLog(m, k, -1, 0));
    return probFromLogComplement(tailLog(m, k + 1, +1, m.n));
  };
  // P(X >= k)
  P.sf = function (m, k) {
    if (k <= 0) return P.ONE;
    if (k > m.n) return P.ZERO;
    if (k > m.mode) return probFromLog(tailLog(m, k, +1, m.n));
    return probFromLogComplement(tailLog(m, k - 1, -1, 0));
  };
  P.lt = function (m, k) { return P.cdf(m, k - 1); };   // P(X < k)
  P.gt = function (m, k) { return P.sf(m, k + 1); };    // P(X > k)
  P.zero = function (m) { return P.pmf(m, 0); };
  P.atLeastOne = function (m) { return P.sf(m, 1); };
  // P(a <= X <= b)
  P.between = function (m, a, b) {
    a = Math.max(0, a); b = Math.min(m.n, b);
    if (a > b) return P.ZERO;
    if (a === 0 && b === m.n) return P.ONE;
    if (b < m.mode) return probFromLog(tailLog(m, b, -1, a));
    if (a > m.mode) return probFromLog(tailLog(m, a, +1, b));
    // interval contains the mode: 1 - lower tail - upper tail
    var lo = a > 0 ? tailLog(m, a - 1, -1, 0) : -Infinity;
    var hi = b < m.n ? tailLog(m, b + 1, +1, m.n) : -Infinity;
    return probFromLogComplement(logAdd(lo, hi));
  };

  // exact mean / variance as reduced rationals
  P.meanExact = function (m) { var nu = m.nBig * m.numB, de = m.denB, g = gcd(nu, de) || 1n; return { num: nu / g, den: de / g }; };
  P.varianceExact = function (m) { var nu = m.nBig * m.numB * m.dqB, de = m.denB * m.denB, g = gcd(nu, de) || 1n; return { num: nu / g, den: de / g }; };

  // ----- quantiles -----
  // Acklam's inverse normal (initial guesses only)
  function normInv(pr) {
    var a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
    var b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
    var c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
    var d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
    var pl = 0.02425, q, r;
    if (pr <= 0) return -Infinity; if (pr >= 1) return Infinity;
    if (pr < pl) { q = Math.sqrt(-2 * Math.log(pr)); return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
    if (pr <= 1 - pl) { q = pr - 0.5; r = q * q; return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1); }
    q = Math.sqrt(-2 * Math.log(1 - pr)); return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
  }
  P.normInv = normInv;

  // smallest k with P(X <= k) >= alpha. `alpha` may be given with its exact complement `beta = 1 - alpha`.
  P.quantile = function (m, alpha, beta) {
    if (beta == null) beta = 1 - alpha;
    if (alpha <= 0) return 0;
    if (beta <= 0) return m.n;
    var useUpper = alpha > 0.5;
    var reached = function (pr) { return useUpper ? pr.q <= beta : pr.p >= alpha; };
    var mu = m.np, sg = m.sd;
    var k;
    if (sg < 1e-9) k = 0;
    else {
      var z = normInv(alpha), g = (1 - 2 * m.p) / sg;
      k = Math.floor(mu + sg * (z + g * (z * z - 1) / 6) + 0.5);
    }
    k = Math.max(0, Math.min(m.n, k));
    var c = P.cdf(m, k), guard = 0;
    if (reached(c)) {
      while (k > 0 && guard++ < 1e7) {
        var pk = P.pmf(m, k);
        var prev = useUpper ? probFromLogComplement(logAdd(c.lq, pk.lp)) : probFromLog(c.lp + Math.log1p(-Math.min(1, Math.exp(pk.lp - c.lp))));
        if (!reached(prev)) break;
        c = prev; k--;
        if (guard % 2000 === 0) c = P.cdf(m, k);
      }
    } else {
      while (k < m.n && guard++ < 1e7) {
        k++;
        var pk2 = P.pmf(m, k);
        c = useUpper ? probFromLogComplement(c.lq + Math.log1p(-Math.min(1, Math.exp(pk2.lp - c.lq)))) : probFromLog(logAdd(c.lp, pk2.lp));
        if (guard % 2000 === 0) c = P.cdf(m, k);
        if (reached(c)) break;
      }
    }
    return k;
  };
  P.median = function (m) { return P.quantile(m, 0.5, 0.5); };
  // central (equal-tailed) prediction interval for coverage c (with exact complement cc = 1-c)
  P.centralInterval = function (m, cov, comp) {
    if (comp == null) comp = 1 - cov;
    var half = comp / 2;
    var lo = P.quantile(m, half, 1 - half);
    var hi = P.quantile(m, 1 - half, half);
    var coverage = P.between(m, lo, hi);
    return { lo: lo, hi: hi, coverage: coverage, below: P.cdf(m, lo - 1), above: P.sf(m, hi + 1) };
  };

  // ----- distribution window for charts (exact bin sums) -----
  P.distribution = function (m, opts) {
    opts = opts || {};
    var rel = opts.rel || 1e-12, maxBins = opts.maxBins || 140, lim = limit();
    var mode = m.mode, Lm = P.logPmf(m, mode), cut = Math.log(rel);
    // walk outward to find the window edges
    var hi = mode, t = 0, steps = 0;
    while (hi < m.n) {
      var nt = t + Math.log(((m.n - hi) * m.num) / ((hi + 1) * m.dq));
      if (nt < cut) break; t = nt; hi++;
      if (++steps % 64 === 0) t = P.logPmf(m, hi) - Lm;
      if (steps > lim) throw new P.TooManyTerms(lim);
    }
    var lo = mode; t = 0; steps = 0;
    while (lo > 0) {
      var nt2 = t + Math.log((lo * m.dq) / ((m.n - lo + 1) * m.num));
      if (nt2 < cut) break; t = nt2; lo--;
      if (++steps % 64 === 0) t = P.logPmf(m, lo) - Lm;
      if (steps > lim) throw new P.TooManyTerms(lim);
    }
    if (opts.minHi != null) hi = Math.max(hi, Math.min(m.n, opts.minHi));
    if (opts.minLo != null) lo = Math.min(lo, Math.max(0, opts.minLo));
    var width = hi - lo + 1;
    var bw = Math.max(1, Math.ceil(width / maxBins));
    var bins = [];
    // single pass, summing exact PMF terms into bins (in scaled space relative to the mode)
    var L0 = P.logPmf(m, lo), tt = 1, j = lo, binStart = lo, acc = 0, total = 0;
    steps = 0;
    for (;;) {
      acc += tt;
      if (j - binStart + 1 === bw || j === hi) {
        bins.push({ k0: binStart, k1: j, lp: L0 + Math.log(acc) });
        total += acc; acc = 0; binStart = j + 1;
      }
      if (j === hi) break;
      tt *= ((m.n - j) * m.num) / ((j + 1) * m.dq); j++;
      if (++steps % 64 === 0) tt = Math.exp(P.logPmf(m, j) - L0);
      if (steps > lim) throw new P.TooManyTerms(lim);
    }
    var maxLp = -Infinity; bins.forEach(function (b) { b.p = Math.exp(b.lp); if (b.lp > maxLp) maxLp = b.lp; });
    var inside = probFromLog(L0 + Math.log(total));
    return { lo: lo, hi: hi, binWidth: bw, bins: bins, maxLp: maxLp, inside: inside };
  };

  // ----- reverse calculator: smallest n with P(X >= k) >= target -----
  // target: { value, num, den } (exact rational from the user's percentage)
  P.minTrials = function (num, den, k, target) {
    num = toBig(num); den = toBig(den);
    if (k <= 0 || target.num === 0n) return { n: 0, trivial: true };
    if (target.num >= target.den) return { impossible: true };
    if (num === 0n) return { impossible: true };
    var comp = Number(target.den - target.num) / Number(target.den);
    var test = function (n) { var r = P.sf(P.model(n, num, den), k); return { n: n, r: r, pass: r.q <= comp, margin: (comp - r.q) / comp }; };
    var lo, hi, cur;
    var p = Number(num) / Number(den);
    if (k === 1) {
      var guess = Math.max(1, Math.ceil(Math.log(comp) / Math.log1p(-p)));
      cur = test(guess);
      if (cur.pass) { hi = guess; lo = guess - 1; while (lo >= 1 && test(lo).pass) { hi = lo; lo--; } }
      else { lo = guess; hi = guess + 1; while (!test(hi).pass) { lo = hi; hi++; } }
    } else {
      lo = k - 1;
      hi = Math.max(k, Math.ceil(k / p));
      var guard = 0;
      while (!test(hi).pass) { lo = hi; hi = hi * 2; if (++guard > 80) return { impossible: true }; }
      while (hi - lo > 1) {
        var mid = Math.floor((lo + hi) / 2);
        if (test(mid).pass) hi = mid; else lo = mid;
      }
    }
    var at = test(hi), before = hi - 1 >= 0 ? test(hi - 1) : null;
    var res = { n: hi, at: at.r, before: before ? before.r : P.ZERO, verified: 'double', margin: Math.min(Math.abs(at.margin), before ? Math.abs(before.margin) : 1) };
    // Exact boundary verification with the BigInt engine when the margin is razor-thin.
    if (res.margin < 1e-9 && TK.hp) {
      var chk = TK.hp.verifyBoundary(BigInt(hi), num, den, BigInt(k), target, (TK.config.LIMITS.hpMaxSteps));
      if (chk.ok) {
        res.verified = 'exact';
        if (!chk.passAtN || chk.passBefore) { res.n = chk.correctedN; res.correctedByExact = true; }
      }
    } else res.verified = 'margin';
    return res;
  };

  // Theoretical helpers shared by the tracker
  P.chestsForChance = function (num, den, pct) { // pct: Number in (0,1) -> exact minimal n for P(X>=1) >= pct
    var t = TK.util.parsePercent((pct * 100).toFixed(10));
    return P.minTrials(num, den, 1, t);
  };

  P._stirlerr = stirlerr; // exposed for the test suite
  P._SFERR = SFERR;
  TK.prob = P;

  // ===================== formatting =====================
  var SUP = { '0': '⁰', '1': '¹', '2': '²', '3': '³', '4': '⁴', '5': '⁵', '6': '⁶', '7': '⁷', '8': '⁸', '9': '⁹', '-': '⁻' };
  function sup(n) { return String(n).split('').map(function (c) { return SUP[c] || c; }).join(''); }
  var F = {};
  F.sup = sup;
  F.PRECISION = {
    standard: { label: 'Standard', decimals: 6, sig: 6 },
    detailed: { label: 'Detailed', decimals: 12, sig: 12 },
    scientific: { label: 'Scientific', decimals: 6, sig: 10 },
    high: { label: 'High precision', decimals: 30, sig: 30 }
  };
  // Scientific string from a natural log value (works far below double underflow).
  F.sciFromLog = function (lp, sig) {
    if (lp === -Infinity) return '0';
    var l10 = lp / LN10;
    var e = Math.floor(l10);
    var maxSig = Math.max(2, Math.min(15, 15 - Math.ceil(Math.log10(Math.max(1, Math.abs(l10))))));
    sig = Math.min(sig, maxSig);
    var mant = Math.pow(10, l10 - e);
    var ms = mant.toFixed(sig - 1);
    if (parseFloat(ms) >= 10) { e += 1; ms = (mant / 10).toFixed(sig - 1); }
    return ms + ' × 10' + sup(e);
  };
  F.sci = function (x, sig) {
    if (x === 0) return '0';
    if (!isFinite(x)) return String(x);
    return F.sciFromLog(Math.log(Math.abs(x)), sig).replace(/^/, x < 0 ? '-' : '');
  };
  // Format a probability object. opts: { mode: percent|decimal|sci|oneIn, precision, decimals }
  F.prob = function (pr, opts) {
    opts = opts || {};
    var mode = opts.mode || 'percent';
    var prec = F.PRECISION[opts.precision || 'standard'] || F.PRECISION.standard;
    if (opts.precision === 'scientific') mode = mode === 'oneIn' ? 'oneIn' : 'sci';
    var scale = mode === 'percent' ? 100 : 1, unit = mode === 'percent' ? '%' : '';
    if (mode === 'oneIn') return F.oneIn(pr);
    if (mode === 'sci') {
      if (pr.p <= 0.5) return F.sciFromLog(pr.lp + Math.log(scale), prec.sig) + unit;
      if (pr.q === 0) return (scale === 100 ? '100' : '1') + unit;
      return (scale === 100 ? '100' : '1') + ' − ' + F.sciFromLog(pr.lq + Math.log(scale), prec.sig) + unit;
    }
    var dec = opts.decimals != null ? opts.decimals : prec.decimals;
    dec = Math.min(dec, 12); // double-precision values carry ~15 significant digits; never print noise
    var res = Math.pow(10, -dec);
    if (pr.p <= 0.5) {
      var v = pr.p * scale;
      if (pr.lp === -Infinity) return '0' + (dec ? '.' + '0'.repeat(dec) : '') + unit;
      if (v < 0.5 * res) return '< ' + (dec ? '0.' + '0'.repeat(dec - 1) + '1' : '1') + unit;
      // keep within ~14 significant digits of double precision
      var intDigits = Math.max(1, Math.floor(Math.log10(Math.max(v, 1e-300))) + 1);
      var d2 = Math.min(dec, Math.max(0, 14 - intDigits));
      return TK.util.fmtNum(v, d2) + unit;
    }
    var c = pr.q * scale;
    if (c < 0.5 * res) return '> ' + (scale === 100 ? '99.' : '0.') + '9'.repeat(Math.max(1, dec)) + unit;
    return TK.util.fmtNum(scale - c, dec) + unit;
  };
  // "1 in 137,500" style. For likely events, describes the miss rate instead.
  F.oneIn = function (pr) {
    if (pr.p <= 0) return 'impossible';
    if (pr.q <= 0) return 'certain';
    if (pr.p <= 0.5) {
      var lx = -pr.lp / LN10;
      if (lx > 15) return '1 in ' + F.sciFromLog(-pr.lp, 4);
      var x = Math.exp(-pr.lp);
      return '1 in ' + (x >= 100 ? TK.util.fmtInt(Math.round(x)) : TK.util.fmtNum(x, x >= 10 ? 1 : 2));
    }
    var lm = -pr.lq / LN10;
    if (lm > 15) return 'misses 1 in ' + F.sciFromLog(-pr.lq, 4);
    var y = Math.exp(-pr.lq);
    return 'misses 1 in ' + (y >= 100 ? TK.util.fmtInt(Math.round(y)) : TK.util.fmtNum(y, y >= 10 ? 1 : 2));
  };
  // Exact decimal expansion of num/den with repeat detection. Returns { text, repeating, period }.
  F.rationalDecimal = function (num, den, maxFrac) {
    num = toBig(num); den = toBig(den); maxFrac = maxFrac || 24;
    var neg = (num < 0n) !== (den < 0n); if (num < 0n) num = -num; if (den < 0n) den = -den;
    var ip = num / den, r = num % den, digits = '', seen = new Map(), repStart = -1;
    while (r !== 0n && digits.length < maxFrac) {
      if (seen.has(r)) { repStart = seen.get(r); break; }
      seen.set(r, digits.length);
      r *= 10n; digits += (r / den).toString(); r %= den;
    }
    var exact = r === 0n;
    var res = { int: (neg ? '-' : '') + TK.util.fmtInt(ip), frac: digits, exact: exact, repStart: repStart };
    if (repStart >= 0) { res.period = digits.slice(repStart); res.prefix = digits.slice(0, repStart); }
    res.text = res.int + (digits ? '.' + digits : '') + (!exact && repStart < 0 ? '…' : '');
    if (repStart >= 0) res.text = res.int + '.' + res.prefix + res.period + res.period.repeat(Math.max(0, Math.ceil(6 / res.period.length))).slice(0, 6) + '…';
    return res;
  };
  F.fraction = function (num, den) { return TK.util.fmtInt(toBig(num)) + (toBig(den) === 1n ? '' : '/' + TK.util.fmtInt(toBig(den))); };
  F.expected = function (m, decimals) {
    var e = P.meanExact(m);
    if (e.den === 1n) return TK.util.fmtInt(e.num);
    var d = F.rationalDecimal(e.num, e.den, decimals || 6);
    var whole = e.num / e.den, val = Number(e.num) / Number(e.den);
    if (whole >= 1000000n) return TK.util.fmtInt(whole) + '.' + d.frac.slice(0, 2);
    return TK.util.fmtNum(val, decimals == null ? (val >= 100 ? 2 : 4) : decimals);
  };
  TK.fmt = F;
})(window.TK = window.TK || {});

/* ===================== high-precision engine (also runs inside the worker) ===================== */
function TK_HpFactory(BF) {
  'use strict';
  function fail(reason) { return { ok: false, reason: reason }; }
  function zeroProb(n, num, den) { return BF.fromRatio(den - num, den).pow(n); }
  function step(t, n, j, num, dq) { return t.mulInt((n - j) * num).divInt((j + 1n) * dq); }
  // P(X <= k) by direct summation from 0 (k at or below the mean)
  function lowerDirect(n, num, den, k, maxSteps) {
    if (k > BigInt(maxSteps)) return fail('steps');
    var t = zeroProb(n, num, den), sum = t, dq = den - num;
    for (var j = 0n; j < k; j++) { t = step(t, n, j, num, dq); sum = sum.add(t); }
    return { ok: true, value: sum, steps: Number(k) };
  }
  // P(X >= k) by summing upward from k until negligible (k above the mean)
  function upperDirect(n, num, den, k, maxSteps, stopAt) {
    if (k > n) return { ok: true, value: BF.ZERO, steps: 0 };
    if (k > BigInt(maxSteps)) return fail('steps');
    var dq = den - num, t = zeroProb(n, num, den), steps = 0;
    for (var j = 0n; j < k; j++) { t = step(t, n, j, num, dq); steps++; }
    var sum = t, jj = k, end = stopAt != null ? stopAt : n;
    while (jj < end) {
      t = step(t, n, jj, num, dq); jj++; steps++;
      sum = sum.add(t);
      if (t.isZero() || sum.top() - t.top() > BF.PREC + 12) break;
      if (steps > maxSteps) return fail('steps');
    }
    return { ok: true, value: sum, steps: steps };
  }
  function meanFloor(n, num, den) { return n * num / den; }
  var HP = {};
  HP.zero = function (n, num, den) { return { ok: true, value: zeroProb(n, num, den), steps: 0 }; };
  HP.atLeastOne = function (n, num, den) { var z = zeroProb(n, num, den); return { ok: true, value: BF.ONE.sub(z), comp: z, steps: 0 }; };
  HP.pmf = function (n, num, den, k, maxSteps) {
    if (k < 0n || k > n) return { ok: true, value: BF.ZERO, steps: 0 };
    if (k > BigInt(maxSteps)) return fail('steps');
    var t = zeroProb(n, num, den), dq = den - num;
    for (var j = 0n; j < k; j++) t = step(t, n, j, num, dq);
    return { ok: true, value: t, steps: Number(k) };
  };
  HP.cdf = function (n, num, den, k, maxSteps) {
    if (k < 0n) return { ok: true, value: BF.ZERO, comp: BF.ONE, steps: 0 };
    if (k >= n) return { ok: true, value: BF.ONE, comp: BF.ZERO, steps: 0 };
    if (k <= meanFloor(n, num, den)) { var r = lowerDirect(n, num, den, k, maxSteps); if (r.ok) r.comp = BF.ONE.sub(r.value); return r; }
    var u = upperDirect(n, num, den, k + 1n, maxSteps);
    if (!u.ok) return u;
    return { ok: true, value: BF.ONE.sub(u.value), comp: u.value, steps: u.steps };
  };
  HP.sf = function (n, num, den, k, maxSteps) {
    if (k <= 0n) return { ok: true, value: BF.ONE, comp: BF.ZERO, steps: 0 };
    if (k > n) return { ok: true, value: BF.ZERO, comp: BF.ONE, steps: 0 };
    if (k > meanFloor(n, num, den)) { var u = upperDirect(n, num, den, k, maxSteps); if (u.ok) u.comp = BF.ONE.sub(u.value); return u; }
    var l = lowerDirect(n, num, den, k - 1n, maxSteps);
    if (!l.ok) return l;
    return { ok: true, value: BF.ONE.sub(l.value), comp: l.value, steps: l.steps };
  };
  HP.between = function (n, num, den, a, b, maxSteps) {
    if (a < 0n) a = 0n; if (b > n) b = n;
    if (a > b) return { ok: true, value: BF.ZERO, steps: 0 };
    var mf = meanFloor(n, num, den);
    if (b <= mf || a > mf) {
      // direct sum of t_a..t_b
      if (b > BigInt(maxSteps)) return fail('steps');
      var dq = den - num, t = zeroProb(n, num, den);
      for (var j = 0n; j < a; j++) t = step(t, n, j, num, dq);
      var sum = t, jj = a, steps = Number(a);
      while (jj < b) { t = step(t, n, jj, num, dq); jj++; steps++; sum = sum.add(t); if (jj > mf && (t.isZero() || sum.top() - t.top() > BF.PREC + 12)) break; }
      return { ok: true, value: sum, steps: steps };
    }
    var lo = a > 0n ? lowerDirect(n, num, den, a - 1n, maxSteps) : { ok: true, value: BF.ZERO, steps: 0 };
    if (!lo.ok) return lo;
    var hi = b < n ? upperDirect(n, num, den, b + 1n, maxSteps) : { ok: true, value: BF.ZERO, steps: 0 };
    if (!hi.ok) return hi;
    var tails = lo.value.add(hi.value);
    return { ok: true, value: BF.ONE.sub(tails), comp: tails, steps: lo.steps + hi.steps };
  };
  // Exact check of the reverse-calculator boundary: is P(X >= k | n) >= target and P(X >= k | n-1) < target?
  HP.verifyBoundary = function (n, num, den, k, target, maxSteps) {
    var comp = BF.fromRatio(target.den - target.num, target.den); // 1 - target
    function passes(nn) { var r = HP.cdf(nn, num, den, k - 1n, maxSteps); if (!r.ok) return null; return r.value.cmp(comp) <= 0; } // P(X<k) <= 1-target
    var a = passes(n), b = n > 0n ? passes(n - 1n) : false;
    if (a == null || b == null) return { ok: false };
    var corrected = n;
    if (!a) { var x = n; while (true) { x += 1n; var px = passes(x); if (px == null) return { ok: false }; if (px) break; } corrected = x; }
    else if (b) { var y = n - 1n; while (y > 0n) { var py = passes(y - 1n); if (py == null) return { ok: false }; if (!py) break; y -= 1n; } corrected = y; }
    return { ok: true, passAtN: a, passBefore: b, correctedN: Number(corrected) };
  };
  return HP;
}
(function (TK) { if (typeof window !== 'undefined' && TK.BF) { TK.hp = TK_HpFactory(TK.BF); TK.factories = TK.factories || {}; TK.factories.Hp = TK_HpFactory; } })(typeof window !== 'undefined' ? (window.TK = window.TK || {}) : {});
