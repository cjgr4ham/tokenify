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

// ------------------------------------------------------------------------- automatic PC tracking
// Companions call with no browser session (the anon role) and their own device secret.
const pc = (ip) => ({ uid: null, ip });
const hex = (n) => [...crypto.getRandomValues(new Uint8Array(n))].map((b) => b.toString(16).padStart(2, '0')).join('');
const playerHash = () => hex(32);
const MIN = 60e3;
function matchPayload(o = {}) {
  const start = o.start ?? Date.now() - 20 * MIN;
  const end = o.end ?? start + 18 * MIN;
  const chests = o.chests ?? 37;
  return {
    player_hash: o.hash, session_id: o.session === undefined ? 'S' + hex(12) : o.session,
    replay_guid: o.guid === undefined ? hex(16) : o.guid,
    started_at: new Date(start).toISOString(), ended_at: new Date(end).toISOString(),
    build: '++Fortnite+Release-42.30-CL-58813929', parser_version: o.parser ?? '1.0.0', registry_version: o.registry ?? 1,
    chests, chest_times: o.times ?? Array.from({ length: chests }, (_, i) => 30 + i * 20),
    breakdown: o.breakdown ?? { regular: chests }, historical: o.historical ?? false, partial: o.partial ?? false,
  };
}
async function linkPc(acc, ip, name = 'Gaming PC') {
  const code = (await call(acc, 'tk_device_pair_start')).code;
  const r = await call(pc(ip), 'tk_device_pair', { p_code: code, p_name: name, p_app_version: '1.0.0' });
  if (!r.ok) throw new Error('pairing failed: ' + r.error);
  return { id: r.device_id, secret: r.secret, ip, who: pc(ip) };
}
const dev = (d, fn, args = {}) => call(d.who, fn, { p_device: d.id, p_secret: d.secret, ...args });
const sorted = (o) => Object.fromEntries(Object.entries(o).sort(([a], [b]) => a.localeCompare(b)));
let sam, bea, cal, dee, beaPc, calPc, deePc, beaHash, calHash, deeHash;

