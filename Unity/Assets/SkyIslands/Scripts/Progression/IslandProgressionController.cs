using System;
using System.Numerics;
using System.Threading.Tasks;
using SkyIslands.Backend;
using SkyIslands.Core;
using UnityEngine;

namespace SkyIslands.Progression
{
    public enum IslandPhase
    {
        Locked,
        Building,
        Completed,
    }

    public enum StructureVisualState
    {
        Scaffold,  // tier 0: nothing built yet
        Built,     // tier 1..4
        Damaged,   // raided: smoke/cracks until repaired; blocks further upgrades
    }

    public readonly struct StructureView
    {
        public StructureView(int slot, string name, int tier, StructureVisualState state,
                             BigInteger? upgradeCost, BigInteger? repairCost, string prefabKey)
        {
            Slot = slot;
            Name = name;
            Tier = tier;
            State = state;
            UpgradeCost = upgradeCost;
            RepairCost = repairCost;
            PrefabKey = prefabKey;
        }

        public int Slot { get; }
        public string Name { get; }
        public int Tier { get; }
        public StructureVisualState State { get; }

        /// <summary>Null when maxed or damaged.</summary>
        public BigInteger? UpgradeCost { get; }

        /// <summary>Null unless damaged.</summary>
        public BigInteger? RepairCost { get; }

        public string PrefabKey { get; }
    }

    /// <summary>
    /// State machine for the current island's 5 structures. Every transition is driven by
    /// an authoritative server snapshot: this class diffs consecutive snapshots and raises
    /// the matching events (tier built, damaged, repaired, island completed, biome unlocked),
    /// so views animate identically whether a change came from this device or a raid.
    /// </summary>
    public sealed class IslandProgressionController : MonoBehaviour
    {
        [SerializeField] GameEconomyManager economy;
        [SerializeField] BiomeDatabase biomes;

        readonly int[] _tiers = new int[EconomyRules.StructuresPerIsland];
        readonly bool[] _damaged = new bool[EconomyRules.StructuresPerIsland];
        SupabaseClient _client;
        int _level;
        bool _campaignCompleted;
        bool _busy;

        /// <summary>New island on screen: rebuild the scene for it.</summary>
        public event Action<IslandConfig> IslandLoaded;

        /// <summary>A structure's tier or damage state changed: swap its prefab / VFX.</summary>
        public event Action<StructureView> StructureChanged;

        /// <summary>(slot, newTier): play the tier-completion celebration.</summary>
        public event Action<int, int> StructureTierCompleted;

        /// <summary>All 5 structures maxed: island celebration, then IslandLoaded for the next.</summary>
        public event Action<IslandConfig> IslandCompleted;

        public event Action<BiomeDefinition> BiomeUnlocked;
        public event Action CampaignCompleted;

        public IslandConfig CurrentIsland { get; private set; }
        public bool IsBusy => _busy;

        public void Initialize(SupabaseClient client)
        {
            _client = client;
            economy.ServerStateApplied += OnServerState;
            if (economy.LastServerState != null) OnServerState(economy.LastServerState);
        }

        void OnDestroy()
        {
            if (economy != null) economy.ServerStateApplied -= OnServerState;
        }

        public IslandPhase GetPhase(int level)
        {
            if (level < _level || (level == _level && _campaignCompleted)) return IslandPhase.Completed;
            return level == _level ? IslandPhase.Building : IslandPhase.Locked;
        }

        public StructureView GetStructure(int slot)
        {
            int tier = _tiers[slot];
            bool damaged = _damaged[slot];
            var state = damaged ? StructureVisualState.Damaged
                      : tier == 0 ? StructureVisualState.Scaffold
                      : StructureVisualState.Built;
            BigInteger? upgrade = damaged || tier >= EconomyRules.MaxTier || _campaignCompleted
                ? (BigInteger?)null
                : EconomyRules.UpgradeCost(_level, slot, tier + 1);
            BigInteger? repair = damaged ? EconomyRules.RepairCost(_level, slot, tier) : (BigInteger?)null;
            return new StructureView(slot, CurrentIsland.StructureName(slot), tier, state, upgrade, repair,
                                     CurrentIsland.StructurePrefabKey(slot, tier, damaged));
        }

        public bool CanUpgrade(int slot)
        {
            var s = GetStructure(slot);
            return !_busy && s.UpgradeCost.HasValue && economy.Crystals >= s.UpgradeCost.Value;
        }

        public bool CanRepair(int slot)
        {
            var s = GetStructure(slot);
            return !_busy && s.RepairCost.HasValue && economy.Crystals >= s.RepairCost.Value;
        }

        /// <summary>Requires a connection: banks offline rolls first, then upgrades server-side.</summary>
        public Task UpgradeAsync(int slot) => MutateAsync("upgrade_structure", slot);

        public Task RepairAsync(int slot) => MutateAsync("repair_structure", slot);

        async Task MutateAsync(string rpc, int slot)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                await economy.SyncAsync();
                var snap = await _client.RpcAsync<PlayerStateSnapshot>(rpc, new { p_slot = slot });
                economy.ApplyServerState(snap);  // comes back through OnServerState
            }
            finally
            {
                _busy = false;
            }
        }

        void OnServerState(PlayerStateSnapshot s)
        {
            if (CurrentIsland == null || s.IslandLevel < _level)
            {
                LoadIsland(s);
                return;
            }

            if (s.IslandLevel > _level)
            {
                IslandConfig finished = CurrentIsland;
                IslandCompleted?.Invoke(finished);
                LoadIsland(s);
                if (CurrentIsland.Biome != finished.Biome) BiomeUnlocked?.Invoke(CurrentIsland.Biome);
                return;
            }

            for (int slot = 0; slot < EconomyRules.StructuresPerIsland; slot++)
            {
                int oldTier = _tiers[slot];
                bool oldDamaged = _damaged[slot];
                _tiers[slot] = s.Tiers[slot];
                _damaged[slot] = s.Damaged[slot];

                if (_tiers[slot] > oldTier) StructureTierCompleted?.Invoke(slot, _tiers[slot]);
                if (_tiers[slot] != oldTier || _damaged[slot] != oldDamaged) StructureChanged?.Invoke(GetStructure(slot));
            }

            if (s.CampaignCompleted && !_campaignCompleted)
            {
                _campaignCompleted = true;
                IslandCompleted?.Invoke(CurrentIsland);
                CampaignCompleted?.Invoke();
            }
        }

        void LoadIsland(PlayerStateSnapshot s)
        {
            _level = s.IslandLevel;
            _campaignCompleted = s.CampaignCompleted;
            Array.Copy(s.Tiers, _tiers, _tiers.Length);
            Array.Copy(s.Damaged, _damaged, _damaged.Length);
            CurrentIsland = biomes.GetIsland(_level);

            IslandLoaded?.Invoke(CurrentIsland);
            for (int slot = 0; slot < EconomyRules.StructuresPerIsland; slot++)
                StructureChanged?.Invoke(GetStructure(slot));
        }
    }
}
