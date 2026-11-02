-- End-to-end checks for the RPC layer. Run via db/tests/run_local.sh.
\set ON_ERROR_STOP 1
\pset pager off

-- Assert helper: fails the script with a readable message.
create function pg_temp.check(ok boolean, what text) returns void
language plpgsql as $$
begin
  if ok is not true then
    raise exception 'FAIL: %', what;
  end if;
  raise notice 'ok   %', what;
end $$;

-- Expect a statement to fail with a given SQLSTATE.
create function pg_temp.expect_error(stmt text, sqlstate_expected text, what text) returns void
language plpgsql as $$
begin
  execute stmt;
  raise exception 'FAIL: % (no error raised)', what;
exception
  when others then
    if sqlstate = sqlstate_expected then
      raise notice 'ok   %', what;
    else
      raise exception 'FAIL: % (got % %)', what, sqlstate, sqlerrm;
    end if;
end $$;

-- Heartbeat batch builder: n consecutive minutes ending at now() - p_offset_min.
create function pg_temp.hb(p_sid text, p_user text, p_n int, p_offset_min int,
                           p_state text default 'active', p_exe text default 'javaw.exe')
returns jsonb language sql as $$
  select jsonb_agg(jsonb_build_object(
           'ts', date_trunc('minute', now()) - make_interval(mins => p_offset_min + i),
           'local_date', current_date,
           'state', p_state, 'sid', p_sid, 'user', p_user,
           'exe', p_exe, 'product', 'Java(TM) Platform SE', 'title', 'Minecraft 1.21'))
  from generate_series(0, p_n - 1) i
$$;

grant execute on all functions in schema pg_temp to anon, authenticated;

-- ---------------------------------------------------------------------
-- Fixtures (as superuser)
-- ---------------------------------------------------------------------
insert into auth.users values
  ('00000000-0000-0000-0000-00000000000a', 'parent@example.com'),
  ('00000000-0000-0000-0000-00000000000b', 'stranger@example.com');
insert into app_admins values ('00000000-0000-0000-0000-00000000000a');

insert into children (id, name, sort_order) values
  ('11111111-1111-1111-1111-111111111111', 'Миша', 1),
  ('22222222-2222-2222-2222-222222222222', 'Аня',  2);

insert into schedule_weekly (child_id, weekday, limit_minutes, window_from, window_to)
select '11111111-1111-1111-1111-111111111111', d, 120, '15:00', '20:00'
from generate_series(1, 7) d;

-- ---------------------------------------------------------------------
-- Admin: RLS and device creation
-- ---------------------------------------------------------------------
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000b';
select pg_temp.check((select count(*) from children) = 0, 'non-admin sees no children');
select pg_temp.expect_error($$select admin_create_device('x')$$, '42501', 'non-admin cannot create device');

set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
select pg_temp.check((select count(*) from children) = 2, 'admin sees children');

select device_id as dev1, code as code1 from admin_create_device('Комп Миши') \gset
select device_id as dev2, code as code2 from admin_create_device('Ноутбук') \gset
select pg_temp.check(:'code1' ~ '^[A-Z2-9]{4}-[A-Z2-9]{4}$', 'code format XXXX-XXXX');

-- ---------------------------------------------------------------------
-- Device registration (anon)
-- ---------------------------------------------------------------------
reset request.jwt.claim.sub;
set role anon;
select pg_temp.expect_error($$select count(*) from children$$, '42501', 'anon has no table access');
select pg_temp.expect_error($$select * from device_register('AAAA-AAAA','pc','1')$$, '28000', 'wrong code rejected');

-- lowercase and no dash must still work
select device_id as reg1, device_secret as sec1
  from device_register(lower(replace(:'code1', '-', '')), 'MISHA-PC', '0.1.0') \gset
select pg_temp.check(:'reg1' = :'dev1', 'register returns device id');
select pg_temp.expect_error(format($$select * from device_register(%L,'pc','1')$$, :'code1'),
                            '28000', 'code is single-use');
select device_id as reg2, device_secret as sec2 from device_register(:'code2', 'LAPTOP', '0.1.0') \gset

-- ---------------------------------------------------------------------
-- Sync
-- ---------------------------------------------------------------------
select pg_temp.expect_error(
  format($$select device_sync(%L,'bad','0.1.0',now(),current_date,'[]')$$, :'dev1'),
  '28000', 'bad secret rejected');

