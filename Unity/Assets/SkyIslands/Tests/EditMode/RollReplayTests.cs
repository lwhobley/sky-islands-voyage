using System;
using System.Globalization;
using System.Numerics;
using NUnit.Framework;
using SkyIslands.Core;

namespace SkyIslands.Tests
{
    /// <summary>
    /// The client's offline roll loop must land in exactly the state public.sync_rolls computed
    /// for the same batch — otherwise players would see rewards the server later takes back.
    /// </summary>
    public class RollReplayTests
    {
        [Test]
        public void OfflineRollsReproduceTheServerState()
        {
            var s = new EconomyState
            {
                RngSeed = GoldenVectors.ReplaySeed,
                IslandLevel = GoldenVectors.ReplayLevel,
                Rolls = GoldenVectors.ReplayStartRolls,
                RegenAnchorMs = GoldenVectors.ReplayT0Ms,
                StateAtMs = GoldenVectors.ReplayT0Ms,
            };

            int accepted = 0;
            var presentation = new Random(1);
            foreach (var (i, m, t) in GoldenVectors.ReplayBatch)
            {
                if (i != s.RollIndex || !GameEconomyManager.TryApplyRoll(s, m, t, presentation, 0.3f, out var result)) break;
                Assert.AreEqual(i, result.Index);
                accepted++;
            }
            // The server finishes every sync by regenerating up to its own clock.
            (s.Rolls, s.RegenAnchorMs) = EconomyRules.Regen(s.Rolls, s.RegenAnchorMs, GoldenVectors.ReplayStateAtMs);

            Assert.AreEqual(GoldenVectors.ReplayAccepted, accepted, "accepted rolls (the rest ran out of budget)");
            Assert.AreEqual(BigInteger.Parse(GoldenVectors.ReplayCrystals, CultureInfo.InvariantCulture), s.Crystals, "crystals");
            Assert.AreEqual(GoldenVectors.ReplayRolls, s.Rolls, "rolls");
            Assert.AreEqual(GoldenVectors.ReplayRegenAnchorMs, s.RegenAnchorMs, "regen anchor");
            Assert.AreEqual(GoldenVectors.ReplayRollIndex, s.RollIndex, "roll index");
            Assert.AreEqual(GoldenVectors.ReplayDryStreak, s.DryStreak, "dry streak");
            Assert.AreEqual(GoldenVectors.ReplayShields, s.Shields, "shields");
            Assert.AreEqual(GoldenVectors.ReplayCardPacks, s.CardPacks, "card packs");
            CollectionAssert.AreEqual(GoldenVectors.ReplayPendingAttacks, s.PendingAttacks, "pending Wind-Cannon multipliers");
        }

        [Test]
        public void RollsBeyondTheBudgetAreRefusedWithoutSideEffects()
        {
            var s = new EconomyState { RngSeed = "budget", Rolls = 4, RegenAnchorMs = 0, StateAtMs = 0 };
            Assert.IsFalse(GameEconomyManager.TryApplyRoll(s, 5, 1, new Random(1), 0f, out _));
            Assert.AreEqual(4, s.Rolls);
            Assert.AreEqual(0, s.RollIndex);
            Assert.AreEqual(BigInteger.Zero, s.Crystals);
        }

        [Test]
        public void ClockRollbackCannotEarnExtraRegen()
        {
            // A roll stamped before the last evaluation is treated as happening at that time.
            var s = new EconomyState { RngSeed = "clock", Rolls = 10, RegenAnchorMs = 1_000_000, StateAtMs = 5_000_000 };
            GameEconomyManager.TryApplyRoll(s, 1, 0, new Random(1), 0f, out _);
            Assert.AreEqual(5_000_000, s.StateAtMs);
        }
    }
}
