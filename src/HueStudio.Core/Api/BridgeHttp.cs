using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace HueStudio.Core.Api;

/// <summary>
/// Builds HttpClients for talking to a bridge on the LAN.
///
/// Hue bridges use a certificate signed by the Philips Hue private root CA with the bridge id as
/// the common name, so normal Windows chain validation fails. We use trust-on-first-use pinning:
/// the certificate seen at pairing time is pinned (SHA-256 thumbprint) and every later connection
/// must present the same certificate. A mismatch is refused.
/// </summary>
public static class BridgeHttp
{
    public static HttpClient Create(
        Uri baseAddress,
        string? pinnedSha256,
        Action<string>? onCertificateSeen = null,
        TimeSpan? timeout = null,
        HttpMessageHandler? innerHandler = null)
    {
        var handler = innerHandler ?? CreateHandler(pinnedSha256, onCertificateSeen);
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
    }

    public static SocketsHttpHandler CreateHandler(string? pinnedSha256, Action<string>? onCertificateSeen) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // The Hue bridge allows a small number of concurrent connections; keep it modest.
        MaxConnectionsPerServer = 4,
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                ValidateCertificate(cert, errors, pinnedSha256, onCertificateSeen),
        },
    };

    internal static bool ValidateCertificate(
        X509Certificate? cert,
        SslPolicyErrors errors,
        string? pinnedSha256,
        Action<string>? onCertificateSeen)
    {
        if (cert is null) return false;
        var thumb = Thumbprint(cert);
        onCertificateSeen?.Invoke(thumb);

        // First contact (pairing): accept and let the caller pin it.
        if (string.IsNullOrEmpty(pinnedSha256)) return true;

        return string.Equals(thumb, pinnedSha256, StringComparison.OrdinalIgnoreCase);
    }

    public static string Thumbprint(X509Certificate cert)
    {
        return Convert.ToHexString(cert.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
    }
}
