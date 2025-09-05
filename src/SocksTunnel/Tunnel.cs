using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SocksTunnel
{
    public enum FrameType : byte
    {
        Open = 1,
        Data = 2,
        Close = 3,
        OpenResult = 4,
        Ping = 5,
        Pong = 6,
        Hello = 7
    }

    public sealed class Tunnel : IAsyncDisposable
    {
        private readonly Logger _log;
        private TcpClient? _client;
        private NetworkStream? _ns;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<int, StreamState> _streams = new();
        private int _nextStreamId = 1;
        private Task? _readerTask;
        private readonly string _name;
        private readonly Func<int, byte, byte[], ushort, Task> _onOpenRemote;

        public bool IsConnected => _client?.Connected == true;

        public Tunnel(Logger log, string name, Func<int, byte, byte[], ushort, Task> onOpenRemote)
        {
            _log = log;
            _name = name;
            _onOpenRemote = onOpenRemote;
        }

        public async Task AttachAsync(TcpClient client, string role, CancellationToken token)
        {
            _client = client;
            _ns = client.GetStream();
            _ns.ReadTimeout = Timeout.Infinite;
            _ns.WriteTimeout = Timeout.Infinite;
            _readerTask = Task.Run(() => ReaderLoop(_cts.Token));
            await SendHelloAsync(role, token);
        }

        private async Task ReaderLoop(CancellationToken token)
        {
            var ns = _ns!;
            var head = new byte[9];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await ReadExactAsync(ns, head, token);
                    var type = (FrameType)head[0];
                    int sid = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1, 4));
                    int len = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(5, 4));

                    byte[] payload = len > 0 ? ArrayPool<byte>.Shared.Rent(len) : Array.Empty<byte>();
                    try
                    {
                        if (len > 0) await ReadExactAsync(ns, payload.AsMemory(0, len), token);
                        await DispatchAsync(type, sid, payload, len, token);
                    }
                    finally
                    {
                        if (len > 0) ArrayPool<byte>.Shared.Return(payload);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ex)
            {
                _log.Warn($"Tunnel reader IO closed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _log.Error($"Tunnel reader error: {ex}");
            }
            finally
            {
                await CloseAllAsync();
            }
        }

        private async Task DispatchAsync(FrameType type, int sid, byte[] payload, int len, CancellationToken token)
        {
            switch (type)
            {
                case FrameType.Hello:
                    _log.Info($"{_name} received HELLO: {System.Text.Encoding.ASCII.GetString(payload, 0, len)}");
                    break;

                case FrameType.Ping:
                    await SendFrameAsync(FrameType.Pong, 0, ReadOnlyMemory<byte>.Empty, token);
                    break;

                case FrameType.Pong:
                    _log.Trace($"{_name} PONG");
                    break;

                case FrameType.Open:
                    {
                        if (len < 1 + 2) { _log.Error("Invalid OPEN payload"); return; }
                        byte atyp = payload[0];
                        int idx = 1;
                        byte[] addrBytes;
                        switch (atyp)
                        {
                            case 1:
                                if (len < idx + 4 + 2) { _log.Error("Invalid OPEN payload"); return; }
                                addrBytes = new byte[4];
                                Buffer.BlockCopy(payload, idx, addrBytes, 0, 4);
                                idx += 4; break;
                            case 3:
                                int dlen = payload[idx++];
                                if (len < idx + dlen + 2) { _log.Error("Invalid OPEN payload"); return; }
                                addrBytes = new byte[dlen];
                                Buffer.BlockCopy(payload, idx, addrBytes, 0, dlen);
                                idx += dlen; break;
                            case 4:
                                if (len < idx + 16 + 2) { _log.Error("Invalid OPEN payload"); return; }
                                addrBytes = new byte[16];
                                Buffer.BlockCopy(payload, idx, addrBytes, 0, 16);
                                idx += 16; break;
                            default:
                                _log.Error($"Unknown ATYP {atyp} in OPEN");
                                await SendOpenResultAsync(sid, success: false, token);
                                return;
                        }
                        ushort port = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(idx, 2));
                        _ = _onOpenRemote(sid, atyp, addrBytes, port);
                    }
                    break;

                case FrameType.OpenResult:
                    {
                        if (_streams.TryGetValue(sid, out var st))
                        {
                            bool success = len > 0 && payload[0] == 1;
                            st.OpenTcs?.TrySetResult(success);
                            if (!success) _streams.TryRemove(sid, out _);
                        }
                    }
                    break;

                case FrameType.Data:
                    {
                        if (_streams.TryGetValue(sid, out var st))
                        {
                            var target = st.IncomingSink;
                            if (target is not null)
                            {
                                try { await target(payload.AsMemory(0, len), token); }
                                catch (Exception ex)
                                {
                                    _log.Warn($"Stream {sid} sink write failed: {ex.Message}");
                                    await CloseStreamAsync(sid, token);
                                }
                            }
                        }
                    }
                    break;

                case FrameType.Close:
                    await CloseStreamAsync(sid, token);
                    break;
            }
        }

        public async Task<int> OpenStreamAsync(byte atyp, byte[] addr, ushort port, Func<ReadOnlyMemory<byte>, CancellationToken, Task> incomingSink, CancellationToken token)
        {
            int sid = Interlocked.Increment(ref _nextStreamId);
            var st = new StreamState(sid)
            {
                IncomingSink = incomingSink,
                OpenTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            if (!_streams.TryAdd(sid, st))
                throw new InvalidOperationException("Stream id collision.");

            var payload = BuildOpenPayload(atyp, addr, port);
            await SendFrameAsync(FrameType.Open, sid, payload, token);

            bool ok = await st.OpenTcs!.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            if (!ok)
            {
                _streams.TryRemove(sid, out _);
                throw new IOException("Remote open failed.");
            }
            return sid;
        }

        public Task SendDataAsync(int sid, ReadOnlyMemory<byte> data, CancellationToken token) =>
            SendFrameAsync(FrameType.Data, sid, data, token);

        public async Task SendCloseAsync(int sid, CancellationToken token)
        {
            await SendFrameAsync(FrameType.Close, sid, ReadOnlyMemory<byte>.Empty, token);
            await CloseStreamAsync(sid, token);
        }

        public Task SendOpenResultAsync(int sid, bool success, CancellationToken token)
        {
            var payload = success ? new byte[] { 1 } : new byte[] { 0 };
            return SendFrameAsync(FrameType.OpenResult, sid, payload, token);
        }

        public Task SendHelloAsync(string role, CancellationToken token)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes($"HELLO {role} v1");
            return SendFrameAsync(FrameType.Hello, 0, bytes, token);
        }

        public Task SendPingAsync(CancellationToken token) =>
            SendFrameAsync(FrameType.Ping, 0, ReadOnlyMemory<byte>.Empty, token);

        private async Task SendFrameAsync(FrameType type, int sid, ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            var ns = _ns ?? throw new IOException("Tunnel not attached");
            byte[] head = new byte[9];
            head[0] = (byte)type;
            BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(1, 4), sid);
            BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(5, 4), payload.Length);

            await _sendLock.WaitAsync(token);
            try
            {
                await ns.WriteAsync(head, token);
                if (!payload.IsEmpty)
                    await ns.WriteAsync(payload, token);
                await ns.FlushAsync(token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static byte[] BuildOpenPayload(byte atyp, byte[] addr, ushort port)
        {
            int len = 1 + addr.Length + 2 + (atyp == 3 ? 1 : 0);
            var payload = new byte[len];
            int idx = 0;
            payload[idx++] = atyp;
            if (atyp == 3) payload[idx++] = (byte)addr.Length;
            Buffer.BlockCopy(addr, 0, payload, idx, addr.Length);
            idx += addr.Length;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(idx, 2), port);
            return payload;
        }

        private static async Task ReadExactAsync(NetworkStream ns, Memory<byte> buf, CancellationToken token)
        {
            int need = buf.Length, off = 0;
            while (need > 0)
            {
                int n = await ns.ReadAsync(buf.Slice(off, need), token);
                if (n == 0) throw new IOException("Remote closed");
                off += n; need -= n;
            }
        }

        public async Task CloseAllAsync()
        {
            foreach (var kv in _streams.Keys)
            {
                try { await CloseStreamAsync(kv, _cts.Token); } catch { }
            }
            _streams.Clear();
            try { _ns?.Close(); } catch { }
            try { _client?.Close(); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_readerTask is not null) { try { await _readerTask; } catch { } }
            await CloseAllAsync();
            _cts.Dispose();
            _sendLock.Dispose();
        }

        public bool TryGetStream(int sid, out StreamState st) => _streams.TryGetValue(sid, out st!);

        public void SetSink(int sid, Func<ReadOnlyMemory<byte>, CancellationToken, Task> sink)
        {
            if (_streams.TryGetValue(sid, out var st)) st.IncomingSink = sink;
        }

        public Task CloseStreamAsync(int sid, CancellationToken token)
        {
            if (_streams.TryRemove(sid, out var st))
            {
                try { st.OnClosed?.Invoke(); } catch { }
                st.OpenTcs?.TrySetResult(false);
            }
            return Task.CompletedTask;
        }

        public sealed class StreamState
        {
            public int Id { get; }
            public TaskCompletionSource<bool>? OpenTcs { get; set; }
            public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? IncomingSink { get; set; }
            public Action? OnClosed { get; set; }
            public StreamState(int id) { Id = id; }
        }
    }

    public sealed class TunnelServer
    {
        private readonly IPEndPoint _listenEp;
        private readonly Logger _log;
        private readonly Func<Tunnel> _tunnelFactory;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;

        public TunnelServer(IPEndPoint listenEp, Logger log, Func<Tunnel> tunnelFactory)
        {
            _listenEp = listenEp; _log = log; _tunnelFactory = tunnelFactory;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            var listener = new TcpListener(_listenEp);
            listener.Start();
            _log.Info($"Tunnel server listening at {_listenEp}");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    _log.Info($"Tunnel incoming connection from {client.Client.RemoteEndPoint}");
                    var tunnel = _tunnelFactory();
                    await tunnel.AttachAsync(client, "server", token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.Warn($"Accept error: {ex.Message}");
                    await Task.Delay(1000, token);
                }
            }
            listener.Stop();
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_acceptTask is not null) try { await _acceptTask; } catch { }
        }
    }

    public sealed class TunnelClientConnector
    {
        private readonly IPEndPoint _serverEp;
        private readonly Logger _log;
        private readonly Func<Tunnel> _tunnelFactory;
        private readonly TimeSpan _reconn;

        public TunnelClientConnector(IPEndPoint serverEp, Logger log, Func<Tunnel> tunnelFactory, TimeSpan reconnect)
        {
            _serverEp = serverEp; _log = log; _tunnelFactory = tunnelFactory; _reconn = reconnect;
        }

        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = new TcpClient { NoDelay = true };
                    _log.Info($"Connecting to tunnel server {_serverEp} ...");
                    await client.ConnectAsync(_serverEp, token);
                    _log.Info("Tunnel connected.");

                    var tunnel = _tunnelFactory();
                    await tunnel.AttachAsync(client, "client", token);

                    while (!token.IsCancellationRequested && tunnel.IsConnected)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), token);
                        await tunnel.SendPingAsync(token);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.Warn($"Tunnel connect/retry: {ex.Message}");
                    await Task.Delay(_reconn, token);
                }
            }
        }
    }
}
