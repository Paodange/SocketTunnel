using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocksTunnel
{
    public sealed class AppConfig
    {
        public string? Mode { get; set; }          // "server" | "client"
        public string? TunnelListen { get; set; }  // ip:port (server)
        public string? Server { get; set; }        // host:port (client)
        public string? Socks { get; set; }         // ip:port
        public string? LogLevel { get; set; }      // TRACE|DEBUG|INFO|WARN|ERROR
        public List<Rule>? Rules { get; set; }     // inline rules
    }


    public sealed class Rule
    {
        public string? Action { get; set; }
        public string? Host { get; set; }      // supports * wildcard
        public string? IP { get; set; }        // single IP or CIDR
        public int? Port { get; set; }
        public string? PortRange { get; set; } // "1000-2000"
        public bool? Default { get; set; }     // true for fallback
    }

    [JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(AppConfig))]
    [JsonSerializable(typeof(List<Rule>))]
    internal partial class ConfigJsonContext : JsonSerializerContext
    {
    }

    public static class ConfigLoader
    {
        public static AppConfig Load(string path, Logger log)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Config path is empty.");
            if (!File.Exists(path)) throw new FileNotFoundException($"Config not found: {path}");
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)
                      ?? new AppConfig();

            // 默认规则：全直连
            if (cfg.Rules == null || cfg.Rules.Count == 0)
            {
                log.Info("No rules specified in config. Using default: Direct all.");
                cfg.Rules = new List<Rule> { new Rule { Action = "Direct", Default = true } };
            }
            return cfg;
        }
    }
}