await section('PC pairing: one-time codes, hashed secrets, limits', async () => {
  sam = await signup('Switch_Sam', 'joy-con drift 64');
  bea = await signup('Pc_Bea', 'keyboard warrior 9');
  cal = await signup('Pc_Cal', 'mouse and keys 77');
  dee = await signup('Pc_Dee', 'rgb lights forever');
  await throws(call(anon, 'tk_device_pair_start'), /permission denied/i, 'pairing codes need a signed-in account');
  const start = await call(bea, 'tk_device_pair_start');
  ok(/^[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(start.code), 'code format XXXX-XXXX');
  ok(new Date(start.expires_at) - Date.now() <= 10 * MIN + 5e3, 'code expires within 10 minutes');
  eq((await call(pc('198.51.100.1'), 'tk_device_pair', { p_code: 'ZZZZ-ZZZZ' })).error, 'bad_code', 'wrong code refused');
  const r = await call(pc('198.51.100.1'), 'tk_device_pair', { p_code: start.code.toLowerCase(), p_name: "Bea's PC", p_app_version: '1.0.0' });
  ok(r.ok && /^[A-Za-z0-9_-]{43}$/.test(r.secret), 'paired with a 256-bit secret (any letter case, dash optional)');
  eq(r.account.username, 'Pc_Bea', 'pairing names the account');
  eq(r.tracking, 'auto', 'linking a PC switches the account to automatic tracking');
  beaPc = { id: r.device_id, secret: r.secret, ip: '198.51.100.1', who: pc('198.51.100.1') };
  eq((await call(pc('198.51.100.2'), 'tk_device_pair', { p_code: start.code })).error, 'bad_code', 'a code works only once');
  const stored = await sql(`select encode(secret_hash, 'hex') h from tk_devices where id = $1`, [r.device_id]);
  ok(stored[0].h.length === 64 && !stored[0].h.includes(r.secret), 'only a hash of the secret is stored');
  const c2 = await call(cal, 'tk_device_pair_start');
  await sql(`update tk_device_pairings set expires_at = now() - interval '1 second' where account_id = $1`, [cal.id]);
  eq((await call(pc('198.51.100.3'), 'tk_device_pair', { p_code: c2.code })).error, 'bad_code', 'expired code refused');
  let last; for (let i = 0; i < 12; i++) last = await call(pc('198.51.100.66'), 'tk_device_pair', { p_code: 'AAAA-AAA' + 'B' });
  eq(last.error, 'rate_limited', 'code guessing is rate limited per IP');
  calPc = await linkPc(cal, '198.51.100.3', "Cal's PC");
  deePc = await linkPc(dee, '198.51.100.4', "Dee's PC");
  for (let i = 0; i < 4; i++) await linkPc(sam, '198.51.100.' + (20 + i), 'Spare ' + i);
  eq((await call(sam, 'tk_device_pair_start')).error, undefined, 'a fifth device is allowed');
  await linkPc(sam, '198.51.100.30', 'Spare 5');
  eq((await call(sam, 'tk_device_pair_start')).error, 'too_many_devices', 'at most five linked PCs');
  const st = await call(sam, 'tk_pc_tracking_state');
  eq(st.devices.length, 5, 'devices listed on the website');
  for (const d of st.devices) ok((await call(sam, 'tk_device_revoke', { p_device: d.id })).ok, 'spare PC revoked');
  await call(sam, 'tk_tracking_settings', { p_mode: 'manual' });
  eq((await call(sam, 'tk_pc_tracking_state')).devices.length, 0, 'revoked PCs disappear from the list');
});

await section('PC device secrets: check-ins, status, revocation, rotation', async () => {
  const hi = await dev(beaPc, 'tk_device_hello', { p_status: { state: 'waiting', folder: 'ok', queued: 2, bogus: 'x' }, p_app_version: '1.0.0' });
  ok(hi.ok && hi.account.username === 'Pc_Bea' && hi.tracking === 'auto', 'check-in returns the account and its tracking mode');
  eq(hi.rotate_secret, null, 'no rotation for a new secret');
  const st = (await call(bea, 'tk_pc_tracking_state')).devices[0];
  eq(sorted(st.status), sorted({ state: 'waiting', folder: 'ok', queued: 2, attention: 0 }), 'status stored without unknown fields');
  ok(st.online, 'device shows as online');
  eq((await call(beaPc.who, 'tk_device_hello', { p_device: beaPc.id, p_secret: 'x'.repeat(43) })).error, 'device_unknown', 'wrong secret refused');
  eq((await call(beaPc.who, 'tk_device_hello', { p_device: calPc.id, p_secret: beaPc.secret })).error, 'device_unknown', "one PC's secret doesn't open another");
  eq((await dev(beaPc, 'tk_device_hello', { p_status: { state: 'launch-missiles', queued: 'many' } })).ok, true, 'garbage status is tolerated');
  eq((await call(bea, 'tk_pc_tracking_state')).devices[0].status.state, 'waiting', '...and stored as plain waiting');
  // failed secrets from one IP are throttled, even if the right secret follows
  const att = pc('203.0.113.50');
  for (let i = 0; i < 30; i++) await call(att, 'tk_device_hello', { p_device: beaPc.id, p_secret: hex(32) });
  eq((await call(att, 'tk_device_hello', { p_device: beaPc.id, p_secret: beaPc.secret })).error, 'rate_limited', 'secret guessing is throttled per IP');
  ok((await dev(beaPc, 'tk_device_hello')).ok, 'the real PC on its own IP is unaffected');
  // rotation: a 30-day-old secret gets a successor; using it retires the old one
  await sql(`update tk_devices set secret_issued_at = now() - interval '31 days' where id = $1`, [beaPc.id]);
  const rot = await dev(beaPc, 'tk_device_hello');
  ok(/^[A-Za-z0-9_-]{43}$/.test(rot.rotate_secret || ''), 'new secret issued after 30 days');
  ok((await dev(beaPc, 'tk_device_hello')).ok, 'old secret still works until the new one is used (a lost response is harmless)');
  const old = beaPc.secret;
  beaPc.secret = (await dev(beaPc, 'tk_device_hello')).rotate_secret;    // the latest one issued
  ok((await dev(beaPc, 'tk_device_hello')).ok, 'new secret accepted');
  eq((await call(beaPc.who, 'tk_device_hello', { p_device: beaPc.id, p_secret: old })).error, 'device_unknown', 'old secret retired');
  // a separate, throwaway PC for revocation
  const tmp = await linkPc(bea, '198.51.100.9', 'Old laptop');
  ok((await call(bea, 'tk_device_rename', { p_device: tmp.id, p_name: 'Old laptop (attic)' })).ok, 'device renamed');
  ok((await call(bea, 'tk_device_revoke', { p_device: tmp.id })).ok, 'device revoked from the website');
  eq((await dev(tmp, 'tk_device_hello')).error, 'device_revoked', 'revoked PC is told so');
  eq((await dev(tmp, 'tk_device_import', { p_match: matchPayload({ hash: playerHash() }) })).error, 'device_revoked', 'revoked PC cannot upload');
  eq((await call(cal, 'tk_device_revoke', { p_device: beaPc.id })).error, 'not_found', "you can't revoke someone else's PC");
  const rs = await call(bea, 'tk_device_rescan', { p_device: beaPc.id });
  ok(rs.ok && (await dev(beaPc, 'tk_device_hello')).rescan === true, 'website RESCAN reaches the PC');
  eq((await dev(beaPc, 'tk_device_hello')).rescan, false, '...once');
});

await section('PC profiles: one Fortnite account belongs to one TOKENIFY account', async () => {
  beaHash = playerHash(); calHash = playerHash(); deeHash = playerHash();
  eq((await dev(beaPc, 'tk_device_import', { p_match: matchPayload({ hash: beaHash }) })).error, 'profile_unclaimed', 'nothing is uploaded for an unconfirmed Fortnite account');
  ok((await dev(beaPc, 'tk_device_profile_claim', { p_player_hash: beaHash, p_label: 'BeaBuilds ★' })).ok, 'Bea confirms her Fortnite account');
  eq((await dev(calPc, 'tk_device_profile_claim', { p_player_hash: beaHash, p_label: 'not me' })).error, 'profile_conflict', "Cal's PC can't take Bea's Fortnite account");
  eq((await dev(calPc, 'tk_device_import', { p_match: matchPayload({ hash: beaHash }) })).error, 'profile_conflict', "...or upload Bea's matches");
  ok((await dev(calPc, 'tk_device_profile_claim', { p_player_hash: calHash })).ok, 'Cal claims his own');
  ok((await dev(deePc, 'tk_device_profile_claim', { p_player_hash: deeHash })).ok, 'Dee claims hers');
  const prof = (await call(bea, 'tk_pc_tracking_state')).profiles;
  eq(prof.map((p) => p.label), ['BeaBuilds ★'], 'profile listed on the website with its label');
  eq((await dev(beaPc, 'tk_device_profile_claim', { p_player_hash: 'nothex' })).error, 'invalid', 'bad hash refused');
  const spare = playerHash();
  ok((await dev(beaPc, 'tk_device_profile_claim', { p_player_hash: spare })).ok, 'a second Fortnite account for Bea');
  ok((await call(bea, 'tk_replay_profile_unlink', { p_player_hash: spare })).ok, 'unlinked from the website');
  ok((await dev(calPc, 'tk_device_profile_claim', { p_player_hash: spare })).ok, 'an unlinked Fortnite account can be claimed elsewhere');
});

let beaMatch;
await section('replay imports: idempotent, validated, never double counted', async () => {
  const p = matchPayload({ hash: beaHash, chests: 37 });
  const a = await dev(beaPc, 'tk_device_import', { p_match: p });
  ok(a.ok && a.status === 'created', 'one finished match imported');
  eq(a.match.chests, 37, 'with its chest count');
  beaMatch = a.match;
  eq((await dev(beaPc, 'tk_device_import', { p_match: p })).status, 'unchanged', 'the same match again is recognised (retry)');
  eq((await sql(`select count(*)::int n from tk_replay_matches where account_id = $1`, [bea.id]))[0].n, 1, 'still one record');
  const lst = await call(bea, 'tk_replay_matches', {});
  eq(lst.matches.length, 1, 'the website sees it');
  eq(lst.matches[0].chest_times.length, 37, 'with one time per chest');
  // a newer parser corrects the count; the old result is kept for audit
  const v2 = await dev(beaPc, 'tk_device_import', { p_match: { ...p, parser_version: '1.1.0', chests: 38, chest_times: [...p.chest_times, 900], breakdown: { regular: 38 } } });
  eq(v2.status, 'updated', 'newer parser result replaces the old one');
  eq(v2.match.chests, 38, 'new count');
  eq(v2.match.revisions, 1, 'previous result kept');
  eq((await dev(beaPc, 'tk_device_import', { p_match: p })).status, 'unchanged', 'an older companion cannot undo it');
  // the player's own correction wins over any later parser
  const cor = await call(bea, 'tk_replay_correct', { p_match: a.match.id, p_chests: 40, p_reason: 'two chests behind a wall' });
  ok(cor.ok && cor.match.chests === 40 && cor.match.replay_chests === 38, 'corrected count, replay count kept');
  const v3 = await dev(beaPc, 'tk_device_import', { p_match: { ...p, parser_version: '1.2.0', chests: 39, chest_times: [...p.chest_times, 900, 950], breakdown: { regular: 39 } } });
  eq(v3.status, 'updated_kept_correction', 'parser update stored...');
  ok(v3.match.chests === 40 && v3.match.replay_chests === 39, '...but the correction stays');
  eq((await call(bea, 'tk_replay_correct', { p_match: a.match.id, p_chests: null })).match.chests, 39, 'clearing the correction');
  eq((await call(cal, 'tk_replay_correct', { p_match: a.match.id, p_chests: 1 })).error, 'not_found', "nobody else can correct it");
  // validation
  const bad = async (over, why) => eq((await dev(beaPc, 'tk_device_import', { p_match: { ...matchPayload({ hash: beaHash }), ...over } })).detail, why, 'rejected: ' + why);
  await bad({ chests: 999, chest_times: [] }, 'chests');
  await bad({ chests: 3 }, 'chest_times');
  await bad({ chest_times: Array(37).fill(99999) }, 'chest_times');
  await bad({ started_at: nowIso(3600e3), ended_at: nowIso(4000e3) }, 'times');
  await bad({ ended_at: nowIso(-50 * MIN) }, 'times');
  await bad({ session_id: null, replay_guid: null }, 'no_match_id');
  await bad({ session_id: "x'; drop table--" }, 'session_id');
  await bad({ parser_version: '0.9.0' }, 'parser_outdated');
  await bad({ breakdown: { a: 1, b: 1, c: 1, d: 1, e: 1, f: 1, g: 1, h: 1, i: 1, j: 1, k: 1, l: 1, m: 1, n: 1, o: 1, p: 1, q: 1 } }, 'breakdown');
  // nothing from before this season (no Pirate Token back then), not even as history
  await bad({ started_at: '2026-10-31T23:00:00Z', ended_at: '2026-10-31T23:20:00Z' }, 'before_season');
  await bad({ started_at: '2026-10-31T23:00:00Z', ended_at: '2026-10-31T23:20:00Z', historical: true }, 'before_season');
  eq((await dev(beaPc, 'tk_device_hello')).season_start.slice(0, 19), '2026-11-01T04:00:00', 'the PC learns the season start (Nov 1, 2026, 12:00 AM Eastern)');
  // the recording first seen without a match id is upgraded in place, not duplicated
  const guid = hex(16), sess = 'S' + hex(12);
  const n1 = await dev(beaPc, 'tk_device_import', { p_match: matchPayload({ hash: beaHash, session: null, guid, start: Date.now() - 300 * MIN }) });
  ok(n1.match.match_key.startsWith('rg:') && !n1.match.has_match_id, 'stored under its replay id');
  const n2 = await dev(beaPc, 'tk_device_import', { p_match: matchPayload({ hash: beaHash, session: sess, guid, start: Date.now() - 300 * MIN, parser: '1.2.1' }) });
  ok(n2.match.id === n1.match.id && n2.match.match_key === 'fn:' + sess.toLowerCase(), 'same record, now with the match id');
  // reinstalling: a brand-new link of the same account recognises what is already there
  const re = await linkPc(bea, '198.51.100.11', 'Reinstalled');
  eq((await dev(re, 'tk_device_import', { p_match: p })).status, 'unchanged', 'reinstalled companion re-sending history adds nothing');
  eq((await dev(re, 'tk_device_matches')).matches.length, 2, 'and can list what TOKENIFY already has');
  await call(bea, 'tk_device_revoke', { p_device: re.id });
  // two PCs, two friends: separate records
  const c1 = await dev(calPc, 'tk_device_import', { p_match: matchPayload({ hash: calHash, chests: 12, start: Date.now() - 400 * MIN }) });
  ok(c1.ok && c1.status === 'created', "Cal's match");
  eq((await call(bea, 'tk_replay_matches', {})).matches.length, 2, "Bea doesn't see Cal's matches");
  eq((await call(cal, 'tk_replay_matches', {})).matches.length, 1, 'Cal sees his own');
  await throws(asRole(db, bea, `select * from public.tk_replay_matches`), /permission denied/i, 'no direct table reads');
  await throws(asRole(db, anon, `select * from public.tk_devices`), /permission denied/i, 'devices hidden from visitors');
  await throws(call(anon, 'tk_replay_matches', {}), /permission denied/i, 'match history needs a session');
});

await section('replay imports: inferred sessions, history, offline catch-up, exclusion', async () => {
  const base = Date.now() - 900 * MIN;
  const m1 = await dev(deePc, 'tk_device_import', { p_match: matchPayload({ hash: deeHash, start: base, end: base + 20 * MIN }) });
  const m2 = await dev(deePc, 'tk_device_import', { p_match: matchPayload({ hash: deeHash, start: base + 32 * MIN, end: base + 50 * MIN }) });
  const m3 = await dev(deePc, 'tk_device_import', { p_match: matchPayload({ hash: deeHash, start: base + 200 * MIN, end: base + 220 * MIN }) });
  eq(m1.match.auto_session, m2.match.auto_session, 'matches 12 minutes apart share an inferred session');
  ok(m3.match.auto_session !== m2.match.auto_session, 'a match hours later starts a new one');
  // a PC that was offline uploads later, out of order: grouping still holds
  const m0 = await dev(deePc, 'tk_device_import', { p_match: matchPayload({ hash: deeHash, start: base - 25 * MIN, end: base - 5 * MIN }) });
  eq(m0.match.auto_session, m1.match.auto_session, 'a late upload joins the session it belongs to');
  const h = await dev(deePc, 'tk_device_import', { p_match: matchPayload({ hash: deeHash, start: base - 600 * MIN, end: base - 580 * MIN, historical: true }) });
  ok(h.match.historical && ![m0, m1, m2, m3].some((m) => m.match.auto_session === h.match.auto_session), 'back-filled history never joins live sessions');
  await call(dee, 'tk_tracking_settings', { p_auto_gap: 10 });
  const m4 = await dev(deePc, 'tk_device_import', { p_match: matchPayload({ hash: deeHash, start: base + 232 * MIN, end: base + 250 * MIN }) });
  ok(m4.match.auto_session !== m3.match.auto_session, 'the grouping gap is the player\'s setting (10 min here)');
  eq((await call(dee, 'tk_tracking_settings', { p_auto_gap: 2 })).error, 'invalid_gap', 'gap limits');
  eq((await call(dee, 'tk_tracking_settings', { p_mode: 'robot' })).error, 'invalid_mode', 'mode limits');
  const ex = await call(dee, 'tk_replay_exclude', { p_match: m4.match.id, p_excluded: true });
  ok(ex.ok && ex.match.excluded, '"not my match" excludes it');
  eq((await call(dee, 'tk_pc_tracking_state')).matches, 5, 'excluded matches are not counted');
  eq((await call(dee, 'tk_replay_exclude', { p_match: m4.match.id, p_excluded: false })).match.excluded, false, 'and can be included again');
  const since = (await call(dee, 'tk_replay_matches', {})).matches.at(-1).updated_at;
  const delta = await call(dee, 'tk_replay_matches', { p_since: since });
  ok(delta.matches.length >= 1 && delta.matches.every((m) => m.updated_at >= since), 'incremental fetch returns only changes');
  ok((await dev(deePc, 'tk_device_import', { p_match: matchPayload({ hash: deeHash, start: Date.parse('2026-11-01T04:05:00Z'), end: Date.parse('2026-11-01T04:25:00Z'), chests: 3, historical: true }) })).ok,
    'a match just after the season start counts');
  // tidy up so the squad test below starts clean
  await sql(`delete from tk_replay_matches where account_id = $1`, [dee.id]);
  await call(dee, 'tk_tracking_settings', { p_auto_gap: 30 });
});

await section('mixed squad: one Switch player counting by hand + three PCs on replays', async () => {
  // Sam (Switch 2, manual) leads; Bea, Cal and Dee play on PC with the companion
  eq((await dev(deePc, 'tk_device_hello')).squad, null, 'no squad game yet: the tracker is told no squad total');
  await call(sam, 'tk_party_create');
  const code = (await call(sam, 'tk_party_code_create')).code;
  for (const p of [bea, cal, dee]) ok((await call(p, 'tk_party_join_code', { p_code: code })).ok, 'joined by code');
  const g0 = (await call(sam, 'tk_party_game_start')).party.game;
  const gid = g0.id;
  const src = Object.fromEntries(g0.members.map((m) => [m.username, m.source]));
  eq(sorted(src), sorted({ Switch_Sam: 'live', Pc_Bea: 'replay', Pc_Cal: 'replay', Pc_Dee: 'replay' }), 'how each member is counted');
  await sql(`update tk_party_games set started_at = now() - interval '19 minutes' where id = $1`, [gid]);
  for (let i = 0; i < 38; i++) await call(sam, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() });
  let g = (await call(bea, 'tk_party_state')).party.game;
  eq(g.known_total, 38, 'only known chests are totalled');
  eq(g.pending_count, 3, 'three players awaiting their replay');
  ok(g.members.filter((m) => m.pending).every((m) => m.chests === null && m.replay_state === 'awaiting'), 'pending players are not shown as zero');
  eq(g.completeness, 'pending', 'the squad total is not final');
  await call(sam, 'tk_party_game_end', { p_game: gid });
  await sql(`update tk_party_games set ended_at = now() - interval '1 minute' where id = $1`, [gid]);
  await dev(calPc, 'tk_device_hello', { p_status: { state: 'processing' } });
  g = (await call(sam, 'tk_party_state')).party.last_game;
  eq(g.members.find((m) => m.username === 'Pc_Cal').replay_state, 'processing', 'a PC reading its replay shows as processing');
  // the three replays arrive, all from the same Fortnite match
  const match = 'S' + hex(12), start = Date.now() - 20 * MIN, end = Date.now() - 2 * MIN;
  const rb = await dev(beaPc, 'tk_device_import', { p_match: matchPayload({ hash: beaHash, chests: 42, session: match, start, end }) });
  eq(rb.match.party.link, 'time-window', 'first replay tied to the squad game by membership + time');
  g = (await call(sam, 'tk_party_state')).party.last_game;
  eq([g.known_total, g.pending_count], [80, 2], '80 known chests, 2 players pending');
  let sq = (await dev(beaPc, 'tk_device_hello')).squad;
  eq([sq.chests, sq.complete, sq.members, sq.reported], [80, false, 4, 2], "the tracker's squad line: 80 so far, 2 of 4 counted, not final");
  const rc = await dev(calPc, 'tk_device_import', { p_match: matchPayload({ hash: calHash, chests: 35, session: match, start: start + 5e3, end }) });
  const rd = await dev(deePc, 'tk_device_import', { p_match: matchPayload({ hash: deeHash, chests: 47, session: match, start: start + 9e3, end }) });
  ok(rc.match.party.link === 'match-id' && rd.match.party.link === 'match-id', 'teammates tied by the shared Fortnite match id');
  g = (await call(sam, 'tk_party_state')).party.last_game;
  const by = Object.fromEntries(g.members.map((m) => [m.username, [m.chests, m.source]]));
  eq(sorted(by), sorted({ Switch_Sam: [38, 'live'], Pc_Bea: [42, 'replay'], Pc_Cal: [35, 'replay'], Pc_Dee: [47, 'replay'] }), 'each contribution with its owner and source');
  eq([g.known_total, g.pending_count, g.completeness], [162, 0, 'complete'], 'TOTAL SQUAD CHESTS 162, complete');
  eq((await sql(`select count(*)::int n from tk_party_games where party_id = $1`, [g.party_id]))[0].n, 1, 'three uploads, one shared game');
  eq((await dev(calPc, 'tk_device_import', { p_match: matchPayload({ hash: calHash, chests: 35, session: match, start: start + 5e3, end, guid: null }) })).status, 'unchanged', "Cal's companion retrying adds nothing");
  eq((await call(sam, 'tk_party_state')).party.last_game.known_total, 162, 'still 162');
  // a correction flows into the squad total; leaving keeps contributions
  const sig = (await sql(`select version from tk_party_signals where party_id = $1`, [g.party_id]))[0].version;
  await call(dee, 'tk_replay_correct', { p_match: rd.match.id, p_chests: 48, p_reason: 'missed one' });
  eq((await call(sam, 'tk_party_state')).party.last_game.known_total, 163, 'correction updates the squad total');
  sq = (await dev(calPc, 'tk_device_hello')).squad;
  eq([sq.chests, sq.complete, sq.members, sq.reported], [163, true, 4, 4], "every member's tracker gets the final squad total");
  ok(Math.abs(Date.parse(sq.ended_at) - Date.now()) < 10 * MIN, 'with the time the squad game ended');
  const season = (await dev(deePc, 'tk_device_hello')).squad_season;
  eq([season.games, season.mates_chests], [1, 163 - 48], "the tracker's squad total: what the other three opened (38 + 42 + 35), never Dee's own 48");
  await sql(`update tk_party_games set void = true where id = $1`, [gid]);
  eq((await dev(deePc, 'tk_device_hello')).squad_season.mates_chests, 0, 'a voided squad game adds nothing');
  await sql(`update tk_party_games set void = false where id = $1`, [gid]);
  ok((await sql(`select version from tk_party_signals where party_id = $1`, [g.party_id]))[0].version > sig, 'and pushes a live update');
  const hist = await call(sam, 'tk_party_history', {});
  eq([hist.stats.linked.games, hist.stats.linked.chests], [1, 163], 'linked squad stats use replay counts');
  ok((await call(cal, 'tk_party_leave')).ok, 'Cal leaves the party');
  eq((await call(sam, 'tk_party_history', {})).games[0].members.find((m) => m.username === 'Pc_Cal').chests, 35, "Cal's contribution stays with that game");
  ok((await call(cal, 'tk_party_join_code', { p_code: code })).ok, 'Cal rejoins');
});

