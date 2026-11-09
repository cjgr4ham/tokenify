/* Minimal in-browser test runner for TOKENIFY. Open tests/index.html. */
window.T = (function () {
  'use strict';
  var tests = [];
  function test(group, name, fn) { tests.push({ group: group, name: name, fn: fn }); }
  function assert(cond, msg) { if (!cond) throw new Error(msg || 'assertion failed'); }
  function close(a, b, rel, msg) {
    var d = Math.abs(a - b), s = Math.max(Math.abs(a), Math.abs(b));
    if (!(d <= rel * s) && !(a === b)) throw new Error((msg ? msg + ': ' : '') + 'expected ' + b + ', got ' + a + ' (rel diff ' + (s ? (d / s).toExponential(3) : d) + ')');
  }
  // Statistics helpers for goodness-of-fit tests
  function gammln(x) {
    var c = [76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5];
    var y = x, tmp = x + 5.5; tmp -= (x + 0.5) * Math.log(tmp);
    var ser = 1.000000000190015; for (var j = 0; j < 6; j++) ser += c[j] / ++y;
    return -tmp + Math.log(2.5066282746310005 * ser / x);
  }
  function gammq(a, x) { // regularized upper incomplete gamma Q(a, x)
    if (x <= 0) return 1;
    if (x < a + 1) {
      var ap = a, sum = 1 / a, del = sum;
      for (var n = 0; n < 1000; n++) { ap++; del *= x / ap; sum += del; if (Math.abs(del) < Math.abs(sum) * 1e-15) break; }
      return 1 - sum * Math.exp(-x + a * Math.log(x) - gammln(a));
    }
    var b = x + 1 - a, c = 1e300, d = 1 / b, h = d;
    for (var i = 1; i < 1000; i++) {
      var an = -i * (i - a); b += 2; d = an * d + b; if (Math.abs(d) < 1e-300) d = 1e-300;
      c = b + an / c; if (Math.abs(c) < 1e-300) c = 1e-300; d = 1 / d; var dl = d * c; h *= dl; if (Math.abs(dl - 1) < 1e-15) break;
    }
    return Math.exp(-x + a * Math.log(x) - gammln(a)) * h;
  }
  function chiSquare(observed, expected) {
    var chi = 0; for (var i = 0; i < observed.length; i++) chi += Math.pow(observed[i] - expected[i], 2) / expected[i];
    var df = observed.length - 1;
    return { chi2: chi, df: df, p: gammq(df / 2, chi / 2) };
  }

  async function run(filter) {
    var out = document.getElementById('results'), sum = document.getElementById('summary');
    var passed = 0, failed = 0, failures = [], group = null, t0 = performance.now();
    for (var i = 0; i < tests.length; i++) {
      var t = tests[i];
      if (filter && t.group.indexOf(filter) < 0) continue;
      if (t.group !== group) { group = t.group; var gh = document.createElement('h2'); gh.textContent = group; out.appendChild(gh); }
      var row = document.createElement('div'); row.className = 'row running'; row.textContent = '… ' + t.name; out.appendChild(row);
      await new Promise(function (r) { setTimeout(r, 0); });
      var s = performance.now(), err = null, note = '';
      try { var ret = await t.fn(); if (typeof ret === 'string') note = ret; } catch (e) { err = e; }
      var ms = (performance.now() - s).toFixed(0);
      if (err) { failed++; failures.push(t.group + ' › ' + t.name + ': ' + err.message); row.className = 'row fail'; row.textContent = '✗ ' + t.name + '  (' + ms + ' ms) — ' + err.message; }
      else { passed++; row.className = 'row pass'; row.textContent = '✓ ' + t.name + '  (' + ms + ' ms)' + (note ? ' — ' + note : ''); }
    }
    var total = ((performance.now() - t0) / 1000).toFixed(1);
    sum.textContent = passed + ' passed, ' + failed + ' failed (' + total + ' s)';
    sum.className = failed ? 'fail' : 'pass';
    window.__testSummary = { passed: passed, failed: failed, failures: failures, seconds: +total };
    return window.__testSummary;
  }
  return { test: test, assert: assert, close: close, run: run, chiSquare: chiSquare, gammq: gammq };
})();
