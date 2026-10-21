using System;
using System.Collections.Generic;
using System.Linq;
using SkyIslands.Cards;
using SkyIslands.Core;

namespace SkyIslands.UI
{
    /// <summary>Season album: sets, owned cards, pack opening and dust crafting.</summary>
    public sealed class AlbumViewModel : ViewModelBase
    {
        readonly CardAlbumService _cards;
        readonly GameEconomyManager _economy;

        public AlbumViewModel(CardAlbumService cards, GameEconomyManager economy)
        {
            _cards = cards;
            _economy = economy;
            _economy.StateChanged += NotifyChanged;
        }

        public CardAlbum Album { get; private set; }
        public int Packs => _economy.CardPacks;
        public bool CanOpenPack => !IsBusy && Packs > 0 && Album?.Season != null;
        public string LastPackText { get; private set; }

        public string SeasonText
        {
            get
            {
                if (Album == null) return "Loading album…";
                if (Album.Season == null) return "The next card season is on its way.";
                var left = DateTimeOffset.FromUnixTimeMilliseconds(Album.Season.EndsAtMs) - DateTimeOffset.UtcNow;
                return $"{Album.Season.Name} · ends in {Math.Max(0, left.Days)}d {Math.Max(0, left.Hours)}h";
            }
        }

        public string DustText => Album == null ? "" : $"{Album.CardDust} dust · {Packs} pack{(Packs == 1 ? "" : "s")}";
        public IReadOnlyList<AlbumSet> Sets => Album?.Sets ?? (IReadOnlyList<AlbumSet>)Array.Empty<AlbumSet>();

        public bool CanCraft(AlbumCard card) => !IsBusy && !card.Owned && Album != null && Album.CardDust >= card.CraftCost;

        public void Load() => Run(async () => Album = await _cards.LoadAsync());

        public void OpenPack() => Run(async () =>
        {
            var result = await _cards.OpenPackAsync();
            LastPackText = string.Join("\n", result.Cards.Select(c =>
                               $"{new string('*', c.Rarity)} {c.Name}" + (c.Duplicate ? $"  (duplicate: +{10 * c.Rarity} dust)" : "  NEW!")));
            Announce(result.CompletedSets);
            Album = await _cards.LoadAsync();
        });

        public void Craft(AlbumCard card) => Run(async () =>
        {
            Announce(await _cards.CraftAsync(card.CardId));
            Album = await _cards.LoadAsync();
        });

        public override void Dispose() => _economy.StateChanged -= NotifyChanged;

        void Announce(List<CompletedSet> sets)
        {
            foreach (var s in sets)
                ShowCelebration($"{s.Name} complete!\n+{s.RewardRolls} rolls · +{UiText.Short(s.RewardCrystals)} crystals");
        }
    }
}
