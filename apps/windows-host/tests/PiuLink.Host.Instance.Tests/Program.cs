using System.Diagnostics;
using System.Reflection;
using PiuLink.Host.Services;

if (args.Length == 2)
{
    using var worker = new SingleInstanceService(args[1]);
    if (args[0] == "--activate")
        return !worker.IsPrimary && worker.ActivateExistingAsync().GetAwaiter().GetResult() ? 0 : 1;
    Console.WriteLine(worker.IsPrimary ? "READY" : "DUPLICATE");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

static Process Start(string mode, string name)
{
    var start = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(mode);
    start.ArgumentList.Add(name);
    return Process.Start(start) ?? throw new Exception("Could not start test child.");
}
static void Require(bool value, string message)
{
    if (!value) throw new Exception(message);
}

var name = "PiuLink.InstanceTest." + Guid.NewGuid().ToString("N");
var activations = 0;
using (var primary = new SingleInstanceService(name))
{
    Require(primary.IsPrimary, "First launch was not primary");
    primary.Listen((command, appId) => {
        if (command == 1) Interlocked.Increment(ref activations);
    });
    var children = Enumerable.Range(0, 6).Select(_ => Start("--activate", name)).ToArray();
    try
    {
        foreach (var child in children)
        {
            Require(child.WaitForExit(12000), "Activation timed out");
            Require(child.ExitCode == 0, "Second launch did not activate the primary: " + child.StandardError.ReadToEnd());
        }
        Require(activations == 6, "An activation request was lost");
    }
    finally
    {
        foreach (var child in children)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.Dispose();
        }
    }
}
Console.WriteLine("PASS six concurrent launches activate one primary");
using (var reopened = new SingleInstanceService(name))
    Require(reopened.IsPrimary, "Normal exit did not release the singleton");
Console.WriteLine("PASS normal exit permits reopening");
using (var crashed = Start("--hold", name))
{
    try
    {
        Require(crashed.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() == "READY",
            "Crash-test process did not own the singleton");
        crashed.Kill(entireProcessTree: true);
        Require(crashed.WaitForExit(5000), "Test process did not terminate");
        using var recovered = new SingleInstanceService(name);
        Require(recovered.IsPrimary, "Crash left a stale singleton");
    }
    finally { if (!crashed.HasExited) crashed.Kill(entireProcessTree: true); }
}
Console.WriteLine("PASS crash permits a new primary");
return 0;
