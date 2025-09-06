using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SocksTunnel
{
    using System.Buffers.Binary;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;

    public sealed class Socks5Proxy
    {
        private readonly IPEndPoint _listen;
        private readonly Logger _log;
        private readonly RuleEngine _ruleEngine;
        private readonly Func<CancellationToken, Tunnel?> _getTunnel;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        public Socks5Proxy(IPEndPoint listen, RuleEngine ruleEngine, Logger log, Func<CancellationToken, Tunnel?> getTunnel)
        {
            _listen = listen;
            _ruleEngine = ruleEngine;
            _log = log;
            _getTunnel = getTunnel;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(_listen);
            _listener.Start();
            _ = AcceptLoop(_cts.Token);
            _log.Info($"Socks5 listening at {_listen}");
        }

        public Task StopAsync()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            return Task.CompletedTask;
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            var listener = _listener!;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    _ = HandleClient(client, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.Warn($"Socks accept error: {ex.Message}");
                    await Task.Delay(200, token);
                }
            }
        }

        private async Task HandleClient(TcpClient cli, CancellationToken token)
        {
            using var _ = cli;
            var ns = cli.GetStream();

            // Greeting
            int ver = ns.ReadByte();
            if (ver != 5) return;
            int nMethods = ns.ReadByte();
            if (nMethods < 0) return;
            byte[] methods = new byte[nMethods];
            await ns.ReadExactlyAsync(methods, token);
            // No-auth
            await ns.WriteAsync(new byte[] { 0x05, 0x00 }, token);

            // Request: VER(0)=5, CMD(1)=1, RSV(2)=0, ATYP(3)
            byte[] hdr = new byte[4];
            await ns.ReadExactlyAsync(hdr, token);
            if (hdr[0] != 5 || hdr[1] != 1) // only CONNECT
            {
                await ReplyFail(ns, 0x07, token); // Command not supported
                return;
            }
            if (hdr[2] != 0x00)
            {
                await ReplyFail(ns, 0x01, token); // general failure
                return;
            }

            // FIX: ATYP must be taken from hdr[3], do NOT read another byte
            byte atyp = hdr[3];

            string? host = null;
            IPAddress? ip = null;
            byte[] addrBytes;

            switch (atyp)
            {
                case 1: // IPv4
                    addrBytes = new byte[4];
                    await ns.ReadExactlyAsync(addrBytes, token);
                    ip = new IPAddress(addrBytes);
                    host = ip.ToString();
                    break;

                case 3: // Domain
                    int dlen = ns.ReadByte();
                    if (dlen < 0)
                    {
                        await ReplyFail(ns, 0x01, token);
                        return;
                    }
                    addrBytes = new byte[dlen];
                    await ns.ReadExactlyAsync(addrBytes, token);
                    host = Encoding.ASCII.GetString(addrBytes);
                    break;

                case 4: // IPv6
                    addrBytes = new byte[16];
                    await ns.ReadExactlyAsync(addrBytes, token);
                    ip = new IPAddress(addrBytes);
                    host = ip.ToString();
                    break;

                default:
                    await ReplyFail(ns, 0x08, token); // addr type not supported
                    return;
            }

            byte[] portBytes = new byte[2];
            await ns.ReadExactlyAsync(portBytes, token);
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);

            bool shouldForward = _ruleEngine.ShouldForward(host, ip, port);
            if (shouldForward)
            {
                var tunnel = _getTunnel(token);
                if (tunnel is null || !tunnel.IsConnected)
                {
                    _log.Warn("No tunnel connected, cannot forward, connect direct.");
                    //await ReplyFail(ns, 0x05, token);
                    _log.Info($"Socks CONNECT {host}:{port} => Direct");
                    await HandleDirect(cli, ns, atyp, addrBytes, port, token);
                    return;
                }
                _log.Info($"Socks CONNECT {host}:{port} => Forward");
                await HandleForward(tunnel, cli, ns, atyp, addrBytes, port, token);
            }
            else
            {
                _log.Info($"Socks CONNECT {host}:{port} => Direct");
                await HandleDirect(cli, ns, atyp, addrBytes, port, token);
            }
        }

        private async Task HandleDirect(TcpClient client, NetworkStream ns, byte atyp, byte[] addrBytes, ushort port, CancellationToken token)
        {
            try
            {
                IPAddress? ip = null; string? host = null;
                switch (atyp)
                {
                    case 1: ip = new IPAddress(addrBytes); break;
                    case 3: host = Encoding.ASCII.GetString(addrBytes); break;
                    case 4: ip = new IPAddress(addrBytes); break;
                }

                var target = new TcpClient { NoDelay = true };
                if (host is not null) await target.ConnectAsync(host, port, token);
                else await target.ConnectAsync(ip!, port, token);

                await ReplySuccess(ns, token);

                var ts = target.GetStream();
                _ = PumpCopy(ns, ts, () => { try { target.Close(); } catch { } }, token);
                await PumpCopy(ts, ns, () => { try { target.Close(); } catch { } }, token);
            }
            catch (Exception ex)
            {
                _log.Warn($"Direct connect failed: {ex.Message}");
                await ReplyFail(ns, 0x01, token);
            }
        }

        private async Task HandleForward(Tunnel tunnel, TcpClient client, NetworkStream ns, byte atyp, byte[] addrBytes, ushort port, CancellationToken token)
        {
            int sid = -1;
            try
            {
                sid = await tunnel.OpenStreamAsync(atyp, addrBytes, port,
                    async (data, ct) =>
                    {
                        try { await ns.WriteAsync(data, ct); } catch { }
                    }, token);

                await ReplySuccess(ns, token);

                var buf = new byte[64 * 1024];
                while (true)
                {
                    int n = await ns.ReadAsync(buf, token);
                    if (n <= 0) break;
                    await tunnel.SendDataAsync(sid, new ReadOnlyMemory<byte>(buf, 0, n), token);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Forward via tunnel failed: {ex.Message}");
            }
            finally
            {
                if (sid > 0) { try { await tunnel.SendCloseAsync(sid, token); } catch { } }
                try { client.Close(); } catch { }
            }
        }

        private static async Task PumpCopy(NetworkStream from, NetworkStream to, Action onClose, CancellationToken token)
        {
            var buf = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    int n = await from.ReadAsync(buf, token);
                    if (n <= 0) break;
                    await to.WriteAsync(new ReadOnlyMemory<byte>(buf, 0, n), token);
                }
            }
            catch { }
            finally
            {
                onClose();
            }
        }

        private static Task ReplySuccess(NetworkStream ns, CancellationToken token)
        {
            byte[] resp = new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            return ns.WriteAsync(resp, token).AsTask();
        }

        private static Task ReplyFail(NetworkStream ns, byte rep, CancellationToken token)
        {
            byte[] resp = new byte[] { 0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            return ns.WriteAsync(resp, token).AsTask();
        }
    }

    internal static class StreamExt
    {
        public static async Task ReadExactlyAsync(this NetworkStream ns, byte[] buf, CancellationToken token)
        {
            int off = 0, need = buf.Length;
            while (need > 0)
            {
                int n = await ns.ReadAsync(buf.AsMemory(off, need), token);
                if (n == 0) throw new IOException("Remote closed.");
                off += n; need -= n;
            }
        }
    }
}
