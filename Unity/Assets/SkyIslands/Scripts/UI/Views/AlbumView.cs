using UnityEngine.UIElements;

namespace SkyIslands.UI
{
    public sealed class AlbumView : PanelView<AlbumViewModel>
    {
        readonly Label _season, _dust, _packResult;
        readonly Button _openPack;
        readonly ScrollView _sets;

        public AlbumView(VisualElement root, AlbumViewModel vm) : base(root, vm)
        {
            _season = root.Q<Label>("season");
            _dust = root.Q<Label>("dust");
            _packResult = root.Q<Label>("pack-result");
            _openPack = root.Q<Button>("open-pack");
            _sets = root.Q<ScrollView>("sets");
            _openPack.clicked += vm.OpenPack;
        }

        public override void Show()
        {
            base.Show();
            Vm.Load();
        }

        protected override void Render()
        {
            _season.text = Vm.SeasonText;
            _dust.text = Vm.DustText;
            _openPack.SetEnabled(Vm.CanOpenPack);
            _openPack.text = Vm.Packs > 0 ? $"Open Pack ({Vm.Packs})" : "No packs";
            _packResult.EnableInClassList("hidden", string.IsNullOrEmpty(Vm.LastPackText));
            _packResult.text = Vm.LastPackText;

            _sets.Clear();
            foreach (var set in Vm.Sets)
            {
                int owned = 0;
                foreach (var c in set.Cards) if (c.Owned) owned++;
                _sets.Add(MakeLabel($"{set.Name}  {owned}/{set.Cards.Count}" + (set.Completed ? "  (complete)" : ""), "panel__section"));
                _sets.Add(MakeLabel(set.Completed
                    ? "Completed"
                    : $"Reward: {set.RewardRolls} rolls + {UiText.Short(set.RewardCrystals)} crystals", "row__detail"));

                var grid = new VisualElement();
                grid.AddToClassList("card-grid");
                foreach (var card in set.Cards)
                {
                    var tile = new VisualElement();
                    tile.AddToClassList("card");
                    tile.EnableInClassList("card--owned", card.Owned);
                    tile.Add(MakeLabel(card.Owned ? card.Name : "???", "card__name"));
                    tile.Add(MakeLabel(new string('*', card.Rarity), "card__rarity"));
                    if (!card.Owned)
                    {
                        var captured = card;
                        tile.Add(MakeButton($"Craft {card.CraftCost}", () => Vm.Craft(captured), Vm.CanCraft(card)));
                    }
                    grid.Add(tile);
                }
                _sets.Add(grid);
            }
        }
    }
}
