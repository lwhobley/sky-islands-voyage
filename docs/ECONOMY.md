# Economy pacing

Numbers from `tools/economy-sim/simulate.py` (300 simulated players per row, fixed seed,
the exact rules from `schema.sql`). Run it yourself:

```bash
python tools/economy-sim/simulate.py --target-shield-rate 0.4 --incoming-raids-per-day 3
```

Assumes 25% of raid targets are shielded, no incoming raids, and players spend every crystal
on the current island. Card packs, set rewards and purchases are not modelled; they only
add income.

## What the rules produce today

| Player | Island 10 | Island 50 | Island 100 | Island 200 |
|---|---|---|---|---|
| Collects every regen tick (max pace) | 1.0 days | 5.6 | 11.3 | **22.9** |
| Opens the app every 4h | 1.0 | 5.7 | 11.5 | 23.0 |
| Opens the app 3x a day | 2.0 | 10.7 | 22.0 | 44.0 |
| Opens the app once a day | 6.0 | 32.0 | 66.0 | 132.0 |

- **Pacing is flat by construction.** Island cost and roll reward both grow as 1.2^(n-1), so
  every island costs about 100 roll rewards: a median of 39 rolls (p10 29, p90 51, max ~89),
  about 3.3 hours of regeneration, from island 1 to island 200. The unit tests
  (`EveryIslandCostsTheSameNumberOfRolls`, `TierPricesAddUpToTheIslandCost`) keep it that way.
- **Raids are about 79% of income.** The most common outcome, Crystals (40% of rolls), pays
  only about 16%. A Wind-Cannon (25% of rolls) pays 10x a roll reward. Overflow shield rolls
  after the 3-shield cap are most of the rest.
- **Raiding never runs dry.** When no fair human target exists the cannon hits an NPC outpost
  that pays the full amount, so solo players get the same raid income as social ones.
- **Raids create crystals.** The attacker is paid the full loot even when the defender holds
  less (the defender only loses what they had). That is inflation-neutral here, since costs
  scale with the same curve, but it is why raiding can outweigh everything else.

## Levers, if you want a different feel

Changing any of these means editing `supabase/schema.sql` and `EconomyRules` in
`GameEconomyManager.cs` together, then regenerating the golden vectors (README, Tests).

| Goal | Lever | Effect |
|---|---|---|
| Longer campaign | `BaseCost` (25,000) | Campaign length scales about linearly with it. 3x gives about 69 days at max pace and about 130 days at 3 opens a day. |
| Make Crystal rolls matter more vs raids | Raid loot multiplier (10x a roll reward) | From the formula: raid share of income is about 83% at 10x, 67% at 4x, 50% at 2x. Lower income also slows pacing, so raise `BaseCost` or `roll reward` to compensate. |
| Slower early game / more sessions | Regen interval (5 min) or cap (50) | Cap 50 means an idle player loses regen after about 4 hours. |
| Fewer wasted shield rolls | Shield cap (3) or overflow payout | Overflow is about 6% of income today. |

These are balance choices, not bugs. Nothing here is changed yet.
