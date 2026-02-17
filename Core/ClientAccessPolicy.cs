using System.Net;
using System.Net.Sockets;

namespace TDSQueue.Proxy;

public sealed class ClientAccessPolicy
{
    private readonly ProxyOptions _options;
    private readonly IReadOnlyList<CidrRule> _rules;

    public ClientAccessPolicy(ProxyOptions options)
    {
        _options = options;
        _rules = (_options.AllowedClientCidrs ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(CidrRule.Parse)
            .ToArray();
    }

    public bool IsAllowed(IPAddress address)
    {
        if (!_options.EnforceClientAllowlist)
        {
            return true;
        }

        if (_rules.Count == 0)
        {
            return false;
        }

        return _rules.Any(rule => rule.Contains(address));
    }

    private sealed class CidrRule
    {
        private readonly uint _network;
        private readonly uint _mask;

        private CidrRule(uint network, uint mask)
        {
            _network = network;
            _mask = mask;
        }

        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var ip = ToUInt32(address);
            return (ip & _mask) == (_network & _mask);
        }

        public static CidrRule Parse(string text)
        {
            var parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException($"Invalid CIDR '{text}'. Only IPv4 CIDR is supported.");
            }

            var prefix = 32;
            if (parts.Length == 2 && !int.TryParse(parts[1], out prefix))
            {
                throw new InvalidOperationException($"Invalid CIDR prefix in '{text}'.");
            }

            if (prefix < 0 || prefix > 32)
            {
                throw new InvalidOperationException($"CIDR prefix out of range in '{text}'.");
            }

            uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            return new CidrRule(ToUInt32(ip), mask);
        }

        private static uint ToUInt32(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }
    }
}
