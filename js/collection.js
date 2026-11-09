/* TOKENIFY — Collection: rewards from interactive openings only (never from the Probability Lab). */
(function (TK) {
  'use strict';
  var U = TK.util, cfg = TK.config, R = TK.rewards, h = U.h, P = TK.prob, F = TK.fmt;
  var els = {};
  function tierColor(o) { return o.tier === 'uncommon' && o.accent ? o.accent : cfg.TIERS[o.tier].color; }
  function pctText(num, den) {
    var d = F.rationalDecimal(BigInt(num) * 100n, BigInt(den), 14);
    if (d.exact && d.frac.length <= 10) return d.int + (d.frac ? '.' + d.frac : '') + '%';
    var v = Number(num) / Number(den) * 100; return '≈' + v.toFixed(Math.max(0, 3 - Math.floor(Math.log10(v)))) + '%';
  }
  function render() {
    var st = TK.store.interactive, d = st.data();
    var items = R.outcomes.filter(function (o) { return o.id !== R.ordinary.id; });
    var found = items.filter(function (o) { return st.count(o.id) > 0; }).length;
    U.setText(els.found, String(found));
    els.ring.style.setProperty('--p', (found / items.length * 100).toFixed(1));
    U.setText(els.sub, d.total ? 'From ' + U.fmtInt(d.total) + ' chests you opened yourself. Cosmetics can be owned once; Gizmos stack.' : 'Open chests on the Open Chests tab to start your collection. Cosmetics can be owned once; Gizmos stack.');
    // cards
    els.grid.innerHTML = '';
    items.forEach(function (o, i) {
      var n = st.count(o.id), owned = n > 0, color = tierColor(o);
      var status = owned ? (o.unique ? 'Owned ✓' : '×' + U.fmtInt(n)) : 'Not found yet';
      var meta = owned ? (o.unique ? 'Found on chest #' + U.fmtInt(d.first[o.id]) : 'First on chest #' + U.fmtInt(d.first[o.id])) : (d.total ? U.fmtInt(d.total) + ' chests opened without one' : '');
      var art = TK.ui.img(o, { alt: owned ? o.name : 'Undiscovered: ' + o.name, small: true });
      var card = h('article', { class: 'ccard tier-' + o.tier + (owned ? ' owned' : ' locked') + (cfg.TIERS[o.tier].order >= 4 ? ' prestige' : ''), style: { '--tc': color, animationDelay: (i * 35) + 'ms' }, tabindex: '0', 'aria-label': o.name + ', ' + status }, [
        h('div', { class: 'ccard-art' }, [art, owned ? null : h('span', { class: 'ccard-q', 'aria-hidden': 'true', text: '?' })]),
        h('div', { class: 'ccard-body' }, [
          h('span', { class: 'rarity-chip tier-' + o.tier, text: o.group ? 'Gizmo' : cfg.TIERS[o.tier].label }),
          h('h3', { class: 'ccard-name', text: o.name }),
          h('div', { class: 'ccard-odds num', text: '1 in ' + U.fmtInt(o.denB) + ' · ' + pctText(o.numB, o.denB) }),
          h('div', { class: 'ccard-status' + (owned ? ' ok' : ''), text: status }),
          meta ? h('div', { class: 'ccard-meta', text: meta }) : null,
          owned && o.unique ? h('div', { class: 'ccard-note', text: 'Owned — further rolls of this become Ordinary Loot.' }) : null
        ])
      ]);
      card.title = o.blurb || '';
      els.grid.appendChild(card);
    });
    // ordinary
    var ord = st.count(R.ordinary.id);
    U.setText(els.ordCount, U.fmtInt(ord));
    U.setText(els.ordShare, d.total ? U.fmtNum(ord / d.total * 100, 2) + '% of your openings' : 'of your openings');
    var eff = R.effectiveProbs(st.ownedMap())[R.ordinary.id];
    var conv = st.convertedTotal();
    els.ordOdds.textContent = 'Right now ' + pctText(eff.num, eff.den) + ' of your chests are Ordinary Loot (the exact remainder' + (Object.keys(st.ownedMap()).length ? ', including the share of cosmetics you already own' : '') + ').' + (conv ? ' ' + U.fmtInt(conv) + (conv === 1 ? ' roll' : ' rolls') + ' of owned items became Ordinary Loot.' : '');
    // jackpots
    els.jackpots.innerHTML = '';
    var jp = d.jackpots.slice().reverse().slice(0, 40);
    if (!jp.length) els.jackpots.appendChild(h('li', { class: 'muted', text: 'Epic, Legendary and Mythic finds will be listed here.' }));
    jp.forEach(function (j) {
      var o = R.byId[j.id]; if (!o) return;
      els.jackpots.appendChild(h('li', { class: 'jp', style: { '--tc': tierColor(o) } }, [TK.ui.img(o, { alt: '', small: true }), h('div', {}, [h('b', { text: o.name }), h('div', { class: 'muted', text: 'Chest #' + U.fmtInt(j.no) + ' · ' + U.fmtDate(j.t) })]), h('span', { class: 'rarity-chip tier-' + o.tier, text: cfg.TIERS[o.tier].label })]));
    });
    // observed vs expected
    var tb = els.table.tBodies[0]; tb.innerHTML = '';
    var N = BigInt(d.total);
    R.outcomes.forEach(function (o) {
      var foundTxt, expTxt;
      if (o.unique) {
        foundTxt = st.count(o.id) ? '✓ owned' : '—';
        var pr = P.atLeastOne(P.model(N, o.numB, o.denB));
        expTxt = F.prob(pr, { mode: 'percent', decimals: 2 }) + ' chance by now';
      } else if (o.id === R.ordinary.id) {
        foundTxt = U.fmtInt(st.count(o.id)); expTxt = 'remainder';
      } else {
        foundTxt = U.fmtInt(st.count(o.id));
        var e = P.meanExact(P.model(N, o.numB, o.denB)); expTxt = U.fmtNum(Number(e.num) / Number(e.den), 2);
      }
      tb.appendChild(h('tr', {}, [h('td', {}, [h('span', { class: 'rw' }, [TK.ui.img(o, { alt: '', small: true }), o.short])]), h('td', { class: 'num', text: foundTxt }), h('td', { class: 'num', text: expTxt }), h('td', { class: 'num', text: o.id === R.ordinary.id ? 'rest' : '1 in ' + U.fmtInt(o.denB) })]));
    });
    // lab (separate)
    var lt = TK.store.lab.totals();
    els.lab.innerHTML = '';
    [['Simulation runs', U.fmtInt(lt.runs)], ['Chests simulated', U.fmtInt(lt.chests)], ['Pirate Tokens in simulations', U.fmtInt(lt.tokens)]].forEach(function (kv) { els.lab.appendChild(h('div', {}, [h('dt', { text: kv[0] }), h('dd', { class: 'num', text: kv[1] })])); });
  }
  TK.collection = {
    init: function () {
      els = { grid: U.$('#col-grid'), found: U.$('#col-found'), ring: U.$('#col-ring'), sub: U.$('#col-sub'), ordCount: U.$('#col-ord-count'), ordShare: U.$('#col-ord-share'), ordOdds: U.$('#col-ord-odds'), jackpots: U.$('#col-jackpots'), table: U.$('#col-table'), lab: U.$('#col-lab-stats') };
      U.bus.on('interactive-changed', function () { if (TK.app.view() === 'collection') render(); else els.dirty = true; });
      U.bus.on('lab-changed', function () { if (TK.app.view() === 'collection') render(); });
      U.bus.on('view', function (v) { if (v === 'collection') render(); });
      render();
    }
  };
})(window.TK = window.TK || {});
