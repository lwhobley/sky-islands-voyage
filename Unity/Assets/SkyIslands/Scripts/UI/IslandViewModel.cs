using System.Collections.Generic;
using SkyIslands.Core;
using SkyIslands.Progression;

namespace SkyIslands.UI
{
    public sealed class StructureItem
    {
        public int Slot;
        public string Name;
        public int Tier;
        public StructureVisualState State;
        public string Detail;
        public string ActionText;
        public bool ActionEnabled;
        public bool IsRepair;
    }

    /// <summary>The current island's five structures with build / repair actions.</summary>
    public sealed class IslandViewModel : ViewModelBase
    {
        readonly IslandProgressionController _islands;
        readonly GameEconomyManager _economy;

        public IslandViewModel(IslandProgressionController islands, GameEconomyManager economy)
        {
            _islands = islands;
            _economy = economy;
            _islands.IslandLoaded += OnIslandLoaded;
            _islands.StructureChanged += OnStructureChanged;
            _islands.StructureTierCompleted += OnTierCompleted;
            _islands.IslandCompleted += OnIslandCompleted;
            _islands.BiomeUnlocked += OnBiomeUnlocked;
            _islands.CampaignCompleted += OnCampaignCompleted;
            _economy.StateChanged += NotifyChanged;  // affordability
        }

        public bool HasIsland => _islands.CurrentIsland != null;
        public string Title => _islands.CurrentIsland?.Name ?? "Charting course…";

        public string Subtitle => _islands.CurrentIsland == null
            ? ""
            : $"{_islands.CurrentIsland.Biome.displayName} · Island {_islands.CurrentIsland.Level} of {EconomyRules.IslandCount} · " +
              $"Total cost {UiText.Short(_islands.CurrentIsland.TotalCost)}";

        /// <summary>Fraction of the island's 20 tiers built.</summary>
        public float Progress
        {
            get
            {
                if (!HasIsland) return 0f;
                int built = 0;
                for (int slot = 0; slot < EconomyRules.StructuresPerIsland; slot++) built += _islands.GetStructure(slot).Tier;
                return built / (float)(EconomyRules.StructuresPerIsland * EconomyRules.MaxTier);
            }
        }

        public IReadOnlyList<StructureItem> Structures
        {
            get
            {
                var items = new List<StructureItem>();
                if (!HasIsland) return items;
                for (int slot = 0; slot < EconomyRules.StructuresPerIsland; slot++)
                {
                    var s = _islands.GetStructure(slot);
                    var item = new StructureItem { Slot = slot, Name = s.Name, Tier = s.Tier, State = s.State };
                    if (s.RepairCost.HasValue)
                    {
                        item.IsRepair = true;
                        item.Detail = "Damaged in a raid — smoke is pouring out!";
                        item.ActionText = "Repair " + UiText.Short(s.RepairCost.Value);
                        item.ActionEnabled = !IsBusy && _islands.CanRepair(slot);
                    }
                    else if (s.UpgradeCost.HasValue)
                    {
                        item.Detail = s.Tier == 0 ? "Not built yet" : $"Tier {s.Tier} of {EconomyRules.MaxTier}";
                        item.ActionText = (s.Tier == 0 ? "Build " : "Upgrade ") + UiText.Short(s.UpgradeCost.Value);
                        item.ActionEnabled = !IsBusy && _islands.CanUpgrade(slot);
                    }
                    else
                    {
                        item.Detail = "Fully built";
                        item.ActionText = "Done";
                    }
                    items.Add(item);
                }
                return items;
            }
        }

        public void Act(StructureItem item) =>
            Run(() => item.IsRepair ? _islands.RepairAsync(item.Slot) : _islands.UpgradeAsync(item.Slot));

        public override void Dispose()
        {
            _islands.IslandLoaded -= OnIslandLoaded;
            _islands.StructureChanged -= OnStructureChanged;
            _islands.StructureTierCompleted -= OnTierCompleted;
            _islands.IslandCompleted -= OnIslandCompleted;
            _islands.BiomeUnlocked -= OnBiomeUnlocked;
            _islands.CampaignCompleted -= OnCampaignCompleted;
            _economy.StateChanged -= NotifyChanged;
        }

        void OnIslandLoaded(IslandConfig _) => NotifyChanged();
        void OnStructureChanged(StructureView _) => NotifyChanged();

        void OnTierCompleted(int slot, int tier) =>
            ShowCelebration($"{_islands.CurrentIsland.StructureName(slot)}\nTier {tier}!");

        void OnIslandCompleted(IslandConfig island) => ShowCelebration($"{island.Name}\ncomplete!");
        void OnBiomeUnlocked(BiomeDefinition biome) => ShowToast($"New skies ahead: {biome.displayName}");
        void OnCampaignCompleted() => ShowCelebration("You rule the\nCelestial Sovereign Spire!");
    }
}
