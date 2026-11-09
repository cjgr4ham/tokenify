// End-to-end check of TOKENIFY Companion against the LOCAL backend (never the live one).
// Needs `node local-supabase.mjs` running and a built companion. Uses throwaway accounts in the local
// in-memory database and copies of sample replays in temporary folders.
//
//   node e2e-companion.mjs <TOKENIFY-Companion.exe> <folder with sample .replay files>
//
// The sample replays are the public ones from Shiqan/FortniteReplayDecompressor (see
// companion/tests/fixtures/README.md): chapter5season5 (player "SLxTnT", 5 chests), season12 (another player)
// and collectPickup (SLxTnT, but from 2020: older than TOKENIFY accepts).
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, copyFileSync, utimesSync, readFileSync, writeFileSync, existsSync, rmSync } from 'node:fs';
import { randomBytes } from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const [exe, samples] = process.argv.slice(2);
const PRESEASON = process.argv.includes('--preseason');
if (!exe || !samples) { console.log('usage: node e2e-companion.mjs <TOKENIFY-Companion.exe> <sample replays folder> [--preseason]'); process.exit(2); }
// The sample replays are from 2019-2024. Run the local backend with --season-start=2019-01-01 for the main
// scenario; run it with the real season start (no flag) and pass --preseason to check they're all ignored.
const here = path.dirname(fileURLToPath(import.meta.url));
const API = 'http://127.0.0.1:54321', KEY = 'local-dev-key';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let passed = 0, failed = 0;
function ok(cond, msg) { if (cond) { passed++; console.log('  ✓ ' + msg); } else { failed++; console.log('  ✗ ' + msg); } }

async function post(url, body, token) {
  for (let attempt = 0; ; attempt++) {
    try {
      const r = await fetch(API + url, { method: 'POST', headers: { apikey: KEY, authorization: 'Bearer ' + (token || KEY), 'content-type': 'application/json' }, body: JSON.stringify(body || {}) });
      const text = await r.text();
      try { return JSON.parse(text); } catch { return { status: r.status, text }; }
    } catch (e) {
      // a kept-alive socket the server closed while spawnSync blocked this script: retry on a fresh one
      if (attempt >= 2) throw e;
      await sleep(300);
    }
  }
}
async function account(name) {
  const s = await post('/auth/v1/signup', {});
  const password = 'e2e ' + randomBytes(9).toString('base64url');
  const r = await post('/rest/v1/rpc/tk_signup', { p_username: name, p_password: password, p_device: 'e2e' }, s.access_token);
  if (!r.ok) throw new Error('signup failed: ' + JSON.stringify(r));
  // throwaway local-only test credentials are recorded in the git-ignored seed file, never printed
  const seedPath = path.join(here, 'seed-test-accounts.json');
  const seed = existsSync(seedPath) ? JSON.parse(readFileSync(seedPath, 'utf8').replace(/^﻿/, '')) : {};
  seed['companion-e2e'] = { ...(seed['companion-e2e'] || {}), [name]: password };
  writeFileSync(seedPath, JSON.stringify(seed, null, 2));
  return { token: s.access_token, id: r.account.id, name, password };
}
const rpc = (acc, fn, args = {}) => post('/rest/v1/rpc/' + fn, args, acc.token);

function env(dataDir, demos) {
  return { ...process.env, TOKENIFY_API_URL: API, TOKENIFY_API_KEY: KEY, TOKENIFY_WEBSITE: 'http://localhost:8090/?backend=local',
    TOKENIFY_COMPANION_DATA: dataDir, TOKENIFY_DEMOS: demos };
}
function place(file, demos, name, mtime) {
  const dest = path.join(demos, name);
  copyFileSync(path.join(samples, file), dest);
  if (mtime) utimesSync(dest, mtime, mtime);
  return dest;
}
async function until(what, fn, timeoutMs = 90000) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) { const v = await fn(); if (v) return v; await sleep(1500); }
  throw new Error('timed out waiting for ' + what);
}