-- 3 minutes from an unknown account
select device_sync(:'dev1', :'sec1', '0.1.0', now(), current_date,
                   pg_temp.hb('S-1-5-21-misha', 'misha', 3, 10)) as r \gset
select pg_temp.check((:'r'::jsonb->>'accepted')::int = 3, 'first batch accepted');
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'accounts') = 1
                     and :'r'::jsonb->'accounts'->0->>'child_id' is null, 'new account appears unbound');
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'children') = 0, 'no children bound yet');

-- Garbage items are dropped, not fatal
select device_sync(:'dev1', :'sec1', '0.1.0', now(), current_date,
                   '[{"ts":"garbage"},{"ts":"2026-01-01T00:00:00Z","local_date":"2026-01-01","state":"weird"}]') as r \gset
select pg_temp.check((:'r'::jsonb->>'accepted')::int = 0, 'malformed heartbeats ignored');

-- Parent binds the account to Misha
reset role;
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
update device_accounts set child_id = '11111111-1111-1111-1111-111111111111'
 where windows_sid = 'S-1-5-21-misha';
insert into bonuses (child_id, local_date, minutes, reason)
values ('11111111-1111-1111-1111-111111111111', current_date, 30, 'уроки сделаны');

-- Resend 2 old minutes + 4 new (one of them locked)
reset request.jwt.claim.sub;
set role anon;
select device_sync(:'dev1', :'sec1', '0.1.1', now() + interval '7 minutes', current_date,
         pg_temp.hb('S-1-5-21-misha', 'misha', 2, 10)
      || pg_temp.hb('S-1-5-21-misha', 'misha', 3, 3)
      || pg_temp.hb('S-1-5-21-misha', 'misha', 1, 2, 'locked')) as r \gset
select pg_temp.check((:'r'::jsonb->>'accepted')::int = 4, 'resend is idempotent');
select pg_temp.check(:'r'::jsonb#>>'{children,0,name}' = 'Миша', 'child is returned');
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,0,limit_minutes}')::int = 120, 'weekly limit');
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,0,bonus_minutes}')::int = 30, 'bonus applied');
select pg_temp.check(:'r'::jsonb#>>'{children,0,days,0,window_from}' = '15:00', 'window from');
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,0,spent_minutes}')::int = 3,
                     'spent counts bound active minutes only');
select pg_temp.check(jsonb_array_length(:'r'::jsonb#>'{children,0,days}') = 2, 'today + tomorrow');

-- Second PC, same child, one overlapping minute + one new
reset role;
insert into device_accounts (device_id, windows_sid, windows_user, child_id)
values (:'dev2', 'S-1-5-21-misha2', 'misha', '11111111-1111-1111-1111-111111111111');
set role anon;
select device_sync(:'dev2', :'sec2', '0.1.1', now(), current_date,
         pg_temp.hb('S-1-5-21-misha2', 'misha', 2, 2)) as r \gset
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,0,spent_minutes}')::int = 4,
                     'limit shared across PCs, overlap counted once');

-- ---------------------------------------------------------------------
-- Commands
-- ---------------------------------------------------------------------
reset role;
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
insert into commands (child_id, kind, lock_from, lock_until)
values ('11111111-1111-1111-1111-111111111111', 'lock', now(), now() + interval '1 hour')
returning id as lock_id \gset
insert into commands (child_id, kind, message)
values ('11111111-1111-1111-1111-111111111111', 'message', 'Ужинать!')
returning id as msg_id \gset
-- Anya's lock must not reach Misha's PC
insert into commands (child_id, kind, lock_from)
values ('22222222-2222-2222-2222-222222222222', 'lock', now());

reset request.jwt.claim.sub;
set role anon;
select device_sync(:'dev1', :'sec1', '0.1.1', now(), current_date, '[]') as r \gset
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'locks') = 1, 'only own child lock delivered');
select pg_temp.check(:'r'::jsonb#>>'{messages,0,text}' = 'Ужинать!', 'message delivered');

select device_sync(:'dev1', :'sec1', '0.1.1', now(), current_date, '[]',
                   array[:'msg_id'::bigint]) as r \gset
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'messages') = 0, 'acked message not repeated');
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'locks') = 1, 'lock stays while active');

-- Message still pending for the second PC
select device_sync(:'dev2', :'sec2', '0.1.1', now(), current_date, '[]') as r \gset
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'messages') = 1, 'message per PC');

