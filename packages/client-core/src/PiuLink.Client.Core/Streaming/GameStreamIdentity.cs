using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PiuLink.Client.Core.Streaming;

public sealed class GameStreamIdentity : IDisposable
{
    private const string CertificateFile = "client-certificate.pem";
    private const string PrivateKeyFile = "client-private-key.pem";
    private const string UniqueIdFile = "unique-id.txt";

    private GameStreamIdentity(string directory, X509Certificate2 certificate, string uniqueId)
    {
        Directory = directory;
        Certificate = certificate;
        UniqueId = uniqueId;
    }

    public string Directory { get; }
    public X509Certificate2 Certificate { get; }
    public string UniqueId { get; }
    public byte[] PemCertificate => System.Text.Encoding.ASCII.GetBytes(Certificate.ExportCertificatePem());
    public void Dispose() => Certificate.Dispose();

    public static GameStreamIdentity LoadOrCreate(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiuLink", "GameStream");
        System.IO.Directory.CreateDirectory(directory);
        var certPath = Path.Combine(directory, CertificateFile);
        var keyPath = Path.Combine(directory, PrivateKeyFile);
        var idPath = Path.Combine(directory, UniqueIdFile);
        X509Certificate2 certificate;
        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            using var loaded = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            certificate = PersistForSchannel(loaded);
        }
        else
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=NVIDIA GameStream Client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(20));
            certificate = PersistForSchannel(created);
            File.WriteAllText(certPath, certificate.ExportCertificatePem());
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }
        var uniqueId = File.Exists(idPath) ? File.ReadAllText(idPath).Trim() : Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        if (!File.Exists(idPath)) File.WriteAllText(idPath, uniqueId);
        return new GameStreamIdentity(directory, certificate, uniqueId);
    }

    private static X509Certificate2 PersistForSchannel(X509Certificate2 certificate)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
#pragma warning disable SYSLIB0057
        return new X509Certificate2(certificate.Export(X509ContentType.Pkcs12, password), password,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    }
}
