# TOKENIFY — Pirate Chest Simulator

Open pirate chests with **exact** drop odds, analyse Pirate Token probabilities in a serious
**Probability Lab**, and count real chest openings with the **Live Tracker** while you play.

Tabs: **Open Chests · Probability Lab · Live Tracker · Collection**

---

## Launch

**Online:** play at **https://cjgr4ham.github.io/tokenify/**. Nothing to install, and it works on phones too.
Data saved there is separate from a local copy unless you sign in on both.

**On your PC:** double-click **`start.bat`**. It starts a tiny local server (Windows PowerShell,
nothing to install) and opens `http://localhost:8080/`. Keep that window open while you play.

**Also works:** open `index.html` directly. Everything runs, but browsers restrict a few things for
local files: if background workers are blocked, simulations run time-sliced on the main thread, and
saving a "Result card" image needs the server.

Internet is only used for the Google Fonts (offline, it falls back to Impact/system fonts) and for
the optional account service.

## The drop table (authoritative: `js/config.js`)

| Reward | Odds per chest |
|---|---|
| Weekly Gold Outfit (Skin) | 1 in 1,562,500 |
| Pirate Token (Pirate Shop Access) | 1 in 137,500 |
| Weekly Vehicle & Decal | 1 in 7,813 |
| Weekly Pickaxe or Back Bling | 1 in 568 |
| Weekly Jam Track | 1 in 391 |
| Weekly Loading Screen | 1 in 272 |
| Any Gizmo (5 equally likely Gizmos, 1 in 635 each) | 1 in 127 |
| Ordinary Loot | exact remainder: 1,863,470,262,613,539,641 / 1,893,779,250,756,250,000 (≈ 98.40%) |

**How a chest is drawn.** Every fraction is put over one common denominator
D = 1,893,779,250,756,250,000 (BigInt LCM), giving exact integer weights. A draw takes 61 random bits
from `crypto.getRandomValues`, **rejects** values ≥ D (no modulo bias) and maps the accepted value to
its weight interval. No pity, no streaks, no scripted results; each chest is independent.

**One of each (interactive game).** In Open Chests / Collection the six cosmetics can be owned
once; Gizmos stack. Chests are still drawn from the exact table. A roll of something you already own
becomes Ordinary Loot, which is identical to removing owned items from your pool and giving their
exact share to Ordinary Loot, so every other reward keeps its listed odds. The **Probability Lab
uses raw draws** (no ownership rule), so it shows the precise odds.

## Probability Lab

- **Simulate** up to 2 billion chests (token mode) or 400 million (full loot): genuine random draws
  in a background Web Worker, with progress and Cancel. Nothing is approximated or scripted.
- **Your chances** (exact binomial, X ~ Binomial(n, 1/137,500)): expected tokens (shown as an exact
  fraction), P(0), P(≥1), P(≥2), P(≥5), expected Gold Outfits; any k (exactly / at least / fewer /
  more) and any range [a, b].
- **Distribution chart** with expected value, central range and your simulated result; rare-tail
  and log-scale views; keyboard accessible.
- **Expected result ranges**: equal-tailed prediction intervals with their true coverage.
- **How many chests do I need?**: the smallest n for any target chance and token count (binary search
  on the exact tail; razor-thin boundaries confirmed with exact arithmetic).
- **Repeat the experiment** (Monte Carlo vs prediction), **Explore all reward odds**, **Advanced
  precision** (Standard / Detailed / Scientific / High precision with 30 significant digits from a
  320-bit BigInt engine, plus a formula inspector and engine self-check), lab history and CSV export.

**Numerics:** the PMF uses Loader's saddle-point method (stirlerr + bd0) with n·p carried exactly;
tails are summed on the side away from the mode with periodic exact re-anchoring; every probability
keeps its complement and logarithm, so tiny values show as an honest bound or 10^-k instead of 0.

## Live Tracker

- **Space** = +1 chest (only on this tab, never while typing or in a dialog, never on key-repeat).
  **N** = End game & ready next. Optional **Backspace** = −1 (off by default).
- Sessions: Start → Pause / Resume → End (with a summary). Closing the browser keeps the session and
  brings it back **paused**; time away is never counted. Device sleep is detected and excluded.
