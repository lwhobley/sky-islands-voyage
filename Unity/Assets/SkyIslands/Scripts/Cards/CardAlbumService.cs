using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SkyIslands.Backend;
using SkyIslands.Core;

namespace SkyIslands.Cards
{
    public sealed class CardSeason
    {
        [JsonProperty("id")] public int Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("ends_at_ms")] public long EndsAtMs;
    }

    public sealed class AlbumCard
    {
        [JsonProperty("card_id")] public int CardId;
        [JsonProperty("name")] public string Name;
        [JsonProperty("rarity")] public int Rarity;
        [JsonProperty("craft_cost")] public int CraftCost;
        [JsonProperty("owned")] public bool Owned;
    }

    public sealed class AlbumSet
    {
        [JsonProperty("set_id")] public int SetId;
        [JsonProperty("name")] public string Name;
        [JsonProperty("reward_rolls")] public int RewardRolls;
        [JsonProperty("reward_crystals"), JsonConverter(typeof(BigIntegerStringConverter))] public BigInteger RewardCrystals;
        [JsonProperty("completed")] public bool Completed;
        [JsonProperty("cards")] public List<AlbumCard> Cards = new List<AlbumCard>();
    }

    public sealed class CardAlbum
    {
        [JsonProperty("season")] public CardSeason Season;  // null between seasons
        [JsonProperty("card_dust")] public long CardDust;
        [JsonProperty("card_packs")] public int CardPacks;
        [JsonProperty("sets")] public List<AlbumSet> Sets = new List<AlbumSet>();
    }

    public sealed class DrawnCard
    {
        [JsonProperty("card_id")] public int CardId;
        [JsonProperty("set_id")] public int SetId;
        [JsonProperty("name")] public string Name;
        [JsonProperty("rarity")] public int Rarity;
        [JsonProperty("duplicate")] public bool Duplicate;
    }

    public sealed class CompletedSet
    {
        [JsonProperty("set_id")] public int SetId;
        [JsonProperty("name")] public string Name;
        [JsonProperty("reward_rolls")] public int RewardRolls;
        [JsonProperty("reward_crystals"), JsonConverter(typeof(BigIntegerStringConverter))] public BigInteger RewardCrystals;
    }

    public sealed class PackResult
    {
        [JsonProperty("cards")] public List<DrawnCard> Cards = new List<DrawnCard>();
        [JsonProperty("dust_gained")] public long DustGained;
        [JsonProperty("completed_sets")] public List<CompletedSet> CompletedSets = new List<CompletedSet>();
        [JsonProperty("state")] public PlayerStateSnapshot State;
    }

    /// <summary>Card album: packs come from Storm Spirit rolls; all draws happen server-side.</summary>
    public sealed class CardAlbumService
    {
        readonly SupabaseClient _client;
        readonly GameEconomyManager _economy;

        public CardAlbumService(SupabaseClient client, GameEconomyManager economy)
        {
            _client = client;
            _economy = economy;
        }

        public async Task<CardAlbum> LoadAsync()
        {
            await _economy.SyncAsync();  // packs won on offline rolls must reach the server first
            return await _client.RpcAsync<CardAlbum>("get_card_album");
        }

        public async Task<PackResult> OpenPackAsync()
        {
            await _economy.SyncAsync();
            var result = await _client.RpcAsync<PackResult>("open_card_pack");
            _economy.ApplyServerState(result.State);
            return result;
        }

        public async Task<List<CompletedSet>> CraftAsync(int cardId)
        {
            var result = await _client.RpcAsync<PackResult>("craft_card", new { p_card_id = cardId });
            _economy.ApplyServerState(result.State);
            return result.CompletedSets;
        }
    }
}
