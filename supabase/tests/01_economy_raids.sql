\set ON_ERROR_STOP 1
-- Economy, RLS, upgrades, raids, revenge, cards. Players: Alpha=1111, Bravo=2222, Charlie=3333.

-- users created by the auth role (exercises trigger privileges)
set role supabase_auth_admin;
insert into auth.users (id) values ('11111111-1111-1111-1111-111111111111'),('22222222-2222-2222-2222-222222222222'),('33333333-3333-3333-3333-333333333333');
reset role;
update public.profiles set display_name = 'Alpha' where id = '11111111-1111-1111-1111-111111111111';
update public.profiles set display_name = 'Bravo' where id = '22222222-2222-2222-2222-222222222222';
update public.profiles set last_active_at = now() - interval '30 days' where id = '33333333-3333-3333-3333-333333333333';
update public.island_states set island_level = 60 where user_id = '33333333-3333-3333-3333-333333333333';

-- ---------- RLS ----------
set request.jwt.claim.sub = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ begin
  assert (select count(*) from public.profiles) = 1, 'sees only own profile';
  begin update public.profiles set crystals = 999; raise exception 'should fail';
  exception when insufficient_privilege then null; end;
  begin update public.profiles set display_name = 'Direct'; raise exception 'renames go through set_display_name';
  exception when insufficient_privilege then null; end;
  begin update public.inventory_rolls set rolls = 999; raise exception 'should fail';
  exception when insufficient_privilege then null; end;
  perform public.set_display_name('  Alpha   Prime ');
  assert (select display_name from public.profiles) = 'Alpha Prime', 'whitespace collapsed';
  begin perform game.regen(1, now(), now()); raise exception 'game schema should be private';
  exception when insufficient_privilege then null; end;
end $$;
reset role;
set role anon;
do $$ begin
  begin perform public.get_player_state(); raise exception 'anon should not execute';
  exception when insufficient_privilege then null; end;
end $$;
reset role;

-- ---------- sync_rolls ----------
set role authenticated;
do $$
declare s jsonb; batch jsonb; t bigint := floor(extract(epoch from now())*1000);
begin
  select jsonb_agg(jsonb_build_object('i', g, 'm', 1, 't', t)) into batch from generate_series(0, 49) g;
  s := public.sync_rolls(batch);
  assert (s->>'accepted')::int = 50, 'accepted 50: ' || s::text;
  assert s->>'rejected_reason' is null;
  assert (s->>'roll_index')::int = 50;
  s := public.sync_rolls(batch);
  assert (s->>'accepted')::int = 0 and s->>'rejected_reason' is null, 'retry: ' || s::text;
  s := public.sync_rolls(jsonb_build_array(jsonb_build_object('i', 99, 'm', 1, 't', t)));
  assert s->>'rejected_reason' = 'index_gap';
  s := public.sync_rolls(jsonb_build_array(jsonb_build_object('i', 50, 'm', 3, 't', t)));
  assert s->>'rejected_reason' = 'bad_multiplier';
end $$;
reset role;

-- insufficient + regen (superuser rewinds the clock)
update public.inventory_rolls set rolls = 0, regen_anchor = now() - interval '61 minutes', state_at = now() - interval '61 minutes'
 where user_id = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ declare s jsonb; t bigint := floor(extract(epoch from now() - interval '61 minutes')*1000);
begin
  s := public.sync_rolls(jsonb_build_array(jsonb_build_object('i', (public.get_player_state()->>'roll_index')::int, 'm', 1, 't', t)));
  assert s->>'rejected_reason' = 'insufficient_rolls', s::text;
  assert (s->>'rolls')::int = 12, 'regen 12 ticks: ' || (s->>'rolls');
  -- future timestamps clamp to now: no borrowing future regen
  s := public.sync_rolls(jsonb_build_array(jsonb_build_object('i', (s->>'roll_index')::int, 'm', 20, 't', t + 86400000*2)));
  assert s->>'rejected_reason' = 'insufficient_rolls', s::text;
end $$;
reset role;

do $$ declare r record; begin
  select * into r from game.regen(49, now() - interval '20 minutes', now());
  assert r.rolls = 50 and r.anchor = now();
  select * into r from game.regen(10, now() - interval '11 minutes', now());
  assert r.rolls = 12 and r.anchor = now() - interval '1 minute';
  select * into r from game.regen(70, now() - interval '5 hours', now());
  assert r.rolls = 70 and r.anchor = now();
end $$;

-- ---------- upgrades & island completion ----------
update public.profiles set crystals = game.island_cost(1) + 10 where id = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ declare s jsonb; begin
  for slot in 0..4 loop for k in 1..4 loop s := public.upgrade_structure(slot); end loop; end loop;
  assert (s->>'island_level')::int = 2, 'advanced to island 2: ' || s::text;
  assert s->'tiers' = '[0,0,0,0,0]'::jsonb;
  assert s->>'crystals' = '12', 'island 1 costs 24,998 after per-tier flooring: ' || (s->>'crystals');
  begin perform public.upgrade_structure(0); raise exception 'should be insufficient';
  exception when others then assert sqlerrm = 'insufficient_crystals', sqlerrm; end;
  begin perform public.upgrade_structure(7); raise exception 'bad slot';
  exception when others then assert sqlerrm = 'invalid_slot', sqlerrm; end;
