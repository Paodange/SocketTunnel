using System.Globalization;

namespace SocksTunnel
{
    public enum LogLevel { TRACE = 0, DEBUG = 1, INFO = 2, WARN = 3, ERROR = 4 }

    public sealed class Logger
    {
        private readonly LogLevel _level;
        private readonly object _lock = new();

        public Logger(LogLevel level) => _level = level;

        public void Trace(string msg) => Write(LogLevel.TRACE, msg);
        public void Debug(string msg) => Write(LogLevel.DEBUG, msg);
        public void Info(string msg) => Write(LogLevel.INFO, msg);
        public void Warn(string msg) => Write(LogLevel.WARN, msg);
        public void Error(string msg) => Write(LogLevel.ERROR, msg);

        private void Write(LogLevel level, string msg)
        {
            if (level < _level) return;
            lock (_lock)
            {
                var color = level switch
                {
                    LogLevel.INFO => ConsoleColor.Green,
                    LogLevel.WARN => ConsoleColor.Yellow,
                    LogLevel.ERROR => ConsoleColor.Red,
                    _ => ConsoleColor.White
                };

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write($"[{timestamp}] ");
                Console.ForegroundColor = color;
                Console.Write($"[{level}] ");
                Console.ResetColor();
                Console.WriteLine(msg);
            }
        }

        public static LogLevel Parse(string? s) =>
            Enum.TryParse<LogLevel>(s, true, out var lv) ? lv : LogLevel.INFO;
    }

    public static class NetParse
    {
        public static IPEndPoint ParseEndpoint(string s)
        {
            if (s.StartsWith('['))
            {
                int idx = s.IndexOf(']');
                if (idx < 0) throw new FormatException("Invalid IPv6 endpoint.");
                string host = s.Substring(1, idx - 1);
                string portStr = s[(idx + 1)..].TrimStart(':');
                return new IPEndPoint(IPAddress.Parse(host), int.Parse(portStr));
            }
            else
            {
                var lastColon = s.LastIndexOf(':');
                if (lastColon < 0) throw new FormatException("Endpoint must be host:port");
                string host = s[..lastColon];
                string portStr = s[(lastColon + 1)..];
                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip))
                {
                    var ips = Dns.GetHostAddresses(host);
                    ip = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                         ?? ips.First();
                }
                return new IPEndPoint(ip, int.Parse(portStr));
            }
        }
    }
}
