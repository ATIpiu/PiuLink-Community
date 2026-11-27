using System.Security.Cryptography;

namespace PiuLink.Host.Core.Install;

public static class ArtifactHashVerifier
{
    public static async Task<bool> VerifyAsync(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
        {
            return false;
        }

        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

