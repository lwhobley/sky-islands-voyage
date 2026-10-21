using SkyIslands.Multiplayer;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkyIslands.UI
{
    public sealed class GuildView : PanelView<GuildViewModel>
    {
        readonly Label _title, _description, _membersHeader, _inviteCodeLabel;
        readonly VisualElement _browse, _home, _manage;
        readonly TextField _captainName, _search, _newName, _newTag, _message, _inviteCode;
        readonly Button _toggleOpen, _reportMessage;
        GuildMessage _selected;
        Label _selectedLine;
        readonly ScrollView _listings, _members, _chat;

        public GuildView(VisualElement root, GuildViewModel vm) : base(root, vm)
        {
            _title = root.Q<Label>("title");
            _description = root.Q<Label>("description");
            _membersHeader = root.Q<Label>("members-header");
            _inviteCodeLabel = root.Q<Label>("invite-code-label");
            _manage = root.Q("manage");
            _inviteCode = root.Q<TextField>("invite-code");
            _toggleOpen = root.Q<Button>("toggle-open");
            _reportMessage = root.Q<Button>("report-message");
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
            root.Q<Button>("join-code").clicked += () => vm.JoinByCode(_inviteCode.value);
            root.Q<Button>("rotate-code").clicked += vm.RotateCode;
            _toggleOpen.clicked += vm.ToggleOpen;
            _reportMessage.clicked += ReportSelected;
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
            _manage.EnableInClassList("hidden", !Vm.CanManage);
            _inviteCodeLabel.text = Vm.InviteCodeText;
            _toggleOpen.text = Vm.OpenToggleText;

            _members.Clear();
            foreach (var m in guild.Members)
            {
                var row = Row(m.DisplayName, $"{m.Role} · island {m.IslandLevel}");
                var captured = m;
                if (Vm.CanKick(m))
                    row.Add(MakeButton("Kick", () => Vm.Kick(captured), !Vm.IsBusy, "btn--secondary"));
                if (Vm.CanReport(m))
                    row.Add(MakeButton("Report", () => Vm.ReportName(captured), !Vm.IsBusy, "btn--secondary"));
                _members.Add(row);
            }

            _chat.Clear();
            Select(null, null);
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
            if (Vm.CanReport(m))
                line.RegisterCallback<PointerDownEvent>(_ => Select(_selected == m ? null : m, line));  // tap to select
            return line;
        }

        void Select(GuildMessage message, Label line)
        {
            _selectedLine?.RemoveFromClassList("chat__line--selected");
            _selected = message;
            _selectedLine = message == null ? null : line;
            _selectedLine?.AddToClassList("chat__line--selected");
            _reportMessage.EnableInClassList("hidden", message == null);
            if (message != null) _reportMessage.text = $"Report {message.AuthorName}";
        }

        void ReportSelected()
        {
            if (_selected == null || Vm.IsBusy) return;
            Vm.ReportMessage(_selected);
            Select(null, null);
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
