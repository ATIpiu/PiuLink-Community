package com.limelight.piulink.ssh;

import android.content.SharedPreferences;
import java.io.*;

interface TerminalTransport extends AutoCloseable {
    void connect(SshProfile profile, byte[] key, SharedPreferences trusted, SshConnection.TrustPrompt prompt) throws Exception;
    InputStream openShell(boolean windows) throws Exception;
    OutputStream captureInput();
    void send(OutputStream out, String data) throws IOException;
    void resize(int columns, int rows);
    default boolean sessionEnded() { return false; }
    void cancel();
    void close();
}
