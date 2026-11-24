using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PiuLink.Client.Core.Models;

namespace PiuLink.Client.Core.Networking;

public sealed record HostDisplay(int CaptureIndex, string DeviceId, string Name, bool IsPrimary, bool IsVirtual,
    int? Width, int? Height, bool IsSelected, int? ScalePercent = null, int[]? ScaleOptions = null, HostDisplayMode[]? Modes = null)
{
    public string Label => $"{Name} · {(IsVirtual ? "虚拟屏幕" : "物理屏幕")} · {CaptureIndex + 1}";
}
public sealed record HostDisplayMode(int Width, int Height, int Fps) { public string Resolution => $"{Width}x{Height}"; }
public sealed record HostResolution(int Width, int Height) { public string Id => $"{Width}x{Height}"; }
public sealed record HostDisplayInventory(bool ExclusiveDisplay, HostDisplay[] Displays, HostResolution[] VirtualResolutions,
    int[] VirtualRefreshRates, bool SupportsWindowGathering = false, bool SupportsDisplayScaling = false);

public sealed class RetainedDisplayRouteException() : IOException("电脑仍保留原应用。可用原屏幕恢复，或先结束应用再更换屏幕。");

public sealed class HostCompanionClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public HostCompanionClient(HttpClient? http = null) {
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(25) };
    }
    public static Uri Endpoint(HostEndpoint host, string path) => new UriBuilder("http", host.Host, 48011, path).Uri;
    public static HostDisplayInventory ParseInventory(string json) {
        var value = JsonSerializer.Deserialize<HostDisplayInventory>(json, Json) ?? throw new IOException("Host 屏幕数据为空。");
        if (value.Displays is null || value.Displays.Length == 0 || value.VirtualResolutions is null || value.VirtualRefreshRates is null ||
            value.Displays.Any(d => string.IsNullOrWhiteSpace(d.DeviceId) || d.CaptureIndex < 0 || !HasValidOptionalDimensions(d)) ||
            value.Displays.Select(d => d.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Displays.Length ||
            value.VirtualResolutions.Any(mode => mode.Width is < 100 or > 99999 || mode.Height is < 100 or > 99999) ||
            value.VirtualResolutions.Select(mode => mode.Id).Distinct(StringComparer.Ordinal).Count() != value.VirtualResolutions.Length ||
            value.VirtualRefreshRates.Any(rate => rate is < 10 or > 480) ||
            value.VirtualRefreshRates.Distinct().Count() != value.VirtualRefreshRates.Length ||
            value.Displays.Any(display => display.ScalePercent.HasValue && !IsWindowsScale(display.ScalePercent.Value)) ||
            value.Displays.Any(display => (display.ScaleOptions ?? []).Any(option => !IsWindowsScale(option)) ||
                (display.ScaleOptions ?? []).Distinct().Count() != (display.ScaleOptions ?? []).Length) ||
            value.Displays.Any(display => (display.Modes ?? []).Any(mode => mode.Width < 100 || mode.Height < 100 || mode.Fps is < 30 or > 480)))
            throw new IOException("Host 屏幕数据格式不受支持，请更新 Host。");
        return value with { Displays = value.Displays.Select(d => (string.IsNullOrWhiteSpace(d.Name)
            ? d with { Name = $"Display {d.CaptureIndex + 1}" } : d) with
            { ScaleOptions = d.ScaleOptions ?? [], Modes = d.Modes ?? [] }).ToArray() };
    }

    private static bool IsWindowsScale(int value) => value is
        100 or 125 or 150 or 175 or 200 or 225 or 250 or 300 or 350 or 400 or 450 or 500;

    private static bool HasValidOptionalDimensions(HostDisplay display) =>
        (display.Width is null && display.Height is null) ||
        (display.Width is >= 100 and <= 99999 && display.Height is >= 100 and <= 99999);
    public async Task<HostDisplayInventory> GetInventoryAsync(HostEndpoint endpoint, CancellationToken token) {
        using var paired = _ownsClient ? await Streaming.QtCompanionIdentity.CreateClientAsync(endpoint, token) : null;
        using var response = await (paired ?? _http).GetAsync(new UriBuilder("https", endpoint.Host, 48011, "/v1/displays").Uri, HttpCompletionOption.ResponseHeadersRead, token);
        CheckResponse(response);
        return ParseInventory(await ReadBounded(response, 128 * 1024, token));
    }

    public static HostDisplay? ResolveDisplay(HostDisplayInventory inventory, string? displayId) =>
        !string.IsNullOrEmpty(displayId)
            ? inventory.Displays.FirstOrDefault(display => string.Equals(display.DeviceId, displayId, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("所选屏幕已断开，请重新选择要分享的屏幕。")
            : inventory.Displays.FirstOrDefault(display => display.IsPrimary && !display.IsVirtual)
                ?? inventory.Displays.FirstOrDefault(display => !display.IsVirtual)
                ?? inventory.Displays.FirstOrDefault(display => display.IsVirtual);

    public static HostDisplay? ResolveStreamDisplay(HostDisplayInventory inventory, string? displayId, StreamProfile profile)
    {
        var display = ResolveDisplay(inventory, displayId);
        if (profile.GatherWindows && display is { IsVirtual: false } && !inventory.SupportsWindowGathering)
            throw new InvalidOperationException("请更新 Host，以使用集中窗口功能。");
        if (display is null && profile.GatherWindows)
            throw new InvalidOperationException("没有可用于集中窗口的物理屏幕。");
        if (display?.IsVirtual == true && !(display.Modes ?? []).Any(mode =>
            mode.Resolution == profile.Resolution && mode.Fps == profile.FramesPerSecond))
            throw new InvalidOperationException("该虚拟屏幕不支持当前分辨率或帧率，请从 Host 提供的选项中选择。");
        return display;
    }

    public static Dictionary<string, object?> Selection(HostDisplay? display, StreamProfile profile) {
        profile.Validate();
        var request = new Dictionary<string, object?> { ["deviceId"] = display?.DeviceId, ["exclusive"] = display?.IsVirtual == true };
        if (display is { IsVirtual: false } && profile.GatherWindows) request["gatherWindows"] = true;
        if (display is not null && profile.DisplayScalePercent > 0)
            request["scalePercent"] = profile.DisplayScalePercent;
        if (display?.IsVirtual == true) {
            var dimensions = profile.Resolution.Split('x');
            request["width"] = int.Parse(dimensions[0]); request["height"] = int.Parse(dimensions[1]);
            request["fps"] = profile.FramesPerSecond;
        }
        return request;
    }
    public async Task PrepareAsync(HostEndpoint endpoint, HostDisplay? display, StreamProfile profile, CancellationToken token) {
        using var paired = _ownsClient ? await Streaming.QtCompanionIdentity.CreateClientAsync(endpoint, token) : null;
        var selection = Selection(display, profile);
        selection["requestId"] = Guid.NewGuid().ToString();
        Streaming.GameStreamHttpClient.PreparedRequests[endpoint.Host] = (string)selection["requestId"]!;
        for (int attempt = 0; ; attempt++) {
            var payload = JsonSerializer.Serialize(selection, Json);
            using var request = new HttpRequestMessage(HttpMethod.Post, new UriBuilder("https", endpoint.Host, 48011, "/v1/displays/select").Uri) {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var response = await (paired ?? _http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.Conflict) {
                var body = await ReadBounded(response, 4096, token);
                string? reason = null;
                try {
                    using var failure = JsonDocument.Parse(body);
                    if (failure.RootElement.TryGetProperty("error", out var value)) reason = value.GetString();
                } catch (JsonException) { }
                if (reason == "display_recovery_failed") throw new IOException("请在 Host 的显示设置中点击“修复桌面布局”，再重新连接。");
                if (reason == "retained_application")
                    throw new RetainedDisplayRouteException();
                if (reason == "display_mode_unavailable")
                    throw new IOException("Host 屏幕不支持当前分辨率、帧率或显示模式。");
                if (!string.IsNullOrEmpty(reason) && reason is not ("stream_active" or "display_recovering"))
                    throw new IOException($"Host 无法准备屏幕（{reason}）。");
                if (attempt < 39) { await Task.Delay(500, token); continue; }
                throw new IOException(reason == "display_recovering" ? "Host 仍在恢复屏幕，请稍后重试。" : "Host 仍有串流连接，请稍后重试。");
            }
            CheckResponse(response);
            using var json = JsonDocument.Parse(await ReadBounded(response, 4096, token));
            if (!json.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ready")
                throw new IOException("Host 尚未确认屏幕准备完成。");
            return;
        }
    }
    public async Task AbortAsync(HostEndpoint endpoint) {
        if (!Streaming.GameStreamHttpClient.PreparedRequests.TryRemove(endpoint.Host, out var id)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var paired = _ownsClient ? await Streaming.QtCompanionIdentity.CreateClientAsync(endpoint, timeout.Token) : null;
        using var request = new HttpRequestMessage(HttpMethod.Post, new UriBuilder("https", endpoint.Host, 48011, "/v1/displays/lease").Uri) {
            Content = new StringContent(JsonSerializer.Serialize(new { requestId = id, action = "abort" }, Json), Encoding.UTF8, "application/json")
        };
        using var response = await (paired ?? _http).SendAsync(request, timeout.Token);
        CheckResponse(response);
    }
    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Host 拒绝了设备身份。请确认选中了正确的电脑，并检查配对记录。",
            HttpStatusCode.ServiceUnavailable => "Host 串流服务暂不可用。请在电脑上检查引擎状态后重试。",
            HttpStatusCode.NotFound => "Host 未提供此功能。请检查 Host 与客户端版本。",
            HttpStatusCode.Conflict => "电脑正在处理另一个会话。请结束该会话后重试。",
            _ => $"Host 请求失败（HTTP {(int)response.StatusCode}）。"
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static async Task<string> ReadBounded(HttpResponseMessage response, int maximum, CancellationToken token) {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0) {
            if (output.Length + count > maximum) throw new IOException("Host 响应过大。");
            output.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
