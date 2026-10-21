-- Minimal stand-in for the parts of a Supabase database that schema.sql depends on,
-- so the schema can be tested against a plain local PostgreSQL.
do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'anon') then
    create role anon nologin;
    create role authenticated nologin;
    create role service_role nologin bypassrls;
    create role supabase_auth_admin nologin;
  end if;
end $$;

create schema auth;
create table auth.users (id uuid primary key default gen_random_uuid(), email text);
-- Tests impersonate a player with: select set_config('request.jwt.claim.sub', '<uuid>', false);
create function auth.uid() returns uuid language sql stable as $$
  select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid
$$;
grant usage on schema auth to anon, authenticated, supabase_auth_admin;
grant all on auth.users to supabase_auth_admin;
grant execute on function auth.uid() to anon, authenticated;

-- Supabase's default privileges, which schema.sql must explicitly take back.
grant usage on schema public to anon, authenticated, service_role;
alter default privileges in schema public grant all on tables to anon, authenticated, service_role;
alter default privileges in schema public grant all on functions to anon, authenticated, service_role;

set client_min_messages = error;  -- silences the wal_level notice on local clusters
create publication supabase_realtime;
reset client_min_messages;
