using System.Collections.Generic;
using SkyIslands.Core;
using SkyIslands.Multiplayer;

namespace SkyIslands.UI
{
    /// <summary>Wind-Cannon raids, their results, and the revenge queue.</summary>
    public sealed class RaidViewModel : ViewModelBase
    {
        readonly RaidAndShieldService _raids;
        readonly GameEconomyManager _economy;

        public RaidViewModel(RaidAndShieldService raids, GameEconomyManager economy)
        {
            _raids = raids;
            _economy = economy;
            _raids.RaidResolved += OnRaidResolved;
            _raids.RevengeAvailable += OnRevengeAvailable;
            _raids.ShieldAbsorbedRaid += OnShieldAbsorbed;
            _raids.RevengeTargetsChanged += NotifyChanged;
            _economy.StateChanged += NotifyChanged;
        }

        public int Attacks => _economy.PendingAttacks;
        public bool CanRaid => !IsBusy && _raids.CanRaid;
        public string AttacksText => Attacks == 0
            ? "No Wind-Cannon shots. Roll a Cannon to raid."
            : $"{Attacks} Wind-Cannon shot{(Attacks == 1 ? "" : "s")} ready";

        public string LastResultText { get; private set; }
        public bool LastResultDeflected { get; private set; }
        public IReadOnlyList<RaidNotification> RevengeTargets => _raids.RevengeTargets;

        public void Raid() => Run(async () => await _raids.LaunchRaidAsync());
        public void Revenge(RaidNotification target) => Run(async () => await _raids.LaunchRevengeAsync(target));

        public override void Dispose()
        {
            _raids.RaidResolved -= OnRaidResolved;
            _raids.RevengeAvailable -= OnRevengeAvailable;
            _raids.ShieldAbsorbedRaid -= OnShieldAbsorbed;
            _raids.RevengeTargetsChanged -= NotifyChanged;
            _economy.StateChanged -= NotifyChanged;
        }

        void OnRaidResolved(RaidResult r)
        {
            string target = r.IsNpc ? r.DefenderName : $"{r.DefenderName}'s island (#{r.DefenderIslandLevel})";
            string loot = UiText.Short(r.CrystalsAwarded);
            LastResultDeflected = r.Shielded;
            LastResultText = r.Shielded
                ? $"A Cloud-Shield deflected your cannon at {target}! You still salvaged {loot} crystals."
                : $"Direct hit on {target}! +{loot} crystals" + (r.DamagedSlot.HasValue ? " and their structure is smoking." : ".");
            if (r.IsRevenge && !r.Shielded) ShowCelebration("Revenge!");
            NotifyChanged();
        }

        void OnRevengeAvailable(RaidNotification n) =>
            ShowToast($"{n.AttackerName} raided you for {UiText.Short(n.CrystalsLost)} crystals! Revenge awaits.");

        void OnShieldAbsorbed(RaidNotification n) =>
            ShowToast($"Your Cloud-Shield blocked {n.AttackerName}'s cannon.");
    }
}
