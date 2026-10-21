using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SkyIslands.Backend;
using UnityEngine;

namespace SkyIslands.Core
{
    /// <summary>
    /// Server-mirrored economy rules. Every number here has a twin in supabase/schema.sql
    /// (game.* functions): change both together, or offline rolls will be re-priced on sync.
    /// </summary>
    public static class EconomyRules
    {
        public const int IslandCount = 200;
        public const int StructuresPerIsland = 5;
        public const int MaxTier = 4;
        public const int MaxShields = 3;
        public const int MaxRolls = 50;
        public const long RegenIntervalMs = 5 * 60 * 1000;
        public const int BatteryRollsPerMultiplier = 2;
        public const int RaidLootInRollRewards = 10;  // raid loot = 10 x roll reward x multiplier
        public const int ShieldedLootDivisor = 5;     // a Cloud-Shield blocks 80%

        public static readonly int[] Multipliers = { 1, 2, 5, 10, 20, 50 };

        const int BaseIslandCost = 25_000;
        const int BaseRollReward = 250;
        static readonly int[] SlotWeightPct = { 14, 17, 20, 23, 26 };
        static readonly int[] TierWeightPct = { 15, 20, 25, 40 };

        // 1.2^(n-1) == 6^(n-1) / 5^(n-1): integer math keeps client and server prices identical.
        static readonly BigInteger[] GrowthNum = Powers(6);
        static readonly BigInteger[] GrowthDen = Powers(5);

        /// <summary>Cost(n) = BaseCost x 1.2^(n-1): crystals to fully build island n.</summary>
        public static BigInteger IslandCost(int level) =>
            BaseIslandCost * GrowthNum[Level(level)] / GrowthDen[level];

        /// <summary>Crystals per 1x Crystal roll. Same curve, so rolls-per-island stays flat.</summary>
        public static BigInteger RollReward(int level) =>
            BaseRollReward * GrowthNum[Level(level)] / GrowthDen[level];

        /// <summary>Cost to build <paramref name="tier"/> (1..4) of structure <paramref name="slot"/> (0..4).</summary>
        public static BigInteger UpgradeCost(int level, int slot, int tier) =>
            BaseIslandCost * GrowthNum[Level(level)] * SlotWeightPct[slot] * TierWeightPct[tier - 1]
            / (GrowthDen[level] * 10_000);

        public static BigInteger RepairCost(int level, int slot, int currentTier) =>
            UpgradeCost(level, slot, currentTier) / 2;

        public static BigInteger RaidLoot(int level, int multiplier, bool shielded)
        {
            BigInteger loot = RollReward(level) * RaidLootInRollRewards * multiplier;
            return shielded ? loot / ShieldedLootDivisor : loot;
        }

        public static bool IsValidMultiplier(int multiplier) => Array.IndexOf(Multipliers, multiplier) >= 0;

        /// <summary>
        /// 1 roll per 5 minutes up to 50. Above the cap the timer idles and the anchor tracks
        /// <paramref name="atMs"/>. Path-independent, so it can be evaluated at any time.
        /// </summary>
        public static (int rolls, long anchorMs) Regen(int rolls, long anchorMs, long atMs)
        {
            if (rolls >= MaxRolls) return (rolls, atMs);
            long ticks = (atMs - anchorMs) / RegenIntervalMs;
            if (ticks <= 0) return (rolls, anchorMs);
            if (rolls + ticks >= MaxRolls) return (MaxRolls, atMs);
            return (rolls + (int)ticks, anchorMs + ticks * RegenIntervalMs);
        }

        static int Level(int level)
        {
            if (level < 1 || level > IslandCount) throw new ArgumentOutOfRangeException(nameof(level), level, "1..200");
            return level;
        }

        static BigInteger[] Powers(int b)
        {
            var p = new BigInteger[IslandCount + 1];
            p[1] = BigInteger.One;
            for (int n = 2; n <= IslandCount; n++) p[n] = p[n - 1] * b;
            return p;
        }
    }

    /// <summary>A roll taken locally and not yet confirmed by the server (sync_rolls payload item).</summary>
    public struct PendingRoll
    {
        [JsonProperty("i")] public long Index;
        [JsonProperty("m")] public int Multiplier;
        [JsonProperty("t")] public long AtMs;
    }

    /// <summary>Persisted (encrypted) economy state: last server snapshot + optimistic local rolls.</summary>
    public sealed class EconomyState
    {
        public string UserId;
        [JsonConverter(typeof(BigIntegerStringConverter))] public BigInteger Crystals;
        public int Rolls;
        public long RegenAnchorMs;
        public long StateAtMs;
        public string RngSeed;
        public long RollIndex;
        public int DryStreak;
        public int Shields;
        public List<int> PendingAttacks = new List<int>();
        public int CardPacks;
        public int IslandLevel = 1;
        public int Multiplier = 1;
        public long ClockOffsetMs;
        public List<PendingRoll> Pending = new List<PendingRoll>();
        public PlayerStateSnapshot LastServerState;
    }

