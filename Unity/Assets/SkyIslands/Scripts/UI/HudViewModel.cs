using System.Numerics;
using SkyIslands.Core;
using SkyIslands.Progression;

namespace SkyIslands.UI
{
    /// <summary>Top bar, the roll button, multiplier and the last roll's faces.</summary>
    public sealed class HudViewModel : ViewModelBase
    {
        readonly GameEconomyManager _economy;
        readonly IslandProgressionController _islands;

        public HudViewModel(GameEconomyManager economy, IslandProgressionController islands)
        {
            _economy = economy;
            _islands = islands;
            _economy.StateChanged += NotifyChanged;
            _economy.Rolled += OnRolled;
            _islands.IslandLoaded += OnIslandLoaded;
        }

        public string Crystals => UiText.Short(_economy.Crystals);
        public string Rolls => _economy.Rolls > EconomyRules.MaxRolls
            ? _economy.Rolls.ToString()
            : $"{_economy.Rolls}/{EconomyRules.MaxRolls}";
        public string RollTimer => _economy.Rolls >= EconomyRules.MaxRolls ? "" : "+1 in " + UiText.Countdown(_economy.TimeToNextRoll);
        public string Shields => $"{_economy.Shields}/{EconomyRules.MaxShields}";
        public int Attacks => _economy.PendingAttacks;
        public int CardPacks => _economy.CardPacks;
        public string Multiplier => "x" + _economy.Multiplier;
        public bool CanRoll => _economy.IsReady && _economy.Rolls >= _economy.Multiplier;
        public string SyncBadge => _economy.UnsyncedRolls > 0 ? $"{_economy.UnsyncedRolls} rolls to sync" : "";
        public string IslandTitle => _islands.CurrentIsland?.Name ?? "";
        public string IslandSubtitle => _islands.CurrentIsland == null
            ? ""
            : $"{_islands.CurrentIsland.Biome.displayName} · Island {_islands.CurrentIsland.Level}/{EconomyRules.IslandCount}";

        public RollResult? LastRoll { get; private set; }

        /// <summary>Increments per roll (roll indexes can repeat after a server rebase).</summary>
        public int RollSequence { get; private set; }

        public string LastRollText
        {
            get
            {
                if (LastRoll is not RollResult r) return "Tap ROLL to set sail!";
                string text = r.Outcome switch
                {
                    RollOutcome.Crystals => $"+{UiText.Short(r.CrystalsAwarded)} crystals",
                    RollOutcome.WindCannon => r.Multiplier > 1 ? $"Wind-Cannon x{r.Multiplier}! Raid ready." : "Wind-Cannon! Raid ready.",
                    RollOutcome.CloudShield => r.CrystalsAwarded > BigInteger.Zero
                        ? $"Shields full: +{UiText.Short(r.CrystalsAwarded)} crystals"
                        : "Cloud-Shield raised!",
                    RollOutcome.StormSpirit => $"Storm Spirit: +{r.Multiplier} card pack{(r.Multiplier > 1 ? "s" : "")}",
                    RollOutcome.AetherBattery => $"Aether Battery: +{EconomyRules.BatteryRollsPerMultiplier * r.Multiplier} rolls",
                    _ => "",
                };
                return r.IsNearMiss ? "So close! " + text : text;
            }
        }

        public void Roll()
        {
            if (!_economy.TryRoll(out _))
                ShowToast(_economy.Rolls >= 1
                    ? "Lower the multiplier to roll."
                    : "Out of rolls. Next one in " + UiText.Countdown(_economy.TimeToNextRoll) + ".");
        }

        public void CycleMultiplier() => _economy.CycleMultiplier();

        /// <summary>Called once a second by the view so the regen countdown ticks.</summary>
        public void Tick() => NotifyChanged();

        public override void Dispose()
        {
            _economy.StateChanged -= NotifyChanged;
            _economy.Rolled -= OnRolled;
            _islands.IslandLoaded -= OnIslandLoaded;
        }

        void OnRolled(RollResult result)
        {
            LastRoll = result;
            RollSequence++;
            NotifyChanged();
        }

        void OnIslandLoaded(IslandConfig _) => NotifyChanged();
    }
}
