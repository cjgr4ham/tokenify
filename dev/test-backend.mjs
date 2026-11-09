// TOKENIFY backend test suite. Runs supabase/accounts-v2.sql in PGlite and drives it through the
// same RPC path PostgREST uses, with several throwaway test accounts that exist only in memory.
//   node test-backend.mjs
import { createDb, rpc, asRole, newAnonUser } from './pg-env.mjs';

let passed = 0, failed = 0;
const failures = [];
function ok(cond, msg) { if (cond) passed++; else { failed++; failures.push(msg); console.log('  ✗ ' + msg); } }
function eq(a, b, msg) { const s = (x) => JSON.stringify(x); ok(s(a) === s(b), `${msg} (expected ${s(b)}, got ${s(a)})`); }
async function throws(p, re, msg) {
  try { await p; ok(false, msg + ' (did not throw)'); }
  catch (e) { ok(re.test(String(e.message || e)), `${msg} (threw: ${e.message})`); }
}
async function section(name, fn) {
  const before = failed;
  try { await fn(); } catch (e) { failed++; failures.push(`${name}: crashed: ${e.stack || e}`); console.log('  ✗ crashed: ' + (e.stack || e)); }
  console.log(`${failed === before ? '✓' : '✗'} ${name}`);
}

const db = await createDb();
const sql = async (q, p = []) => (await db.query(q, p)).rows;
const call = (who, fn, args) => rpc(db, who, fn, args);
const anon = { uid: null };

async function browser(ip) { return { uid: await newAnonUser(db), ip: ip || null }; }
const PW = { P: 'treasure-map-77', bob: 'blue whale 88', cara: 'sunset-river-55', dan: 'dan the man 42', eve: 'night owl 31', frank: 'fort-knox-90', gina: 'gizmo!garden!13' };
async function signup(name, pw) {
  const b = await browser();
  const r = await call(b, 'tk_signup', { p_username: name, p_password: pw, p_device: 'Test runner' });
  if (!r.ok) throw new Error(`signup ${name} failed: ${r.error}`);
  return { ...b, id: r.account.id, name, codes: r.recovery_codes };
}
async function befriend(a, b) {
  const r1 = await call(a, 'tk_friend_request', { p_account: b.id });
  if (r1.status === 'friends') return;
  const r2 = await call(b, 'tk_friend_request', { p_account: a.id });   // mutual request auto-accepts
  if (r2.status !== 'friends') throw new Error('befriend failed');
}
const uuid = () => crypto.randomUUID();
const nowIso = (offsetMs = 0) => new Date(Date.now() + offsetMs).toISOString();

let P, bob, cara, dan, eve, frank, gina;

await section('sign-up validation and uniqueness', async () => {
  const b = await browser();
  eq((await call(b, 'tk_signup', { p_username: 'ab', p_password: 'long enough pw' })).error, 'username_invalid', 'too short username');
  eq((await call(b, 'tk_signup', { p_username: 'bad name', p_password: 'long enough pw' })).error, 'username_invalid', 'space in username');
  eq((await call(b, 'tk_signup', { p_username: '_lead', p_password: 'long enough pw' })).error, 'username_invalid', 'leading separator');
  eq((await call(b, 'tk_signup', { p_username: 'trail-', p_password: 'long enough pw' })).error, 'username_invalid', 'trailing separator');
  eq((await call(b, 'tk_signup', { p_username: 'dou__ble', p_password: 'long enough pw' })).error, 'username_invalid', 'double separator');
  eq((await call(b, 'tk_signup', { p_username: 'abcdefghijklmnopq', p_password: 'long enough pw' })).error, 'username_invalid', '17 chars');
  eq((await call(b, 'tk_signup', { p_username: 'Admin', p_password: 'long enough pw' })).error, 'username_taken', 'reserved name');
  eq((await call(b, 'tk_signup', { p_username: 'Hunter_1', p_password: 'short' })).error, 'password_short', 'short password');
  eq((await call(b, 'tk_signup', { p_username: 'Hunter_1', p_password: 'my hunter_1 pass' })).error, 'password_has_username', 'password contains username');
  eq((await call(b, 'tk_signup', { p_username: 'Hunter_1', p_password: 'Password123' })).error, 'password_common', 'common password');
  eq((await call(b, 'tk_signup', { p_username: 'Hunter_1', p_password: 'zzzzzzzzzz' })).error, 'password_common', 'repeated character');
  eq((await call(b, 'tk_signup', { p_username: 'Hunter_1', p_password: 'x'.repeat(65) })).error, 'password_long', 'too long password');
  P = await signup('PirateHunter', PW.P);
  eq(P.codes.length, 8, 'eight recovery codes');
  ok(P.codes.every((c) => /^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(c)), 'recovery code format');
  eq((await call(b, 'tk_signup', { p_username: 'piratehunter', p_password: 'another long pw' })).error, 'username_taken', 'case-insensitive uniqueness');
  eq((await call(P, 'tk_signup', { p_username: 'Second1', p_password: 'another long pw' })).error, 'already_signed_in', 'one account per browser session');
  const hashes = await sql(`select password_hash from tk_accounts where lower(username) = 'piratehunter'`);
  ok(hashes[0].password_hash.startsWith('$2a$11$') && !hashes[0].password_hash.includes(PW.P), 'password stored as bcrypt hash');
  const codes = await sql(`select count(*)::int n from tk_recovery_codes rc join tk_accounts a on a.id = rc.account_id where a.username = 'PirateHunter'`);
  eq(codes[0].n, 8, 'recovery code verifiers stored');
  const plain = await sql(`select count(*)::int n from tk_recovery_codes where encode(code_hash, 'escape') like '%' || $1 || '%'`, [P.codes[0].replace(/-/g, '')]);
  eq(plain[0].n, 0, 'recovery codes not stored in plain text');
});

