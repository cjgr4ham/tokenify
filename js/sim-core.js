/* TOKENIFY — simulation core (runs in a Web Worker built from these functions' source; falls back
 * to time-sliced main-thread execution if workers are unavailable, e.g. some file:// setups).
 *
 * Jobs perform genuine random draws with crypto.getRandomValues — one accepted draw per chest:
 *  - "bernoulli": counts successes of an exact Bernoulli(num/den) per chest (token fast mode).
 *    32-bit word u: reject u >= k*den (k = floor(2^32/den)); success iff u < num*k. Exact.
 *  - "full": samples the complete reward table using the same integer thresholds as TK.rewards.
 *  - "experiments": repeats a bernoulli job E times and records each experiment's count.
 */
function TK_SimFactory() {
  'use strict';
  var WORDS = 16384; // 64 KiB, the getRandomValues limit

  function BernoulliJob(spec) {
    this.kind = 'bernoulli';
    this.total = spec.n; this.done = 0; this.successes = 0;
    this.limit = spec.limit; this.thresh = spec.thresh;
    this.buf = new Uint32Array(WORDS); this.pos = WORDS; this.words = 0;
    this.finished = this.total === 0;
  }
  BernoulliJob.prototype.run = function (budget) {
    var end = Math.min(this.total, this.done + budget), i = this.done, s = 0;
    var buf = this.buf, len = WORDS, pos = this.pos, limit = this.limit, thresh = this.thresh;
    while (i < end) {
      if (pos >= len) { crypto.getRandomValues(buf); pos = 0; }
      var take = len - pos; if (take > end - i) take = end - i;
      var stop = pos + take, rej = 0;
      for (var q = pos; q < stop; q++) { var u = buf[q]; if (u < thresh) s++; else if (u >= limit) rej++; }
      this.words += take; pos = stop; i += take - rej;
    }
    this.pos = pos; this.done = i; this.successes += s;
    if (this.done >= this.total) this.finished = true;
  };
  BernoulliJob.prototype.result = function () { return { kind: 'bernoulli', n: this.total, successes: this.successes, words: this.words }; };

  function FullJob(spec) {
    this.kind = 'full';
    this.total = spec.n; this.done = 0;
    this.T_HI = spec.T_HI; this.T_LO = spec.T_LO; this.D_HI = spec.D_HI; this.D_LO = spec.D_LO; this.mask = spec.HI_MASK;
    this.nSpecial = spec.T_HI.length - 1;
    this.counts = new Float64Array(spec.T_HI.length);
    this.buf = new Uint32Array(WORDS); this.pos = WORDS; this.rejections = 0;
    this.finished = this.total === 0;
  }
  FullJob.prototype.run = function (budget) {
    var end = Math.min(this.total, this.done + budget), i = this.done;
    var buf = this.buf, pos = this.pos, mask = this.mask, DH = this.D_HI, DL = this.D_LO;
    var TH = this.T_HI, TL = this.T_LO, ns = this.nSpecial, SH = TH[ns - 1], SL = TL[ns - 1];
    var counts = this.counts, ordinary = 0, rej = 0;
    while (i < end) {
      if (pos >= WORDS - 1) { crypto.getRandomValues(buf); pos = 0; }
      var hi = (buf[pos] & mask) >>> 0, lo = buf[pos + 1]; pos += 2;
      if (hi > DH || (hi === DH && lo >= DL)) { rej++; continue; }
      i++;
      if (hi > SH || (hi === SH && lo >= SL)) { ordinary++; continue; }
      for (var j = 0; j < ns; j++) { if (hi < TH[j] || (hi === TH[j] && lo < TL[j])) { counts[j]++; break; } }
    }
    counts[ns] += ordinary;
    this.pos = pos; this.done = i; this.rejections += rej;
    if (this.done >= this.total) this.finished = true;
  };
  FullJob.prototype.result = function () { return { kind: 'full', n: this.total, counts: Array.prototype.slice.call(this.counts), rejections: this.rejections }; };

  function ExperimentsJob(spec) {
    this.kind = 'experiments';
    this.E = spec.experiments; this.n = spec.n; this.total = spec.experiments * spec.n; this.done = 0;
    this.limit = spec.limit; this.thresh = spec.thresh;
    this.results = new Float64Array(this.E); this.e = 0; this.cur = null;
    this.finished = this.total === 0;
    if (this.n === 0) { this.finished = true; this.e = this.E; }
  }
  ExperimentsJob.prototype.run = function (budget) {
    var left = budget;
    while (left > 0 && this.e < this.E) {
      if (!this.cur) this.cur = new BernoulliJob({ n: this.n, limit: this.limit, thresh: this.thresh });
      var before = this.cur.done;
      this.cur.run(left);
      var did = this.cur.done - before; left -= did; this.done += did;
      if (this.cur.finished) { this.results[this.e++] = this.cur.successes; this.cur = null; }
    }
    if (this.e >= this.E) this.finished = true;
  };
  ExperimentsJob.prototype.result = function () { return { kind: 'experiments', n: this.n, experiments: this.E, counts: Array.prototype.slice.call(this.results) }; };

  return {
    create: function (spec) {
      if (spec.kind === 'bernoulli') return new BernoulliJob(spec);
      if (spec.kind === 'full') return new FullJob(spec);
      if (spec.kind === 'experiments') return new ExperimentsJob(spec);
      throw new Error('unknown job ' + spec.kind);
    }
  };
}

