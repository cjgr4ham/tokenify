/* TOKENIFY — authoritative configuration.
 * Every probability used anywhere in the app is derived from the integer
 * fractions in this file. Nothing else may define a drop rate.
 */
(function (TK) {
  'use strict';

  // Rarity tiers drive presentation only (colour, reveal length). They never affect odds.
  var TIERS = {
    common:    { id: 'common',    label: 'Common',    order: 0, color: '#d9c08a', deep: '#7a5b25', glow: 'rgba(255,214,140,0.55)' },
    uncommon:  { id: 'uncommon',  label: 'Gizmo',     order: 1, color: '#5ee6b8', deep: '#0f6b55', glow: 'rgba(94,230,184,0.55)' },
    rare:      { id: 'rare',      label: 'Rare',      order: 2, color: '#56aefc', deep: '#123f7a', glow: 'rgba(86,174,252,0.6)' },
    epic:      { id: 'epic',      label: 'Epic',      order: 3, color: '#c084ff', deep: '#4a1f86', glow: 'rgba(192,132,255,0.6)' },
    legendary: { id: 'legendary', label: 'Legendary', order: 4, color: '#ffb93f', deep: '#8a4a05', glow: 'rgba(255,185,63,0.7)' },
    mythic:    { id: 'mythic',    label: 'Mythic',    order: 5, color: '#ffe58a', deep: '#9a6a00', glow: 'rgba(255,229,138,0.8)' }
  };

  // The exact drop table (per chest). num/den are integers.
  // unique: true -> a cosmetic you can own only once in the interactive game (Gizmos can stack).
  // art: supplied artwork; fallback: TOKENIFY SVG used automatically if the artwork fails to load.
  var REWARDS = [
    { id: 'gold-outfit', rank: 1, name: 'Weekly Gold Outfit', short: 'Gold Outfit', kind: 'Skin', num: 1, den: 1562500, tier: 'mythic', unique: true,
      art: 'assets/rewards/gold-outfit.webp', fallback: 'assets/rewards/gold-outfit.svg',
      blurb: 'The rarest reward in the chest: this week’s solid-gold outfit.' },
    { id: 'pirate-token', rank: 2, name: 'Pirate Token', short: 'Pirate Token', kind: 'Pirate Shop Access', num: 1, den: 137500, tier: 'legendary', unique: true,
      art: 'assets/tokens/pirate-token.webp', artSmall: 'assets/tokens/pirate-token-160.png', fallback: 'assets/brand/compass.svg',
      blurb: 'Grants Pirate Shop access. The second-rarest reward.' },
    { id: 'vehicle-decal', rank: 3, name: 'Weekly Vehicle & Decal', short: 'Vehicle & Decal', kind: 'Vehicle', num: 1, den: 7813, tier: 'epic', unique: true,
      art: 'assets/rewards/vehicle-decal.webp', fallback: 'assets/rewards/vehicle-decal.svg', blurb: 'This week’s vehicle with its matching decal.' },
    { id: 'pickaxe-backbling', rank: 4, name: 'Weekly Pickaxe or Back Bling', short: 'Pickaxe / Back Bling', kind: 'Cosmetic', num: 1, den: 568, tier: 'epic', unique: true,
      art: 'assets/rewards/pickaxe-backbling.svg', blurb: 'This week’s harvesting tool or back bling.' },
    { id: 'jam-track', rank: 5, name: 'Weekly Jam Track', short: 'Jam Track', kind: 'Music', num: 1, den: 391, tier: 'rare', unique: true,
      art: 'assets/rewards/jam-track.svg', blurb: 'This week’s Jam Track.' },
    { id: 'loading-screen', rank: 6, name: 'Weekly Loading Screen', short: 'Loading Screen', kind: 'Loading Screen', num: 1, den: 272, tier: 'rare', unique: true,
      art: 'assets/rewards/loading-screen.webp', fallback: 'assets/rewards/loading-screen.svg', blurb: 'This week’s loading screen.' }
  ];

  // "Any Gizmo" is 1/127. A Gizmo drop is equally likely to be any of these five,
  // so each member is (1/127) / 5 = 1/635 exactly. Members are derived, never typed in.
  var GIZMO_GROUP = {
    id: 'any-gizmo', name: 'Any Gizmo', short: 'Any Gizmo', num: 1, den: 127, rank: 7,
    members: [
      { id: 'lucky-locator', name: 'Lucky Locator', short: 'Lucky Locator', accent: '#c86bff',
        art: 'assets/gizmos/lucky-locator.webp', fallback: 'assets/gizmos/lucky-locator.svg',
        blurb: 'Creates a buried Sprite Chest guaranteed to contain a Sprite missing from your collection.' },
      { id: 'sprite-locator', name: 'Sprite Locator', short: 'Sprite Locator', accent: '#ff4d5e',
        art: 'assets/gizmos/sprite-locator.webp', fallback: 'assets/gizmos/sprite-locator.svg',
        blurb: 'Creates a buried Sprite Chest containing a random Sprite (not guaranteed to be new).' },
      { id: 'portable-extractor', name: 'Portable Extractor', short: 'Portable Extractor', accent: '#4f8dff',
        art: 'assets/gizmos/portable-extractor.webp', fallback: 'assets/gizmos/portable-extractor.svg',
        blurb: 'Extract your equipped Sprite anywhere on the map, no Extraction Site needed.' },
      { id: 'extraction-accelerator', name: 'Extraction Accelerator', short: 'Extraction Accelerator', accent: '#7ee04a',
        art: 'assets/gizmos/extraction-accelerator.webp', fallback: 'assets/gizmos/extraction-accelerator.svg',
        blurb: 'Instantly summons an Extraction Crate at a nearby Extraction Site.' },
      { id: 'golden-fireworks', name: 'Golden Fireworks', short: 'Golden Fireworks', accent: '#ffd84a',
        art: 'assets/gizmos/golden-fireworks.svg',
        blurb: 'Launches a firework showing your username and drops Sprite Dust.' }
    ]
  };

  // Ownership rule for the interactive game (Open Chests, Collection, Live Tracker token finds):
  // every chest is still drawn from the exact table above; if the draw is a unique cosmetic the
  // player already owns, they receive Ordinary Loot instead. That is mathematically identical to
  // removing owned items from the pool and adding their exact share to Ordinary Loot, so every
  // other reward keeps its listed probability. The Probability Lab is unaffected (raw draws).
  var RULES = { uniqueCosmetics: true };

  // Everything that is not a special drop. Its probability is computed as the exact remainder.
  var ORDINARY = { id: 'ordinary', rank: 8, name: 'Ordinary Loot', short: 'Ordinary Loot', kind: 'Loot', tier: 'common',
    art: 'assets/rewards/ordinary-loot.svg', remainder: true,
    blurb: 'A modest handful of pirate coins and trinkets. Most chests contain this.' };

  // Reveal length per tier at "Normal" speed (ms). Presentation only.
  var REVEAL_MS = { common: 820, uncommon: 1500, rare: 1950, epic: 2650, legendary: 5600, mythic: 8600 };
  var SPEED = { cinematic: 1.35, normal: 1, fast: 0.42 };

  // Large-simulation limits (random draws actually performed, never approximated).
  var LIMITS = {
    fastMax: 2000000000,        // token-only Bernoulli trials per run
    fullMax: 400000000,         // full reward-table draws per run
    mcMaxTotal: 2000000000,     // experiments x chests
    mcMaxExperiments: 200000,
    hpMaxSteps: 2500000,        // high-precision series terms
    sumMaxTerms: 60000000       // double-precision tail-sum terms
  };

  // Accounts backend (Supabase). See README.md > "Accounts & sync".
  // The key goes in js/supabase-key.js: the project's *publishable* key (sb_publishable_..., or the legacy
  // "anon" key). It is public by design; every table is protected by row-level security and server-side checks.
  // A secret / service_role key is refused here, so it can never be used from the browser.
  // Development only: on localhost, ?backend=local switches that origin to the local test backend in dev/
  // (?backend=live switches back). This switch is ignored everywhere except localhost.
  var SUPABASE = (function () {
    var isLocalHost = /^(localhost|127\.0\.0\.1|\[::1\])$/.test(location.hostname);
    if (isLocalHost) {
      try {
        var want = new URLSearchParams(location.search).get('backend');
        if (want === 'local' || want === 'live') localStorage.setItem('tokenify.device.backend', want);
        if (localStorage.getItem('tokenify.device.backend') === 'local') return { url: 'http://127.0.0.1:54321', anonKey: 'local-dev-key', local: true, realtime: false };
      } catch (e) { /* storage blocked: use the live backend */ }
    }
    var c = window.TOKENIFY_SUPABASE || {};
    var url = String(c.url || 'https://sbzwbxtqplblfngeqlzo.supabase.co').trim().replace(/\/+$/, '');
    var key = String(c.anonKey || window.TOKENIFY_SUPABASE_KEY || '').trim();
    if (/^PASTE_?KEY_?HERE$/i.test(key)) key = '';
    function jwtRole(k) {
      try { var p = k.split('.'); return p.length === 3 ? (JSON.parse(atob(p[1].replace(/-/g, '+').replace(/_/g, '/'))).role || '') : ''; }
      catch (e) { return ''; }
    }
    if (/^sb_secret_/i.test(key) || jwtRole(key) === 'service_role') {
      if (window.console) console.error('TOKENIFY: js/supabase-key.js holds a SECRET key. Use the publishable key instead. Accounts are off.');
      key = '';
    }
    return { url: url, anonKey: key, local: false, realtime: true };
  })();

  // Public profile pictures (a fixed set of TOKENIFY artwork: no uploads, nothing to moderate).
  var AVATARS = [
    { id: 'compass', name: 'Compass', art: 'assets/brand/compass.svg', ring: '#f6c24a' },
    { id: 'token', name: 'Pirate Token', art: 'assets/tokens/pirate-token-160.png', ring: '#ffb93f' },
    { id: 'outfit', name: 'Gold Outfit', art: 'assets/rewards/gold-outfit.webp', ring: '#ffe58a' },
    { id: 'vehicle', name: 'Vehicle', art: 'assets/rewards/vehicle-decal.webp', ring: '#c084ff' },
    { id: 'pickaxe', name: 'Pickaxe', art: 'assets/rewards/pickaxe-backbling.svg', ring: '#56aefc' },
    { id: 'jam', name: 'Jam Track', art: 'assets/rewards/jam-track.svg', ring: '#56aefc' },
    { id: 'loading', name: 'Loading Screen', art: 'assets/rewards/loading-screen.webp', ring: '#56aefc' },
    { id: 'lucky', name: 'Lucky Locator', art: 'assets/gizmos/lucky-locator.webp', ring: '#c86bff' },
    { id: 'sprite', name: 'Sprite Locator', art: 'assets/gizmos/sprite-locator.webp', ring: '#ff4d5e' },
    { id: 'extractor', name: 'Portable Extractor', art: 'assets/gizmos/portable-extractor.webp', ring: '#4f8dff' },
    { id: 'accelerator', name: 'Extraction Accelerator', art: 'assets/gizmos/extraction-accelerator.webp', ring: '#7ee04a' },
    { id: 'fireworks', name: 'Golden Fireworks', art: 'assets/gizmos/golden-fireworks.svg', ring: '#ffd84a' },
    { id: 'chest', name: 'Pirate Chest', art: 'assets/chest/close-00.webp', ring: '#e09a2c' }
  ];

  TK.config = {
    AVATARS: AVATARS,
    APP_NAME: 'TOKENIFY',
    TIERS: TIERS,
    REWARDS: REWARDS,
    GIZMO_GROUP: GIZMO_GROUP,
    ORDINARY: ORDINARY,
    RULES: RULES,
    REVEAL_MS: REVEAL_MS,
    SPEED: SPEED,
    LIMITS: LIMITS,
    SUPABASE: SUPABASE,
    STORAGE_PREFIX: 'tokenify.'
  };
})(window.TK = window.TK || {});
