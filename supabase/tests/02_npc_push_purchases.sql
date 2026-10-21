\set ON_ERROR_STOP 1
-- NPC fallback, fair matching, push outbox, purchases, album read. Runs after 01.

-- ---------- NPC fallback ----------
-- Alpha (island 2) hit Bravo < 30 min ago; Charlie is island 60 and inactive => NPC outpost.
set request.jwt.claim.sub = '11111111-1111-1111-1111-111111111111';
update public.inventory_rolls set pending_attacks = '{2}' where user_id = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ declare r jsonb; before numeric; begin
  before := (public.get_player_state()->>'crystals')::numeric;
  r := public.perform_raid();
  assert (r->>'is_npc')::boolean and r->>'defender_id' is null, r::text;
  assert r->>'crystals_awarded' in ('6000', '1200'), r->>'crystals_awarded';  -- 300*10*2, or /5 shielded
  assert (r->'state'->>'crystals')::numeric = before + (r->>'crystals_awarded')::numeric;
  assert jsonb_array_length(r->'defender_tiers') = 5;
end $$;
reset role;
do $$ begin
  assert (select count(*) from public.raid_history where npc_name is not null and not revenge_available) = 1;
  assert (select count(*) from public.notification_outbox) = 2, 'only human raids enqueue pushes';
end $$;

-- fair-window matching: a level-120 attacker never gets a level-1 player
update public.island_states set island_level = 120 where user_id = '33333333-3333-3333-3333-333333333333';
update public.profiles set last_active_at = now() where id = '33333333-3333-3333-3333-333333333333';
do $$ begin
  assert game.match_raid_target('33333333-3333-3333-3333-333333333333', 120) is null, 'no unfair match';
  -- Bravo revenged Alpha < 30 min ago, so Alpha is on cooldown for Bravo.
  assert game.match_raid_target('22222222-2222-2222-2222-222222222222', 1) is null, 'cooldown respected';
end $$;
update public.island_states set island_level = 9 where user_id = '33333333-3333-3333-3333-333333333333';
do $$ begin
  assert game.match_raid_target('22222222-2222-2222-2222-222222222222', 1) = '33333333-3333-3333-3333-333333333333',
    'second window (+/-15) finds Charlie';
end $$;

-- ---------- push tokens + outbox ----------
set request.jwt.claim.sub = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ begin
  perform public.register_push_token('bravo-device-token-AAAA', 'android');
  perform public.register_push_token('bravo-device-token-BBBB', 'ios');
  assert (select count(*) from public.push_tokens) = 2, 'sees own tokens';
  begin perform public.register_push_token('x', 'android'); raise exception 'short token';
  exception when others then assert sqlerrm = 'invalid_push_token', sqlerrm; end;
  begin perform public.claim_push_batch(10); raise exception 'players cannot drain the outbox';
  exception when insufficient_privilege then null; end;
  begin perform 1 from public.notification_outbox; raise exception 'outbox hidden';
  exception when insufficient_privilege then null; end;
end $$;
reset role;
set role service_role;
do $$ declare b jsonb; ids bigint[]; bravo jsonb; begin
  b := public.claim_push_batch(10);
  assert jsonb_array_length(b) = 2, b::text;
  select e into bravo from jsonb_array_elements(b) e where e->>'user_id' = '22222222-2222-2222-2222-222222222222';
  assert jsonb_array_length(bravo->'tokens') = 2 and bravo->'payload'->>'priority' = 'high', bravo::text;
  assert jsonb_array_length(public.claim_push_batch(10)) = 0, 'claimed rows are not re-issued immediately';
  select array_agg((e->>'id')::bigint) into ids from jsonb_array_elements(b) e;
  perform public.complete_push_batch(ids, array['bravo-device-token-AAAA']);
end $$;
reset role;
do $$ begin
  assert (select count(*) from public.notification_outbox where sent_at is null) = 0;
  assert (select array_agg(token) from public.push_tokens) = '{bravo-device-token-BBBB}', 'dead token pruned';
end $$;
-- stale claim is retried
insert into public.notification_outbox (user_id, payload, claimed_at, attempts)
values ('22222222-2222-2222-2222-222222222222', '{"title":"t"}', now() - interval '5 minutes', 1);
set role service_role;
do $$ begin assert jsonb_array_length(public.claim_push_batch(10)) = 1, 'stale claim retried'; end $$;
reset role;

-- ---------- purchases ----------
set role authenticated;
do $$ begin
  begin perform public.grant_purchase('22222222-2222-2222-2222-222222222222', 'google', 'sky.rolls.small', 'GPA.1'); raise exception 'players cannot grant';
  exception when insufficient_privilege then null; end;
end $$;
reset role;
set role service_role;
do $$ declare s jsonb; r0 int; begin
  select rolls into r0 from public.inventory_rolls where user_id = '22222222-2222-2222-2222-222222222222';
  s := public.grant_purchase('22222222-2222-2222-2222-222222222222', 'google', 'sky.rolls.medium', 'GPA.1234');
  assert not (s->>'duplicate')::boolean and (s->>'rolls')::int = r0 + 500 and (s->>'card_packs')::int >= 2, s::text;
  s := public.grant_purchase('22222222-2222-2222-2222-222222222222', 'google', 'sky.rolls.medium', 'GPA.1234');
  assert (s->>'duplicate')::boolean and (s->>'rolls')::int = r0 + 500, 'idempotent';
  begin perform public.grant_purchase('11111111-1111-1111-1111-111111111111', 'google', 'sky.rolls.medium', 'GPA.1234'); raise exception 'stolen receipt';
  exception when others then assert sqlerrm = 'transaction_belongs_to_another_player', sqlerrm; end;
  begin perform public.grant_purchase('11111111-1111-1111-1111-111111111111', 'apple', 'nope', '1'); raise exception 'unknown';
  exception when others then assert sqlerrm = 'unknown_product', sqlerrm; end;
end $$;
reset role;

-- ---------- card album read ----------
insert into public.card_sets (season_id, name) values (1, 'Empty Set');
set request.jwt.claim.sub = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ declare a jsonb; begin
  a := public.get_card_album();
  assert jsonb_array_length(a->'sets') = 3 and jsonb_array_length(a->'sets'->0->'cards') = 5, a::text;
  assert (a->'sets'->0->>'completed')::boolean, 'Harbor Crew completed by crafting in 01';
  assert a->'sets'->2->'cards' = '[]'::jsonb, 'a set with no cards is an empty list, not null';
  assert (select count(*) from public.purchases) = 1, 'sees own purchase';
end $$;
reset role;
delete from public.card_sets where name = 'Empty Set';
\echo PASS 02_npc_push_purchases
