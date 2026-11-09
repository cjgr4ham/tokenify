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

## Accounts, friends & parties

TOKENIFY needs an account: a **username and password**, nothing else (no email, phone or social
login). Signing in keeps you signed in across refreshes and browser restarts for 30 days of
inactivity; logging out, resetting your password or signing out other devices ends sessions on the
server immediately.

- **Recovery without email:** sign-up shows 8 one-time **recovery codes** (copy or download them).
  "Forgot password?" takes your username, one unused code and a new password, then signs out every
  other device. You can make a fresh set of codes from **Account › Security**.
- **Home** (`#home`) is the landing page: your real totals, CPG, token chance, tracking time, party
  and recent activity. It never starts or resumes a session by itself.
- **Friends** (`#friends`, the people icon in the header): search by username, send / accept /
  decline / cancel requests, remove or block players, and see who's online, tracking or in a party.
  You can hide your online status and stop prefix search in **Account › Privacy**.
- **Parties** (`#party`): up to 4 players. Invite friends or share a 30-minute party code. The leader
  starts and ends **squad games**; everyone keeps counting with their own Live Counter and the
  squad total adds up live. Your personal count is always the big number.
- **Squads Fill:** after a squad game, add teammates who don't use TOKENIFY as *exact* (manually
  reported), *estimated* (Barely looted / Some looting / Hunted chests: adjustable percentages of
  your own count, in Settings › Squad estimates) or *unknown* (nothing invented). Linked players are
  never estimated.
- **Squad stats** (Party › Squad history & stats): *Linked squad CPG* uses only games where everyone
  was linked; *Estimated squad CPG* includes reported and estimated teammates; partial and
  unconfirmed games are counted separately. Squad-wide drop chances describe drops anywhere in the
  squad, never your personal chance.

### How it works (security)

- Backend: Supabase (Postgres). Everything lives in **`supabase/accounts-v2.sql`**: tables, row-level
  security, and the `tk_*` functions the site calls. The browser has **no direct table access**;
  each function checks the caller's session and permissions on the server.
- Passwords are hashed with **bcrypt** (pgcrypto, cost 11) and checked in the database. Supabase Auth's
  hosted sign-up needs an email or phone, so TOKENIFY doesn't use it for accounts. The browser holds
  a throwaway *anonymous* Supabase session purely as a transport, and a server-side TOKENIFY
  session links it to your account (30-day sliding expiry, revocable).
- Rate limits on sign-in, sign-up, recovery, username checks, search, requests, invites and party
  codes; account lock-out after 10 wrong passwords in 15 minutes.
- Squad chests are events with their own IDs, so retries and reconnects never count twice; a
  removal targets the exact event. Shared totals are rebuilt from those events on the server.
- Live updates use Supabase Realtime on two signal tables (version counters only), with polling as
  a fallback.

### Setting up a Supabase project

1. Create a project at [supabase.com](https://supabase.com) and run **`supabase/accounts-v2.sql`** in
   its SQL editor (safe to run again).
2. **Authentication › Sign In / Providers:** turn on **Allow anonymous sign-ins** (the transport
   session). Email sign-in can be switched off; TOKENIFY doesn't use it.
3. Put your project URL in `js/config.js` and its **publishable** key (`sb_publishable_…`) in
   `js/supabase-key.js`. Never use a secret / `service_role` key in the browser (TOKENIFY refuses one).

Data saved in a browser before accounts existed can be added to your account once (you're asked
after signing in, or use **Account › Import earlier data**).

## Local development backend

`dev/` runs the real `supabase/accounts-v2.sql` inside [PGlite](https://pglite.dev) (Postgres in
WebAssembly), so everything can be tested with throwaway accounts that never touch the live project:

```
cd dev
npm install
node test-backend.mjs        # 205 backend checks: accounts, sessions, recovery, friends, blocking,
                             # parties, squad games, idempotent chests, teammates, permissions
node local-supabase.mjs      # local stand-in for the Supabase APIs + the site on :8090
```

Then open `http://localhost:8090/?backend=local` (and `http://127.0.0.1:8090/?backend=local` for a
second account side by side). `?backend=live` switches back; the switch only works on localhost.

## Tests

Open **`tests/index.html`** (or `http://localhost:8080/tests/`) for the 62 in-browser tests; the backend has its own suite (see *Local development backend*). Checks include: exact weights and
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
index.html            app shell: sign-in, Home, Live Counter, Chest Simulator (+ Collection), Probability Lab, Friends & party
css/                  base, animations, open-chests, lab, tracker, collection, auth, home, social, theme-light, responsive
js/config.js          drop table, rarity tiers, limits, avatars, Supabase settings (the only place odds live)
js/reward-engine.js   exact integer weights + unbiased crypto sampler + ownership helper
js/probability-engine.js  binomial engine, formatting, 320-bit high-precision engine
js/bigfloat.js        BigInt floating point            js/sim-core.js   worker jobs + host
js/chest-animation.js chest frames, reveals, cinematics  js/particles.js  canvas particles
js/api.js             backend client, RPC errors, live updates   js/auth.js  sign-in, sign-up, recovery codes
js/home.js  js/social.js (friends)  js/party.js (parties, squad games)  js/profile.js (account menu)
js/open-chests.js  js/probability-lab.js  js/charts.js  js/tracker-model.js  js/tracker-ui.js
js/collection.js  js/persistence.js  js/sync.js  js/audio.js  js/ui.js  js/util.js  js/app.js
js/vendor/            @supabase/supabase-js 2.118.0 (MIT)
supabase/accounts-v2.sql   the whole backend      supabase/schema.sql   v1 (email accounts, retired)
dev/                  local PGlite backend + backend test suite   tests/   in-browser test suite
assets/               chest frames, token, rewards, gizmos, brand (wordmark, compass, icons), sources
```
