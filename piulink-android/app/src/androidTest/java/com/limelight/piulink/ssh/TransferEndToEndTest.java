package com.limelight.piulink.ssh;

import android.content.Context;
import android.util.Base64;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;

import com.jcraft.jsch.ChannelSftp;

import org.junit.Test;
import org.junit.runner.RunWith;

import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;

import static org.junit.Assert.assertFalse;

/** Opt-in device-to-PC transfer test. Credentials are supplied only as runner arguments. */
@RunWith(AndroidJUnit4.class)
public final class TransferEndToEndTest {
    @Test public void uploadsToWindowsInbox() throws Exception {
        android.os.Bundle args = InstrumentationRegistry.getArguments();
        String host = args.getString("host");
        String user = args.getString("user");
        String privateKey = args.getString("privateKey");
        String marker = args.getString("marker");
        assertFalse("host argument required", host == null || host.isEmpty());
        assertFalse("user argument required", user == null || user.isEmpty());
        assertFalse("privateKey argument required", privateKey == null || privateKey.isEmpty());
        assertFalse("marker argument required", marker == null || marker.isEmpty());

        Context context = InstrumentationRegistry.getInstrumentation().getTargetContext();
        try (SshConnection ssh = new SshConnection()) {
            ssh.connect(new SshProfile(host, user, 22), Base64.decode(privateKey, Base64.NO_WRAP),
                    context.getSharedPreferences("transfer_e2e_hosts", 0), (endpoint, fingerprint) -> true);
            ChannelSftp sftp = ssh.openSftp();
            try {
                try { sftp.cd("Downloads"); } catch (Exception e) { sftp.mkdir("Downloads"); sftp.cd("Downloads"); }
                try { sftp.cd("PiuLink"); } catch (Exception e) { sftp.mkdir("PiuLink"); sftp.cd("PiuLink"); }
                byte[] body = marker.getBytes(StandardCharsets.UTF_8);
                sftp.put(new ByteArrayInputStream(body), "piulink-e2e-test.txt", ChannelSftp.OVERWRITE);
            } finally { sftp.disconnect(); }
        }
    }
}
