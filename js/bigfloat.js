/* TOKENIFY — arbitrary-precision binary floating point on top of BigInt.
 * value = m * 2^e, with |m| normalised to exactly PREC bits (PREC = 320 ≈ 96 decimal digits).
 * Used only for the "High Precision" mode and for exact boundary checks. The factory is
 * also stringified into the background worker, so it must stay self-contained.
 */
function TK_BigFloatFactory() {
  'use strict';
  var PREC = 320;
  var PRECn = BigInt(PREC);

  function bitLen(x) {
    if (x < 0n) x = -x;
    if (x === 0n) return 0;
    var hex = x.toString(16);
    return (hex.length - 1) * 4 + (32 - Math.clz32(parseInt(hex[0], 16)));
  }

  function BF(m, e) { this.m = m; this.e = e; }

  BF.norm = function (m, e) {
    if (m === 0n) return BF.ZERO;
    var neg = m < 0n, a = neg ? -m : m;
    var bl = bitLen(a);
    if (bl > PREC) {
      var sh = bl - PREC, shn = BigInt(sh);
      a = (a + (1n << (shn - 1n))) >> shn; // round half up
      e += sh;
      if (bitLen(a) > PREC) { a >>= 1n; e += 1; }
    } else if (bl < PREC) {
      var up = PREC - bl;
      a <<= BigInt(up); e -= up;
    }
    return new BF(neg ? -a : a, e);
  };
  BF.ZERO = new BF(0n, 0);
  BF.fromBigInt = function (n) { return BF.norm(BigInt(n), 0); };
  BF.ONE = BF.fromBigInt(1n);
  BF.fromRatio = function (num, den) { return BF.fromBigInt(BigInt(num)).divInt(BigInt(den)); };
  BF.fromNumber = function (x) {
    if (x === 0) return BF.ZERO;
    if (!isFinite(x)) throw new Error('BF.fromNumber: non-finite');
    var neg = x < 0; x = Math.abs(x);
    var e = 0;
    while (x !== Math.floor(x)) { x *= 2; e--; }   // exact: doubles are dyadic rationals
    while (x >= 9007199254740992) { x /= 2; e++; }
    return BF.norm(neg ? -BigInt(x) : BigInt(x), e);
  };

  BF.prototype.isZero = function () { return this.m === 0n; };
  BF.prototype.sign = function () { return this.m === 0n ? 0 : (this.m < 0n ? -1 : 1); };
  BF.prototype.neg = function () { return new BF(-this.m, this.e); };
  BF.prototype.abs = function () { return this.m < 0n ? this.neg() : this; };
  BF.prototype.mul = function (b) { if (this.m === 0n || b.m === 0n) return BF.ZERO; return BF.norm(this.m * b.m, this.e + b.e); };
  BF.prototype.mulInt = function (k) { if (this.m === 0n || k === 0n) return BF.ZERO; return BF.norm(this.m * k, this.e); };
  BF.prototype.divInt = function (k) {
    if (k === 0n) throw new Error('BF divide by zero');
    if (this.m === 0n) return BF.ZERO;
    var sh = PREC + bitLen(k) + 2;
    return BF.norm((this.m << BigInt(sh)) / k, this.e - sh);
  };
  BF.prototype.div = function (b) {
    if (b.m === 0n) throw new Error('BF divide by zero');
    if (this.m === 0n) return BF.ZERO;
    var sh = PREC + 4;
    return BF.norm((this.m << BigInt(sh)) / b.m, this.e - b.e - sh);
  };
  BF.prototype.top = function () { return this.m === 0n ? -Infinity : this.e + PREC; }; // ~ floor(log2|x|)+1
  BF.prototype.add = function (b) {
    if (this.m === 0n) return b;
    if (b.m === 0n) return this;
    var x = this, y = b;
    if (x.top() < y.top()) { var t = x; x = y; y = t; }
    if (x.top() - y.top() > PREC + 16) return x; // y below the last kept bit
    var d = x.e - y.e; // >= -16 when tops are close; normalise so we shift the larger-exponent one
    if (d >= 0) return BF.norm((x.m << BigInt(d)) + y.m, y.e);
    return BF.norm(x.m + (y.m << BigInt(-d)), x.e);
  };
  BF.prototype.sub = function (b) { return this.add(b.neg()); };
  BF.prototype.cmp = function (b) { return this.sub(b).sign(); };
  BF.prototype.pow = function (n) { // n: BigInt >= 0, binary exponentiation
    n = BigInt(n);
    var result = BF.ONE, base = this;
    while (n > 0n) {
      if (n & 1n) result = result.mul(base);
      n >>= 1n;
      if (n > 0n) base = base.mul(base);
    }
    return result;
  };
  // natural/base-10 log as a double (fine for display of astronomically small values)
  BF.prototype.log2 = function () {
    if (this.m <= 0n) return this.m === 0n ? -Infinity : NaN;
    var top = Number(this.m >> (PRECn - 60n)); // 60 leading bits
    return Math.log2(top) + this.e + (PREC - 60);
  };
  BF.prototype.log10 = function () { return this.log2() * 0.30102999566398119521; };
  BF.prototype.toNumber = function () {
    if (this.m === 0n) return 0;
    var l2 = this.log2 ? this.abs().log2() : 0;
    if (l2 > 1100) return this.m < 0n ? -Infinity : Infinity;
    if (l2 < -1100) return 0;
    var top = Number(this.m >> (PRECn - 64n));
    return top * Math.pow(2, this.e + (PREC - 64));
  };

  function pow10(k) { return 10n ** BigInt(k); }
  // round(|x| * 2^e) for integer x >= 0
  function shiftRound(x, e) {
    if (e >= 0) return x << BigInt(e);
    var s = BigInt(-e);
    return (x + (1n << (s - 1n))) >> s;
  }
  // round(x * 2^e / d)
  function divRound(x, e, d) {
    if (e >= 0) { var num = x << BigInt(e); return (num + d / 2n) / d; }
    var den = d << BigInt(-e);
    return (x + den / 2n) / den;
  }

  // Correctly rounded scientific representation: { mant: "123456", exp } meaning 1.23456 × 10^exp.
  // Returns { approx:true, log10 } when the decimal exponent is astronomically large.
  BF.prototype.toSci = function (digits) {
    if (this.m === 0n) return { zero: true, mant: '0'.repeat(digits), exp: 0 };
    var neg = this.m < 0n, a = neg ? this.neg() : this;
    var l10 = a.log10();
    var k = Math.floor(l10);
    if (!isFinite(k) || Math.abs(k) > 60000) return { neg: neg, approx: true, log10: l10 };
    for (var attempt = 0; attempt < 4; attempt++) {
      var s = digits - 1 - k, q;
      if (s >= 0) q = shiftRound(a.m * pow10(s), a.e);
      else q = divRound(a.m, a.e, pow10(-s));
      var str = q.toString();
      if (str.length === digits) return { neg: neg, mant: str, exp: k };
      if (str.length > digits) k += 1; else k -= 1;
    }
    return { neg: neg, approx: true, log10: l10 };
  };
  // Correctly rounded fixed-point string with `decimals` digits after the point.
  BF.prototype.toFixed = function (decimals) {
    var neg = this.m < 0n, a = neg ? this.neg() : this;
    if (a.m === 0n) return (decimals > 0 ? '0.' + '0'.repeat(decimals) : '0');
    if (a.log10() < -decimals - 2) return (neg ? '-' : '') + (decimals > 0 ? '0.' + '0'.repeat(decimals) : '0');
    var q = shiftRound(a.m * pow10(decimals), a.e).toString();
    if (decimals > 0) {
      while (q.length <= decimals) q = '0' + q;
      q = q.slice(0, q.length - decimals) + '.' + q.slice(q.length - decimals);
    }
    return (neg ? '-' : '') + q;
  };

  BF.PREC = PREC;
  BF.bitLen = bitLen;
  return BF;
}
(function (TK) { if (typeof window !== 'undefined') { TK.BF = TK_BigFloatFactory(); TK.factories = TK.factories || {}; TK.factories.BigFloat = TK_BigFloatFactory; } })(typeof window !== 'undefined' ? (window.TK = window.TK || {}) : {});