await section('squad replays: hybrid, missing, conflicts, manual linking', async () => {
  const partyId = (await call(sam, 'tk_party_state')).party.id;
  await sql(`update tk_party_games set started_at = started_at - interval '2 hours', ended_at = ended_at - interval '2 hours' where party_id = $1`, [partyId]);
  // Bea switches to hybrid: she counts live, her replay then replaces the live count (kept for reference)
  await call(bea, 'tk_tracking_settings', { p_mode: 'hybrid' });
  const g1 = (await call(sam, 'tk_party_game_start')).party.game.id;
  await sql(`update tk_party_games set started_at = now() - interval '19 minutes' where id = $1`, [g1]);
  for (let i = 0; i < 10; i++) await call(bea, 'tk_party_chest_add', { p_event_id: uuid(), p_opened_at: nowIso() });
  let g = (await call(sam, 'tk_party_state')).party.game;
  const bLive = g.members.find((m) => m.username === 'Pc_Bea');
  ok(bLive.source === 'live' && bLive.chests === 10, 'hybrid player counts live during the game');
  // this replay has no Fortnite match id (only its recording id), so it is tied by membership + time
  const first = await dev(beaPc, 'tk_device_import', { p_match: matchPayload({ hash: beaHash, chests: 12, session: null, start: Date.now() - 20 * MIN, end: Date.now() - MIN }) });
  eq(first.match.party.link, 'time-window', 'tied to the running squad game');
  g = (await call(sam, 'tk_party_state')).party.game;
  const bRep = g.members.find((m) => m.username === 'Pc_Bea');
  ok(bRep.chests === 12 && bRep.live_chests === 10 && bRep.source === 'replay', 'replay replaces her live count; never 10 + 12');
  // a different recording overlapping the same squad game is flagged for the player, never added
  const dup = await dev(beaPc, 'tk_device_import', { p_match: matchPayload({ hash: beaHash, chests: 9, session: null, start: Date.now() - 15 * MIN, end: Date.now() - 30e3 }) });
  eq(dup.match.party.link, 'conflict', 'a second replay for the same squad game needs confirming');
  eq((await call(sam, 'tk_party_state')).party.game.members.find((m) => m.username === 'Pc_Bea').chests, 12, 'and changes nothing on its own');
  // the earlier squad game (now 2 hours back) is tied to one Fortnite match id; a replay of another match
  // in that same time window is not linked to it
  const other = await dev(beaPc, 'tk_device_import', { p_match: matchPayload({ hash: beaHash, chests: 4, session: 'S' + hex(12), start: Date.now() - 138 * MIN, end: Date.now() - 122 * MIN }) });
  eq(other.match.party, null, 'a replay of a different Fortnite match is never tied to a game by time alone');
  const cand = await call(bea, 'tk_replay_party_candidates', { p_match: dup.match.id });
  ok(cand.games.some((x) => x.id === g1 && x.other_replay), 'candidates show the squad game already has a replay');
  eq((await call(bea, 'tk_replay_attach', { p_match: dup.match.id, p_game: g1 })).error, 'slot_taken', 'replacing needs an explicit choice');
  ok((await call(bea, 'tk_replay_attach', { p_match: dup.match.id, p_game: g1, p_replace: true })).ok, 'player picks the right match');
  eq((await call(sam, 'tk_party_state')).party.game.members.find((m) => m.username === 'Pc_Bea').chests, 9, 'now that one counts');
  ok((await call(bea, 'tk_replay_detach', { p_match: dup.match.id })).ok, 'and can be unlinked');
  eq((await call(cal, 'tk_replay_attach', { p_match: dup.match.id, p_game: g1 })).error, 'not_found', "nobody else can link Bea's match");
  // Dee's replay never comes: pending, then missing (unknown, not zero), then she can use her live count
  await call(sam, 'tk_party_game_end', { p_game: g1 });
  await sql(`update tk_party_games set started_at = now() - interval '70 minutes', ended_at = now() - interval '50 minutes' where id = $1`, [g1]);
  await dev(calPc, 'tk_device_hello', { p_status: { state: 'waiting' } });   // Cal's PC finished processing earlier
  g = (await call(sam, 'tk_party_history', {})).games.find((x) => x.id === g1);
  const dRow = g.members.find((m) => m.username === 'Pc_Dee');
  ok(dRow.missing && dRow.chests === null, 'a replay that never arrived is "missing", not zero');
  eq(g.completeness, 'partial', 'the game is marked incomplete');
  ok((await call(dee, 'tk_party_use_live_count', { p_game: g1 })).ok, 'Dee chooses her live count instead');
  eq((await call(sam, 'tk_party_history', {})).games.find((x) => x.id === g1).members.find((m) => m.username === 'Pc_Dee').chests, 0, 'counted as what she tracked live');
  // matches outside any squad game stay personal; manual-mode accounts are never auto-linked
  const solo = await dev(calPc, 'tk_device_import', { p_match: matchPayload({ hash: calHash, chests: 5, start: Date.now() - 600 * MIN }) });
  eq(solo.match.party, null, 'a solo match is not tied to any squad game');
  await call(cal, 'tk_tracking_settings', { p_mode: 'manual' });
  const g2 = (await call(sam, 'tk_party_game_start')).party.game;
  eq(g2.members.find((m) => m.username === 'Pc_Cal').source, 'live', 'manual mode: counted live in new squad games');
  await sql(`update tk_party_games set started_at = now() - interval '19 minutes' where id = $1`, [g2.id]);
  const man = await dev(calPc, 'tk_device_import', { p_match: matchPayload({ hash: calHash, chests: 7, start: Date.now() - 20 * MIN, end: Date.now() - MIN }) });
  eq(man.match.party, null, 'manual mode: replays are stored but not added to squad games');
  await call(sam, 'tk_party_game_end', { p_game: g2.id });
  const hist = await dev(deePc, 'tk_device_import', { p_match: matchPayload({ hash: deeHash, chests: 3, historical: true, start: Date.now() - 20 * MIN, end: Date.now() - MIN }) });
  eq(hist.match.party, null, 'back-filled history is never added to squad games');
});

