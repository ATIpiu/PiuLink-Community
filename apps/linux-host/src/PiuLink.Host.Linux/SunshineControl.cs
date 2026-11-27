using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PiuLink.Host.Linux;

internal sealed class SunshineControl : IDisposable
{
    private static readonly Uri Endpoint = new("https://127.0.0.1:47990/");
    private readonly HttpClient _http;

    internal SunshineControl(HostState state)
    {
        var credentials = File.ReadAllLines(Path.Combine(state.DirectoryPath, "engine-auth"));
        if (credentials.Length != 2 || string.IsNullOrWhiteSpace(credentials[1]))
            throw new IOException("Sunshine control credentials are missing. Run the Host setup first.");
        using var certificate = state.LoadCertificate();
        var digest = SHA256.HashData(certificate.RawData);
        _http = new HttpClient(new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer is not null &&
                CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(peer.RawData))
        }) { BaseAddress = Endpoint, Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials[0]}:{credentials[1]}")));
        _http.DefaultRequestHeaders.Referrer = Endpoint;
        _http.DefaultRequestHeaders.Add("Origin", Endpoint.GetLeftPart(UriPartial.Authority));
    }

    internal async Task<JsonElement> RequestAsync(string path, object? body = null, CancellationToken token = default)
    {
        using var response = body is null
            ? await _http.GetAsync(path, token)
            : await _http.PostAsJsonAsync(path, body, token);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) &&
            (status.ValueKind == JsonValueKind.False || status.ToString().Equals("false", StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"Sunshine rejected {path}.");
        return root.Clone();
    }

    public void Dispose() => _http.Dispose();
}
