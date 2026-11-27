using System.Text.Json;
using PiuLink.Client.Core.Streaming;

namespace PiuLink.Client.Core.Networking;

public sealed record HostSshInfo(string Username, int Port, string Platform);

public sealed class HostSshInfoClient
{
    public async Task<HostSshInfo> GetAsync(HostEndpoint endpoint, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        token = deadline.Token;
        using var client = await QtCompanionIdentity.CreateClientAsync(endpoint, token);
        using var response = await client.GetAsync(new UriBuilder("https", endpoint.Host, 48011, "v1/ssh").Uri,
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var data = new MemoryStream(); var buffer = new byte[1024]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (data.Length + count > 8192) throw new IOException("SSH configuration is too large.");
            data.Write(buffer, 0, count);
        }
        var info = JsonSerializer.Deserialize<HostSshInfo>(data.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IOException("Host returned an empty SSH configuration.");
        if (string.IsNullOrWhiteSpace(info.Username) || info.Username.Length > 256 || info.Username.Any(char.IsControl) ||
            info.Port is < 1 or > 65535)
            throw new IOException("Host returned an invalid SSH configuration.");
        return info;
    }
}
