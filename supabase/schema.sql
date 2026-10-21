-- =============================================================================
-- Sky Islands: Voyage — Supabase / PostgreSQL schema (run once, as a migration)
-- =============================================================================
-- Trust model
--   * Clients may SELECT their own rows (RLS). They can never INSERT / UPDATE /
--     DELETE economy tables directly: every mutation goes through a
--     SECURITY DEFINER RPC in `public`, or through the service role (NestJS:
--     IAP receipt validation, push delivery, live-ops).
--   * Rolls work offline. The client uploads only (index, multiplier, timestamp);
--     the server re-derives every outcome from the player's seed and re-checks
--     the regen budget, so a modified client cannot invent rewards.
--     game.pick_outcome / game.regen MUST stay in lockstep with DiceRollEngine.cs
--     and EconomyRules (GameEconomyManager.cs).
--   * Cost(200) = 25,000 * 1.2^199 ≈ 1.4e20 overflows bigint, so crystals are
--     numeric(40,0) and are sent to clients as strings.
--
-- Lock order (prevents deadlocks between concurrent RPCs):
--   inventory_rolls -> raid_history -> profiles (by id) -> island_states
-- =============================================================================

create schema if not exists game;   -- private helpers; not exposed through PostgREST
revoke all on schema game from public;

-- -----------------------------------------------------------------------------
-- Tables
-- -----------------------------------------------------------------------------

create table public.profiles (
  id             uuid primary key references auth.users (id) on delete cascade,
  display_name   text not null default 'Voyager' check (char_length(display_name) between 1 and 24),
  crystals       numeric(40, 0) not null default 0 check (crystals >= 0),
  shields        smallint not null default 0 check (shields between 0 and 3),
  card_dust      bigint not null default 0 check (card_dust >= 0),
  last_active_at timestamptz not null default now(),
  created_at     timestamptz not null default now()
);

create table public.island_states (
  user_id               uuid primary key references public.profiles (id) on delete cascade,
  island_level          smallint not null default 1 check (island_level between 1 and 200),
  tiers                 smallint[] not null default '{0,0,0,0,0}',
  damaged               boolean[] not null default '{f,f,f,f,f}',
  campaign_completed_at timestamptz,  -- set when island 200 is finished
  updated_at            timestamptz not null default now(),
  check (cardinality(tiers) = 5 and cardinality(damaged) = 5),
  check (0 <= all (tiers) and 4 >= all (tiers))
);

create table public.inventory_rolls (
  user_id         uuid primary key references public.profiles (id) on delete cascade,
  rolls           integer not null default 50 check (rolls >= 0),
  regen_anchor    timestamptz not null default now(),  -- start of the current 5-minute regen tick
  state_at        timestamptz not null default now(),  -- time the roll budget was last evaluated
  rng_seed        text not null default gen_random_uuid()::text,
  roll_index      bigint not null default 0,           -- next roll index the server expects
  dry_streak      smallint not null default 0,         -- consecutive rolls without cannon/shield
  pending_attacks smallint[] not null default '{}',    -- multipliers of unspent Wind-Cannon hits
  card_packs      integer not null default 0 check (card_packs >= 0),
  updated_at      timestamptz not null default now()
);