const tmp = mkdtempSync(path.join(os.tmpdir(), 'tokenify-e2e-'));
const demos = path.join(tmp, 'Demos'); const data1 = path.join(tmp, 'data1'); const data2 = path.join(tmp, 'data2');
spawnSync('cmd', ['/c', 'mkdir', demos], { stdio: 'ignore' });
let app = null, pcp = null;
try {
  console.log('Companion end-to-end (local backend)');
  pcp = await account('E2E_Pc' + randomBytes(2).toString('hex'));

  if (PRESEASON) {
    // last season's matches (no Pirate Token then) are never read or sent, whatever the history choice
    const c = (await rpc(pcp, 'tk_device_pair_start')).code;
    ok(spawnSync(exe, ['--pair', c, '--history', 'import'], { env: env(data1, demos), timeout: 60000 }).status === 0, 'companion linked');
    place('chapter5season5.replay', demos, 'UnsavedReplay-2024.11.03-18.28.36.replay');
    place('season12.replay', demos, 'UnsavedReplay-2026.11.09-21.00.00.replay', new Date());   // copied today, recorded in 2020
    app = spawn(exe, ['--minimized'], { env: env(data1, demos), stdio: 'ignore' });
    await sleep(25000);
    ok((await rpc(pcp, 'tk_replay_matches')).matches.length === 0, 'nothing from before this season reached TOKENIFY');
    app.kill(); app = null; await sleep(1500);
    const report = path.join(tmp, 'report.txt');
    spawnSync(exe, ['--report', report], { env: env(data1, demos), timeout: 60000 });
    const text = existsSync(report) ? readFileSync(report, 'utf8') : '';
    ok(/pre-season=2/.test(text), 'both replays skipped as "before this season" without being read: ' + (text.match(/Replay files by state:[^\n]*/) || [''])[0]);
    throw { done: true };
  }

  // 1. an older replay already on the PC before setup (history), then link the PC
  place('chapter5season5.replay', demos, 'UnsavedReplay-2024.11.03-18.28.36.replay');
  const code = (await rpc(pcp, 'tk_device_pair_start')).code;
  ok(/^[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(code), 'website shows a pairing code');
  const pair = spawnSync(exe, ['--pair', code, '--history', 'import'], { env: env(data1, demos), encoding: 'utf8', timeout: 60000 });
  ok(pair.status === 0, 'companion linked with the code (' + (existsSync(path.join(data1, 'pair-result.txt')) ? readFileSync(path.join(data1, 'pair-result.txt'), 'utf8') : 'no result') + ')');
  const again = spawnSync(exe, ['--pair', code], { env: env(path.join(tmp, 'other'), demos), encoding: 'utf8', timeout: 60000 });
  ok(again.status === 1, 'the same code cannot link a second PC');

  // 2. run the companion: it reads the replay, confirms the player's Fortnite account and uploads the match
  app = spawn(exe, ['--minimized'], { env: env(data1, demos), detached: false, stdio: 'ignore' });
  const first = await until('the first match', async () => { const r = await rpc(pcp, 'tk_replay_matches'); return r.matches && r.matches.length >= 1 ? r : null; });
  const m = first.matches[0];
  ok(m.chests === 5 && m.chest_times.length === 5, 'match imported with 5 chests and their times');
  ok(m.historical === true, 'an older replay is imported as history, never into a live session');
  ok(m.has_match_id === true && m.parser_version === '1.0.0', 'with the Fortnite match id and parser version');
  const st = await rpc(pcp, 'tk_pc_tracking_state');
  ok(st.devices.length === 1 && st.devices[0].app_version === '1.0.0', 'the website lists the PC');
  ok(st.profiles.length === 1 && st.profiles[0].label === 'SLxTnT', "the replay's Fortnite account was linked (SLxTnT)");
  await until('a status check-in', async () => { const s = (await rpc(pcp, 'tk_pc_tracking_state')).devices[0]; return s && s.status && s.status.state ? s : null; });
  const dev = (await rpc(pcp, 'tk_pc_tracking_state')).devices[0];
  ok(['up_to_date', 'waiting', 'needs_attention', 'syncing', 'processing'].includes(dev.status.state) && dev.status.folder === 'ok', 'status reported: ' + dev.status.state + ', folder ' + dev.status.folder);

  // 3. another player's replay on the same PC: asked about, never uploaded
  place('season12.replay', demos, 'UnsavedReplay-2026.11.09-21.00.00.replay', new Date());
  await sleep(20000);
  ok((await rpc(pcp, 'tk_replay_matches')).matches.length === 1, "another Fortnite account's match is not sent");
  ok((await rpc(pcp, 'tk_pc_tracking_state')).profiles.length === 1, '...and that account is not linked');

  // 4. a match older than TOKENIFY accepts is refused by the server, and shown as refused (not retried forever)
  place('collectPickup.replay', demos, 'UnsavedReplay-2026.11.09-21.30.00.replay', new Date());
  await sleep(20000);
  ok((await rpc(pcp, 'tk_replay_matches')).matches.length === 1, 'a 2020 replay is refused by the server');
  const log1 = readFileSync(path.join(data1, 'companion.log'), 'utf8');
  ok(/refused by TOKENIFY \(times\)/.test(log1), 'the companion records why');
  ok(!/sb_publishable|local-dev-key|[A-Za-z0-9_-]{43}/.test(log1.replace(/[0-9a-f]{64}/g, '')), 'the log contains no keys or secrets');

  // 5. reinstall: a fresh copy with a new code re-reads the same replay: TOKENIFY recognises it
  app.kill(); app = null; await sleep(1500);
  const code2 = (await rpc(pcp, 'tk_device_pair_start')).code;
  ok(spawnSync(exe, ['--pair', code2, '--history', 'import'], { env: env(data2, demos), timeout: 60000 }).status === 0, 'reinstalled companion linked');
  app = spawn(exe, ['--minimized'], { env: env(data2, demos), stdio: 'ignore' });
  await until('the reinstalled companion to check in', async () => (await rpc(pcp, 'tk_pc_tracking_state')).devices.some((d) => d.last_sync_at && d.app_version) ? true : null);
  const log2 = path.join(data2, 'companion.log');
  const sent = await until('the reinstalled companion to re-send', async () => existsSync(log2) && /Sent [^\n]*replay: 5 chests/.test(readFileSync(log2, 'utf8')) ? true : null, 90000)
    .catch(() => false);
  const after = await rpc(pcp, 'tk_replay_matches');
  ok(after.matches.length === 1 && after.matches[0].chests === 5, 'still one match with 5 chests (nothing counted twice)');
  const lines = existsSync(log2) ? readFileSync(log2, 'utf8').split(/\r?\n/).filter((l) => /Sent |refused|Linked/.test(l)) : [];
  ok(sent && lines.some((l) => /5 chests \(unchanged\)/.test(l)), 'the server answered "unchanged": ' + JSON.stringify(lines.map((l) => l.slice(20))));

  // 6. revoking the PC on the website stops it
  const devs = (await rpc(pcp, 'tk_pc_tracking_state')).devices;
  for (const d of devs) await rpc(pcp, 'tk_device_revoke', { p_device: d.id });
  place('chapter5season5.replay', demos, 'UnsavedReplay-2026.11.09-22.00.00.replay', new Date());
  await until('the companion to notice', async () => /no longer accepts this PC/.test(readFileSync(path.join(data2, 'companion.log'), 'utf8')) ? true : null, 120000);
  ok(true, 'a revoked PC stops syncing and says so');
} catch (e) {
  if (!(e && e.done)) {
    failed++;
    console.log('  ✗ ' + (e.stack || e));
  }
} finally {
  if (app) app.kill();
  // delete the throwaway account (and with it its devices, matches and Fortnite account link) so the run can repeat
  if (pcp) await post('/rest/v1/rpc/tk_account_delete', { p_password: pcp.password }, pcp.token).catch(() => null);
  await sleep(1000);
  if (failed) {
    for (const d of [data1, data2]) {
      const log = path.join(d, 'companion.log');
      if (existsSync(log)) console.log(`\n--- ${log}\n` + readFileSync(log, 'utf8').split(/\r?\n/).slice(-25).join('\n'));
    }
    console.log('\n(kept for inspection: ' + tmp + ')');
  } else {
    try { rmSync(tmp, { recursive: true, force: true }); } catch { /* files may still be locked for a moment */ }
  }
}
console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
