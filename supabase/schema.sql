-- TOKENIFY — optional cloud sync for the Live Tracker (Supabase / Postgres).
-- Run this once in your Supabase project's SQL editor. Then put the project URL and the
-- *anon public* key into js/config.js (never the service_role key).
--
-- Security model
--   * Supabase Auth stores credentials (bcrypt); TOKENIFY never sees or stores passwords.
--   * Row-level security: a user can only ever read their own row.
--   * Clients cannot write the table directly. All writes go through push_tracker_state(),
--     which validates the payload and applies optimistic concurrency (expected_version), so a
--     retried or stale write can never silently overwrite newer data.

create table if not exists public.tracker_state (
  user_id    uuid primary key references auth.users (id) on delete cascade,
  state      jsonb       not null,
  version    bigint      not null default 1,
  device_id  text,
  updated_at timestamptz not null default now()
);

alter table public.tracker_state enable row level security;

-- Explicit, minimal privileges (works whether or not "Automatically expose new tables" is on):
-- signed-in users may only SELECT (and RLS limits that to their own row); anonymous visitors get nothing.
revoke all on public.tracker_state from anon, authenticated;
grant usage on schema public to authenticated;
grant select on public.tracker_state to authenticated;

drop policy if exists "read own tracker state" on public.tracker_state;
create policy "read own tracker state" on public.tracker_state
  for select using (auth.uid() = user_id);
-- (no insert/update/delete policies: writes only via the function below)

create or replace function public.push_tracker_state(expected_version bigint, new_state jsonb, device text default null)
returns table (ok boolean, version bigint, state jsonb)
language plpgsql
security definer
set search_path = public
as $$
declare
  uid uuid := auth.uid();
  cur record;
begin
  if uid is null then
    raise exception 'not authenticated' using errcode = '28000';
  end if;
  if new_state is null or jsonb_typeof(new_state) <> 'object' or (new_state ->> 'v') is distinct from '1'
     or jsonb_typeof(new_state -> 'sessions') <> 'object' or jsonb_typeof(new_state -> 'games') <> 'object'
     or jsonb_typeof(new_state -> 'gameOrder') <> 'array' or jsonb_typeof(new_state -> 'sessionOrder') <> 'array' then
    raise exception 'invalid tracker state' using errcode = '22023';
  end if;
  if octet_length(new_state::text) > 6000000 then
    raise exception 'tracker state too large' using errcode = '54000';
  end if;

  select t.version, t.state into cur from public.tracker_state t where t.user_id = uid for update;

  if not found then
    if expected_version <> 0 then
      return query select false, 0::bigint, null::jsonb;
      return;
    end if;
    insert into public.tracker_state (user_id, state, version, device_id) values (uid, new_state, 1, left(device, 80));
    return query select true, 1::bigint, null::jsonb;
    return;
  end if;

  if cur.version <> expected_version then
    -- caller is behind: hand back the newest copy so it can merge and retry
    return query select false, cur.version, cur.state;
    return;
  end if;

  update public.tracker_state
     set state = new_state, version = cur.version + 1, device_id = left(device, 80), updated_at = now()
   where user_id = uid;
  return query select true, cur.version + 1, null::jsonb;
end;
$$;

revoke all on function public.push_tracker_state(bigint, jsonb, text) from public, anon;
grant execute on function public.push_tracker_state(bigint, jsonb, text) to authenticated;

-- Self-service account deletion (removes the auth user; the tracker row cascades).
create or replace function public.delete_my_account()
returns void
language plpgsql
security definer
set search_path = public, auth
as $$
begin
  if auth.uid() is null then
    raise exception 'not authenticated' using errcode = '28000';
  end if;
  delete from auth.users where id = auth.uid();
end;
$$;

revoke all on function public.delete_my_account() from public, anon;
grant execute on function public.delete_my_account() to authenticated;