    /// <summary>
    /// Core economic engine: roll regeneration, multiplier selection and spending, reward
    /// distribution and offline persistence. Rolls resolve locally (instant, offline-capable)
    /// and are uploaded to sync_rolls, which re-derives them; the server response is
    /// authoritative and any still-unsynced local rolls are replayed on top of it.
    /// </summary>
    public sealed class GameEconomyManager : MonoBehaviour
    {
        const string SaveKey = "economy";

        [SerializeField, Range(0f, 1f), Tooltip("Share of Crystal results presented as a near-miss split. Cosmetic only.")]
        float nearMissChance = 0.3f;

        [SerializeField, Min(1), Tooltip("Upload offline rolls after this many (Wind-Cannon hits upload immediately).")]
        int syncEveryRolls = 10;

        readonly System.Random _presentationRng = new System.Random();
        SupabaseClient _client;
        EconomyState _state;
        Task _syncInFlight;
        int _lastBroadcastRolls = -1;

        public event Action<RollResult> Rolled;

        /// <summary>Any balance changed (rolls, crystals, shields, attacks, packs, multiplier).</summary>
        public event Action StateChanged;

        /// <summary>A server snapshot was applied (islands and raids react to this).</summary>
        public event Action<PlayerStateSnapshot> ServerStateApplied;

        /// <summary>False until the first server snapshot (fresh install) or a save is loaded.</summary>
        public bool IsReady => _state?.RngSeed != null;

        public PlayerStateSnapshot LastServerState => _state?.LastServerState;
        public BigInteger Crystals => _state?.Crystals ?? BigInteger.Zero;
        public int Rolls => _state == null ? 0 : EconomyRules.Regen(_state.Rolls, _state.RegenAnchorMs, NowMs).rolls;
        public int Shields => _state?.Shields ?? 0;
        public int PendingAttacks => _state?.PendingAttacks.Count ?? 0;
        public int CardPacks => _state?.CardPacks ?? 0;
        public int IslandLevel => _state?.IslandLevel ?? 1;
        public int Multiplier => _state?.Multiplier ?? 1;
        public int UnsyncedRolls => _state?.Pending.Count ?? 0;

        public TimeSpan TimeToNextRoll
        {
            get
            {
                if (_state == null) return TimeSpan.Zero;
                var (rolls, anchor) = EconomyRules.Regen(_state.Rolls, _state.RegenAnchorMs, NowMs);
                if (rolls >= EconomyRules.MaxRolls) return TimeSpan.Zero;
                return TimeSpan.FromMilliseconds(anchor + EconomyRules.RegenIntervalMs - NowMs);
            }
        }

        /// <summary>Server-corrected wall clock in Unix ms.</summary>
        long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (_state?.ClockOffsetMs ?? 0);

        public void Initialize(SupabaseClient client)
        {
            _client = client;
            var saved = SecureSave.Load<EconomyState>(SaveKey);
            _state = saved != null && saved.UserId == client.UserId ? saved : null;
            if (_state?.LastServerState != null) ServerStateApplied?.Invoke(_state.LastServerState);
            StateChanged?.Invoke();
        }

        /// <summary>Spends <see cref="Multiplier"/> rolls and resolves one Aether Roll.</summary>
        public bool TryRoll(out RollResult result)
        {
            result = default;
            if (!IsReady) return false;

            long now = NowMs;
            int multiplier = _state.Multiplier;
            if (!TryApplyRoll(_state, multiplier, now, _presentationRng, nearMissChance, out result)) return false;

            _state.Pending.Add(new PendingRoll { Index = result.Index, Multiplier = multiplier, AtMs = now });
            ClampMultiplier();
            Save();
            Rolled?.Invoke(result);
            StateChanged?.Invoke();

            if (result.Outcome == RollOutcome.WindCannon || _state.Pending.Count >= syncEveryRolls)
                _ = SyncSafeAsync();
            return true;
        }

