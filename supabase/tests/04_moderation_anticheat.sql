\set ON_ERROR_STOP 1
-- Moderation, live-ops grants, and multiplier-timing detection. Runs after 01-03.

-- ---------- names & chat ----------
set request.jwt.claim.sub = '33333333-3333-3333-3333-333333333333';
set role authenticated;
do $$ declare s jsonb; g jsonb; begin
  s := public.set_display_name('Captain Ada-7');
  assert s->>'display_name' = 'Captain Ada-7', s::text;
  begin perform public.set_display_name('ab'); raise exception 'too short';
  exception when others then assert sqlerrm = 'invalid_display_name', sqlerrm; end;
  begin perform public.set_display_name('<script>'); raise exception 'bad chars';
  exception when others then assert sqlerrm = 'invalid_display_name', sqlerrm; end;
  begin perform public.set_display_name('The A.d m-i n'); raise exception 'impersonation';
  exception when others then assert sqlerrm = 'name_not_allowed', sqlerrm; end;
  perform public.set_display_name('Zoë Ångström');  -- non-ASCII letters are fine
  begin perform public.create_guild('Official Fleet', 'OFF'); raise exception 'guild impersonation';
  exception when others then assert sqlerrm = 'name_not_allowed', sqlerrm; end;
  g := public.create_guild('Cloud Wardens', 'CW');
  assert jsonb_array_length(g->'members') = 1;
  assert (public.send_guild_message('ask the ADMIN team')->>'body') = 'ask the ***** team', 'chat masks terms';
  perform public.leave_guild();
end $$;
reset role;

-- ---------- live-ops grant ----------
set role authenticated;
do $$ begin
  begin perform public.admin_grant('me', '33333333-3333-3333-3333-333333333333', 100, 0, 0, 'x'); raise exception 'players cannot grant';
  exception when insufficient_privilege then null; end;
end $$;
reset role;
set role service_role;
do $$ declare s jsonb; r0 int; c0 numeric; begin
  select rolls into r0 from public.inventory_rolls where user_id = '33333333-3333-3333-3333-333333333333';
  select crystals into c0 from public.profiles where id = '33333333-3333-3333-3333-333333333333';
  s := public.admin_grant('ops@studio', '33333333-3333-3333-3333-333333333333', 250, 3, 1000, 'Outage 2026-10-20');
  assert (s->>'rolls')::int = r0 + 250 and (s->>'crystals')::numeric = c0 + 1000, s::text;
  begin perform public.admin_grant('ops@studio', '33333333-3333-3333-3333-333333333333', 999999, 0, 0, 'typo'); raise exception 'bounds';
  exception when others then assert sqlerrm = 'grant_out_of_bounds', sqlerrm; end;
  begin perform public.admin_grant('ops@studio', '33333333-3333-3333-3333-333333333333', 1, 0, 0, ' '); raise exception 'reason';
  exception when others then assert sqlerrm = 'actor_and_reason_required', sqlerrm; end;
  begin perform public.admin_grant('ops@studio', gen_random_uuid(), 1, 0, 0, 'ghost'); raise exception 'missing';
  exception when others then assert sqlerrm = 'player_not_found', sqlerrm; end;
end $$;
reset role;
do $$ begin
  assert (select count(*) from public.admin_actions where action = 'grant' and actor = 'ops@studio') = 1, 'audited once';
end $$;

-- ---------- multiplier-timing detection ----------
-- Cheater (Alpha) peeks at each upcoming outcome and uses 10x only on non-Crystal rolls.
-- Honest (Bravo) picks 1x or 10x at random. Both get a big roll budget.
update public.inventory_rolls set rolls = 1000000
 where user_id in ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');
delete from public.roll_audit;

create temp table batches (user_id uuid, i bigint, m int);
grant select on batches to authenticated;
do $$
declare
  inv public.inventory_rolls%rowtype;
  streak int;
  o int;
  m int;
begin
  for inv in select * from public.inventory_rolls
              where user_id in ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222') loop
    streak := inv.dry_streak;
    for k in 0..1499 loop
      o := game.pick_outcome(inv.rng_seed, inv.roll_index + k, streak >= 15);
      if inv.user_id = '11111111-1111-1111-1111-111111111111' then
        m := case when o <> 0 then 10 else 1 end;  -- outcome-peeking
      else
        m := case when random() < 0.5 then 10 else 1 end;  -- honest
      end if;
      insert into batches values (inv.user_id, inv.roll_index + k, m);
      streak := case when o in (1, 2) then 0 else streak + 1 end;
    end loop;
  end loop;
end $$;

create function pg_temp.upload(p_user uuid) returns void language plpgsql as $$
declare t bigint := floor(extract(epoch from now()) * 1000); b jsonb; s jsonb;
begin
  perform set_config('request.jwt.claim.sub', p_user::text, false);
  for chunk in 0..2 loop
    select jsonb_agg(jsonb_build_object('i', i, 'm', m, 't', t) order by i) into b
      from (select * from batches where user_id = p_user order by i offset chunk * 500 limit 500) x;
    s := public.sync_rolls(b);
    assert (s->>'accepted')::int = 500 and s->>'rejected_reason' is null, s::text;
  end loop;
end $$;
set role authenticated;
do $$ begin perform pg_temp.upload('11111111-1111-1111-1111-111111111111'); end $$;
do $$ begin perform pg_temp.upload('22222222-2222-2222-2222-222222222222'); end $$;
reset role;

set role service_role;
do $$ declare flagged jsonb; everyone jsonb; begin
  flagged := public.admin_multiplier_anomalies(200, 4);
  everyone := public.admin_multiplier_anomalies(200, -1000);
  raise notice 'anomaly report (all players): %', everyone;
  assert jsonb_array_length(flagged) = 1, 'exactly one player flagged: ' || flagged::text;
  assert flagged->0->>'user_id' = '11111111-1111-1111-1111-111111111111', 'the cheater';
  assert (flagged->0->>'hi_premium_rate')::numeric = 1, 'cheater only ever wins at 10x';
end $$;
reset role;
set role authenticated;
do $$ begin
  begin perform 1 from public.roll_audit; raise exception 'audit hidden from players';
  exception when insufficient_privilege then null; end;
  perform public.set_display_name('Back\slash');
  raise exception 'backslash should be rejected';
exception when others then
  assert sqlerrm = 'invalid_display_name', sqlerrm;
end $$;
reset role;
\echo PASS 04_moderation_anticheat
