using System;
using System.Globalization;
using System.Numerics;
using NUnit.Framework;
using SkyIslands.Core;

namespace SkyIslands.Tests
{
    public class EconomyRulesTests
    {
        static BigInteger Big(string s) => BigInteger.Parse(s, CultureInfo.InvariantCulture);

        [Test]
        public void CostCurveStartsAtBaseCost()
        {
            Assert.AreEqual(new BigInteger(25_000), EconomyRules.IslandCost(1));
            Assert.AreEqual(new BigInteger(30_000), EconomyRules.IslandCost(2));
            Assert.AreEqual(new BigInteger(36_000), EconomyRules.IslandCost(3));
            Assert.AreEqual(Big("142892018831855232798"), EconomyRules.IslandCost(200), "beyond 64-bit at the top");
        }

        [Test]
        public void LevelCurveMatchesServer()
        {
            foreach (var v in GoldenVectors.Levels)
            {
                Assert.AreEqual(Big(v.islandCost), EconomyRules.IslandCost(v.level), $"island_cost({v.level})");
                Assert.AreEqual(Big(v.rollReward), EconomyRules.RollReward(v.level), $"roll_reward({v.level})");
                Assert.AreEqual(Big(v.loot50Shielded), EconomyRules.RaidLoot(v.level, 50, true), $"raid_loot({v.level}, 50, shielded)");
                Assert.AreEqual(Big(v.loot20), EconomyRules.RaidLoot(v.level, 20, false), $"raid_loot({v.level}, 20)");
            }
        }

        [Test]
        public void UpgradeAndRepairCostsMatchServer()
        {
            foreach (var v in GoldenVectors.Upgrades)
            {
                Assert.AreEqual(Big(v.upgrade), EconomyRules.UpgradeCost(v.level, v.slot, v.tier), $"upgrade_cost({v.level},{v.slot},{v.tier})");
                Assert.AreEqual(Big(v.repair), EconomyRules.RepairCost(v.level, v.slot, v.tier), $"repair_cost({v.level},{v.slot},{v.tier})");
            }
        }

        [Test]
        public void RegenMatchesServer()
        {
            foreach (var v in GoldenVectors.Regen)
            {
                var (rolls, anchor) = EconomyRules.Regen(v.rolls, v.anchor, v.at);
                Assert.AreEqual(v.expectedRolls, rolls, $"regen({v.rolls}, +{v.at - v.anchor}ms) rolls");
                Assert.AreEqual(v.expectedAnchor, anchor, $"regen({v.rolls}, +{v.at - v.anchor}ms) anchor");
            }
        }

        [Test]
        public void RegenIsPathIndependent()
        {
            long t0 = 1_760_000_000_000;
            var direct = EconomyRules.Regen(3, t0, t0 + 7_777_777);
            var (r, a) = EconomyRules.Regen(3, t0, t0 + 1_234_567);
            var stepped = EconomyRules.Regen(r, a, t0 + 7_777_777);
            Assert.AreEqual(direct, stepped);
        }

        [Test]
        public void OnlyListedMultipliersAreValid()
        {
            foreach (int m in new[] { 1, 2, 5, 10, 20, 50 }) Assert.IsTrue(EconomyRules.IsValidMultiplier(m));
            foreach (int m in new[] { 0, 3, 4, 100, -1 }) Assert.IsFalse(EconomyRules.IsValidMultiplier(m));
        }

        [Test]
        public void EveryIslandCostsTheSameNumberOfRolls()
        {
            // Island cost and roll reward grow on the same 1.2^(n-1) curve, so pacing stays flat
            // from island 1 to 200. (Expressed in thousandths of a roll reward: 100.000 +/- 0.1.)
            for (int level = 1; level <= EconomyRules.IslandCount; level++)
            {
                BigInteger ratio = EconomyRules.IslandCost(level) * 1000 / EconomyRules.RollReward(level);
                Assert.That((long)ratio, Is.InRange(99_900L, 100_100L), $"island {level}");
            }
        }

        [Test]
        public void TierPricesAddUpToTheIslandCost()
        {
            for (int level = 1; level <= EconomyRules.IslandCount; level++)
            {
                BigInteger sum = BigInteger.Zero;
                for (int slot = 0; slot < EconomyRules.StructuresPerIsland; slot++)
                    for (int tier = 1; tier <= EconomyRules.MaxTier; tier++)
                        sum += EconomyRules.UpgradeCost(level, slot, tier);
                BigInteger cost = EconomyRules.IslandCost(level);
                Assert.LessOrEqual(sum, cost, $"island {level}: tiers never cost more than Cost(n)");
                Assert.LessOrEqual(cost - sum, new BigInteger(20), $"island {level}: at most 1 crystal lost to flooring per tier");
            }
        }

        [Test]
        public void RejectsLevelsOutsideTheCampaign()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => EconomyRules.IslandCost(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => EconomyRules.RollReward(201));
        }
    }
}
