using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HapticAI.Profiles;
using UnityEngine;

namespace HapticAI.Net
{
    /// <summary>
    /// The headset's end of the session socket (<c>ws://server/ws</c>). One connection for the
    /// whole session: the scene goes up once, profile clips stream down as they are synthesised,
    /// spray readings go up several times a second and selections come back - none of which fits
    /// a request per exchange, and all of which the old HTTP path paid a fresh connection for.
    ///
    /// Built on <see cref="ClientWebSocket"/> so it needs no package. Socket I/O runs on thread-pool
    /// tasks; everything the rest of the scene sees is raised from <see cref="Pump"/>, which the
    /// owning MonoBehaviour calls once per frame, so handlers may touch Unity objects freely.
    ///
    /// Reconnects with backoff for as long as it is started. After a reconnect the owner is told
    /// (<see cref="Connected"/>) and is expected to resend the scene: the server keeps no state
    /// across connections, but replanning a scene it has already seen is sub-second.
    /// </summary>
    public sealed class HapticSessionClient : IDisposable
    {
        public enum Status { Stopped, Connecting, Connected, Reconnecting }

        const int ReceiveBufferBytes = 64 * 1024;
        const float PingIntervalSeconds = 5f;

        readonly Uri _uri;
        readonly int _connectTimeoutSeconds;
        readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        ClientWebSocket _socket;
        CancellationTokenSource _lifetime;
        Task _runLoop;
        float _nextPingAt;
        int _reconnects;

        public Status State { get; private set; } = Status.Stopped;
        public string Url => _uri.ToString();
        /// <summary>From the server's welcome: "live:<model>", "http:<model>" or "none".</summary>
        public string ServerTransport { get; private set; } = "(not connected)";
        public bool ServerHasGemini { get; private set; }
        public bool ServerHasModel { get; private set; }
        public float LastRoundTripMs { get; private set; } = -1f;
        public int MessagesSent { get; private set; }
        public int MessagesReceived { get; private set; }
        public string LastError { get; private set; }

        /// <summary>Raised on every (re)connection, on the main thread.</summary>
        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<HapticWire.Profiles> ProfilesReceived;
        /// <summary>(set id, profile id, source, wav bytes)</summary>
        public event Action<string, string, string, byte[]> ClipReceived;
        public event Action<HapticWire.Select> SelectionReceived;
        public event Action<string> ServerError;

        public HapticSessionClient(string baseUrl, int connectTimeoutSeconds = 10)
        {
            _uri = ToSocketUri(baseUrl);
            _connectTimeoutSeconds = connectTimeoutSeconds < 1 ? 10 : connectTimeoutSeconds;
        }

