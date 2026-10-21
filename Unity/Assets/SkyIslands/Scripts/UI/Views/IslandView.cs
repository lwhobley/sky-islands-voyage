using SkyIslands.Core;
using SkyIslands.Progression;
using UnityEngine.UIElements;

namespace SkyIslands.UI
{
    public sealed class IslandView : PanelView<IslandViewModel>
    {
        readonly Label _title, _subtitle;
        readonly ProgressBar _progress;
        readonly ScrollView _structures;

        public IslandView(VisualElement root, IslandViewModel vm) : base(root, vm)
        {
            _title = root.Q<Label>("title");
            _subtitle = root.Q<Label>("subtitle");
            _progress = root.Q<ProgressBar>("progress");
            _structures = root.Q<ScrollView>("structures");
        }

        protected override void Render()
        {
            _title.text = Vm.Title;
            _subtitle.text = Vm.Subtitle;
            _progress.value = Vm.Progress;
            _progress.title = $"{UnityEngine.Mathf.RoundToInt(Vm.Progress * 100)}% built";

            _structures.Clear();
            foreach (var item in Vm.Structures)
            {
                var row = new VisualElement();
                row.AddToClassList("row");
                row.EnableInClassList("row--damaged", item.State == StructureVisualState.Damaged);
                row.EnableInClassList("row--complete", item.Tier == EconomyRules.MaxTier && !item.IsRepair);

                var main = new VisualElement();
                main.AddToClassList("row__main");
                main.Add(MakeLabel(item.Name, "row__title"));
                main.Add(MakeLabel(item.Detail, "row__detail"));
                var pips = new VisualElement();
                pips.AddToClassList("pips");
                for (int t = 1; t <= EconomyRules.MaxTier; t++)
                {
                    var pip = new VisualElement();
                    pip.AddToClassList("pip");
                    pip.EnableInClassList("pip--filled", t <= item.Tier);
                    pips.Add(pip);
                }
                main.Add(pips);
                row.Add(main);

                var captured = item;
                row.Add(MakeButton(item.ActionText, () => Vm.Act(captured), item.ActionEnabled,
                                   item.IsRepair ? "btn--danger" : "btn"));
                _structures.Add(row);
            }
        }
    }
}