function TK_WorkerMain(self, BF, HP, SIM) {
  'use strict';
  function pack(bf) { return bf ? { m: bf.m, e: bf.e } : null; }
  self.onmessage = function (ev) {
    var msg = ev.data;
    try {
      if (msg.type === 'sim') {
        var job = SIM.create(msg.spec), last = Date.now(), t0 = last;
        self.postMessage({ type: 'progress', id: msg.id, done: 0, total: job.total });
        while (!job.finished) {
          job.run(1 << 21);
          var now = Date.now();
          if (now - last > 90) { self.postMessage({ type: 'progress', id: msg.id, done: job.done, total: job.total, ms: now - t0 }); last = now; }
        }
        var r = job.result(); r.ms = Date.now() - t0;
        self.postMessage({ type: 'result', id: msg.id, result: r });
      } else if (msg.type === 'hp') {
        var q = msg.req, res;
        var n = BigInt(q.n), num = BigInt(q.num), den = BigInt(q.den), ms = q.maxSteps || 2500000;
        if (q.fn === 'zero') res = HP.zero(n, num, den);
        else if (q.fn === 'atLeastOne') res = HP.atLeastOne(n, num, den);
        else if (q.fn === 'pmf') res = HP.pmf(n, num, den, BigInt(q.k), ms);
        else if (q.fn === 'cdf') res = HP.cdf(n, num, den, BigInt(q.k), ms);
        else if (q.fn === 'sf') res = HP.sf(n, num, den, BigInt(q.k), ms);
        else if (q.fn === 'between') res = HP.between(n, num, den, BigInt(q.a), BigInt(q.b), ms);
        else throw new Error('unknown hp fn ' + q.fn);
        self.postMessage({ type: 'hp', id: msg.id, ok: res.ok, reason: res.reason, value: pack(res.value), comp: pack(res.comp), steps: res.steps });
      }
    } catch (err) {
      self.postMessage({ type: 'error', id: msg.id, message: String(err && err.message || err) });
    }
  };
}

