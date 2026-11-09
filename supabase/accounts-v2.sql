-- =====================================================================================================
-- TOKENIFY v2 backend: username/password accounts, sessions, friends, parties and squad games.
-- Run this whole file in the Supabase SQL editor. It is safe to run again (create-if-missing /
-- create-or-replace throughout) and never deletes existing TOKENIFY v2 data.
--
-- Needs: Authentication > Sign In / Providers > "Allow anonymous sign-ins" switched ON.
--
-- How sign-in works
--   * An account is a username + password. Passwords are hashed with bcrypt (pgcrypto, cost 11) and
--     verified here on the server. Hashes never leave the database.
--   * The browser holds a Supabase *anonymous* auth session purely as a transport: it is how the API
--     knows which browser is calling. A TOKENIFY session (tk_sessions) links that browser session to an
--     account, with its own 30-day sliding expiry, renewal and revocation (log out, "sign out other
--     devices", password reset). No email address or phone number is ever needed or invented.
--   * Every table has row-level security and no direct client access, except two tiny "signal" tables
--     (version counters used to push live updates). The browser can only call the tk_* functions below,
--     and each one checks the caller's session and permissions itself.
-- =====================================================================================================

create extension if not exists pgcrypto with schema extensions;
create schema if not exists tkp;                      -- private helpers (not exposed through the API)
revoke all on schema tkp from public;
grant usage on schema tkp to authenticated;           -- needed only for the two RLS helper functions

-- ----------------------------------------------------------------------------------------- tables
create table if not exists public.tk_accounts (
  id                  uuid primary key default gen_random_uuid(),
  username            text not null,
  password_hash       text not null,
  avatar              text not null default 'compass',
  show_online         boolean not null default true,
  searchable          boolean not null default true,
  settings            jsonb not null default '{}'::jsonb,
  created_at          timestamptz not null default now(),
  password_changed_at timestamptz not null default now(),
  constraint tk_accounts_username_len check (char_length(username) between 3 and 16)
);
create unique index if not exists tk_accounts_username_lower on public.tk_accounts (lower(username));
create index if not exists tk_accounts_username_prefix on public.tk_accounts (lower(username) text_pattern_ops);

create table if not exists public.tk_sessions (
  auth_uid     uuid primary key references auth.users (id) on delete cascade,
  id           uuid not null default gen_random_uuid() unique,
  account_id   uuid not null references public.tk_accounts (id) on delete cascade,
  device       text,
  created_at   timestamptz not null default now(),
  last_seen_at timestamptz not null default now(),
  expires_at   timestamptz not null default now() + interval '30 days'
);
create index if not exists tk_sessions_account on public.tk_sessions (account_id);

create table if not exists public.tk_recovery_codes (
  id         bigint generated always as identity primary key,
  account_id uuid not null references public.tk_accounts (id) on delete cascade,
  code_hash  bytea not null,
  created_at timestamptz not null default now(),
  used_at    timestamptz
);
create index if not exists tk_recovery_codes_account on public.tk_recovery_codes (account_id);

create table if not exists public.tk_rate_events (
  id     bigint generated always as identity primary key,
  bucket text not null,
  at     timestamptz not null default now()
);
create index if not exists tk_rate_events_bucket_at on public.tk_rate_events (bucket, at);

create table if not exists public.tk_presence (
  account_id   uuid primary key references public.tk_accounts (id) on delete cascade,
  last_seen_at timestamptz not null default now(),
  status       text not null default 'online' check (status in ('online', 'tracking'))
);

create table if not exists public.tk_account_signals (
  account_id uuid primary key references public.tk_accounts (id) on delete cascade,
  version    bigint not null default 1,
  updated_at timestamptz not null default now()
);

create table if not exists public.tk_friend_requests (
  id           uuid primary key default gen_random_uuid(),
  from_id      uuid not null references public.tk_accounts (id) on delete cascade,
  to_id        uuid not null references public.tk_accounts (id) on delete cascade,
  status       text not null default 'pending' check (status in ('pending', 'accepted', 'declined', 'cancelled')),
  created_at   timestamptz not null default now(),
  responded_at timestamptz,
  constraint tk_friend_requests_not_self check (from_id <> to_id)
);
create unique index if not exists tk_friend_requests_one_pending
  on public.tk_friend_requests (least(from_id, to_id), greatest(from_id, to_id)) where status = 'pending';
create index if not exists tk_friend_requests_to on public.tk_friend_requests (to_id) where status = 'pending';
create index if not exists tk_friend_requests_from on public.tk_friend_requests (from_id) where status = 'pending';

create table if not exists public.tk_friendships (
  a_id       uuid not null references public.tk_accounts (id) on delete cascade,
  b_id       uuid not null references public.tk_accounts (id) on delete cascade,
  created_at timestamptz not null default now(),
  primary key (a_id, b_id),
  constraint tk_friendships_ordered check (a_id < b_id)
);
create index if not exists tk_friendships_b on public.tk_friendships (b_id);

create table if not exists public.tk_blocks (
  blocker_id uuid not null references public.tk_accounts (id) on delete cascade,
  blocked_id uuid not null references public.tk_accounts (id) on delete cascade,
  created_at timestamptz not null default now(),
  primary key (blocker_id, blocked_id),
  constraint tk_blocks_not_self check (blocker_id <> blocked_id)
);
create index if not exists tk_blocks_blocked on public.tk_blocks (blocked_id);

create table if not exists public.tk_parties (
  id                 uuid primary key default gen_random_uuid(),
  leader_id          uuid references public.tk_accounts (id) on delete set null,
  status             text not null default 'active' check (status in ('active', 'disbanded')),
  auto_next          boolean not null default false,
  members_can_invite boolean not null default true,
  code_hash          bytea,
  code_expires_at    timestamptz,
  created_at         timestamptz not null default now(),
  disbanded_at       timestamptz
);
create unique index if not exists tk_parties_code on public.tk_parties (code_hash) where code_hash is not null;

create table if not exists public.tk_party_signals (
  party_id   uuid primary key references public.tk_parties (id) on delete cascade,
  version    bigint not null default 1,
  updated_at timestamptz not null default now()
);

create table if not exists public.tk_party_members (
  id          uuid primary key default gen_random_uuid(),
  party_id    uuid not null references public.tk_parties (id) on delete cascade,
  account_id  uuid not null references public.tk_accounts (id) on delete cascade,
  joined_at   timestamptz not null default now(),
  left_at     timestamptz,
  left_reason text
);
create unique index if not exists tk_party_members_one_active on public.tk_party_members (account_id) where left_at is null;
create index if not exists tk_party_members_party on public.tk_party_members (party_id) where left_at is null;

create table if not exists public.tk_party_invites (
  id           uuid primary key default gen_random_uuid(),
  party_id     uuid not null references public.tk_parties (id) on delete cascade,
  from_id      uuid not null references public.tk_accounts (id) on delete cascade,
  to_id        uuid not null references public.tk_accounts (id) on delete cascade,
  status       text not null default 'pending' check (status in ('pending', 'accepted', 'declined', 'cancelled', 'expired')),
  created_at   timestamptz not null default now(),
  expires_at   timestamptz not null default now() + interval '30 minutes',
  responded_at timestamptz
);
create unique index if not exists tk_party_invites_one_pending on public.tk_party_invites (party_id, to_id) where status = 'pending';
create index if not exists tk_party_invites_to on public.tk_party_invites (to_id) where status = 'pending';

create table if not exists public.tk_party_games (
  id           uuid primary key default gen_random_uuid(),
  party_id     uuid not null references public.tk_parties (id) on delete cascade,
  number       integer not null,
  status       text not null default 'active' check (status in ('active', 'ended')),
  void         boolean not null default false,
  confirmed    boolean not null default false,
  started_at   timestamptz not null default now(),
  ended_at     timestamptz,
  started_by   uuid references public.tk_accounts (id) on delete set null,
  ended_by     uuid references public.tk_accounts (id) on delete set null,
  auto_started boolean not null default false,
  unique (party_id, number)
);
create unique index if not exists tk_party_games_one_active on public.tk_party_games (party_id) where status = 'active';

create table if not exists public.tk_party_game_members (
  id                uuid primary key default gen_random_uuid(),
  game_id           uuid not null references public.tk_party_games (id) on delete cascade,
  account_id        uuid references public.tk_accounts (id) on delete set null,
  username_snapshot text not null,
  joined_at         timestamptz not null default now(),
  left_at           timestamptz,
  chests            integer not null default 0 check (chests >= 0)
);
create unique index if not exists tk_party_game_members_uniq on public.tk_party_game_members (game_id, account_id);
create index if not exists tk_party_game_members_account on public.tk_party_game_members (account_id);

create table if not exists public.tk_chest_events (
  id             uuid primary key,
  account_id     uuid references public.tk_accounts (id) on delete set null,
  party_id       uuid not null references public.tk_parties (id) on delete cascade,
  game_id        uuid not null references public.tk_party_games (id) on delete cascade,
  opened_at      timestamptz not null,
  recorded_at    timestamptz not null default now(),
  session_ref    text,
  game_ref       text,
  removed_at     timestamptz,
  removed_reason text
);
create index if not exists tk_chest_events_game on public.tk_chest_events (game_id, account_id) where removed_at is null;

create table if not exists public.tk_party_game_teammates (
  id                uuid primary key default gen_random_uuid(),
  game_id           uuid not null references public.tk_party_games (id) on delete cascade,
  slot              smallint not null check (slot between 1 and 3),
  label             text check (label is null or char_length(label) <= 24),
  mode              text not null check (mode in ('exact', 'estimated', 'unknown')),
  exact_count       integer check (exact_count is null or exact_count between 0 and 999),
  estimate_level    text check (estimate_level is null or estimate_level in ('barely', 'some', 'hunted')),
  estimate_pct      numeric(6, 2),
  estimate_basis    integer,
  estimate_count    numeric(8, 2),
  linked_account_id uuid references public.tk_accounts (id) on delete set null,
  entered_by        uuid references public.tk_accounts (id) on delete set null,
  updated_at        timestamptz not null default now(),
  unique (game_id, slot)
);

create table if not exists public.tk_corrections (
  id         bigint generated always as identity primary key,
  account_id uuid references public.tk_accounts (id) on delete set null,
  game_id    uuid references public.tk_party_games (id) on delete cascade,
  kind       text not null,
  detail     jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now()
);
create index if not exists tk_corrections_game on public.tk_corrections (game_id);

create table if not exists public.tk_tracker_state (
  account_id uuid primary key references public.tk_accounts (id) on delete cascade,
  state      jsonb not null,
  version    bigint not null default 1,
  device_id  text,
  updated_at timestamptz not null default now()
);

-- Row-level security everywhere; the client gets no direct table access at all...
do $$
declare t text;
begin
  foreach t in array array['tk_accounts', 'tk_sessions', 'tk_recovery_codes', 'tk_rate_events', 'tk_presence',
    'tk_account_signals', 'tk_party_signals', 'tk_friend_requests', 'tk_friendships', 'tk_blocks', 'tk_parties',
    'tk_party_members', 'tk_party_invites', 'tk_party_games', 'tk_party_game_members', 'tk_chest_events',
    'tk_party_game_teammates', 'tk_corrections', 'tk_tracker_state'] loop
    execute format('alter table public.%I enable row level security', t);
    execute format('revoke all on public.%I from anon, authenticated', t);
  end loop;
end $$;
-- ...except reading the two version-counter tables, which carry no personal data and power live updates.
grant select on public.tk_account_signals, public.tk_party_signals to authenticated;

-- ---------------------------------------------------------------------------------------- helpers
create or replace function tkp.current_account() returns uuid
language sql stable security definer set search_path = '' as $$
  select s.account_id from public.tk_sessions s where s.auth_uid = auth.uid() and s.expires_at > now()
$$;

create or replace function tkp.require_account() returns uuid
language plpgsql stable security definer set search_path = '' as $$
declare v uuid := tkp.current_account();
begin
  if v is null then raise exception 'TK:not_signed_in' using errcode = '28000'; end if;
  return v;
end $$;

create or replace function tkp.client_ip() returns text
language plpgsql stable set search_path = '' as $$
declare h jsonb; v text;
begin
  begin
    h := nullif(current_setting('request.headers', true), '')::jsonb;
  exception when others then return null;
  end;
  if h is null then return null; end if;
  v := coalesce(h ->> 'cf-connecting-ip', h ->> 'x-real-ip', split_part(coalesce(h ->> 'x-forwarded-for', ''), ',', 1));
  return left(nullif(btrim(v), ''), 64);
end $$;

create or replace function tkp.rate_count(p_bucket text, p_window interval) returns integer
language sql stable security definer set search_path = '' as $$
  select count(*)::integer from public.tk_rate_events where bucket = p_bucket and at > now() - p_window
$$;

-- Records one attempt in a bucket unless the bucket is already full. Callers return a normal
-- {"ok": false} result (never raise) so that the recorded attempt is kept.
create or replace function tkp.rate_take(p_bucket text, p_max integer, p_window interval) returns boolean
language plpgsql security definer set search_path = '' as $$
begin
  if p_bucket is null then return true; end if;
  if tkp.rate_count(p_bucket, p_window) >= p_max then return false; end if;
  insert into public.tk_rate_events (bucket) values (p_bucket);
  return true;
end $$;

create or replace function tkp.random_code(p_len integer) returns text
language plpgsql volatile set search_path = '' as $$
declare alphabet constant text := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; b bytea := extensions.gen_random_bytes(p_len); r text := '';
begin
  for i in 0 .. p_len - 1 loop
    r := r || substr(alphabet, (get_byte(b, i) % 32) + 1, 1);   -- 256 is a multiple of 32: no bias
  end loop;
  return r;
end $$;

create or replace function tkp.normalize_code(p_code text) returns text
language sql immutable set search_path = '' as $$
  select upper(regexp_replace(coalesce(p_code, ''), '[^A-Za-z0-9]', '', 'g'))
$$;

create or replace function tkp.code_hash(p_scope text, p_code text) returns bytea
language sql immutable set search_path = '' as $$
  select extensions.digest(p_scope || ':' || p_code, 'sha256')
$$;

create or replace function tkp.valid_username(p_name text) returns boolean
language sql immutable set search_path = '' as $$
  select p_name is not null and char_length(p_name) between 3 and 16
     and p_name ~ '^[A-Za-z0-9]([A-Za-z0-9]|[_.-][A-Za-z0-9])*$'
$$;

create or replace function tkp.reserved_username(p_name text) returns boolean
language sql immutable set search_path = '' as $$
  select lower(coalesce(p_name, '')) = any (array['admin', 'administrator', 'tokenify', 'support', 'moderator', 'mod',
    'system', 'root', 'null', 'undefined', 'epic', 'epicgames', 'fortnite', 'staff', 'official', 'help', 'owner'])
$$;

create or replace function tkp.password_problem(p_password text, p_username text) returns text
language plpgsql immutable set search_path = '' as $$
declare lp text := lower(coalesce(p_password, ''));
begin
  if p_password is null or char_length(p_password) < 8 then return 'password_short'; end if;
  if char_length(p_password) > 64 or octet_length(p_password) > 72 then return 'password_long'; end if;
  if p_username is not null and char_length(p_username) >= 3 and strpos(lp, lower(p_username)) > 0 then return 'password_has_username'; end if;
  if lp = any (array['password', 'password1', 'password12', 'password123', 'passw0rd', '12345678', '123456789',
      '1234567890', '87654321', '11111111', '00000000', 'qwerty12', 'qwerty123', 'qwertyuiop', 'iloveyou',
      'fortnite', 'fortnite1', 'fortnite123', 'tokenify', 'tokenify1', 'letmein1', 'abc12345', 'abcd1234', 'asdfghjkl'])
     or p_password ~ '^(.)\1+$' then
    return 'password_common';
  end if;
  return null;
end $$;

create or replace function tkp.hash_password(p_password text) returns text
language sql volatile set search_path = '' as $$
  select extensions.crypt(p_password, extensions.gen_salt('bf', 11))
$$;

create or replace function tkp.check_password(p_password text, p_hash text) returns boolean
language sql stable set search_path = '' as $$
  select p_hash is not null and p_password is not null and extensions.crypt(p_password, p_hash) = p_hash
$$;

create or replace function tkp.clean_text(p_text text, p_max integer) returns text
language sql immutable set search_path = '' as $$
  select nullif(left(btrim(regexp_replace(coalesce(p_text, ''), '[^ -~]', '', 'g')), p_max), '')
$$;

create or replace function tkp.user_card(p_id uuid) returns jsonb
language sql stable security definer set search_path = '' as $$
  select jsonb_build_object('id', a.id, 'username', a.username, 'avatar', a.avatar) from public.tk_accounts a where a.id = p_id
$$;

create or replace function tkp.account_json(p_id uuid) returns jsonb
language sql stable security definer set search_path = '' as $$
  select jsonb_build_object('id', a.id, 'username', a.username, 'avatar', a.avatar, 'created_at', a.created_at,
           'show_online', a.show_online, 'searchable', a.searchable, 'settings', a.settings,
           'password_changed_at', a.password_changed_at)
    from public.tk_accounts a where a.id = p_id
$$;

create or replace function tkp.bump_account(p_id uuid) returns void
language sql security definer set search_path = '' as $$
  insert into public.tk_account_signals as s (account_id) values (p_id)
  on conflict (account_id) do update set version = s.version + 1, updated_at = now()
$$;

create or replace function tkp.bump_party(p_id uuid) returns void
language sql security definer set search_path = '' as $$
  insert into public.tk_party_signals as s (party_id) values (p_id)
  on conflict (party_id) do update set version = s.version + 1, updated_at = now()
$$;

create or replace function tkp.active_party_of(p_account uuid) returns uuid
language sql stable security definer set search_path = '' as $$
  select m.party_id from public.tk_party_members m where m.account_id = p_account and m.left_at is null
$$;

create or replace function tkp.is_party_member(p_party uuid) returns boolean
language sql stable security definer set search_path = '' as $$
  select exists (select 1 from public.tk_party_members m
                  where m.party_id = p_party and m.left_at is null and m.account_id = tkp.current_account())
$$;

create or replace function tkp.are_friends(p_a uuid, p_b uuid) returns boolean
language sql stable security definer set search_path = '' as $$
  select exists (select 1 from public.tk_friendships f where f.a_id = least(p_a, p_b) and f.b_id = greatest(p_a, p_b))
$$;

create or replace function tkp.blocked_either(p_a uuid, p_b uuid) returns boolean
language sql stable security definer set search_path = '' as $$
  select exists (select 1 from public.tk_blocks k
                  where (k.blocker_id = p_a and k.blocked_id = p_b) or (k.blocker_id = p_b and k.blocked_id = p_a))
$$;

-- 'offline' | 'online' | 'in_party' | 'tracking'. p_force ignores the "hide my status" preference
-- (used only between members of the same party, who always see whether each other is connected).
create or replace function tkp.presence_of(p_target uuid, p_force boolean default false) returns text
language sql stable security definer set search_path = '' as $$
  select coalesce((
    select case
             when not p_force and not a.show_online then 'offline'
             when p.last_seen_at is null or p.last_seen_at < now() - interval '75 seconds' then 'offline'
             when p.status = 'tracking' then 'tracking'
             when exists (select 1 from public.tk_party_members m where m.account_id = a.id and m.left_at is null) then 'in_party'
             else 'online'
           end
      from public.tk_accounts a left join public.tk_presence p on p.account_id = a.id
     where a.id = p_target), 'offline')
$$;

create or replace function tkp.issue_recovery_codes(p_account uuid) returns text[]
language plpgsql security definer set search_path = '' as $$
declare codes text[] := '{}'; c text;
begin
  delete from public.tk_recovery_codes where account_id = p_account;
  for i in 1 .. 8 loop
    c := tkp.random_code(12);                                          -- 60 random bits per code
    insert into public.tk_recovery_codes (account_id, code_hash) values (p_account, tkp.code_hash(p_account::text, c));
    codes := codes || (substr(c, 1, 4) || '-' || substr(c, 5, 4) || '-' || substr(c, 9, 4));
  end loop;
  return codes;
end $$;

create or replace function tkp.start_session(p_account uuid, p_device text) returns void
language plpgsql security definer set search_path = '' as $$
begin
  insert into public.tk_sessions (auth_uid, account_id, device) values (auth.uid(), p_account, tkp.clean_text(p_device, 80))
  on conflict (auth_uid) do update
    set account_id = excluded.account_id, device = excluded.device, id = gen_random_uuid(),
        created_at = now(), last_seen_at = now(), expires_at = now() + interval '30 days';
  insert into public.tk_presence (account_id) values (p_account) on conflict (account_id) do nothing;
end $$;

-- Removes TOKENIFY sessions (and their throwaway anonymous auth users) for an account.
create or replace function tkp.end_sessions(p_account uuid, p_keep_auth_uid uuid) returns void
language plpgsql security definer set search_path = '' as $$
declare ids uuid[];
begin
  select coalesce(array_agg(auth_uid), '{}') into ids from public.tk_sessions
   where account_id = p_account and (p_keep_auth_uid is null or auth_uid <> p_keep_auth_uid);
  delete from public.tk_sessions where auth_uid = any (ids);
  delete from auth.users u where u.id = any (ids) and coalesce(u.is_anonymous, false);
end $$;

create or replace function tkp.estimate_pct(p_account uuid, p_level text) returns numeric
language sql stable security definer set search_path = '' as $$
  select coalesce((a.settings -> 'est' ->> p_level)::numeric,
                  case p_level when 'barely' then 25 when 'some' then 60 else 100 end)
    from public.tk_accounts a where a.id = p_account
$$;

create or replace function tkp.completeness(p_linked integer, p_mates integer, p_unknown integer, p_estimated integer, p_confirmed boolean)
returns text language sql immutable set search_path = '' as $$
  select case when p_unknown > 0 then 'partial'
              when p_estimated > 0 then 'estimated'
              when p_mates > 0 then 'reported'
              when p_linked >= 4 or p_confirmed then 'complete'
              else 'unconfirmed' end
$$;

create or replace function tkp.cleanup() returns void
language plpgsql security definer set search_path = '' as $$
begin
  delete from public.tk_rate_events where at < now() - interval '1 day';
  delete from auth.users u where coalesce(u.is_anonymous, false)
     and exists (select 1 from public.tk_sessions s where s.auth_uid = u.id and s.expires_at < now());
  delete from public.tk_sessions where expires_at < now();
  -- abandoned sign-in attempts: anonymous transport users that never became a TOKENIFY session
  delete from auth.users u where coalesce(u.is_anonymous, false) and u.created_at < now() - interval '1 day'
     and not exists (select 1 from public.tk_sessions s where s.auth_uid = u.id);
  update public.tk_party_invites set status = 'expired' where status = 'pending' and expires_at < now();
end $$;

-- ---------------------------------------------------------------------------- accounts & sessions
create or replace function public.tk_username_available(p_username text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_name text := btrim(coalesce(p_username, '')); v_ip text := tkp.client_ip(); v_ok boolean;
begin
  if v_ip is not null then v_ok := tkp.rate_take('avail:ip:' || v_ip, 60, interval '1 minute');
  elsif auth.uid() is not null then v_ok := tkp.rate_take('avail:uid:' || auth.uid(), 60, interval '1 minute');
  else v_ok := tkp.rate_take('avail:anon', 1200, interval '1 minute');
  end if;
  if not v_ok then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  if not tkp.valid_username(v_name) then return jsonb_build_object('ok', true, 'available', false, 'reason', 'invalid'); end if;
  if tkp.reserved_username(v_name) or exists (select 1 from public.tk_accounts a where lower(a.username) = lower(v_name)) then
    return jsonb_build_object('ok', true, 'available', false, 'reason', 'taken');
  end if;
  return jsonb_build_object('ok', true, 'available', true);
end $$;

create or replace function public.tk_signup(p_username text, p_password text, p_device text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_uid uuid := auth.uid(); v_name text := btrim(coalesce(p_username, '')); v_ip text := tkp.client_ip();
        v_id uuid; v_problem text; v_codes text[];
begin
  if v_uid is null or not exists (select 1 from auth.users u where u.id = v_uid) then
    return jsonb_build_object('ok', false, 'error', 'session_stale');
  end if;
  if tkp.current_account() is not null then return jsonb_build_object('ok', false, 'error', 'already_signed_in'); end if;
  if (v_ip is not null and not tkp.rate_take('signup:ip:' || v_ip, 30, interval '1 hour'))
     or not tkp.rate_take('signup:all', 600, interval '1 hour') then
    return jsonb_build_object('ok', false, 'error', 'rate_limited');
  end if;
  if not tkp.valid_username(v_name) then return jsonb_build_object('ok', false, 'error', 'username_invalid'); end if;
  if tkp.reserved_username(v_name) then return jsonb_build_object('ok', false, 'error', 'username_taken'); end if;
  v_problem := tkp.password_problem(p_password, v_name);
  if v_problem is not null then return jsonb_build_object('ok', false, 'error', v_problem); end if;
  begin
    insert into public.tk_accounts (username, password_hash, settings)
    values (v_name, tkp.hash_password(p_password), '{"est": {"barely": 25, "some": 60, "hunted": 100}}'::jsonb)
    returning id into v_id;
  exception when unique_violation then
    return jsonb_build_object('ok', false, 'error', 'username_taken');
  end;
  perform tkp.start_session(v_id, p_device);
  perform tkp.bump_account(v_id);
  v_codes := tkp.issue_recovery_codes(v_id);
  return jsonb_build_object('ok', true, 'account', tkp.account_json(v_id), 'recovery_codes', to_jsonb(v_codes));
end $$;

create or replace function public.tk_login(p_username text, p_password text, p_device text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_uid uuid := auth.uid(); v_ip text := tkp.client_ip(); v_key text := lower(btrim(coalesce(p_username, '')));
        v_id uuid; v_hash text;
begin
  if v_uid is null or not exists (select 1 from auth.users u where u.id = v_uid) then
    return jsonb_build_object('ok', false, 'error', 'session_stale');
  end if;
  if tkp.current_account() is not null then return jsonb_build_object('ok', false, 'error', 'already_signed_in'); end if;
  if v_key = '' or coalesce(p_password, '') = '' then return jsonb_build_object('ok', false, 'error', 'bad_credentials'); end if;
  if not tkp.rate_take('login:uid:' || v_uid, 20, interval '15 minutes')
     or (v_ip is not null and not tkp.rate_take('login:ip:' || v_ip, 60, interval '15 minutes')) then
    return jsonb_build_object('ok', false, 'error', 'rate_limited');
  end if;
  if tkp.rate_count('loginfail:' || v_key, interval '15 minutes') >= 10 then
    return jsonb_build_object('ok', false, 'error', 'too_many_attempts');
  end if;
  select a.id, a.password_hash into v_id, v_hash from public.tk_accounts a where lower(a.username) = v_key;
  if v_id is null then
    perform extensions.crypt(p_password, '$2a$11$C6UzMDM.H6dfI/f/IKcEeO');   -- same cost as a real check
    insert into public.tk_rate_events (bucket) values ('loginfail:' || v_key);
    return jsonb_build_object('ok', false, 'error', 'bad_credentials');
  end if;
  if not tkp.check_password(p_password, v_hash) then
    insert into public.tk_rate_events (bucket) values ('loginfail:' || v_key);
    return jsonb_build_object('ok', false, 'error', 'bad_credentials');
  end if;
  delete from public.tk_rate_events where bucket = 'loginfail:' || v_key;
  perform tkp.start_session(v_id, p_device);
  return jsonb_build_object('ok', true, 'account', tkp.account_json(v_id));
end $$;

create or replace function public.tk_logout() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_uid uuid := auth.uid(); v_acc uuid := tkp.current_account();
begin
  if v_uid is null then return jsonb_build_object('ok', true); end if;
  delete from public.tk_sessions where auth_uid = v_uid;
  delete from auth.users u where u.id = v_uid and coalesce(u.is_anonymous, false);
  if v_acc is not null and not exists (select 1 from public.tk_sessions s where s.account_id = v_acc and s.expires_at > now()) then
    update public.tk_presence set last_seen_at = now() - interval '10 minutes' where account_id = v_acc;
  end if;
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_me() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.current_account();
begin
  if me is null then return jsonb_build_object('ok', false, 'error', 'not_signed_in'); end if;
  update public.tk_sessions set last_seen_at = now(), expires_at = now() + interval '30 days' where auth_uid = auth.uid();
  return jsonb_build_object('ok', true, 'account', tkp.account_json(me), 'server_time', now(),
    'session_id', (select s.id from public.tk_sessions s where s.auth_uid = auth.uid()),
    'recovery_codes_left', (select count(*) from public.tk_recovery_codes c where c.account_id = me and c.used_at is null));
end $$;

create or replace function public.tk_recover(p_username text, p_code text, p_new_password text, p_device text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_uid uuid := auth.uid(); v_ip text := tkp.client_ip(); v_key text := lower(btrim(coalesce(p_username, '')));
        v_code text := tkp.normalize_code(p_code); v_id uuid; v_name text; v_row bigint; v_problem text;
begin
  if v_uid is null or not exists (select 1 from auth.users u where u.id = v_uid) then
    return jsonb_build_object('ok', false, 'error', 'session_stale');
  end if;
  if tkp.current_account() is not null then return jsonb_build_object('ok', false, 'error', 'already_signed_in'); end if;
  if not tkp.rate_take('recover:uid:' || v_uid, 10, interval '1 hour')
     or (v_ip is not null and not tkp.rate_take('recover:ip:' || v_ip, 20, interval '1 hour'))
     or tkp.rate_count('recoverfail:' || v_key, interval '1 hour') >= 5 then
    return jsonb_build_object('ok', false, 'error', 'rate_limited');
  end if;
  select a.id, a.username into v_id, v_name from public.tk_accounts a where lower(a.username) = v_key;
  v_problem := tkp.password_problem(p_new_password, coalesce(v_name, btrim(p_username)));
  if v_problem is not null then return jsonb_build_object('ok', false, 'error', v_problem); end if;
  if v_id is not null and char_length(v_code) = 12 then
    select c.id into v_row from public.tk_recovery_codes c
     where c.account_id = v_id and c.used_at is null and c.code_hash = tkp.code_hash(v_id::text, v_code)
     for update;
  end if;
  if v_row is null then
    insert into public.tk_rate_events (bucket) values ('recoverfail:' || v_key);
    return jsonb_build_object('ok', false, 'error', 'recovery_failed');
  end if;
  update public.tk_recovery_codes set used_at = now() where id = v_row;
  update public.tk_accounts set password_hash = tkp.hash_password(p_new_password), password_changed_at = now() where id = v_id;
  perform tkp.end_sessions(v_id, v_uid);                                     -- every other device is signed out
  perform tkp.start_session(v_id, p_device);
  delete from public.tk_rate_events where bucket in ('recoverfail:' || v_key, 'loginfail:' || v_key);
  return jsonb_build_object('ok', true, 'account', tkp.account_json(v_id),
    'codes_left', (select count(*) from public.tk_recovery_codes c where c.account_id = v_id and c.used_at is null));
end $$;

create or replace function public.tk_password_change(p_current text, p_new text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_hash text; v_name text; v_problem text;
begin
  if not tkp.rate_take('pwchange:' || me, 10, interval '1 hour') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  select a.password_hash, a.username into v_hash, v_name from public.tk_accounts a where a.id = me;
  if not tkp.check_password(p_current, v_hash) then return jsonb_build_object('ok', false, 'error', 'bad_password'); end if;
  v_problem := tkp.password_problem(p_new, v_name);
  if v_problem is not null then return jsonb_build_object('ok', false, 'error', v_problem); end if;
  update public.tk_accounts set password_hash = tkp.hash_password(p_new), password_changed_at = now() where id = me;
  perform tkp.end_sessions(me, auth.uid());                                  -- keep only this device signed in
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_recovery_codes_regenerate(p_password text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_hash text;
begin
  if not tkp.rate_take('codes:' || me, 10, interval '1 hour') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  select a.password_hash into v_hash from public.tk_accounts a where a.id = me;
  if not tkp.check_password(p_password, v_hash) then return jsonb_build_object('ok', false, 'error', 'bad_password'); end if;
  return jsonb_build_object('ok', true, 'recovery_codes', to_jsonb(tkp.issue_recovery_codes(me)));
end $$;

create or replace function public.tk_sessions_list() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  return jsonb_build_object('ok', true, 'sessions', coalesce((
    select jsonb_agg(jsonb_build_object('id', s.id, 'device', s.device, 'created_at', s.created_at,
             'last_seen_at', s.last_seen_at, 'expires_at', s.expires_at, 'current', s.auth_uid = auth.uid())
           order by (s.auth_uid = auth.uid()) desc, s.last_seen_at desc)
      from public.tk_sessions s where s.account_id = me and s.expires_at > now()), '[]'::jsonb));
end $$;

create or replace function public.tk_session_revoke(p_session uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_uid uuid;
begin
  select s.auth_uid into v_uid from public.tk_sessions s where s.id = p_session and s.account_id = me;
  if v_uid is null then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  delete from public.tk_sessions where auth_uid = v_uid;
  delete from auth.users u where u.id = v_uid and coalesce(u.is_anonymous, false);
  return jsonb_build_object('ok', true, 'was_current', v_uid = auth.uid());
end $$;

create or replace function public.tk_sessions_revoke_others() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  perform tkp.end_sessions(me, auth.uid());
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_profile_update(p_avatar text default null, p_show_online boolean default null,
  p_searchable boolean default null, p_estimates jsonb default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_b numeric; v_s numeric; v_h numeric; v_party uuid;
begin
  if not tkp.rate_take('profile:' || me, 60, interval '10 minutes') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  if p_avatar is not null and not (p_avatar = any (array['compass', 'token', 'outfit', 'vehicle', 'pickaxe', 'jam',
      'loading', 'lucky', 'sprite', 'extractor', 'accelerator', 'fireworks', 'chest'])) then
    return jsonb_build_object('ok', false, 'error', 'invalid_avatar');
  end if;
  if p_estimates is not null then
    begin
      v_b := (p_estimates ->> 'barely')::numeric; v_s := (p_estimates ->> 'some')::numeric; v_h := (p_estimates ->> 'hunted')::numeric;
    exception when others then return jsonb_build_object('ok', false, 'error', 'invalid_estimates');
    end;
    if v_b is null or v_s is null or v_h is null or v_b < 0 or v_s < 0 or v_h < 0 or v_b > 200 or v_s > 200 or v_h > 200 then
      return jsonb_build_object('ok', false, 'error', 'invalid_estimates');
    end if;
  end if;
  update public.tk_accounts a set
    avatar = coalesce(p_avatar, a.avatar),
    show_online = coalesce(p_show_online, a.show_online),
    searchable = coalesce(p_searchable, a.searchable),
    settings = case when p_estimates is null then a.settings
                    else jsonb_set(a.settings, '{est}', jsonb_build_object('barely', round(v_b, 1), 'some', round(v_s, 1), 'hunted', round(v_h, 1))) end
  where a.id = me;
  v_party := tkp.active_party_of(me);
  if v_party is not null then perform tkp.bump_party(v_party); end if;
  return jsonb_build_object('ok', true, 'account', tkp.account_json(me));
end $$;

-- ------------------------------------------------------------------------------------ tracker sync
-- One versioned snapshot of the personal Live Tracker per account. A write is accepted only if the
-- caller has seen the latest version; otherwise the newest copy comes back so the browser can merge
-- by record ID and retry. Retries can never duplicate chests and nothing is silently overwritten.
create or replace function public.tk_tracker_pull() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); r record;
begin
  select t.state, t.version into r from public.tk_tracker_state t where t.account_id = me;
  if r.version is null then return jsonb_build_object('ok', true, 'version', 0, 'state', null); end if;
  return jsonb_build_object('ok', true, 'version', r.version, 'state', r.state);
end $$;

create or replace function public.tk_tracker_push(p_expected bigint, p_state jsonb, p_device text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); cur record; n integer;
begin
  if not tkp.rate_take('push:' || me, 120, interval '1 minute') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  if p_state is null or jsonb_typeof(p_state) <> 'object' or (p_state ->> 'v') is distinct from '1'
     or jsonb_typeof(p_state -> 'sessions') <> 'object' or jsonb_typeof(p_state -> 'games') <> 'object'
     or jsonb_typeof(p_state -> 'gameOrder') <> 'array' or jsonb_typeof(p_state -> 'sessionOrder') <> 'array' then
    return jsonb_build_object('ok', false, 'error', 'invalid_state');
  end if;
  if octet_length(p_state::text) > 6000000 then return jsonb_build_object('ok', false, 'error', 'state_too_large'); end if;
  select t.version, t.state into cur from public.tk_tracker_state t where t.account_id = me for update;
  if cur.version is null then
    if p_expected <> 0 then return jsonb_build_object('ok', false, 'error', 'conflict', 'version', 0, 'state', null); end if;
    insert into public.tk_tracker_state (account_id, state, version, device_id) values (me, p_state, 1, tkp.clean_text(p_device, 80))
    on conflict (account_id) do nothing;
    get diagnostics n = row_count;
    if n = 0 then   -- another device created it in the meantime
      select t.version, t.state into cur from public.tk_tracker_state t where t.account_id = me;
      return jsonb_build_object('ok', false, 'error', 'conflict', 'version', cur.version, 'state', cur.state);
    end if;
    return jsonb_build_object('ok', true, 'version', 1);
  end if;
  if cur.version <> p_expected then
    return jsonb_build_object('ok', false, 'error', 'conflict', 'version', cur.version, 'state', cur.state);
  end if;
  update public.tk_tracker_state set state = p_state, version = cur.version + 1, device_id = tkp.clean_text(p_device, 80), updated_at = now()
   where account_id = me;
  return jsonb_build_object('ok', true, 'version', cur.version + 1);
end $$;

-- ------------------------------------------------------------------------------- presence & social
create or replace function tkp.party_failover(p_party uuid) returns void
language plpgsql security definer set search_path = '' as $$
declare v_leader uuid; v_next uuid;
begin
  select p.leader_id into v_leader from public.tk_parties p where p.id = p_party and p.status = 'active' for update;
  if not found then return; end if;
  if v_leader is not null and exists (select 1 from public.tk_presence pr where pr.account_id = v_leader
                                        and pr.last_seen_at > now() - interval '3 minutes')
     and exists (select 1 from public.tk_party_members m where m.party_id = p_party and m.account_id = v_leader and m.left_at is null) then
    return;
  end if;
  -- deterministic successor: the earliest-joined member who is connected right now
  select m.account_id into v_next from public.tk_party_members m join public.tk_presence pr on pr.account_id = m.account_id
   where m.party_id = p_party and m.left_at is null and m.account_id is distinct from v_leader
     and pr.last_seen_at > now() - interval '75 seconds'
   order by m.joined_at, m.account_id limit 1;
  if v_next is null then return; end if;
  update public.tk_parties set leader_id = v_next where id = p_party;
  perform tkp.bump_party(p_party);
end $$;

create or replace function public.tk_heartbeat(p_status text default 'online') returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_status text := case when p_status = 'tracking' then 'tracking' else 'online' end;
        v_prev_seen timestamptz; v_prev_status text; v_party uuid;
begin
  select pr.last_seen_at, pr.status into v_prev_seen, v_prev_status from public.tk_presence pr where pr.account_id = me;
  insert into public.tk_presence (account_id, last_seen_at, status) values (me, now(), v_status)
  on conflict (account_id) do update set last_seen_at = now(), status = excluded.status;
  update public.tk_sessions set last_seen_at = now(), expires_at = now() + interval '30 days' where auth_uid = auth.uid();
  v_party := tkp.active_party_of(me);
  if v_party is not null then
    if v_prev_seen is null or v_prev_seen < now() - interval '75 seconds' or v_prev_status is distinct from v_status then
      perform tkp.bump_party(v_party);
    end if;
    perform tkp.party_failover(v_party);
  end if;
  if random() < 0.02 then perform tkp.cleanup(); end if;
  return jsonb_build_object('ok', true, 'server_time', now(), 'party_id', v_party,
    'account_version', coalesce((select s.version from public.tk_account_signals s where s.account_id = me), 0),
    'party_version', coalesce((select s.version from public.tk_party_signals s where s.party_id = v_party), 0));
end $$;

create or replace function public.tk_social_state() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_friends jsonb; v_in jsonb; v_out jsonb; v_blocked jsonb; v_inv jsonb;
begin
  update public.tk_party_invites set status = 'expired' where to_id = me and status = 'pending' and expires_at < now();
  select coalesce(jsonb_agg(tkp.user_card(f.other) || jsonb_build_object('presence', f.presence, 'since', f.created_at,
           'same_party', f.party is not null and f.party = tkp.active_party_of(me))
         order by case f.presence when 'tracking' then 0 when 'in_party' then 1 when 'online' then 2 else 3 end, lower(f.username)), '[]'::jsonb)
    into v_friends
    from (select x.other, x.created_at, tkp.presence_of(x.other) as presence, tkp.active_party_of(x.other) as party, a.username
            from (select case when fr.a_id = me then fr.b_id else fr.a_id end as other, fr.created_at
                    from public.tk_friendships fr where fr.a_id = me or fr.b_id = me) x
            join public.tk_accounts a on a.id = x.other) f;
  select coalesce(jsonb_agg(jsonb_build_object('id', r.id, 'user', tkp.user_card(r.from_id), 'created_at', r.created_at) order by r.created_at desc), '[]'::jsonb)
    into v_in from public.tk_friend_requests r where r.to_id = me and r.status = 'pending';
  select coalesce(jsonb_agg(jsonb_build_object('id', r.id, 'user', tkp.user_card(r.to_id), 'created_at', r.created_at) order by r.created_at desc), '[]'::jsonb)
    into v_out from public.tk_friend_requests r where r.from_id = me and r.status = 'pending';
  select coalesce(jsonb_agg(jsonb_build_object('user', tkp.user_card(k.blocked_id), 'created_at', k.created_at) order by k.created_at desc), '[]'::jsonb)
    into v_blocked from public.tk_blocks k where k.blocker_id = me;
  select coalesce(jsonb_agg(jsonb_build_object('id', i.id, 'party_id', i.party_id, 'from', tkp.user_card(i.from_id),
           'leader', tkp.user_card(p.leader_id), 'created_at', i.created_at, 'expires_at', i.expires_at,
           'members', (select count(*) from public.tk_party_members m where m.party_id = i.party_id and m.left_at is null))
         order by i.created_at desc), '[]'::jsonb)
    into v_inv from public.tk_party_invites i join public.tk_parties p on p.id = i.party_id and p.status = 'active'
   where i.to_id = me and i.status = 'pending' and i.expires_at > now();
  return jsonb_build_object('ok', true,
    'version', coalesce((select s.version from public.tk_account_signals s where s.account_id = me), 0),
    'friends', v_friends, 'incoming', v_in, 'outgoing', v_out, 'blocked', v_blocked, 'party_invites', v_inv,
    'notifications', jsonb_array_length(v_in) + jsonb_array_length(v_inv));
end $$;

create or replace function public.tk_user_search(p_query text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); q text := lower(btrim(coalesce(p_query, ''))); v_pat text; v jsonb;
begin
  if not tkp.rate_take('search:' || me, 60, interval '1 minute') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  if char_length(q) < 2 or char_length(q) > 16 or q !~ '^[a-z0-9_.-]+$' then return jsonb_build_object('ok', true, 'results', '[]'::jsonb); end if;
  v_pat := replace(q, '_', '\_') || '%';
  select coalesce(jsonb_agg(jsonb_build_object('id', s.id, 'username', s.username, 'avatar', s.avatar,
           'relation', case
              when exists (select 1 from public.tk_blocks k where k.blocker_id = me and k.blocked_id = s.id) then 'blocked'
              when tkp.are_friends(me, s.id) then 'friends'
              when exists (select 1 from public.tk_friend_requests r where r.from_id = me and r.to_id = s.id and r.status = 'pending') then 'outgoing'
              when exists (select 1 from public.tk_friend_requests r where r.from_id = s.id and r.to_id = me and r.status = 'pending') then 'incoming'
              else 'none' end,
           'request_id', (select r.id from public.tk_friend_requests r where r.status = 'pending'
                           and ((r.from_id = me and r.to_id = s.id) or (r.from_id = s.id and r.to_id = me)) limit 1))
         order by s.exact desc, char_length(s.username), lower(s.username)), '[]'::jsonb)
    into v
    from (select a.id, a.username, a.avatar, lower(a.username) = q as exact
            from public.tk_accounts a
           where a.id <> me and lower(a.username) like v_pat escape '\'
             and (a.searchable or lower(a.username) = q)
             and not exists (select 1 from public.tk_blocks k where k.blocker_id = a.id and k.blocked_id = me)
           order by lower(a.username) = q desc, char_length(a.username), lower(a.username)
           limit 10) s;
  return jsonb_build_object('ok', true, 'results', v);
end $$;

create or replace function tkp.accept_request(p_request uuid) returns void
language plpgsql security definer set search_path = '' as $$
declare r record;
begin
  update public.tk_friend_requests set status = 'accepted', responded_at = now() where id = p_request and status = 'pending'
  returning from_id, to_id into r;
  if r.from_id is null then return; end if;
  insert into public.tk_friendships (a_id, b_id) values (least(r.from_id, r.to_id), greatest(r.from_id, r.to_id)) on conflict do nothing;
  perform tkp.bump_account(r.from_id);
  perform tkp.bump_account(r.to_id);
end $$;

create or replace function public.tk_friend_request(p_account uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_req uuid;
begin
  if p_account is null or p_account = me then return jsonb_build_object('ok', false, 'error', 'self'); end if;
  if not exists (select 1 from public.tk_accounts a where a.id = p_account) then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  perform pg_advisory_xact_lock(hashtextextended(least(me, p_account)::text || greatest(me, p_account)::text, 7));
  if tkp.blocked_either(me, p_account) then return jsonb_build_object('ok', false, 'error', 'unavailable'); end if;
  if tkp.are_friends(me, p_account) then return jsonb_build_object('ok', true, 'status', 'friends'); end if;
  select r.id into v_req from public.tk_friend_requests r where r.from_id = p_account and r.to_id = me and r.status = 'pending';
  if v_req is not null then                               -- they already asked us: that's a yes from both sides
    perform tkp.accept_request(v_req);
    return jsonb_build_object('ok', true, 'status', 'friends');
  end if;
  select r.id into v_req from public.tk_friend_requests r where r.from_id = me and r.to_id = p_account and r.status = 'pending';
  if v_req is not null then return jsonb_build_object('ok', true, 'status', 'outgoing', 'request_id', v_req); end if;
  if not tkp.rate_take('friendreq:' || me, 30, interval '1 hour') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  if (select count(*) from public.tk_friend_requests r where r.from_id = me and r.status = 'pending') >= 50 then
    return jsonb_build_object('ok', false, 'error', 'too_many_pending');
  end if;
  if (select count(*) from public.tk_friendships f where f.a_id = me or f.b_id = me) >= 300 then
    return jsonb_build_object('ok', false, 'error', 'friend_limit');
  end if;
  insert into public.tk_friend_requests (from_id, to_id) values (me, p_account) returning id into v_req;
  perform tkp.bump_account(p_account);
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'status', 'outgoing', 'request_id', v_req);
end $$;

create or replace function public.tk_friend_respond(p_request uuid, p_accept boolean) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); r record;
begin
  select * into r from public.tk_friend_requests fr where fr.id = p_request and fr.to_id = me and fr.status = 'pending' for update;
  if r.id is null then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  if not coalesce(p_accept, false) then
    update public.tk_friend_requests set status = 'declined', responded_at = now() where id = r.id;
    perform tkp.bump_account(r.from_id);
    perform tkp.bump_account(me);
    return jsonb_build_object('ok', true, 'status', 'declined');
  end if;
  if tkp.blocked_either(me, r.from_id) then
    update public.tk_friend_requests set status = 'declined', responded_at = now() where id = r.id;
    return jsonb_build_object('ok', false, 'error', 'unavailable');
  end if;
  if (select count(*) from public.tk_friendships f where f.a_id = me or f.b_id = me) >= 300 then
    return jsonb_build_object('ok', false, 'error', 'friend_limit');
  end if;
  perform tkp.accept_request(r.id);
  return jsonb_build_object('ok', true, 'status', 'friends');
end $$;

create or replace function public.tk_friend_cancel(p_request uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_to uuid;
begin
  update public.tk_friend_requests set status = 'cancelled', responded_at = now()
   where id = p_request and from_id = me and status = 'pending' returning to_id into v_to;
  if v_to is null then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  perform tkp.bump_account(v_to);
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true);
end $$;

create or replace function tkp.cancel_party_invites_between(p_a uuid, p_b uuid) returns void
language plpgsql security definer set search_path = '' as $$
begin
  update public.tk_party_invites set status = 'cancelled', responded_at = now()
   where status = 'pending' and ((from_id = p_a and to_id = p_b) or (from_id = p_b and to_id = p_a));
end $$;

create or replace function public.tk_friend_remove(p_account uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  delete from public.tk_friendships where a_id = least(me, p_account) and b_id = greatest(me, p_account);
  perform tkp.cancel_party_invites_between(me, p_account);
  perform tkp.bump_account(p_account);
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_block(p_account uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid; v_leader uuid; v_action text := null;
begin
  if p_account is null or p_account = me then return jsonb_build_object('ok', false, 'error', 'self'); end if;
  if not exists (select 1 from public.tk_accounts a where a.id = p_account) then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  insert into public.tk_blocks (blocker_id, blocked_id) values (me, p_account) on conflict do nothing;
  delete from public.tk_friendships where a_id = least(me, p_account) and b_id = greatest(me, p_account);
  update public.tk_friend_requests set status = 'cancelled', responded_at = now()
   where status = 'pending' and ((from_id = me and to_id = p_account) or (from_id = p_account and to_id = me));
  perform tkp.cancel_party_invites_between(me, p_account);
  -- never stay in a party with someone you blocked
  v_party := tkp.active_party_of(me);
  if v_party is not null and tkp.active_party_of(p_account) = v_party then
    select p.leader_id into v_leader from public.tk_parties p where p.id = v_party;
    if v_leader = me then perform tkp.leave_party(p_account, 'kicked'); v_action := 'removed_from_party';
    else perform tkp.leave_party(me, 'left'); v_action := 'left_party';
    end if;
  end if;
  perform tkp.bump_account(p_account);
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'party_action', v_action);
end $$;

create or replace function public.tk_unblock(p_account uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  delete from public.tk_blocks where blocker_id = me and blocked_id = p_account;
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true);
end $$;

-- -------------------------------------------------------------------------------------- parties
create or replace function tkp.game_json(p_game uuid, p_viewer uuid) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare g record; v_members jsonb; v_linked integer; v_linked_n integer; v_mates jsonb; v_exact integer;
        v_est numeric; v_unknown integer; v_est_n integer; v_mates_n integer;
begin
  select * into g from public.tk_party_games pg where pg.id = p_game;
  if g.id is null then return null; end if;
  select coalesce(jsonb_agg(jsonb_build_object('account_id', gm.account_id,
           'username', coalesce(a.username, gm.username_snapshot), 'avatar', coalesce(a.avatar, 'compass'),
           'chests', gm.chests, 'joined_at', gm.joined_at, 'left_at', gm.left_at,
           'is_me', gm.account_id is not distinct from p_viewer, 'former', gm.account_id is null)
         order by gm.chests desc, gm.joined_at), '[]'::jsonb),
         coalesce(sum(gm.chests), 0)::integer, count(*)::integer
    into v_members, v_linked, v_linked_n
    from public.tk_party_game_members gm left join public.tk_accounts a on a.id = gm.account_id
   where gm.game_id = p_game;
  select coalesce(jsonb_agg(jsonb_build_object('slot', t.slot, 'label', t.label, 'mode', t.mode, 'exact_count', t.exact_count,
           'estimate_level', t.estimate_level, 'estimate_pct', t.estimate_pct, 'estimate_basis', t.estimate_basis,
           'estimate_count', t.estimate_count, 'linked', tkp.user_card(t.linked_account_id),
           'entered_by', tkp.user_card(t.entered_by), 'updated_at', t.updated_at) order by t.slot), '[]'::jsonb),
         coalesce(sum(t.exact_count) filter (where t.mode = 'exact'), 0)::integer,
         coalesce(sum(t.estimate_count) filter (where t.mode = 'estimated'), 0),
         (count(*) filter (where t.mode = 'unknown'))::integer,
         (count(*) filter (where t.mode = 'estimated'))::integer,
         count(*)::integer
    into v_mates, v_exact, v_est, v_unknown, v_est_n, v_mates_n
    from public.tk_party_game_teammates t where t.game_id = p_game;
  return jsonb_build_object('id', g.id, 'party_id', g.party_id, 'number', g.number, 'status', g.status, 'void', g.void,
    'confirmed', g.confirmed, 'started_at', g.started_at, 'ended_at', g.ended_at, 'auto_started', g.auto_started,
    'members', v_members, 'teammates', v_mates, 'linked_count', v_linked_n, 'open_slots', greatest(0, 4 - v_linked_n),
    'known_total', v_linked, 'reported_total', v_exact, 'estimated_total', v_est, 'unknown_count', v_unknown,
    'estimated_squad_total', v_linked + v_exact + v_est,
    'completeness', tkp.completeness(v_linked_n, v_mates_n, v_unknown, v_est_n, g.confirmed),
    'is_participant', exists (select 1 from public.tk_party_game_members gm where gm.game_id = p_game and gm.account_id = p_viewer));
end $$;

create or replace function tkp.party_state_json(p_party uuid, p_viewer uuid) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare p record; v_game uuid; v_last uuid;
begin
  select * into p from public.tk_parties x where x.id = p_party;
  if p.id is null or p.status <> 'active' then return null; end if;
  select g.id into v_game from public.tk_party_games g where g.party_id = p_party and g.status = 'active';
  select g.id into v_last from public.tk_party_games g where g.party_id = p_party and g.status = 'ended' order by g.ended_at desc limit 1;
  return jsonb_build_object('id', p.id, 'leader_id', p.leader_id, 'is_leader', p.leader_id = p_viewer,
    'auto_next', p.auto_next, 'members_can_invite', p.members_can_invite, 'created_at', p.created_at,
    'code_active', p.code_hash is not null and p.code_expires_at > now(),
    'code_expires_at', case when p.code_expires_at > now() then p.code_expires_at end,
    'version', coalesce((select s.version from public.tk_party_signals s where s.party_id = p_party), 0),
    'members', (select coalesce(jsonb_agg(tkp.user_card(m.account_id) || jsonb_build_object('joined_at', m.joined_at,
                  'is_leader', m.account_id = p.leader_id, 'is_me', m.account_id = p_viewer,
                  'presence', tkp.presence_of(m.account_id, true)) order by m.joined_at), '[]'::jsonb)
                  from public.tk_party_members m where m.party_id = p_party and m.left_at is null),
    'invites', (select coalesce(jsonb_agg(jsonb_build_object('id', i.id, 'to', tkp.user_card(i.to_id), 'from_id', i.from_id,
                  'created_at', i.created_at, 'expires_at', i.expires_at) order by i.created_at), '[]'::jsonb)
                  from public.tk_party_invites i where i.party_id = p_party and i.status = 'pending' and i.expires_at > now()),
    'game', case when v_game is null then null else tkp.game_json(v_game, p_viewer) end,
    'last_game', case when v_last is null then null else tkp.game_json(v_last, p_viewer) end);
end $$;

create or replace function tkp.end_game(p_game uuid, p_by uuid) returns boolean
language plpgsql security definer set search_path = '' as $$
declare v_party uuid;
begin
  update public.tk_party_games set status = 'ended', ended_at = now(), ended_by = p_by
   where id = p_game and status = 'active' returning party_id into v_party;
  if v_party is null then return false; end if;
  perform tkp.bump_party(v_party);
  return true;
end $$;

create or replace function tkp.disband(p_party uuid, p_by uuid) returns void
language plpgsql security definer set search_path = '' as $$
declare g uuid; inv record;
begin
  for g in select pg.id from public.tk_party_games pg where pg.party_id = p_party and pg.status = 'active' loop
    perform tkp.end_game(g, p_by);
  end loop;
  update public.tk_party_members set left_at = now(), left_reason = 'disbanded' where party_id = p_party and left_at is null;
  for inv in update public.tk_party_invites set status = 'cancelled', responded_at = now()
              where party_id = p_party and status = 'pending' returning to_id loop
    perform tkp.bump_account(inv.to_id);
  end loop;
  update public.tk_parties set status = 'disbanded', disbanded_at = now(), code_hash = null, code_expires_at = null where id = p_party;
  perform tkp.bump_party(p_party);
end $$;

create or replace function tkp.leave_party(p_account uuid, p_reason text) returns void
language plpgsql security definer set search_path = '' as $$
declare v_party uuid; v_leader uuid; v_next uuid; v_game uuid;
begin
  v_party := tkp.active_party_of(p_account);
  if v_party is null then return; end if;
  select p.leader_id into v_leader from public.tk_parties p where p.id = v_party for update;
  update public.tk_party_members set left_at = now(), left_reason = p_reason
   where party_id = v_party and account_id = p_account and left_at is null;
  select g.id into v_game from public.tk_party_games g where g.party_id = v_party and g.status = 'active';
  if v_game is not null then   -- their contribution so far stays in the game; they just stop adding to it
    update public.tk_party_game_members set left_at = now() where game_id = v_game and account_id = p_account and left_at is null;
  end if;
  update public.tk_party_invites set status = 'cancelled', responded_at = now()
   where party_id = v_party and from_id = p_account and status = 'pending';
  if not exists (select 1 from public.tk_party_members m where m.party_id = v_party and m.left_at is null) then
    perform tkp.disband(v_party, null);
    return;
  end if;
  if v_leader is not distinct from p_account or v_leader is null then
    select m.account_id into v_next from public.tk_party_members m left join public.tk_presence pr on pr.account_id = m.account_id
     where m.party_id = v_party and m.left_at is null
     order by (coalesce(pr.last_seen_at, '-infinity'::timestamptz) > now() - interval '75 seconds') desc, m.joined_at, m.account_id
     limit 1;
    update public.tk_parties set leader_id = v_next where id = v_party;
  end if;
  perform tkp.bump_party(v_party);
end $$;

-- Joins p_party (leaving any other party first). Returns null on success or an error code.
create or replace function tkp.join_party(p_account uuid, p_party uuid) returns text
language plpgsql security definer set search_path = '' as $$
declare v_current uuid := tkp.active_party_of(p_account); v_status text; v_count integer; v_game uuid;
begin
  if v_current = p_party then return null; end if;
  -- lock both parties in a fixed order so two people swapping parties can't deadlock
  perform 1 from public.tk_parties p where p.id in (p_party, coalesce(v_current, p_party)) order by p.id for update;
  select p.status into v_status from public.tk_parties p where p.id = p_party;
  if v_status is distinct from 'active' then return 'party_gone'; end if;
  if exists (select 1 from public.tk_party_members m where m.party_id = p_party and m.left_at is null
              and tkp.blocked_either(m.account_id, p_account)) then
    return 'unavailable';
  end if;
  select count(*) into v_count from public.tk_party_members m where m.party_id = p_party and m.left_at is null;
  if v_count >= 4 then return 'party_full'; end if;
  if v_current is not null then perform tkp.leave_party(p_account, 'switched'); end if;
  insert into public.tk_party_members (party_id, account_id) values (p_party, p_account);
  select g.id into v_game from public.tk_party_games g where g.party_id = p_party and g.status = 'active';
  if v_game is not null then  -- late joiners count only chests opened from now on
    insert into public.tk_party_game_members (game_id, account_id, username_snapshot)
    values (v_game, p_account, (select a.username from public.tk_accounts a where a.id = p_account))
    on conflict (game_id, account_id) do update set left_at = null;
  end if;
  perform tkp.bump_party(p_party);
  return null;
end $$;

create or replace function public.tk_party_state() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid := tkp.active_party_of(me);
begin
  if v_party is null then return jsonb_build_object('ok', true, 'party', null); end if;
  return jsonb_build_object('ok', true, 'party', tkp.party_state_json(v_party, me));
end $$;

create or replace function public.tk_party_create() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid;
begin
  if tkp.active_party_of(me) is not null then return jsonb_build_object('ok', false, 'error', 'already_in_party'); end if;
  if not tkp.rate_take('partycreate:' || me, 30, interval '1 hour') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  insert into public.tk_parties (leader_id) values (me) returning id into v_party;
  insert into public.tk_party_members (party_id, account_id) values (v_party, me);
  perform tkp.bump_party(v_party);
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'party', tkp.party_state_json(v_party, me));
end $$;

create or replace function public.tk_party_invite(p_account uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid := tkp.active_party_of(me); p record; v_inv uuid;
begin
  if v_party is null then return jsonb_build_object('ok', false, 'error', 'no_party'); end if;
  select * into p from public.tk_parties x where x.id = v_party for update;
  if p.leader_id is distinct from me and not p.members_can_invite then return jsonb_build_object('ok', false, 'error', 'not_allowed'); end if;
  if p_account is null or p_account = me then return jsonb_build_object('ok', false, 'error', 'self'); end if;
  if not tkp.are_friends(me, p_account) then return jsonb_build_object('ok', false, 'error', 'not_friends'); end if;
  if tkp.blocked_either(me, p_account) then return jsonb_build_object('ok', false, 'error', 'unavailable'); end if;
  if exists (select 1 from public.tk_party_members m where m.party_id = v_party and m.account_id = p_account and m.left_at is null) then
    return jsonb_build_object('ok', true, 'status', 'member');
  end if;
  if (select count(*) from public.tk_party_members m where m.party_id = v_party and m.left_at is null) >= 4 then
    return jsonb_build_object('ok', false, 'error', 'party_full');
  end if;
  if not tkp.rate_take('partyinvite:' || me, 40, interval '1 hour') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  update public.tk_party_invites set from_id = me, created_at = now(), expires_at = now() + interval '30 minutes'
   where party_id = v_party and to_id = p_account and status = 'pending' returning id into v_inv;
  if v_inv is null then
    insert into public.tk_party_invites (party_id, from_id, to_id) values (v_party, me, p_account) returning id into v_inv;
  end if;
  perform tkp.bump_account(p_account);
  perform tkp.bump_party(v_party);
  return jsonb_build_object('ok', true, 'status', 'invited', 'invite_id', v_inv);
end $$;

create or replace function public.tk_party_invite_respond(p_invite uuid, p_accept boolean) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); i record; v_err text;
begin
  select * into i from public.tk_party_invites x where x.id = p_invite and x.to_id = me for update;
  if i.id is null or i.status <> 'pending' then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  if i.expires_at < now() then
    update public.tk_party_invites set status = 'expired' where id = i.id;
    return jsonb_build_object('ok', false, 'error', 'expired');
  end if;
  if not coalesce(p_accept, false) then
    update public.tk_party_invites set status = 'declined', responded_at = now() where id = i.id;
    perform tkp.bump_party(i.party_id);
    perform tkp.bump_account(me);
    return jsonb_build_object('ok', true, 'status', 'declined');
  end if;
  v_err := tkp.join_party(me, i.party_id);
  if v_err is not null then
    if v_err = 'party_gone' then update public.tk_party_invites set status = 'cancelled', responded_at = now() where id = i.id; end if;
    return jsonb_build_object('ok', false, 'error', v_err);
  end if;
  update public.tk_party_invites set status = 'accepted', responded_at = now() where id = i.id;
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'status', 'joined', 'party', tkp.party_state_json(i.party_id, me));
end $$;

create or replace function public.tk_party_invite_cancel(p_invite uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); i record;
begin
  select x.* into i from public.tk_party_invites x join public.tk_parties p on p.id = x.party_id
   where x.id = p_invite and x.status = 'pending' and (x.from_id = me or p.leader_id = me) for update of x;
  if i.id is null then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  update public.tk_party_invites set status = 'cancelled', responded_at = now() where id = i.id;
  perform tkp.bump_account(i.to_id);
  perform tkp.bump_party(i.party_id);
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_party_code_create() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid := tkp.active_party_of(me); v_code text; v_exp timestamptz;
begin
  if v_party is null then return jsonb_build_object('ok', false, 'error', 'no_party'); end if;
  if (select p.leader_id from public.tk_parties p where p.id = v_party) is distinct from me then
    return jsonb_build_object('ok', false, 'error', 'not_leader');
  end if;
  if not tkp.rate_take('partycode:' || me, 20, interval '1 hour') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  for attempt in 1 .. 5 loop
    v_code := tkp.random_code(8);                        -- 40 random bits; joins by code are rate limited
    begin
      update public.tk_parties set code_hash = tkp.code_hash('party', v_code), code_expires_at = now() + interval '30 minutes'
       where id = v_party returning code_expires_at into v_exp;
      exit;
    exception when unique_violation then v_code := null;
    end;
  end loop;
  if v_code is null then return jsonb_build_object('ok', false, 'error', 'try_again'); end if;
  perform tkp.bump_party(v_party);
  return jsonb_build_object('ok', true, 'code', substr(v_code, 1, 4) || '-' || substr(v_code, 5, 4), 'expires_at', v_exp);
end $$;

create or replace function public.tk_party_code_revoke() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid := tkp.active_party_of(me);
begin
  update public.tk_parties set code_hash = null, code_expires_at = null where id = v_party and leader_id = me;
  if not found then return jsonb_build_object('ok', false, 'error', 'not_leader'); end if;
  perform tkp.bump_party(v_party);
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_party_join_code(p_code text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_code text := tkp.normalize_code(p_code); v_party uuid; v_err text; v_ip text := tkp.client_ip();
begin
  if not tkp.rate_take('partyjoin:' || me, 10, interval '10 minutes')
     or (v_ip is not null and not tkp.rate_take('partyjoin:ip:' || v_ip, 30, interval '10 minutes')) then
    return jsonb_build_object('ok', false, 'error', 'rate_limited');
  end if;
  if char_length(v_code) <> 8 then return jsonb_build_object('ok', false, 'error', 'invalid_code'); end if;
  select p.id into v_party from public.tk_parties p
   where p.code_hash = tkp.code_hash('party', v_code) and p.code_expires_at > now() and p.status = 'active';
  if v_party is null then return jsonb_build_object('ok', false, 'error', 'invalid_code'); end if;
  v_err := tkp.join_party(me, v_party);
  if v_err is not null then return jsonb_build_object('ok', false, 'error', v_err); end if;
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'party', tkp.party_state_json(v_party, me));
end $$;

create or replace function public.tk_party_leave() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  perform tkp.leave_party(me, 'left');
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_party_kick(p_account uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid := tkp.active_party_of(me);
begin
  if v_party is null then return jsonb_build_object('ok', false, 'error', 'no_party'); end if;
  if (select p.leader_id from public.tk_parties p where p.id = v_party) is distinct from me then return jsonb_build_object('ok', false, 'error', 'not_leader'); end if;
  if p_account = me then return jsonb_build_object('ok', false, 'error', 'self'); end if;
  if tkp.active_party_of(p_account) is distinct from v_party then return jsonb_build_object('ok', false, 'error', 'not_member'); end if;
  perform tkp.leave_party(p_account, 'kicked');
  perform tkp.bump_account(p_account);
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_party_transfer(p_account uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid := tkp.active_party_of(me);
begin
  if v_party is null then return jsonb_build_object('ok', false, 'error', 'no_party'); end if;
  perform 1 from public.tk_parties p where p.id = v_party for update;
  if (select p.leader_id from public.tk_parties p where p.id = v_party) is distinct from me then return jsonb_build_object('ok', false, 'error', 'not_leader'); end if;
  if tkp.active_party_of(p_account) is distinct from v_party then return jsonb_build_object('ok', false, 'error', 'not_member'); end if;
  update public.tk_parties set leader_id = p_account where id = v_party;
  perform tkp.bump_party(v_party);
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_party_disband() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid := tkp.active_party_of(me); m record;
begin
  if v_party is null then return jsonb_build_object('ok', false, 'error', 'no_party'); end if;
  perform 1 from public.tk_parties p where p.id = v_party for update;
  if (select p.leader_id from public.tk_parties p where p.id = v_party) is distinct from me then
    return jsonb_build_object('ok', false, 'error', 'not_leader');
  end if;
  for m in select x.account_id from public.tk_party_members x where x.party_id = v_party and x.left_at is null loop
    perform tkp.bump_account(m.account_id);
  end loop;
  perform tkp.disband(v_party, me);
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_party_settings(p_auto_next boolean default null, p_members_can_invite boolean default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid := tkp.active_party_of(me);
begin
  update public.tk_parties set auto_next = coalesce(p_auto_next, auto_next), members_can_invite = coalesce(p_members_can_invite, members_can_invite)
   where id = v_party and leader_id = me;
  if not found then return jsonb_build_object('ok', false, 'error', 'not_leader'); end if;
  perform tkp.bump_party(v_party);
  return jsonb_build_object('ok', true, 'party', tkp.party_state_json(v_party, me));
end $$;

create or replace function tkp.start_game(p_party uuid, p_by uuid, p_auto boolean) returns uuid
language plpgsql security definer set search_path = '' as $$
declare v_game uuid;
begin
  insert into public.tk_party_games (party_id, number, started_by, auto_started)
  values (p_party, coalesce((select max(g.number) from public.tk_party_games g where g.party_id = p_party), 0) + 1, p_by, p_auto)
  returning id into v_game;
  insert into public.tk_party_game_members (game_id, account_id, username_snapshot)
  select v_game, m.account_id, a.username from public.tk_party_members m join public.tk_accounts a on a.id = m.account_id
   where m.party_id = p_party and m.left_at is null;
  perform tkp.bump_party(p_party);
  return v_game;
end $$;

create or replace function public.tk_party_game_start() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid := tkp.active_party_of(me); v_leader uuid;
begin
  if v_party is null then return jsonb_build_object('ok', false, 'error', 'no_party'); end if;
  select p.leader_id into v_leader from public.tk_parties p where p.id = v_party for update;
  if v_leader is distinct from me then return jsonb_build_object('ok', false, 'error', 'not_leader'); end if;
  if exists (select 1 from public.tk_party_games g where g.party_id = v_party and g.status = 'active') then
    return jsonb_build_object('ok', true, 'already', true, 'party', tkp.party_state_json(v_party, me));
  end if;
  perform tkp.start_game(v_party, me, false);
  return jsonb_build_object('ok', true, 'party', tkp.party_state_json(v_party, me));
end $$;

create or replace function public.tk_party_game_end(p_game uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); g record; v_leader uuid;
begin
  select * into g from public.tk_party_games x where x.id = p_game for update;
  if g.id is null or tkp.active_party_of(me) is distinct from g.party_id then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  select p.leader_id into v_leader from public.tk_parties p where p.id = g.party_id;
  if v_leader is distinct from me then return jsonb_build_object('ok', false, 'error', 'not_leader'); end if;
  if g.status = 'ended' then return jsonb_build_object('ok', true, 'already', true, 'game', tkp.game_json(p_game, me)); end if;
  perform tkp.end_game(p_game, me);
  return jsonb_build_object('ok', true, 'game', tkp.game_json(p_game, me));
end $$;

create or replace function public.tk_party_game_void(p_game uuid, p_void boolean) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); g record;
begin
  select x.* into g from public.tk_party_games x join public.tk_parties p on p.id = x.party_id
   where x.id = p_game and p.leader_id = me and tkp.active_party_of(me) = x.party_id for update of x;
  if g.id is null then return jsonb_build_object('ok', false, 'error', 'not_leader'); end if;
  if g.status <> 'ended' then return jsonb_build_object('ok', false, 'error', 'game_active'); end if;
  update public.tk_party_games set void = coalesce(p_void, false) where id = p_game;
  insert into public.tk_corrections (account_id, game_id, kind, detail) values (me, p_game, 'game_void', jsonb_build_object('void', coalesce(p_void, false)));
  perform tkp.bump_party(g.party_id);
  return jsonb_build_object('ok', true, 'game', tkp.game_json(p_game, me));
end $$;

-- One chest opened by the caller. Idempotent on p_event_id (retries and reconnects can never double
-- count). Linked to the party's squad game only if it was opened while the caller was a member and
-- the game was running; otherwise it stays a purely personal chest ({"linked": false}).
create or replace function public.tk_party_chest_add(p_event_id uuid, p_opened_at timestamptz,
  p_session_ref text default null, p_game_ref text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_owner uuid; v_ev_game uuid; v_party uuid; v_joined timestamptz; v_auto_next boolean;
        v_gid uuid; v_gnum integer; v_gstart timestamptz; v_active boolean := false; v_auto boolean := false; v_count integer; n integer;
begin
  if p_event_id is null or p_opened_at is null then return jsonb_build_object('ok', false, 'error', 'invalid'); end if;
  select e.account_id, e.game_id into v_owner, v_ev_game from public.tk_chest_events e where e.id = p_event_id;
  if v_ev_game is not null then
    if v_owner is distinct from me then return jsonb_build_object('ok', false, 'error', 'conflict'); end if;
    return jsonb_build_object('ok', true, 'linked', true, 'duplicate', true, 'game_id', v_ev_game);
  end if;
  if not tkp.rate_take('chest:' || me, 300, interval '1 minute') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  select m.party_id, m.joined_at into v_party, v_joined from public.tk_party_members m where m.account_id = me and m.left_at is null;
  if v_party is null then return jsonb_build_object('ok', true, 'linked', false, 'reason', 'no_party'); end if;
  if p_opened_at < v_joined - interval '15 seconds' or p_opened_at > now() + interval '2 minutes' then
    return jsonb_build_object('ok', true, 'linked', false, 'reason', 'outside_membership');
  end if;
  select p.auto_next into v_auto_next from public.tk_parties p where p.id = v_party for update;   -- serialises auto-start
  select g.id, g.number, g.started_at into v_gid, v_gnum, v_gstart
    from public.tk_party_games g where g.party_id = v_party and g.status = 'active';
  v_active := v_gid is not null;
  if v_active and p_opened_at < v_gstart - interval '15 seconds' then
    v_gid := null;                                   -- opened before this game began: maybe the previous one
  end if;
  if v_gid is null then                              -- delayed delivery for a game that just ended?
    select g.id, g.number into v_gid, v_gnum from public.tk_party_games g
     where g.party_id = v_party and g.status = 'ended' and g.ended_at > now() - interval '10 minutes'
       and p_opened_at between g.started_at - interval '15 seconds' and g.ended_at
     order by g.ended_at desc limit 1;
  end if;
  if v_gid is null then
    if v_active then return jsonb_build_object('ok', true, 'linked', false, 'reason', 'before_game'); end if;
    if coalesce(v_auto_next, false) and p_opened_at > now() - interval '2 minutes' then
      v_gid := tkp.start_game(v_party, me, true);
      select g.number into v_gnum from public.tk_party_games g where g.id = v_gid;
      v_auto := true;
    else
      return jsonb_build_object('ok', true, 'linked', false, 'reason', 'no_game');
    end if;
  end if;
  insert into public.tk_chest_events (id, account_id, party_id, game_id, opened_at, session_ref, game_ref)
  values (p_event_id, me, v_party, v_gid, p_opened_at, tkp.clean_text(p_session_ref, 64), tkp.clean_text(p_game_ref, 64))
  on conflict (id) do nothing;
  get diagnostics n = row_count;
  if n = 0 then return jsonb_build_object('ok', true, 'linked', true, 'duplicate', true, 'game_id', v_gid); end if;
  insert into public.tk_party_game_members (game_id, account_id, username_snapshot, joined_at)
  values (v_gid, me, (select a.username from public.tk_accounts a where a.id = me), now())
  on conflict (game_id, account_id) do nothing;
  update public.tk_party_game_members set chests = chests + 1 where game_id = v_gid and account_id = me returning chests into v_count;
  perform tkp.bump_party(v_party);
  return jsonb_build_object('ok', true, 'linked', true, 'game_id', v_gid, 'game_number', v_gnum, 'auto_started', v_auto, 'chests', v_count);
end $$;

create or replace function public.tk_party_chest_remove(p_event_id uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); ev record; v_status text;
begin
  select * into ev from public.tk_chest_events e where e.id = p_event_id and e.account_id = me for update;
  if ev.id is null then return jsonb_build_object('ok', true, 'removed', false, 'reason', 'unknown'); end if;
  if ev.removed_at is not null then return jsonb_build_object('ok', true, 'removed', true, 'duplicate', true); end if;
  update public.tk_chest_events set removed_at = now(), removed_reason = 'user' where id = p_event_id;
  update public.tk_party_game_members set chests = greatest(0, chests - 1) where game_id = ev.game_id and account_id = me;
  select g.status into v_status from public.tk_party_games g where g.id = ev.game_id;
  if v_status = 'ended' then
    insert into public.tk_corrections (account_id, game_id, kind, detail) values (me, ev.game_id, 'chest_removed', jsonb_build_object('event', p_event_id));
  end if;
  perform tkp.bump_party(ev.party_id);
  return jsonb_build_object('ok', true, 'removed', true);
end $$;

-- Optional after-game assessment of teammates who don't use TOKENIFY. Exact counts are marked as
-- manually reported; estimates use the caller's own adjustable assumptions; "unknown" invents nothing.
create or replace function public.tk_party_teammate_set(p_game uuid, p_slot integer, p_label text, p_mode text,
  p_exact integer default null, p_level text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); g record; v_linked_n integer; v_basis integer; v_pct numeric; v_old jsonb; v_label text := tkp.clean_text(p_label, 24);
begin
  select * into g from public.tk_party_games x where x.id = p_game for update;
  if g.id is null or not exists (select 1 from public.tk_party_game_members gm where gm.game_id = p_game and gm.account_id = me) then
    return jsonb_build_object('ok', false, 'error', 'not_participant');
  end if;
  if g.status <> 'ended' then return jsonb_build_object('ok', false, 'error', 'game_active'); end if;
  if p_mode is null or p_mode not in ('exact', 'estimated', 'unknown') then return jsonb_build_object('ok', false, 'error', 'invalid'); end if;
  select count(*) into v_linked_n from public.tk_party_game_members gm where gm.game_id = p_game;
  if p_slot is null or p_slot < 1 or p_slot > least(3, 4 - v_linked_n) then return jsonb_build_object('ok', false, 'error', 'no_slot'); end if;
  if p_mode = 'exact' and (p_exact is null or p_exact < 0 or p_exact > 999) then return jsonb_build_object('ok', false, 'error', 'invalid_count'); end if;
  if p_mode = 'estimated' then
    if p_level is null or p_level not in ('barely', 'some', 'hunted') then return jsonb_build_object('ok', false, 'error', 'invalid'); end if;
    select gm.chests into v_basis from public.tk_party_game_members gm where gm.game_id = p_game and gm.account_id = me;
    v_pct := tkp.estimate_pct(me, p_level);
  end if;
  select to_jsonb(t) - 'id' - 'game_id' into v_old from public.tk_party_game_teammates t where t.game_id = p_game and t.slot = p_slot;
  insert into public.tk_party_game_teammates as t (game_id, slot, label, mode, exact_count, estimate_level, estimate_pct,
      estimate_basis, estimate_count, entered_by, updated_at)
  values (p_game, p_slot, v_label, p_mode,
          case when p_mode = 'exact' then p_exact end,
          case when p_mode = 'estimated' then p_level end,
          case when p_mode = 'estimated' then v_pct end,
          case when p_mode = 'estimated' then v_basis end,
          case when p_mode = 'estimated' then round(v_basis * v_pct / 100.0, 1) end,
          me, now())
  on conflict (game_id, slot) do update set label = excluded.label, mode = excluded.mode, exact_count = excluded.exact_count,
     estimate_level = excluded.estimate_level, estimate_pct = excluded.estimate_pct, estimate_basis = excluded.estimate_basis,
     estimate_count = excluded.estimate_count, entered_by = excluded.entered_by, updated_at = now();
  update public.tk_party_games set confirmed = true where id = p_game;
  insert into public.tk_corrections (account_id, game_id, kind, detail)
  values (me, p_game, 'teammate_set', jsonb_build_object('slot', p_slot, 'before', v_old, 'mode', p_mode, 'exact', p_exact, 'level', p_level));
  perform tkp.bump_party(g.party_id);
  return jsonb_build_object('ok', true, 'game', tkp.game_json(p_game, me));
end $$;

create or replace function public.tk_party_teammate_clear(p_game uuid, p_slot integer) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); g record; v_old jsonb;
begin
  select * into g from public.tk_party_games x where x.id = p_game for update;
  if g.id is null or not exists (select 1 from public.tk_party_game_members gm where gm.game_id = p_game and gm.account_id = me) then
    return jsonb_build_object('ok', false, 'error', 'not_participant');
  end if;
  delete from public.tk_party_game_teammates t where t.game_id = p_game and t.slot = p_slot returning to_jsonb(t) - 'id' - 'game_id' into v_old;
  if v_old is not null then
    insert into public.tk_corrections (account_id, game_id, kind, detail) values (me, p_game, 'teammate_cleared', jsonb_build_object('slot', p_slot, 'before', v_old));
  end if;
  perform tkp.bump_party(g.party_id);
  return jsonb_build_object('ok', true, 'game', tkp.game_json(p_game, me));
end $$;

-- "Everyone in that match was on TOKENIFY": makes a game with fewer than four linked players complete.
create or replace function public.tk_party_game_confirm(p_game uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); g record;
begin
  select * into g from public.tk_party_games x where x.id = p_game for update;
  if g.id is null or not exists (select 1 from public.tk_party_game_members gm where gm.game_id = p_game and gm.account_id = me) then
    return jsonb_build_object('ok', false, 'error', 'not_participant');
  end if;
  if g.status <> 'ended' then return jsonb_build_object('ok', false, 'error', 'game_active'); end if;
  update public.tk_party_games set confirmed = true where id = p_game;
  insert into public.tk_corrections (account_id, game_id, kind, detail) values (me, p_game, 'confirmed_all_linked', '{}'::jsonb);
  perform tkp.bump_party(g.party_id);
  return jsonb_build_object('ok', true, 'game', tkp.game_json(p_game, me));
end $$;

-- A teammate who was entered by hand later joined TOKENIFY: attach their account to that entry (the
-- count stays the manually reported one; nothing is added twice).
create or replace function public.tk_party_teammate_link(p_game uuid, p_slot integer, p_account uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); g record;
begin
  select * into g from public.tk_party_games x where x.id = p_game for update;
  if g.id is null or not exists (select 1 from public.tk_party_game_members gm where gm.game_id = p_game and gm.account_id = me) then
    return jsonb_build_object('ok', false, 'error', 'not_participant');
  end if;
  if p_account is not null then
    if not tkp.are_friends(me, p_account) then return jsonb_build_object('ok', false, 'error', 'not_friends'); end if;
    if exists (select 1 from public.tk_party_game_members gm where gm.game_id = p_game and gm.account_id = p_account) then
      return jsonb_build_object('ok', false, 'error', 'already_linked_member');
    end if;
  end if;
  update public.tk_party_game_teammates set linked_account_id = p_account, updated_at = now() where game_id = p_game and slot = p_slot;
  if not found then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  insert into public.tk_corrections (account_id, game_id, kind, detail) values (me, p_game, 'teammate_linked', jsonb_build_object('slot', p_slot, 'account', p_account));
  perform tkp.bump_party(g.party_id);
  return jsonb_build_object('ok', true, 'game', tkp.game_json(p_game, me));
end $$;

create or replace function tkp.squad_stats(p_account uuid) returns jsonb
language sql stable security definer set search_path = '' as $$
  with games as (
    select g.id, g.started_at, g.ended_at, g.confirmed,
           (select coalesce(sum(gm.chests), 0) from public.tk_party_game_members gm where gm.game_id = g.id) as linked_total,
           (select count(*) from public.tk_party_game_members gm where gm.game_id = g.id) as linked_n,
           (select coalesce(sum(t.exact_count) filter (where t.mode = 'exact'), 0) from public.tk_party_game_teammates t where t.game_id = g.id) as exact_total,
           (select coalesce(sum(t.estimate_count) filter (where t.mode = 'estimated'), 0) from public.tk_party_game_teammates t where t.game_id = g.id) as est_total,
           (select count(*) filter (where t.mode = 'unknown') from public.tk_party_game_teammates t where t.game_id = g.id) as unknown_n,
           (select count(*) filter (where t.mode = 'estimated') from public.tk_party_game_teammates t where t.game_id = g.id) as est_n,
           (select count(*) from public.tk_party_game_teammates t where t.game_id = g.id) as mates_n
      from public.tk_party_games g
      join public.tk_party_game_members mine on mine.game_id = g.id and mine.account_id = p_account
     where g.status = 'ended' and not g.void
  ), c as (
    select games.*, tkp.completeness(linked_n::integer, mates_n::integer, unknown_n::integer, est_n::integer, confirmed) as comp
      from games
  )
  select jsonb_build_object(
    'games', count(*),
    'linked', jsonb_build_object('games', count(*) filter (where comp = 'complete'),
                                 'chests', coalesce(sum(linked_total) filter (where comp = 'complete'), 0)),
    'estimated', jsonb_build_object('games', count(*) filter (where comp in ('complete', 'reported', 'estimated')),
                                    'chests', coalesce(sum(linked_total + exact_total + est_total) filter (where comp in ('complete', 'reported', 'estimated')), 0)),
    'partial_games', count(*) filter (where comp = 'partial'),
    'unconfirmed_games', count(*) filter (where comp = 'unconfirmed'),
    'linked_rate', jsonb_build_object(
        'games', count(*) filter (where comp = 'complete' and ended_at - started_at >= interval '5 minutes'),
        'chests', coalesce(sum(linked_total) filter (where comp = 'complete' and ended_at - started_at >= interval '5 minutes'), 0),
        'seconds', coalesce(extract(epoch from sum(ended_at - started_at) filter (where comp = 'complete' and ended_at - started_at >= interval '5 minutes')), 0)))
  from c
$$;

create or replace function public.tk_party_history(p_limit integer default 20) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  return jsonb_build_object('ok', true,
    'games', coalesce((select jsonb_agg(tkp.game_json(x.id, me) order by x.ended_at desc)
                         from (select g.id, g.ended_at from public.tk_party_games g
                                 join public.tk_party_game_members gm on gm.game_id = g.id and gm.account_id = me
                                where g.status = 'ended' order by g.ended_at desc
                                limit least(greatest(coalesce(p_limit, 20), 1), 50)) x), '[]'::jsonb),
    'stats', tkp.squad_stats(me));
end $$;

-- --------------------------------------------------------------------------------------- deletion
create or replace function public.tk_account_delete(p_password text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_hash text; f record;
begin
  if not tkp.rate_take('delete:' || me, 5, interval '1 hour') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  select a.password_hash into v_hash from public.tk_accounts a where a.id = me;
  if not tkp.check_password(p_password, v_hash) then return jsonb_build_object('ok', false, 'error', 'bad_password'); end if;
  perform tkp.leave_party(me, 'deleted');
  for f in select case when x.a_id = me then x.b_id else x.a_id end as other from public.tk_friendships x where x.a_id = me or x.b_id = me loop
    perform tkp.bump_account(f.other);
  end loop;
  perform tkp.end_sessions(me, null);
  -- squad history keeps the (now anonymous) contribution under the old name; everything personal goes
  delete from public.tk_accounts where id = me;
  return jsonb_build_object('ok', true);
end $$;

-- ----------------------------------------------------------------------------- live update signals
alter table public.tk_account_signals replica identity full;
alter table public.tk_party_signals replica identity full;
drop policy if exists "tk read own account signal" on public.tk_account_signals;
create policy "tk read own account signal" on public.tk_account_signals for select to authenticated
  using (account_id = tkp.current_account());
drop policy if exists "tk read party signal" on public.tk_party_signals;
create policy "tk read party signal" on public.tk_party_signals for select to authenticated
  using (tkp.is_party_member(party_id));

do $$
begin
  if exists (select 1 from pg_publication where pubname = 'supabase_realtime') then
    if not exists (select 1 from pg_publication_tables where pubname = 'supabase_realtime' and schemaname = 'public' and tablename = 'tk_party_signals') then
      execute 'alter publication supabase_realtime add table public.tk_party_signals';
    end if;
    if not exists (select 1 from pg_publication_tables where pubname = 'supabase_realtime' and schemaname = 'public' and tablename = 'tk_account_signals') then
      execute 'alter publication supabase_realtime add table public.tk_account_signals';
    end if;
  end if;
end $$;

-- ------------------------------------------------------------------------------------- privileges
-- Functions are executable by PUBLIC by default; lock everything down, then open exactly what's needed.
do $$
declare f record;
begin
  for f in select p.oid::regprocedure as sig, n.nspname
             from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where n.nspname = 'tkp' or (n.nspname = 'public' and p.proname like 'tk\_%') loop
    execute format('revoke all on function %s from public, anon, authenticated', f.sig);
    if f.nspname = 'public' then execute format('grant execute on function %s to authenticated', f.sig); end if;
  end loop;
end $$;
grant execute on function tkp.current_account() to authenticated;            -- used by RLS policies
grant execute on function tkp.is_party_member(uuid) to authenticated;        -- used by RLS policies
grant execute on function public.tk_username_available(text) to anon;        -- live check on the sign-up form

-- ------------------------------------------------------------------------------ retire v1 (email)
-- v1 used Supabase email accounts. Its write function would let any signed-in browser session store
-- data, so it is removed. The old table is dropped only if it is empty; otherwise its rows are kept.
drop function if exists public.push_tracker_state(bigint, jsonb, text);
drop function if exists public.delete_my_account();
do $$
begin
  if to_regclass('public.tracker_state') is not null then
    if not exists (select 1 from public.tracker_state) then execute 'drop table public.tracker_state'; end if;
  end if;
end $$;

-- What the SQL editor shows after running this file:
select (select count(*) from public.tk_accounts) as tokenify_accounts,
       (select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = 'public' and p.proname like 'tk\_%') as tk_functions,
       (select count(*) from auth.users u where not coalesce(u.is_anonymous, false)) as v1_email_users,
       to_regclass('public.tracker_state') is not null as v1_table_kept;
