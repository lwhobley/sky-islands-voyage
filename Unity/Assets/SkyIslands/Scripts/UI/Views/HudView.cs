using System;
using SkyIslands.Core;
using UnityEngine.UIElements;

namespace SkyIslands.UI
{
    /// <summary>Always-visible HUD: balances, roll controls and the three faces.</summary>
    public sealed class HudView : IDisposable
    {
        const int RevealStepMs = 140;
        const int NearMissExtraDelayMs = 380;  // the last face lingers on a near miss

        readonly HudViewModel _vm;
        readonly Label _crystals, _rolls, _rollTimer, _shields, _syncBadge, _islandTitle, _islandSubtitle, _result;
        readonly Label[] _faces;
        readonly Button _roll, _multiplier, _raidQuick;
        readonly IVisualElementScheduledItem _ticker;
        int _shownSequence;

        public HudView(VisualElement root, HudViewModel vm, Action openRaids)
        {
            _vm = vm;
            _crystals = root.Q<Label>("crystals");
            _rolls = root.Q<Label>("rolls");
            _rollTimer = root.Q<Label>("roll-timer");
            _shields = root.Q<Label>("shields");
            _syncBadge = root.Q<Label>("sync-badge");
            _islandTitle = root.Q<Label>("island-title");
            _islandSubtitle = root.Q<Label>("island-subtitle");
            _result = root.Q<Label>("roll-result");
            _faces = new[] { root.Q<Label>("face-0"), root.Q<Label>("face-1"), root.Q<Label>("face-2") };
            _roll = root.Q<Button>("roll");
            _multiplier = root.Q<Button>("multiplier");
            _raidQuick = root.Q<Button>("raid-quick");

            _roll.clicked += vm.Roll;
            _multiplier.clicked += vm.CycleMultiplier;
            _raidQuick.clicked += openRaids;
            _vm.Changed += Render;
            _ticker = root.schedule.Execute(vm.Tick).Every(1000);
            Render();
        }

        public void Dispose()
        {
            _vm.Changed -= Render;
            _ticker.Pause();
        }

        void Render()
        {
            _crystals.text = _vm.Crystals;
            _rolls.text = _vm.Rolls;
            _rollTimer.text = _vm.RollTimer;
            _shields.text = _vm.Shields;
            _syncBadge.text = _vm.SyncBadge;
            _islandTitle.text = _vm.IslandTitle;
            _islandSubtitle.text = _vm.IslandSubtitle;
            _multiplier.text = _vm.Multiplier;
            _roll.SetEnabled(_vm.CanRoll);
            _raidQuick.style.display = _vm.Attacks > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _raidQuick.text = $"RAID ({_vm.Attacks})";

            if (_vm.LastRoll is RollResult r && _vm.RollSequence != _shownSequence)
            {
                _shownSequence = _vm.RollSequence;
                RevealFaces(r);
            }
            else if (_vm.LastRoll == null)
            {
                _result.text = _vm.LastRollText;
            }
        }

        void RevealFaces(RollResult r)
        {
            int sequence = _shownSequence;
            _result.text = "";
            _result.EnableInClassList("roll-result--near-miss", r.IsNearMiss);
            _result.EnableInClassList("roll-result--pity", r.PityActive && !r.IsNearMiss);
            for (int i = 0; i < _faces.Length; i++)
            {
                _faces[i].text = "…";
                SetOutcomeClass(_faces[i], null);
            }

            for (int i = 0; i < _faces.Length; i++)
            {
                int face = i;
                long delay = RevealStepMs * (i + 1) + (r.IsNearMiss && i == _faces.Length - 1 ? NearMissExtraDelayMs : 0);
                _faces[face].schedule.Execute(() =>
                {
                    if (_shownSequence != sequence) return;  // a newer roll took over
                    _faces[face].text = UiText.Outcome(r.Faces[face]);
                    SetOutcomeClass(_faces[face], r.Faces[face]);
                    _faces[face].AddToClassList("face--landed");
                    _faces[face].schedule.Execute(() => _faces[face].RemoveFromClassList("face--landed")).StartingIn(150);
                    if (face == _faces.Length - 1) _result.text = _vm.LastRollText;
                }).StartingIn(delay);
            }
        }

        static void SetOutcomeClass(VisualElement face, RollOutcome? outcome)
        {
            foreach (RollOutcome o in Enum.GetValues(typeof(RollOutcome)))
                face.EnableInClassList("face--" + o.ToString().ToLowerInvariant(), o == outcome);
        }
    }
}
