package com.limelight.piulink.ssh;

import com.limelight.utils.TouchDialog;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.net.Network;
import com.limelight.R;
import com.limelight.binding.PlatformBinding;
import com.limelight.computers.ComputerDatabaseManager;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.utils.NetHelper;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.function.Consumer;
import javax.net.ssl.HttpsURLConnection;

/** Host owns the default SSH account; the client uses the active computer address. */
public final class ComputerSsh {
    public static final int APP_ID = Integer.MIN_VALUE + 22;
    private ComputerSsh() { }

    private static ComputerDetails resolvePairedComputer(Context context, String cleanHost,
                                                         ComputerDetails knownComputer) throws IOException {
        if (knownComputer != null && knownComputer.serverCert != null) return knownComputer;
        ComputerDatabaseManager database = new ComputerDatabaseManager(context);
        try {
            ComputerDetails matched = null;
            for (ComputerDetails candidate : database.getAllComputers()) {
                if (candidate.serverCert == null) continue;
                for (ComputerDetails.AddressTuple tuple : new ComputerDetails.AddressTuple[] {
                        candidate.activeAddress, candidate.localAddress, candidate.remoteAddress,
                        candidate.manualAddress, candidate.ipv6Address }) {
                    if (tuple != null && cleanHost.equalsIgnoreCase(NetHelper.stripIpv6Zone(tuple.address))) {
                        if (matched != null && !matched.uuid.equals(candidate.uuid)) {
                            throw new IOException("Ambiguous Host identity");
                        }
                        matched = candidate;
                    }
                }
            }
            return matched;
        } finally {
            database.close();
        }
    }

    static HttpURLConnection openCompanionRequest(Context context, String rawHost, String path,
                                                  ComputerDetails knownComputer, String method,
                                                  byte[] body, int connectTimeoutMs, int readTimeoutMs,
                                                  Consumer<HttpURLConnection> activeTracker) throws IOException {
        String cleanHost = NetHelper.stripIpv6Zone(rawHost);
        String authority = NetHelper.formatAuthorityHost(cleanHost);
        ComputerDetails paired = resolvePairedComputer(context, cleanHost, knownComputer);
        String scheme = paired != null ? "https" : "http";
        URL url = new URL(scheme + "://" + authority + ":48011" + path);
        Network network = NetHelper.getBindableLocalNetworkForAddress(context, cleanHost);
        for (;;) {
            HttpURLConnection connection = (HttpURLConnection) (network == null ? url.openConnection() : network.openConnection(url));
            if (activeTracker != null) activeTracker.accept(connection);
            boolean writing = false;
            try {
                if (paired != null) {
                    new NvHTTP(new ComputerDetails.AddressTuple(cleanHost, 48011), 0, "",
                            paired.serverCert, PlatformBinding.getCryptoProvider(context))
                            .configurePairedCompanion((HttpsURLConnection) connection);
                }
                connection.setInstanceFollowRedirects(false);
                connection.setUseCaches(false);
                connection.setConnectTimeout(connectTimeoutMs);
                connection.setReadTimeout(readTimeoutMs);
                connection.setRequestMethod(method);
                if (body != null) {
                    connection.setDoOutput(true);
                    connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                    connection.setFixedLengthStreamingMode(body.length);
                    try (OutputStream out = connection.getOutputStream()) {
                        writing = true;
                        out.write(body);
                    }
                }
                connection.getResponseCode();
                return connection;
            } catch (Exception error) {
                if (activeTracker != null) activeTracker.accept(null);
                connection.disconnect();
                if (!writing && network != null && NetHelper.isNetworkBindingDenied(error)) {
                    NetHelper.noteDirectNetworkBindingDenied(network);
                    network = null;
                    continue;
                }
                if (error instanceof IOException) throw (IOException) error;
                throw new IOException(error);
            }
        }
    }

    static String readBoundedUtf8(InputStream input) throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        byte[] buffer = new byte[1024];
        int count;
        while ((count = input.read(buffer)) != -1) {
            if (bytes.size() + count > 8192) throw new IOException("Response too large");
            bytes.write(buffer, 0, count);
        }
        return new String(bytes.toByteArray(), StandardCharsets.UTF_8);
    }

    public static void openSsh(Activity activity, ComputerDetails computer, boolean configure) {
        if (configure) {
            new TouchDialog(activity).setTitle(R.string.ssh_title)
                    .setMessage(R.string.ssh_computer_setup_hint)
                    .setPositiveButton(R.string.ssh_connect, (d, w) -> fetchDefaults(activity, computer))
                    .setNegativeButton(android.R.string.cancel, null).show();
        } else { fetchDefaults(activity, computer); }
    }
    private static void fetchDefaults(Activity activity, ComputerDetails computer) {
        if (computer == null || computer.activeAddress == null) {
            android.widget.Toast.makeText(activity, R.string.pair_pc_offline, android.widget.Toast.LENGTH_SHORT).show();
            return;
        }
        String address = computer.activeAddress.address;
        AlertDialog loading = new TouchDialog(activity).setTitle(R.string.ssh_title)
                .setMessage(R.string.ssh_reading_host).setNegativeButton(android.R.string.cancel, null).create();
        loading.show();
        new Thread(() -> {
            SshProfile result = null;
            boolean windows = false;
            HttpURLConnection connection = null;
            try {
                connection = openCompanionRequest(activity, address, "/v1/ssh", computer,
                        "GET", null, 7000, 10000, null);
                int responseCode = connection.getResponseCode();
                if (responseCode == 403) throw new IOException("电脑端未允许这部手机，或该地址不在受信任的局域网中");
                if (responseCode != 200) throw new IOException("电脑 SSH 授权服务不可用 (HTTP " + responseCode + ")");
                String responseText;
                try (InputStream input = connection.getInputStream()) {
                    responseText = readBoundedUtf8(input);
                }
                org.json.JSONObject data = new org.json.JSONObject(responseText);
                result = new SshProfile(address, data.getString("username"), data.getInt("port"));
                windows = "windows".equals(data.optString("platform"));
            } catch (Exception error) {
                com.limelight.LimeLog.warning("Unable to read SSH defaults: " + error.getClass().getSimpleName());
            } finally { if (connection != null) connection.disconnect(); }
            SshProfile profile = result; boolean isWindows = windows;
            activity.runOnUiThread(() -> {
                if (!loading.isShowing()) return;
                loading.dismiss();
                if (activity.isFinishing() || activity.isDestroyed()) return;
                if (profile == null) {
                    com.limelight.utils.ReminderPreferences.offerAction(activity, R.string.reminder_ssh,
                            R.string.ssh_title, R.string.ssh_host_update_needed, R.string.monitor_refresh,
                            () -> fetchDefaults(activity, computer));
                    return;
                }
                try { profile.replace(activity, null); }
                catch (IllegalStateException invalidStorage) {
                    com.limelight.utils.Dialog.displayTip(activity, R.string.reminder_ssh, R.string.ssh_title, R.string.ssh_storage_error, () -> {});
                    return;
                }
                activity.getSharedPreferences("computer_ssh", 0).edit()
                        .putString(computer.uuid + ":user", profile.user).putInt(computer.uuid + ":port", profile.port).apply();
                activity.startActivity(new Intent(activity, TerminalActivity.class)
                        .putExtra("host", profile.host).putExtra("port", profile.port).putExtra("user", profile.user)
                        .putExtra("windows_account", isWindows));
            });
        }, "PiuLink SSH defaults").start();
    }
}
