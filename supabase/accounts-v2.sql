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
--   * Automatic PC tracking: the Windows app (TOKENIFY Companion) is linked once with a 10-minute pairing code
--     and then signs its requests with a device secret (hashed here, rotated every 30 days, revocable). It sends
--     one small summary per finished Fortnite match: chest count and times, match id, a hash of the Fortnite
--     account id. Never the replay file, other players' data, or anything else from the PC.
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

-- Automatic PC tracking. A Windows companion (TOKENIFY Companion) is linked to an account with a one-time
-- pairing code and from then on authenticates with its own device secret (stored here only as a hash). It reads
-- the replay Fortnite saves after each match and sends one compact summary per match: never the replay itself.
create table if not exists public.tk_devices (
  id                  uuid primary key default gen_random_uuid(),
  account_id          uuid not null references public.tk_accounts (id) on delete cascade,
  name                text not null default 'Windows PC',
  secret_hash         bytea not null,
  next_secret_hash    bytea,                         -- secret rotation: issued, not used by the companion yet
  secret_issued_at    timestamptz not null default now(),
  app_version         text,
  status              jsonb not null default '{}'::jsonb,
  status_at           timestamptz,
  rescan_requested_at timestamptz,
  created_at          timestamptz not null default now(),
  last_seen_at        timestamptz,
  last_sync_at        timestamptz,
  revoked_at          timestamptz,
  constraint tk_devices_name_len check (char_length(name) between 1 and 40)
);
create index if not exists tk_devices_account on public.tk_devices (account_id) where revoked_at is null;

create table if not exists public.tk_device_pairings (
  id         uuid primary key default gen_random_uuid(),
  account_id uuid not null references public.tk_accounts (id) on delete cascade,
  code_hash  bytea not null unique,
  created_at timestamptz not null default now(),
  expires_at timestamptz not null default now() + interval '10 minutes',
  used_at    timestamptz,
  device_id  uuid references public.tk_devices (id) on delete set null
);
create index if not exists tk_device_pairings_account on public.tk_device_pairings (account_id);

-- Fortnite accounts a TOKENIFY account counts chests for, as SHA-256 hashes of the Epic account id (never the
-- id itself). One Fortnite account belongs to one TOKENIFY account, so a shared PC can't send one player's chests
-- to someone else.
create table if not exists public.tk_replay_profiles (
  player_hash   text primary key check (player_hash ~ '^[0-9a-f]{64}$'),
  account_id    uuid not null references public.tk_accounts (id) on delete cascade,
  label         text check (label is null or char_length(label) <= 40),
  device_id     uuid references public.tk_devices (id) on delete set null,
  created_at    timestamptz not null default now(),
  last_match_at timestamptz
);
create index if not exists tk_replay_profiles_account on public.tk_replay_profiles (account_id);

-- One record per Fortnite match per player. match_key is "fn:<Fortnite match session id>" (the same for everyone
-- in that match) or, if the replay had none, "rg:<replay id>". Importing the same match again never adds it twice.
create table if not exists public.tk_replay_matches (
  id                uuid primary key default gen_random_uuid(),
  account_id        uuid not null references public.tk_accounts (id) on delete cascade,
  device_id         uuid references public.tk_devices (id) on delete set null,
  player_hash       text not null,
  match_key         text not null,
  replay_guid       text,
  session_id        text,
  started_at        timestamptz not null,
  ended_at          timestamptz not null,
  build             text,
  parser_version    text not null,
  registry_version  integer not null,
  chests            integer not null check (chests between 0 and 400),   -- replay-derived (latest parser)
  breakdown         jsonb not null default '{}'::jsonb,
  chest_times       jsonb not null default '[]'::jsonb,                    -- seconds since the match started
  partial           boolean not null default false,                        -- sent anyway although not fully readable
  corrected_chests  integer check (corrected_chests is null or corrected_chests between 0 and 400),
  correction_reason text,
  corrected_at      timestamptz,
  excluded          boolean not null default false,                        -- "not my match": counts nowhere
  historical        boolean not null default false,                        -- back-filled from old replays
  auto_session      uuid not null,                                         -- inferred session (matches close together)
  party_game_id     uuid references public.tk_party_games (id) on delete set null,
  party_link        text check (party_link is null or party_link in ('match-id', 'time-window', 'confirmed', 'ambiguous', 'conflict', 'detached')),
  imported_at       timestamptz not null default now(),
  updated_at        timestamptz not null default now(),
  revisions         jsonb not null default '[]'::jsonb,                    -- earlier results and corrections (audit)
  unique (account_id, match_key)
);
create index if not exists tk_replay_matches_account_time on public.tk_replay_matches (account_id, started_at desc);
create index if not exists tk_replay_matches_account_updated on public.tk_replay_matches (account_id, updated_at);
create index if not exists tk_replay_matches_session on public.tk_replay_matches (session_id) where session_id is not null;

-- Squad games: how each member's chests are counted ('live' as they happen, or 'replay' after the match) and the
-- replay result that replaced the live count. A pending replay is shown as pending, never as zero.
alter table public.tk_party_games add column if not exists match_session_id text;
alter table public.tk_party_game_members add column if not exists tracking text not null default 'live';
alter table public.tk_party_game_members add column if not exists replay_match_id uuid;
alter table public.tk_party_game_members add column if not exists replay_chests integer;
do $$
begin
  if not exists (select 1 from pg_constraint where conname = 'tk_party_game_members_tracking') then
    alter table public.tk_party_game_members add constraint tk_party_game_members_tracking check (tracking in ('live', 'replay'));
  end if;
  if not exists (select 1 from pg_constraint where conname = 'tk_party_game_members_replay_fk') then
    alter table public.tk_party_game_members add constraint tk_party_game_members_replay_fk
      foreign key (replay_match_id) references public.tk_replay_matches (id) on delete set null;
  end if;
end $$;

-- Row-level security everywhere; the client gets no direct table access at all...
do $$
declare t text;
begin
  foreach t in array array['tk_accounts', 'tk_sessions', 'tk_recovery_codes', 'tk_rate_events', 'tk_presence',
    'tk_account_signals', 'tk_party_signals', 'tk_friend_requests', 'tk_friendships', 'tk_blocks', 'tk_parties',
    'tk_party_members', 'tk_party_invites', 'tk_party_games', 'tk_party_game_members', 'tk_chest_events',
    'tk_party_game_teammates', 'tk_corrections', 'tk_tracker_state', 'tk_devices', 'tk_device_pairings',
    'tk_replay_profiles', 'tk_replay_matches'] loop
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
  delete from public.tk_device_pairings where expires_at < now() - interval '1 day';
end $$;

-- ------------------------------------------------------------------- automatic PC tracking helpers
create or replace function tkp.random_secret() returns text
language sql volatile set search_path = '' as $$
  select translate(encode(extensions.gen_random_bytes(32), 'base64'), '+/=', '-_')   -- 256 bits, URL-safe
$$;

create or replace function tkp.secret_hash(p_secret text) returns bytea
language sql immutable set search_path = '' as $$
  select extensions.digest('tokenify-device:' || coalesce(p_secret, ''), 'sha256')
$$;

-- Like clean_text but keeps non-ASCII letters (Fortnite names can use them); strips control characters only.
create or replace function tkp.clean_label(p_text text, p_max integer) returns text
language sql immutable set search_path = '' as $$
  select nullif(left(btrim(regexp_replace(coalesce(p_text, ''), '[[:cntrl:]]', '', 'g')), p_max), '')
$$;

