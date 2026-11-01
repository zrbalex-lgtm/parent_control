-- Minimal stand-in for the Supabase environment, for local tests only.
-- Never run this against a real Supabase project.

-- Roles are cluster-wide, so they may survive from a previous run.
do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'anon') then
    create role anon nologin;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'authenticated') then
    create role authenticated nologin;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'service_role') then
    create role service_role nologin;
  end if;
end $$;

create schema extensions;
create extension pgcrypto schema extensions;
alter database pc_test set search_path = parent_control, public, extensions;

create schema auth;
create table auth.users (
  id    uuid primary key,
  email text
);

-- Supabase reads the user id from the JWT; tests set it via a GUC.
create function auth.uid() returns uuid
language sql stable
as $$ select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $$;

grant usage on schema auth, extensions, public to anon, authenticated;
grant execute on function auth.uid() to anon, authenticated;

