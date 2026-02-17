using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace TDSQueue.Proxy;

public sealed class TlsTerminationService
{
    private readonly ProxyOptions _options;
    private readonly ILogger<TlsTerminationService> _logger;
    private readonly Lazy<X509Certificate2?> _certificate;

    public TlsTerminationService(ProxyOptions options, ILogger<TlsTerminationService> logger)
    {
        _options = options;
        _logger = logger;
        _certificate = new Lazy<X509Certificate2?>(LoadCertificate);
    }

    public bool Enabled => _options.Tls.Enabled;

    public async ValueTask<SslStream> UpgradeClientSideAsync(Stream plainStream, CancellationToken cancellationToken)
    {
        var certificate = _certificate.Value
            ?? throw new InvalidOperationException("TLS termination enabled but certificate could not be loaded.");

        var sslStream = new SslStream(plainStream, leaveInnerStreamOpen: true);
        var authOptions = new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = _options.Tls.ClientCertificateRequired,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = _options.Tls.CheckCertificateRevocation
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.Tls.HandshakeTimeoutSeconds));

        _logger.LogDebug("Starting TLS handshake with client.");
        try
        {
            await sslStream.AuthenticateAsServerAsync(authOptions, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthenticationException(
                $"Client TLS handshake timed out after {_options.Tls.HandshakeTimeoutSeconds}s.");
        }

        _logger.LogDebug("TLS handshake with client completed.");
        return sslStream;
    }

    public async ValueTask<SslStream> UpgradeTargetSideAsync(Stream plainStream, string targetHost, CancellationToken cancellationToken)
    {
        var sslStream = new SslStream(
            plainStream,
            leaveInnerStreamOpen: true,
            (sender, certificate, chain, errors) =>
            {
                if (_options.Tls.TrustTargetServerCertificate)
                {
                    return true;
                }

                return errors == SslPolicyErrors.None;
            });

        var authOptions = new SslClientAuthenticationOptions
        {
            TargetHost = string.IsNullOrWhiteSpace(_options.Tls.TargetServerName)
                ? targetHost
                : _options.Tls.TargetServerName,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = _options.Tls.CheckCertificateRevocation
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.Tls.HandshakeTimeoutSeconds));

        _logger.LogDebug("Starting TLS handshake with target {TargetHost}.", authOptions.TargetHost);
        try
        {
            await sslStream.AuthenticateAsClientAsync(authOptions, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthenticationException(
                $"Target TLS handshake timed out after {_options.Tls.HandshakeTimeoutSeconds}s.");
        }

        _logger.LogDebug("TLS handshake with target completed.");
        return sslStream;
    }

    private X509Certificate2? LoadCertificate()
    {
        if (!Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.Tls.CertificatePath))
        {
            throw new InvalidOperationException("Proxy:Tls:CertificatePath is required when TLS termination is enabled.");
        }

        var certPath = SecretResolver.Resolve(_options.Tls.CertificatePath);
        var certPassword = _options.Tls.CertificatePassword is null
            ? null
            : SecretResolver.Resolve(_options.Tls.CertificatePassword);

        if (!File.Exists(certPath))
        {
            throw new FileNotFoundException($"TLS certificate not found at '{certPath}'.");
        }

        return X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);
    }
}
