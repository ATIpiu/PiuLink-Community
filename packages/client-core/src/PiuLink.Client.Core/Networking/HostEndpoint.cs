using System.Net;
using System.Net.Sockets;

namespace PiuLink.Client.Core.Networking;

public sealed record HostEndpoint(string Host, int Port)
{
    public const int DefaultSunshineHttpPort = 47989;

    public static async Task<HostEndpoint> ResolveAsync(string input, string? cached = null, CancellationToken token = default)
    {
        var invite = PiuLink.Rendezvous.HostInvite.Parse(input);
        if (invite == null) return Parse(input);
        Func<CancellationToken,Task<string>>? punch = OperatingSystem.IsWindows() ?
            ct => DirectInviteClient.ConnectAsync(invite, ct) : null;
        return Parse(await invite.ResolveAsync(cached, token, punch).ConfigureAwait(false));
    }

    public string MoonlightTarget =>
        IPAddress.TryParse(Host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? Port == DefaultSunshineHttpPort ? $"[{Host}]" : $"[{Host}]:{Port}"
            : Port == DefaultSunshineHttpPort ? Host : $"{Host}:{Port}";

    public bool IsTailscaleAddress
    {
        get
        {
            if (Host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!IPAddress.TryParse(Host, out var address))
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
            }

            // Every tailnet receives IPv6 addresses from fd7a:115c:a1e0::/48.
            return address.AddressFamily == AddressFamily.InterNetworkV6 &&
                   bytes.Length == 16 &&
                   bytes[0] == 0xfd && bytes[1] == 0x7a &&
                   bytes[2] == 0x11 && bytes[3] == 0x5c &&
                   bytes[4] == 0xa1 && bytes[5] == 0xe0;
        }
    }

    public static HostEndpoint Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new FormatException("Enter an IP address or host name.");
        }

        var value = input.Trim();
        if (value.Contains("://", StringComparison.Ordinal) || value.Any(char.IsWhiteSpace))
        {
            throw new FormatException("Enter a host name or IP address without a URL scheme or spaces.");
        }

        string host;
        var port = DefaultSunshineHttpPort;

        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket <= 1)
            {
                throw new FormatException("Invalid bracketed IPv6 address.");
            }

            host = value[1..closingBracket];
            if (!IPAddress.TryParse(host, out var ipv6) || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
                throw new FormatException("Invalid bracketed IPv6 address.");
            var suffix = value[(closingBracket + 1)..];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || !TryParsePort(suffix[1..], out port))
                {
                    throw new FormatException("Invalid port.");
                }
            }
        }
        else if (IPAddress.TryParse(value, out var directAddress) &&
                 directAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            host = value;
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon > 0 && value.IndexOf(':') == colon)
            {
                if (!TryParsePort(value[(colon + 1)..], out port))
                {
                    throw new FormatException("Invalid port.");
                }

                host = value[..colon];
            }
            else
            {
                host = value;
            }
        }

        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.StartsWith('-'))
        {
            throw new FormatException("Invalid host name.");
        }

        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new FormatException("Invalid host name or IP address.");
        }

        return new HostEndpoint(host, port);
    }

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(value, out port) && port is >= 1 and <= 65535;
}