- Games: the next chest after "End game" starts the next game automatically. CPG all-time and last 10
  side by side; void / restore, edit, split a combined game, undo an accidental end; idle reminders.
- Pirate Token chances (session and overall) use exactly 1/137,500; the time and games estimates are
  based on 137,500 further chests at your measured pace (past chests never shorten the wait).
- One browser tab owns the tracker at a time, so nothing is counted twice. Every change is saved
  instantly. Export/import JSON and CSV under Details › Data.
- A browser page cannot see key presses while Fortnite itself has focus; keep TOKENIFY focused on a
  second monitor (or click it before pressing Space).

## Accounts & sync (optional)

Without setup, TOKENIFY runs in **guest mode** (saved in this browser). To enable accounts:

1. Create a free project at [supabase.com](https://supabase.com).
2. In the SQL editor, run **`supabase/schema.sql`**. It creates the table, row-level security and
   the validated write function.
3. In **Authentication › URL Configuration**, add the address you serve TOKENIFY from (for example
   `http://localhost:8080`) as the Site URL / redirect URL.
4. Put your project URL into `SUPABASE` in **`js/config.js`**, and your **publishable** key
   (`sb_publishable_…`, or the legacy `anon` key) in place of `PASTE_KEY_HERE` in **`js/supabase-key.js`**.
   Never use a secret / `service_role` key in the browser. TOKENIFY refuses one if it is pasted by mistake.

Then use the account button in the top bar: sign up (with email confirmation), log in, reset
password, log out, or delete the account. Guest data can be uploaded or merged on first sign-in;
merges are by record ID and never overwrite. Offline changes sync when you are back online.
Accounts activate only once `js/config.js` and `js/supabase-key.js` are filled in; otherwise TOKENIFY
stays in guest mode.

## Tests

Open **`tests/index.html`** (or `http://localhost:8080/tests/`). Checks include: exact weights and
fractions, PMF/CDF against exact rational arithmetic, identities, edge cases (0 chests, n > 2^53,
invalid input, 0% / 100% targets), reverse-calculator boundaries verified with 320-bit arithmetic,
prediction-interval definitions, sampler boundary mapping, chi-square tests of real draws, worker
cancel, and the Live Tracker data model (sessions, pause, restore, sleep, CPG, void, split, undo,
one-token rule, export/import).

## Artwork & fonts

- Chest animation frames, effects and parts are cut from the supplied sprite sheet
  (`assets/source/chest-spritesheet.webp`) into `assets/chest/`.
- Supplied art in use: Pirate Token, Gold Outfit, Vehicle & Decal, Loading Screen (framed key art),
  Lucky Locator, Sprite Locator, Portable Extractor, Extraction Accelerator.
- TOKENIFY-made SVGs: Pickaxe / Back Bling, Jam Track, Golden Fireworks and Ordinary Loot (replace the
  files, or point `art:` at new ones in `js/config.js`). Every supplied image also has an SVG fallback.
- Fonts: **Anton** and **Barlow / Barlow Condensed** (open licence, via Google Fonts) stand in for
  Fortnite's commercial Burbank Big Condensed, which is not bundled.
- TOKENIFY is an independent fan project and is not affiliated with Epic Games.

## Files

```
index.html            app shell (4 tabs)        start.bat / start-server.ps1   local launcher
css/                  base, animations, open-chests, lab, tracker, collection, theme-light, responsive
js/config.js          drop table, rarity tiers, limits, Supabase settings (the only place odds live)
js/reward-engine.js   exact integer weights + unbiased crypto sampler + ownership helper
js/probability-engine.js  binomial engine, formatting, 320-bit high-precision engine
js/bigfloat.js        BigInt floating point            js/sim-core.js   worker jobs + host
js/chest-animation.js chest frames, reveals, cinematics  js/particles.js  canvas particles
js/open-chests.js  js/probability-lab.js  js/charts.js  js/tracker-model.js  js/tracker-ui.js
js/collection.js  js/persistence.js  js/sync.js  js/audio.js  js/ui.js  js/util.js  js/app.js
supabase/schema.sql   optional accounts             tests/            in-browser test suite
assets/               chest frames, token, rewards, gizmos, brand (wordmark, compass, icons), sources
```