await section('corrections made in the Windows tracker reach the website', async () => {
  await call(cal, 'tk_tracking_settings', { p_mode: 'auto' });
  const start = Date.now() - 1300 * MIN;
  const m = (await dev(calPc, 'tk_device_import', { p_match: matchPayload({ hash: calHash, chests: 20, start, end: start + 15 * MIN }) })).match;
  const key = m.match_key;
  const c1 = await dev(calPc, 'tk_device_correct', { p_player_hash: calHash, p_match_key: key, p_chests: 23, p_excluded: false, p_reason: 'three behind a wall' });
  ok(c1.ok && c1.status === 'updated', 'the tracker can correct a count');
  eq([c1.match.chests, c1.match.replay_chests, c1.match.corrected_chests, c1.match.correction_reason], [23, 20, 23, 'three behind a wall'], "the replay's own count stays on record next to the correction");
  eq((await call(cal, 'tk_replay_matches', {})).matches.find((x) => x.id === m.id).chests, 23, 'the website shows the corrected count');
  eq((await dev(calPc, 'tk_device_correct', { p_player_hash: calHash, p_match_key: key, p_chests: 23, p_excluded: false, p_reason: 'three behind a wall' })).status, 'unchanged', 'sending it again changes nothing');
  eq((await dev(calPc, 'tk_device_matches')).matches.find((x) => x.match_key === key).corrected, true, 'the tracker reads it back as corrected');
  // a newer parser must not undo the player's correction
  const again = await dev(calPc, 'tk_device_import', { p_match: { ...matchPayload({ hash: calHash, chests: 21, start, end: start + 15 * MIN, parser: '2.0.0' }), session_id: key.slice(3) } });
  eq([again.status, again.match.chests, again.match.replay_chests], ['updated_kept_correction', 23, 21], 'a re-read replay updates the replay count and keeps the correction');
  const ex = await dev(calPc, 'tk_device_correct', { p_player_hash: calHash, p_match_key: key.toUpperCase(), p_chests: 23, p_excluded: true });
  ok(ex.ok && ex.match.excluded, '"leave this match out" from the tracker (match ids are not case sensitive)');
  const back = await dev(calPc, 'tk_device_correct', { p_player_hash: calHash, p_match_key: key, p_chests: null, p_excluded: false });
  eq([back.match.excluded, back.match.corrected_chests, back.match.chests], [false, null, 21], 'removing the correction and putting the match back');
  eq((await dev(calPc, 'tk_device_correct', { p_player_hash: calHash, p_match_key: key, p_chests: 401 })).error, 'invalid_count', 'count limits');
  eq((await dev(calPc, 'tk_device_correct', { p_player_hash: calHash, p_match_key: 'fn:nosuchmatch0000', p_chests: 1 })).error, 'not_found', 'unknown match');
  eq((await dev(calPc, 'tk_device_correct', { p_player_hash: calHash, p_match_key: "fn:x'; drop table tk_accounts;--", p_chests: 1 })).error, 'invalid', 'malformed match id refused');
  eq((await dev(beaPc, 'tk_device_correct', { p_player_hash: calHash, p_match_key: key, p_chests: 1 })).error, 'not_found', "another player's tracker cannot change it");
  eq((await call(beaPc.who, 'tk_device_correct', { p_device: calPc.id, p_secret: 'x'.repeat(43), p_player_hash: calHash, p_match_key: key, p_chests: 1 })).error, 'device_unknown', 'and not without the device secret');
  eq((await call(cal, 'tk_device_correct', { p_device: calPc.id, p_secret: 'x'.repeat(43), p_player_hash: calHash, p_match_key: key, p_chests: 1 })).error, 'device_unknown', "being signed in as the owner is no substitute for the PC's secret");
  await sql(`delete from tk_replay_matches where id = $1`, [m.id]);
  await call(cal, 'tk_tracking_settings', { p_mode: 'manual' });
});

await section('companion disconnect and housekeeping', async () => {
  const tmp = await linkPc(dee, '198.51.100.40', 'Temp');
  ok((await dev(tmp, 'tk_device_disconnect')).ok, 'companion can disconnect itself');
  eq((await dev(tmp, 'tk_device_hello')).error, 'device_revoked', 'and stays disconnected');
  ok((await dev(tmp, 'tk_device_disconnect')).ok, 'disconnecting twice is harmless');
  await call(dee, 'tk_device_pair_start');
  await sql(`update tk_device_pairings set expires_at = now() - interval '2 days' where account_id = $1 and used_at is null`, [dee.id]);
  await sql(`select tkp.cleanup()`);
  eq((await sql(`select count(*)::int n from tk_device_pairings where account_id = $1 and used_at is null`, [dee.id]))[0].n, 0, 'expired pairing codes cleaned up');
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
