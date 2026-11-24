package com.limelight.piulink.ssh;

import android.app.Activity;
import android.os.Bundle;
import android.util.Base64;

import com.jcraft.jsch.ChannelSftp;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.io.FileOutputStream;
import java.util.concurrent.Executors;

/** Debug-build-only end-to-end transfer probe. Never included in release APKs. */
public final class TransferProbeActivity extends Activity {
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        Executors.newSingleThreadExecutor().execute(() -> runProbe());
    }

    private void runProbe() {
        File result = new File(getFilesDir(), "transfer-probe-result.txt");
        try (SshConnection ssh = new SshConnection()) {
            String host = required("host");
            String user = required("user");
            String marker = required("marker");
            byte[] key = Base64.decode(required("privateKey"), Base64.NO_WRAP);
            ssh.connect(new SshProfile(host, user, 22), key,
                    getSharedPreferences("transfer_probe_hosts", 0), (endpoint, fingerprint) -> true);
            ChannelSftp sftp = ssh.openSftp();
            try {
                try { sftp.cd("Downloads"); } catch (Exception e) { sftp.mkdir("Downloads"); sftp.cd("Downloads"); }
                try { sftp.cd("PiuLink"); } catch (Exception e) { sftp.mkdir("PiuLink"); sftp.cd("PiuLink"); }
                sftp.put(new ByteArrayInputStream(marker.getBytes(StandardCharsets.UTF_8)),
                        "piulink-e2e-test.txt", ChannelSftp.OVERWRITE);
            } finally { sftp.disconnect(); }
            writeResult(result, "PASS");
        } catch (Exception e) {
            try { writeResult(result, "FAIL: " + e); }
            catch (Exception ignored) { }
        } finally { finish(); }
    }

    private static void writeResult(File file, String value) throws Exception {
        try (FileOutputStream output = new FileOutputStream(file, false)) {
            output.write(value.getBytes(StandardCharsets.UTF_8));
        }
    }

    private String required(String name) {
        String value = getIntent().getStringExtra(name);
        if (value == null || value.isEmpty()) throw new IllegalArgumentException(name + " is required");
        return value;
    }
}
