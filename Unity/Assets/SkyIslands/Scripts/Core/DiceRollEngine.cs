using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SkyIslands.Core
{
    /// <summary>Outcome codes are shared with the server (game.pick_outcome) — do not reorder.</summary>
    public enum RollOutcome
    {
        Crystals = 0,
        WindCannon = 1,
        CloudShield = 2,
        StormSpirit = 3,
        AetherBattery = 4,
    }

    public readonly struct RollResult
    {
        public RollResult(long index, int multiplier, RollOutcome outcome, BigInteger crystalsAwarded,
                          bool pityActive, RollOutcome[] faces, bool isNearMiss)
        {
            Index = index;
            Multiplier = multiplier;
            Outcome = outcome;
            CrystalsAwarded = crystalsAwarded;
            PityActive = pityActive;
            Faces = faces;
            IsNearMiss = isNearMiss;
        }

        public long Index { get; }
        public int Multiplier { get; }
        public RollOutcome Outcome { get; }
        public BigInteger CrystalsAwarded { get; }
        public bool PityActive { get; }

        /// <summary>The three symbols to land on screen. Presentation only.</summary>
        public RollOutcome[] Faces { get; }

        public bool IsNearMiss { get; }
    }

    /// <summary>
    /// Probability matrix for the Aether Roll.
    ///
    /// Outcome resolution is deterministic — SHA-256("&lt;seed&gt;:&lt;index&gt;") picks from integer
    /// weights — so the server can replay offline rolls and verify them. It MUST match
    /// game.roll_u32 / game.pick_outcome in supabase/schema.sql exactly.
    ///
    /// Face layout (including near-miss splits) is cosmetic and never changes the reward.
    /// </summary>
    public static class DiceRollEngine
    {
        public const int PityThreshold = 15;     // consecutive dry rolls before the boost
        public const int PityBoostPercent = 20;  // attack & shield weights x1.2 while active

        // Crystals 40%, Wind-Cannon 25%, Cloud-Shield 15%, Storm Spirit 12%, Aether Battery 8% (x10).
        static readonly int[] BaseWeights = { 400, 250, 150, 120, 80 };
        static readonly int[] PityWeights = BuildPityWeights();

        // Symbols teased on the first two faces of a near miss.
        static readonly RollOutcome[] NearMissTeasers =
        {
            RollOutcome.WindCannon, RollOutcome.StormSpirit, RollOutcome.AetherBattery, RollOutcome.AetherBattery,
        };

        /// <summary>A roll is "dry" unless it lands a Wind-Cannon or Cloud-Shield.</summary>
        public static bool IsPityActive(int dryStreak) => dryStreak >= PityThreshold;

        public static int NextDryStreak(int dryStreak, RollOutcome outcome) =>
            outcome == RollOutcome.WindCannon || outcome == RollOutcome.CloudShield
                ? 0
                : Math.Min(dryStreak + 1, 1000);

        public static RollOutcome ResolveOutcome(string seed, long index, bool pityActive)
        {
            int[] weights = pityActive ? PityWeights : BaseWeights;
            int total = 0;
            foreach (int w in weights) total += w;

            ulong r = ((ulong)HashU32(seed, index) * (ulong)total) >> 32;
            int acc = 0;
            for (int k = 0; k < weights.Length; k++)
            {
                acc += weights[k];
                if (r < (ulong)acc) return (RollOutcome)k;
            }
            return RollOutcome.Crystals;  // unreachable
        }

        /// <summary>Big-endian uint32 from the first 4 bytes of SHA-256("seed:index").</summary>
        public static uint HashU32(string seed, long index)
        {
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(seed + ":" + index.ToString(CultureInfo.InvariantCulture)));
            return ((uint)h[0] << 24) | ((uint)h[1] << 16) | ((uint)h[2] << 8) | h[3];
        }

        /// <summary>
        /// Lays out the three faces. Winning outcomes show a triple. A Crystals result is,
        /// with <paramref name="nearMissChance"/>, shown as a split: two matching premium
        /// symbols and a crystal on the last face.
        /// </summary>
        public static RollOutcome[] BuildFaces(RollOutcome outcome, Random rng, float nearMissChance, out bool isNearMiss)
        {
            isNearMiss = outcome == RollOutcome.Crystals && rng.NextDouble() < nearMissChance;
            if (!isNearMiss) return new[] { outcome, outcome, outcome };

            RollOutcome tease = NearMissTeasers[rng.Next(NearMissTeasers.Length)];
            return new[] { tease, tease, RollOutcome.Crystals };
        }

        static int[] BuildPityWeights()
        {
            var w = (int[])BaseWeights.Clone();
            w[(int)RollOutcome.WindCannon] = w[(int)RollOutcome.WindCannon] * (100 + PityBoostPercent) / 100;
            w[(int)RollOutcome.CloudShield] = w[(int)RollOutcome.CloudShield] * (100 + PityBoostPercent) / 100;
            return w;
        }
    }
}
