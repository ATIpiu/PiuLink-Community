package com.limelight.computers;

import android.system.Os;
import android.system.OsConstants;
import com.limelight.nvstream.http.ComputerDetails.AddressTuple;
import com.limelight.nvstream.http.NvHTTP;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.URI;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public final class HostInvite {
    static final String BASE = "https://atipiu.org:8443/piulink/";
    private static final Pattern FRAGMENT = Pattern.compile("([a-f0-9]{64})\\.([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})");
    final String room, uuid, serviceBase;
    private HostInvite(URI uri, Matcher match) {
        serviceBase = "https://" + uri.getRawAuthority() + "/piulink/";
        room = match.group(1); uuid = match.group(2);
    }

    public static URI parseUri(String text) {
        if (text == null) return null;
        try {
            URI uri = URI.create(text);
            if (!"https".equals(uri.getScheme()) || uri.getHost() == null || uri.getUserInfo() != null ||
                    uri.getRawQuery() != null || !"/piulink/join".equals(uri.getRawPath()) ||
                    uri.getRawFragment() == null || !FRAGMENT.matcher(uri.getRawFragment()).matches()) return null;
            return uri;
        } catch (IllegalArgumentException ignored) { return null; }
    }

    static HostInvite parse(String text) {
        URI uri = parseUri(text);
        if (uri == null) throw new IllegalArgumentException("Invalid Host invitation");
        Matcher match = FRAGMENT.matcher(uri.getRawFragment());
        if (!match.matches()) throw new IllegalArgumentException("Invalid Host invitation");
        return new HostInvite(uri, match);
    }

    static final class ServiceUnavailable extends IOException {
        ServiceUnavailable(int status) { super("Invitation service returned HTTP " + status); }
    }

    static boolean isTransient(IOException failure) {
        if (Thread.currentThread().isInterrupted()) return false;
        for (Throwable cause = failure; cause != null; cause = cause.getCause()) {
            if (cause instanceof javax.net.ssl.SSLException) return false;
            if (cause instanceof ServiceUnavailable || cause instanceof java.io.InterruptedIOException || cause instanceof java.io.EOFException ||
                    cause instanceof java.net.SocketException || cause instanceof java.net.UnknownHostException) return true;
        }
        return false;
    }

    List<AddressTuple> resolve() throws IOException {
        HttpURLConnection connection = (HttpURLConnection)new URL(serviceBase + "v1/resolve").openConnection();
        try {
            connection.setInstanceFollowRedirects(false);
            connection.setUseCaches(false);
            connection.setConnectTimeout(4000);
            connection.setReadTimeout(4000);
            connection.setRequestMethod("POST");
            connection.setRequestProperty("Authorization", "Bearer " + room);
            connection.setFixedLengthStreamingMode(0);
            connection.setDoOutput(true);
            connection.getOutputStream().close();
            int status = connection.getResponseCode();
            if (status == 404) throw new FileNotFoundException("Host offline");
            if (status >= 502 && status <= 504) throw new ServiceUnavailable(status);
            if (status != 200) throw new IOException("Directory unavailable");
            ByteArrayOutputStream body = new ByteArrayOutputStream();
            try (InputStream input = connection.getInputStream()) {
                byte[] buf = new byte[512];
                int size;
                while ((size = input.read(buf)) != -1) {
                    if (body.size() + size > 4096) throw new IOException("Invalid response");
                    body.write(buf, 0, size);
                }
            }
            JSONObject data = new JSONObject(body.toString(StandardCharsets.UTF_8.name()));
            if (!uuid.equalsIgnoreCase(data.getString("hostId"))) throw new IOException("Wrong Host identity");
            JSONArray ips = data.getJSONArray("addresses");
            if (ips.length() > 13) throw new IOException("Invalid addresses");
            List<AddressTuple> result = new ArrayList<>();
            for (int i = 0; i < ips.length(); i++) {
                String value = ips.getString(i);
                InetAddress ip = Os.inet_pton(value.contains(":") ? OsConstants.AF_INET6 : OsConstants.AF_INET, value);
                if (ip != null && !ip.isLoopbackAddress() && !ip.isMulticastAddress() &&
                        !ip.isLinkLocalAddress() && !ip.isAnyLocalAddress())
                    result.add(new AddressTuple(ip.getHostAddress(), NvHTTP.DEFAULT_HTTP_PORT));
            }
            return result;
        } catch (JSONException invalid) {
            throw new IOException("Invalid directory response", invalid);
        } finally {
            connection.disconnect();
        }
    }
}
