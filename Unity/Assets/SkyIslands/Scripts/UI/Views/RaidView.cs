using UnityEngine.UIElements;

namespace SkyIslands.UI
{
    public sealed class RaidView : PanelView<RaidViewModel>
    {
        readonly Label _attacks, _result, _revengeEmpty;
        readonly Button _raid;
        readonly ScrollView _revengeList;

        public RaidView(VisualElement root, RaidViewModel vm) : base(root, vm)
        {
            _attacks = root.Q<Label>("attacks");
            _result = root.Q<Label>("result");
            _revengeEmpty = root.Q<Label>("revenge-empty");
            _raid = root.Q<Button>("raid");
            _revengeList = root.Q<ScrollView>("revenge-list");
            _raid.clicked += vm.Raid;
        }

        protected override void Render()
        {
            _attacks.text = Vm.AttacksText;
            _raid.SetEnabled(Vm.CanRaid);

            _result.EnableInClassList("hidden", string.IsNullOrEmpty(Vm.LastResultText));
            _result.EnableInClassList("result-card--deflected", Vm.LastResultDeflected);
            _result.text = Vm.LastResultText;

            _revengeList.Clear();
            _revengeEmpty.EnableInClassList("hidden", Vm.RevengeTargets.Count > 0);
            foreach (var target in Vm.RevengeTargets)
            {
                var row = new VisualElement();
                row.AddToClassList("row");
                row.AddToClassList("row--alert");
                var main = new VisualElement();
                main.AddToClassList("row__main");
                main.Add(MakeLabel(target.AttackerName, "row__title"));
                main.Add(MakeLabel($"Took {UiText.Short(target.CrystalsLost)} crystals", "row__detail"));
                row.Add(main);
                var captured = target;
                row.Add(MakeButton("Revenge", () => Vm.Revenge(captured), Vm.CanRaid, "btn--danger"));
                _revengeList.Add(row);
            }
        }
    }
}
