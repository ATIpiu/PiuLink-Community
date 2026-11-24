using System.Text.Json;

namespace PiuLink.Host.Core.Install;

public sealed record BundledComponentManifest(
    int SchemaVersion,
    string Id,
    string Version,
    string Architecture,
    string FileName,
    Uri DownloadUri,
    string Sha256,
    string ExpectedSignerSubject,
    string ExpectedSignerThumbprint,
    string LicenseExpression,
    string LicenseFileName,
    string LicenseSha256,
    Uri LicenseUri,
    string SourceTag,
    string SourceCommit,
    Uri SourceUri)
{
    public static async Task<BundledComponentManifest> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<BundledComponentManifest>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);
        if (manifest is null)
        {
            throw new InvalidDataException("The bundled-component manifest is empty.");
        }

        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported bundled-component manifest schema: {SchemaVersion}.");
        }

        if (!Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported bundled-component architecture: {Architecture}.");
        }

        ValidateFileName(FileName, nameof(FileName));
        ValidateFileName(LicenseFileName, nameof(LicenseFileName));
        new ReleaseArtifact(Id, Version, DownloadUri, Sha256, ExpectedSignerSubject).Validate();

        if (string.IsNullOrWhiteSpace(LicenseSha256) ||
            LicenseSha256.Length != 64 || !LicenseSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The license SHA-256 must contain 64 hexadecimal characters.");
        }

        if (string.IsNullOrWhiteSpace(ExpectedSignerThumbprint) ||
            ExpectedSignerThumbprint.Length != 40 || !ExpectedSignerThumbprint.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The expected Authenticode thumbprint must contain 40 hexadecimal characters.");
        }

        if (LicenseUri is null || SourceUri is null ||
            !LicenseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !SourceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("License and source links must use HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(LicenseExpression) || string.IsNullOrWhiteSpace(SourceTag) ||
            string.IsNullOrWhiteSpace(SourceCommit) ||
            SourceCommit.Length != 40 || !SourceCommit.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("License and source metadata are required.");
        }
    }

    private static void ValidateFileName(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{field} must be a plain file name.");
        }
    }
}
