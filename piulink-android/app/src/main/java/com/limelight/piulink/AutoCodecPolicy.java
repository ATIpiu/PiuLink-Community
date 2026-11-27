package com.limelight.piulink;

import java.net.Inet6Address;
import java.net.InetAddress;

public final class AutoCodecPolicy {
    private static final long FULL_HD_PIXELS = 1920L * 1080L;

    private AutoCodecPolicy() { }

    public static boolean preferAv1(String host, int width, int height, boolean meteredNetwork) {
        return meteredNetwork || (long) width * height > FULL_HD_PIXELS || isTailscaleAddress(host);
    }

    static boolean isTailscaleAddress(String host) {
        if (host == null) return false;
        String value = host.trim();
        if (value.toLowerCase(java.util.Locale.ROOT).endsWith(".ts.net")) return true;
        if (value.startsWith("[") && value.endsWith("]")) value = value.substring(1, value.length() - 1);
        // InetAddress resolves host names. Only pass numeric-looking values so
        // automatic codec selection never adds a DNS probe to stream startup.
        if (!value.contains(":") && !value.matches("[0-9.]+")) return false;
        try {
            InetAddress address = InetAddress.getByName(value);
            byte[] bytes = address.getAddress();
            if (bytes.length == 4) {
                int first = bytes[0] & 0xff;
                int second = bytes[1] & 0xff;
                return first == 100 && second >= 64 && second <= 127;
            }
            return address instanceof Inet6Address && bytes.length == 16 &&
                    (bytes[0] & 0xff) == 0xfd && (bytes[1] & 0xff) == 0x7a &&
                    (bytes[2] & 0xff) == 0x11 && (bytes[3] & 0xff) == 0x5c &&
                    (bytes[4] & 0xff) == 0xa1 && (bytes[5] & 0xff) == 0xe0;
        }
        catch (Exception ignored) {
            return false;
        }
    }
}