-- Anti-cheat tallies (never readable by players, so they can't calibrate against it).
-- Offline rolls require the seed on the device, so a modded client can see upcoming
-- outcomes and raise the multiplier only before good ones. Honest play has the same
-- non-Crystal ("premium") rate at every multiplier; cheating shows up as a gap between
-- high (>=10x) and low (<=2x) multiplier rolls. See public.admin_multiplier_anomalies.
create table public.roll_audit (
  user_id    uuid primary key references public.profiles (id) on delete cascade,
  hi_rolls   integer not null default 0,
  hi_premium integer not null default 0,
  lo_rolls   integer not null default 0,
  lo_premium integer not null default 0,
  updated_at timestamptz not null default now()
);

-- Every live-ops action taken through the admin API.
create table public.admin_actions (
  id         bigint generated always as identity primary key,
  actor      text not null,  -- operator identity supplied by the admin API
  action     text not null,
  user_id    uuid references public.profiles (id) on delete set null,
  details    jsonb not null default '{}',
  created_at timestamptz not null default now()
);

-- Case-insensitive substrings rejected in display/guild names and masked in guild chat.
create table public.banned_name_terms (
  term text primary key check (term = lower(term) and char_length(term) between 2 and 40)
);

create table public.raid_history (
  id                uuid primary key default gen_random_uuid(),
  attacker_id       uuid not null references public.profiles (id) on delete cascade,
  defender_id       uuid references public.profiles (id) on delete cascade,  -- null: NPC outpost
  npc_name          text,
  multiplier        smallint not null,
  shielded          boolean not null,
  crystals_awarded  numeric(40, 0) not null,
  crystals_lost     numeric(40, 0) not null,
  damaged_slot      smallint,
  revenge_of        uuid references public.raid_history (id) on delete set null,
  revenge_available boolean not null,
  revenge_raid_id   uuid references public.raid_history (id) on delete set null,
  notification      jsonb,   -- defender payload for Realtime + push; null for NPC raids
  created_at        timestamptz not null default now(),
  check (attacker_id <> defender_id),
  check ((defender_id is null) <> (npc_name is null)),
  check ((defender_id is null) = (notification is null))
);

-- Card album: seasons -> sets -> cards. card_albums holds the cards a player owns.
create table public.card_seasons (
  id        smallint generated by default as identity primary key,
  name      text not null,
  starts_at timestamptz not null,
  ends_at   timestamptz not null,
  check (ends_at > starts_at)
);

create table public.card_sets (
  id                   integer generated by default as identity primary key,
  season_id            smallint not null references public.card_seasons (id) on delete cascade,
  name                 text not null,
  reward_rolls         integer not null default 0 check (reward_rolls >= 0),
  -- crystal reward = roll_reward(island_level) * this, so it stays relevant on every island
  reward_crystal_rolls integer not null default 0 check (reward_crystal_rolls >= 0)
);

create table public.card_definitions (
  id          integer generated by default as identity primary key,
  set_id      integer not null references public.card_sets (id) on delete cascade,
  name        text not null,
  rarity      smallint not null check (rarity between 1 and 5),
  drop_weight integer not null check (drop_weight > 0)
);

create table public.card_albums (
  user_id     uuid not null references public.profiles (id) on delete cascade,
  card_id     integer not null references public.card_definitions (id) on delete cascade,
  obtained_at timestamptz not null default now(),
  primary key (user_id, card_id)
);

create table public.card_set_completions (
  user_id      uuid not null references public.profiles (id) on delete cascade,
  set_id       integer not null references public.card_sets (id) on delete cascade,
  completed_at timestamptz not null default now(),
  primary key (user_id, set_id)
);

-- -----------------------------------------------------------------------------
-- Indexes
-- -----------------------------------------------------------------------------

create index island_states_level_idx       on public.island_states (island_level, user_id);  -- raid matching
create index profiles_last_active_idx      on public.profiles (last_active_at);
create index raid_history_cooldown_idx     on public.raid_history (attacker_id, defender_id, created_at desc);
create index raid_history_revenge_idx      on public.raid_history (defender_id, created_at desc) where revenge_available;
create index card_sets_season_idx          on public.card_sets (season_id);
create index card_definitions_set_idx      on public.card_definitions (set_id);

-- -----------------------------------------------------------------------------
-- Economy rules (mirror of EconomyRules in GameEconomyManager.cs)
-- -----------------------------------------------------------------------------

-- 1.2^(n-1) is evaluated exactly as 6^(n-1) / 5^(n-1) with integer division, so
-- the client (BigInteger) and server (numeric) produce bit-identical prices.

-- Cost(n) = floor(BaseCost * 1.2^(n-1)): total crystals to finish island n.
create function game.island_cost(p_level int) returns numeric
language sql immutable strict set search_path = '' as $$
  select div(25000 * 6::numeric ^ (p_level - 1), 5::numeric ^ (p_level - 1))
$$;

-- Crystals per 1x Crystal roll; grows with the same curve so rolls-per-island stays flat.
create function game.roll_reward(p_level int) returns numeric
language sql immutable strict set search_path = '' as $$
  select div(250 * 6::numeric ^ (p_level - 1), 5::numeric ^ (p_level - 1))
$$;

-- Island cost split across 5 structures x 4 tiers (weights in %). p_slot is 0-based,
-- p_tier is the tier being built (1..4).
create function game.upgrade_cost(p_level int, p_slot int, p_tier int) returns numeric
language sql immutable strict set search_path = '' as $$
  select div(25000 * 6::numeric ^ (p_level - 1)
               * (array[14, 17, 20, 23, 26])[p_slot + 1]
               * (array[15, 20, 25, 40])[p_tier],
             5::numeric ^ (p_level - 1) * 10000)
$$;

-- Repairing a damaged structure costs half of its current tier.
create function game.repair_cost(p_level int, p_slot int, p_tier int) returns numeric
language sql immutable strict set search_path = '' as $$
  select div(game.upgrade_cost(p_level, p_slot, p_tier), 2)
$$;

-- A Cloud-Shield blocks 80% of the raid (attacker gets 1/5).
create function game.raid_loot(p_level int, p_multiplier int, p_shielded boolean) returns numeric
language sql immutable strict set search_path = '' as $$
  select div(game.roll_reward(p_level) * 10 * p_multiplier, case when p_shielded then 5 else 1 end)
$$;

create function game.epoch_ms(p_at timestamptz) returns bigint
language sql immutable strict set search_path = '' as $$
  select floor(extract(epoch from p_at) * 1000)::bigint
$$;

-- Roll regeneration: 1 roll / 300 s up to 50. Above the cap (bought rolls, batteries) the timer idles.
create function game.regen(p_rolls int, p_anchor timestamptz, p_at timestamptz,
                           out rolls int, out anchor timestamptz)
language plpgsql immutable strict set search_path = '' as $$
declare
  v_ticks bigint;
begin
  if p_rolls >= 50 then
    rolls := p_rolls; anchor := p_at; return;
  end if;
  v_ticks := floor(extract(epoch from (p_at - p_anchor)) / 300);
  if v_ticks <= 0 then
    rolls := p_rolls; anchor := p_anchor;
  elsif p_rolls + v_ticks >= 50 then
    rolls := 50; anchor := p_at;
  else
    rolls := p_rolls + v_ticks;
    anchor := p_anchor + make_interval(secs => v_ticks * 300);
  end if;
end $$;

-- Deterministic roll RNG: first 4 bytes of SHA-256("<seed>:<index>") as a big-endian uint32.
create function game.roll_u32(p_seed text, p_index bigint) returns bigint
language sql immutable strict set search_path = '' as $$
  select (get_byte(h, 0)::bigint << 24) | (get_byte(h, 1) << 16) | (get_byte(h, 2) << 8) | get_byte(h, 3)
  from (select sha256(convert_to(p_seed || ':' || p_index::text, 'UTF8')) as h) x
$$;

-- Outcome codes: 0 Crystals, 1 Wind-Cannon, 2 Cloud-Shield, 3 Storm Spirit, 4 Aether Battery.
-- Weights 40/25/15/12/8 (x10 to stay integral). Pity (15+ dry rolls) boosts cannon & shield by 20%.
create function game.pick_outcome(p_seed text, p_index bigint, p_pity boolean) returns int
language plpgsql immutable strict set search_path = '' as $$
declare
  v_weights int[] := case when p_pity then array[400, 300, 180, 120, 80]
                          else array[400, 250, 150, 120, 80] end;
  v_total int := case when p_pity then 1080 else 1000 end;
  v_r bigint := (game.roll_u32(p_seed, p_index) * v_total) >> 32;
  v_acc int := 0;
begin
  for k in 1..5 loop
    v_acc := v_acc + v_weights[k];
    if v_r < v_acc then return k - 1; end if;
  end loop;
  return 0;  -- unreachable
end $$;

-- -----------------------------------------------------------------------------
-- Internal helpers
-- -----------------------------------------------------------------------------

-- Flat snapshot every mutating RPC returns; parsed by PlayerStateSnapshot (SupabaseClient.cs).
create function game.player_state(p_uid uuid) returns jsonb
language sql stable set search_path = '' as $$
  select jsonb_build_object(
    'server_time_ms',     game.epoch_ms(now()),
    'user_id',            p.id,
    'display_name',       p.display_name,
    'crystals',           p.crystals::text,
    'shields',            p.shields,
    'card_dust',          p.card_dust,
    'rolls',              r.rolls,
    'regen_anchor_ms',    game.epoch_ms(r.regen_anchor),
    'state_at_ms',        game.epoch_ms(r.state_at),
    'rng_seed',           r.rng_seed,
    'roll_index',         r.roll_index,
    'dry_streak',         r.dry_streak,
    'pending_attacks',    to_jsonb(r.pending_attacks),
    'card_packs',         r.card_packs,
    'island_level',       i.island_level,
    'tiers',              to_jsonb(i.tiers),
    'damaged',            to_jsonb(i.damaged),
    'campaign_completed', i.campaign_completed_at is not null)
  from public.profiles p
  join public.inventory_rolls r on r.user_id = p.id
  join public.island_states i on i.user_id = p.id
  where p.id = p_uid
$$;

create function game.require_uid() returns uuid
language plpgsql stable set search_path = '' as $$
begin
  if auth.uid() is null then
    raise exception 'not_authenticated' using errcode = '28000';
  end if;
  return auth.uid();
end $$;

-- Advances to the next island once all 5 structures are at tier 4 and intact.
create function game.complete_island_if_done(p_uid uuid) returns boolean
language plpgsql set search_path = '' as $$
begin
  update public.island_states
     set island_level          = least(island_level + 1, 200),
         tiers                 = case when island_level < 200 then '{0,0,0,0,0}'::smallint[] else tiers end,
         damaged               = '{f,f,f,f,f}',
         campaign_completed_at = case when island_level = 200 then now() end,
         updated_at            = now()
   where user_id = p_uid
     and campaign_completed_at is null
     and tiers = '{4,4,4,4,4}'::smallint[]
     and not (true = any (damaged));
  return found;
end $$;

-- Raid matchmaking (btree on island_level), skipping anyone this attacker hit in the
-- last 30 minutes: nearest level within +/-5 among players active in 14 days, then
-- within +/-15 among players active in 30 days. Null means "raid an NPC outpost", so a
-- high-level attacker is never matched against a newcomer it would wipe out.
create function game.match_raid_target(p_uid uuid, p_level int) returns uuid
language sql volatile set search_path = '' as $$
  select i.user_id
    from public.island_states i
    join public.profiles p on p.id = i.user_id
    join (values (5, interval '14 days', 1), (15, interval '30 days', 2)) w (spread, recency, pass)
      on i.island_level between p_level - w.spread and p_level + w.spread
     and p.last_active_at > now() - w.recency
   where i.user_id <> p_uid
     and not exists (select 1 from public.raid_history h
                      where h.attacker_id = p_uid and h.defender_id = i.user_id
                        and h.created_at > now() - interval '30 minutes')
   order by w.pass, abs(i.island_level - p_level), random()
   limit 1
$$;

-- Grants rewards for every set in the season the player has just completed.
create function game.award_set_completions(p_uid uuid, p_season smallint) returns jsonb
language plpgsql set search_path = '' as $$
declare
  v_set public.card_sets%rowtype;
  v_level int;
  v_crystals numeric;
  v_awarded jsonb := '[]';
begin
  select island_level into v_level from public.island_states where user_id = p_uid;
  for v_set in
    select s.* from public.card_sets s
     where s.season_id = p_season
       and exists (select 1 from public.card_definitions d where d.set_id = s.id)
       and not exists (select 1 from public.card_set_completions c where c.user_id = p_uid and c.set_id = s.id)
       and not exists (select 1 from public.card_definitions d
                        where d.set_id = s.id
                          and not exists (select 1 from public.card_albums a
                                           where a.user_id = p_uid and a.card_id = d.id))
  loop
    insert into public.card_set_completions (user_id, set_id) values (p_uid, v_set.id);
    v_crystals := game.roll_reward(v_level) * v_set.reward_crystal_rolls;
    update public.profiles set crystals = crystals + v_crystals where id = p_uid;
    update public.inventory_rolls set rolls = rolls + v_set.reward_rolls where user_id = p_uid;
    v_awarded := v_awarded || jsonb_build_object(
      'set_id', v_set.id, 'name', v_set.name,
      'reward_rolls', v_set.reward_rolls, 'reward_crystals', v_crystals::text);
  end loop;
  return v_awarded;
end $$;

create function game.active_season() returns smallint
language sql stable set search_path = '' as $$
  select id from public.card_seasons
   where now() >= starts_at and now() < ends_at
   order by starts_at desc
   limit 1
$$;

-- New auth user -> starter rows.
create function game.handle_new_user() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  insert into public.profiles (id) values (new.id);
  insert into public.inventory_rolls (user_id) values (new.id);
  insert into public.island_states (user_id) values (new.id);
  return new;
end $$;

create trigger on_auth_user_created
  after insert on auth.users
  for each row execute function game.handle_new_user();

-- -----------------------------------------------------------------------------
-- Public RPCs (POST /rest/v1/rpc/<name>)
-- -----------------------------------------------------------------------------

create function public.get_player_state() returns jsonb
language plpgsql security definer set search_path = '' as $$
begin
  return game.player_state(game.require_uid());
end $$;

-- Uploads offline rolls: p_rolls = [{"i": rollIndex, "m": multiplier, "t": unixMs}, ...].
-- Already-applied indexes are skipped (safe to retry). Processing stops at the first
-- invalid roll; the response's state is authoritative and the client rebases on it.
create function public.sync_rolls(p_rolls jsonb) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_now timestamptz := now();
  v_inv public.inventory_rolls%rowtype;
  v_shields smallint;
  v_reward numeric;
  v_crystals numeric := 0;
  v_item jsonb;
  v_i bigint;
  v_m int;
  v_t timestamptz;
  v_outcome int;
  v_accepted int := 0;
  v_rejected text;
  v_hi_rolls int := 0;  -- anti-cheat tallies, see public.roll_audit
  v_hi_premium int := 0;
  v_lo_rolls int := 0;
  v_lo_premium int := 0;
begin
  if jsonb_typeof(p_rolls) is distinct from 'array' or jsonb_array_length(p_rolls) > 500 then
    raise exception 'invalid_batch' using errcode = '22023';
  end if;

  select * into v_inv from public.inventory_rolls where user_id = v_uid for update;
  select shields into v_shields from public.profiles where id = v_uid for update;
  v_reward := game.roll_reward((select island_level from public.island_states where user_id = v_uid));

  for v_item in select value from jsonb_array_elements(p_rolls) loop
    v_i := (v_item ->> 'i')::bigint;
    v_m := (v_item ->> 'm')::int;
    if v_i < v_inv.roll_index then continue; end if;
    if v_i > v_inv.roll_index then v_rejected := 'index_gap'; exit; end if;
    if v_m is null or v_m not in (1, 2, 5, 10, 20, 50) then v_rejected := 'bad_multiplier'; exit; end if;

    -- Client clocks are untrusted: clamp into [last evaluation, now].
    v_t := least(greatest(to_timestamp((v_item ->> 't')::bigint / 1000.0), v_inv.state_at), v_now);
    select g.rolls, g.anchor into v_inv.rolls, v_inv.regen_anchor
      from game.regen(v_inv.rolls, v_inv.regen_anchor, v_t) g;
    v_inv.state_at := v_t;
    if v_inv.rolls < v_m then v_rejected := 'insufficient_rolls'; exit; end if;
    v_inv.rolls := v_inv.rolls - v_m;

    v_outcome := game.pick_outcome(v_inv.rng_seed, v_inv.roll_index, v_inv.dry_streak >= 15);
    case v_outcome
      when 0 then v_crystals := v_crystals + v_reward * v_m;
      when 1 then v_inv.pending_attacks := v_inv.pending_attacks || v_m::smallint;
      when 2 then
        if v_shields < 3 then v_shields := v_shields + 1;
        else v_crystals := v_crystals + v_reward * v_m;  -- shields full: pay out crystals instead
        end if;
      when 3 then v_inv.card_packs := v_inv.card_packs + v_m;
      when 4 then v_inv.rolls := v_inv.rolls + 2 * v_m;
    end case;
    v_inv.dry_streak := case when v_outcome in (1, 2) then 0 else least(v_inv.dry_streak + 1, 1000) end;
    v_inv.roll_index := v_inv.roll_index + 1;
    v_accepted := v_accepted + 1;

    if v_m >= 10 then
      v_hi_rolls := v_hi_rolls + 1;
      v_hi_premium := v_hi_premium + (v_outcome <> 0)::int;
    elsif v_m <= 2 then
      v_lo_rolls := v_lo_rolls + 1;
      v_lo_premium := v_lo_premium + (v_outcome <> 0)::int;
    end if;
  end loop;

  select g.rolls, g.anchor into v_inv.rolls, v_inv.regen_anchor
    from game.regen(v_inv.rolls, v_inv.regen_anchor, v_now) g;

  update public.inventory_rolls
     set rolls = v_inv.rolls, regen_anchor = v_inv.regen_anchor, state_at = v_now,
         roll_index = v_inv.roll_index, dry_streak = v_inv.dry_streak,
         pending_attacks = v_inv.pending_attacks, card_packs = v_inv.card_packs, updated_at = v_now
   where user_id = v_uid;
  update public.profiles
     set crystals = crystals + v_crystals, shields = v_shields, last_active_at = v_now
   where id = v_uid;

  if v_hi_rolls + v_lo_rolls > 0 then
    insert into public.roll_audit as a (user_id, hi_rolls, hi_premium, lo_rolls, lo_premium)
    values (v_uid, v_hi_rolls, v_hi_premium, v_lo_rolls, v_lo_premium)
    on conflict (user_id) do update
      set hi_rolls = a.hi_rolls + excluded.hi_rolls, hi_premium = a.hi_premium + excluded.hi_premium,
          lo_rolls = a.lo_rolls + excluded.lo_rolls, lo_premium = a.lo_premium + excluded.lo_premium,
          updated_at = now();
  end if;

  return game.player_state(v_uid)
         || jsonb_build_object('accepted', v_accepted, 'rejected_reason', v_rejected);
end $$;

create function public.upgrade_structure(p_slot int) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_isl public.island_states%rowtype;
  v_tier int;
  v_cost numeric;
begin
  if p_slot is null or p_slot not between 0 and 4 then
    raise exception 'invalid_slot' using errcode = '22023';
  end if;
  perform 1 from public.profiles where id = v_uid for update;
  select * into v_isl from public.island_states where user_id = v_uid for update;
  if v_isl.campaign_completed_at is not null then raise exception 'campaign_completed'; end if;
  if v_isl.damaged[p_slot + 1] then raise exception 'structure_damaged'; end if;
  v_tier := v_isl.tiers[p_slot + 1];
  if v_tier >= 4 then raise exception 'max_tier'; end if;

  v_cost := game.upgrade_cost(v_isl.island_level, p_slot, v_tier + 1);
  update public.profiles set crystals = crystals - v_cost, last_active_at = now()
   where id = v_uid and crystals >= v_cost;
  if not found then raise exception 'insufficient_crystals'; end if;

  v_isl.tiers[p_slot + 1] := v_tier + 1;
  update public.island_states set tiers = v_isl.tiers, updated_at = now() where user_id = v_uid;
  perform game.complete_island_if_done(v_uid);
  return game.player_state(v_uid);
end $$;

create function public.repair_structure(p_slot int) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_isl public.island_states%rowtype;
  v_cost numeric;
begin
  if p_slot is null or p_slot not between 0 and 4 then
    raise exception 'invalid_slot' using errcode = '22023';
  end if;
  perform 1 from public.profiles where id = v_uid for update;
  select * into v_isl from public.island_states where user_id = v_uid for update;
  if not v_isl.damaged[p_slot + 1] then raise exception 'not_damaged'; end if;

  v_cost := game.repair_cost(v_isl.island_level, p_slot, v_isl.tiers[p_slot + 1]);
  update public.profiles set crystals = crystals - v_cost, last_active_at = now()
   where id = v_uid and crystals >= v_cost;
  if not found then raise exception 'insufficient_crystals'; end if;

  v_isl.damaged[p_slot + 1] := false;
  update public.island_states set damaged = v_isl.damaged, updated_at = now() where user_id = v_uid;
  perform game.complete_island_if_done(v_uid);
  return game.player_state(v_uid);
end $$;

-- Spends the oldest Wind-Cannon hit. The server picks the target (or the original attacker
-- when p_revenge_of is a raid against the caller), so clients cannot single out victims.
-- With no fair human target the cannon hits an NPC outpost (no defender, no revenge).
create function public.perform_raid(p_revenge_of uuid default null) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_inv public.inventory_rolls%rowtype;
  v_mult int;
  v_target uuid;
  v_att public.profiles%rowtype;
  v_def public.profiles%rowtype;
  v_def_isl public.island_states%rowtype;
  v_att_level int;
  v_shielded boolean;
  v_loot numeric;
  v_lost numeric;
  v_slot int;
  v_raid_id uuid := gen_random_uuid();
  v_notification jsonb;
  v_npc text;
begin
  select * into v_inv from public.inventory_rolls where user_id = v_uid for update;
  if coalesce(cardinality(v_inv.pending_attacks), 0) = 0 then
    raise exception 'no_attack_available';
  end if;
  v_mult := v_inv.pending_attacks[1];
  select island_level into v_att_level from public.island_states where user_id = v_uid;

  if p_revenge_of is not null then
    select attacker_id into v_target from public.raid_history
     where id = p_revenge_of and defender_id = v_uid and revenge_available
       for update;
    if v_target is null then raise exception 'revenge_unavailable'; end if;
  else
    v_target := game.match_raid_target(v_uid, v_att_level);
  end if;

  if v_target is null then
    -- No fair human target: raid a generated NPC outpost at the attacker's level.
    v_npc := (array['Sky Pirate Outpost', 'Rustfang Raider Camp', 'Cloudreaver Den', 'Gearjack Hideout'])
             [1 + floor(random() * 4)::int];
    perform 1 from public.profiles where id = v_uid for update;
    select * into v_att from public.profiles where id = v_uid;
    v_def_isl.island_level := v_att_level;
    v_def_isl.tiers := array(select (1 + floor(random() * 4))::smallint from generate_series(1, 5));
    v_def_isl.damaged := '{f,f,f,f,f}';
    v_shielded := random() < 0.25;
  else
    perform 1 from public.profiles where id in (v_uid, v_target) order by id for update;
    select * into v_att from public.profiles where id = v_uid;
    select * into v_def from public.profiles where id = v_target;
    select * into v_def_isl from public.island_states where user_id = v_target for update;
    v_shielded := v_def.shields > 0;
  end if;

  v_loot := game.raid_loot(v_att_level, v_mult, v_shielded);
  v_lost := case when v_npc is null then least(v_def.crystals, v_loot) else 0 end;

  if not v_shielded then
    -- Knock the most valuable intact structure into its damaged/smoking state.
    select s.slot - 1 into v_slot
      from unnest(v_def_isl.tiers, v_def_isl.damaged) with ordinality as s (tier, dmg, slot)
     where s.tier > 0 and not s.dmg
     order by s.tier desc, s.slot desc
     limit 1;
    if v_slot is not null then
      v_def_isl.damaged[v_slot + 1] := true;
      if v_npc is null then
        update public.island_states set damaged = v_def_isl.damaged, updated_at = now()
         where user_id = v_target;
      end if;
    end if;
  end if;

  if v_npc is null then
    update public.profiles
       set crystals = crystals - v_lost, shields = shields - case when v_shielded then 1 else 0 end
     where id = v_target;
  end if;
  update public.profiles set crystals = crystals + v_loot, last_active_at = now() where id = v_uid;
  update public.inventory_rolls
     set pending_attacks = v_inv.pending_attacks[2:], updated_at = now()
   where user_id = v_uid;

  -- Built before the insert so the Realtime INSERT event already carries it.
  if v_npc is null then
    v_notification := jsonb_build_object(
      'type',          case when v_shielded then 'shield_block' else 'revenge' end,
      'priority',      case when v_shielded then 'normal' else 'high' end,
      'raid_id',       v_raid_id,
      'attacker_id',   v_uid,
      'attacker_name', v_att.display_name,
      'crystals_lost', v_lost::text,
      'damaged_slot',  v_slot,
      'shielded',      v_shielded,
      'is_revenge',    p_revenge_of is not null,
      'title',         case when v_shielded then 'Your Cloud-Shield held!' else 'Your island was raided!' end,
      'body',          case when v_shielded
                         then format('%s''s Wind-Cannon was deflected. You lost %s crystals.', v_att.display_name, v_lost)
                         else format('%s took %s crystals. Take your revenge!', v_att.display_name, v_lost) end);
  end if;

  insert into public.raid_history
    (id, attacker_id, defender_id, npc_name, multiplier, shielded, crystals_awarded, crystals_lost,
     damaged_slot, revenge_of, revenge_available, notification)
  values
    (v_raid_id, v_uid, v_target, v_npc, v_mult, v_shielded, v_loot, v_lost,
     v_slot, p_revenge_of, v_npc is null and not v_shielded, v_notification);

  if p_revenge_of is not null then
    update public.raid_history set revenge_available = false, revenge_raid_id = v_raid_id
     where id = p_revenge_of;
  end if;

  return jsonb_build_object(
    'raid_id',               v_raid_id,
    'defender_id',           v_target,
    'defender_name',         coalesce(v_npc, v_def.display_name),
    'is_npc',                v_npc is not null,
    'defender_island_level', v_def_isl.island_level,
    'defender_tiers',        to_jsonb(v_def_isl.tiers),
    'defender_damaged',      to_jsonb(v_def_isl.damaged),
    'shielded',              v_shielded,
    'multiplier',            v_mult,
    'crystals_awarded',      v_loot::text,
    'damaged_slot',          v_slot,
    'is_revenge',            p_revenge_of is not null,
    'state',                 game.player_state(v_uid));
end $$;

-- Pending revenge targets for the caller (RLS-scoped, newest first).
create function public.list_revenge_targets() returns jsonb
language sql stable set search_path = '' as $$
  select coalesce(jsonb_agg(h.notification order by h.created_at desc), '[]')
    from (select notification, created_at from public.raid_history
           where defender_id = auth.uid() and revenge_available
           order by created_at desc
           limit 20) h
$$;

-- Opens one pack: 3 weighted draws from the active season. Duplicates become dust (10 x rarity).
create function public.open_card_pack() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_season smallint := game.active_season();
  v_packs int;
  v_card public.card_definitions%rowtype;
  v_dup boolean;
  v_dust bigint := 0;
  v_cards jsonb := '[]';
  v_sets jsonb;
begin
  if v_season is null then raise exception 'no_active_season'; end if;
  select card_packs into v_packs from public.inventory_rolls where user_id = v_uid for update;
  if v_packs <= 0 then raise exception 'no_card_packs'; end if;
  perform 1 from public.profiles where id = v_uid for update;

  for k in 1..3 loop
    -- Weighted pick (Efraimidis–Spirakis): smallest -ln(u)/w wins.
    select d.* into v_card
      from public.card_definitions d
      join public.card_sets s on s.id = d.set_id
     where s.season_id = v_season
     order by -ln(1.0 - random()) / d.drop_weight
     limit 1;
    if not found then raise exception 'season_has_no_cards'; end if;

    insert into public.card_albums (user_id, card_id) values (v_uid, v_card.id)
      on conflict do nothing;
    v_dup := not found;
    if v_dup then v_dust := v_dust + 10 * v_card.rarity; end if;
    v_cards := v_cards || jsonb_build_object(
      'card_id', v_card.id, 'set_id', v_card.set_id, 'name', v_card.name,
      'rarity', v_card.rarity, 'duplicate', v_dup);
  end loop;

  update public.inventory_rolls set card_packs = card_packs - 1, updated_at = now() where user_id = v_uid;
  update public.profiles set card_dust = card_dust + v_dust where id = v_uid;
  v_sets := game.award_set_completions(v_uid, v_season);

  return jsonb_build_object('cards', v_cards, 'dust_gained', v_dust,
                            'completed_sets', v_sets, 'state', game.player_state(v_uid));
end $$;

-- Recycles dust into a specific missing card of the active season (50 x rarity).
create function public.craft_card(p_card_id int) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_season smallint := game.active_season();
  v_rarity smallint;
  v_sets jsonb;
begin
  select d.rarity into v_rarity
    from public.card_definitions d
    join public.card_sets s on s.id = d.set_id
   where d.id = p_card_id and s.season_id = v_season;
  if v_rarity is null then raise exception 'card_not_in_active_season'; end if;

  perform 1 from public.profiles where id = v_uid for update;
  if exists (select 1 from public.card_albums where user_id = v_uid and card_id = p_card_id) then
    raise exception 'card_already_owned';
  end if;
  update public.profiles set card_dust = card_dust - 50 * v_rarity
   where id = v_uid and card_dust >= 50 * v_rarity;
  if not found then raise exception 'insufficient_dust'; end if;

  insert into public.card_albums (user_id, card_id) values (v_uid, p_card_id);
  v_sets := game.award_set_completions(v_uid, v_season);
  return jsonb_build_object('completed_sets', v_sets, 'state', game.player_state(v_uid));
end $$;

-- Album screen: active season, every set with its cards and the caller's progress.
create function public.get_card_album() returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_season public.card_seasons%rowtype;
  v_level int;
begin
  select * into v_season from public.card_seasons where id = game.active_season();
  if v_season.id is null then return jsonb_build_object('season', null, 'sets', '[]'::jsonb); end if;
  select island_level into v_level from public.island_states where user_id = v_uid;

  return jsonb_build_object(
    'season', jsonb_build_object('id', v_season.id, 'name', v_season.name,
                                 'ends_at_ms', game.epoch_ms(v_season.ends_at)),
    'card_dust', (select card_dust from public.profiles where id = v_uid),
    'card_packs', (select card_packs from public.inventory_rolls where user_id = v_uid),
    'sets', coalesce((
      select jsonb_agg(jsonb_build_object(
               'set_id', s.id,
               'name', s.name,
               'reward_rolls', s.reward_rolls,
               'reward_crystals', (game.roll_reward(v_level) * s.reward_crystal_rolls)::text,
               'completed', exists (select 1 from public.card_set_completions c
                                     where c.user_id = v_uid and c.set_id = s.id),
               'cards', coalesce((select jsonb_agg(jsonb_build_object(
                                  'card_id', d.id, 'name', d.name, 'rarity', d.rarity,
                                  'craft_cost', 50 * d.rarity,
                                  'owned', exists (select 1 from public.card_albums a
                                                    where a.user_id = v_uid and a.card_id = d.id))
                                order by d.rarity, d.id)
                           from public.card_definitions d where d.set_id = s.id), '[]'::jsonb))
             order by s.id)
        from public.card_sets s where s.season_id = v_season.id), '[]'::jsonb));
end $$;

-- -----------------------------------------------------------------------------
-- Store purchases (granted by the NestJS service after receipt verification)
-- -----------------------------------------------------------------------------

create table public.store_products (
  product_id text primary key,             -- same id in App Store Connect and Play Console
  rolls      integer not null default 0 check (rolls >= 0),
  card_packs integer not null default 0 check (card_packs >= 0),
  active     boolean not null default true
);

create table public.purchases (
  id                 uuid primary key default gen_random_uuid(),
  user_id            uuid not null references public.profiles (id) on delete cascade,
  store              text not null check (store in ('apple', 'google')),
  product_id         text not null references public.store_products (product_id),
  transaction_id     text not null,        -- Apple transactionId / Google orderId
  rolls_granted      integer not null,
  card_packs_granted integer not null,
  created_at         timestamptz not null default now(),
  unique (store, transaction_id)
);
create index purchases_user_idx on public.purchases (user_id, created_at desc);

-- Service role only. Idempotent per (store, transaction_id), so verification retries are safe.
create function public.grant_purchase(p_user_id uuid, p_store text, p_product_id text, p_transaction_id text)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_product public.store_products%rowtype;
  v_owner uuid;
begin
  select * into v_product from public.store_products where product_id = p_product_id and active;
  if not found then raise exception 'unknown_product'; end if;

  insert into public.purchases (user_id, store, product_id, transaction_id, rolls_granted, card_packs_granted)
  values (p_user_id, p_store, p_product_id, p_transaction_id, v_product.rolls, v_product.card_packs)
  on conflict (store, transaction_id) do nothing;

  if not found then
    select user_id into v_owner from public.purchases
     where store = p_store and transaction_id = p_transaction_id;
    if v_owner <> p_user_id then raise exception 'transaction_belongs_to_another_player'; end if;
    return game.player_state(p_user_id) || jsonb_build_object('duplicate', true);
  end if;

  update public.inventory_rolls
     set rolls = rolls + v_product.rolls, card_packs = card_packs + v_product.card_packs, updated_at = now()
   where user_id = p_user_id;
  return game.player_state(p_user_id) || jsonb_build_object('duplicate', false);
end $$;

-- -----------------------------------------------------------------------------
-- Push notifications: device tokens + a transactional outbox drained by NestJS
-- -----------------------------------------------------------------------------

create table public.push_tokens (
  token      text primary key,
  user_id    uuid not null references public.profiles (id) on delete cascade,
  platform   text not null check (platform in ('ios', 'android')),
  updated_at timestamptz not null default now()
);
create index push_tokens_user_idx on public.push_tokens (user_id);

create table public.notification_outbox (
  id         bigint generated always as identity primary key,
  user_id    uuid not null references public.profiles (id) on delete cascade,
  payload    jsonb not null,
  created_at timestamptz not null default now(),
  claimed_at timestamptz,
  attempts   smallint not null default 0,
  sent_at    timestamptz
);
create index notification_outbox_pending_idx on public.notification_outbox (created_at) where sent_at is null;

create function game.enqueue_raid_push() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  insert into public.notification_outbox (user_id, payload) values (new.defender_id, new.notification);
  return new;
end $$;

create trigger raid_history_enqueue_push
  after insert on public.raid_history
  for each row when (new.defender_id is not null)
  execute function game.enqueue_raid_push();

create function public.register_push_token(p_token text, p_platform text) returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
begin
  if p_platform not in ('ios', 'android') or coalesce(length(p_token), 0) not between 10 and 4096 then
    raise exception 'invalid_push_token' using errcode = '22023';
  end if;
  -- A device's token follows whoever is signed in on it.
  insert into public.push_tokens (token, user_id, platform) values (p_token, v_uid, p_platform)
  on conflict (token) do update set user_id = excluded.user_id, platform = excluded.platform, updated_at = now();
  -- Keep the 10 most recent devices per player.
  delete from public.push_tokens
   where user_id = v_uid
     and token not in (select token from public.push_tokens where user_id = v_uid
                        order by updated_at desc limit 10);
end $$;

create function public.unregister_push_token(p_token text) returns void
language sql security definer set search_path = '' as $$
  delete from public.push_tokens where token = p_token and user_id = auth.uid()
$$;

-- Service role only. Claims up to p_limit unsent notifications (rows claimed more than
-- 2 minutes ago are retried; after 5 attempts a row is left as a dead letter).
create function public.claim_push_batch(p_limit int default 100) returns jsonb
language sql security definer set search_path = '' as $$
  with claimed as (
    update public.notification_outbox o
       set claimed_at = now(), attempts = o.attempts + 1
     where o.id in (select id from public.notification_outbox
                     where sent_at is null and attempts < 5
                       and (claimed_at is null or claimed_at < now() - interval '2 minutes')
                     order by created_at
                     limit p_limit
                       for update skip locked)
    returning o.id, o.user_id, o.payload)
  select coalesce(jsonb_agg(jsonb_build_object(
           'id', c.id,
           'user_id', c.user_id,
           'payload', c.payload,
           'tokens', coalesce((select jsonb_agg(jsonb_build_object('token', t.token, 'platform', t.platform))
                                 from public.push_tokens t where t.user_id = c.user_id), '[]'::jsonb))),
         '[]'::jsonb)
    from claimed c
$$;

-- Service role only. Marks notifications delivered, drops tokens FCM reported dead,
-- and prunes week-old delivered rows.
create function public.complete_push_batch(p_ids bigint[], p_invalid_tokens text[] default '{}')
returns void
language plpgsql security definer set search_path = '' as $$
begin
  update public.notification_outbox set sent_at = now() where id = any (p_ids);
  delete from public.push_tokens where token = any (p_invalid_tokens);
  delete from public.notification_outbox where sent_at < now() - interval '7 days';
end $$;

-- -----------------------------------------------------------------------------
-- Guilds & guild chat (chat is delivered by Realtime INSERTs on guild_messages)
-- -----------------------------------------------------------------------------

create table public.guilds (
  id          uuid primary key default gen_random_uuid(),
  name        text not null check (char_length(name) between 3 and 24),
  tag         text not null check (tag ~ '^[A-Z0-9]{2,5}$'),
  description text not null default '' check (char_length(description) <= 200),
  is_open     boolean not null default true,  -- open: anyone may join; closed: invite-only (future)
  created_at  timestamptz not null default now()
);
create unique index guilds_name_key on public.guilds (lower(name));
create unique index guilds_tag_key on public.guilds (tag);

create table public.guild_members (
  user_id   uuid primary key references public.profiles (id) on delete cascade,  -- one guild per player
  guild_id  uuid not null references public.guilds (id) on delete cascade,
  role      text not null check (role in ('leader', 'officer', 'member')),
  joined_at timestamptz not null default now()
);
create index guild_members_guild_idx on public.guild_members (guild_id, joined_at);
create unique index guild_members_one_leader on public.guild_members (guild_id) where role = 'leader';

create table public.guild_messages (
  id          bigint generated always as identity primary key,
  guild_id    uuid not null references public.guilds (id) on delete cascade,
  user_id     uuid references public.profiles (id) on delete set null,  -- null: system message
  author_name text not null,
  body        text not null check (char_length(body) between 1 and 500),
  created_at  timestamptz not null default now()
);
create index guild_messages_guild_idx on public.guild_messages (guild_id, id desc);
create index guild_messages_rate_idx on public.guild_messages (user_id, created_at desc);

create function game.guild_system_message(p_guild uuid, p_body text) returns void
language sql set search_path = '' as $$
  insert into public.guild_messages (guild_id, user_id, author_name, body) values (p_guild, null, 'Guild', p_body)
$$;

-- The caller's membership, locked; raises if not in a guild.
create function game.my_membership(p_uid uuid) returns public.guild_members
language plpgsql set search_path = '' as $$
declare
  v_m public.guild_members%rowtype;
begin
  select * into v_m from public.guild_members where user_id = p_uid for update;
  if not found then raise exception 'not_in_guild'; end if;
  return v_m;
end $$;

create function public.list_guilds(p_search text default null) returns jsonb
language sql stable security definer set search_path = '' as $$
  select coalesce(jsonb_agg(g order by g.members desc, g.name), '[]'::jsonb)
    from (select gu.id, gu.name, gu.tag, gu.description,
                 (select count(*) from public.guild_members m where m.guild_id = gu.id) as members
            from public.guilds gu
           where gu.is_open
             and (p_search is null or gu.name ilike '%' || replace(replace(p_search, '%', ''), '_', '') || '%'
                  or gu.tag = upper(p_search))
           limit 50) g
   where g.members < 30
$$;

-- Guild screen: guild, roster and the latest 50 messages (oldest first). Null if guildless.
create function public.get_my_guild() returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_m public.guild_members%rowtype;
begin
  select * into v_m from public.guild_members where user_id = v_uid;
  if not found then return null; end if;
  return (
    select jsonb_build_object(
      'id', g.id, 'name', g.name, 'tag', g.tag, 'description', g.description, 'is_open', g.is_open,
      'my_role', v_m.role,
      'members', (select jsonb_agg(jsonb_build_object(
                           'user_id', m.user_id, 'display_name', p.display_name, 'role', m.role,
                           'island_level', i.island_level, 'last_active_ms', game.epoch_ms(p.last_active_at))
                         order by case m.role when 'leader' then 0 when 'officer' then 1 else 2 end, m.joined_at)
                    from public.guild_members m
                    join public.profiles p on p.id = m.user_id
                    join public.island_states i on i.user_id = m.user_id
                   where m.guild_id = g.id),
      'messages', coalesce((select jsonb_agg(to_jsonb(x) order by x.id)
                              from (select id, user_id, author_name, body, game.epoch_ms(created_at) as created_ms
                                      from public.guild_messages where guild_id = g.id
                                     order by id desc limit 50) x), '[]'::jsonb))
    from public.guilds g where g.id = v_m.guild_id);
end $$;

create function public.create_guild(p_name text, p_tag text, p_description text default '') returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_guild uuid;
begin
  if exists (select 1 from public.guild_members where user_id = v_uid) then
    raise exception 'already_in_guild';
  end if;
  if game.contains_banned_term(p_name) or game.contains_banned_term(p_tag) then
    raise exception 'name_not_allowed';
  end if;
  begin
    insert into public.guilds (name, tag, description)
    values (btrim(p_name), upper(btrim(p_tag)), coalesce(btrim(p_description), ''))
    returning id into v_guild;
  exception
    when unique_violation then raise exception 'guild_name_taken';
    when check_violation then raise exception 'invalid_guild_details';
  end;
  insert into public.guild_members (user_id, guild_id, role) values (v_uid, v_guild, 'leader');
  perform game.guild_system_message(v_guild,
    (select display_name from public.profiles where id = v_uid) || ' founded the guild.');
  return public.get_my_guild();
end $$;

create function public.join_guild(p_guild_id uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_open boolean;
begin
  -- Locking the guild row serialises joins, so the 30-member cap holds under concurrency.
  select is_open into v_open from public.guilds where id = p_guild_id for update;
  if v_open is null then raise exception 'guild_not_found'; end if;
  if not v_open then raise exception 'guild_closed'; end if;
  if (select count(*) from public.guild_members where guild_id = p_guild_id) >= 30 then
    raise exception 'guild_full';
  end if;
  begin
    insert into public.guild_members (user_id, guild_id, role) values (v_uid, p_guild_id, 'member');
  exception when unique_violation then
    raise exception 'already_in_guild';
  end;
  perform game.guild_system_message(p_guild_id,
    (select display_name from public.profiles where id = v_uid) || ' joined the guild.');
  return public.get_my_guild();
end $$;

-- Leaving hands leadership to the longest-serving officer (else member); the last one out
-- disbands the guild.
create function public.leave_guild() returns void
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_m public.guild_members%rowtype;
  v_heir uuid;
begin
  perform 1 from public.guilds g join public.guild_members m on m.guild_id = g.id
   where m.user_id = v_uid for update of g;
  v_m := game.my_membership(v_uid);
  delete from public.guild_members where user_id = v_uid;

  if v_m.role = 'leader' then
    select user_id into v_heir from public.guild_members
     where guild_id = v_m.guild_id
     order by case role when 'officer' then 0 else 1 end, joined_at
     limit 1;
    if v_heir is null then
      delete from public.guilds where id = v_m.guild_id;
      return;
    end if;
    update public.guild_members set role = 'leader' where user_id = v_heir;
  end if;
  perform game.guild_system_message(v_m.guild_id,
    (select display_name from public.profiles where id = v_uid) || ' left the guild.');
end $$;

-- Leader may kick anyone; officers may kick members.
create function public.kick_guild_member(p_user_id uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_me public.guild_members%rowtype := game.my_membership(v_uid);
  v_target public.guild_members%rowtype;
begin
  select * into v_target from public.guild_members
   where user_id = p_user_id and guild_id = v_me.guild_id for update;
  if not found or p_user_id = v_uid then raise exception 'member_not_found'; end if;
  if not (v_me.role = 'leader' or (v_me.role = 'officer' and v_target.role = 'member')) then
    raise exception 'insufficient_guild_role';
  end if;
  delete from public.guild_members where user_id = p_user_id;
  perform game.guild_system_message(v_me.guild_id,
    (select display_name from public.profiles where id = p_user_id) || ' was removed from the guild.');
  return public.get_my_guild();
end $$;

-- Leader only: promote/demote between officer and member, or hand over leadership.
create function public.set_guild_role(p_user_id uuid, p_role text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_me public.guild_members%rowtype := game.my_membership(v_uid);
begin
  if v_me.role <> 'leader' then raise exception 'insufficient_guild_role'; end if;
  if p_role not in ('leader', 'officer', 'member') or p_user_id = v_uid then
    raise exception 'invalid_role_change' using errcode = '22023';
  end if;
  if not exists (select 1 from public.guild_members where user_id = p_user_id and guild_id = v_me.guild_id) then
    raise exception 'member_not_found';
  end if;
  if p_role = 'leader' then
    update public.guild_members set role = 'officer' where user_id = v_uid;  -- frees the unique leader slot
  end if;
  update public.guild_members set role = p_role where user_id = p_user_id;
  return public.get_my_guild();
end $$;

-- 1–500 characters, at most 5 messages per 10 seconds per player.
create function public.send_guild_message(p_body text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_guild uuid;
  v_body text := btrim(p_body);
  v_row public.guild_messages%rowtype;
begin
  select guild_id into v_guild from public.guild_members where user_id = v_uid;
  if v_guild is null then raise exception 'not_in_guild'; end if;
  if coalesce(char_length(v_body), 0) not between 1 and 500 then
    raise exception 'invalid_message' using errcode = '22023';
  end if;
  if (select count(*) from public.guild_messages
       where user_id = v_uid and created_at > now() - interval '10 seconds') >= 5 then
    raise exception 'rate_limited';
  end if;
  insert into public.guild_messages (guild_id, user_id, author_name, body)
  values (v_guild, v_uid, (select display_name from public.profiles where id = v_uid), game.mask_banned_terms(v_body))
  returning * into v_row;
  return jsonb_build_object('id', v_row.id, 'user_id', v_row.user_id, 'author_name', v_row.author_name,
                            'body', v_row.body, 'created_ms', game.epoch_ms(v_row.created_at));
end $$;

-- -----------------------------------------------------------------------------
-- Moderation: display names, guild names, chat
-- -----------------------------------------------------------------------------

insert into public.banned_name_terms (term) values
  ('admin'), ('moderator'), ('official'), ('developer');  -- impersonation; extend with your own list

-- Matches ignoring case, spaces and punctuation ("A.d m-i n" contains "admin").
create function game.contains_banned_term(p_text text) returns boolean
language sql stable set search_path = '' as $$
  select exists (
    select 1 from public.banned_name_terms t
     where position(t.term in regexp_replace(lower(coalesce(p_text, '')), '[^[:alnum:]]', '', 'g')) > 0)
$$;

create function game.mask_banned_terms(p_text text) returns text
language plpgsql stable set search_path = '' as $$
declare
  v_text text := p_text;
  v_term text;
begin
  for v_term in select term from public.banned_name_terms loop
    v_text := regexp_replace(v_text, regexp_replace(v_term, '([.^$*+?(){}\[\]\\|-])', '\\\1', 'g'),
                             repeat('*', char_length(v_term)), 'gi');
  end loop;
  return v_text;
end $$;

-- 3–20 characters, whitespace collapsed. Letters of any script are allowed; markup, control
-- and symbol characters are not (a denylist, so it doesn't depend on the database locale).
create function public.set_display_name(p_name text) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_uid uuid := game.require_uid();
  v_name text := btrim(regexp_replace(coalesce(p_name, ''), '\s+', ' ', 'g'));
begin
  if char_length(v_name) not between 3 and 20 or v_name ~ '[[:cntrl:]<>"''`&;:{}\[\]\\/@#$%^*=+|~?!]' then
    raise exception 'invalid_display_name' using errcode = '22023';
  end if;
  if game.contains_banned_term(v_name) then raise exception 'name_not_allowed'; end if;
  update public.profiles set display_name = v_name where id = v_uid;
  return game.player_state(v_uid);
end $$;

-- -----------------------------------------------------------------------------
-- Live-ops (service role only; called by the NestJS admin API, which records the operator)
-- -----------------------------------------------------------------------------

-- Compensation / goodwill grant. Bounded so a fat-fingered request can't break the economy.
create function public.admin_grant(p_actor text, p_user_id uuid, p_rolls int, p_card_packs int,
                                   p_crystals numeric, p_reason text) returns jsonb
language plpgsql security definer set search_path = '' as $$
begin
  if coalesce(btrim(p_actor), '') = '' or coalesce(btrim(p_reason), '') = '' then
    raise exception 'actor_and_reason_required' using errcode = '22023';
  end if;
  if p_rolls not between 0 and 100000 or p_card_packs not between 0 and 1000 or p_crystals < 0
     or p_crystals <> trunc(p_crystals) then
    raise exception 'grant_out_of_bounds' using errcode = '22023';
  end if;

  update public.inventory_rolls
     set rolls = rolls + p_rolls, card_packs = card_packs + p_card_packs, updated_at = now()
   where user_id = p_user_id;
  if not found then raise exception 'player_not_found'; end if;
  update public.profiles set crystals = crystals + p_crystals where id = p_user_id;

  insert into public.admin_actions (actor, action, user_id, details)
  values (p_actor, 'grant', p_user_id,
          jsonb_build_object('rolls', p_rolls, 'card_packs', p_card_packs,
                             'crystals', p_crystals::text, 'reason', p_reason));
  return game.player_state(p_user_id);
end $$;

-- Players whose premium-outcome rate on >=10x rolls significantly exceeds their rate on
-- <=2x rolls (two-proportion z-test). Honest play, including timing the pity boost, stays
-- well under z = 4 at these sample sizes; outcome-peeking clients score far above it.
create function public.admin_multiplier_anomalies(p_min_rolls int default 200, p_min_z float8 default 4)
returns jsonb
language sql stable security definer set search_path = '' as $$
  select coalesce(jsonb_agg(to_jsonb(x) order by x.z desc), '[]'::jsonb)
    from (select a.user_id, p.display_name, a.hi_rolls, a.lo_rolls,
                 round((a.hi_premium::float8 / a.hi_rolls)::numeric, 4) as hi_premium_rate,
                 round((a.lo_premium::float8 / a.lo_rolls)::numeric, 4) as lo_premium_rate,
                 round(s.z::numeric, 2) as z
            from public.roll_audit a
            join public.profiles p on p.id = a.user_id
            cross join lateral (select (a.hi_premium + a.lo_premium)::float8 / (a.hi_rolls + a.lo_rolls) as pooled) q
            cross join lateral (
              select (a.hi_premium::float8 / a.hi_rolls - a.lo_premium::float8 / a.lo_rolls)
                     / nullif(sqrt(q.pooled * (1 - q.pooled) * (1.0 / a.hi_rolls + 1.0 / a.lo_rolls)), 0) as z) s
           where a.hi_rolls >= p_min_rolls and a.lo_rolls >= p_min_rolls and s.z >= p_min_z
           order by s.z desc
           limit 100) x
$$;

-- -----------------------------------------------------------------------------
-- Row-level security & privileges
-- -----------------------------------------------------------------------------

alter table public.profiles             enable row level security;
alter table public.island_states        enable row level security;
alter table public.inventory_rolls      enable row level security;
alter table public.raid_history         enable row level security;
alter table public.card_seasons         enable row level security;
alter table public.card_sets            enable row level security;
alter table public.card_definitions     enable row level security;
alter table public.card_albums          enable row level security;
alter table public.card_set_completions enable row level security;
alter table public.store_products       enable row level security;
alter table public.purchases            enable row level security;
alter table public.push_tokens          enable row level security;
alter table public.notification_outbox  enable row level security;  -- no policies: service role only
alter table public.guilds               enable row level security;
alter table public.guild_members        enable row level security;
alter table public.guild_messages       enable row level security;
alter table public.roll_audit           enable row level security;  -- no policies: service role only
alter table public.admin_actions        enable row level security;  -- no policies: service role only
alter table public.banned_name_terms    enable row level security;  -- no policies: service role only

-- Supabase grants ALL to anon/authenticated by default; take it back to read-only.
revoke all on public.profiles, public.island_states, public.inventory_rolls, public.raid_history,
              public.card_seasons, public.card_sets, public.card_definitions,
              public.card_albums, public.card_set_completions,
              public.store_products, public.purchases, public.push_tokens, public.notification_outbox,
              public.guilds, public.guild_members, public.guild_messages,
              public.roll_audit, public.admin_actions, public.banned_name_terms
  from anon, authenticated;
grant select on public.profiles, public.island_states, public.inventory_rolls, public.raid_history,
                public.card_seasons, public.card_sets, public.card_definitions,
                public.card_albums, public.card_set_completions,
                public.store_products, public.purchases, public.push_tokens,
                public.guilds, public.guild_members, public.guild_messages
  to authenticated;

create policy "profiles: read own" on public.profiles
  for select to authenticated using (id = (select auth.uid()));
create policy "island_states: read own" on public.island_states
  for select to authenticated using (user_id = (select auth.uid()));
create policy "inventory_rolls: read own" on public.inventory_rolls
  for select to authenticated using (user_id = (select auth.uid()));
create policy "raid_history: read own raids" on public.raid_history
  for select to authenticated
  using (attacker_id = (select auth.uid()) or defender_id = (select auth.uid()));
create policy "card_albums: read own" on public.card_albums
  for select to authenticated using (user_id = (select auth.uid()));
create policy "card_set_completions: read own" on public.card_set_completions
  for select to authenticated using (user_id = (select auth.uid()));
create policy "card_seasons: catalog" on public.card_seasons
  for select to authenticated using (true);
create policy "card_sets: catalog" on public.card_sets
  for select to authenticated using (true);
create policy "card_definitions: catalog" on public.card_definitions
  for select to authenticated using (true);
create policy "store_products: catalog" on public.store_products
  for select to authenticated using (active);
create policy "purchases: read own" on public.purchases
  for select to authenticated using (user_id = (select auth.uid()));
create policy "push_tokens: read own" on public.push_tokens
  for select to authenticated using (user_id = (select auth.uid()));
create policy "guilds: browse" on public.guilds
  for select to authenticated using (true);
create policy "guild_members: read own membership" on public.guild_members
  for select to authenticated using (user_id = (select auth.uid()));
-- Also gates Realtime: a player only receives chat for the guild they are currently in.
create policy "guild_messages: read own guild" on public.guild_messages
  for select to authenticated
  using (guild_id = (select m.guild_id from public.guild_members m where m.user_id = (select auth.uid())));

revoke execute on all functions in schema game from public;
revoke execute on function
  public.get_player_state(), public.sync_rolls(jsonb), public.upgrade_structure(int),
  public.repair_structure(int), public.perform_raid(uuid), public.list_revenge_targets(),
  public.open_card_pack(), public.craft_card(int), public.get_card_album(),
  public.register_push_token(text, text), public.unregister_push_token(text),
  public.grant_purchase(uuid, text, text, text), public.claim_push_batch(int),
  public.complete_push_batch(bigint[], text[]),
  public.list_guilds(text), public.get_my_guild(), public.create_guild(text, text, text),
  public.join_guild(uuid), public.leave_guild(), public.kick_guild_member(uuid),
  public.set_guild_role(uuid, text), public.send_guild_message(text), public.set_display_name(text),
  public.admin_grant(text, uuid, int, int, numeric, text), public.admin_multiplier_anomalies(int, float8)
  from public, anon, authenticated;
grant execute on function
  public.get_player_state(), public.sync_rolls(jsonb), public.upgrade_structure(int),
  public.repair_structure(int), public.perform_raid(uuid), public.list_revenge_targets(),
  public.open_card_pack(), public.craft_card(int), public.get_card_album(),
  public.register_push_token(text, text), public.unregister_push_token(text),
  public.list_guilds(text), public.get_my_guild(), public.create_guild(text, text, text),
  public.join_guild(uuid), public.leave_guild(), public.kick_guild_member(uuid),
  public.set_guild_role(uuid, text), public.send_guild_message(text), public.set_display_name(text)
  to authenticated;
-- Called by the NestJS service with the service-role key.
grant execute on function
  public.grant_purchase(uuid, text, text, text), public.claim_push_batch(int),
  public.complete_push_batch(bigint[], text[]),
  public.admin_grant(text, uuid, int, int, numeric, text), public.admin_multiplier_anomalies(int, float8)
  to service_role;

-- -----------------------------------------------------------------------------
-- Realtime: defenders subscribe to raid_history INSERTs (filter defender_id), guild
-- members to guild_messages INSERTs (filter guild_id). RLS applies to both.
-- -----------------------------------------------------------------------------
do $$
begin
  if exists (select 1 from pg_publication where pubname = 'supabase_realtime') then
    alter publication supabase_realtime add table public.raid_history, public.guild_messages;
  end if;
end $$;