        /// <summary>Steps to the next multiplier the current roll balance can afford (wraps to 1x).</summary>
        public void CycleMultiplier()
        {
            if (_state == null) return;
            int rolls = Rolls;
            int i = Array.IndexOf(EconomyRules.Multipliers, _state.Multiplier);
            int next = EconomyRules.Multipliers[(i + 1) % EconomyRules.Multipliers.Length];
            _state.Multiplier = next <= rolls ? next : 1;
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Uploads pending rolls and rebases on the server state. Concurrent callers queue
        /// behind the in-flight request, so awaiting this guarantees every roll taken before
        /// the call has reached the server.
        /// </summary>
        public async Task SyncAsync()
        {
            while (_syncInFlight != null) await _syncInFlight;
            var sync = SyncCoreAsync();
            _syncInFlight = sync;
            try { await sync; }
            finally { _syncInFlight = null; }
        }

        /// <summary>
        /// Adopts an authoritative snapshot (from any RPC), then replays local rolls the
        /// server has not seen yet.
        /// </summary>
        public void ApplyServerState(PlayerStateSnapshot snap)
        {
            var pending = _state?.Pending ?? new List<PendingRoll>();
            int multiplier = _state?.Multiplier ?? 1;

            _state = new EconomyState
            {
                UserId = snap.UserId,
                Crystals = snap.Crystals,
                Rolls = snap.Rolls,
                RegenAnchorMs = snap.RegenAnchorMs,
                StateAtMs = snap.StateAtMs,
                RngSeed = snap.RngSeed,
                RollIndex = snap.RollIndex,
                DryStreak = snap.DryStreak,
                Shields = snap.Shields,
                PendingAttacks = new List<int>(snap.PendingAttacks),
                CardPacks = snap.CardPacks,
                IslandLevel = snap.IslandLevel,
                Multiplier = multiplier,
                ClockOffsetMs = snap.ServerTimeMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LastServerState = snap,
            };

            if (snap.RejectedReason != null)
            {
                Debug.LogWarning($"[Economy] Server rejected rolls from #{snap.RollIndex}: {snap.RejectedReason}. Rebasing.");
            }
            else
            {
                foreach (var p in pending)
                {
                    if (p.Index < _state.RollIndex) continue;  // confirmed by this snapshot
                    if (p.Index != _state.RollIndex ||
                        !TryApplyRoll(_state, p.Multiplier, p.AtMs, _presentationRng, 0f, out _)) break;
                    _state.Pending.Add(p);
                }
            }

            ClampMultiplier();
            Save();
            ServerStateApplied?.Invoke(snap);
            StateChanged?.Invoke();
        }

        /// <summary>Mirror of one iteration of public.sync_rolls (internal for the golden replay test).</summary>
        internal static bool TryApplyRoll(EconomyState s, int multiplier, long atMs, System.Random fx, float nearMiss,
                                 out RollResult result)
        {
            result = default;
            if (!EconomyRules.IsValidMultiplier(multiplier)) return false;

            atMs = Math.Max(atMs, s.StateAtMs);
            (s.Rolls, s.RegenAnchorMs) = EconomyRules.Regen(s.Rolls, s.RegenAnchorMs, atMs);
            s.StateAtMs = atMs;
            if (s.Rolls < multiplier) return false;
            s.Rolls -= multiplier;

            bool pity = DiceRollEngine.IsPityActive(s.DryStreak);
            RollOutcome outcome = DiceRollEngine.ResolveOutcome(s.RngSeed, s.RollIndex, pity);
            BigInteger crystals = BigInteger.Zero;
            switch (outcome)
            {
                case RollOutcome.Crystals:
                    crystals = EconomyRules.RollReward(s.IslandLevel) * multiplier;
                    break;
                case RollOutcome.WindCannon:
                    s.PendingAttacks.Add(multiplier);
                    break;
                case RollOutcome.CloudShield:
                    if (s.Shields < EconomyRules.MaxShields) s.Shields++;
                    else crystals = EconomyRules.RollReward(s.IslandLevel) * multiplier;  // shields full
                    break;
                case RollOutcome.StormSpirit:
                    s.CardPacks += multiplier;
                    break;
                case RollOutcome.AetherBattery:
                    s.Rolls += EconomyRules.BatteryRollsPerMultiplier * multiplier;
                    break;
            }
            s.Crystals += crystals;
            s.DryStreak = DiceRollEngine.NextDryStreak(s.DryStreak, outcome);

            RollOutcome[] faces = DiceRollEngine.BuildFaces(outcome, fx, nearMiss, out bool isNearMiss);
            result = new RollResult(s.RollIndex, multiplier, outcome, crystals, pity, faces, isNearMiss);
            s.RollIndex++;
            return true;
        }

        async Task SyncCoreAsync()
        {
            var batch = _state?.Pending.ToArray() ?? Array.Empty<PendingRoll>();
            var snap = await _client.RpcAsync<PlayerStateSnapshot>("sync_rolls", new { p_rolls = batch });
            ApplyServerState(snap);
        }

        async Task SyncSafeAsync()
        {
            try { await SyncAsync(); }
            catch (Exception e) { Debug.LogWarning($"[Economy] Sync deferred: {e.Message}"); }
        }

        void ClampMultiplier()
        {
            int rolls = Rolls;
            while (_state.Multiplier > 1 && _state.Multiplier > rolls)
                _state.Multiplier = EconomyRules.Multipliers[Array.IndexOf(EconomyRules.Multipliers, _state.Multiplier) - 1];
        }

        void Save()
        {
            if (_state != null) SecureSave.Save(SaveKey, _state);
        }

        void Update()
        {
            // Surface regen ticks to the UI.
            int rolls = Rolls;
            if (rolls == _lastBroadcastRolls) return;
            _lastBroadcastRolls = rolls;
            StateChanged?.Invoke();
        }

        void OnApplicationPause(bool paused)
        {
            if (_state == null) return;
            if (paused) Save();
            _ = SyncSafeAsync();  // pausing: bank rolls; resuming: pull raids that hit us meanwhile
        }

        void OnApplicationQuit() => Save();
    }
}
