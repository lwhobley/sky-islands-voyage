\set ON_ERROR_STOP 1
-- Live-ops catalog: card seasons and store products. Runs after 01-05.
-- Season 1 from seed.sql is live (now-1d .. now+60d).

set role authenticated;
do $$ begin
  begin perform public.admin_create_season('x', '{}'); raise exception 'players cannot create seasons';
  exception when insufficient_privilege then null; end;
  begin perform public.admin_set_product('x', 'sky.free.rolls', 1, 0, true); raise exception 'players cannot edit the catalog';
  exception when insufficient_privilege then null; end;
end $$;
reset role;

set role service_role;
do $$
declare
  good jsonb;
  r jsonb;
  s_from timestamptz := now() + interval '61 days';
begin
  good := jsonb_build_object(
    'name', ' Season 2: Tempest Tides ',
    'starts_at', s_from, 'ends_at', s_from + interval '30 days',
    'sets', jsonb_build_array(
      jsonb_build_object('name', 'Harbor Storms', 'reward_rolls', 40, 'reward_crystal_rolls', 60, 'cards', jsonb_build_array(
        jsonb_build_object('name', 'Squall Sprite', 'rarity', 1, 'drop_weight', 100),
        jsonb_build_object('name', 'Tidecaller', 'rarity', 3, 'drop_weight', 20))),
      jsonb_build_object('name', 'Deep Sky', 'reward_rolls', 0, 'reward_crystal_rolls', 10, 'cards', jsonb_build_array(
        jsonb_build_object('name', 'Leviathan Kite', 'rarity', 5, 'drop_weight', 1)))));

  r := public.admin_create_season('ops@studio', good);
  assert (r->>'sets')::int = 2 and (r->>'cards')::int = 3, r::text;

  -- name was trimmed; season shows as upcoming
  r := public.admin_list_seasons();
  assert jsonb_array_length(r) = 2 and r->0->>'name' = 'Season 2: Tempest Tides' and r->0->>'state' = 'upcoming', r::text;
  assert r->1->>'state' = 'live' and (r->1->>'cards')::int = 10, r::text;

  -- overlap with the live season and with the new one
  begin perform public.admin_create_season('ops@studio', jsonb_set(good, '{starts_at}', to_jsonb(now() + interval '10 days')));
    raise exception 'overlap';
  exception when others then assert sqlerrm = 'season_overlaps', sqlerrm; end;

  -- validation: everything is rejected atomically (no partial season is left behind)
  declare
    later timestamptz := now() + interval '200 days';
    base jsonb := jsonb_build_object('name', 'Bad', 'starts_at', later, 'ends_at', later + interval '1 day');
    card jsonb := jsonb_build_object('name', 'C', 'rarity', 1, 'drop_weight', 1);
    badcases jsonb[] := array[
      base || '{"sets": []}',
      base || '{"sets": "nope"}',
      jsonb_set(base, '{ends_at}', to_jsonb(later - interval '1 day')) || jsonb_build_object('sets', jsonb_build_array()),
      base || jsonb_build_object('sets', jsonb_build_array(jsonb_build_object('name', 'S', 'reward_rolls', 0, 'reward_crystal_rolls', 0, 'cards', '[]'::jsonb))),
      base || jsonb_build_object('sets', jsonb_build_array(jsonb_build_object('name', 'S', 'reward_rolls', 0, 'reward_crystal_rolls', 0,
              'cards', jsonb_build_array(jsonb_set(card, '{rarity}', '9'))))),
      base || jsonb_build_object('sets', jsonb_build_array(jsonb_build_object('name', 'S', 'reward_rolls', 0, 'reward_crystal_rolls', 0,
              'cards', jsonb_build_array(jsonb_set(card, '{drop_weight}', '0'))))),
      base || jsonb_build_object('sets', jsonb_build_array(jsonb_build_object('name', 'S', 'reward_rolls', 'many', 'reward_crystal_rolls', 0,
              'cards', jsonb_build_array(card)))),
      base || jsonb_build_object('sets', jsonb_build_array(jsonb_build_object('name', 'S', 'reward_rolls', 0, 'reward_crystal_rolls', 0,
              'cards', jsonb_build_array(card)))) || '{"starts_at": "not a date"}'];
    b jsonb;
  begin
    foreach b in array badcases loop
      begin perform public.admin_create_season('ops@studio', b); raise exception 'accepted bad season: %', b;
      exception when others then assert sqlerrm = 'invalid_season', sqlerrm || ' for ' || b::text; end;
    end loop;
    assert (select count(*) from public.card_seasons) = 2, 'no partial seasons left behind';
    assert (select count(*) from public.card_sets) = 4, 'no orphan sets';
  end;

  begin perform public.admin_create_season(' ', good); raise exception 'actor';
  exception when others then assert sqlerrm = 'actor_and_reason_required', sqlerrm; end;
