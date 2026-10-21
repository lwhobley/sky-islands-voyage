using System.Threading.Tasks;
using SkyIslands.Core;

namespace SkyIslands.Backend
{
    /// <summary>
    /// Calls the NestJS service (server/) for store purchases, and registers push tokens.
    ///
    /// Purchase flow: tag the purchase with the player's id (Apple appAccountToken /
    /// Google obfuscatedAccountId = SupabaseClient.UserId), let the store complete it, then
    /// call Verify*. Finish/acknowledge the transaction in the store SDK only after this
    /// returns; on 422 the purchase is invalid, on other errors retry later.
    /// </summary>
    public sealed class GameServicesClient
    {
        readonly SupabaseClient _client;
        readonly GameEconomyManager _economy;
        readonly string _serverUrl;

        public GameServicesClient(SupabaseClient client, GameEconomyManager economy, string serverUrl)
        {
            _client = client;
            _economy = economy;
            _serverUrl = serverUrl.TrimEnd('/');
        }

        public Task VerifyApplePurchaseAsync(string transactionId) =>
            VerifyAsync(new { store = "apple", transactionId });

        public Task VerifyGooglePurchaseAsync(string productId, string purchaseToken) =>
            VerifyAsync(new { store = "google", productId, purchaseToken });

        /// <summary>3–20 characters; the server rejects markup and impersonation terms.</summary>
        public async Task SetDisplayNameAsync(string name)
        {
            await _economy.SyncAsync();
            _economy.ApplyServerState(await _client.RpcAsync<PlayerStateSnapshot>("set_display_name", new { p_name = name }));
        }

        /// <summary>Call with the FCM registration token whenever Firebase issues one.</summary>
        public Task RegisterPushTokenAsync(string token, bool isIos) =>
            _client.RpcAsync<object>("register_push_token", new { p_token = token, p_platform = isIos ? "ios" : "android" });

        async Task VerifyAsync(object request)
        {
            // Rolls taken offline must be on the server before the granted state comes back.
            await _economy.SyncAsync();
            var state = await _client.PostAsync<PlayerStateSnapshot>(_serverUrl + "/iap/verify", request);
            _economy.ApplyServerState(state);
        }
    }
}