end $$;
reset role;

-- ---------- raid (unshielded) ----------
update public.inventory_rolls set pending_attacks = '{5,2}' where user_id = '11111111-1111-1111-1111-111111111111';
update public.island_states set tiers = '{2,3,1,0,0}' where user_id = '22222222-2222-2222-2222-222222222222';
update public.profiles set crystals = 1000, shields = 0 where id = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ declare r jsonb; begin
  r := public.perform_raid();
  assert r->>'defender_id' = '22222222-2222-2222-2222-222222222222', 'matched Bravo';
  assert (r->>'shielded')::boolean = false and not (r->>'is_npc')::boolean;
  assert (r->>'damaged_slot')::int = 1, 'highest tier slot damaged';
  assert r->'state'->'pending_attacks' = '[2]'::jsonb;
  assert r->>'crystals_awarded' = '15000', r->>'crystals_awarded';  -- floor(250*1.2)=300 *10 *5
end $$;
reset role;
do $$ begin
  assert (select crystals from public.profiles where id = '22222222-2222-2222-2222-222222222222') = 0, 'defender drained';
  assert (select damaged from public.island_states where user_id = '22222222-2222-2222-2222-222222222222') = '{f,t,f,f,f}';
end $$;

-- ---------- revenge loop (defender's view) ----------
set request.jwt.claim.sub = '22222222-2222-2222-2222-222222222222';
update public.inventory_rolls set pending_attacks = '{1}' where user_id = '22222222-2222-2222-2222-222222222222';
update public.profiles set shields = 1 where id = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ declare lst jsonb; r jsonb; rid uuid; begin
  assert (select count(*) from public.raid_history) = 1, 'defender sees the raid via RLS';
  lst := public.list_revenge_targets();
  assert jsonb_array_length(lst) = 1 and lst->0->>'priority' = 'high', lst::text;
  rid := (lst->0->>'raid_id')::uuid;
  r := public.perform_raid(rid);
  assert r->>'defender_id' = '11111111-1111-1111-1111-111111111111';
  assert (r->>'shielded')::boolean and r->>'damaged_slot' is null;
  assert r->>'crystals_awarded' = '500', r->>'crystals_awarded';  -- 250*10*1 / 5
  assert jsonb_array_length(public.list_revenge_targets()) = 0, 'revenge consumed';
  begin perform public.repair_structure(1); raise exception 'broke';
  exception when others then assert sqlerrm = 'insufficient_crystals', sqlerrm; end;
end $$;
reset role;
do $$ begin
  assert (select shields from public.profiles where id = '11111111-1111-1111-1111-111111111111') = 0, 'shield consumed';
  assert (select notification->>'type' from public.raid_history where revenge_of is not null) = 'shield_block';
  assert (select not revenge_available from public.raid_history where revenge_of is not null), 'shielded raid gives no revenge';
end $$;
update public.profiles set crystals = 1000000 where id = '22222222-2222-2222-2222-222222222222';
update public.inventory_rolls set pending_attacks = '{1}' where user_id = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ declare s jsonb; begin
  s := public.repair_structure(1);
  assert s->'damaged' = '[false,false,false,false,false]'::jsonb;
  assert (s->>'crystals')::numeric = 1000000 - 531, s->>'crystals';  -- floor(floor(25000*.17*.25)/2)
  begin perform public.perform_raid((select id from public.raid_history where revenge_of is null)); raise exception 'double revenge';
  exception when others then assert sqlerrm = 'revenge_unavailable', sqlerrm; end;
end $$;
reset role;

-- ---------- card album ----------
update public.inventory_rolls set card_packs = 40 where user_id = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ declare r jsonb; begin
  for k in 1..40 loop
    r := public.open_card_pack();
    assert jsonb_array_length(r->'cards') = 3;
  end loop;
  assert (r->'state'->>'card_packs')::int = 0;
  begin perform public.open_card_pack(); raise exception 'no packs';
  exception when others then assert sqlerrm = 'no_card_packs', sqlerrm; end;
end $$;
reset role;
-- craft a missing card with dust
insert into public.card_albums (user_id, card_id)
select '22222222-2222-2222-2222-222222222222', id from public.card_definitions where set_id = 1
on conflict do nothing;
delete from public.card_albums where user_id = '22222222-2222-2222-2222-222222222222' and card_id = 5;
delete from public.card_set_completions where user_id = '22222222-2222-2222-2222-222222222222' and set_id = 1;
update public.profiles set card_dust = 300 where id = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ declare r jsonb; begin
  r := public.craft_card(5);
  assert (r->'state'->>'card_dust')::int = 50, r::text;
  assert jsonb_array_length(r->'completed_sets') = 1, 'crafting the last card completes the set';
  begin perform public.craft_card(5); raise exception 'owned';
  exception when others then assert sqlerrm = 'card_already_owned', sqlerrm; end;
end $$;
reset role;
\echo PASS 01_economy_raids
