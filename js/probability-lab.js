/* TOKENIFY — Probability Lab.
 * THEORETICAL values come from TK.prob (exact binomial model, p = 1/137,500 as a rational).
 * SIMULATED values come only from genuine random draws in the background worker (TK.compute).
 * The two are labelled separately everywhere and never substituted for each other. */
(function (TK) {
  'use strict';
  var U = TK.util, P = TK.prob, F = TK.fmt, R = TK.rewards, h = U.h, S = TK.settings, LIM = TK.config.LIMITS;
  var TOKEN = { num: 1n, den: 137500n, name: 'Pirate Token' };
  var els = {}, n = 1000000n, m = null, running = null, mcRunning = null, lastRun = null, pinned = null, chart, mcChart, exChart;
  var gen = 0; // bumps whenever inputs change; stale high-precision results are discarded
  var rangeSel = { c: 0.95, comp: 0.05, label: '95%' }, revSel = null;

  // ---------------- formatting ----------------
  function prec() { return S.get('precision') || 'standard'; }
  function fmode() { return S.get('probFormat') || 'percent'; }
  function fp(pr) { return F.prob(pr, { mode: fmode(), precision: prec() === 'high' ? 'detailed' : prec() }); }
  function alt(pr) { return F.oneIn(pr) + ' · ' + F.prob(pr, { mode: 'sci', precision: 'scientific' }); }
  function sciStr(s, unit) {
    if (s.zero) return '0';
    if (s.approx) return '10^' + s.log10.toFixed(4);
    return (s.neg ? '-' : '') + s.mant[0] + '.' + s.mant.slice(1) + ' × 10' + F.sup(s.exp) + (unit || '');
  }
  function groupFixed(str) { var p = str.split('.'); return U.groupDigits(p[0]) + (p[1] ? '.' + p[1] : ''); }
  // 30-significant-digit string from a BigFloat value and (optionally) its exact complement
  function hpString(v, comp, mode) {
    var BF = TK.BF, hundred = BF.fromBigInt(100n);
    if (mode === 'oneIn') { if (!v || v.isZero()) return 'impossible'; return '1 in ' + sciStr(BF.ONE.div(v).toSci(16)); }
    var pct = mode === 'percent', scale = pct ? hundred : BF.ONE, unit = pct ? '%' : '', whole = pct ? '100' : '1';
    var nearOne = comp && !comp.isZero() && comp.cmp(BF.fromRatio(1n, 2n)) < 0;
    if (mode === 'sci') return nearOne ? whole + ' − ' + sciStr(comp.mul(scale).toSci(30)) + unit : sciStr(v.mul(scale).toSci(30)) + unit;
    if (!nearOne) {
      var x = v.mul(scale); if (x.isZero()) return '0' + unit;
      var dec = Math.ceil(-x.log10()) + 29; if (dec < 0) dec = 0;
      if (dec > 90) return sciStr(x.toSci(30)) + unit;
      return groupFixed(x.toFixed(dec)) + unit;
    }
    var c = comp.mul(scale), d2 = Math.max(30, Math.ceil(-c.log10()) + 29);
    if (d2 > 90) return whole + ' − ' + sciStr(c.toSci(30)) + unit;
    return groupFixed(scale.sub(c).toFixed(d2)) + unit;
  }
  // Show a probability; in High precision mode, follow up with a 30-digit value from the BigInt engine.
  function show(el, pr, hpReq) {
    if (!el) return;
    el.textContent = fp(pr); el.title = alt(pr); el.classList.remove('hp');
    if (prec() !== 'high' || !hpReq) return;
    var g = gen; el.textContent = fp(pr) + ' …'; el.classList.add('hp-wait');
    TK.compute.runHp(Object.assign({ n: String(hpReq.n != null ? hpReq.n : n), num: String(hpReq.num || TOKEN.num), den: String(hpReq.den || TOKEN.den), maxSteps: LIM.hpMaxSteps }, hpReq)).then(function (r) {
      if (g !== gen) return;
      el.classList.remove('hp-wait');
      if (!r.ok) { el.textContent = fp(pr); el.title = 'High precision unavailable here (' + (r.reason === 'steps' ? 'needs more than ' + U.fmtInt(LIM.hpMaxSteps) + ' series terms' : r.reason) + '); showing double precision.'; return; }
      el.textContent = hpString(r.value, r.comp, fmode()); el.classList.add('hp'); el.title = '30 significant digits, 320-bit exact-rational series';
    });
  }
  function fmtExpected(model, digits) {
    var e = P.meanExact(model);
    if (e.den === 1n) return U.fmtInt(e.num);
    var d = F.rationalDecimal(e.num, e.den, prec() === 'high' ? 32 : (digits || 4) + 2);
    var whole = d.int, frac = d.frac;
    if (prec() === 'high') return whole + '.' + frac + (d.exact ? '' : '…');
    var v = Number(e.num) / Number(e.den), dd = digits != null ? digits : (v >= 1000 ? 2 : 4);
    return U.fmtNum(v, dd) + (d.exact && frac.length <= dd ? '' : '');
  }
  function fracText(model) { var e = P.meanExact(model); return e.den === 1n ? 'exactly ' + U.fmtInt(e.num) : '= ' + U.fmtInt(e.num) + '/' + U.fmtInt(e.den) + ' exactly'; }

  // ---------------- inputs ----------------
  function readN() {
    var r = U.parseCount(els.n.value);
    if (!r.ok) { els.nMsg.textContent = r.empty ? 'Enter how many chests to simulate.' : r.error; els.nMsg.className = 'lab-msg error'; els.n.setAttribute('aria-invalid', 'true'); return null; }
    els.n.removeAttribute('aria-invalid');
    var v = r.value, mode = S.get('labMode'), max = mode === 'full' ? LIM.fullMax : LIM.fastMax, note = '';
    if (v > BigInt(max)) note = 'Random simulation in the browser is limited to ' + U.fmtInt(max) + ' chests in ' + (mode === 'full' ? 'full loot' : 'token') + ' mode — the exact maths below still covers ' + U.fmtInt(v) + '.';
    else if (v > 9007199254740991n) note = 'Beyond 2⁵³ chests: counts stay exact; distribution values use 53-bit arithmetic for n.';
    else if (mode === 'fast' && v > 200000000n) note = 'Large run: every chest is a real random draw, so this takes a few seconds.';
    els.nMsg.textContent = note; els.nMsg.className = 'lab-msg' + (v > BigInt(max) ? ' warn' : '');
    return v;
  }
  function intInput(el, def, max) {
    var r = U.parseCount(el.value);
    if (!r.ok) { el.setAttribute('aria-invalid', 'true'); return null; }
    el.removeAttribute('aria-invalid');
    var v = r.value; if (max != null && v > BigInt(max)) v = BigInt(max);
    return Number(v);
  }
  var recompute = U.debounce(function () { theory(); }, 140);
  function theory() {
    var v = readN(); if (v == null) return;
    n = v; gen++;
    S.set('labN', U.fmtInt(n));
    m = P.model(n, TOKEN.num, TOKEN.den);
    U.setText(els.chN, U.fmtInt(n));
    renderChances(); renderAim(); renderInterval(); renderChart(); renderRanges(); renderExplorer(); renderInspector();
  }

  // ---------------- your chances ----------------
  function renderChances() {
    els.ccExpected.textContent = fmtExpected(m); els.ccExpectedS.textContent = fracText(m) + ' · an average, not a guarantee';
    show(els.ccOne, P.atLeastOne(m), { fn: 'atLeastOne' }); els.ccOneS.textContent = F.oneIn(P.atLeastOne(m));
    show(els.ccZero, P.zero(m), { fn: 'zero' }); els.ccZeroS.textContent = '(137,499/137,500)' + F.sup('n');
    var two = P.sf(m, 2), five = P.sf(m, 5);
    show(els.ccTwo, two, { fn: 'sf', k: 2 }); els.ccTwoS.textContent = F.oneIn(two);
    show(els.ccFive, five, { fn: 'sf', k: 5 }); els.ccFiveS.textContent = F.oneIn(five);
    var gm = P.model(n, 1n, 1562500n);
    els.ccGold.textContent = fmtExpected(gm, 4); els.ccGoldS.textContent = 'uses 1 in 1,562,500 · ' + F.oneIn(P.atLeastOne(gm)) + ' for at least one';
  }
  function renderAim() {
    var k = intInput(els.aimK, 3, 1e15); if (k == null) { els.aimMsg.textContent = 'Enter a whole number of tokens (0 or more).'; return; }
    els.aimMsg.textContent = '';
    S.set('aimK', String(k));
    show(els.aimExact, P.pmf(m, k), { fn: 'pmf', k: k });
    show(els.aimAtLeast, P.sf(m, k), { fn: 'sf', k: k });
    show(els.aimFewer, P.lt(m, k), { fn: 'cdf', k: k - 1 });
    show(els.aimMore, P.gt(m, k), { fn: 'sf', k: k + 1 });
  }
  function renderInterval() {
    var a = intInput(els.ivA, 3), b = intInput(els.ivB, 8);
    if (a == null || b == null) { els.ivOut.textContent = '—'; return; }
    if (a > b) { var t = a; a = b; b = t; }
    S.set('ivA', String(a)); S.set('ivB', String(b));
    show(els.ivOut, P.between(m, a, b), { fn: 'between', a: a, b: b });
    if (chart && chart.data) { chart.data.highlight = { from: a, to: b }; if (chart.shown) chart.shown.highlight = chart.data.highlight; chart.draw(); }
  }

  // ---------------- distribution chart ----------------
  var lastDist = null;
  function obsForN() { return lastRun && lastRun.n === n.toString() ? lastRun.tokens : null; }
  function renderChart() {
    var tails = S.get('distTails'), log = S.get('distLog');
    var a = intInput(els.ivA, 3), b = intInput(els.ivB, 8); if (a != null && b != null && a > b) { var t = a; a = b; b = t; }
    try {
      var maxBins = Math.max(24, Math.min(160, Math.floor(els.distWrap.clientWidth / 6)));
      var obs = obsForN();
      var d = P.distribution(m, { rel: tails ? 1e-40 : 1e-9, maxBins: maxBins, minHi: obs != null ? obs + 1 : null, minLo: obs != null ? Math.max(0, obs - 1) : null });
      lastDist = d;
      var bars = d.bins.map(function (b2) { return { x0: b2.k0, x1: b2.k1, v: b2.p, lp: b2.lp, label: b2.k0 === b2.k1 ? U.fmtInt(b2.k0) + ' tokens' : U.fmtInt(b2.k0) + '–' + U.fmtInt(b2.k1) + ' tokens' }; });
      var iv = P.centralInterval(m, rangeSel.c, rangeSel.comp);
      var markers = [{ x: m.np, label: 'Expected ' + U.fmtNum(m.np, m.np < 100 ? 2 : 0), role: 'mean', color: TK.chartColors.mean, dash: true }];
      if (obs != null) markers.push({ x: obs, label: 'You: ' + U.fmtInt(obs), role: 'obs', color: TK.chartColors.obs, width: 2.5 });
      chart.set({ bars: bars, markers: markers, band: { from: iv.lo, to: iv.hi }, highlight: (a != null && b != null) ? { from: a, to: b } : null, log: log, decades: tails ? 40 : 12, xTitle: 'Pirate Tokens found', obsBar: obs });
      var sub = d.binWidth > 1 ? 'Each bar groups ' + U.fmtInt(d.binWidth) + ' token counts (bars are exact sums). ' : '';
      els.distSub.textContent = sub + 'Shows ' + U.fmtInt(d.lo) + '–' + U.fmtInt(d.hi) + ' tokens, which together hold ' + F.prob(d.inside, { mode: 'percent', precision: 'detailed' }) + ' of all probability.';
      var mode = P.median(m);
      els.distSummary.textContent = 'Probability distribution of Pirate Tokens for ' + U.fmtInt(n) + ' chests. Most likely ' + U.fmtInt(m.mode) + ' tokens (' + F.prob(P.pmf(m, m.mode), { mode: 'percent' }) + '). Median ' + U.fmtInt(mode) + '. Expected ' + U.fmtNum(m.np, 4) + '. ' + rangeSel.label + ' of runs fall between ' + U.fmtInt(iv.lo) + ' and ' + U.fmtInt(iv.hi) + ' tokens.' + (obs != null ? ' Your simulation found ' + U.fmtInt(obs) + '.' : '');
    } catch (e) {
      lastDist = null;
      chart.set({ bars: [], empty: e instanceof P.TooManyTerms ? 'Too many possible counts to draw for this many chests' : 'Distribution unavailable' });
      els.distSub.textContent = e instanceof P.TooManyTerms ? 'The distribution spans more than ' + U.fmtInt(LIM.sumMaxTerms) + ' token counts, which is too many to sum exactly in the browser. Expected values and chances of zero / at least one remain exact.' : '';
    }
  }

  // ---------------- expected ranges ----------------
  function renderRanges() {
    var iv;
    try { iv = P.centralInterval(m, rangeSel.c, rangeSel.comp); } catch (e) { els.rangeText.textContent = 'Too many chests to compute ranges in the browser.'; return; }
    var lo = lastDist ? lastDist.lo : Math.max(0, Math.floor(m.np - 6 * m.sd)), hi = lastDist ? lastDist.hi : Math.ceil(m.np + 6 * m.sd + 3);
    var obs = obsForN(); if (obs != null) { lo = Math.min(lo, obs); hi = Math.max(hi, obs); }
    var span = Math.max(1, hi - lo + 1), pos = function (x) { return ((x - lo + 0.5) / span * 100).toFixed(2) + '%'; };
    els.rvBand.style.left = pos(iv.lo - 0.5); els.rvBand.style.width = ((iv.hi - iv.lo + 1) / span * 100).toFixed(2) + '%';
    els.rvMean.style.left = pos(m.np); els.rvLo.textContent = U.fmtInt(iv.lo); els.rvHi.textContent = U.fmtInt(iv.hi);
    els.rvLo.style.left = pos(iv.lo); els.rvHi.style.left = pos(iv.hi);
    if (obs != null) { els.rvObs.hidden = false; els.rvObs.style.left = pos(obs); els.rvObs.title = 'Your simulation: ' + U.fmtInt(obs); } else els.rvObs.hidden = true;
    els.rangeText.innerHTML = '';
    els.rangeText.append('Under this probability model, about ' + rangeSel.label + ' of independent runs of ' + U.fmtInt(n) + ' chests would find ');
    els.rangeText.appendChild(h('b', { text: iv.lo === iv.hi ? 'exactly ' + U.fmtInt(iv.lo) : 'between ' + U.fmtInt(iv.lo) + ' and ' + U.fmtInt(iv.hi) }));
    els.rangeText.append(' Pirate Tokens.');
    var tail = rangeSel.comp / 2 * 100;
    els.rangeFine.textContent = 'Equal-tailed prediction interval for whole-number token counts: no more than ' + (tail >= 0.1 ? U.fmtNum(tail, tail >= 1 ? 1 : 2) : TK.fmt.sci(tail, 3)) + '% of runs fall below it and no more than that above it, so it actually covers ' + F.prob(iv.coverage, { mode: 'percent', precision: 'detailed' }) + '. It is not a confidence interval — the drop rate is already known exactly.';
    if (chart && chart.data) { chart.data.band = { from: iv.lo, to: iv.hi }; if (chart.shown) chart.shown.band = chart.data.band; chart.draw(); }
  }

  // ---------------- reverse calculator ----------------
  function renderReverse() {
    var k = intInput(els.revK, 1, 1e9); if (k == null) return;
    var t = revSel; if (!t) return;
    S.set('revK', String(k));
    if (k === 0) { setRev('0', 'chests', 'You already have at least 0 tokens.', ''); return; }
    if (t.num === 0n) { setRev('0', 'chests', 'A 0% target is met before opening anything.', ''); return; }
    if (t.num >= t.den) { setRev('Never', '', 'No finite number of chests guarantees a 100% chance — every chest could miss.', ''); return; }
    var res;
    try { res = P.minTrials(TOKEN.num, TOKEN.den, k, t); } catch (e) { setRev('—', '', 'Too large to compute in the browser.', ''); return; }
    if (res.impossible) { setRev('—', '', 'Out of range.', ''); return; }
    var label = k === 1 ? 'Pirate Token' : 'Pirate Tokens';
    setRev(U.fmtInt(res.n), 'chests', 'for a ' + t.text + '% chance of at least ' + U.fmtInt(k) + ' ' + label + ' (' + U.fmtNum(res.n / 137500, 2) + ' × 137,500)',
      'Check: P(≥' + k + ' | ' + U.fmtInt(res.n) + ') = ' + F.prob(res.at, { mode: 'percent', precision: 'detailed' }) + ' ≥ ' + t.text + '%, while P(≥' + k + ' | ' + U.fmtInt(res.n - 1) + ') = ' + F.prob(res.before, { mode: 'percent', precision: 'detailed' }) + '. ' +
      (res.verified === 'exact' ? 'Boundary confirmed with exact 320-bit arithmetic.' : 'Boundary confirmed by a comfortable numerical margin.'));
  }
  function setRev(v, l, s, verify) { els.revN.textContent = v; els.revL.textContent = l; els.revS.textContent = s; els.revVerify.textContent = verify; }

  // ---------------- simulation ----------------
  function specFor(mode, count) {
    if (mode === 'full') { var sm = R.sampler; return { kind: 'full', n: count, T_HI: sm.T_HI, T_LO: sm.T_LO, D_HI: sm.D_HI, D_LO: sm.D_LO, HI_MASK: sm.HI_MASK }; }
    var bp = R.bernoulliParams(TOKEN.num, TOKEN.den); return { kind: 'bernoulli', n: count, limit: bp.limit, thresh: bp.thresh };
  }
  function setRunning(on) {
    els.sim.disabled = on; els.sim.textContent = on ? 'Simulating…' : 'Simulate chests';
    els.progress.hidden = !on; els.again.disabled = on;
    if (on) { els.bar.style.width = '0%'; els.pct.textContent = '0%'; els.count.textContent = ''; }
  }
  async function simulate() {
    if (running) return;
    var v = readN(); if (v == null) { els.n.focus(); return; }
    var mode = S.get('labMode'), max = mode === 'full' ? LIM.fullMax : LIM.fastMax;
    if (v === 0n) { els.nMsg.textContent = 'Zero chests always gives zero tokens — try a bigger number.'; return; }
    if (v > BigInt(max)) { TK.ui.toast('That many chests is beyond the in-browser simulation limit. The exact theory is shown instead.'); theory(); return; }
    theory();
    var count = Number(v);
    setRunning(true);
    var t0 = performance.now();
    running = TK.compute.runSim(specFor(mode, count), function (done, total) {
      var p = total ? done / total : 0;
      els.bar.style.width = (p * 100).toFixed(1) + '%'; els.pct.textContent = (p * 100).toFixed(p < 0.1 ? 1 : 0) + '%';
      els.count.textContent = U.fmtInt(done) + ' / ' + U.fmtInt(total);
    });
    try {
      var r = await running.promise;
      var run = { t: Date.now(), mode: mode, n: String(count), ms: r.ms || Math.round(performance.now() - t0) };
      if (r.kind === 'full') { run.counts = r.counts; run.tokens = r.counts[R.byId['pirate-token'].index]; run.golds = r.counts[R.byId['gold-outfit'].index]; }
      else run.tokens = r.successes;
      lastRun = run;
      TK.store.lab.add(Object.assign({}, run));
      renderResult(run); renderUnusual(run); renderChart(); renderRanges(); renderHistory();
      U.announce('Simulation finished: ' + U.fmtInt(run.tokens) + ' Pirate Tokens in ' + U.fmtInt(count) + ' chests.');
      TK.audio.play(run.tokens > 0 ? 'chime' : 'ui');
    } catch (e) {
      if (!e.cancelled) { TK.ui.toast('The simulation stopped unexpectedly. Please try again.'); console.error(e); }
      else TK.ui.toast('Simulation cancelled — no partial results are shown.');
    } finally { running = null; setRunning(false); }
  }
  function renderResult(run) {
    els.resultEmpty.hidden = true; els.resultBody.hidden = false;
    var rn = BigInt(run.n), rm = P.model(rn, TOKEN.num, TOKEN.den), k = run.tokens;
    els.resTokens.textContent = U.fmtInt(k);
    els.resTokensLabel.textContent = k === 1 ? 'Pirate Token found' : 'Pirate Tokens found';
    els.resN.textContent = U.fmtInt(rn); els.resTime.textContent = '· ' + (run.ms < 1000 ? run.ms + ' ms' : (run.ms / 1000).toFixed(1) + ' s') + (run.mode === 'full' ? ' · full loot' : ' · token mode');
    els.resExpected.textContent = fmtExpected(rm);
    var diff = k - rm.np; els.resDiff.textContent = (diff >= 0 ? '+' : '−') + U.fmtNum(Math.abs(diff), Math.abs(diff) < 100 ? 2 : 0);
    els.resExact.textContent = fp(P.pmf(rm, k)); els.resExact.title = alt(P.pmf(rm, k));
    els.resAtLeast.textContent = fp(P.atLeastOne(rm));
    els.resGoldRow.hidden = run.mode !== 'full';
    els.tableWrap.hidden = run.mode !== 'full';
    if (run.mode === 'full') {
      els.resGold.textContent = U.fmtInt(run.golds);
      var tb = els.table.tBodies[0]; tb.innerHTML = '';
      R.outcomes.forEach(function (o, i) {
        var em = P.meanExact(P.model(rn, o.numB, o.denB));
        tb.appendChild(h('tr', {}, [
          h('td', {}, [h('span', { class: 'rw' }, [TK.ui.img(o, { alt: '', small: true }), o.name])]),
          h('td', { text: o.id === R.ordinary.id ? 'remainder' : '1 in ' + U.fmtInt(o.denB) }),
          h('td', { class: 'num', text: U.fmtInt(run.counts[i]) }),
          h('td', { class: 'num', text: U.fmtNum(Number(em.num) / Number(em.den), Number(em.num) / Number(em.den) < 100 ? 2 : 0) })
        ]));
      });
    }
    els.resultPanel.classList.remove('flash'); void els.resultPanel.offsetWidth; els.resultPanel.classList.add('flash');
    renderCompare();
  }
  function midPercentile(rm, k) { var below = P.lt(rm, k).p, at = P.pmf(rm, k).p; return Math.min(1, Math.max(0, below + at / 2)); }
  function renderUnusual(run) {
    els.unusual.hidden = false;
    var rn = BigInt(run.n), rm = P.model(rn, TOKEN.num, TOKEN.den), k = run.tokens;
    U.$$('.k-ref', els.unusual).forEach(function (s) { s.textContent = U.fmtInt(k); });
    show(els.unExact, P.pmf(rm, k)); show(els.unAtLeast, P.sf(rm, k)); show(els.unAtMost, P.cdf(rm, k));
    var dev = k - rm.np;
    els.unDev.textContent = (dev >= 0 ? '+' : '−') + U.fmtNum(Math.abs(dev), 2) + ' tokens';
    els.unZ.textContent = rm.sd >= 0.5 ? (dev >= 0 ? '+' : '−') + U.fmtNum(Math.abs(dev / rm.sd), 2) + ' SD' : '— (too few expected tokens)';
    var mp = midPercentile(rm, k);
    els.unPct.textContent = (k > rm.np ? 'luckier than ' : 'unluckier than ') + U.fmtNum((k > rm.np ? mp : 1 - mp) * 100, 1) + '%';
    var lo = P.cdf(rm, k).p, hi = P.sf(rm, k).p, two = Math.min(1, 2 * Math.min(lo, hi));
    var up = k > rm.np, v;
    if (k === rm.mode) v = 'Spot on: ' + U.fmtInt(k) + ' is the single most likely number of tokens for this many chests.';
    else if (two >= 0.5) v = 'Your result was close to the mathematical expectation.';
    else if (two >= 0.2) v = up ? 'A little above average — results like this are very common.' : 'A little below average — results like this are very common.';
    else if (two >= 0.05) v = up ? 'You found more tokens than most simulations, but this is still common.' : 'Slightly unlucky, but well within normal variation.';
    else if (two >= 0.01) v = 'This result is uncommon, but possible — ' + (up ? 'a lucky run.' : 'an unlucky run.');
    else if (two >= 0.001) v = up ? 'This was an unusually lucky outcome.' : 'This was an unusually unlucky outcome.';
    else v = 'Exceptionally rare: fewer than 1 in 1,000 runs land this far ' + (up ? 'above' : 'below') + ' the average.';
    els.unVerdict.textContent = v;
    els.gaugeMark.style.left = (mp * 100).toFixed(1) + '%';
    els.resultPanel.classList.toggle('lucky', up && two < 0.05);
  }
  function renderCompare() {
    if (!pinned || !lastRun || pinned === lastRun) { els.compare.hidden = true; return; }
    els.compare.hidden = false; els.compare.innerHTML = '';
    function col(run, label) {
      var rm = P.model(BigInt(run.n), TOKEN.num, TOKEN.den), mp = midPercentile(rm, run.tokens);
      return h('div', { class: 'cmp-col' }, [h('span', { class: 'tag tag-sim sm', text: label }), h('div', { class: 'cmp-v num', text: U.fmtInt(run.tokens) }), h('div', { class: 'muted', text: 'tokens in ' + U.fmtInt(run.n) + ' chests' }), h('div', { class: 'fine', text: 'percentile ' + U.fmtNum(mp * 100, 1) + '%' })]);
    }
    els.compare.append(col(pinned, 'Pinned run'), h('div', { class: 'cmp-vs', text: 'vs' }), col(lastRun, 'Latest run'));
    if (pinned.n !== lastRun.n) els.compare.appendChild(h('p', { class: 'fine', text: 'Different chest counts — compare the percentiles rather than the raw counts.' }));
  }

  // ---------------- Monte Carlo ----------------
  async function runMC() {
    if (mcRunning) return;
    var cn = intInput(els.mcN), ce = intInput(els.mcE);
    if (!cn || !ce) { els.mcMsg.textContent = 'Enter whole numbers for both fields.'; return; }
    if (ce > LIM.mcMaxExperiments) { els.mcMsg.textContent = 'Up to ' + U.fmtInt(LIM.mcMaxExperiments) + ' experiments per run.'; return; }
    if (cn * ce > LIM.mcMaxTotal) { els.mcMsg.textContent = 'That is ' + U.fmtInt(cn * ce) + ' chests in total; the browser limit is ' + U.fmtInt(LIM.mcMaxTotal) + '. Reduce one of the numbers — nothing is silently scaled down.'; return; }
    els.mcMsg.textContent = 'Total work: ' + U.fmtInt(cn * ce) + ' real random chests.';
    S.set('mcN', U.fmtInt(cn)); S.set('mcE', U.fmtInt(ce));
    var bp = R.bernoulliParams(TOKEN.num, TOKEN.den);
    els.mcRun.disabled = true; els.mcProgress.hidden = false;
    mcRunning = TK.compute.runSim({ kind: 'experiments', n: cn, experiments: ce, limit: bp.limit, thresh: bp.thresh }, function (done, total) {
      var p = total ? done / total : 0; els.mcBar.style.width = (p * 100).toFixed(1) + '%'; els.mcPct.textContent = (p * 100).toFixed(0) + '%'; els.mcCount.textContent = U.fmtInt(done) + ' / ' + U.fmtInt(total);
    });
    try { renderMC(await mcRunning.promise); }
    catch (e) { if (!e.cancelled) els.mcMsg.textContent = 'The experiments stopped unexpectedly.'; else els.mcMsg.textContent = 'Cancelled — no partial results are shown.'; }
    finally { mcRunning = null; els.mcRun.disabled = false; els.mcProgress.hidden = true; }
  }
  function renderMC(r) {
    var c = r.counts, E = c.length, sum = 0, mn = Infinity, mx = -Infinity, any = 0;
    c.forEach(function (x) { sum += x; if (x < mn) mn = x; if (x > mx) mx = x; if (x > 0) any++; });
    var avg = sum / E, ss = 0; c.forEach(function (x) { ss += (x - avg) * (x - avg); });
    var sd = E > 1 ? Math.sqrt(ss / (E - 1)) : 0, mm = P.model(BigInt(r.n), TOKEN.num, TOKEN.den), th = P.atLeastOne(mm).p;
    els.mcResults.hidden = false; els.mcStats.innerHTML = '';
    [['Average tokens per experiment', U.fmtNum(avg, 3) + ' (theory ' + U.fmtNum(mm.np, 3) + ')'], ['Minimum / maximum', U.fmtInt(mn) + ' / ' + U.fmtInt(mx)], ['Standard deviation', U.fmtNum(sd, 3) + ' (theory ' + U.fmtNum(mm.sd, 3) + ')'],
     ['Experiments with ≥1 token', U.fmtNum(any / E * 100, 2) + '%'], ['Theoretical P(≥1)', F.prob(P.atLeastOne(mm), { mode: 'percent', precision: 'standard' })], ['Difference', ((any / E - th) * 100 >= 0 ? '+' : '−') + U.fmtNum(Math.abs(any / E - th) * 100, 2) + ' percentage points']]
      .forEach(function (kv) { els.mcStats.appendChild(h('div', {}, [h('dt', { text: kv[0] }), h('dd', { class: 'num', text: kv[1] })])); });
    var lo = mn, hi = mx, width = hi - lo + 1, bw = Math.max(1, Math.ceil(width / 80)), bins = [];
    for (var k = lo; k <= hi; k += bw) bins.push({ x0: k, x1: Math.min(hi, k + bw - 1), v: 0 });
    c.forEach(function (x) { bins[Math.floor((x - lo) / bw)].v++; });
    var dots = bins.map(function (b) { return { x: (b.x0 + b.x1) / 2, v: P.between(mm, b.x0, b.x1).p * E }; });
    mcChart.set({ bars: bins.map(function (b) { return Object.assign(b, { label: (b.x0 === b.x1 ? U.fmtInt(b.x0) : U.fmtInt(b.x0) + '–' + U.fmtInt(b.x1)) + ' tokens' }); }), dots: dots, yCount: true, xTitle: 'Tokens per experiment' }, false);
    els.mcSummary.textContent = E + ' experiments of ' + U.fmtInt(r.n) + ' chests: average ' + U.fmtNum(avg, 3) + ' tokens versus ' + U.fmtNum(mm.np, 3) + ' predicted; ' + U.fmtNum(any / E * 100, 1) + '% found at least one versus ' + U.fmtNum(th * 100, 1) + '% predicted.';
  }

  // ---------------- explorer ----------------
  function explorerItems() {
    var items = R.outcomes.filter(function (o) { return o.id !== R.ordinary.id; }).map(function (o) { return { id: o.id, name: o.name, short: o.short, num: o.numB, den: o.denB, o: o }; });
    var g = R.groups['any-gizmo']; items.splice(6, 0, { id: g.id, name: 'Any Gizmo', short: 'Any Gizmo', num: g.numB, den: g.denB, o: { art: 'assets/gizmos/golden-fireworks.svg', name: 'Any Gizmo' } });
    return items;
  }
  function buildExplorer() {
    var box = els.exPicker; box.innerHTML = '';
    explorerItems().forEach(function (it) {
      var b = h('button', { type: 'button', role: 'radio', class: 'ex-chip', 'aria-checked': String(S.get('explorer') === it.id), 'data-id': it.id }, [TK.ui.img(it.o, { alt: '', small: true }), h('span', { text: it.short })]);
      box.appendChild(b);
    });
    TK.ui.bindRadios(box, 'data-id', function (id) { S.set('explorer', id); renderExplorer(); });
  }
  function renderExplorer() {
    if (!els.exPanel.open) return;
    var it = explorerItems().filter(function (x) { return x.id === S.get('explorer'); })[0] || explorerItems()[1];
    var em = P.model(n, it.num, it.den);
    els.exArt.src = TK.ui.art(it.o); els.exName.textContent = it.name;
    els.exOdds.textContent = '1 in ' + U.fmtInt(it.den) + ' per chest · exactly ' + U.fmtInt(it.num) + '/' + U.fmtInt(it.den);
    els.exStats.innerHTML = '';
    function row(label, el) { els.exStats.appendChild(h('div', {}, [h('dt', { text: label }), el])); }
    row('Expected in ' + U.fmtInt(n) + ' chests', h('dd', { class: 'num', text: fmtExpected(em) }));
    var one = h('dd', { class: 'num' }); row('At least one', one); show(one, P.atLeastOne(em), { fn: 'atLeastOne', num: it.num, den: it.den });
    var zero = h('dd', { class: 'num' }); row('None at all', zero); show(zero, P.zero(em), { fn: 'zero', num: it.num, den: it.den });
    var k = intInput(els.exK, 2) || 0, kq = h('span', {});
    show(kq, P.sf(em, k), { fn: 'sf', k: k, num: it.num, den: it.den });
    els.exKOut.textContent = ''; els.exKOut.append('P(at least ' + U.fmtInt(k) + ') = ', kq);
    var t = U.parsePercent(els.exQ.value);
    if (t.ok && t.num > 0n && t.num < t.den) { var r = P.minTrials(it.num, it.den, 1, t); els.exQOut.textContent = U.fmtInt(r.n) + ' chests for a ' + t.text + '% chance of at least one'; }
    else els.exQOut.textContent = 'Enter a target between 0 and 100%.';
    try {
      var d = P.distribution(em, { rel: 1e-9, maxBins: 70 });
      exChart.set({ bars: d.bins.map(function (b) { return { x0: b.k0, x1: b.k1, v: b.p, label: (b.k0 === b.k1 ? U.fmtInt(b.k0) : U.fmtInt(b.k0) + '–' + U.fmtInt(b.k1)) + ' × ' + it.short }; }), markers: [{ x: em.np, label: 'Expected', role: 'mean', color: TK.chartColors.mean, dash: true }], xTitle: it.short + ' found' });
    } catch (e) { exChart.set({ bars: [], empty: 'Too wide to draw for this many chests' }); }
    els.exSummary.textContent = it.name + ': expected ' + fmtExpected(em) + ' in ' + U.fmtInt(n) + ' chests; ' + F.prob(P.atLeastOne(em), { mode: 'percent' }) + ' chance of at least one.';
  }

  // ---------------- precision inspector ----------------
  function renderInspector() {
    if (!els.precPanel.open) return;
    var box = els.inspector; box.innerHTML = '';
    var k = intInput(els.aimK, 3) || 0;
    var rows = [
      ['Pirate Token per chest', 'p = 1/137,500', P.probFromLog(Math.log(1 / 137500)), { fn: 'pmf', n: '1', k: 1 }],
      ['Zero tokens', '(1 − p)' + F.sup('n') + ' = (137,499/137,500)^' + U.fmtInt(n), P.zero(m), { fn: 'zero' }],
      ['At least one token', '1 − (137,499/137,500)^' + U.fmtInt(n) + '  [computed as −expm1(n·log1p(−p))]', P.atLeastOne(m), { fn: 'atLeastOne' }],
      ['Exactly ' + k + ' tokens', 'C(n,' + k + ') · p^' + k + ' · (1 − p)^(n − ' + k + ')  [Loader saddle-point]', P.pmf(m, k), { fn: 'pmf', k: k }],
      ['At least ' + k + ' tokens', 'Σ P(X = j) for j ≥ ' + k + '  [tail summed away from the mode]', P.sf(m, k), { fn: 'sf', k: k }]
    ];
    var table = h('table', { class: 'data-table insp' }, [h('thead', {}, [h('tr', {}, [h('th', { text: 'Quantity' }), h('th', { text: 'Exact expression' }), h('th', { text: 'Double precision' }), h('th', { text: 'High precision (30 digits)' })])])]);
    var tb = h('tbody'); table.appendChild(tb);
    rows.forEach(function (r) {
      var hpCell = h('td', { class: 'num hp-cell', text: 'computing…' });
      tb.appendChild(h('tr', {}, [h('td', { text: r[0] }), h('td', { class: 'expr', text: r[1] }), h('td', { class: 'num', text: F.prob(r[2], { mode: 'sci', precision: 'scientific' }) }), hpCell]));
      var g = gen;
      TK.compute.runHp(Object.assign({ n: r[3].n || String(n), num: '1', den: '137500', maxSteps: LIM.hpMaxSteps }, r[3])).then(function (res) {
        if (g !== gen) return;
        hpCell.textContent = res.ok ? hpString(res.value, res.comp, 'sci') : 'not available (' + (res.reason === 'steps' ? 'too many terms' : res.reason) + ')';
      });
    });
    var e = P.meanExact(m), v = P.varianceExact(m);
    box.appendChild(h('div', { class: 'table-scroll' }, [table]));
    box.appendChild(h('dl', { class: 'kv kv-2' }, [
      h('div', {}, [h('dt', { text: 'Expected tokens E[X] = n·p' }), h('dd', { class: 'num', text: U.fmtInt(e.num) + (e.den === 1n ? '' : '/' + U.fmtInt(e.den)) })]),
      h('div', {}, [h('dt', { text: 'Variance n·p·(1 − p)' }), h('dd', { class: 'num', text: U.fmtNum(Number(v.num) / Number(v.den), 6) })]),
      h('div', {}, [h('dt', { text: 'Standard deviation' }), h('dd', { class: 'num', text: U.fmtNum(m.sd, 6) })]),
      h('div', {}, [h('dt', { text: 'Most likely count (mode)' }), h('dd', { class: 'num', text: U.fmtInt(m.mode) })])
    ]));
  }
  function renderSelfcheck() {
    var box = els.selfcheck; box.innerHTML = '';
    box.appendChild(h('h3', { class: 'panel-title sm', text: 'Engine self-check' }));
    var ul = h('ul', { class: 'checks' });
    R.validate().forEach(function (c) { ul.appendChild(h('li', { class: c.pass ? 'ok' : 'bad' }, [h('span', { text: c.pass ? '✓' : '✗' }), ' ' + c.label + (c.detail ? ' — ' + c.detail : '')])); });
    box.appendChild(ul);
  }
  function precisionNote() {
    var p = prec();
    els.precNote.textContent = p === 'high'
      ? 'High precision: values are recomputed with 320-bit exact-rational arithmetic (about 96 digits) and shown to 30 significant digits. Very large token counts fall back to double precision with a note.'
      : p === 'scientific' ? 'Scientific: 10 significant digits from the double-precision engine (about 15 reliable digits).'
        : p === 'detailed' ? 'Detailed: 12 decimal places. Values below that resolution are shown as an honest bound (e.g. < 0.000000000001%).'
          : 'Standard: 6 decimal places. Tiny values are shown as a bound; switch to Scientific or High precision to see them.';
  }

  // ---------------- history / export / share ----------------
  function renderHistory() {
    var list = els.history; list.innerHTML = '';
    var runs = TK.store.lab.runs();
    if (!runs.length) { list.appendChild(h('li', { class: 'muted', text: 'No laboratory runs yet.' })); return; }
    runs.slice(0, 30).forEach(function (r) {
      var rm = P.model(BigInt(r.n), TOKEN.num, TOKEN.den);
      list.appendChild(h('li', { class: 'lh' }, [h('span', { class: 'lh-n num', text: U.fmtInt(r.tokens) }), h('span', { class: 'lh-d', text: 'tokens in ' + U.fmtInt(r.n) + ' chests · expected ' + U.fmtNum(rm.np, rm.np < 100 ? 2 : 0) }), h('span', { class: 'lh-t muted', text: (r.mode === 'full' ? 'Full loot · ' : 'Token · ') + U.fmtDate(r.t) })]));
    });
  }
  function exportRuns() {
    var rows = [['timestamp', 'mode', 'chests', 'pirate_tokens', 'expected_tokens', 'gold_outfits', 'milliseconds']];
    TK.store.lab.runs().forEach(function (r) { var rm = P.model(BigInt(r.n), TOKEN.num, TOKEN.den); rows.push([new Date(r.t).toISOString(), r.mode, r.n, r.tokens, rm.np.toFixed(6), r.golds != null ? r.golds : '', r.ms]); });
    U.download('tokenify-lab-runs.csv', U.csv(rows), 'text/csv');
  }
  function exportDist() {
    if (!lastDist) { TK.ui.toast('No distribution to export for this chest count.'); return; }
    var rows = [['tokens_from', 'tokens_to', 'probability', 'cumulative_probability']], cum = P.cdf(m, lastDist.lo - 1).p;
    lastDist.bins.forEach(function (b) { cum += b.p; rows.push([b.k0, b.k1, b.p.toExponential(15), Math.min(1, cum).toExponential(15)]); });
    U.download('tokenify-distribution-' + n + '-chests.csv', U.csv(rows), 'text/csv');
  }
  function shareCard() {
    if (!lastRun) return;
    var c = document.createElement('canvas'); c.width = 1200; c.height = 630; var g = c.getContext('2d');
    var bg = g.createRadialGradient(600, 200, 40, 600, 315, 760); bg.addColorStop(0, '#2a2348'); bg.addColorStop(0.5, '#121a33'); bg.addColorStop(1, '#070b16');
    g.fillStyle = bg; g.fillRect(0, 0, 1200, 630);
    g.save(); g.translate(330, 315); for (var i = 0; i < 48; i++) { g.rotate(Math.PI * 2 / 48); g.fillStyle = i % 2 ? 'rgba(255,214,120,.07)' : 'rgba(255,214,120,.02)'; g.beginPath(); g.moveTo(0, 0); g.lineTo(900, -40); g.lineTo(900, 40); g.closePath(); g.fill(); } g.restore();
    var tok = new Image(); tok.src = 'assets/tokens/pirate-token.webp';
    tok.onload = function () {
      g.drawImage(tok, 90, 75, 480, 480);
      g.fillStyle = '#ffd36b'; g.font = '700 30px "Barlow Condensed", sans-serif'; g.fillText('SIMULATED WITH TOKENIFY', 620, 150);
      g.fillStyle = '#f4ecd9'; g.font = '64px Anton, Impact, sans-serif'; g.fillText(U.fmtInt(lastRun.n) + ' CHESTS', 620, 240);
      g.fillStyle = '#ffe58f'; g.font = '150px Anton, Impact, sans-serif'; g.fillText(U.fmtInt(lastRun.tokens), 620, 400);
      g.fillStyle = '#f4ecd9'; g.font = '46px Anton, Impact, sans-serif'; g.fillText(lastRun.tokens === 1 ? 'PIRATE TOKEN FOUND' : 'PIRATE TOKENS FOUND', 620, 460);
      var rm = P.model(BigInt(lastRun.n), TOKEN.num, TOKEN.den);
      g.fillStyle = '#9aa4bb'; g.font = '600 26px Barlow, sans-serif'; g.fillText('Expected ' + U.fmtNum(rm.np, 2) + ' · exact odds 1 in 137,500 per chest', 620, 520);
      g.fillText('A random simulation, not a real in-game result.', 620, 560);
      try { c.toBlob(function (blob) { var a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'tokenify-result.png'; a.click(); setTimeout(function () { URL.revokeObjectURL(a.href); }, 2000); }, 'image/png'); }
      catch (e) { TK.ui.toast('Your browser blocks image export for local files. Launch TOKENIFY with start.bat to save result cards.', { duration: 7000 }); }
    };
    tok.onerror = function () { TK.ui.toast('Couldn’t load the token artwork for the card.'); };
  }

  // ---------------- init ----------------
  TK.lab = {
    init: function () {
      var ids = { n: 'lab-n', nMsg: 'lab-n-msg', sim: 'btn-sim', progress: 'sim-progress', bar: 'sim-bar', pct: 'sim-pct', count: 'sim-count', cancel: 'sim-cancel',
        resultPanel: 'result-panel', resultEmpty: 'result-empty', resultBody: 'result-body', resTokens: 'res-tokens', resTokensLabel: 'res-tokens-label', resN: 'res-n', resTime: 'res-time',
        resExpected: 'res-expected', resDiff: 'res-diff', resExact: 'res-exact', resAtLeast: 'res-atleast', resGoldRow: 'res-gold-row', resGold: 'res-gold', tableWrap: 'full-table-wrap', table: 'full-table',
        again: 'res-again', pin: 'res-pin', share: 'res-share', compare: 'compare', unusual: 'unusual-panel', unVerdict: 'unusual-verdict', gaugeMark: 'gauge-mark',
        unExact: 'un-exact', unAtLeast: 'un-atleast', unAtMost: 'un-atmost', unDev: 'un-dev', unZ: 'un-z', unPct: 'un-pct',
        chN: 'ch-n', ccExpected: 'cc-expected', ccExpectedS: 'cc-expected-s', ccOne: 'cc-one', ccOneS: 'cc-one-s', ccZero: 'cc-zero', ccZeroS: 'cc-zero-s', ccTwo: 'cc-two', ccTwoS: 'cc-two-s', ccFive: 'cc-five', ccFiveS: 'cc-five-s', ccGold: 'cc-gold', ccGoldS: 'cc-gold-s',
        aimK: 'aim-k', aimMsg: 'aim-msg', aimExact: 'aim-exact', aimAtLeast: 'aim-atleast', aimFewer: 'aim-fewer', aimMore: 'aim-more', ivA: 'iv-a', ivB: 'iv-b', ivOut: 'iv-out',
        distWrap: 'dist-wrap', distCanvas: 'dist-canvas', distTip: 'dist-tip', distSub: 'dist-sub', distSummary: 'dist-summary', distTails: 'dist-tails', distLog: 'dist-log',
        rvBand: 'rv-band', rvMean: 'rv-mean', rvObs: 'rv-obs', rvLo: 'rv-lo', rvHi: 'rv-hi', rangeText: 'range-text', rangeFine: 'range-fine', rangeCustom: 'range-custom',
        revK: 'rev-k', revN: 'rev-n', revL: 'rev-l', revS: 'rev-s', revVerify: 'rev-verify', revCustom: 'rev-custom',
        mcN: 'mc-n', mcE: 'mc-e', mcRun: 'mc-run', mcMsg: 'mc-msg', mcProgress: 'mc-progress', mcBar: 'mc-bar', mcPct: 'mc-pct', mcCount: 'mc-count', mcCancel: 'mc-cancel', mcResults: 'mc-results', mcStats: 'mc-stats', mcCanvas: 'mc-canvas', mcTip: 'mc-tip', mcSummary: 'mc-summary',
        exPanel: 'explorer-panel', exPicker: 'ex-picker', exArt: 'ex-art', exName: 'ex-name', exOdds: 'ex-odds', exStats: 'ex-stats', exK: 'ex-k', exKOut: 'ex-k-out', exQ: 'ex-q', exQOut: 'ex-q-out', exCanvas: 'ex-canvas', exSummary: 'ex-summary',
        precPanel: 'precision-panel', precNote: 'prec-note', inspector: 'inspector', selfcheck: 'selfcheck', history: 'lab-history' };
      Object.keys(ids).forEach(function (k) { els[k] = document.getElementById(ids[k]); });
      // restore inputs
      els.n.value = S.get('labN'); els.aimK.value = S.get('aimK'); els.ivA.value = S.get('ivA'); els.ivB.value = S.get('ivB');
      els.revK.value = S.get('revK'); els.mcN.value = S.get('mcN'); els.mcE.value = S.get('mcE');
      chart = new TK.Chart(els.distCanvas, { tip: els.distTip, keyboard: true, height: 300,
        tipText: function (b) { var single = b.x0 === b.x1, pr = P.probFromLog(b.lp); return b.label + ': ' + F.prob(pr, { mode: 'percent', precision: 'detailed' }) + (single ? ' · ' + F.oneIn(pr) + ' · P(≤' + U.fmtInt(b.x1) + ') = ' + F.prob(P.cdf(m, b.x1), { mode: 'percent' }) : ''); },
        onPick: function (b) { els.aimK.value = String(b.x0); renderAim(); els.aimK.focus(); } });
      mcChart = new TK.Chart(els.mcCanvas, { tip: els.mcTip, height: 240, tipText: function (b) { return b.label + ': ' + U.fmtInt(b.v) + ' experiments'; } });
      exChart = new TK.Chart(els.exCanvas, { height: 200 });
      // events
      U.$('#sim-form').addEventListener('submit', function (e) { e.preventDefault(); simulate(); });
      els.n.addEventListener('input', recompute);
      els.n.addEventListener('blur', function () { var r = U.parseCount(els.n.value); if (r.ok) els.n.value = U.fmtInt(r.value); });
      U.$$('#lab-presets .chip').forEach(function (b) { b.addEventListener('click', function () { els.n.value = U.fmtInt(BigInt(b.getAttribute('data-n'))); theory(); }); });
      var modeSeg = TK.ui.bindRadios(U.$('#lab-mode'), 'data-mode', function (v) { S.set('labMode', v); readN(); });
      modeSeg.set(S.get('labMode'));
      els.cancel.addEventListener('click', function () { if (running) running.cancel(); });
      els.again.addEventListener('click', simulate);
      els.pin.addEventListener('click', function () { if (!lastRun) return; pinned = lastRun; TK.ui.toast('Run pinned. Simulate again to compare.'); renderCompare(); });
      els.share.addEventListener('click', shareCard);
      [els.aimK].forEach(function (el) { el.addEventListener('input', U.debounce(function () { renderAim(); renderInspector(); }, 120)); });
      [els.ivA, els.ivB].forEach(function (el) { el.addEventListener('input', U.debounce(renderInterval, 120)); });
      U.$$('.stepper .step-btn').forEach(function (b) {
        b.addEventListener('click', function () {
          var inp = b.parentElement.querySelector('input'), r = U.parseCount(inp.value), v = r.ok ? Number(r.value) : 0;
          v = Math.max(inp.id === 'rev-k' ? 1 : 0, v + parseInt(b.getAttribute('data-step'), 10)); inp.value = String(v);
          inp.dispatchEvent(new Event('input'));
        });
      });
      els.distTails.setAttribute('aria-pressed', String(!!S.get('distTails'))); els.distLog.setAttribute('aria-pressed', String(!!S.get('distLog')));
      els.distTails.addEventListener('click', function () { var v = !S.get('distTails'); S.set('distTails', v); els.distTails.setAttribute('aria-pressed', String(v)); if (v && !S.get('distLog')) { S.set('distLog', true); els.distLog.setAttribute('aria-pressed', 'true'); } renderChart(); renderRanges(); });
      els.distLog.addEventListener('click', function () { var v = !S.get('distLog'); S.set('distLog', v); els.distLog.setAttribute('aria-pressed', String(v)); renderChart(); });
      // ranges
      var rangeRadios = TK.ui.bindRadios(U.$('#range-chips'), 'data-c', function (v) {
        els.rangeCustom.value = ''; var t = U.parsePercent(v); rangeSel = { c: t.value, comp: Number(t.den - t.num) / Number(t.den), label: v + '%' }; S.set('rangeC', v); renderChart(); renderRanges();
      });
      els.rangeCustom.addEventListener('input', U.debounce(function () {
        var t = U.parsePercent(els.rangeCustom.value);
        if (!t.ok || t.num === 0n || t.num >= t.den) { els.rangeCustom.setAttribute('aria-invalid', els.rangeCustom.value ? 'true' : 'false'); return; }
        els.rangeCustom.removeAttribute('aria-invalid'); rangeRadios.clear();
        rangeSel = { c: t.value, comp: Number(t.den - t.num) / Number(t.den), label: t.text + '%' }; renderChart(); renderRanges();
      }, 250));
      var rc = U.parsePercent(S.get('rangeC') || '95'); rangeSel = { c: rc.value, comp: Number(rc.den - rc.num) / Number(rc.den), label: (S.get('rangeC') || '95') + '%' }; rangeRadios.set(S.get('rangeC') || '95');
      // reverse
      var revRadios = TK.ui.bindRadios(U.$('#rev-chips'), 'data-q', function (v) { els.revCustom.value = ''; revSel = U.parsePercent(v); S.set('revQ', v); renderReverse(); });
      els.revCustom.addEventListener('input', U.debounce(function () {
        var t = U.parsePercent(els.revCustom.value); if (!t.ok) { els.revCustom.setAttribute('aria-invalid', els.revCustom.value ? 'true' : 'false'); return; }
        els.revCustom.removeAttribute('aria-invalid'); revRadios.clear(); revSel = t; renderReverse();
      }, 250));
      els.revK.addEventListener('input', U.debounce(renderReverse, 150));
      revSel = U.parsePercent(S.get('revQ') || '50'); revRadios.set(S.get('revQ') || '50');
      // Monte Carlo
      els.mcRun.addEventListener('click', runMC);
      els.mcCancel.addEventListener('click', function () { if (mcRunning) mcRunning.cancel(); });
      // explorer + precision
      buildExplorer();
      els.exPanel.addEventListener('toggle', renderExplorer);
      [els.exK, els.exQ].forEach(function (el) { el.addEventListener('input', U.debounce(renderExplorer, 150)); });
      var precSeg = TK.ui.bindRadios(U.$('#prec-mode'), 'data-prec', function (v) { S.set('precision', v); precisionNote(); gen++; theory(); if (lastRun) { renderResult(lastRun); renderUnusual(lastRun); } });
      precSeg.set(prec());
      var fmtSeg = TK.ui.bindRadios(U.$('#prec-format'), 'data-fmt', function (v) { S.set('probFormat', v); gen++; theory(); if (lastRun) { renderResult(lastRun); renderUnusual(lastRun); } });
      fmtSeg.set(fmode());
      els.precPanel.addEventListener('toggle', function () { if (els.precPanel.open) { renderInspector(); renderSelfcheck(); } });
      precisionNote();
      // history
      U.$('#lh-export').addEventListener('click', exportRuns);
      U.$('#lh-export-dist').addEventListener('click', exportDist);
      U.$('#lh-clear').addEventListener('click', function () { TK.ui.confirm({ title: 'Clear laboratory history?', message: 'Removes saved Probability Lab runs from this browser. Your interactive collection is not affected.', confirmLabel: 'Clear', danger: true }).then(function (ok) { if (ok) { TK.store.lab.clear(); renderHistory(); } }); });
      U.bus.on('lab-changed', renderHistory);
      U.bus.on('view', function (v) { if (v === 'lab') { chart.resize(); } });
      window.addEventListener('resize', U.debounce(function () { if (TK.app.view() === 'lab') renderChart(); }, 200));
      theory(); renderReverse(); renderHistory();
    }
  };
})(window.TK = window.TK || {});
