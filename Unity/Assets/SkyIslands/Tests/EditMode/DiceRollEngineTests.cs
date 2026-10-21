using System;
using NUnit.Framework;
using SkyIslands.Core;

namespace SkyIslands.Tests
{
    public class DiceRollEngineTests
    {
        [Test]
        public void HashMatchesServer()
        {
            foreach (var v in GoldenVectors.Rng)
                Assert.AreEqual(v.u32, DiceRollEngine.HashU32(GoldenVectors.RngSeed, v.index), $"roll_u32 at index {v.index}");
        }

        [Test]
        public void OutcomesMatchServer()
        {
            foreach (var v in GoldenVectors.Rng)
            {
                Assert.AreEqual((RollOutcome)v.outcome, DiceRollEngine.ResolveOutcome(GoldenVectors.RngSeed, v.index, false),
                                $"pick_outcome at index {v.index}");
                Assert.AreEqual((RollOutcome)v.pityOutcome, DiceRollEngine.ResolveOutcome(GoldenVectors.RngSeed, v.index, true),
                                $"pick_outcome with pity at index {v.index}");
            }
        }

        [TestCase(false, new[] { 0.40, 0.25, 0.15, 0.12, 0.08 })]
        [TestCase(true, new[] { 400 / 1080.0, 300 / 1080.0, 180 / 1080.0, 120 / 1080.0, 80 / 1080.0 })]
        public void DistributionMatchesDropTable(bool pity, double[] expected)
        {
            const int n = 100_000;
            var counts = new int[5];
            for (long i = 0; i < n; i++) counts[(int)DiceRollEngine.ResolveOutcome("distribution", i, pity)]++;
            for (int k = 0; k < 5; k++)
                Assert.AreEqual(expected[k], counts[k] / (double)n, 0.01, $"{(RollOutcome)k} share");
        }

        [Test]
        public void PityStartsAfterFifteenDryRolls()
        {
            Assert.IsFalse(DiceRollEngine.IsPityActive(14));
            Assert.IsTrue(DiceRollEngine.IsPityActive(15));
        }

        [Test]
        public void DryStreakResetsOnlyOnCannonOrShield()
        {
            Assert.AreEqual(0, DiceRollEngine.NextDryStreak(9, RollOutcome.WindCannon));
            Assert.AreEqual(0, DiceRollEngine.NextDryStreak(9, RollOutcome.CloudShield));
            Assert.AreEqual(10, DiceRollEngine.NextDryStreak(9, RollOutcome.Crystals));
            Assert.AreEqual(10, DiceRollEngine.NextDryStreak(9, RollOutcome.StormSpirit));
            Assert.AreEqual(10, DiceRollEngine.NextDryStreak(9, RollOutcome.AetherBattery));
            Assert.AreEqual(1000, DiceRollEngine.NextDryStreak(1000, RollOutcome.Crystals), "capped like the server");
        }

        [Test]
        public void FacesAreCosmeticOnly()
        {
            var rng = new Random(7);
            foreach (RollOutcome outcome in Enum.GetValues(typeof(RollOutcome)))
            {
                for (int k = 0; k < 500; k++)
                {
                    var faces = DiceRollEngine.BuildFaces(outcome, rng, 1f, out bool nearMiss);
                    Assert.AreEqual(3, faces.Length);
                    if (outcome != RollOutcome.Crystals)
                    {
                        Assert.IsFalse(nearMiss, "only Crystal results are ever shown as a near miss");
                        CollectionAssert.AreEqual(new[] { outcome, outcome, outcome }, faces);
                    }
                    else
                    {
                        Assert.IsTrue(nearMiss);
                        Assert.AreEqual(faces[0], faces[1]);
                        Assert.AreNotEqual(RollOutcome.Crystals, faces[0]);
                        Assert.AreEqual(RollOutcome.Crystals, faces[2], "the last face shows the real result");
                    }
                }
            }

            DiceRollEngine.BuildFaces(RollOutcome.Crystals, rng, 0f, out bool disabled);
            Assert.IsFalse(disabled, "nearMissChance 0 disables the effect");
        }
    }
}
