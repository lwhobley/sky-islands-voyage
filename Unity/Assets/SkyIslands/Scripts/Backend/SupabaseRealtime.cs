using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SkyIslands.Backend
{
    /// <summary>
    /// Supabase Realtime (Phoenix protocol, vsn 1.0.0) subscriber for INSERTs on one table,
    /// filtered server-side and RLS-checked with the player's JWT. Reconnects with backoff.
    /// Started from the Unity main thread, so events are raised on the main thread.
    /// </summary>
    public sealed class SupabaseRealtime : IDisposable
    {
        const int HeartbeatMs = 25_000;

        readonly SupabaseClient _client;
        readonly string _table;
        readonly string _filter;
        readonly string _topic;
        readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        CancellationTokenSource _cts;
        ClientWebSocket _ws;
        int _ref;

        public SupabaseRealtime(SupabaseClient client, string table, string filter)
        {
            _client = client;
            _table = table;
            _filter = filter;
            _topic = "realtime:" + table;
        }

        /// <summary>The inserted row (Realtime's payload.data.record).</summary>
        public event Action<JObject> Inserted;

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _client.AccessTokenChanged += OnAccessTokenChanged;
            _ = RunAsync(_cts.Token);
        }

        public void Dispose()
        {
            _client.AccessTokenChanged -= OnAccessTokenChanged;
            _cts?.Cancel();
            _cts = null;
        }

        async Task RunAsync(CancellationToken ct)
        {
            int backoffSeconds = 1;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndListenAsync(ct, () => backoffSeconds = 1);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Debug.LogWarning($"[Realtime] {e.Message}; reconnecting in {backoffSeconds}s");
                }
                catch (Exception)
                {
                    break;  // cancelled
                }

                try { await Task.Delay(backoffSeconds * 1000, ct); }
                catch (OperationCanceledException) { break; }
                backoffSeconds = Math.Min(backoffSeconds * 2, 60);
            }
        }

        async Task ConnectAndListenAsync(CancellationToken ct, Action onJoined)
        {
            await _client.EnsureSessionAsync();  // never join with an expired JWT
            using var ws = new ClientWebSocket();
            string baseUrl = _client.Url.Replace("https://", "wss://").Replace("http://", "ws://");
            await ws.ConnectAsync(new Uri($"{baseUrl}/realtime/v1/websocket?apikey={Uri.EscapeDataString(_client.AnonKey)}&vsn=1.0.0"), ct);
            _ws = ws;

            await SendAsync(new
            {
                topic = _topic,
                @event = "phx_join",
                payload = new
                {
                    config = new
                    {
                        postgres_changes = new[] { new { @event = "INSERT", schema = "public", table = _table, filter = _filter } },
                    },
                    access_token = _client.AccessToken,
                },
                @ref = NextRef(),
            }, ct);

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = HeartbeatAsync(heartbeatCts.Token);
            try
            {
                await ReceiveLoopAsync(ws, onJoined, ct);
            }
            finally
            {
                heartbeatCts.Cancel();
                _ws = null;
            }
        }

        async Task ReceiveLoopAsync(ClientWebSocket ws, Action onJoined, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (res.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed by server");
                    message.Write(buffer, 0, res.Count);
                } while (!res.EndOfMessage);

                // Keep timestamps as their original ISO strings (no local-time conversion).
                var msg = JsonConvert.DeserializeObject<JObject>(
                    Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length),
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                string ev = (string)msg["event"];
                if (ev == "postgres_changes")
                {
                    if (msg["payload"]?["data"]?["record"] is JObject record) Inserted?.Invoke(record);
                }
                else if (ev == "phx_reply" && (string)msg["topic"] == _topic)
                {
                    if ((string)msg["payload"]?["status"] == "ok") onJoined();
                    else Debug.LogWarning($"[Realtime] join rejected: {msg["payload"]}");
                }
                else if (ev == "phx_error" || ev == "phx_close")
                {
                    throw new WebSocketException($"channel {ev}");
                }
            }
        }

        async Task HeartbeatAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatMs, ct);
                    await SendAsync(new { topic = "phoenix", @event = "heartbeat", payload = new { }, @ref = NextRef() }, ct);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { }
            catch (Exception e) { Debug.LogWarning($"[Realtime] heartbeat failed: {e.Message}"); }
        }

        async void OnAccessTokenChanged(string token)
        {
            if (_ws?.State != WebSocketState.Open) return;
            try
            {
                await SendAsync(new { topic = _topic, @event = "access_token", payload = new { access_token = token }, @ref = NextRef() },
                                _cts?.Token ?? CancellationToken.None);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Realtime] token update failed: {e.Message}");
            }
        }

        async Task SendAsync(object message, CancellationToken ct)
        {
            var ws = _ws ?? throw new WebSocketException("not connected");
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
            await _sendLock.WaitAsync(ct);
            try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct); }
            finally { _sendLock.Release(); }
        }

        string NextRef() => (++_ref).ToString();
    }
}