await section('username availability (works before signing in)', async () => {
  eq((await call(anon, 'tk_username_available', { p_username: 'PIRATEHUNTER' })).available, false, 'taken, any case');
  eq((await call(anon, 'tk_username_available', { p_username: 'Fresh_Name' })).available, true, 'free name');
  eq((await call(anon, 'tk_username_available', { p_username: '!!' })).reason, 'invalid', 'invalid name');
});

await section('login, persistent sessions, logout and expiry', async () => {
  const c = await browser();
  eq((await call(c, 'tk_login', { p_username: 'piratehunter', p_password: 'wrong password' })).error, 'bad_credentials', 'wrong password');
  eq((await call(c, 'tk_login', { p_username: 'nobody_here', p_password: 'whatever pw' })).error, 'bad_credentials', 'unknown user same error');
  const r = await call(c, 'tk_login', { p_username: 'PIRATEHUNTER', p_password: PW.P, p_device: 'Phone' });
  ok(r.ok, 'correct login (case-insensitive username)');
  eq((await call(c, 'tk_me')).account.id, P.id, 'session restores the same account');
  const list = await call(P, 'tk_sessions_list');
  eq(list.sessions.length, 2, 'two signed-in devices');
  eq(list.sessions.filter((s) => s.current).length, 1, 'exactly one current session');
  eq((await call(c, 'tk_logout')).ok, true, 'logout');
  eq((await call(c, 'tk_me')).error, 'not_signed_in', 'logged-out session is dead');
  eq((await sql(`select count(*)::int n from auth.users where id = $1`, [c.uid]))[0].n, 0, 'logout removes the transport session');
  await sql(`update tk_sessions set expires_at = now() - interval '1 second' where auth_uid = $1`, [P.uid]);
  eq((await call(P, 'tk_me')).error, 'not_signed_in', 'expired session rejected');
  await sql(`update tk_sessions set expires_at = now() + interval '30 days' where auth_uid = $1`, [P.uid]);
  ok((await call(P, 'tk_me')).ok, 'valid again after renewal');
  const stale = { uid: crypto.randomUUID() };
  eq((await call(stale, 'tk_login', { p_username: 'PirateHunter', p_password: PW.P })).error, 'session_stale', 'unknown transport session refused');
});

await section('brute-force protection', async () => {
  const d = await browser('10.0.0.9');
  for (let i = 0; i < 10; i++) await call(d, 'tk_login', { p_username: 'PirateHunter', p_password: 'guess ' + i });
  eq((await call(d, 'tk_login', { p_username: 'PirateHunter', p_password: PW.P })).error, 'too_many_attempts', 'account locked after 10 failures');
  const d2 = await browser('10.0.0.10');
  eq((await call(d2, 'tk_login', { p_username: 'PirateHunter', p_password: PW.P })).error, 'too_many_attempts', 'lock applies from any browser');
  await sql(`delete from tk_rate_events where bucket like 'loginfail:%'`);
  const e = await browser('10.0.0.11');
  let last;
  for (let i = 0; i < 21; i++) last = await call(e, 'tk_login', { p_username: 'nobody' + i, p_password: 'guess guess' });
  eq(last.error, 'rate_limited', 'per-browser login rate limit');
});

