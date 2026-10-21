using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkyIslands.Core;
using UnityEngine.Networking;

namespace SkyIslands.Backend
{
    /// <summary>
    /// Minimal Supabase client: anonymous auth + PostgREST RPC over UnityWebRequest.
    /// All game mutations are server RPCs defined in supabase/schema.sql.
    /// </summary>
    public sealed class SupabaseClient
    {
        const string SessionSaveKey = "session";

        Session _session;

        public SupabaseClient(string url, string anonKey)
        {
            Url = url.TrimEnd('/');
            AnonKey = anonKey;
        }

        public string Url { get; }
        public string AnonKey { get; }
        public string UserId => _session?.UserId;
        public string AccessToken => _session?.AccessToken;

        /// <summary>Raised after a sign-in or token refresh (Realtime re-authenticates on it).</summary>
        public event Action<string> AccessTokenChanged;

        /// <summary>
        /// Restores the saved session (refreshing it if expired) or signs in anonymously.
        /// Throws when offline; UserId is still available afterwards if a session was saved.
        /// </summary>
        public async Task EnsureSessionAsync()
        {
            _session ??= SecureSave.Load<Session>(SessionSaveKey);
            if (_session == null)
            {
                StoreSession(await SendAsync("POST", Url + "/auth/v1/signup", "{}", authenticated: false));
                return;
            }
            if (_session.IsExpiring) await RefreshAsync();
        }

        public Task<T> RpcAsync<T>(string function, object args = null) =>
            PostAsync<T>(Url + "/rest/v1/rpc/" + function, args);

        /// <summary>
        /// Authenticated JSON POST to any endpoint that accepts the player's Supabase access token
        /// (PostgREST, or the game's NestJS service). Refreshes and retries once on 401.
        /// </summary>
        public async Task<T> PostAsync<T>(string url, object args = null)
        {
            if (_session == null) throw new InvalidOperationException("No session; call EnsureSessionAsync first.");
            if (_session.IsExpiring) await RefreshAsync();

            string body = JsonConvert.SerializeObject(args ?? new object());
            string json;
            try
            {
                json = await SendAsync("POST", url, body, authenticated: true);
            }
            catch (SupabaseException e) when (e.Status == 401)
            {
                await RefreshAsync();
                json = await SendAsync("POST", url, body, authenticated: true);
            }
            return string.IsNullOrEmpty(json) ? default : JsonConvert.DeserializeObject<T>(json);
        }

        async Task RefreshAsync()
        {
            string body = JsonConvert.SerializeObject(new { refresh_token = _session.RefreshToken });
            StoreSession(await SendAsync("POST", Url + "/auth/v1/token?grant_type=refresh_token", body, authenticated: false));
        }

        void StoreSession(string json)
        {
            var j = JObject.Parse(json);
            _session = new Session
            {
                AccessToken = (string)j["access_token"],
                RefreshToken = (string)j["refresh_token"],
                ExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)j["expires_in"] * 1000,
                UserId = (string)j["user"]?["id"],
            };
            SecureSave.Save(SessionSaveKey, _session);
            AccessTokenChanged?.Invoke(_session.AccessToken);
        }

        async Task<string> SendAsync(string method, string url, string body, bool authenticated)
        {
            using var req = new UnityWebRequest(url, method)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body ?? "")),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 15,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", AnonKey);
            req.SetRequestHeader("Authorization", "Bearer " + (authenticated ? _session.AccessToken : AnonKey));

            await req.SendWebRequest().AsTask();

            if (req.result == UnityWebRequest.Result.ConnectionError)
                throw new SupabaseException(0, "network_error", req.error);
            string text = req.downloadHandler.text;
            if (req.responseCode >= 400)
                throw SupabaseException.FromResponse(req.responseCode, text);
            return text;
        }

        sealed class Session
        {
            public string AccessToken;
            public string RefreshToken;
            public long ExpiresAtMs;
            public string UserId;

            [JsonIgnore]
            public bool IsExpiring => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > ExpiresAtMs - 60_000;
        }
    }

    public sealed class SupabaseException : Exception
    {
        public SupabaseException(long status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }

        public long Status { get; }

        /// <summary>Postgres SQLSTATE / PostgREST code, e.g. "P0001".</summary>
        public string Code { get; }

        internal static SupabaseException FromResponse(long status, string body)
        {
            try
            {
                var j = JObject.Parse(body);
                string message = (string)j["message"] ?? (string)j["msg"] ?? (string)j["error_description"] ??
                                 (string)j["error"] ?? body;
                return new SupabaseException(status, (string)j["code"] ?? (string)j["error"], message);
            }
            catch (JsonException)
            {
                return new SupabaseException(status, null, body);
            }
        }
    }

    /// <summary>Flat player snapshot returned by every mutating RPC (game.player_state in schema.sql).</summary>
    public sealed class PlayerStateSnapshot
    {
        [JsonProperty("server_time_ms")] public long ServerTimeMs;
        [JsonProperty("user_id")] public string UserId;
        [JsonProperty("display_name")] public string DisplayName;
        [JsonProperty("crystals"), JsonConverter(typeof(BigIntegerStringConverter))] public BigInteger Crystals;
        [JsonProperty("shields")] public int Shields;
        [JsonProperty("card_dust")] public long CardDust;
        [JsonProperty("rolls")] public int Rolls;
        [JsonProperty("regen_anchor_ms")] public long RegenAnchorMs;
        [JsonProperty("state_at_ms")] public long StateAtMs;
        [JsonProperty("rng_seed")] public string RngSeed;
        [JsonProperty("roll_index")] public long RollIndex;
        [JsonProperty("dry_streak")] public int DryStreak;
        [JsonProperty("pending_attacks")] public List<int> PendingAttacks = new List<int>();
        [JsonProperty("card_packs")] public int CardPacks;
        [JsonProperty("island_level")] public int IslandLevel;
        [JsonProperty("tiers")] public int[] Tiers;
        [JsonProperty("damaged")] public bool[] Damaged;
        [JsonProperty("campaign_completed")] public bool CampaignCompleted;

        // Present only on sync_rolls responses.
        [JsonProperty("accepted")] public int? Accepted;
        [JsonProperty("rejected_reason")] public string RejectedReason;
    }

    /// <summary>Crystal amounts exceed 64 bits by island ~150, so they travel as strings.</summary>
    public sealed class BigIntegerStringConverter : JsonConverter<BigInteger>
    {
        public override void WriteJson(JsonWriter writer, BigInteger value, JsonSerializer serializer) =>
            writer.WriteValue(value.ToString(CultureInfo.InvariantCulture));

        public override BigInteger ReadJson(JsonReader reader, Type objectType, BigInteger existingValue,
                                            bool hasExistingValue, JsonSerializer serializer) =>
            reader.Value == null
                ? BigInteger.Zero
                : BigInteger.Parse(Convert.ToString(reader.Value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    static class UnityWebRequestExtensions
    {
        public static Task AsTask(this UnityWebRequestAsyncOperation op)
        {
            var tcs = new TaskCompletionSource<bool>();
            if (op.isDone) tcs.SetResult(true);
            else op.completed += _ => tcs.SetResult(true);
            return tcs.Task;
        }
    }
}
