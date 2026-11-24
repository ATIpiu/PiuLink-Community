package com.limelight.piulink;

import java.net.InetAddress;
import java.util.Locale;

/**
 * Classifies only what PiuLink can observe at its socket boundary. Tailscale's
 * Android VPN does not expose its underlay path (direct IPv4, direct IPv6, or
 * DERP) to other applications, so overlay destinations must remain distinct.
 */
public final class NetworkPathClassifier {
    public enum Path {
        IPV4_DIRECT,
        IPV6_DIRECT,
        TAILSCALE_MANAGED,
        HOSTNAME_UNKNOWN
    }

    private NetworkPathClassifier() { }

    public static Path classify(String host) {
        if (host == null) return Path.HOSTNAME_UNKNOWN;
        String value = host.trim();
        if (value.isEmpty()) return Path.HOSTNAME_UNKNOWN;
        if (value.toLowerCase(Locale.ROOT).endsWith(".ts.net")) {
            return Path.TAILSCALE_MANAGED;
        }
        if (value.startsWith("[") && value.endsWith("]")) {
            value = value.substring(1, value.length() - 1);
        }

        // Never resolve DNS here. This method is called while refreshing the UI,
        // and a hostname may also resolve to a different address than the stream.
        if (!value.contains(":") && !value.matches("[0-9.]+")) {
            return Path.HOSTNAME_UNKNOWN;
        }

        try {
            byte[] bytes = InetAddress.getByName(value).getAddress();
            if (bytes.length == 4) {
                int first = bytes[0] & 0xff;
                int second = bytes[1] & 0xff;
                return first == 100 && second >= 64 && second <= 127
                        ? Path.TAILSCALE_MANAGED
                        : Path.IPV4_DIRECT;
            }
            if (bytes.length == 16) {
                boolean tailscale = (bytes[0] & 0xff) == 0xfd &&
                        (bytes[1] & 0xff) == 0x7a &&
                        (bytes[2] & 0xff) == 0x11 &&
                        (bytes[3] & 0xff) == 0x5c &&
                        (bytes[4] & 0xff) == 0xa1 &&
                        (bytes[5] & 0xff) == 0xe0;
                return tailscale ? Path.TAILSCALE_MANAGED : Path.IPV6_DIRECT;
            }
        }
        catch (Exception ignored) {
            // Invalid numeric-looking input is reported as unknown.
        }
        return Path.HOSTNAME_UNKNOWN;
    }
}