await section('account recovery with one-time codes', async () => {
  const r0 = await browser();
  eq((await call(r0, 'tk_recover', { p_username: 'PirateHunter', p_code: 'AAAA-BBBB-CCCC', p_new_password: 'brand new pw 9' })).error, 'recovery_failed', 'wrong code rejected');
  eq((await call(r0, 'tk_recover', { p_username: 'PirateHunter', p_code: P.codes[0], p_new_password: 'short' })).error, 'password_short', 'new password validated first (code not burned)');
  const r = await call(r0, 'tk_recover', { p_username: 'piratehunter', p_code: P.codes[0].toLowerCase().replace(/-/g, ' '), p_new_password: 'brand new pw 9' });
  ok(r.ok, 'valid code resets the password (format-insensitive)');
  eq(r.codes_left, 7, 'code consumed');
  eq((await call(P, 'tk_me')).error, 'not_signed_in', 'other devices signed out after reset');
  const r1 = await browser();
  eq((await call(r1, 'tk_recover', { p_username: 'PirateHunter', p_code: P.codes[0], p_new_password: 'another new pw 8' })).error, 'recovery_failed', 'codes are single use');
  eq((await call(r1, 'tk_login', { p_username: 'PirateHunter', p_password: PW.P })).error, 'bad_credentials', 'old password no longer works');
  ok((await call(r1, 'tk_login', { p_username: 'PirateHunter', p_password: 'brand new pw 9' })).ok, 'new password works');
  PW.P = 'brand new pw 9';
  P = { ...P, uid: r1.uid };
  eq((await call(P, 'tk_password_change', { p_current: 'nope nope nope', p_new: 'treasure-map-77' })).error, 'bad_password', 'password change needs the current password');
  ok((await call(P, 'tk_password_change', { p_current: PW.P, p_new: 'treasure-map-77' })).ok, 'password changed');
  PW.P = 'treasure-map-77';
  eq((await call(P, 'tk_recovery_codes_regenerate', { p_password: 'wrong wrong' })).error, 'bad_password', 'regenerating codes needs the password');
  const g = await call(P, 'tk_recovery_codes_regenerate', { p_password: PW.P });
  eq(g.recovery_codes.length, 8, 'fresh set of codes');
  P.codes = g.recovery_codes;
  eq((await call(P, 'tk_me')).recovery_codes_left, 8, 'old codes replaced');
});

await section('profile, privacy and settings', async () => {
  eq((await call(P, 'tk_profile_update', { p_avatar: 'token' })).account.avatar, 'token', 'avatar changed');
  eq((await call(P, 'tk_profile_update', { p_avatar: 'hacker.png' })).error, 'invalid_avatar', 'unknown avatar refused');
  const s = await call(P, 'tk_profile_update', { p_estimates: { barely: 30, some: 55, hunted: 95 } });
  const est = s.account.settings.est; eq([est.barely, est.some, est.hunted], [30, 55, 95], 'estimate assumptions saved');
  eq((await call(P, 'tk_profile_update', { p_estimates: { barely: 300, some: 55, hunted: 95 } })).error, 'invalid_estimates', 'out-of-range assumption refused');
  eq((await call(P, 'tk_profile_update', { p_estimates: { barely: 'x', some: 55, hunted: 95 } })).error, 'invalid_estimates', 'non-numeric assumption refused');
});

await section('the client cannot bypass the functions', async () => {
  await throws(call(anon, 'tk_me'), /permission denied/i, 'signed-out visitors cannot call account functions');
  await throws(asRole(db, P, `select * from public.tk_accounts`), /permission denied/i, 'no direct table reads');
  await throws(asRole(db, P, `update public.tk_party_game_members set chests = 999`), /permission denied/i, 'no direct writes');
  await throws(asRole(db, P, `select tkp.leave_party($1, 'x')`, [P.id]), /permission denied/i, 'private helpers not callable');
  const fresh = await browser();
  await throws(call(fresh, 'tk_social_state'), /TK:not_signed_in/, 'transport session without an account is rejected');
});