create or replace function tkp.version_num(p_version text) returns bigint
language sql immutable set search_path = '' as $$
  select case when p_version ~ '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$'
              then split_part(p_version, '.', 1)::bigint * 100000000 + split_part(p_version, '.', 2)::bigint * 10000
                   + split_part(p_version, '.', 3)::bigint end
$$;

-- Checks a companion's device id + secret. Using the "next" secret completes a rotation.
create or replace function tkp.device_check(p_device uuid, p_secret text, out device_id uuid, out account_id uuid, out problem text)
language plpgsql security definer set search_path = '' as $$
declare d record; h bytea;
begin
  problem := 'device_unknown';
  if p_device is null or p_secret is null or char_length(p_secret) not between 32 and 128 then return; end if;
  h := tkp.secret_hash(p_secret);
  select x.id, x.account_id, x.secret_hash, x.next_secret_hash, x.revoked_at into d from public.tk_devices x where x.id = p_device for update;
  if d.id is null or (d.secret_hash <> h and d.next_secret_hash is distinct from h) then return; end if;
  if d.revoked_at is not null then problem := 'device_revoked'; return; end if;
  if d.secret_hash = h then
    update public.tk_devices set last_seen_at = now() where id = d.id;
  else
    update public.tk_devices set secret_hash = h, next_secret_hash = null, secret_issued_at = now(), last_seen_at = now() where id = d.id;
  end if;
  device_id := d.id;
  account_id := d.account_id;
  problem := null;
end $$;

-- Gate for every companion request: failed-secret throttling per IP, the device check, then a per-device budget.
create or replace function tkp.device_gate(p_device uuid, p_secret text, p_bucket text, p_max integer, p_window interval,
  out device_id uuid, out account_id uuid, out err jsonb)
language plpgsql security definer set search_path = '' as $$
declare v_ip text := coalesce(tkp.client_ip(), 'unknown'); c record;
begin
  if tkp.rate_count('devfail:' || v_ip, interval '10 minutes') >= 30 then
    err := jsonb_build_object('ok', false, 'error', 'rate_limited');
    return;
  end if;
  select * into c from tkp.device_check(p_device, p_secret);
  if c.problem is not null then
    insert into public.tk_rate_events (bucket) values ('devfail:' || v_ip);
    err := jsonb_build_object('ok', false, 'error', c.problem);
    return;
  end if;
  if p_bucket is not null and not tkp.rate_take(p_bucket || ':' || c.device_id, p_max, p_window) then
    err := jsonb_build_object('ok', false, 'error', 'rate_limited');
    return;
  end if;
  device_id := c.device_id;
  account_id := c.account_id;
end $$;

-- 'manual' (counted by hand), 'auto' (replays only) or 'hybrid' (by hand, then checked against the replay).
create or replace function tkp.tracking_mode(p_account uuid) returns text
language sql stable security definer set search_path = '' as $$
  select coalesce(nullif(a.settings ->> 'tracking', ''), 'manual') from public.tk_accounts a where a.id = p_account
$$;

-- How a member's chests are counted in a squad game: 'replay' when they rely on automatic PC tracking (the
-- result arrives after the match), otherwise 'live'.
create or replace function tkp.member_tracking(p_account uuid) returns text
language sql stable security definer set search_path = '' as $$
  select case when tkp.tracking_mode(p_account) = 'auto'
               and exists (select 1 from public.tk_devices d where d.account_id = p_account and d.revoked_at is null
                             and coalesce(d.last_seen_at, d.created_at) > now() - interval '30 days')
              then 'replay' else 'live' end
$$;

create or replace function tkp.auto_gap(p_account uuid) returns interval
language sql stable security definer set search_path = '' as $$
  select make_interval(mins => least(greatest(coalesce((a.settings ->> 'auto_gap')::integer, 30), 5), 240))
    from public.tk_accounts a where a.id = p_account
$$;

create or replace function tkp.revisions_push(p_list jsonb, p_item jsonb) returns jsonb
language sql immutable set search_path = '' as $$
  select coalesce((select jsonb_agg(e order by n) from jsonb_array_elements(coalesce(p_list, '[]'::jsonb) || jsonb_build_array(p_item))
                     with ordinality as x(e, n)
                    where n > jsonb_array_length(coalesce(p_list, '[]'::jsonb)) + 1 - 20), '[]'::jsonb)
$$;

-- What a companion reports about itself, reduced to known fields.
create or replace function tkp.device_status_clean(p jsonb) returns jsonb
language plpgsql immutable set search_path = '' as $$
declare v_state text; v_folder text;
begin
  if p is null or jsonb_typeof(p) <> 'object' then return null; end if;
  v_state := p ->> 'state';
  v_folder := p ->> 'folder';
  if v_state is null or v_state not in ('waiting', 'in_match', 'processing', 'syncing', 'up_to_date', 'needs_attention', 'paused') then
    v_state := 'waiting';
  end if;
  if v_folder not in ('ok', 'missing', 'empty', 'unreadable') then v_folder := null; end if;
  return jsonb_strip_nulls(jsonb_build_object('state', v_state, 'folder', v_folder,
    'queued', least(greatest(coalesce((p ->> 'queued')::integer, 0), 0), 100000),
    'attention', least(greatest(coalesce((p ->> 'attention')::integer, 0), 0), 100000),
    'last_match_chests', case when (p ->> 'last_match_chests') ~ '^[0-9]{1,3}$' then (p ->> 'last_match_chests')::integer end,
    'last_match_at', case when (p ->> 'last_match_at') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z$' then p ->> 'last_match_at' end,
    'parser', tkp.clean_text(p ->> 'parser', 16),
    'auto', case when jsonb_typeof(p -> 'auto') = 'boolean' then p -> 'auto' end));
exception when others then
  return jsonb_build_object('state', 'waiting');
end $$;

-- The Pirate Token exists from the start of this season (Chapter 7 Season 3: Nov 1, 2026, 12:00 AM Eastern).
-- Matches from before it never count. Change this one date for a new season (then re-run this file).
create or replace function tkp.season_start() returns timestamptz
language sql immutable set search_path = '' as $$
  select timestamptz '2026-11-01 04:00:00+00'
$$;