        /// <summary>"http://host:port" → "ws://host:port/ws" (and https → wss). A ws:// URL is kept.</summary>
        public static Uri ToSocketUri(string baseUrl)
        {
            string url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) url = "ws://" + url.Substring(7);
            else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "wss://" + url.Substring(8);
            else if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                     !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) url = "ws://" + url;
            if (!url.EndsWith("/ws", StringComparison.Ordinal)) url += "/ws";
            return new Uri(url);
        }

        // --- Lifecycle ------------------------------------------------------------------------

        public void Start()
        {
            if (_runLoop != null) return;
            _lifetime = new CancellationTokenSource();
            State = Status.Connecting;
            _runLoop = Task.Run(() => RunAsync(_lifetime.Token));
        }

        public void Stop()
        {
            if (_lifetime == null) return;
            _lifetime.Cancel();
            try { _socket?.Abort(); } catch { /* tearing down */ }
            _socket = null;
            _runLoop = null;
            State = Status.Stopped;
        }

        public void Dispose()
        {
            Stop();
            _lifetime?.Dispose();
            _lifetime = null;
        }

        /// <summary>Dispatch queued socket events on the caller's (main) thread. Call once per frame.</summary>
        public void Pump()
        {
            while (_mainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }

            if (State == Status.Connected && Time.realtimeSinceStartup >= _nextPingAt)
            {
                _nextPingAt = Time.realtimeSinceStartup + PingIntervalSeconds;
                Send(new HapticWire.Ping { t = NowMs() });
            }
        }

        // --- Sending --------------------------------------------------------------------------

        public bool Send<T>(T message) where T : class => SendText(JsonUtility.ToJson(message));

        /// <summary>Queue a text frame. False (and nothing sent) when not connected.</summary>
        public bool SendText(string json)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open || State != Status.Connected) return false;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            _ = SendAsync(socket, bytes);
            return true;
        }

        async Task SendAsync(ClientWebSocket socket, byte[] bytes)
        {
            // ClientWebSocket allows one outstanding send; serialise them.
            await _sendGate.WaitAsync();
            try
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    _lifetime?.Token ?? CancellationToken.None);
                MessagesSent++;
            }
            catch (Exception e)
            {
                LastError = e.Message;   // the receive loop notices the dead socket and reconnects
            }
            finally
            {
                _sendGate.Release();
            }
        }

        // --- Connection loop ------------------------------------------------------------------

        async Task RunAsync(CancellationToken ct)
        {
            float backoff = 1f;
            while (!ct.IsCancellationRequested)
            {
                var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                try
                {
                    using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        connectCts.CancelAfter(TimeSpan.FromSeconds(_connectTimeoutSeconds));
                        await socket.ConnectAsync(_uri, connectCts.Token);
                    }
                    _socket = socket;
                    backoff = 1f;
                    int generation = ++_reconnects;
                    Post(() =>
                    {
                        State = Status.Connected;
                        _nextPingAt = Time.realtimeSinceStartup + PingIntervalSeconds;
                        Debug.Log($"[HapticAI] Session socket connected to {_uri} (connection #{generation}).");
                        Send(new HapticWire.Hello());
                        Connected?.Invoke();
                    });

                    await ReceiveLoopAsync(socket, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                }
                finally
                {
                    if (ReferenceEquals(_socket, socket)) _socket = null;
                    try { socket.Dispose(); } catch { /* already gone */ }
                }

                if (ct.IsCancellationRequested) break;
                string reason = LastError ?? "closed";
                Post(() =>
                {
                    State = Status.Reconnecting;
                    Debug.LogWarning($"[HapticAI] Session socket lost ({reason}); reconnecting.");
                    Disconnected?.Invoke(reason);
                });

                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
                catch (OperationCanceledException) { break; }
                backoff = Math.Min(backoff * 2f, 10f);
            }
            Post(() => State = Status.Stopped);
        }

        async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[ReceiveBufferBytes];
            var message = new MemoryStream();
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        LastError = $"server closed ({result.CloseStatus})";
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                MessagesReceived++;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    Post(() => HandleText(text));
                }
                else
                {
                    byte[] frame = message.ToArray();
                    Post(() => HandleBinary(frame));
                }
            }
        }

        // --- Inbound ----------------------------------------------------------------------------

        void HandleText(string json)
        {
            string type = HapticWire.TypeOf(json);
            switch (type)
            {
                case "welcome":
                    var welcome = JsonUtility.FromJson<HapticWire.Welcome>(json);
                    ServerHasGemini = welcome.gemini;
                    ServerHasModel = welcome.model;
                    ServerTransport = string.IsNullOrEmpty(welcome.transport) ? "none" : welcome.transport;
                    Debug.Log($"[HapticAI] Server: gemini={welcome.gemini} model={welcome.model} transport={ServerTransport}");
                    break;
                case "profiles":
                    ProfilesReceived?.Invoke(JsonUtility.FromJson<HapticWire.Profiles>(json));
                    break;
                case "select":
                    SelectionReceived?.Invoke(JsonUtility.FromJson<HapticWire.Select>(json));
                    break;
                case "pong":
                    var pong = JsonUtility.FromJson<HapticWire.Pong>(json);
                    LastRoundTripMs = Math.Max(0, NowMs() - pong.t);
                    break;
                case "error":
                    string message = JsonUtility.FromJson<HapticWire.Error>(json)?.message ?? "(no message)";
                    Debug.LogWarning($"[HapticAI] Server error: {message}");
                    ServerError?.Invoke(message);
                    break;
                default:
                    Debug.LogWarning($"[HapticAI] Unknown session message type '{type}'.");
                    break;
            }
        }

        /// <summary>[4-byte big-endian header length][header JSON][WAV bytes]</summary>
        void HandleBinary(byte[] frame)
        {
            if (frame == null || frame.Length < 4) return;
            int headerLength = (frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3];
            if (headerLength < 0 || 4 + headerLength > frame.Length)
            {
                Debug.LogWarning("[HapticAI] Malformed clip frame.");
                return;
            }
            var header = JsonUtility.FromJson<HapticWire.ClipHeader>(Encoding.UTF8.GetString(frame, 4, headerLength));
            if (header == null || header.type != "clip") return;

            var wav = new byte[frame.Length - 4 - headerLength];
            Buffer.BlockCopy(frame, 4 + headerLength, wav, 0, wav.Length);
            ClipReceived?.Invoke(header.set_id, header.profile_id, header.source, wav);
        }

        void Post(Action action) => _mainThread.Enqueue(action);

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