await section('friends: search, requests, accept/decline/cancel, remove', async () => {
  bob = await signup('Bob_The-Builder', PW.bob);
  cara = await signup('Cara', PW.cara);
  dan = await signup('Dan.Dan', PW.dan);
  eve = await signup('Eve', PW.eve);
  const s = await call(P, 'tk_user_search', { p_query: 'bo' });
  eq(s.results.map((x) => x.username), ['Bob_The-Builder'], 'prefix search');
  eq(s.results[0].relation, 'none', 'no relation yet');
  ok(!('password_hash' in s.results[0]), 'no private fields in results');
  eq((await call(P, 'tk_user_search', { p_query: 'bob_t' })).results.length, 1, 'underscore is literal in search');
  eq((await call(P, 'tk_user_search', { p_query: 'pir' })).results.length, 0, 'you never find yourself');
  const req = await call(P, 'tk_friend_request', { p_account: bob.id });
  eq(req.status, 'outgoing', 'request sent');
  eq((await call(P, 'tk_friend_request', { p_account: bob.id })).request_id, req.request_id, 'duplicate request reuses the pending one');
  eq((await sql(`select count(*)::int n from tk_friend_requests where status = 'pending'`))[0].n, 1, 'only one pending row');
  eq((await call(P, 'tk_user_search', { p_query: 'bob' })).results[0].relation, 'outgoing', 'search shows REQUEST SENT');
  const bs = await call(bob, 'tk_social_state');
  eq(bs.incoming.length, 1, 'recipient sees the request');
  eq(bs.notifications, 1, 'notification badge count');
  eq((await call(bob, 'tk_user_search', { p_query: 'pirate' })).results[0].relation, 'incoming', 'search shows ACCEPT REQUEST');
  eq((await call(bob, 'tk_friend_respond', { p_request: bs.incoming[0].id, p_accept: true })).status, 'friends', 'accepted');
  eq((await call(P, 'tk_social_state')).friends.map((f) => f.username), ['Bob_The-Builder'], 'friendship on both sides (1)');
  eq((await call(bob, 'tk_social_state')).friends.map((f) => f.username), ['PirateHunter'], 'friendship on both sides (2)');
  eq((await call(P, 'tk_friend_request', { p_account: P.id })).error, 'self', 'no self friendship');
  // simultaneous requests become a friendship instead of two pending rows
  eq((await call(cara, 'tk_friend_request', { p_account: dan.id })).status, 'outgoing', 'cara asks dan');
  eq((await call(dan, 'tk_friend_request', { p_account: cara.id })).status, 'friends', 'dan asking back = friends');
  // decline and cancel
  const er = await call(eve, 'tk_friend_request', { p_account: P.id });
  eq((await call(P, 'tk_friend_respond', { p_request: er.request_id, p_accept: false })).status, 'declined', 'declined');
  eq((await call(eve, 'tk_social_state')).outgoing.length, 0, 'declined request gone for sender');
  const pr = await call(P, 'tk_friend_request', { p_account: eve.id });
  ok((await call(P, 'tk_friend_cancel', { p_request: pr.request_id })).ok, 'cancelled');
  eq((await call(eve, 'tk_social_state')).incoming.length, 0, 'cancelled request gone for recipient');
  eq((await call(eve, 'tk_friend_respond', { p_request: pr.request_id, p_accept: true })).error, 'not_found', 'cannot accept a cancelled request');
  ok((await call(P, 'tk_friend_remove', { p_account: bob.id })).ok, 'friend removed');
  eq((await call(bob, 'tk_social_state')).friends.length, 0, 'removal updates both accounts');
});

await section('blocking and search privacy', async () => {
  ok((await call(eve, 'tk_block', { p_account: P.id })).ok, 'eve blocks P');
  eq((await call(P, 'tk_user_search', { p_query: 'eve' })).results.length, 0, 'blocked users cannot find the blocker');
  eq((await call(P, 'tk_friend_request', { p_account: eve.id })).error, 'unavailable', 'no requests to someone who blocked you');
  eq((await call(eve, 'tk_user_search', { p_query: 'pirate' })).results[0].relation, 'blocked', 'blocker sees BLOCKED');
  eq((await call(eve, 'tk_social_state')).blocked.length, 1, 'block list');
  ok((await call(eve, 'tk_unblock', { p_account: P.id })).ok, 'unblocked');
  eq((await call(P, 'tk_user_search', { p_query: 'eve' })).results.length, 1, 'visible again');
  await call(dan, 'tk_profile_update', { p_searchable: false });
  eq((await call(P, 'tk_user_search', { p_query: 'dan' })).results.length, 0, 'unsearchable account hidden from prefix search');
  eq((await call(P, 'tk_user_search', { p_query: 'dan.dan' })).results.length, 1, '...but found by exact username');
  await call(dan, 'tk_profile_update', { p_searchable: true });
});

let party1, game1;
await section('parties: create, invite, join, capacity of four', async () => {
  await befriend(P, bob); await befriend(P, cara); await befriend(P, dan); await befriend(P, eve);
  const c = await call(P, 'tk_party_create');
  ok(c.ok && c.party.is_leader, 'party created with creator as leader');
  party1 = c.party.id;
  eq(c.party.members.length, 1, 'no fictional members');
  eq((await call(P, 'tk_party_create')).error, 'already_in_party', 'one party at a time');
  const frank0 = await signup('Frank', PW.frank); frank = frank0;
  eq((await call(P, 'tk_party_invite', { p_account: frank.id })).error, 'not_friends', 'invites are for friends');
  ok((await call(P, 'tk_party_invite', { p_account: bob.id })).ok, 'invite bob');
  const bi = (await call(bob, 'tk_social_state')).party_invites;
  eq(bi.length, 1, 'bob sees the invite');
  eq(bi[0].leader.username, 'PirateHunter', 'invite shows the leader');
  eq((await call(bob, 'tk_party_invite_respond', { p_invite: bi[0].id, p_accept: true })).status, 'joined', 'bob joined');
  await call(P, 'tk_party_invite', { p_account: cara.id });
  await call(P, 'tk_party_invite', { p_account: dan.id });
  await call(P, 'tk_party_invite', { p_account: eve.id });
  for (const u of [cara, dan]) {
    const inv = (await call(u, 'tk_social_state')).party_invites[0];
    eq((await call(u, 'tk_party_invite_respond', { p_invite: inv.id, p_accept: true })).status, 'joined', u.name + ' joined');
  }
  const ei = (await call(eve, 'tk_social_state')).party_invites[0];
  eq((await call(eve, 'tk_party_invite_respond', { p_invite: ei.id, p_accept: true })).error, 'party_full', 'fifth player refused (party filled meanwhile)');
  const st = await call(bob, 'tk_party_state');
  eq(st.party.members.length, 4, 'four members');
  eq(st.party.is_leader, false, 'bob is not leader');
  eq((await call(bob, 'tk_party_game_start')).error, 'not_leader', 'members cannot start squad games');
  eq((await call(bob, 'tk_party_kick', { p_account: cara.id })).error, 'not_leader', 'members cannot kick');
  eq((await call(eve, 'tk_party_state')).party, null, 'outsiders see no party');
});