-- Validates one match summary from a companion. Returns null when acceptable, else a short reason.
create or replace function tkp.replay_problem(m jsonb) returns text
language plpgsql stable set search_path = '' as $$
declare v_chests integer; v_start timestamptz; v_end timestamptz; v_registry integer; v_secs numeric;
begin
  if m is null or jsonb_typeof(m) <> 'object' then return 'payload'; end if;
  if octet_length(m::text) > 20000 then return 'too_large'; end if;
  if coalesce(m ->> 'player_hash', '') !~ '^[0-9a-f]{64}$' then return 'player_hash'; end if;
  if (m ->> 'session_id') is not null and (m ->> 'session_id') !~ '^[A-Za-z0-9_-]{8,64}$' then return 'session_id'; end if;
  if (m ->> 'replay_guid') is not null and (m ->> 'replay_guid') !~ '^[A-Za-z0-9-]{8,64}$' then return 'replay_guid'; end if;
  if (m ->> 'session_id') is null and (m ->> 'replay_guid') is null then return 'no_match_id'; end if;
  if tkp.version_num(m ->> 'parser_version') is null then return 'parser_version'; end if;
  if tkp.version_num(m ->> 'parser_version') < tkp.version_num('1.0.0') then return 'parser_outdated'; end if;
  begin
    v_chests := (m ->> 'chests')::integer;
    v_start := (m ->> 'started_at')::timestamptz;
    v_end := (m ->> 'ended_at')::timestamptz;
    v_registry := (m ->> 'registry_version')::integer;
  exception when others then
    return 'types';
  end;
  if v_registry is null or v_registry < 1 or v_registry > 100000 then return 'registry_version'; end if;
  if v_chests is null or v_chests < 0 or v_chests > 400 then return 'chests'; end if;
  if v_start is null or v_end is null or v_end < v_start or v_end - v_start > interval '3 hours' then return 'times'; end if;
  if v_start > now() + interval '10 minutes' or v_start < now() - interval '3 years' then return 'times'; end if;
  if v_start < tkp.season_start() then return 'before_season'; end if;   -- no Pirate Token back then
  if jsonb_typeof(m -> 'chest_times') is distinct from 'array' or jsonb_array_length(m -> 'chest_times') <> v_chests then return 'chest_times'; end if;
  v_secs := extract(epoch from v_end - v_start) + 120;
  if exists (select 1 from jsonb_array_elements(m -> 'chest_times') e
              where jsonb_typeof(e) <> 'number' or (e #>> '{}')::numeric < 0 or (e #>> '{}')::numeric > v_secs) then
    return 'chest_times';
  end if;
  if (m -> 'breakdown') is not null then
    if jsonb_typeof(m -> 'breakdown') <> 'object' then return 'breakdown'; end if;
    if (select count(*) from jsonb_object_keys(m -> 'breakdown')) > 16 then return 'breakdown'; end if;
    if exists (select 1 from jsonb_each(m -> 'breakdown') b
                where b.key !~ '^[a-z]{1,16}$' or jsonb_typeof(b.value) <> 'number' or (b.value #>> '{}')::numeric not between 0 and 400) then
      return 'breakdown';
    end if;
  end if;
  if (m -> 'historical') is not null and jsonb_typeof(m -> 'historical') <> 'boolean' then return 'historical'; end if;
  if (m -> 'partial') is not null and jsonb_typeof(m -> 'partial') <> 'boolean' then return 'partial'; end if;
  if char_length(coalesce(m ->> 'build', '')) > 80 then return 'build'; end if;
  return null;
end $$;

-- Inferred play session for an imported match: the session of a match that ended less than the account's gap
-- (default 30 minutes) before it, or of one that starts less than the gap after it; otherwise a new one. Existing
-- matches never move, so an inferred session never changes once the site has shown it.
create or replace function tkp.auto_session_for(p_account uuid, p_start timestamptz, p_end timestamptz, p_historical boolean) returns uuid
language plpgsql volatile security definer set search_path = '' as $$
declare v uuid; gap interval := tkp.auto_gap(p_account);
begin
  select r.auto_session into v from public.tk_replay_matches r
   where r.account_id = p_account and r.historical = p_historical
     and r.started_at <= p_start and r.ended_at >= p_start - gap
   order by r.started_at desc limit 1;
  if v is null then
    select r.auto_session into v from public.tk_replay_matches r
     where r.account_id = p_account and r.historical = p_historical
       and r.started_at >= p_start and r.started_at <= p_end + gap
     order by r.started_at limit 1;
  end if;
  return coalesce(v, gen_random_uuid());
end $$;

create or replace function tkp.replay_json(p_id uuid) returns jsonb
language sql stable security definer set search_path = '' as $$
  select jsonb_build_object('id', r.id, 'match_key', r.match_key, 'started_at', r.started_at, 'ended_at', r.ended_at,
    'chests', coalesce(r.corrected_chests, r.chests), 'replay_chests', r.chests, 'corrected_chests', r.corrected_chests,
    'correction_reason', r.correction_reason, 'breakdown', r.breakdown, 'chest_times', r.chest_times,
    'partial', r.partial, 'excluded', r.excluded, 'historical', r.historical, 'auto_session', r.auto_session,
    'build', r.build, 'parser_version', r.parser_version, 'registry_version', r.registry_version,
    'has_match_id', r.session_id is not null, 'imported_at', r.imported_at, 'updated_at', r.updated_at,
    'revisions', jsonb_array_length(r.revisions),
    'device', (select d.name from public.tk_devices d where d.id = r.device_id),
    'party', case when r.party_link is null then null else jsonb_build_object('game_id', r.party_game_id, 'link', r.party_link,
               'number', (select pg.number from public.tk_party_games pg where pg.id = r.party_game_id)) end)
  from public.tk_replay_matches r where r.id = p_id
$$;

-- Ties an imported match to the squad game it belongs to and sets that member's contribution, or refreshes the
-- contribution of a match already tied. The link is made only when it is reliable:
--   1. "match-id": a squad game already tied (by a teammate's replay) to this exact Fortnite match, or
--   2. "time-window": the one squad game this player took part in that overlaps the match well.
-- Several candidates ("ambiguous") or a game that already has another replay from this player ("conflict") are
-- left for the player to confirm on the website. Nothing is ever merged on timestamps alone across players.
create or replace function tkp.replay_attach_party(p_match uuid) returns void
language plpgsql security definer set search_path = '' as $$
declare r record; v_game uuid; v_link text; v_n integer; v_party uuid; v_effective integer; v_gm record;
begin
  select * into r from public.tk_replay_matches x where x.id = p_match for update;
  if r.id is null then return; end if;
  v_effective := case when r.excluded then null else coalesce(r.corrected_chests, r.chests) end;

  if r.party_game_id is not null and r.party_link in ('match-id', 'time-window', 'confirmed') then
    update public.tk_party_game_members set replay_chests = v_effective
     where game_id = r.party_game_id and account_id = r.account_id and replay_match_id = r.id;
    select g.party_id into v_party from public.tk_party_games g where g.id = r.party_game_id;
    if v_party is not null then perform tkp.bump_party(v_party); end if;
    return;
  end if;
  if r.excluded or r.historical or r.party_link = 'detached' then return; end if;
  if tkp.tracking_mode(r.account_id) not in ('auto', 'hybrid') then return; end if;

  if r.session_id is not null then
    select g.id into v_game from public.tk_party_games g
      join public.tk_party_game_members gm on gm.game_id = g.id and gm.account_id = r.account_id
     where g.match_session_id = r.session_id and not g.void
     order by g.started_at desc limit 1;
    if v_game is not null then v_link := 'match-id'; end if;
  end if;

  if v_game is null then
    select count(*), min(x.id::text)::uuid into v_n, v_game from (
      select g.id from public.tk_party_games g
        join public.tk_party_game_members gm on gm.game_id = g.id and gm.account_id = r.account_id
       where not g.void
         and (g.match_session_id is null or g.match_session_id = r.session_id)
         and g.started_at < r.ended_at + interval '2 minutes'
         and coalesce(g.ended_at, now()) > r.started_at - interval '2 minutes'
         and extract(epoch from least(coalesce(g.ended_at, now()), r.ended_at) - greatest(g.started_at, r.started_at))
             >= 0.5 * least(extract(epoch from coalesce(g.ended_at, now()) - g.started_at), extract(epoch from r.ended_at - r.started_at), 600)
    ) x;
    if v_n = 1 then
      v_link := 'time-window';
    elsif v_n > 1 then
      update public.tk_replay_matches set party_link = 'ambiguous', party_game_id = null where id = r.id;
      return;
    else
      return;
    end if;
  end if;

  select * into v_gm from public.tk_party_game_members gm where gm.game_id = v_game and gm.account_id = r.account_id for update;
  if v_gm.replay_match_id is not null and v_gm.replay_match_id <> r.id then
    update public.tk_replay_matches set party_link = 'conflict', party_game_id = v_game where id = r.id;
    return;
  end if;
  update public.tk_party_game_members set replay_match_id = r.id, replay_chests = v_effective where id = v_gm.id;
  update public.tk_replay_matches set party_game_id = v_game, party_link = v_link where id = r.id;
  update public.tk_party_games set match_session_id = coalesce(match_session_id, r.session_id) where id = v_game;
  insert into public.tk_corrections (account_id, game_id, kind, detail)
  values (r.account_id, v_game, 'replay_linked', jsonb_build_object('match', r.id, 'link', v_link, 'chests', v_effective));
  select g.party_id into v_party from public.tk_party_games g where g.id = v_game;
  perform tkp.bump_party(v_party);
end $$;

-- Removes a match's contribution from its squad game (used for "not my match" and manual re-linking).
create or replace function tkp.replay_unlink_party(p_match uuid, p_link text) returns void
language plpgsql security definer set search_path = '' as $$
declare r record; v_party uuid;
begin
  select * into r from public.tk_replay_matches x where x.id = p_match for update;
  if r.id is null then return; end if;
  if r.party_game_id is not null then
    update public.tk_party_game_members set replay_match_id = null, replay_chests = null
     where game_id = r.party_game_id and replay_match_id = r.id;
    select g.party_id into v_party from public.tk_party_games g where g.id = r.party_game_id;
    insert into public.tk_corrections (account_id, game_id, kind, detail)
    values (r.account_id, r.party_game_id, 'replay_unlinked', jsonb_build_object('match', r.id, 'reason', p_link));
  end if;
  update public.tk_replay_matches set party_game_id = null, party_link = p_link where id = r.id;
  if v_party is not null then perform tkp.bump_party(v_party); end if;
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
        v_est numeric; v_unknown integer; v_est_n integer; v_mates_n integer; v_pending integer; v_missing integer;
begin
  select * into g from public.tk_party_games pg where pg.id = p_game;
  if g.id is null then return null; end if;
  -- A member on automatic PC tracking counts with their replay result; until it arrives they are pending
  -- (never shown as zero). A replay that never arrived (45 minutes after the end) is "missing": unknown.
  with mm as (
    select gm.account_id, gm.joined_at, gm.left_at, gm.chests as live_chests, gm.tracking, gm.replay_chests,
           coalesce(a.username, gm.username_snapshot) as uname, coalesce(a.avatar, 'compass') as av,
           case when gm.replay_chests is not null then gm.replay_chests when gm.tracking = 'replay' then null else gm.chests end as eff,
           case when gm.replay_chests is not null then 'synced'
                when gm.tracking <> 'replay' then null
                when g.status = 'active' then 'awaiting'
                when exists (select 1 from public.tk_devices d where d.account_id = gm.account_id and d.revoked_at is null
                               and d.status ->> 'state' = 'processing' and d.status_at > now() - interval '3 minutes') then 'processing'
                when g.ended_at < now() - interval '45 minutes' then 'missing'
                else 'awaiting' end as rstate
      from public.tk_party_game_members gm left join public.tk_accounts a on a.id = gm.account_id
     where gm.game_id = p_game)
  select coalesce(jsonb_agg(jsonb_build_object('account_id', mm.account_id, 'username', mm.uname, 'avatar', mm.av,
           'chests', mm.eff, 'live_chests', mm.live_chests, 'joined_at', mm.joined_at, 'left_at', mm.left_at,
           'source', case when mm.replay_chests is not null or mm.tracking = 'replay' then 'replay' else 'live' end,
           'replay_state', mm.rstate, 'pending', mm.eff is null and mm.rstate in ('awaiting', 'processing'),
           'missing', mm.rstate = 'missing',
           'is_me', mm.account_id is not distinct from p_viewer, 'former', mm.account_id is null)
         order by coalesce(mm.eff, -1) desc, mm.joined_at), '[]'::jsonb),
         coalesce(sum(mm.eff), 0)::integer, count(*)::integer,
         (count(*) filter (where mm.eff is null and mm.rstate in ('awaiting', 'processing')))::integer,
         (count(*) filter (where mm.rstate = 'missing'))::integer
    into v_members, v_linked, v_linked_n, v_pending, v_missing
    from mm;
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
    'pending_count', v_pending, 'missing_count', v_missing,
    'estimated_squad_total', v_linked + v_exact + v_est,
    'completeness', case when v_pending > 0 then 'pending' when v_missing > 0 then 'partial'
                         else tkp.completeness(v_linked_n, v_mates_n, v_unknown, v_est_n, g.confirmed) end,
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
    insert into public.tk_party_game_members (game_id, account_id, username_snapshot, tracking)
    values (v_game, p_account, (select a.username from public.tk_accounts a where a.id = p_account), tkp.member_tracking(p_account))
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
  insert into public.tk_party_game_members (game_id, account_id, username_snapshot, tracking)
  select v_game, m.account_id, a.username, tkp.member_tracking(m.account_id)
    from public.tk_party_members m join public.tk_accounts a on a.id = m.account_id
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
  insert into public.tk_party_game_members (game_id, account_id, username_snapshot, joined_at, tracking)
  values (v_gid, me, (select a.username from public.tk_accounts a where a.id = me), now(), tkp.member_tracking(me))
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
           (select coalesce(sum(case when gm.replay_chests is not null then gm.replay_chests when gm.tracking = 'replay' then 0 else gm.chests end), 0)
              from public.tk_party_game_members gm where gm.game_id = g.id) as linked_total,
           (select count(*) from public.tk_party_game_members gm where gm.game_id = g.id) as linked_n,
           (select count(*) from public.tk_party_game_members gm where gm.game_id = g.id and gm.tracking = 'replay' and gm.replay_chests is null) as waiting_n,
           (select coalesce(sum(t.exact_count) filter (where t.mode = 'exact'), 0) from public.tk_party_game_teammates t where t.game_id = g.id) as exact_total,
           (select coalesce(sum(t.estimate_count) filter (where t.mode = 'estimated'), 0) from public.tk_party_game_teammates t where t.game_id = g.id) as est_total,
           (select count(*) filter (where t.mode = 'unknown') from public.tk_party_game_teammates t where t.game_id = g.id) as unknown_n,
           (select count(*) filter (where t.mode = 'estimated') from public.tk_party_game_teammates t where t.game_id = g.id) as est_n,
           (select count(*) from public.tk_party_game_teammates t where t.game_id = g.id) as mates_n
      from public.tk_party_games g
      join public.tk_party_game_members mine on mine.game_id = g.id and mine.account_id = p_account
     where g.status = 'ended' and not g.void
  ), c as (
    -- a replay still on its way keeps the game out of every squad figure until it arrives (or is given up on)
    select games.*, case when waiting_n > 0 and ended_at > now() - interval '45 minutes' then 'pending'
                         when waiting_n > 0 then 'partial'
                         else tkp.completeness(linked_n::integer, mates_n::integer, unknown_n::integer, est_n::integer, confirmed) end as comp
      from games
  )
  select jsonb_build_object(
    'games', count(*),
    'linked', jsonb_build_object('games', count(*) filter (where comp = 'complete'),
                                 'chests', coalesce(sum(linked_total) filter (where comp = 'complete'), 0)),
    'estimated', jsonb_build_object('games', count(*) filter (where comp in ('complete', 'reported', 'estimated')),
                                    'chests', coalesce(sum(linked_total + exact_total + est_total) filter (where comp in ('complete', 'reported', 'estimated')), 0)),
    'partial_games', count(*) filter (where comp = 'partial'),
    'pending_games', count(*) filter (where comp = 'pending'),
    'unconfirmed_games', count(*) filter (where comp = 'unconfirmed'),
    'linked_rate', jsonb_build_object(
        'games', count(*) filter (where comp = 'complete' and ended_at - started_at >= interval '5 minutes'),
        'chests', coalesce(sum(linked_total) filter (where comp = 'complete' and ended_at - started_at >= interval '5 minutes'), 0),
        'seconds', coalesce(extract(epoch from sum(ended_at - started_at) filter (where comp = 'complete' and ended_at - started_at >= interval '5 minutes')), 0)))
  from c
$$;

-- The party total of a player's most recent squad game (last 24 hours), for the Windows tracker's optional
-- "squad" line. Only chests that are really known are added up: linked players' own counts and teammates
-- entered as exact numbers, never estimates. "complete" says whether that covers every player of the game.
create or replace function tkp.squad_last(p_account uuid) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare v_game uuid; j jsonb;
begin
  select g.id into v_game from public.tk_party_games g
    join public.tk_party_game_members gm on gm.game_id = g.id and gm.account_id = p_account
   where g.status = 'ended' and not g.void and g.ended_at > now() - interval '24 hours'
   order by g.ended_at desc limit 1;
  if v_game is null then return null; end if;
  j := tkp.game_json(v_game, p_account);
  return jsonb_build_object(
    'chests', (j ->> 'known_total')::integer + (j ->> 'reported_total')::integer,
    'complete', (j ->> 'completeness') in ('complete', 'reported'),
    'members', (j ->> 'linked_count')::integer + jsonb_array_length(j -> 'teammates'),
    'reported', (select count(*) from jsonb_array_elements(j -> 'members') m where jsonb_typeof(m -> 'chests') = 'number')
              + (select count(*) from jsonb_array_elements(j -> 'teammates') t where t ->> 'mode' = 'exact'),
    'ended_at', j -> 'ended_at');
end $$;

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

-- ------------------------------------------------------------- automatic PC tracking: the companion
-- These are called by TOKENIFY Companion with its device id + secret (no browser session), so they are also open
-- to the anon role; each one checks the device itself (tkp.device_gate).

-- Links a new companion with the one-time code shown on the website (10 minutes, single use, 40 bits, throttled
-- per IP and overall). Returns the device secret exactly once.
create or replace function public.tk_device_pair(p_code text, p_name text default null, p_app_version text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_ip text := coalesce(tkp.client_ip(), 'unknown'); v_code text := tkp.normalize_code(p_code); pr record;
        v_secret text; v_dev uuid;
begin
  if not tkp.rate_take('pair-ip:' || v_ip, 10, interval '10 minutes') or not tkp.rate_take('pair-all', 500, interval '10 minutes') then
    return jsonb_build_object('ok', false, 'error', 'rate_limited');
  end if;
  if char_length(v_code) <> 8 then return jsonb_build_object('ok', false, 'error', 'bad_code'); end if;
  select * into pr from public.tk_device_pairings x where x.code_hash = tkp.code_hash('pair', v_code) for update;
  if pr.id is null or pr.used_at is not null or pr.expires_at < now() then return jsonb_build_object('ok', false, 'error', 'bad_code'); end if;
  if (select count(*) from public.tk_devices d where d.account_id = pr.account_id and d.revoked_at is null) >= 5 then
    return jsonb_build_object('ok', false, 'error', 'too_many_devices');
  end if;
  v_secret := tkp.random_secret();
  insert into public.tk_devices (account_id, name, secret_hash, app_version, last_seen_at)
  values (pr.account_id, coalesce(tkp.clean_label(p_name, 40), 'Windows PC'), tkp.secret_hash(v_secret), tkp.clean_text(p_app_version, 24), now())
  returning id into v_dev;
  update public.tk_device_pairings set used_at = now(), device_id = v_dev where id = pr.id;
  -- linking a PC means "count my chests automatically" unless the player already chose otherwise
  update public.tk_accounts a set settings = jsonb_set(a.settings, '{tracking}', '"auto"')
   where a.id = pr.account_id and coalesce(a.settings ->> 'tracking', '') = '';
  perform tkp.bump_account(pr.account_id);
  return jsonb_build_object('ok', true, 'device_id', v_dev, 'secret', v_secret, 'account', tkp.user_card(pr.account_id),
    'tracking', tkp.tracking_mode(pr.account_id), 'season_start', tkp.season_start());
end $$;

-- Regular check-in: reports the companion's status, returns the account's choices, and rotates the secret
-- every 30 days (the new secret becomes the only valid one the first time it is used).
create or replace function public.tk_device_hello(p_device uuid, p_secret text, p_status jsonb default null, p_app_version text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare g record; d record; v_rotate text; v_status jsonb := tkp.device_status_clean(p_status); v_party uuid;
begin
  select * into g from tkp.device_gate(p_device, p_secret, 'devhello', 180, interval '1 hour');
  if g.err is not null then return g.err; end if;
  select * into d from public.tk_devices x where x.id = g.device_id for update;
  if d.secret_issued_at < now() - interval '30 days' then
    v_rotate := tkp.random_secret();
    update public.tk_devices set next_secret_hash = tkp.secret_hash(v_rotate) where id = d.id;
  end if;
  update public.tk_devices set status = coalesce(v_status, status), status_at = case when v_status is not null then now() else status_at end,
         app_version = coalesce(tkp.clean_text(p_app_version, 24), app_version), rescan_requested_at = null
   where id = d.id;
  if v_status is not null and d.status is distinct from v_status then
    perform tkp.bump_account(d.account_id);
    v_party := tkp.active_party_of(d.account_id);
    if v_party is not null and (d.status ->> 'state') is distinct from (v_status ->> 'state') then perform tkp.bump_party(v_party); end if;
  end if;
  return jsonb_build_object('ok', true, 'account', tkp.user_card(d.account_id), 'device_name', d.name,
    'tracking', tkp.tracking_mode(d.account_id), 'rotate_secret', v_rotate, 'rescan', d.rescan_requested_at is not null,
    'profiles', (select coalesce(jsonb_agg(jsonb_build_object('player_hash', p.player_hash, 'label', p.label) order by p.created_at), '[]'::jsonb)
                   from public.tk_replay_profiles p where p.account_id = d.account_id),
    'season_start', tkp.season_start(),
    'squad', tkp.squad_last(d.account_id),
    'server_time', now());
end $$;

-- "This Fortnite account is mine": first claim wins, so one player's chests can't land on another account.
create or replace function public.tk_device_profile_claim(p_device uuid, p_secret text, p_player_hash text, p_label text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare g record; v_owner uuid;
begin
  select * into g from tkp.device_gate(p_device, p_secret, 'devclaim', 30, interval '1 hour');
  if g.err is not null then return g.err; end if;
  if coalesce(p_player_hash, '') !~ '^[0-9a-f]{64}$' then return jsonb_build_object('ok', false, 'error', 'invalid'); end if;
  select p.account_id into v_owner from public.tk_replay_profiles p where p.player_hash = p_player_hash for update;
  if v_owner is not null and v_owner <> g.account_id then return jsonb_build_object('ok', false, 'error', 'profile_conflict'); end if;
  insert into public.tk_replay_profiles (player_hash, account_id, label, device_id)
  values (p_player_hash, g.account_id, tkp.clean_label(p_label, 40), g.device_id)
  on conflict (player_hash) do update set label = coalesce(excluded.label, public.tk_replay_profiles.label), device_id = excluded.device_id;
  perform tkp.bump_account(g.account_id);
  return jsonb_build_object('ok', true);
end $$;

-- One finished match. Idempotent: the same match sent again (retry, reinstall, second PC) never counts twice;
-- a newer parser's result replaces the old one (kept in revisions) but never a player's own correction.
create or replace function public.tk_device_import(p_device uuid, p_secret text, p_match jsonb) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare g record; m jsonb := p_match; v_problem text; v_hash text; v_session text; v_guid text; v_key text;
        v_start timestamptz; v_end timestamptz; v_chests integer; v_parser text; v_registry integer; v_hist boolean;
        v_partial boolean; v_owner uuid; ex record; v_id uuid; v_status text; v_breakdown jsonb; v_times jsonb; v_build text;
begin
  select * into g from tkp.device_gate(p_device, p_secret, 'devimp', 600, interval '1 hour');
  if g.err is not null then return g.err; end if;
  v_problem := tkp.replay_problem(m);
  if v_problem is not null then return jsonb_build_object('ok', false, 'error', 'invalid_match', 'detail', v_problem); end if;
  v_hash := m ->> 'player_hash';
  select p.account_id into v_owner from public.tk_replay_profiles p where p.player_hash = v_hash;
  if v_owner is null then return jsonb_build_object('ok', false, 'error', 'profile_unclaimed'); end if;
  if v_owner <> g.account_id then return jsonb_build_object('ok', false, 'error', 'profile_conflict'); end if;

  v_session := lower(m ->> 'session_id');
  v_guid := lower(m ->> 'replay_guid');
  v_key := case when v_session is not null then 'fn:' || v_session else 'rg:' || v_guid end;
  v_start := (m ->> 'started_at')::timestamptz;
  v_end := (m ->> 'ended_at')::timestamptz;
  v_chests := (m ->> 'chests')::integer;
  v_parser := m ->> 'parser_version';
  v_registry := (m ->> 'registry_version')::integer;
  v_hist := coalesce((m ->> 'historical')::boolean, false);
  v_partial := coalesce((m ->> 'partial')::boolean, false);
  v_breakdown := coalesce(m -> 'breakdown', '{}'::jsonb);
  v_times := m -> 'chest_times';
  v_build := tkp.clean_text(m ->> 'build', 80);

  -- the same match, or the same recording under its replay id (a newer parser may have found the match id)
  select * into ex from public.tk_replay_matches r
   where r.account_id = g.account_id and (r.match_key = v_key or (v_guid is not null and r.replay_guid = v_guid))
   order by (r.match_key = v_key) desc limit 1 for update;
  if ex.id is null then
    insert into public.tk_replay_matches (account_id, device_id, player_hash, match_key, replay_guid, session_id, started_at, ended_at,
      build, parser_version, registry_version, chests, breakdown, chest_times, partial, historical, auto_session)
    values (g.account_id, g.device_id, v_hash, v_key, v_guid, v_session, v_start, v_end, v_build, v_parser, v_registry,
      v_chests, v_breakdown, v_times, v_partial, v_hist, tkp.auto_session_for(g.account_id, v_start, v_end, v_hist))
    returning id into v_id;
    v_status := 'created';
  else
    v_id := ex.id;
    if ex.player_hash <> v_hash then return jsonb_build_object('ok', false, 'error', 'conflict'); end if;
    if tkp.version_num(v_parser) < tkp.version_num(ex.parser_version)
       or (not ex.partial and v_partial)
       or (ex.chests = v_chests and ex.parser_version = v_parser and ex.registry_version = v_registry and ex.partial = v_partial
           and ex.match_key = v_key and ex.chest_times = v_times) then
      v_status := 'unchanged';   -- a retry, an older companion, or a partial read of a match already complete
    else
      update public.tk_replay_matches set
        match_key = v_key, session_id = coalesce(v_session, session_id), replay_guid = coalesce(v_guid, replay_guid),
        started_at = v_start, ended_at = v_end, build = coalesce(v_build, build), parser_version = v_parser,
        registry_version = v_registry, chests = v_chests, breakdown = v_breakdown, chest_times = v_times,
        partial = v_partial, device_id = g.device_id, updated_at = now(),
        revisions = tkp.revisions_push(revisions, jsonb_build_object('at', now(), 'kind', 'parser',
          'parser_version', ex.parser_version, 'registry_version', ex.registry_version, 'chests', ex.chests, 'partial', ex.partial))
       where id = ex.id;
      v_status := case when ex.corrected_chests is not null then 'updated_kept_correction' else 'updated' end;
    end if;
  end if;

  update public.tk_devices set last_sync_at = now() where id = g.device_id;
  update public.tk_replay_profiles set last_match_at = greatest(coalesce(last_match_at, v_start), v_start) where player_hash = v_hash;
  if v_status <> 'unchanged' then
    perform tkp.replay_attach_party(v_id);
    perform tkp.bump_account(g.account_id);
  end if;
  return jsonb_build_object('ok', true, 'status', v_status, 'match', tkp.replay_json(v_id));
end $$;

-- The server's view of recent matches, so a reinstalled or second companion shows what TOKENIFY already has.
create or replace function public.tk_device_matches(p_device uuid, p_secret text, p_limit integer default 50) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare g record;
begin
  select * into g from tkp.device_gate(p_device, p_secret, 'devlist', 60, interval '1 hour');
  if g.err is not null then return g.err; end if;
  return jsonb_build_object('ok', true, 'matches', coalesce((
    select jsonb_agg(jsonb_build_object('match_key', x.match_key, 'replay_guid', x.replay_guid,
             'chests', coalesce(x.corrected_chests, x.chests), 'replay_chests', x.chests, 'corrected', x.corrected_chests is not null,
             'excluded', x.excluded, 'party_link', x.party_link, 'parser_version', x.parser_version, 'updated_at', x.updated_at)
           order by x.started_at desc)
      from (select * from public.tk_replay_matches r where r.account_id = g.account_id
             order by r.started_at desc limit least(greatest(coalesce(p_limit, 50), 1), 500)) x), '[]'::jsonb));
end $$;

-- A correction, or "leave this match out", made in the Windows tracker, so the website shows the same count.
-- Same rules as the website's own tk_replay_correct / tk_replay_exclude: the replay's number stays on record,
-- a newer parser never overwrites a correction, and p_chests = null removes it. Sending the same thing twice
-- changes nothing.
create or replace function public.tk_device_correct(p_device uuid, p_secret text, p_player_hash text, p_match_key text,
  p_chests integer default null, p_excluded boolean default null, p_reason text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare g record; r record; v_key text := lower(coalesce(p_match_key, '')); v_excluded boolean; v_changed boolean := false;
begin
  select * into g from tkp.device_gate(p_device, p_secret, 'devcorrect', 120, interval '1 hour');
  if g.err is not null then return g.err; end if;
  if p_chests is not null and (p_chests < 0 or p_chests > 400) then return jsonb_build_object('ok', false, 'error', 'invalid_count'); end if;
  if v_key !~ '^(fn|rg):[a-z0-9_-]{8,64}$' or coalesce(p_player_hash, '') !~ '^[0-9a-f]{64}$' then
    return jsonb_build_object('ok', false, 'error', 'invalid');
  end if;
  select * into r from public.tk_replay_matches x
   where x.account_id = g.account_id and x.match_key = v_key and x.player_hash = p_player_hash for update;
  if r.id is null then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  v_excluded := coalesce(p_excluded, r.excluded);

  if r.corrected_chests is distinct from p_chests then
    update public.tk_replay_matches set corrected_chests = p_chests,
           correction_reason = case when p_chests is null then null else tkp.clean_label(p_reason, 120) end,
           corrected_at = case when p_chests is null then null else now() end, updated_at = now(),
           revisions = tkp.revisions_push(revisions, jsonb_build_object('at', now(),
             'kind', case when p_chests is null then 'correction_cleared' else 'correction' end, 'source', 'tracker',
             'before', coalesce(r.corrected_chests, r.chests), 'after', coalesce(p_chests, r.chests), 'reason', tkp.clean_label(p_reason, 120)))
     where id = r.id;
    v_changed := true;
  end if;
  if r.excluded is distinct from v_excluded then
    update public.tk_replay_matches set excluded = v_excluded, updated_at = now(),
           revisions = tkp.revisions_push(revisions, jsonb_build_object('at', now(),
             'kind', case when v_excluded then 'excluded' else 'included' end, 'source', 'tracker'))
     where id = r.id;
    v_changed := true;
  end if;
  if v_changed then
    if v_excluded then perform tkp.replay_unlink_party(r.id, null); else perform tkp.replay_attach_party(r.id); end if;
    perform tkp.bump_account(g.account_id);
  end if;
  return jsonb_build_object('ok', true, 'status', case when v_changed then 'updated' else 'unchanged' end, 'match', tkp.replay_json(r.id));
end $$;

-- "Disconnect" from the companion itself.
create or replace function public.tk_device_disconnect(p_device uuid, p_secret text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare g record;
begin
  select * into g from tkp.device_gate(p_device, p_secret, null, null, null);
  if g.err is not null then
    return case when g.err ->> 'error' = 'device_revoked' then jsonb_build_object('ok', true) else g.err end;
  end if;
  update public.tk_devices set revoked_at = now() where id = g.device_id;
  perform tkp.bump_account(g.account_id);
  return jsonb_build_object('ok', true);
end $$;

-- --------------------------------------------------------------- automatic PC tracking: the website
create or replace function public.tk_device_pair_start() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_code text; v_exp timestamptz := now() + interval '10 minutes';
begin
  if not tkp.rate_take('pairstart:' || me, 12, interval '1 hour') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  if (select count(*) from public.tk_devices d where d.account_id = me and d.revoked_at is null) >= 5 then
    return jsonb_build_object('ok', false, 'error', 'too_many_devices');
  end if;
  delete from public.tk_device_pairings where account_id = me and used_at is null;   -- one live code at a time
  loop
    v_code := tkp.random_code(8);
    begin
      insert into public.tk_device_pairings (account_id, code_hash, expires_at) values (me, tkp.code_hash('pair', v_code), v_exp);
      exit;
    exception when unique_violation then
      null;   -- astronomically unlikely; draw another code
    end;
  end loop;
  return jsonb_build_object('ok', true, 'code', substr(v_code, 1, 4) || '-' || substr(v_code, 5, 4), 'expires_at', v_exp);
end $$;

create or replace function public.tk_pc_tracking_state() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  return jsonb_build_object('ok', true,
    'tracking', tkp.tracking_mode(me),
    'season_start', tkp.season_start(),
    'auto_gap', (extract(epoch from tkp.auto_gap(me)) / 60)::integer,
    'devices', (select coalesce(jsonb_agg(jsonb_build_object('id', d.id, 'name', d.name, 'created_at', d.created_at,
                  'last_seen_at', d.last_seen_at, 'last_sync_at', d.last_sync_at, 'app_version', d.app_version,
                  'status', d.status, 'status_at', d.status_at,
                  'online', coalesce(d.last_seen_at, d.created_at) > now() - interval '12 minutes') order by d.created_at), '[]'::jsonb)
                  from public.tk_devices d where d.account_id = me and d.revoked_at is null),
    'pairing', (select jsonb_build_object('expires_at', p.expires_at) from public.tk_device_pairings p
                 where p.account_id = me and p.used_at is null and p.expires_at > now() order by p.created_at desc limit 1),
    'profiles', (select coalesce(jsonb_agg(jsonb_build_object('player_hash', p.player_hash, 'label', p.label,
                   'created_at', p.created_at, 'last_match_at', p.last_match_at) order by p.created_at), '[]'::jsonb)
                   from public.tk_replay_profiles p where p.account_id = me),
    'matches', (select count(*) from public.tk_replay_matches r where r.account_id = me and not r.excluded),
    'last_match', (select tkp.replay_json(r.id) from public.tk_replay_matches r where r.account_id = me and not r.excluded
                    order by r.ended_at desc limit 1));
end $$;

create or replace function public.tk_device_rename(p_device uuid, p_name text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v text := tkp.clean_label(p_name, 40);
begin
  if v is null then return jsonb_build_object('ok', false, 'error', 'invalid_name'); end if;
  update public.tk_devices set name = v where id = p_device and account_id = me and revoked_at is null;
  if not found then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true);
end $$;

-- Revoking a PC stops every future upload from that installation at once.
create or replace function public.tk_device_revoke(p_device uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  update public.tk_devices set revoked_at = now() where id = p_device and account_id = me and revoked_at is null;
  if not found then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_device_rescan(p_device uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  if not tkp.rate_take('rescan:' || me, 20, interval '1 hour') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  update public.tk_devices set rescan_requested_at = now() where id = p_device and account_id = me and revoked_at is null;
  if not found then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  return jsonb_build_object('ok', true);
end $$;

create or replace function public.tk_tracking_settings(p_mode text default null, p_auto_gap integer default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid;
begin
  if not tkp.rate_take('tracking:' || me, 60, interval '10 minutes') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  if p_mode is not null and p_mode not in ('manual', 'auto', 'hybrid') then return jsonb_build_object('ok', false, 'error', 'invalid_mode'); end if;
  if p_auto_gap is not null and (p_auto_gap < 5 or p_auto_gap > 240) then return jsonb_build_object('ok', false, 'error', 'invalid_gap'); end if;
  update public.tk_accounts a set settings = a.settings
      || case when p_mode is null then '{}'::jsonb else jsonb_build_object('tracking', p_mode) end
      || case when p_auto_gap is null then '{}'::jsonb else jsonb_build_object('auto_gap', p_auto_gap) end
   where a.id = me;
  v_party := tkp.active_party_of(me);
  if v_party is not null then   -- a squad game in progress follows the new choice from now on
    update public.tk_party_game_members gm set tracking = tkp.member_tracking(me)
      from public.tk_party_games g
     where g.id = gm.game_id and g.party_id = v_party and g.status = 'active' and gm.account_id = me and gm.replay_chests is null;
    perform tkp.bump_party(v_party);
  end if;
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'tracking', tkp.tracking_mode(me), 'auto_gap', (extract(epoch from tkp.auto_gap(me)) / 60)::integer);
end $$;

-- Imported matches changed since p_since (inclusive), oldest change first: the website applies them to the
-- Live Counter's history idempotently, so fetching the same change twice is harmless.
create or replace function public.tk_replay_matches(p_since timestamptz default null, p_limit integer default 300) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_limit integer := least(greatest(coalesce(p_limit, 300), 1), 500); v jsonb; v_n integer;
begin
  select coalesce(jsonb_agg(tkp.replay_json(x.id) order by x.updated_at, x.id), '[]'::jsonb), count(*)
    into v, v_n
    from (select r.id, r.updated_at from public.tk_replay_matches r
           where r.account_id = me and (p_since is null or r.updated_at >= p_since)
           order by r.updated_at, r.id limit v_limit) x;
  return jsonb_build_object('ok', true, 'matches', v, 'more', v_n >= v_limit, 'tracking', tkp.tracking_mode(me));
end $$;

-- A player's own correction of an imported count. The replay's number is kept for audit, a newer parser never
-- overwrites the correction, and p_chests = null removes it.
create or replace function public.tk_replay_correct(p_match uuid, p_chests integer, p_reason text default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); r record;
begin
  if not tkp.rate_take('rcorrect:' || me, 60, interval '10 minutes') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  if p_chests is not null and (p_chests < 0 or p_chests > 400) then return jsonb_build_object('ok', false, 'error', 'invalid_count'); end if;
  select * into r from public.tk_replay_matches x where x.id = p_match and x.account_id = me for update;
  if r.id is null then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  update public.tk_replay_matches set corrected_chests = p_chests,
         correction_reason = case when p_chests is null then null else tkp.clean_label(p_reason, 120) end,
         corrected_at = case when p_chests is null then null else now() end, updated_at = now(),
         revisions = tkp.revisions_push(revisions, jsonb_build_object('at', now(),
           'kind', case when p_chests is null then 'correction_cleared' else 'correction' end,
           'before', coalesce(r.corrected_chests, r.chests), 'after', coalesce(p_chests, r.chests), 'reason', tkp.clean_label(p_reason, 120)))
   where id = r.id;
  perform tkp.replay_attach_party(r.id);
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'match', tkp.replay_json(r.id));
end $$;

-- "Not my match": excluded matches count nowhere (personal or squad) until included again.
create or replace function public.tk_replay_exclude(p_match uuid, p_excluded boolean) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); r record; v boolean := coalesce(p_excluded, true);
begin
  if not tkp.rate_take('rcorrect:' || me, 60, interval '10 minutes') then return jsonb_build_object('ok', false, 'error', 'rate_limited'); end if;
  select * into r from public.tk_replay_matches x where x.id = p_match and x.account_id = me for update;
  if r.id is null then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  update public.tk_replay_matches set excluded = v, updated_at = now(),
         revisions = tkp.revisions_push(revisions, jsonb_build_object('at', now(), 'kind', case when v then 'excluded' else 'included' end))
   where id = r.id;
  if v then perform tkp.replay_unlink_party(r.id, null); else perform tkp.replay_attach_party(r.id); end if;
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'match', tkp.replay_json(r.id));
end $$;

-- Squad games a match could belong to (for "which squad game was this?").
create or replace function public.tk_replay_party_candidates(p_match uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); r record;
begin
  select * into r from public.tk_replay_matches x where x.id = p_match and x.account_id = me;
  if r.id is null then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  return jsonb_build_object('ok', true, 'games', coalesce((
    select jsonb_agg(jsonb_build_object('id', g.id, 'number', g.number, 'started_at', g.started_at, 'ended_at', g.ended_at,
             'status', g.status, 'linked_here', gm.replay_match_id = r.id,
             'other_replay', gm.replay_match_id is not null and gm.replay_match_id <> r.id) order by g.started_at)
      from public.tk_party_games g join public.tk_party_game_members gm on gm.game_id = g.id and gm.account_id = me
     where not g.void and g.started_at < r.ended_at + interval '30 minutes'
       and coalesce(g.ended_at, now()) > r.started_at - interval '30 minutes'), '[]'::jsonb));
end $$;

create or replace function public.tk_replay_attach(p_match uuid, p_game uuid, p_replace boolean default false) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); r record; gm record; v_party uuid; v_void boolean;
begin
  select * into r from public.tk_replay_matches x where x.id = p_match and x.account_id = me for update;
  if r.id is null then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  if r.excluded or r.historical then return jsonb_build_object('ok', false, 'error', 'not_linkable'); end if;
  select g.party_id, g.void into v_party, v_void from public.tk_party_games g where g.id = p_game;
  select * into gm from public.tk_party_game_members m where m.game_id = p_game and m.account_id = me for update;
  if gm.id is null or v_void then return jsonb_build_object('ok', false, 'error', 'not_participant'); end if;
  if gm.replay_match_id is not null and gm.replay_match_id <> r.id then
    if not coalesce(p_replace, false) then return jsonb_build_object('ok', false, 'error', 'slot_taken'); end if;
    perform tkp.replay_unlink_party(gm.replay_match_id, 'conflict');
  end if;
  if r.party_game_id is not null and r.party_game_id <> p_game then perform tkp.replay_unlink_party(r.id, null); end if;
  update public.tk_party_game_members set replay_match_id = r.id, replay_chests = coalesce(r.corrected_chests, r.chests) where id = gm.id;
  update public.tk_replay_matches set party_game_id = p_game, party_link = 'confirmed', updated_at = now() where id = r.id;
  update public.tk_party_games set match_session_id = coalesce(match_session_id, r.session_id) where id = p_game;
  insert into public.tk_corrections (account_id, game_id, kind, detail) values (me, p_game, 'replay_confirmed', jsonb_build_object('match', r.id));
  perform tkp.bump_party(v_party);
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'match', tkp.replay_json(r.id));
end $$;

create or replace function public.tk_replay_detach(p_match uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  if not exists (select 1 from public.tk_replay_matches x where x.id = p_match and x.account_id = me) then
    return jsonb_build_object('ok', false, 'error', 'not_found');
  end if;
  perform tkp.replay_unlink_party(p_match, 'detached');
  update public.tk_replay_matches set updated_at = now() where id = p_match;
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true, 'match', tkp.replay_json(p_match));
end $$;

create or replace function public.tk_replay_profile_unlink(p_player_hash text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account();
begin
  delete from public.tk_replay_profiles where player_hash = p_player_hash and account_id = me;
  if not found then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  perform tkp.bump_account(me);
  return jsonb_build_object('ok', true);
end $$;

-- A PC player whose replay never arrived can count that squad game with what they counted live instead.
create or replace function public.tk_party_use_live_count(p_game uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare me uuid := tkp.require_account(); v_party uuid;
begin
  update public.tk_party_game_members set tracking = 'live' where game_id = p_game and account_id = me and replay_chests is null;
  if not found then return jsonb_build_object('ok', false, 'error', 'not_found'); end if;
  select g.party_id into v_party from public.tk_party_games g where g.id = p_game;
  insert into public.tk_corrections (account_id, game_id, kind, detail) values (me, p_game, 'use_live_count', '{}'::jsonb);
  perform tkp.bump_party(v_party);
  return jsonb_build_object('ok', true, 'game', tkp.game_json(p_game, me));
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
-- TOKENIFY Companion authenticates with its own device secret, checked inside each function:
grant execute on function public.tk_device_pair(text, text, text) to anon;
grant execute on function public.tk_device_hello(uuid, text, jsonb, text) to anon;
grant execute on function public.tk_device_profile_claim(uuid, text, text, text) to anon;
grant execute on function public.tk_device_import(uuid, text, jsonb) to anon;
grant execute on function public.tk_device_matches(uuid, text, integer) to anon;
grant execute on function public.tk_device_correct(uuid, text, text, text, integer, boolean, text) to anon;
grant execute on function public.tk_device_disconnect(uuid, text) to anon;

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
       (select count(*) from public.tk_devices where revoked_at is null) as linked_pcs,
       (select count(*) from auth.users u where not coalesce(u.is_anonymous, false)) as v1_email_users,
       to_regclass('public.tracker_state') is not null as v1_table_kept;