reset role;
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
update commands set cancelled_at = now() where id = :lock_id;
reset request.jwt.claim.sub;
set role anon;
select device_sync(:'dev1', :'sec1', '0.1.1', now(), current_date, '[]') as r \gset
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'locks') = 0, 'cancelled lock gone');

-- ---------------------------------------------------------------------
-- Overrides, dashboard, disable, reset
-- ---------------------------------------------------------------------
-- PC 2 reports a clock 7 minutes ahead
select device_sync(:'dev2', :'sec2', '0.1.1', now() + interval '7 minutes', current_date, '[]') as r \gset

reset role;
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
insert into schedule_overrides (child_id, local_date, limit_minutes)
values ('11111111-1111-1111-1111-111111111111', current_date + 1, 240);

select admin_dashboard(current_date) as dash \gset
select pg_temp.check(jsonb_array_length(:'dash'::jsonb->'children') = 2, 'dashboard children');
select pg_temp.check((:'dash'::jsonb#>>'{children,0,spent_minutes}')::int = 4, 'dashboard spent');
select pg_temp.check(:'dash'::jsonb#>>'{children,0,now,exe}' = 'javaw.exe', 'dashboard shows current app');
select pg_temp.check(jsonb_array_length(:'dash'::jsonb->'devices') = 2, 'dashboard devices');
select pg_temp.check(
  (select bool_and(((d->'flags'->>'clock_skew')::boolean) = (d->>'id' = :'dev2'))
     from jsonb_array_elements(:'dash'::jsonb->'devices') d),
  'clock skew flagged only for PC 2 (7 min)');

reset request.jwt.claim.sub;
set role anon;
select device_sync(:'dev1', :'sec1', '0.1.1', now(), current_date, '[]') as r \gset
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,1,limit_minutes}')::int = 240,
                     'override wins for tomorrow');
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,1,window_from}') = '15:00',
                     'override inherits window');

reset role;
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
update devices set is_enabled = false where id = :'dev2';
select admin_reset_device_code(:'dev1') as code1b \gset

reset request.jwt.claim.sub;
set role anon;
select device_sync(:'dev2', :'sec2', '0.1.1', now(), current_date, '[]') as r \gset
select pg_temp.check((:'r'::jsonb->>'device_enabled')::boolean = false, 'disabled device told to stand down');
select pg_temp.expect_error(
  format($$select device_sync(%L,%L,'0.1.1',now(),current_date,'[]')$$, :'dev1', :'sec1'),
  '28000', 'old secret invalid after reset');
select device_id as reg1b, device_secret as sec1b from device_register(:'code1b', 'MISHA-PC', '0.2.0') \gset
select pg_temp.check(:'reg1b' = :'dev1', 're-registration keeps device id');

-- ---------------------------------------------------------------------
-- Time requests
-- ---------------------------------------------------------------------
select device_sync(:'dev1', :'sec1b', '0.2.0', now(), current_date, '[]', '{}',
  '[{"client_id":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","sid":"S-1-5-21-misha","minutes":30,"reason":"доиграть"}]') as r \gset
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'requests') = 1
                     and :'r'::jsonb#>>'{requests,0,status}' = 'pending', 'request created as pending');