await section('party codes', async () => {
  eq((await call(bob, 'tk_party_code_create')).error, 'not_leader', 'only the leader makes codes');
  const code = await call(P, 'tk_party_code_create');
  ok(/^[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(code.code), 'code format');
  eq((await call(eve, 'tk_party_join_code', { p_code: code.code })).error, 'party_full', 'code respects capacity');
  ok((await call(dan, 'tk_party_leave')).ok, 'dan leaves');
  eq((await call(eve, 'tk_party_join_code', { p_code: 'ZZZZ-ZZZZ' })).error, 'invalid_code', 'wrong code');
  ok((await call(eve, 'tk_party_join_code', { p_code: code.code.toLowerCase().replace('-', '') })).ok, 'eve joins by code');
  await sql(`update tk_parties set code_expires_at = now() - interval '1 second' where id = $1`, [party1]);
  eq((await call(dan, 'tk_party_join_code', { p_code: code.code })).error, 'invalid_code', 'expired code');
  let last; for (let i = 0; i < 10; i++) last = await call(dan, 'tk_party_join_code', { p_code: 'AAAA-AAA' + 'B' });
  eq(last.error, 'rate_limited', 'code guessing is rate limited');
  await sql(`delete from tk_rate_events where bucket like 'partyjoin:%'`);
});

await section('squad game: linked contributions, idempotency, late joiners', async () => {
  const s = await call(P, 'tk_party_game_start');
  game1 = s.party.game.id;
  eq(s.party.game.members.length, 4, 'all current members are in the game');
  eq((await call(P, 'tk_party_game_start')).already, true, 'no duplicate active game');
  const ids = [uuid(), uuid(), uuid()];
  for (const id of ids) ok((await call(P, 'tk_party_chest_add', { p_event_id: id, p_opened_at: nowIso() })).linked, 'P chest linked');
  const dup = await call(P, 'tk_party_chest_add', { p_event_id: ids[0], p_opened_at: nowIso() });
  ok(dup.duplicate, 'retried event recognised');
  const b1 = uuid(), b2 = uuid();
  await call(bob, 'tk_party_chest_add', { p_event_id: b1, p_opened_at: nowIso() });
  await call(bob, 'tk_party_chest_add', { p_event_id: b2, p_opened_at: nowIso() });
  ok((await call(bob, 'tk_party_chest_remove', { p_event_id: b2 })).removed, 'bob removes a chest');
  ok((await call(bob, 'tk_party_chest_remove', { p_event_id: b2 })).duplicate, 'repeated removal is harmless');
  eq((await call(P, 'tk_party_chest_remove', { p_event_id: b1 })).removed, false, "you can't remove someone else's chest");
  eq((await call(eve, 'tk_party_chest_add', { p_event_id: b1, p_opened_at: nowIso() })).error, 'conflict', "you can't claim someone else's event id");
  eq((await call(cara, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso(-3600e3) })).reason, 'outside_membership', 'chest from before joining is not attributed');
  const g = (await call(cara, 'tk_party_state')).party.game;
  const by = Object.fromEntries(g.members.map((m) => [m.username, m.chests]));
  eq(by.PirateHunter, 3, 'P has 3'); eq(by['Bob_The-Builder'], 1, 'bob has 1'); eq(g.known_total, 4, 'known squad total 4');
  const ev = await sql(`select count(*)::int n from tk_chest_events where game_id = $1 and removed_at is null`, [game1]);
  eq(ev[0].n, g.known_total, 'total reconstructable from individual events');
  // kick eve mid-game: her contribution stays; dan rejoins late and counts only from now on
  await call(eve, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() });
  ok((await call(P, 'tk_party_kick', { p_account: eve.id })).ok, 'eve removed mid-game');
  eq((await call(eve, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() })).reason, 'no_party', 'removed member no longer adds');
  await call(P, 'tk_party_invite', { p_account: dan.id });
  const di = (await call(dan, 'tk_social_state')).party_invites[0];
  await call(dan, 'tk_party_invite_respond', { p_invite: di.id, p_accept: true });
  ok((await call(dan, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() })).linked, 'late joiner counts from joining');
  const g2 = (await call(dan, 'tk_party_state')).party.game;
  const eveRow = g2.members.find((m) => m.username === 'Eve');
  eq(eveRow && eveRow.chests, 1, "eve's earlier contribution kept"); ok(eveRow && eveRow.left_at, 'eve marked as left');
  eq(g2.known_total, 6, 'known squad total 6');
});

