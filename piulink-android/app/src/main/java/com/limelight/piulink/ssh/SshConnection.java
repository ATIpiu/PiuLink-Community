package com.limelight.piulink.ssh;

import android.content.SharedPreferences;
import android.util.Base64;

import com.jcraft.jsch.ChannelExec;
import com.jcraft.jsch.ChannelSftp;
import com.jcraft.jsch.HostKey;
import com.jcraft.jsch.HostKeyRepository;
import com.jcraft.jsch.JSch;
import com.jcraft.jsch.Session;

import com.jcraft.jsch.UserInfo;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.concurrent.atomic.AtomicBoolean;

/** Blocking OpenSSH transport. All methods except cancel() run off the Android UI thread. */
public final class SshConnection implements TerminalTransport {
    public interface TrustPrompt { boolean accept(String endpoint, String fingerprint); }

    private volatile Session session;
    private volatile ChannelExec terminal;
    private volatile OutputStream input;
    private final AtomicBoolean cancelled = new AtomicBoolean();
    private volatile int columns = 80, rows = 24;

    public void connect(SshProfile profile, byte[] privateKey,
                        SharedPreferences trusted, TrustPrompt prompt) throws Exception {
        JSch jsch = new JSch();
        jsch.setHostKeyRepository(new HostKeyRepository() {
            @Override public int check(String host, byte[] key) {
                try {
                    String encoded = Base64.encodeToString(key, Base64.NO_WRAP);
                    String saved = trusted.getString(host, null);
                    if (saved != null) return saved.equals(encoded) ? OK : CHANGED;
                    String fingerprint = "SHA256:" + Base64.encodeToString(
                            MessageDigest.getInstance("SHA-256").digest(key),
                            Base64.NO_WRAP | Base64.NO_PADDING);
                    if (!cancelled.get() && prompt.accept(host, fingerprint) && !cancelled.get() &&
                            trusted.edit().putString(host, encoded).commit()) return OK;
                    return NOT_INCLUDED;
                } catch (Exception e) {
                    return NOT_INCLUDED;
                }
            }
            @Override public void add(HostKey key, UserInfo info) { }
            @Override public void remove(String host, String type) { }
            @Override public void remove(String host, String type, byte[] key) { }
            @Override public String getKnownHostsRepositoryID() { return "PiuLink verified SSH hosts"; }
            @Override public HostKey[] getHostKey() { return new HostKey[0]; }
            @Override public HostKey[] getHostKey(String host, String type) { return new HostKey[0]; }
        });

        if (privateKey == null) throw new IOException("Device SSH key required");
        jsch.addIdentity("piulink-device", privateKey, null, null);
        Session ssh = jsch.getSession(profile.user, profile.host, profile.port);
        session = ssh;
        ssh.setConfig("StrictHostKeyChecking", "yes");
        ssh.setConfig("PreferredAuthentications", "publickey");
        ssh.setServerAliveInterval(15000);
        ssh.setServerAliveCountMax(3);
        try {
            if (cancelled.get()) throw new IOException("Connection cancelled");
            ssh.connect(15000);
            if (cancelled.get()) throw new IOException("Connection cancelled");
        } catch (Exception e) {
            ssh.disconnect();
            throw e;
        } finally {
            jsch.removeAllIdentity();
        }
    }

    public InputStream openShell(boolean windows) throws Exception {
        detach();
        if (cancelled.get()) throw new IOException("Connection cancelled");
        ChannelExec channel = (ChannelExec) session.openChannel("exec");
        terminal = channel;
        channel.setCommand(PersistentShell.command(windows));
        channel.setPty(true);
        channel.setPtyType("xterm-256color", columns, rows, 0, 0);
        try {
            InputStream output = channel.getInputStream();
            OutputStream destination = channel.getOutputStream();
            channel.connect(10000);
            if (cancelled.get()) throw new IOException("Connection cancelled");
            channel.setPtySize(columns, rows, 0, 0);
            input = destination;
            return output;
        } catch (Exception e) {
            if (terminal == channel) {
                terminal = null;
                input = null;
            }
            channel.disconnect();
            throw e;
        }
    }

    /** Opens a binary-safe SFTP channel on the verified SSH session. */
    public ChannelSftp openSftp() throws Exception {
        if (cancelled.get() || session == null || !session.isConnected())
            throw new IOException("SSH session is not connected");
        ChannelSftp channel = (ChannelSftp) session.openChannel("sftp");
        channel.connect(10000);
        if (cancelled.get()) {
            channel.disconnect();
            throw new IOException("Connection cancelled");
        }
        return channel;
    }

    public OutputStream captureInput() { return input; }

    public void send(OutputStream out, String data) throws IOException {
        if (out != null && !cancelled.get()) {
            out.write(data.getBytes(StandardCharsets.UTF_8));
            out.flush();
        }
    }

    public void resize(int cols, int lines) {
        columns = Math.max(2, Math.min(500, cols));
        rows = Math.max(2, Math.min(300, lines));
        ChannelExec channel = terminal;
        if (channel != null && channel.isConnected()) channel.setPtySize(columns, rows, 0, 0);
    }

    public void detach() {
        input = null;
        ChannelExec old = terminal;
        terminal = null;
        if (old != null) old.disconnect();
    }

    public void cancel() { cancelled.set(true); }

    @Override public void close() {
        cancel();
        detach();
        Session old = session;
        if (old != null) old.disconnect();
    }

}
