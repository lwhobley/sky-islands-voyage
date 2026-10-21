using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkyIslands.Backend;
using SkyIslands.Core;
using UnityEngine;

namespace SkyIslands.Multiplayer
{
    /// <summary>Attacker-side outcome of public.perform_raid.</summary>
    public sealed class RaidResult
    {
        [JsonProperty("raid_id")] public string RaidId;
        [JsonProperty("defender_id")] public string DefenderId;  // null for NPC outposts
        [JsonProperty("defender_name")] public string DefenderName;
        [JsonProperty("is_npc")] public bool IsNpc;
        [JsonProperty("defender_island_level")] public int DefenderIslandLevel;
        [JsonProperty("defender_tiers")] public int[] DefenderTiers;
        [JsonProperty("defender_damaged")] public bool[] DefenderDamaged;  // after this raid
        [JsonProperty("shielded")] public bool Shielded;
        [JsonProperty("multiplier")] public int Multiplier;
        [JsonProperty("crystals_awarded"), JsonConverter(typeof(BigIntegerStringConverter))] public BigInteger CrystalsAwarded;
        [JsonProperty("damaged_slot")] public int? DamagedSlot;
        [JsonProperty("is_revenge")] public bool IsRevenge;
        [JsonProperty("state")] public PlayerStateSnapshot State;
    }

    /// <summary>Defender-side payload (raid_history.notification), delivered over Realtime.</summary>
    public sealed class RaidNotification
    {
        [JsonProperty("type")] public string Type;          // "revenge" | "shield_block"
        [JsonProperty("priority")] public string Priority;  // "high" | "normal"
        [JsonProperty("raid_id")] public string RaidId;
        [JsonProperty("attacker_id")] public string AttackerId;
        [JsonProperty("attacker_name")] public string AttackerName;
        [JsonProperty("crystals_lost"), JsonConverter(typeof(BigIntegerStringConverter))] public BigInteger CrystalsLost;
        [JsonProperty("damaged_slot")] public int? DamagedSlot;
        [JsonProperty("shielded")] public bool Shielded;
        [JsonProperty("is_revenge")] public bool IsRevenge;
        [JsonProperty("title")] public string Title;
        [JsonProperty("body")] public string Body;

        public bool IsHighPriority => Priority == "high";
    }

    /// <summary>
    /// Multiplayer raid loop. Attacks are resolved entirely server-side (target matching,
    /// shield check, 80% mitigation, structure damage, crystal transfer); this service
    /// dispatches them, plays the deflection effect, and listens for raids against the
    /// player to maintain the revenge queue.
    /// </summary>
    public sealed class RaidAndShieldService : MonoBehaviour
    {
        const int MaxRevengeTargets = 20;

        [SerializeField] GameEconomyManager economy;
        [SerializeField, Tooltip("Played when our Wind-Cannon hits a Cloud-Shield.")]
        ParticleSystem deflectionVfx;

        readonly List<RaidNotification> _revengeTargets = new List<RaidNotification>();
        SupabaseClient _client;
        SupabaseRealtime _realtime;
        bool _raidInFlight;

        /// <summary>Our raid resolved: animate cannon → deflection (Shielded) or structure hit.</summary>
        public event Action<RaidResult> RaidResolved;

        /// <summary>Someone raided us unshielded: high-priority revenge prompt.</summary>
        public event Action<RaidNotification> RevengeAvailable;

        /// <summary>Someone raided us and our Cloud-Shield absorbed 80%.</summary>
        public event Action<RaidNotification> ShieldAbsorbedRaid;

        public event Action RevengeTargetsChanged;

        public IReadOnlyList<RaidNotification> RevengeTargets => _revengeTargets;
        public bool CanRaid => !_raidInFlight && economy.PendingAttacks > 0;

        public void Initialize(SupabaseClient client)
        {
            _client = client;
            _realtime = new SupabaseRealtime(client, "raid_history", "defender_id=eq." + client.UserId);
            _realtime.Inserted += OnRaidAgainstUs;
            _realtime.Start();
            _ = RefreshRevengeTargetsSafeAsync();
        }

        /// <summary>Fires the oldest Wind-Cannon hit at a server-matched target.</summary>
        public Task<RaidResult> LaunchRaidAsync() => RaidAsync(null);

        /// <summary>Fires the oldest Wind-Cannon hit back at the player who raided us.</summary>
        public Task<RaidResult> LaunchRevengeAsync(RaidNotification target) => RaidAsync(target.RaidId);

        async Task<RaidResult> RaidAsync(string revengeOf)
        {
            if (_raidInFlight) throw new InvalidOperationException("A raid is already in progress.");
            _raidInFlight = true;
            try
            {
                // The server only knows about Wind-Cannon hits that have been synced.
                await economy.SyncAsync();
                RaidResult result;
                try
                {
                    result = await _client.RpcAsync<RaidResult>("perform_raid", new { p_revenge_of = revengeOf });
                }
                catch (SupabaseException e) when (revengeOf != null && e.Message == "revenge_unavailable")
                {
                    RemoveRevengeTarget(revengeOf);
                    throw;
                }

                economy.ApplyServerState(result.State);
                if (revengeOf != null) RemoveRevengeTarget(revengeOf);
                if (result.Shielded && deflectionVfx != null) deflectionVfx.Play();
                RaidResolved?.Invoke(result);
                return result;
            }
            finally
            {
                _raidInFlight = false;
            }
        }

        public async Task RefreshRevengeTargetsAsync()
        {
            var list = await _client.RpcAsync<List<RaidNotification>>("list_revenge_targets");
            _revengeTargets.Clear();
            _revengeTargets.AddRange(list);
            RevengeTargetsChanged?.Invoke();
        }

        async void OnRaidAgainstUs(JObject record)
        {
            try
            {
                // jsonb normally arrives as an object; tolerate a string-encoded payload.
                JToken token = record["notification"];
                if (token == null) return;
                var n = (token.Type == JTokenType.String ? JToken.Parse((string)token) : token).ToObject<RaidNotification>();

                if (n.Shielded)
                {
                    ShieldAbsorbedRaid?.Invoke(n);
                }
                else
                {
                    _revengeTargets.RemoveAll(t => t.RaidId == n.RaidId);
                    _revengeTargets.Insert(0, n);
                    if (_revengeTargets.Count > MaxRevengeTargets) _revengeTargets.RemoveAt(_revengeTargets.Count - 1);
                    RevengeAvailable?.Invoke(n);
                    RevengeTargetsChanged?.Invoke();
                }

                // Crystals, shields and structure damage changed server-side; pull them in.
                await economy.SyncAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Raids] Incoming raid handling failed: {e.Message}");
            }
        }

        void RemoveRevengeTarget(string raidId)
        {
            if (_revengeTargets.RemoveAll(t => t.RaidId == raidId) > 0) RevengeTargetsChanged?.Invoke();
        }

        async Task RefreshRevengeTargetsSafeAsync()
        {
            try { await RefreshRevengeTargetsAsync(); }
            catch (Exception e) { Debug.LogWarning($"[Raids] Revenge list refresh deferred: {e.Message}"); }
        }

        void OnApplicationPause(bool paused)
        {
            // Raids that landed while we were suspended were missed by the socket.
            if (!paused && _client != null) _ = RefreshRevengeTargetsSafeAsync();
        }

        void OnDestroy() => _realtime?.Dispose();
    }
}
