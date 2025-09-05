using System.Net;
using System.Net.Sockets;
using System.Text;
namespace SocksTunnel
{
    internal static class Program
    {
        private static readonly CancellationTokenSource _cts = new();
        private static Tunnel? _tunnel;
        private static Logger? _log;
        private static RuleEngine? _rules;
        private static Socks5Proxy? _socks;

        public static async Task Main(string[] args)
        {
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; _cts.Cancel(); };

            var configPath = args.Length > 0 ? args[0] : "config.json";
            _log = new Logger(LogLevel.INFO); // temp before config read

            AppConfig cfg;
            try
            {
                cfg = ConfigLoader.Load(configPath, _log);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load config: {ex.Message}");
                return;
            }

            _log = new Logger(Logger.Parse(cfg.LogLevel));
            _log.Info($"SocksTunnel starting with config '{configPath}' ...");

            if (string.IsNullOrWhiteSpace(cfg.Mode) ||
                !(cfg.Mode.Equals("server", StringComparison.OrdinalIgnoreCase) ||
                  cfg.Mode.Equals("client", StringComparison.OrdinalIgnoreCase)))
            {
                _log.Error("Config Mode must be 'server' or 'client'.");
                return;
            }

            _rules = new RuleEngine(cfg.Rules!, _log);
            var role = cfg.Mode.ToLowerInvariant();
            var token = _cts.Token;

            async Task OnOpenRemote(int sid, byte atyp, byte[] addrBytes, ushort port)
            {
                try
                {
                    var target = new TcpClient { NoDelay = true };
                    if (atyp == 3)
                    {
                        var host = Encoding.ASCII.GetString(addrBytes);
                        _log!.Info($"[RemoteOpen] connecting {host}:{port}");
                        await target.ConnectAsync(host, port, token);
                    }
                    else
                    {
                        var ip = new IPAddress(addrBytes);
                        _log!.Info($"[RemoteOpen] connecting {ip}:{port}");
                        await target.ConnectAsync(ip, port, token);
                    }

                    await _tunnel!.SendOpenResultAsync(sid, true, token);
                    var ts = target.GetStream();

                    _tunnel!.SetSink(sid, async (data, ct) =>
                    {
                        try { await ts.WriteAsync(data, ct); } catch { }
                    });

                    var buf = new byte[64 * 1024];
                    try
                    {
                        while (true)
                        {
                            int n = await ts.ReadAsync(buf, token);
                            if (n <= 0) break;
                            await _tunnel!.SendDataAsync(sid, new ReadOnlyMemory<byte>(buf, 0, n), token);
                        }
                    }
                    catch { }
                    finally
                    {
                        try { await _tunnel!.SendCloseAsync(sid, token); } catch { }
                        try { target.Close(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _log!.Warn($"[RemoteOpen] connect failed: {ex.Message}");
                    try { await _tunnel!.SendOpenResultAsync(sid, false, token); } catch { }
                }
            }

            Func<Tunnel> factory = () =>
            {
                var t = new Tunnel(_log!, role, (sid, atyp, addr, port) => OnOpenRemote(sid, atyp, addr, port));
                _tunnel = t;
                return t;
            };

            if (!string.IsNullOrWhiteSpace(cfg.Socks))
            {
                var socksEp = NetParse.ParseEndpoint(cfg.Socks);
                _socks = new Socks5Proxy(
                    socksEp,
                    _rules!,
                    _log!,
                    _ => _tunnel
                );
            }

            if (role == "server")
            {
                if (string.IsNullOrWhiteSpace(cfg.TunnelListen))
                {
                    _log!.Error("Missing TunnelListen in config for server mode.");
                    return;
                }
                var listenEp = NetParse.ParseEndpoint(cfg.TunnelListen);
                var server = new TunnelServer(listenEp, _log!, factory);
                server.Start();
                _socks?.Start();

                _log!.Info("Server running. Press Ctrl+C to exit.");
                try { await Task.Delay(Timeout.Infinite, token); } catch { }

                await server.StopAsync();
                if (_socks is not null) await _socks.StopAsync();
                if (_tunnel is not null) await _tunnel.DisposeAsync();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(cfg.Server))
                {
                    _log!.Error("Missing Server in config for client mode.");
                    return;
                }
                var serverEp = NetParse.ParseEndpoint(cfg.Server);
                var connector = new TunnelClientConnector(serverEp, _log!, factory, TimeSpan.FromSeconds(3));
                var runTask = connector.RunAsync(token);
                _socks?.Start();

                _log!.Info("Client running. Press Ctrl+C to exit.");
                try { await runTask; } catch (OperationCanceledException) { }

                if (_socks is not null) await _socks.StopAsync();
                if (_tunnel is not null) await _tunnel.DisposeAsync();
            }

            _log!.Info("SocksTunnel stopped.");
        }
    }
}