await section('ending games, late delivery, auto next game', async () => {
  eq((await call(bob, 'tk_party_game_end', { p_game: game1 })).error, 'not_leader', 'only the leader ends the game');
  const before = nowIso(-1000);
  const e = await call(P, 'tk_party_game_end', { p_game: game1 });
  eq(e.game.status, 'ended', 'game ended');
  eq((await call(P, 'tk_party_game_end', { p_game: game1 })).already, true, 'double end is harmless');
  eq((await call(bob, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() })).reason, 'no_game', 'no new events after the end');
  const late = await call(bob, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: before });
  ok(late.linked && late.game_id === game1, 'delayed delivery of a chest opened before the end still counts');
  ok((await call(P, 'tk_party_settings', { p_auto_next: true })).party.auto_next, 'auto next game enabled');
  eq((await call(bob, 'tk_party_settings', { p_auto_next: false })).error, 'not_leader', 'members cannot change settings');
  const a = await call(cara, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() });
  ok(a.auto_started && a.game_number === 2, 'first chest starts squad game 2');
  const a2 = await call(P, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() });
  ok(a2.linked && !a2.auto_started && a2.game_number === 2, 'second chest joins the same game');
  eq((await sql(`select count(*)::int n from tk_party_games where party_id = $1 and status = 'active'`, [party1]))[0].n, 1, 'never two active games');
  await call(P, 'tk_party_settings', { p_auto_next: false });
  await call(P, 'tk_party_game_end', { p_game: a.game_id });
});

await section('leadership: transfer, failover, leaving, disbanding', async () => {
  ok((await call(P, 'tk_party_transfer', { p_account: bob.id })).ok, 'leadership transferred');
  eq((await call(P, 'tk_party_game_start')).error, 'not_leader', 'old leader lost control');
  // bob goes silent; P and cara are online -> earliest-joined online member (P) takes over
  await sql(`update tk_presence set last_seen_at = now() - interval '10 minutes' where account_id = $1`, [bob.id]);
  await call(P, 'tk_heartbeat', { p_status: 'online' });
  await call(cara, 'tk_heartbeat', { p_status: 'tracking' });
  eq((await call(cara, 'tk_party_state')).party.leader_id, P.id, 'deterministic failover to the earliest-joined online member');
  ok((await call(P, 'tk_party_leave')).ok, 'leader leaves');
  const st = await call(cara, 'tk_party_state');
  ok(st.party && st.party.leader_id !== P.id && st.party.members.length === 3, 'leadership passed on, party continues');
  const leader = [bob, cara, dan].find((u) => u.id === st.party.leader_id);
  ok((await call(leader, 'tk_party_disband')).ok, 'disbanded');
  for (const u of [bob, cara, dan]) eq((await call(u, 'tk_party_state')).party, null, u.name + ' no longer in a party');
  const h = await call(cara, 'tk_party_history', {});
  ok(h.games.length >= 1 && h.games.some((g) => g.id === game1), 'shared game history survives disbanding');
  // the last member leaving disbands automatically
  const solo = await call(eve, 'tk_party_create');
  await call(eve, 'tk_party_leave');
  eq((await sql(`select status from tk_parties where id = $1`, [solo.party.id]))[0].status, 'disbanded', 'empty party disbanded');
});

await section('blocking inside a party', async () => {
  await call(P, 'tk_party_create');
  await befriend(P, bob);
  await call(P, 'tk_party_invite', { p_account: bob.id });
  await call(bob, 'tk_party_invite_respond', { p_invite: (await call(bob, 'tk_social_state')).party_invites[0].id, p_accept: true });
  eq((await call(P, 'tk_block', { p_account: bob.id })).party_action, 'removed_from_party', 'leader blocking a member removes them');
  eq((await call(bob, 'tk_party_state')).party, null, 'bob is out');
  await call(P, 'tk_unblock', { p_account: bob.id });
  await call(P, 'tk_party_leave');
  await call(cara, 'tk_party_create');
  await befriend(cara, eve);
  await call(cara, 'tk_party_invite', { p_account: eve.id });
  await call(eve, 'tk_party_invite_respond', { p_invite: (await call(eve, 'tk_social_state')).party_invites[0].id, p_accept: true });
  eq((await call(eve, 'tk_block', { p_account: cara.id })).party_action, 'left_party', 'member blocking the leader leaves');
  eq((await call(eve, 'tk_party_join_code', { p_code: (await call(cara, 'tk_party_code_create')).code })).error, 'unavailable', 'cannot rejoin a party with a blocked player');
  await call(eve, 'tk_unblock', { p_account: cara.id });
  await call(cara, 'tk_party_leave');
});