/* ---------------- main-thread host ---------------- */
(function (TK) {
  if (typeof window === 'undefined') return;
  'use strict';
  TK.factories = TK.factories || {};
  TK.factories.Sim = TK_SimFactory;
  var SIM = TK_SimFactory();
  var workerUrl = null, workerOk = null;

  function source() {
    return '"use strict";\nvar BF = (' + TK_BigFloatFactory.toString() + ')();\n' +
      'var HP = (' + TK_HpFactory.toString() + ')(BF);\n' +
      'var SIM = (' + TK_SimFactory.toString() + ')();\n' +
      '(' + TK_WorkerMain.toString() + ')(self, BF, HP, SIM);\n';
  }
  function makeWorker() {
    if (workerOk === false) return null;
    try {
      if (!workerUrl) workerUrl = URL.createObjectURL(new Blob([source()], { type: 'text/javascript' }));
      var w = new Worker(workerUrl);
      workerOk = true;
      return w;
    } catch (e) {
      workerOk = false;
      console.warn('[tokenify] Web Worker unavailable, using time-sliced main thread.', e);
      return null;
    }
  }

  var nextId = 1;
  // Run a simulation job. Returns { promise, cancel }. onProgress(done, total).
  function runSim(spec, onProgress) {
    var id = nextId++, cancelled = false, worker = makeWorker(), rejectFn;
    var promise = new Promise(function (resolve, reject) {
      rejectFn = reject;
      if (worker) {
        worker.onmessage = function (ev) {
          var m = ev.data; if (m.id !== id) return;
          if (m.type === 'progress') { if (onProgress) onProgress(m.done, m.total, m.ms); }
          else if (m.type === 'result') { worker.terminate(); resolve(m.result); }
          else if (m.type === 'error') { worker.terminate(); reject(new Error(m.message)); }
        };
        worker.onerror = function (e) {
          // a worker that cannot start (e.g. blocked blob) -> fall back once
          e.preventDefault && e.preventDefault();
          worker.terminate(); workerOk = false;
          if (!cancelled) runSliced(spec, onProgress, function () { return cancelled; }).then(resolve, reject);
        };
        worker.postMessage({ type: 'sim', id: id, spec: spec });
      } else {
        runSliced(spec, onProgress, function () { return cancelled; }).then(resolve, reject);
      }
    });
    return {
      promise: promise,
      cancel: function () {
        cancelled = true;
        if (worker) worker.terminate();
        if (rejectFn) rejectFn(Object.assign(new Error('cancelled'), { cancelled: true }));
      },
      usingWorker: !!worker
    };
  }
  function runSliced(spec, onProgress, isCancelled) {
    return new Promise(function (resolve, reject) {
      var job = SIM.create(spec), budget = 1 << 18, t0 = Date.now();
      function tick() {
        if (isCancelled()) return reject(Object.assign(new Error('cancelled'), { cancelled: true }));
        var s = performance.now();
        job.run(budget);
        var el = performance.now() - s;
        budget = Math.max(1 << 14, Math.min(1 << 24, Math.floor(budget * (12 / Math.max(1, el)))));
        if (onProgress) onProgress(job.done, job.total, Date.now() - t0);
        if (job.finished) { var r = job.result(); r.ms = Date.now() - t0; resolve(r); }
        else setTimeout(tick, 0);
      }
      setTimeout(tick, 0);
    });
  }

  // High-precision requests run in their own short-lived worker.
  function runHp(req) {
    var id = nextId++, worker = makeWorker();
    var timeoutMs = 60000;
    return new Promise(function (resolve) {
      function finish(m) {
        var unpack = function (x) { return x ? new TK.BF(x.m, x.e) : null; };
        resolve({ ok: m.ok, reason: m.reason, value: unpack(m.value), comp: unpack(m.comp), steps: m.steps });
      }
      if (!worker) {
        // main-thread fallback (bounded by maxSteps)
        setTimeout(function () {
          try {
            var n = BigInt(req.n), num = BigInt(req.num), den = BigInt(req.den), ms = Math.min(req.maxSteps || 400000, 400000), r;
            var H = TK.hp;
            if (req.fn === 'zero') r = H.zero(n, num, den);
            else if (req.fn === 'atLeastOne') r = H.atLeastOne(n, num, den);
            else if (req.fn === 'pmf') r = H.pmf(n, num, den, BigInt(req.k), ms);
            else if (req.fn === 'cdf') r = H.cdf(n, num, den, BigInt(req.k), ms);
            else if (req.fn === 'sf') r = H.sf(n, num, den, BigInt(req.k), ms);
            else r = H.between(n, num, den, BigInt(req.a), BigInt(req.b), ms);
            resolve({ ok: r.ok, reason: r.reason, value: r.value, comp: r.comp, steps: r.steps });
          } catch (e) { resolve({ ok: false, reason: String(e.message || e) }); }
        }, 0);
        return;
      }
      var timer = setTimeout(function () { worker.terminate(); resolve({ ok: false, reason: 'timeout' }); }, timeoutMs);
      worker.onmessage = function (ev) {
        var m = ev.data; if (m.id !== id) return;
        clearTimeout(timer); worker.terminate();
        if (m.type === 'error') resolve({ ok: false, reason: m.message }); else finish(m);
      };
      worker.onerror = function () { clearTimeout(timer); worker.terminate(); resolve({ ok: false, reason: 'worker-error' }); };
      worker.postMessage({ type: 'hp', id: id, req: req });
    });
  }

  TK.compute = { runSim: runSim, runHp: runHp, runSliced: runSliced, sim: SIM, workerAvailable: function () { return workerOk !== false; } };
})(typeof window !== 'undefined' ? (window.TK = window.TK || {}) : {});