-- resend + a second request while one is pending + unknown sid + garbage
select device_sync(:'dev1', :'sec1b', '0.2.0', now(), current_date, '[]', '{}',
  '[{"client_id":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","sid":"S-1-5-21-misha","minutes":30},
    {"client_id":"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb","sid":"S-1-5-21-misha","minutes":60},
    {"client_id":"cccccccc-cccc-4ccc-8ccc-cccccccccccc","sid":"S-unknown","minutes":60},
    {"client_id":"not-a-uuid","sid":"S-1-5-21-misha","minutes":"x"}]') as r \gset
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'requests') = 1, 'one pending per child, resend idempotent, junk ignored');
select (:'r'::jsonb#>>'{requests,0,id}')::bigint as req1 \gset

reset role;
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000b';
select pg_temp.expect_error(format('select admin_decide_request(%s, true)', :req1), '42501', 'non-admin cannot decide');
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
select admin_decide_request(:req1, true, 20, current_date);
select pg_temp.expect_error(format('select admin_decide_request(%s, false)', :req1), '22023', 'cannot decide twice');

reset request.jwt.claim.sub;
set role anon;
select device_sync(:'dev1', :'sec1b', '0.2.0', now(), current_date, '[]') as r \gset
select pg_temp.check(:'r'::jsonb#>>'{requests,0,status}' = 'approved'
                     and (:'r'::jsonb#>>'{requests,0,granted_minutes}')::int = 20, 'approval visible to PC');
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,0,bonus_minutes}')::int = 50, 'approval adds bonus (30 + 20)');

-- new request after approval, then deny
select device_sync(:'dev1', :'sec1b', '0.2.0', now(), current_date, '[]', '{}',
  '[{"client_id":"dddddddd-dddd-4ddd-8ddd-dddddddddddd","sid":"S-1-5-21-misha","minutes":15}]') as r \gset
select pg_temp.check(jsonb_array_length(:'r'::jsonb->'requests') = 2, 'second request allowed after decision');
reset role;
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
select admin_decide_request(id, false) from time_requests where client_id = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
select pg_temp.check((select status from time_requests where client_id = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd') = 'denied', 'deny');
select pg_temp.check((select sum(minutes) from bonuses where local_date = current_date) = 50, 'deny adds no bonus');
reset request.jwt.claim.sub;

-- ---------------------------------------------------------------------
-- Locked by default (005)
-- ---------------------------------------------------------------------
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
select pg_temp.check((select not bool_or(locked_by_default) from children), 'locked by default is off initially');
update children set locked_by_default = true;
reset request.jwt.claim.sub;

set role anon;
select device_sync(:'dev1', :'sec1b', '0.3.0', now(), current_date, '[]') as r \gset
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,0,requires_unlock}')::boolean
                     and not (:'r'::jsonb#>>'{children,0,days,0,unlocked}')::boolean, 'today requires unlock, not unlocked');
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,1,requires_unlock}')::boolean
                     and not (:'r'::jsonb#>>'{children,0,days,1,unlocked}')::boolean, 'tomorrow locked again until opened');

select device_sync(:'dev1', :'sec1b', '0.3.0', now(), current_date, '[]', '{}',
  '[{"client_id":"eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee","sid":"S-1-5-21-misha","kind":"unlock","reason":"уроки сделал"}]') as r \gset
select pg_temp.check(:'r'::jsonb#>>'{requests,-1,kind}' = 'unlock'
                     and :'r'::jsonb#>>'{requests,-1,status}' = 'pending', 'unlock request created');

reset role;
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
select admin_decide_request(id, true, null, current_date) from time_requests where client_id = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
select pg_temp.check((select count(*) from day_unlocks where local_date = current_date) = 1, 'approval opens access');
select pg_temp.check((select sum(minutes) from bonuses where local_date = current_date) = 50, 'unlock adds no bonus');
select admin_dashboard(current_date) as dash \gset
select pg_temp.check((:'dash'::jsonb#>>'{children,0,unlocked}')::boolean, 'dashboard shows unlocked');
reset request.jwt.claim.sub;

set role anon;
select device_sync(:'dev1', :'sec1b', '0.3.0', now(), current_date, '[]') as r \gset
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,0,unlocked}')::boolean, 'PC sees access opened');
select pg_temp.check(:'r'::jsonb#>>'{requests,-1,status}' = 'approved', 'unlock request approved');
select pg_temp.expect_error($$update parent_control.children set locked_by_default = false$$, '42501', 'PC cannot change the flag');

-- ---------------------------------------------------------------------
-- Per-child locked by default (006)
-- ---------------------------------------------------------------------
reset role;
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
update children set locked_by_default = false where name = 'Миша';
update settings set locked_by_default = true;  -- no longer read
reset request.jwt.claim.sub;
set role anon;
select device_sync(:'dev1', :'sec1b', '0.4.0', now(), current_date, '[]') as r \gset
select pg_temp.check(not (:'r'::jsonb#>>'{children,0,days,1,requires_unlock}')::boolean, 'child flag off: no unlock needed');
reset role;
update children set locked_by_default = true where name = 'Миша';
set role anon;
select device_sync(:'dev1', :'sec1b', '0.4.0', now(), current_date, '[]') as r \gset
select pg_temp.check((:'r'::jsonb#>>'{children,0,days,1,requires_unlock}')::boolean, 'child flag on: unlock needed');

-- ---------------------------------------------------------------------
-- Tamper events (007)
-- ---------------------------------------------------------------------
reset role;
set role anon;
select pg_temp.expect_error(format($$select device_report(%L, 'bad', '[]')$$, :'dev1'), '28000', 'report needs the secret');

-- notify-only event does not lock
select device_report(:'dev1', :'sec1b',
  '[{"client_id":"f0000000-0000-4000-8000-000000000001","occurred_at":"2026-11-02T10:00:00Z","type":"tray_unresponsive","user":"misha"}]') as r \gset
select pg_temp.check((:'r'::jsonb->>'accepted')::int = 1 and not (:'r'::jsonb->>'tamper_locked')::boolean, 'non-lock event accepted, no lock');

-- lock event (+ resend + junk)
select device_report(:'dev1', :'sec1b',
  '[{"client_id":"f0000000-0000-4000-8000-000000000002","type":"config_restored","details":{"file":"device.json"}},
    {"client_id":"f0000000-0000-4000-8000-000000000001","type":"tray_unresponsive"},
    {"client_id":"bad","type":"acl_fixed"},
    {"client_id":"f0000000-0000-4000-8000-000000000003","type":"DROP TABLE"}]') as r \gset
select pg_temp.check((:'r'::jsonb->>'accepted')::int = 1 and (:'r'::jsonb->>'tamper_locked')::boolean, 'lock event locks, junk/resend ignored');

select device_sync(:'dev1', :'sec1b', '1.1.0', now(), current_date, '[]') as r \gset
select pg_temp.check((:'r'::jsonb->>'tamper_locked')::boolean and :'r'::jsonb->>'tamper_action' = 'lock', 'sync reports tamper lock');

-- beacon: unverified, rate limited, flags config lost
select device_beacon('MISHA-PC', 'device.json and registry missing', :'dev1');
select device_beacon('MISHA-PC', 'again', :'dev1');
reset role;
select pg_temp.check((select count(*) from tamper_events where not verified) = 1, 'beacon rate limited');
select pg_temp.check((select config_state from devices where id = :'dev1') = 'lost', 'beacon marks config lost');
set role anon;
select device_sync(:'dev1', :'sec1b', '1.1.0', now(), current_date, '[]') as r \gset
reset role;
select pg_temp.check((select config_state from devices where id = :'dev1') = 'ok', 'authenticated sync clears lost');

-- admin
set role authenticated;
set request.jwt.claim.sub = '00000000-0000-0000-0000-00000000000a';
select admin_dashboard(current_date) as dash \gset
select pg_temp.check((select bool_or((d->>'tamper_unhandled')::int = 3 and d->>'tamper_locked_since' is not null)
                        from jsonb_array_elements(:'dash'::jsonb->'devices') d), 'dashboard shows device tamper state');
select pg_temp.check((:'dash'::jsonb#>>'{children,0,tamper_locked}')::boolean, 'dashboard flags child');
select admin_mark_tamper_handled(array(select id from tamper_events));
select admin_clear_tamper_lock(:'dev1');
select pg_temp.check((select count(*) from tamper_events where handled_at is null) = 0, 'events handled');
update settings set tamper_action = 'notify';
reset request.jwt.claim.sub;
set role anon;
select device_report(:'dev1', :'sec1b',
  '[{"client_id":"f0000000-0000-4000-8000-000000000004","type":"acl_fixed"}]') as r \gset
select pg_temp.check(not (:'r'::jsonb->>'tamper_locked')::boolean, 'notify mode does not lock');
select pg_temp.expect_error($$select count(*) from parent_control.tamper_events$$, '42501', 'anon cannot read events');

-- local clear (008): applied in order, a later lock event wins
reset role;
update parent_control.settings set tamper_action = 'lock';
set role anon;
select device_report(:'dev1', :'sec1b',
  '[{"client_id":"f0000000-0000-4000-8000-000000000005","type":"clock_changed"},
    {"client_id":"f0000000-0000-4000-8000-000000000006","type":"tamper_cleared_locally","user":"papa"}]') as r \gset
select pg_temp.check(not (:'r'::jsonb->>'tamper_locked')::boolean, 'local clear lifts the lock');
select device_report(:'dev1', :'sec1b',
  '[{"client_id":"f0000000-0000-4000-8000-000000000007","type":"tamper_cleared_locally"},
    {"client_id":"f0000000-0000-4000-8000-000000000008","type":"data_tampered"}]') as r \gset
select pg_temp.check((:'r'::jsonb->>'tamper_locked')::boolean, 'lock after clear wins');

reset role;
select pg_temp.check(true, 'ALL TESTS PASSED');
