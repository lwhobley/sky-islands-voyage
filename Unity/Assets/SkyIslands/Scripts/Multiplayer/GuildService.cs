using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkyIslands.Backend;
using UnityEngine;

namespace SkyIslands.Multiplayer
{
    public sealed class GuildMember
    {
        [JsonProperty("user_id")] public string UserId;
        [JsonProperty("display_name")] public string DisplayName;
        [JsonProperty("role")] public string Role;  // leader | officer | member
        [JsonProperty("island_level")] public int IslandLevel;
        [JsonProperty("last_active_ms")] public long LastActiveMs;
    }

    public sealed class GuildMessage
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("user_id")] public string UserId;  // null: system message
        [JsonProperty("author_name")] public string AuthorName;
        [JsonProperty("body")] public string Body;
        [JsonProperty("created_ms")] public long CreatedMs;

        public bool IsSystem => UserId == null;
    }

    public sealed class GuildInfo
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("tag")] public string Tag;
        [JsonProperty("description")] public string Description;
        [JsonProperty("my_role")] public string MyRole;
        [JsonProperty("is_open")] public bool IsOpen;
        [JsonProperty("invite_code")] public string InviteCode;  // only sent to leaders and officers
        [JsonProperty("members")] public List<GuildMember> Members = new List<GuildMember>();
        [JsonProperty("messages")] public List<GuildMessage> Messages = new List<GuildMessage>();
    }

    public sealed class GuildListing
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("tag")] public string Tag;
        [JsonProperty("description")] public string Description;
        [JsonProperty("members")] public int Members;
    }

    /// <summary>
    /// Guild membership and live chat. Membership changes are server RPCs; chat arrives over a
    /// Realtime subscription to guild_messages for the current guild (RLS-gated, so a player
    /// stops receiving the moment they leave or are removed).
    /// </summary>
    public sealed class GuildService : IDisposable
    {
        public const int MaxGuildMembers = 30;
        const int MaxCachedMessages = 200;

        readonly SupabaseClient _client;
        readonly HashSet<long> _seenMessageIds = new HashSet<long>();
        SupabaseRealtime _chat;

        public GuildService(SupabaseClient client) => _client = client;

        /// <summary>Null while the player is not in a guild.</summary>
        public GuildInfo Guild { get; private set; }

        public event Action<GuildInfo> GuildChanged;
        public event Action<GuildMessage> MessageReceived;

        public async Task RefreshAsync() => Apply(await _client.RpcAsync<GuildInfo>("get_my_guild"));

        public Task<List<GuildListing>> ListAsync(string search = null) =>
            _client.RpcAsync<List<GuildListing>>("list_guilds", new { p_search = string.IsNullOrWhiteSpace(search) ? null : search });

        public async Task CreateAsync(string name, string tag, string description) =>
            Apply(await _client.RpcAsync<GuildInfo>("create_guild", new { p_name = name, p_tag = tag, p_description = description }));

        public async Task JoinAsync(string guildId) =>
            Apply(await _client.RpcAsync<GuildInfo>("join_guild", new { p_guild_id = guildId }));

        public async Task LeaveAsync()
        {
            await _client.RpcAsync<object>("leave_guild");
            Apply(null);
        }

        public async Task KickAsync(string userId) =>
            Apply(await _client.RpcAsync<GuildInfo>("kick_guild_member", new { p_user_id = userId }));

        /// <summary>Joins an open or closed guild with the code its leader or officers shared.</summary>
        public async Task JoinByCodeAsync(string code) =>
            Apply(await _client.RpcAsync<GuildInfo>("join_guild_by_code", new { p_code = code }));

        public async Task UpdateSettingsAsync(bool isOpen, string description) =>
            Apply(await _client.RpcAsync<GuildInfo>("update_guild_settings", new { p_is_open = isOpen, p_description = description }));

        public async Task RotateInviteCodeAsync() =>
            Apply(await _client.RpcAsync<GuildInfo>("rotate_guild_invite_code"));

        /// <summary>
        /// Reasons: offensive_name, harassment, spam, cheating, other. Pass the chat message id
        /// to attach it as evidence (it must be the reported player's message in your guild).
        /// </summary>
        public Task ReportAsync(string userId, string reason, string details = "", long? messageId = null) =>
            _client.RpcAsync<object>("report_player",
                new { p_user_id = userId, p_reason = reason, p_details = details, p_message_id = messageId });

        public async Task SetRoleAsync(string userId, string role) =>
            Apply(await _client.RpcAsync<GuildInfo>("set_guild_role", new { p_user_id = userId, p_role = role }));

        public async Task SendAsync(string body)
        {
            var message = await _client.RpcAsync<GuildMessage>("send_guild_message", new { p_body = body });
            Receive(message);  // Realtime echo of our own message is de-duplicated by id
        }

        public void Dispose() => Subscribe(null);

        void Apply(GuildInfo guild)
        {
            string previousId = Guild?.Id;
            Guild = guild;
            _seenMessageIds.Clear();
            if (guild != null)
                foreach (var m in guild.Messages) _seenMessageIds.Add(m.Id);
            if (guild?.Id != previousId) Subscribe(guild?.Id);
            GuildChanged?.Invoke(guild);
        }

        void Subscribe(string guildId)
        {
            if (_chat != null)
            {
                _chat.Inserted -= OnChatInsert;
                _chat.Dispose();
                _chat = null;
            }
            if (guildId == null) return;
            _chat = new SupabaseRealtime(_client, "guild_messages", "guild_id=eq." + guildId);
            _chat.Inserted += OnChatInsert;
            _chat.Start();
        }

        void OnChatInsert(JObject record)
        {
            try
            {
                var message = new GuildMessage
                {
                    Id = (long)record["id"],
                    UserId = (string)record["user_id"],
                    AuthorName = (string)record["author_name"],
                    Body = (string)record["body"],
                    CreatedMs = ParseTimestampMs((string)record["created_at"]),
                };
                Receive(message);
                // Joins, leaves and kicks are announced as system messages: refresh the roster.
                if (message.IsSystem) _ = RefreshSafeAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Guild] Bad chat record: {e.Message}");
            }
        }

        void Receive(GuildMessage message)
        {
            if (Guild == null || !_seenMessageIds.Add(message.Id)) return;
            Guild.Messages.Add(message);
            if (Guild.Messages.Count > MaxCachedMessages) Guild.Messages.RemoveAt(0);
            MessageReceived?.Invoke(message);
        }

        async Task RefreshSafeAsync()
        {
            try { await RefreshAsync(); }
            catch (Exception e) { Debug.LogWarning($"[Guild] Refresh failed: {e.Message}"); }
        }

        static long ParseTimestampMs(string value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
