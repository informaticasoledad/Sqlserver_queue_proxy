using System.Net;

namespace TDSQueue.Proxy;

public static class TargetEndpointResolver
{
    public static IPEndPoint Resolve(ProxyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TargetHost))
        {
            throw new InvalidOperationException(
                "Configure Proxy:TargetHost and Proxy:TargetPort.");
        }

        var addresses = Dns.GetHostAddresses(options.TargetHost);
        if (addresses.Length == 0)
        {
            throw new InvalidOperationException($"Could not resolve host '{options.TargetHost}'.");
        }

        return new IPEndPoint(addresses[0], options.TargetPort);
    }
}
