using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PiuLink.Host.Core.Install;

public enum BundledInstallerState
{
    Missing,
    Ready,
    Invalid
}

public sealed record BundledInstallerStatus(
    BundledInstallerState State,
    string Message,
    BundledComponentManifest? Manifest = null);

public sealed record BundledInstallResult(
    bool Succeeded,
    bool Cancelled,
    string Message,
    int? ExitCode = null);

public sealed class SunshineInstallerService
{
    private const string ManifestFileName = "manifest.json";

    private readonly string _componentDirectory;
    private readonly BundledComponentManifest _expectedManifest;
    private readonly Func<string, string, string, AuthenticodeVerificationResult> _signatureVerifier;

    public SunshineInstallerService(
        string componentDirectory,
        BundledComponentManifest? expectedManifest = null,
        Func<string, string, string, AuthenticodeVerificationResult>? signatureVerifier = null)
    {
        _componentDirectory = Path.GetFullPath(componentDirectory);
        _expectedManifest = expectedManifest ?? SunshinePinnedRelease.Manifest;
        _expectedManifest.Validate();
        _signatureVerifier = signatureVerifier ?? AuthenticodeVerifier.Verify;
    }

    public static SunshineInstallerService CreateForApplication(string applicationDirectory) =>
        new(Path.Combine(applicationDirectory, "third_party", "sunshine"));

    public async Task<BundledInstallerStatus> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(_componentDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new BundledInstallerStatus(
                BundledInstallerState.Missing,
                "This development build does not contain the reviewed Sunshine payload.");
        }

        BundledComponentManifest manifest;
        try
        {
            manifest = await BundledComponentManifest.LoadAsync(manifestPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return new BundledInstallerStatus(
                BundledInstallerState.Invalid,
                $"The Sunshine manifest is invalid: {exception.Message}");
        }

        if (manifest != _expectedManifest)
        {
            return new BundledInstallerStatus(
                BundledInstallerState.Invalid,
                "The staged Sunshine manifest does not match PiuLink's compiled release pin.");
        }

        var payloadPath = ResolveComponentFile(manifest.FileName);
        var licensePath = ResolveComponentFile(manifest.LicenseFileName);
        if (!File.Exists(payloadPath) || !File.Exists(licensePath))
        {
            return new BundledInstallerStatus(
                BundledInstallerState.Missing,
                "The Sunshine payload or its GPLv3 license file is missing.",
                manifest);
        }

        if (!await ArtifactHashVerifier.VerifyAsync(licensePath, manifest.LicenseSha256, cancellationToken))
        {
            return new BundledInstallerStatus(
                BundledInstallerState.Invalid,
                "The bundled Sunshine GPLv3 license file failed SHA-256 verification.",
                manifest);
        }

        if (!await ArtifactHashVerifier.VerifyAsync(payloadPath, manifest.Sha256, cancellationToken))
        {
            return new BundledInstallerStatus(
                BundledInstallerState.Invalid,
                "The bundled Sunshine installer failed SHA-256 verification.",
                manifest);
        }

        var signature = _signatureVerifier(
            payloadPath,
            manifest.ExpectedSignerSubject,
            manifest.ExpectedSignerThumbprint);
        if (!signature.IsValid)
        {
            return new BundledInstallerStatus(
                BundledInstallerState.Invalid,
                signature.Message,
                manifest);
        }

        return new BundledInstallerStatus(
            BundledInstallerState.Ready,
            $"Sunshine {manifest.Version} is verified and ready to install.",
            manifest);
    }

    public async Task<BundledInstallResult> InstallAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await InspectAsync(cancellationToken);
        if (status.State != BundledInstallerState.Ready || status.Manifest is null)
        {
            return new BundledInstallResult(false, false, status.Message);
        }

        var payloadPath = ResolveComponentFile(status.Manifest.FileName);
        FileStream payloadLock;
        try
        {
            // Keep the verified file locked against writes, replacement and deletion until msiexec exits.
            // This closes the time-of-check/time-of-use gap between validation and elevation.
            payloadLock = new FileStream(
                payloadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
        catch (IOException exception)
        {
            return new BundledInstallResult(
                false,
                false,
                $"PiuLink could not lock the verified Sunshine payload: {exception.Message}");
        }

        await using var lockedPayload = payloadLock;
        if (!await ArtifactHashVerifier.VerifyAsync(payloadPath, status.Manifest.Sha256, cancellationToken))
        {
            return new BundledInstallResult(false, false, "The Sunshine payload changed before elevation.");
        }

        var lockedSignature = _signatureVerifier(
            payloadPath,
            status.Manifest.ExpectedSignerSubject,
            status.Manifest.ExpectedSignerThumbprint);
        if (!lockedSignature.IsValid)
        {
            return new BundledInstallResult(false, false, lockedSignature.Message);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(payloadPath);
        startInfo.ArgumentList.Add("/qn");
        startInfo.ArgumentList.Add("/norestart");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new BundledInstallResult(false, false, "Windows could not start the Sunshine installer.");
            }

            await process.WaitForExitAsync(cancellationToken);
            var succeeded = process.ExitCode is 0 or 1641 or 3010;
            return new BundledInstallResult(
                succeeded,
                false,
                succeeded
                    ? "Sunshine was installed successfully."
                    : $"The Sunshine installer exited with code {process.ExitCode}.",
                process.ExitCode);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new BundledInstallResult(false, true, "Installation was cancelled before elevation.");
        }
    }

    public async Task<string?> GetLicensePathAsync(CancellationToken cancellationToken = default)
    {
        var status = await InspectAsync(cancellationToken);
        return status.Manifest is null
            ? null
            : ResolveComponentFile(status.Manifest.LicenseFileName);
    }

    private string ResolveComponentFile(string fileName)
    {
        var candidate = Path.GetFullPath(Path.Combine(_componentDirectory, fileName));
        var componentPrefix = _componentDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _componentDirectory
            : _componentDirectory + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(componentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The component manifest attempted to escape its staging directory.");
        }

        return candidate;
    }
}
