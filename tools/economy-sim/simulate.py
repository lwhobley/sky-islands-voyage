#!/usr/bin/env python3
"""Monte Carlo pacing simulator for Sky Islands: Voyage (Python 3, standard library only).

Uses the same integer rules as supabase/schema.sql and EconomyRules in GameEconomyManager.cs:
cost curve 1.2^(n-1), roll regeneration (1 per 5 min, cap 50), the 40/25/15/12/8 drop table
with the pity boost, raid loot, shields and Aether Batteries. Players always spend every
crystal on the current island, so an island completes as soon as the sum of its 20 tier
prices has been earned.

Assumptions you can change with flags: how often the player opens the app, how often a
raid target is shielded (NPC outposts: 25%), and incoming raids that consume the player's
shields. Card packs, card-set rewards and purchases are not modelled (they only add income).

  python tools/economy-sim/simulate.py              # default report
  python tools/economy-sim/simulate.py --players 500 --target-shield-rate 0.4
"""
import argparse
import random
import statistics

ISLANDS = 200
MAX_ROLLS = 50
REGEN_S = 300
SHIELD_CAP = 3
SLOT_PCT = (14, 17, 20, 23, 26)
TIER_PCT = (15, 20, 25, 40)
BASE_W = (400, 250, 150, 120, 80)    # crystals, cannon, shield, storm spirit, battery
PITY_W = (400, 300, 180, 120, 80)


def pow65(n):
    return 6 ** (n - 1), 5 ** (n - 1)


def island_total(level):
    """Sum of the 20 per-tier prices (what the player really pays; slightly below Cost(n))."""
    a, b = pow65(level)
    return sum(25000 * a * s * t // (b * 10000) for s in SLOT_PCT for t in TIER_PCT)


def roll_reward(level):
    a, b = pow65(level)
    return 250 * a // b


TOTALS = [0] + [island_total(n) for n in range(1, ISLANDS + 1)]
REWARDS = [0] + [roll_reward(n) for n in range(1, ISLANDS + 1)]


class Player:
    def __init__(self, rng, shield_rate, incoming_raids_per_day):
        self.rng, self.shield_rate = rng, shield_rate
        self.incoming_per_s = incoming_raids_per_day / 86400
        self.level, self.progress = 1, 0
        self.rolls, self.dry, self.shields = MAX_ROLLS, 0, 0
        self.income = {"crystals": 0, "raids": 0, "overflow": 0}
        self.rolls_on_level = 0
        self.level_rolls = []          # rolls spent per completed island
        self.level_done_at = []        # simulated seconds at each completion

    def credit(self, kind, amount, now):
        self.income[kind] += amount
        self.progress += amount
        while self.level <= ISLANDS and self.progress >= TOTALS[self.level]:
            self.progress -= TOTALS[self.level]
            self.level_rolls.append(self.rolls_on_level)
            self.level_done_at.append(now)
            self.rolls_on_level = 0
            self.level += 1

    def roll_once(self, now):
        """Spend one roll at 1x (every outcome scales linearly with the multiplier)."""
        w = PITY_W if self.dry >= 15 else BASE_W
        r = self.rng.random() * sum(w)
        o = 0
        while o < 4 and r >= w[o]:
            r -= w[o]
            o += 1
        self.rolls -= 1
        self.rolls_on_level += 1
        reward = REWARDS[min(self.level, ISLANDS)]
        if o == 0:
            self.credit("crystals", reward, now)
        elif o == 1:
            loot = reward * 10
            self.credit("raids", loot // 5 if self.rng.random() < self.shield_rate else loot, now)
        elif o == 2:
            if self.shields < SHIELD_CAP:
                self.shields += 1
            else:
                self.credit("overflow", reward, now)
        elif o == 4:
            self.rolls += 2
        self.dry = 0 if o in (1, 2) else min(self.dry + 1, 1000)

    def absorb_raids(self, elapsed_s):
        """Incoming raids use up shields (Poisson arrivals)."""
        expected = elapsed_s * self.incoming_per_s
        n = int(expected) + (1 if self.rng.random() < expected - int(expected) else 0)
        self.shields = max(0, self.shields - n)


def run(strategy_hours, rng, shield_rate, incoming, horizon_days):
    """strategy_hours: hours between app opens; 0 = collects every regen tick (maximum pace)."""
    p = Player(rng, shield_rate, incoming)
    now, horizon = 0, horizon_days * 86400
    if strategy_hours == 0:
        while p.level <= ISLANDS and now < horizon:
            while p.rolls > 0 and p.level <= ISLANDS:
                p.roll_once(now)
            if p.level > ISLANDS:
                break
            now += REGEN_S
            p.rolls += 1
            p.absorb_raids(REGEN_S)
    else:
        step = int(strategy_hours * 3600)
        while p.level <= ISLANDS and now < horizon:
            while p.rolls > 0 and p.level <= ISLANDS:
                p.roll_once(now)
            now += step
            p.absorb_raids(step)
            p.rolls = min(MAX_ROLLS, p.rolls + step // REGEN_S)  # capped while away
    return p


def pct(values, q):
    values = sorted(values)
    return values[min(len(values) - 1, int(q * len(values)))]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--players", type=int, default=300)
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--target-shield-rate", type=float, default=0.25, help="chance a raid target is shielded")
    ap.add_argument("--incoming-raids-per-day", type=float, default=0.0)
    ap.add_argument("--horizon-days", type=int, default=400)
    args = ap.parse_args()

    print(f"Island cost / roll reward: level 1 = {TOTALS[1]}/{REWARDS[1]}, level 200 = {TOTALS[200]}/{REWARDS[200]}")
    ratios = [TOTALS[n] / REWARDS[n] for n in range(1, ISLANDS + 1)]
    print(f"  crystals-per-island expressed in 1x roll rewards: min {min(ratios):.2f}, max {max(ratios):.2f}")
    print(f"Assumptions: {args.target_shield_rate:.0%} of raid targets shielded, "
          f"{args.incoming_raids_per_day} incoming raids/day, {args.players} simulated players each\n")

    strategies = [
        ("collects every regen (max pace)", 0),
        ("opens app every 4h", 4),
        ("opens app every 8h (3x/day)", 8),
        ("opens app once a day", 24),
    ]
    for label, hours in strategies:
        rng = random.Random(args.seed)
        players = [run(hours, rng, args.target_shield_rate, args.incoming_raids_per_day, args.horizon_days)
                   for _ in range(args.players)]
        print(f"== {label}")
        for milestone in (10, 50, 100, 200):
            days = [p.level_done_at[milestone - 1] / 86400 for p in players if len(p.level_done_at) >= milestone]
            if len(days) < len(players):
                print(f"   island {milestone:>3}: {len(players) - len(days)} players did not finish within the horizon")
            if days:
                print(f"   island {milestone:>3} done after {statistics.median(days):6.1f} days "
                      f"(p10 {pct(days, .1):.1f}, p90 {pct(days, .9):.1f})")
        rolls = [r for p in players for r in p.level_rolls]
        print(f"   rolls per island: median {statistics.median(rolls):.0f}, p10 {pct(rolls, .1)}, p90 {pct(rolls, .9)}, max {max(rolls)}")
        income = {k: sum(p.income[k] for p in players) for k in players[0].income}
        total = sum(income.values())
        print("   income share: " + ", ".join(f"{k} {v / total:.0%}" for k, v in income.items()) + "\n")


if __name__ == "__main__":
    main()
