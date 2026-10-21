-- Sample card season so open_card_pack / craft_card work out of the box.
insert into public.card_seasons (id, name, starts_at, ends_at)
values (1, 'Season 1: Gears & Gales', now() - interval '1 day', now() + interval '60 days');

insert into public.card_sets (id, season_id, name, reward_rolls, reward_crystal_rolls) values
  (1, 1, 'Harbor Crew',     25, 40),
  (2, 1, 'Storm Chasers',   50, 80);

insert into public.card_definitions (set_id, name, rarity, drop_weight) values
  (1, 'Deckhand Pip',          1, 100),
  (1, 'Boilerwright Ada',      1, 100),
  (1, 'Quartermaster Fenn',    2,  60),
  (1, 'Navigator Sol',         3,  30),
  (1, 'Captain Brassbeard',    5,   8),
  (2, 'Gale Kite',             1, 100),
  (2, 'Thunder Drum',          2,  60),
  (2, 'Lightning Jar',         3,  30),
  (2, 'Cyclone Compass',       4,  15),
  (2, 'The Storm Spirit',      5,   5);

-- Explicit ids above: move identity sequences past them.
select setval(pg_get_serial_sequence('public.card_seasons', 'id'), 1);
select setval(pg_get_serial_sequence('public.card_sets', 'id'), 2);

-- Store catalog (product ids must match App Store Connect / Play Console).
insert into public.store_products (product_id, rolls, card_packs) values
  ('sky.rolls.small',   150,  0),
  ('sky.rolls.medium',  500,  2),
  ('sky.rolls.large',  1500,  8),
  ('sky.rolls.mega',   5000, 30);
