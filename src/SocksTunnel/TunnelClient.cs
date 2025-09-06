namespace SocksTunnel
{
    public sealed class TunnelClient
    {
        private readonly IPEndPoint _serverEp;
        private readonly Logger _log;
        private readonly Func<Tunnel> _tunnelFactory;
        private readonly TimeSpan _reconn;

        public TunnelClient(IPEndPoint serverEp, Logger log, Func<Tunnel> tunnelFactory, TimeSpan reconnect)
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