await section('squads fill: exact, estimated, unknown teammates and squad CPG', async () => {
  gina = await signup('Gina', PW.gina);
  await befriend(frank, gina);
  await call(frank, 'tk_profile_update', { p_estimates: { barely: 25, some: 60, hunted: 100 } });
  await call(frank, 'tk_party_create');
  await call(frank, 'tk_party_invite', { p_account: gina.id });
  await call(gina, 'tk_party_invite_respond', { p_invite: (await call(gina, 'tk_social_state')).party_invites[0].id, p_accept: true });
  async function play(fc, gc) {
    const s = await call(frank, 'tk_party_game_start');
    const id = s.party.game.id;
    for (let i = 0; i < fc; i++) await call(frank, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() });
    for (let i = 0; i < gc; i++) await call(gina, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() });
    await call(frank, 'tk_party_game_end', { p_game: id });
    return id;
  }
  const gA = await play(10, 6);
  eq((await call(frank, 'tk_party_history', {})).games[0].completeness, 'unconfirmed', 'two linked players, nothing said about the rest');
  eq((await call(gina, 'tk_party_game_confirm', { p_game: gA })).game.completeness, 'complete', 'confirmed: everyone was linked');
  const gB = await play(4, 4);
  eq((await call(frank, 'tk_party_teammate_set', { p_game: gB, p_slot: 1, p_label: 'Random #1', p_mode: 'exact', p_exact: 5 })).game.completeness, 'reported', 'exact manual count');
  const gC = await play(10, 0);
  const c = await call(frank, 'tk_party_teammate_set', { p_game: gC, p_slot: 1, p_label: null, p_mode: 'estimated', p_level: 'hunted' });
  eq(Number(c.game.teammates[0].estimate_count), 10, 'hunted = 100% of my count');
  eq(c.game.completeness, 'estimated', 'estimate labelled');
  const some = await call(frank, 'tk_party_teammate_set', { p_game: gC, p_slot: 2, p_label: 'Random #2', p_mode: 'estimated', p_level: 'some' });
  eq(Number(some.game.teammates[1].estimate_count), 6, 'some looting = 60% of my count');
  eq((await call(frank, 'tk_party_teammate_set', { p_game: gC, p_slot: 3, p_label: null, p_mode: 'unknown' })).error, 'no_slot', 'never more than four squad members');
  await call(frank, 'tk_party_teammate_clear', { p_game: gC, p_slot: 2 });
  const gD = await play(3, 3);
  const d = await call(gina, 'tk_party_teammate_set', { p_game: gD, p_slot: 1, p_label: 'Who?', p_mode: 'unknown' });
  eq(d.game.completeness, 'partial', 'unknown teammate makes the game partial');
  eq(d.game.estimated_squad_total, 6, 'unknown adds nothing');
  await play(7, 7);                                                                   // gE: unconfirmed
  const gF = await play(50, 50);
  await call(frank, 'tk_party_game_confirm', { p_game: gF });
  ok((await call(frank, 'tk_party_game_void', { p_game: gF, p_void: true })).game.void, 'leader voids a game');
  const s = (await call(gina, 'tk_party_history', {})).stats;
  eq(s.linked, { games: 1, chests: 16 }, 'linked squad CPG uses only fully linked games');
  eq(s.estimated, { games: 3, chests: 16 + 13 + 20 }, 'estimated squad CPG includes reported + estimated games');
  eq(s.partial_games, 1, 'partial games counted separately'); eq(s.unconfirmed_games, 1, 'unconfirmed games counted separately');
  const live = await call(frank, 'tk_party_game_start');
  eq((await call(frank, 'tk_party_teammate_set', { p_game: live.party.game.id, p_slot: 1, p_label: null, p_mode: 'unknown' })).error, 'game_active', 'assess after the game');
  eq((await call(eve, 'tk_party_teammate_set', { p_game: gB, p_slot: 2, p_label: null, p_mode: 'unknown' })).error, 'not_participant', 'outsiders cannot edit');
  const audit = await sql(`select count(*)::int n from tk_corrections where game_id = any($1::uuid[])`, [[gA, gB, gC, gD, gF]]);
  ok(audit[0].n >= 6, 'corrections are logged');
  await call(frank, 'tk_party_game_end', { p_game: live.party.game.id });
});

