using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PiuLink.Host.Services;

internal static class FileTransferTls
{
    internal static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    internal static SslStream Create(Stream network, string? expectedFingerprint, Func<X509Certificate?, bool> isPaired) =>
        new(network, false, (_, peer, _, _) => peer is not null &&
            (expectedFingerprint is null || Fingerprint(peer) == expectedFingerprint) && isPaired(peer));

    internal static Task AcceptAsync(SslStream stream, X509Certificate2 identity, CancellationToken token) =>
        stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = identity, ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, token);

    internal static Task ConnectAsync(SslStream stream, X509Certificate2 identity, string address, CancellationToken token) =>
        stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = address, ClientCertificates = new X509CertificateCollection { identity },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => identity,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, token);
}
