namespace SocksTunnel
{
    public sealed class TunnelServer
    {
        private readonly IPEndPoint _listenEp;
        private readonly Logger _log;
        private readonly Func<Tunnel> _tunnelFactory;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;

        public TunnelServer(IPEndPoint listenEp, Logger log, Func<Tunnel> tunnelFactory)
        {
            _listenEp = listenEp;
            _log = log;
            _tunnelFactory = tunnelFactory;
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
}
