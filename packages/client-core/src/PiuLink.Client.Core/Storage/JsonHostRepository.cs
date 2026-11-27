using System.Text.Json;
using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;

namespace PiuLink.Client.Core.Storage;

public sealed class JsonHostRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonHostRepository(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ATIPiu Labs",
            "PiuLink Client",
            "hosts.json");
    }

    public string FilePath { get; }

    public async Task<IReadOnlyList<HostProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        // Readers can finish an existing snapshot while another process replaces the file.
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var hosts = await JsonSerializer.DeserializeAsync<List<HostProfile>>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (hosts is null) throw new InvalidDataException("The saved host list is null.");
        Validate(hosts);
        return hosts;
    }

    private static void Validate(IEnumerable<HostProfile> hosts)
    {
        var ids = new HashSet<Guid>();
        foreach (var host in hosts)
        {
            if (host is null || host.Id == Guid.Empty || string.IsNullOrWhiteSpace(host.Name) || !ids.Add(host.Id))
                throw new InvalidDataException("The saved host list contains an invalid record.");
            HostEndpoint.Parse(host.Address);
            host.StreamSettings?.Validate();
        }
    }

    public async Task SaveAsync(
        IReadOnlyCollection<HostProfile> hosts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(hosts);
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("Host repository path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $"hosts-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    hosts,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
