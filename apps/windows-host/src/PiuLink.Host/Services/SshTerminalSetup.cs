using System.Diagnostics;
using System.IO;
using System.Text;

namespace PiuLink.Host.Services;

internal sealed record SshTerminalStatus(string OpenSsh);

internal static class SshTerminalSetup
{
    internal const string EnableScript = "try { $ErrorActionPreference='Stop'; " +
        "$capability=Get-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0'; " +
        "if ($capability.State -eq 'InstallPending') { exit 10 }; " +
        "if ($capability.State -ne 'Installed') { Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null; " +
        "$capability=Get-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' }; " +
        "if (!(Get-Service sshd -ErrorAction SilentlyContinue)) { " +
        "if ($capability.State -eq 'InstallPending') { exit 10 } else { exit 11 } }; " +
        "Set-Service sshd -StartupType Automatic; Start-Service sshd; " +
        "if (!(Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) { " +
        "New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH SSH Server (sshd)' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow | Out-Null }; exit 0 " +
        "} catch { exit 1 }";
    internal const string DisableScript = "try { $ErrorActionPreference='Stop'; Stop-Service sshd; Set-Service sshd -StartupType Disabled; exit 0 } catch { exit 1 }";
    public static async Task<SshTerminalStatus> ProbeAsync()
    {
        var ssh = await ReadAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
            "$s=Get-Service sshd -ErrorAction SilentlyContinue; if (!$s) {'NotInstalled'} else {$s.Status.ToString()}"]);
        string sshStatus = ssh.Output.Trim() switch {
            "Running" => "已启动", "Stopped" => "已安装，未启动", "NotInstalled" => "未安装", _ => "检测失败"
        };
        return new(sshStatus);
    }

    public static async Task SetOpenSshAsync(bool enabled)
    {
        var start = new ProcessStartInfo("powershell.exe") {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(enabled ? EnableScript : DisableScript))
        };
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Windows SSH 配置工具。");
        await process.WaitForExitAsync();
        if (process.ExitCode == 10)
            throw new IOException("OpenSSH 服务器已排队安装，但 Windows 尚未完成系统组件注册。请重启电脑一次；PiuLink 会在下次启动后自动检测服务状态。");
        if (process.ExitCode == 11)
            throw new IOException("Windows 报告 OpenSSH 已安装，但 sshd 服务没有注册。请在 Windows 设置的可选功能中修复或重新安装 OpenSSH 服务器。");
        if (process.ExitCode != 0) throw new IOException("Windows SSH 配置未完成。请检查管理员授权、Windows 更新服务及可选功能安装状态。");
    }

    private static async Task<(int ExitCode, string Output)> ReadAsync(string file, string[] args, int timeoutSeconds = 15)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        try {
            using var process = Process.Start(start) ?? throw new IOException("无法启动检测工具。");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return (-1, "检测或安装超时，请重试。"); }
            var output = await stdout + await stderr;
            return (process.ExitCode, output.Length > 2000 ? output[^2000..] : output);
        } catch (System.ComponentModel.Win32Exception) { return (-1, "系统组件不可用"); }
    }
}
