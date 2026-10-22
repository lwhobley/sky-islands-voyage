using System;
using System.Collections.Generic;
using SkyIslands.Backend;
using SkyIslands.Core;
using SkyIslands.Multiplayer;

namespace SkyIslands.UI
{
    /// <summary>Browse / found a guild, or the roster and live chat once in one.</summary>
    public sealed class GuildViewModel : ViewModelBase
    {
        readonly GuildService _guilds;
        readonly GameServicesClient _services;
        readonly GameEconomyManager _economy;
        readonly string _myUserId;

        public GuildViewModel(GuildService guilds, GameServicesClient services, GameEconomyManager economy, string myUserId)
        {
            _guilds = guilds;
            _services = services;
            _economy = economy;
            _myUserId = myUserId;
            _guilds.GuildChanged += OnGuildChanged;
            _guilds.MessageReceived += OnMessage;
        }

        /// <summary>Raised per chat line so the view can append instead of rebuilding.</summary>
        public event Action<GuildMessage> MessageAdded;

        public GuildInfo Guild => _guilds.Guild;
        public bool InGuild => Guild != null;
        public IReadOnlyList<GuildListing> Listings { get; private set; } = Array.Empty<GuildListing>();
        public bool CanManage => Guild != null && (Guild.MyRole == "leader" || Guild.MyRole == "officer");
        public string InviteCodeText => CanManage ? $"Invite code: {Guild.InviteCode}" : "";
        public string OpenToggleText => Guild?.IsOpen == true ? "Make invite-only" : "Open to everyone";
        public string Title => InGuild ? $"{Guild.Name} [{Guild.Tag}]" : "Guilds";
        public string MembersHeader => InGuild ? $"Members {Guild.Members.Count}/{GuildService.MaxGuildMembers}" : "";

        public string DisplayName => _economy.LastServerState?.DisplayName ?? "";

        public bool IsMine(GuildMessage m) => m.UserId == _myUserId;

        public bool CanKick(GuildMember m) =>
            m.UserId != _myUserId && (Guild?.MyRole == "leader" || (Guild?.MyRole == "officer" && m.Role == "member"));

        public void Refresh() => Run(async () =>
        {
            await _guilds.RefreshAsync();
            if (!InGuild) Listings = await _guilds.ListAsync();
        });

        public void Search(string text) => Run(async () => Listings = await _guilds.ListAsync(text));
        public void Join(GuildListing g) => Run(() => _guilds.JoinAsync(g.Id));
        public void Create(string name, string tag) => Run(() => _guilds.CreateAsync(name, tag, ""));
        public void Kick(GuildMember m) => Run(() => _guilds.KickAsync(m.UserId));
        public void JoinByCode(string code) => Run(() => _guilds.JoinByCodeAsync(code));
        public void ToggleOpen() => Run(() => _guilds.UpdateSettingsAsync(!Guild.IsOpen, Guild.Description));
        public void RotateCode() => Run(() => _guilds.RotateInviteCodeAsync());

        public bool CanReport(GuildMember m) => m.UserId != _myUserId;
        public bool CanReport(GuildMessage m) => !m.IsSystem && m.UserId != _myUserId;

        public void ReportName(GuildMember m) => Run(async () =>
        {
            await _guilds.ReportAsync(m.UserId, "offensive_name");
            ShowToast("Thanks. A moderator will review it.");
        });

        public void ReportMessage(GuildMessage m) => Run(async () =>
        {
            await _guilds.ReportAsync(m.UserId, "harassment", "", m.Id);
            ShowToast("Thanks. A moderator will review it.");
        });

        public void Rename(string name) => Run(async () =>
        {
            await _services.SetDisplayNameAsync(name);
            if (InGuild) await _guilds.RefreshAsync();  // roster shows the new name
            ShowToast($"Ahoy, Captain {DisplayName}!");
        });

        public void Leave() => Run(async () =>
        {
            await _guilds.LeaveAsync();
            Listings = await _guilds.ListAsync();
        });

        public void Send(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Run(() => _guilds.SendAsync(text));
        }

        public override void Dispose()
        {
            _guilds.GuildChanged -= OnGuildChanged;
            _guilds.MessageReceived -= OnMessage;
        }

        void OnGuildChanged(GuildInfo _) => NotifyChanged();
        void OnMessage(GuildMessage m) => MessageAdded?.Invoke(m);
    }
}
