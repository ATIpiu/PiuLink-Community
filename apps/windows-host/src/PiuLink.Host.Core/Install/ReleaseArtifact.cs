namespace PiuLink.Host.Core.Install;

public sealed record ReleaseArtifact(
    string Id,
    string Version,
    Uri DownloadUri,
    string Sha256,
    string ExpectedSignerSubject)
{
    public void Validate()
    {
        if (DownloadUri is null ||
            !DownloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Installer downloads must use HTTPS.");
        }
        if (string.IsNullOrWhiteSpace(Sha256) || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Installer SHA-256 must contain 64 hexadecimal characters.");
        }
        if (string.IsNullOrWhiteSpace(ExpectedSignerSubject))
        {
            throw new InvalidOperationException("An expected Authenticode signer is required.");
        }
    }
}
