using PiuLink.Rendezvous;

namespace PiuLink.Host.Linux;

internal sealed class InviteRegistration(string directory)
{
    private string SecretPath => Path.Combine(directory, "invite.key");

    internal async Task<string> EnableAsync(CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        if (!File.Exists(SecretPath))
        {
            var temp = SecretPath + "." + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temp, new FileStreamOptions {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                }))
                using (var writer = new StreamWriter(file))
                    writer.Write(InvitePublisher.NewOwner());
                try { File.Move(temp, SecretPath); }
                catch (IOException) when (File.Exists(SecretPath)) { }
            }
            finally { File.Delete(temp); }
        }
        return (await PublishAsync(token)).Link;
    }

    public void Disable() => File.Delete(SecretPath);

    private async Task<HostInvite> PublishAsync(CancellationToken token) =>
        await InvitePublisher.PublishAsync(await File.ReadAllTextAsync(SecretPath, token), token);

    internal async Task MaintainAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (File.Exists(SecretPath))
            {
                try { await PublishAsync(token); }
                catch (Exception) when (!token.IsCancellationRequested)
                { Console.Error.WriteLine("Invite registration failed. Retrying in 30 seconds."); }
            }
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }
    }
}
