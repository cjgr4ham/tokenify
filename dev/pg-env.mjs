// Local stand-in for the parts of Supabase that TOKENIFY's SQL relies on, running the *real*
// supabase/accounts-v2.sql inside PGlite (Postgres compiled to WebAssembly). Used by the backend test
// suite and by the local development backend; nothing here talks to the internet.
import { PGlite } from '@electric-sql/pglite';
import { pgcrypto } from '@electric-sql/pglite/contrib/pgcrypto';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
export const SCHEMA_FILE = path.join(here, '..', 'supabase', 'accounts-v2.sql');

// Mirrors the Supabase environment: API roles, the auth schema with auth.uid(), the realtime
// publication, and Supabase's default grants (so the migration's lock-down is genuinely exercised).
export const BOOTSTRAP = String.raw`
create schema if not exists extensions;
create extension if not exists pgcrypto with schema extensions;
do $$ begin
  if not exists (select 1 from pg_roles where rolname = 'anon') then create role anon nologin; end if;
  if not exists (select 1 from pg_roles where rolname = 'authenticated') then create role authenticated nologin; end if;
  if not exists (select 1 from pg_roles where rolname = 'service_role') then create role service_role nologin bypassrls; end if;
end $$;
grant usage on schema public to anon, authenticated, service_role;
create schema if not exists auth;
grant usage on schema auth to anon, authenticated;
create table if not exists auth.users (
  id uuid primary key default gen_random_uuid(),
  is_anonymous boolean not null default true,
  created_at timestamptz not null default now()
);
create or replace function auth.uid() returns uuid language sql stable as $$
  select nullif(coalesce(nullif(current_setting('request.jwt.claim.sub', true), ''),
                         (nullif(current_setting('request.jwt.claims', true), '')::jsonb ->> 'sub')), '')::uuid
$$;
grant execute on function auth.uid() to anon, authenticated;
do $$ begin
  if not exists (select 1 from pg_publication where pubname = 'supabase_realtime') then create publication supabase_realtime; end if;
end $$;
alter default privileges in schema public grant all on tables to anon, authenticated, service_role;
alter default privileges in schema public grant all on functions to anon, authenticated, service_role;
`;

export async function createDb(opts = {}) {
  const db = new PGlite({ extensions: { pgcrypto }, ...(opts.dataDir ? { dataDir: opts.dataDir } : {}) });
  await db.exec(BOOTSTRAP);
  await db.exec(await readFile(SCHEMA_FILE, 'utf8'));
  if (opts.seasonStart) await setSeasonStart(db, opts.seasonStart);
  return db;
}

// Test-only: moves the season start (normally Nov 1, 2026) so old sample replays count. Never used live.
export async function setSeasonStart(db, iso) {
  const when = new Date(iso);
  if (isNaN(when)) throw new Error('bad season start: ' + iso);
  await db.exec(`create or replace function tkp.season_start() returns timestamptz language sql immutable set search_path = '' as $$ select timestamptz '${when.toISOString()}' $$`);
}

const FN = /^[a-z_][a-z0-9_]*$/;
const ARG = /^p_[a-z_]+$/;

// Calls public.<fn>(named args) exactly the way PostgREST would: inside a transaction, as the API
// role, with the JWT claims and request headers set as GUCs. Returns the function's JSON result.
export async function rpc(db, who, fn, args = {}) {
  if (!FN.test(fn)) throw new Error('bad function name');
  const keys = Object.keys(args);
  keys.forEach((k) => { if (!ARG.test(k)) throw new Error('bad argument name ' + k); });
  const role = who && who.uid ? 'authenticated' : 'anon';
  const claims = who && who.uid ? JSON.stringify({ sub: who.uid, role: 'authenticated', is_anonymous: true, aud: 'authenticated' }) : '';
  const headers = JSON.stringify(who && who.ip ? { 'x-forwarded-for': who.ip } : {});
  const values = keys.map((k) => { const v = args[k]; return v !== null && typeof v === 'object' ? JSON.stringify(v) : v; });
  const call = `select public.${fn}(${keys.map((k, i) => `${k} => $${i + 1}`).join(', ')}) as r`;
  return db.transaction(async (tx) => {
    await tx.exec(`set local role ${role}`);
    await tx.query(`select set_config('request.jwt.claims', $1, true), set_config('request.headers', $2, true)`, [claims, headers]);
    const res = await tx.query(call, values);
    return res.rows[0].r;
  });
}

// Runs arbitrary SQL as a given API role (used to prove the client can't read tables directly).
export async function asRole(db, who, sql, params = []) {
  const role = who && who.uid ? 'authenticated' : 'anon';
  const claims = who && who.uid ? JSON.stringify({ sub: who.uid, role: 'authenticated' }) : '';
  return db.transaction(async (tx) => {
    await tx.exec(`set local role ${role}`);
    await tx.query(`select set_config('request.jwt.claims', $1, true)`, [claims]);
    return (await tx.query(sql, params)).rows;
  });
}

export async function newAnonUser(db) {
  const r = await db.query(`insert into auth.users default values returning id`);
  return r.rows[0].id;
}
