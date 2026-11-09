/* TOKENIFY — Open Chests page: opening controls, reveal cards, multi-open summaries, history & stats.
 * Draws happen in TK.store.interactive.drawAndRecord (exact table + one-of-each ownership rule). */
(function (TK) {
  'use strict';
  var U = TK.util, cfg = TK.config, R = TK.rewards, h = U.h;
  var ORDER = function (t) { return cfg.TIERS[t].order; };
  var st, chest, count = 1, current = null, filter = 'all', multiOpen = false, els = {};

  // ---------- odds text ----------
  // Exact decimal when it terminates (e.g. 1/1,562,500 = 0.000064%), otherwise a rounded value marked ≈.
  function pctOfRatio(num, den, sig) {
    var exact = TK.fmt.rationalDecimal(BigInt(num) * 100n, BigInt(den), 14);
    if (exact.exact && exact.frac.length <= 10) return exact.int + (exact.frac ? '.' + exact.frac : '') + '%';
    var v = Number(num) / Number(den) * 100, s = sig || 4;
    var d = Math.max(0, s - 1 - Math.floor(Math.log10(v)));
    return '≈' + v.toFixed(Math.min(12, d)) + '%';
  }
  function effOrdinary() { var e = R.effectiveProbs(st.ownedMap()); return e[R.ordinary.id]; }
  function pct(o) {
    if (o.id === R.ordinary.id) { var e = effOrdinary(); return pctOfRatio(e.num, e.den, 6); }
    return pctOfRatio(o.numB, o.denB, 4);
  }
  function oddsLine(o) {
    if (o.id === R.ordinary.id) return pct(o) + ' of chests';
    if (o.group) return '1 in ' + U.fmtInt(o.denB) + ' per chest · any Gizmo 1 in 127';
    return '1 in ' + U.fmtInt(o.denB) + ' per chest';
  }
  function tierColor(o) { return o.tier === 'uncommon' && o.accent ? o.accent : cfg.TIERS[o.tier].color; }

  function revealCard(res) {
    var o = res.outcome, tier = o.tier, ord = ORDER(tier);
    var plate = h('div', { class: 'rv-plate' }, [
      h('span', { class: 'rarity-chip tier-' + tier, text: o.group ? 'Gizmo' : cfg.TIERS[tier].label }),
      h('div', { class: 'rv-name', text: o.name }),
      h('div', { class: 'rv-odds num', text: oddsLine(o) })
    ]);
    if (res.converted) plate.appendChild(h('div', { class: 'rv-note', text: 'You already own the ' + res.rolled.short + ', so this roll became Ordinary Loot.' }));
    var card = h('div', { class: 'rv tier-' + tier, style: { '--tc': cfg.TIERS[tier].color, '--accent': tierColor(o) } }, [
      ord >= 2 ? h('div', { class: 'rv-burst' }) : null,
      h('div', { class: 'rv-glow' }),
      h('div', { class: 'rv-art' }, [TK.ui.img(o, { alt: '' })]),
      plate
    ]);
    if (ord === 1) card.style.animationName = 'rv-pop';
    return card;
  }

  function onRevealed(res) {
    if (res.revealed) return;
    res.revealed = true;
    renderStats(true); renderLatest(res); renderHistory(); renderLive();
    var o = res.outcome;
    U.announce('Chest ' + U.fmtInt(res.no) + ': ' + o.name + ' (' + (o.group ? 'Gizmo' : cfg.TIERS[o.tier].label) + ', ' + oddsLine(o) + ')' + (res.converted ? '. Already owned item converted to Ordinary Loot.' : ''), ORDER(o.tier) >= 4);
  }
  TK.chestUI = { revealCard: revealCard, pct: pct, onRevealed: onRevealed, oddsLine: oddsLine };

  // ---------- panels ----------
  function renderStats(bump) {
    var d = st.data();
    U.setText(els.total, U.fmtInt(d.total));
    U.setText(els.tokens, st.owns('pirate-token') ? '1 ✓' : '0');
    U.setText(els.golds, st.owns('gold-outfit') ? '1 ✓' : '0');
    var rarest = R.outcomes.filter(function (o) { return o.id !== R.ordinary.id && st.count(o.id) > 0; }).sort(function (a, b) { return a.weight < b.weight ? -1 : a.weight > b.weight ? 1 : 0; })[0];
    els.rarest.textContent = rarest ? rarest.short : '—';
    els.rarest.parentElement.style.setProperty('--tc', rarest ? cfg.TIERS[rarest.tier].color : '');
    if (bump) U.$$('.ocs', els.statbar).forEach(function (x) { x.classList.remove('bump'); void x.offsetWidth; x.classList.add('bump'); });
  }
  function renderLatest(res) {
    var box = els.latest; box.innerHTML = ''; box.className = '';
    if (!res) { box.className = 'latest-empty'; box.textContent = 'Your first reward will appear here.'; return; }
    var o = res.outcome;
    box.appendChild(h('div', { class: 'latest-card', style: { '--tc': tierColor(o) } }, [
      TK.ui.img(o, { alt: '', small: true }),
      h('div', {}, [
        h('span', { class: 'rarity-chip tier-' + o.tier, text: o.group ? 'Gizmo' : cfg.TIERS[o.tier].label }),
        h('div', { class: 'latest-name', text: o.name }),
        h('div', { class: 'latest-odds num', text: oddsLine(o) + ' · chest #' + U.fmtInt(res.no) }),
        res.converted ? h('div', { class: 'fine', text: 'Rolled ' + res.rolled.short + ' (already owned)' }) : null
      ])
    ]));
  }
  function renderHistory() {
    var list = els.history, hist = st.data().hist; list.innerHTML = '';
    if (!hist.length) { list.appendChild(h('li', { class: 'history-empty', text: 'No chests opened yet.' })); return; }
    var lines = 0, i = hist.length - 1;
    while (i >= 0 && lines < 60) {
      var e = hist[i], o = R.outcomes[e[1]];
      var keep = filter === 'all' || (filter === 'special' && o.id !== R.ordinary.id) || (filter === 'rare' && ORDER(o.tier) >= 2);
      if (!keep) { i--; continue; }
      if (o.id === R.ordinary.id && filter === 'all' && e.length < 4) { // group consecutive plain Ordinary Loot
        var j = i; while (j - 1 >= 0 && hist[j - 1][1] === e[1] && hist[j - 1].length < 4) j--;
        var n = i - j + 1;
        list.appendChild(h('li', { class: 'hl hl-group' }, [TK.ui.img(o, { alt: '', small: true }),
          h('span', { class: 'hl-name', text: n > 1 ? 'Ordinary Loot ×' + n : 'Ordinary Loot' }),
          h('span', { class: 'hl-no', text: n > 1 ? '#' + U.fmtInt(hist[j][0]) + '–' + U.fmtInt(e[0]) : '#' + U.fmtInt(e[0]) })]));
        i = j - 1; lines++; continue;
      }
      var li = h('li', { class: 'hl' + (o.id !== R.ordinary.id ? ' special' : ''), style: { '--tc': tierColor(o) } }, [
        TK.ui.img(o, { alt: '', small: true }),
        h('span', { class: 'hl-name', text: o.name }),
        e.length >= 4 ? h('span', { class: 'rarity-chip', style: { background: 'rgba(255,255,255,.12)', color: 'var(--text-dim)' }, 'data-tip-text': 'Rolled ' + R.outcomes[e[3]].name + ', which you already own, so it became Ordinary Loot.', text: 'owned roll' })
          : h('span', { class: 'rarity-chip tier-' + o.tier, text: o.group ? 'Gizmo' : cfg.TIERS[o.tier].label }),
        h('span', { class: 'hl-no', text: '#' + U.fmtInt(e[0]) })
      ]);
      list.appendChild(li); lines++; i--;
    }
    if (!list.children.length) list.appendChild(h('li', { class: 'history-empty', text: filter === 'rare' ? 'No rare or better rewards yet.' : 'No special rewards yet.' }));
  }
  function renderLive() {
    var gizmos = R.gizmoIds.reduce(function (a, id) { return a + st.count(id); }, 0);
    U.setText(els.gizmos, U.fmtInt(gizmos));
    var specials = R.specials.filter(function (o) { return st.count(o.id) > 0; });
    var common = specials.sort(function (a, b) { return st.count(b.id) - st.count(a.id) || (a.weight < b.weight ? -1 : 1); })[0];
    U.setText(els.common, common ? common.short + ' ×' + st.count(common.id) : '—');
    var d = st.data();
    function since(id) {
      if (st.owns(id)) return 'Found on #' + U.fmtInt(d.first[id]);
      return d.total ? U.fmtInt(d.total) + ' (none yet)' : '—';
    }
    U.setText(els.sinceToken, since('pirate-token'));
    U.setText(els.sinceGold, since('gold-outfit'));
    U.setText(els.special, U.fmtInt(R.specials.reduce(function (a, o) { return a + st.count(o.id); }, 0)));
  }

  // ---------- multi-open summaries ----------
  function closeMulti() { if (!multiOpen) return; multiOpen = false; els.multi.hidden = true; els.multi.innerHTML = ''; chest.afterMulti(); }
  function renderMulti(results, instant) {
    var m = els.multi; m.innerHTML = ''; m.hidden = false; multiOpen = true;
    var n = results.length, conv = results.filter(function (r) { return r.converted; }).length;
    var done = h('button', { class: 'btn-ghost', type: 'button' }, ['Done']);
    done.addEventListener('click', closeMulti);
    var inner = h('div', { class: 'multi-inner' }, [h('div', { class: 'multi-head' }, [h('div', { class: 'multi-title', text: n + ' chests opened' }), done])]);
    if (n <= 12) {
      var grid = h('div', { class: 'grid10' });
      results.forEach(function (r, i) {
        var o = r.outcome, special = o.id !== R.ordinary.id;
        var card = h('div', { class: 'mcard tier-' + o.tier + (special ? ' special' : ''), style: { '--tc': tierColor(o), animationDelay: (instant ? 0 : i * 90) + 'ms' } }, [
          TK.ui.img(o, { alt: '', small: true }), h('div', { class: 'mcard-name', text: o.short }),
          h('span', { class: 'rarity-chip tier-' + o.tier, text: o.group ? 'Gizmo' : cfg.TIERS[o.tier].label })
        ]);
        if (r.converted) card.setAttribute('data-tip-text', 'Rolled ' + r.rolled.name + ' (already owned) → Ordinary Loot');
        grid.appendChild(card);
        if (special && !instant) setTimeout(function () { TK.audio.play(ORDER(o.tier) >= 2 ? 'rare' : 'gizmo'); }, i * 90 + 200);
      });
      inner.appendChild(grid);
    } else {
      var dots = h('div', { class: 'dots100', 'aria-hidden': 'true' });
      results.forEach(function (r, i) { dots.appendChild(h('span', { class: 'dot tier-' + r.outcome.tier, style: { '--tc': tierColor(r.outcome), animationDelay: (instant ? 0 : Math.floor(i / 10) * 55 + (i % 10) * 6) + 'ms' } })); });
      var counts = {}; results.forEach(function (r) { counts[r.outcome.id] = (counts[r.outcome.id] || 0) + 1; });
      var list = h('ul', { class: 'sum-list' });
      R.outcomes.filter(function (o) { return counts[o.id] && o.id !== R.ordinary.id; }).forEach(function (o) {
        list.appendChild(h('li', { style: { '--tc': tierColor(o) } }, [TK.ui.img(o, { alt: '', small: true }), h('span', {}, [o.name, h('br'), h('small', { class: 'muted', text: oddsLine(o) })]), h('b', { text: '×' + counts[o.id] })]));
      });
      if (!list.children.length) list.appendChild(h('li', { class: 'sum-none' }, [h('span'), h('span', { text: 'No special drops this time — that happens in about ' + U.fmtNum(Math.pow(effOrdinary().prob, results.length) * 100, 1) + '% of ' + results.length + '-chest batches.' }), h('span')]));
      list.appendChild(h('li', { style: { '--tc': 'var(--t-common)' } }, [TK.ui.img(R.ordinary, { alt: '', small: true }), h('span', { text: 'Ordinary Loot' }), h('b', { text: '×' + (counts[R.ordinary.id] || 0) })]));
      inner.appendChild(h('div', { class: 'sum100' }, [dots, list]));
      var specials = results.filter(function (r) { return r.outcome.id !== R.ordinary.id; });
      if (specials.length && !instant) setTimeout(function () { TK.audio.play('sparkle', { n: Math.min(8, 2 + specials.length) }); }, 500);
    }
    if (conv) inner.appendChild(h('p', { class: 'fine', text: conv + (conv === 1 ? ' roll' : ' rolls') + ' landed on cosmetics you already own and became Ordinary Loot.' }));
    m.appendChild(inner);
    var summary = {}; results.forEach(function (r) { summary[r.outcome.short] = (summary[r.outcome.short] || 0) + 1; });
    U.announce(n + ' chests: ' + Object.keys(summary).map(function (k) { return summary[k] + ' ' + k; }).join(', '));
    setTimeout(function () { try { done.focus({ preventScroll: true }); } catch (e) { /* ignore */ } }, 50);
  }

  // ---------- opening ----------
  async function open() {
    if (chest.cine) return; // a jackpot reveal is waiting for "Collect"
    if (current && !current.revealed && chest.busy) {
      if (ORDER(current.outcome.tier) >= 2) { chest.skip(); return; } // never let a rare reveal be skipped past unseen
      onRevealed(current);
    }
    if (multiOpen) closeMulti();
    var results = st.drawAndRecord(count);
    if (count === 1) { current = results[0]; chest.play(current); return; }
    current = null;
    els.open.classList.add('busy');
    var skipped = await chest.playMulti(results);
    els.open.classList.remove('busy');
    results.forEach(function (r) { r.revealed = true; });
    renderStats(true); renderLatest(results[results.length - 1]); renderHistory(); renderLive();
    renderMulti(results, skipped);
    var jack = results.filter(function (r) { return ORDER(r.outcome.tier) >= 4; }).sort(function (a, b) { return ORDER(b.outcome.tier) - ORDER(a.outcome.tier); })[0];
    if (jack) {
      await U.wait(skipped ? 200 : (count === 10 ? 1300 : 900));
      await chest.cinematicFor(jack);
    }
  }

  function setCount(n) {
    count = n; TK.settings.set('openCount', n);
    els.openLabel.textContent = n === 1 ? 'Open Chest' : 'Open ' + n + ' Chests';
  }

  function makeDust() {
    var box = els.dust, q = TK.settings.get('quality'), n = q === 'low' ? 4 : q === 'medium' ? 9 : 16;
    box.innerHTML = '';
    for (var i = 0; i < n; i++) {
      var d = h('i', { style: { left: (8 + Math.random() * 84) + '%', '--dur': (7 + Math.random() * 7).toFixed(1) + 's', '--delay': (-Math.random() * 12).toFixed(1) + 's', '--dx': ((Math.random() - 0.5) * 70).toFixed(0) + 'px', '--o': (0.25 + Math.random() * 0.55).toFixed(2) } });
      d.style.width = d.style.height = (2 + Math.random() * 3).toFixed(1) + 'px';
      box.appendChild(d);
    }
  }

  TK.openChests = {
    init: function (onProgress) {
      st = TK.store.interactive;
      els = {
        statbar: U.$('.oc-statbar'), total: U.$('#oc-total'), tokens: U.$('#oc-tokens'), golds: U.$('#oc-golds'), rarest: U.$('#oc-rarest'),
        stage: U.$('#oc-stage'), open: U.$('#btn-open'), openLabel: U.$('#btn-open-label'), latest: U.$('#oc-latest'), history: U.$('#oc-history'),
        gizmos: U.$('#ls-gizmos'), common: U.$('#ls-common'), sinceToken: U.$('#ls-since-token'), sinceGold: U.$('#ls-since-gold'), special: U.$('#ls-special'),
        multi: U.$('#multi'), dust: U.$('#stage-dust'), skip: U.$('#skip-btn')
      };
      chest = new TK.Chest({
        stage: els.stage, wrap: U.$('#chest-wrap'), button: U.$('#chest'), frames: U.$('#chest-frames'), aura: U.$('#chest-aura'),
        canvas: U.$('#stage-fx'), reveal: U.$('#reveal'), flash: U.$('#stage-flash'), rays: U.$('#stage-rays'), skip: els.skip
      });
      TK.openChests.chest = chest;
      makeDust();
      U.bus.on('settings', function (e) { if (e.key === 'quality') makeDust(); });
      setCount([1, 10, 100].indexOf(TK.settings.get('openCount')) >= 0 ? TK.settings.get('openCount') : 1);
      var seg = TK.ui.bindRadios(U.$('#open-count'), 'data-count', function (v) { setCount(parseInt(v, 10)); });
      seg.set(count);
      els.open.addEventListener('click', open);
      U.$('#chest').addEventListener('click', open);
      els.skip.addEventListener('click', function () { chest.skip(); });
      els.stage.addEventListener('click', function (e) { if (chest.busy && !e.target.closest('button') && !e.target.closest('.multi')) chest.skip(); });
      U.$$('#oc-filter .chip').forEach(function (b) {
        b.addEventListener('click', function () {
          filter = b.getAttribute('data-filter');
          U.$$('#oc-filter .chip').forEach(function (x) { var on = x === b; x.classList.toggle('on', on); x.setAttribute('aria-pressed', on ? 'true' : 'false'); });
          renderHistory();
        });
      });
      U.bus.on('interactive-changed', function (e) { if (e && e.reset) { current = null; renderStats(); renderLatest(null); renderHistory(); renderLive(); } });
      renderStats(); renderHistory(); renderLive();
      var hist = st.data().hist; if (hist.length) { var last = hist[hist.length - 1]; renderLatest({ outcome: R.outcomes[last[1]], rolled: last[3] != null ? R.outcomes[last[3]] : null, converted: last.length >= 4, no: last[0] }); }
      return chest.load(onProgress);
    },
    onKey: function (e) {
      if (e.key === 'Enter' && !e.repeat) { e.preventDefault(); open(); return true; }
      if (e.key === 'Escape' && chest.busy) { chest.skip(); return true; }
      if (e.key === 'Escape' && multiOpen) { closeMulti(); return true; }
      return false;
    },
    open: open
  };
})(window.TK = window.TK || {});
