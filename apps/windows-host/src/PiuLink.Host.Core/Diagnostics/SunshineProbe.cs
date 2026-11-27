using PiuLink.Host.Core.Models;

namespace PiuLink.Host.Core.Diagnostics;

public sealed class SunshineProbe
{
    private static readonly string[] KnownServiceNames = ["SunshineService", "sunshinesvc"];
    private readonly IProcessRunner _processRunner;
    private readonly Func<string?> _executableLocator;

    public SunshineProbe(IProcessRunner processRunner, Func<string?>? executableLocator = null)
    {
        _processRunner = processRunner;
        _executableLocator = executableLocator ?? ExecutableLocator.FindSunshine;
    }

    public async Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_executableLocator() is null)
        {
            return new ComponentHealth("Sunshine", ComponentState.Missing, "Not installed");
        }

        foreach (var serviceName in KnownServiceNames)
        {
            var result = await _processRunner.RunAsync(
                "sc.exe",
                ["query", serviceName],
                TimeSpan.FromSeconds(5),
                cancellationToken);

            if (!result.Succeeded)
            {
                continue;
            }

            if (result.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                var configuration = await _processRunner.RunAsync(
                    "sc.exe",
                    ["qc", serviceName],
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                var autoStart = configuration.Succeeded &&
                    configuration.StandardOutput.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase);
                return new ComponentHealth(
                    "Sunshine",
                    ComponentState.Ready,
                    autoStart ? "Service running · starts with Windows" : "Service running",
                    autoStart ? serviceName : $"{serviceName}; automatic start was not detected");
            }

            return new ComponentHealth("Sunshine", ComponentState.NeedsAttention, "Installed; service not running", serviceName);
        }

        return new ComponentHealth(
            "Sunshine",
            ComponentState.Installed,
            "Installed; Windows service not found",
            "Sunshine may be running as a user process. Service setup is recommended for unattended access.");
    }
}
