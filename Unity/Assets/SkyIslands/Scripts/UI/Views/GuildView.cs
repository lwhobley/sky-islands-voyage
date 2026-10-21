using SkyIslands.Multiplayer;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkyIslands.UI
{
    public sealed class GuildView : PanelView<GuildViewModel>
    {
        readonly Label _title, _description, _membersHeader;
        readonly VisualElement _browse, _home;
        readonly TextField _captainName, _search, _newName, _newTag, _message;
        readonly ScrollView _listings, _members, _chat;

        public GuildView(VisualElement root, GuildViewModel vm) : base(root, vm)
        {
            _title = root.Q<Label>("title");
            _description = root.Q<Label>("description");
            _membersHeader = root.Q<Label>("members-header");
            _browse = root.Q("browse");
            _home = root.Q("home");
            _captainName = root.Q<TextField>("captain-name");
            _search = root.Q<TextField>("search");
            _newName = root.Q<TextField>("new-name");
            _newTag = root.Q<TextField>("new-tag");
            _message = root.Q<TextField>("message");
            _listings = root.Q<ScrollView>("listings");
            _members = root.Q<ScrollView>("members");
            _chat = root.Q<ScrollView>("chat");

            root.Q<Button>("rename").clicked += () => vm.Rename(_captainName.value);
            root.Q<Button>("search-button").clicked += () => vm.Search(_search.value);
            root.Q<Button>("create").clicked += () => vm.Create(_newName.value, _newTag.value);
            root.Q<Button>("leave").clicked += vm.Leave;
            root.Q<Button>("send").clicked += SendMessage;
            _message.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) SendMessage();
            }, TrickleDown.TrickleDown);

            vm.MessageAdded += OnMessageAdded;
        }

        public override void Show()
        {
            _captainName.SetValueWithoutNotify(Vm.DisplayName);
            base.Show();
            Vm.Refresh();
        }

        public override void Dispose()
        {
            base.Dispose();
            Vm.MessageAdded -= OnMessageAdded;
        }

        protected override void Render()
        {
            _title.text = Vm.Title;
            _browse.EnableInClassList("hidden", Vm.InGuild);
            _home.EnableInClassList("hidden", !Vm.InGuild);
            if (Vm.InGuild) RenderHome();
            else RenderBrowse();
        }

        void RenderBrowse()
        {
            _listings.Clear();
            if (Vm.Listings.Count == 0) _listings.Add(MakeLabel("No open guilds found. Found your own!", "panel__subtitle"));
            foreach (var g in Vm.Listings)
            {
                var row = Row($"{g.Name} [{g.Tag}]", $"{g.Members}/{GuildService.MaxGuildMembers} members · {g.Description}");
                var captured = g;
                row.Add(MakeButton("Join", () => Vm.Join(captured), !Vm.IsBusy));
                _listings.Add(row);
            }
        }

        void RenderHome()
        {
            var guild = Vm.Guild;
            _description.text = guild.Description;
            _membersHeader.text = Vm.MembersHeader;

            _members.Clear();
            foreach (var m in guild.Members)
            {
                var row = Row(m.DisplayName, $"{m.Role} · island {m.IslandLevel}");
                if (Vm.CanKick(m))
                {
                    var captured = m;
                    row.Add(MakeButton("Kick", () => Vm.Kick(captured), !Vm.IsBusy, "btn--secondary"));
                }
                _members.Add(row);
            }

            _chat.Clear();
            foreach (var message in guild.Messages) _chat.Add(ChatLine(message));
            ScrollChatToEnd();
        }

        void SendMessage()
        {
            if (Vm.IsBusy) return;  // keep the draft; Run() would drop it
            Vm.Send(_message.value);
            _message.value = "";
        }

        void OnMessageAdded(GuildMessage message)
        {
            if (!Visible || !Vm.InGuild) return;
            _chat.Add(ChatLine(message));
            ScrollChatToEnd();
        }

        Label ChatLine(GuildMessage m)
        {
            var line = MakeLabel(m.IsSystem ? m.Body : $"{m.AuthorName}: {m.Body}", "chat__line");
            line.EnableInClassList("chat__line--system", m.IsSystem);
            line.EnableInClassList("chat__line--mine", Vm.IsMine(m));
            return line;
        }

        void ScrollChatToEnd() =>
            _chat.schedule.Execute(() => _chat.verticalScroller.value = _chat.verticalScroller.highValue).StartingIn(16);

        static VisualElement Row(string title, string detail)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var main = new VisualElement();
            main.AddToClassList("row__main");
            main.Add(MakeLabel(title, "row__title"));
            main.Add(MakeLabel(detail, "row__detail"));
            row.Add(main);
            return row;
        }
    }
}