end $$;
reset role;

-- the new season is not drawn from until it starts; the live one keeps working
do $$ begin
  assert game.active_season() = 1, 'upcoming season is not active';
  update public.card_seasons set starts_at = now() - interval '1 minute' where name like 'Season 2%';
  update public.card_seasons set ends_at = now() - interval '1 second' where id = 1;
  assert (select name from public.card_seasons where id = game.active_season()) like 'Season 2%', 'new season takes over when it starts';
end $$;

-- delete: only before the start
set role service_role;
do $$ declare later timestamptz := now() + interval '300 days'; r jsonb; sid smallint; begin
  r := public.admin_create_season('ops@studio', jsonb_build_object('name', 'Season 3', 'starts_at', later, 'ends_at', later + interval '1 day',
    'sets', jsonb_build_array(jsonb_build_object('name', 'S', 'reward_rolls', 0, 'reward_crystal_rolls', 0,
      'cards', jsonb_build_array(jsonb_build_object('name', 'C', 'rarity', 1, 'drop_weight', 1))))));
  sid := (r->>'season_id')::smallint;
  begin perform public.admin_delete_season('ops@studio', (select id from public.card_seasons where name like 'Season 2%')); raise exception 'live';
  exception when others then assert sqlerrm = 'season_already_started', sqlerrm; end;
  begin perform public.admin_delete_season('ops@studio', 9999::smallint); raise exception 'missing';
  exception when others then assert sqlerrm = 'season_not_found', sqlerrm; end;
  perform public.admin_delete_season('ops@studio', sid);
  assert not exists (select 1 from public.card_seasons where id = sid) and not exists (select 1 from public.card_sets where season_id = sid),
    'deleting a season removes its sets and cards';
end $$;

-- products
do $$ declare r jsonb; begin
  r := public.admin_set_product('ops@studio', 'sky.rolls.tiny', 50, 0, true);
  assert r->>'product_id' = 'sky.rolls.tiny' and (r->>'rolls')::int = 50, r::text;
  r := public.admin_set_product('ops@studio', 'sky.rolls.tiny', 75, 1, false);  -- update + deactivate
  assert (r->>'rolls')::int = 75 and not (r->>'active')::boolean, r::text;
  assert jsonb_array_length(public.admin_list_products()) = 5, 'seeded 4 + new one';
  begin perform public.admin_set_product('ops@studio', 'bad id!', 1, 0, true); raise exception 'id';
  exception when others then assert sqlerrm = 'invalid_product', sqlerrm; end;
  begin perform public.admin_set_product('ops@studio', 'sky.rolls.neg', -1, 0, true); raise exception 'neg';
  exception when others then assert sqlerrm = 'invalid_product', sqlerrm; end;
  -- a deactivated product can no longer be granted
  begin perform public.grant_purchase('22222222-2222-2222-2222-222222222222', 'google', 'sky.rolls.tiny', 'GPA.tiny'); raise exception 'inactive';
  exception when others then assert sqlerrm = 'unknown_product', sqlerrm; end;
end $$;
reset role;
do $$ begin
  assert (select count(*) from public.admin_actions where action in ('create_season', 'delete_season', 'set_product')) = 5, 'every successful change audited, failed attempts leave no trace';
end $$;
\echo PASS 06_liveops_catalog
