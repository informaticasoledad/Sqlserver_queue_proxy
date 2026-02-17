using System.ComponentModel.DataAnnotations;

namespace TDSQueue.Proxy;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    [Required]
    public string ListenAddress { get; init; } = "127.0.0.1";

    [Range(1, 65535)]
    public int HealthPort { get; init; } = 18080;

    public bool EnableOtlpExporter { get; init; }

    public string? OtlpEndpoint { get; init; }

    public bool EnableAdminEndpoint { get; init; }

    public bool AllowRemoteAdmin { get; init; }

    public string? AdminToken { get; init; }
}