await section('live-update signals respect privacy', async () => {
  const st = await call(frank, 'tk_party_state');
  const pid = st.party.id;
  eq((await asRole(db, gina, `select count(*)::int n from public.tk_party_signals where party_id = $1`, [pid]))[0].n, 1, 'members can read their party signal');
  eq((await asRole(db, eve, `select count(*)::int n from public.tk_party_signals where party_id = $1`, [pid]))[0].n, 0, 'outsiders cannot');
  eq((await asRole(db, eve, `select count(*)::int n from public.tk_account_signals`))[0].n, 1, 'only your own account signal is visible');
});

await section('presence', async () => {
  await befriend(frank, eve);
  await call(eve, 'tk_heartbeat', { p_status: 'tracking' });
  const f = (await call(frank, 'tk_social_state')).friends.find((x) => x.username === 'Eve');
  eq(f.presence, 'tracking', 'friend shows TRACKING');
  await call(eve, 'tk_profile_update', { p_show_online: false });
  eq((await call(frank, 'tk_social_state')).friends.find((x) => x.username === 'Eve').presence, 'offline', 'hidden status shows offline');
  await sql(`update tk_presence set last_seen_at = now() - interval '5 minutes' where account_id = $1`, [gina.id]);
  eq((await call(frank, 'tk_social_state')).friends.find((x) => x.username === 'Gina').presence, 'offline', 'stale heartbeat = offline (a crashed browser cannot stay online)');
  eq((await call(frank, 'tk_party_state')).party.members.find((m) => m.username === 'Gina').presence, 'offline', 'party sees it too');
  await call(gina, 'tk_heartbeat', {});
  eq((await call(frank, 'tk_party_state')).party.members.find((m) => m.username === 'Gina').presence, 'in_party', 'back online in the party');
});

await section('tracker snapshot sync', async () => {
  const st = { v: 1, sessions: {}, games: {}, gameOrder: [], sessionOrder: [], tokens: [] };
  eq((await call(frank, 'tk_tracker_pull')).version, 0, 'nothing stored yet');
  eq((await call(frank, 'tk_tracker_push', { p_expected: 0, p_state: st, p_device: 'pc' })).version, 1, 'first write');
  const c = await call(frank, 'tk_tracker_push', { p_expected: 0, p_state: st });
  ok(!c.ok && c.error === 'conflict' && c.version === 1, 'stale write refused with the newer copy');
  eq((await call(frank, 'tk_tracker_push', { p_expected: 1, p_state: { ...st, importTotal: 5 } })).version, 2, 'second write');
  eq((await call(frank, 'tk_tracker_pull')).state.importTotal, 5, 'pull returns latest');
  eq((await call(frank, 'tk_tracker_push', { p_expected: 2, p_state: { nope: 1 } })).error, 'invalid_state', 'malformed state refused');
  eq((await call(gina, 'tk_tracker_pull')).version, 0, "accounts never see each other's tracker");
});

await section('account deletion keeps shared history anonymous', async () => {
  const before = (await call(frank, 'tk_party_history', {})).stats;
  eq((await call(gina, 'tk_account_delete', { p_password: 'wrong wrong' })).error, 'bad_password', 'needs the password');
  ok((await call(gina, 'tk_account_delete', { p_password: PW.gina })).ok, 'deleted');
  const h = await call(frank, 'tk_party_history', {});
  const g = h.games.find((x) => x.members.some((m) => m.former));
  ok(g && g.members.find((m) => m.former).username === 'Gina', 'former member shown under their old name');
  eq(h.stats.linked, before.linked, 'squad totals unchanged');
  eq((await call(frank, 'tk_social_state')).friends.some((x) => x.username === 'Gina'), false, 'gone from friends');
  const b = await browser();
  eq((await call(b, 'tk_login', { p_username: 'Gina', p_password: PW.gina })).error, 'bad_credentials', 'cannot log in');
  eq((await call(anon, 'tk_username_available', { p_username: 'gina' })).available, true, 'username freed');
});

await section('housekeeping', async () => {
  const old = await newAnonUser(db);
  await sql(`update auth.users set created_at = now() - interval '2 days' where id = $1`, [old]);
  await sql(`select tkp.cleanup()`);
  eq((await sql(`select count(*)::int n from auth.users where id = $1`, [old]))[0].n, 0, 'abandoned anonymous sessions removed');
  eq((await sql(`select count(*)::int n from auth.users where id = $1`, [frank.uid]))[0].n, 1, 'live sessions kept');
});

console.log(`\n${passed} passed, ${failed} failed`);
if (failed) { console.log('\nFailures:\n - ' + failures.join('\n - ')); process.exit(1); }
