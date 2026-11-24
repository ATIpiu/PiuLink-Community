using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PiuLink.Host.Core.Diagnostics;

namespace PiuLink.Host.Services;

internal sealed record SunshineDeviceCredential(string Username, string Secret);

internal sealed record SunshineProvisionResult(
    bool Succeeded,
    bool Cancelled,
    string Message,
    SunshineDeviceCredential? Credential = null);

internal sealed class SunshineManagedIdentity
{
    private const string CredentialTarget = "ATIPiu Labs/PiuLink Host/Sunshine control";
    private const string DeviceUsername = "piulink";

    private readonly WindowsCredentialStore _credentialStore = new();

    public SunshineDeviceCredential? TryRead()
    {
        var stored = _credentialStore.Read(CredentialTarget);
        return stored is null ||
               string.IsNullOrWhiteSpace(stored.Username) ||
               string.IsNullOrEmpty(stored.Secret)
            ? null
            : new SunshineDeviceCredential(stored.Username, stored.Secret);
    }

    public async Task<SunshineProvisionResult> ProvisionAsync(
        CancellationToken cancellationToken = default)
    {
        var sunshinePath = ExecutableLocator.FindSunshine();
        if (sunshinePath is null)
        {
            return new SunshineProvisionResult(
                false,
                false,
                "Install Sunshine before setting up PiuLink-managed control.");
        }

        // The Windows credential is the durable identity. An application/engine reinstall
        // may remove executables, but must never rotate this secret. If Sunshine lost its
        // local configuration, reapply the same credential to repair it.
        var credential = TryRead() ?? CreateCredential();
        _credentialStore.Write(
            CredentialTarget,
            new StoredCredential(credential.Username, credential.Secret));

        var script = BuildProvisioningScript(sunshinePath, credential);
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new SunshineProvisionResult(
                    false,
                    false,
                    "Windows could not start Sunshine secure setup.");
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? new SunshineProvisionResult(
                    true,
                    false,
                    "Sunshine is now controlled by your PiuLink device identity.",
                    credential)
                : new SunshineProvisionResult(
                    false,
                    false,
                    $"Sunshine secure setup exited with code {process.ExitCode}.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new SunshineProvisionResult(
                false,
                true,
                "Sunshine secure setup was cancelled before elevation.");
        }
    }

    private static SunshineDeviceCredential CreateCredential()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new SunshineDeviceCredential(DeviceUsername, secret);
    }

    private static string BuildProvisioningScript(
        string sunshinePath,
        SunshineDeviceCredential credential)
    {
        static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        var sunshineDirectory = Path.GetDirectoryName(sunshinePath)
            ?? throw new InvalidOperationException("Sunshine's installation directory could not be resolved.");
        var configPath = Path.Combine(sunshineDirectory, "config", "sunshine.conf");

        return string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "$service = Get-Service -Name 'SunshineService' -ErrorAction Stop",
            "if ($service.Status -ne 'Stopped') { Stop-Service -Name 'SunshineService' -Force -ErrorAction Stop }",
            "try {",
            $"  $configPath = {Quote(configPath)}",
            "  $configDirectory = Split-Path -Parent $configPath",
            "  [System.IO.Directory]::CreateDirectory($configDirectory) | Out-Null",
            "  $lines = if ([System.IO.File]::Exists($configPath)) { [System.IO.File]::ReadAllLines($configPath) } else { @() }",
            "  $lines = @($lines | Where-Object { $_ -notmatch '^\\s*system_tray\\s*=' })",
            "  $updatedConfig = (@($lines) + 'system_tray = false') -join [Environment]::NewLine",
            "  $updatedConfig += [Environment]::NewLine",
            "  [System.IO.File]::WriteAllText($configPath, $updatedConfig, [System.Text.UTF8Encoding]::new($false))",
            $"  Push-Location -LiteralPath {Quote(sunshineDirectory)}",
            "  try {",
            $"    & {Quote(sunshinePath)} --creds {Quote(credential.Username)} {Quote(credential.Secret)}",
            "    if ($LASTEXITCODE -ne 0) { throw \"Sunshine credential setup failed with exit code $LASTEXITCODE.\" }",
            "  } finally { Pop-Location }",
            "}",
            "finally {",
            "  Start-Service -Name 'SunshineService' -ErrorAction Stop",
            "}");
    }
}
