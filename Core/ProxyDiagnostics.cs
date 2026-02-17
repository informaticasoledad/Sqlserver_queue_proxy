using System.Diagnostics;

namespace TDSQueue.Proxy;

public static class ProxyDiagnostics
{
    public const string ActivitySourceName = "TDSQueue.Proxy";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
