using System;
using SkyIslands.Backend;
using SkyIslands.Cards;
using SkyIslands.Core;
using SkyIslands.Multiplayer;
using SkyIslands.Progression;
using UnityEngine;

namespace SkyIslands
{
    /// <summary>Composition root: signs in, wires the systems, and performs the first sync.</summary>
    public sealed class SkyIslandsBootstrap : MonoBehaviour
    {
        [SerializeField] string supabaseUrl = "https://YOUR-PROJECT-REF.supabase.co";
        [SerializeField, Tooltip("Publishable / anon key — never the service-role key.")]
        string supabaseAnonKey = "";
        [SerializeField, Tooltip("Base URL of the NestJS service in server/ (purchases).")]
        string gameServerUrl = "https://api.example.com";

        [SerializeField] GameEconomyManager economy;
        [SerializeField] IslandProgressionController islands;
        [SerializeField] RaidAndShieldService raids;

        public SupabaseClient Client { get; private set; }
        public GameEconomyManager Economy => economy;
        public IslandProgressionController Islands => islands;
        public RaidAndShieldService Raids => raids;
        public GuildService Guilds { get; private set; }
        public CardAlbumService Cards { get; private set; }
        public GameServicesClient GameServices { get; private set; }

        /// <summary>True once every system is wired (possibly still offline).</summary>
        public bool IsReady { get; private set; }

        public event Action Ready;

        async void Start()
        {
            Client = new SupabaseClient(supabaseUrl, supabaseAnonKey);
            try
            {
                await Client.EnsureSessionAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Boot] Starting offline: {e.Message}");
            }

            if (Client.UserId == null)
            {
                Debug.LogError("[Boot] First launch needs a connection to create the player.");
                return;
            }

            economy.Initialize(Client);   // restores the encrypted offline save
            islands.Initialize(Client);
            raids.Initialize(Client);
            Guilds = new GuildService(Client);
            Cards = new CardAlbumService(Client, economy);
            GameServices = new GameServicesClient(Client, economy, gameServerUrl);

            IsReady = true;
            Ready?.Invoke();

            try
            {
                await economy.SyncAsync();
                await Guilds.RefreshAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Boot] Initial sync deferred: {e.Message}");
            }
        }

        void OnDestroy() => Guilds?.Dispose();
    }
}
