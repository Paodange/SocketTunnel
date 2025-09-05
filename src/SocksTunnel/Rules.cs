namespace SocksTunnel
{
    public sealed class RuleEngine
    {
        private readonly List<Rule> _rules;
        private readonly Logger _log;

        public RuleEngine(IEnumerable<Rule> rules, Logger log)
        {
            _rules = rules.ToList();
            _log = log;
        }

        public bool ShouldForward(string? host, IPAddress? ip, int port)
        {
            string action = "Direct";
            foreach (var rule in _rules)
            {
                if (Matches(rule, host, ip, port))
                {
                    _log.Debug($"Rule matched: {Describe(rule)} => {rule.Action}");
                    action = rule.Action!;
                }
            }
            return string.Compare(action, "forward", true) == 0;
        }

        private static bool Matches(Rule r, string? host, IPAddress? ip, int port)
        {
            if (r.Default == true) return true;

            if (!string.IsNullOrWhiteSpace(r.Host))
            {
                if (string.IsNullOrWhiteSpace(host)) return false;
                if (!GlobMatch(host, r.Host!)) return false;
            }
            if (!string.IsNullOrWhiteSpace(r.IP))
            {
                if (ip is null) return false;
                if (!IpMatches(ip, r.IP!)) return false;
            }
            if (r.Port.HasValue && port != r.Port.Value) return false;

            if (!string.IsNullOrWhiteSpace(r.PortRange))
            {
                var parts = r.PortRange!.Split('-', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out int p1) || !int.TryParse(parts[1], out int p2)) return false;
                if (port < Math.Min(p1, p2) || port > Math.Max(p1, p2)) return false;
            }
            return true;
        }

        private static bool GlobMatch(string input, string pattern)
        {
            input = input.ToLowerInvariant();
            pattern = pattern.ToLowerInvariant();
            var parts = pattern.Split('*');
            if (parts.Length == 1) return input == pattern;
            int pos = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Length == 0)
                {
                    if (i == 0) continue;
                    if (i == parts.Length - 1) return true;
                    continue;
                }
                int idx = input.IndexOf(part, pos, StringComparison.Ordinal);
                if (idx < 0) return false;
                if (i == 0 && !pattern.StartsWith("*") && idx != 0) return false;
                pos = idx + part.Length;
                if (i == parts.Length - 1 && !pattern.EndsWith("*") && pos != input.Length) return false;
            }
            return true;
        }

        private static bool IpMatches(IPAddress ip, string cidrOrIp)
        {
            if (!cidrOrIp.Contains('/'))
            {
                return IPAddress.TryParse(cidrOrIp, out var single) && ip.Equals(single);
            }
            var parts = cidrOrIp.Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var network)) return false;
            if (!int.TryParse(parts[1], out int prefixLen)) return false;

            var ipBytes = ip.GetAddressBytes();
            var netBytes = network.GetAddressBytes();
            if (ipBytes.Length != netBytes.Length) return false;

            int fullBytes = prefixLen / 8;
            int remBits = prefixLen % 8;

            for (int i = 0; i < fullBytes; i++)
                if (ipBytes[i] != netBytes[i]) return false;

            if (remBits == 0) return true;

            byte mask = (byte)(~(0xFF >> remBits));
            return (ipBytes[fullBytes] & mask) == (netBytes[fullBytes] & mask);
        }

        private static string Describe(Rule r)
        {
            var parts = new List<string>();
            if (r.Host is not null) parts.Add($"Host={r.Host}");
            if (r.IP is not null) parts.Add($"IP={r.IP}");
            if (r.Port is not null) parts.Add($"Port={r.Port}");
            if (r.PortRange is not null) parts.Add($"PortRange={r.PortRange}");
            if (r.Default == true) parts.Add("Default=true");
            return string.Join(", ", parts);
        }
    }
}