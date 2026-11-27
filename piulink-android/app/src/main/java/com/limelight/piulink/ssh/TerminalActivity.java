package com.limelight.piulink.ssh;

import com.limelight.utils.TouchDialog;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.AlertDialog;
import android.os.Bundle;
import android.util.Base64;
import android.view.WindowManager;
import android.webkit.*;
import android.widget.*;
import com.limelight.R;
import java.io.*;
import java.util.*;
import java.util.concurrent.*;

public final class TerminalActivity extends Activity {
    private final ExecutorService work = Executors.newSingleThreadExecutor();
    private final BoundedTaskQueue writes = new BoundedTaskQueue(32);
    private WebView web;
    private TextView status;
    private SshProfile profile;
    private volatile TerminalTransport connection;
    private volatile java.net.HttpURLConnection activeHttp;
    private final TerminalOutputGate outputGate = new TerminalOutputGate();
    private volatile ArrayBlockingQueue<Boolean> trustAnswer;
    private AlertDialog dialog;

    private int generation;
    private volatile int terminalGeneration;
    private boolean ready, active;
    private boolean authorizationAttempted;
    private int terminalColumns = 80, terminalRows = 24;

    @SuppressLint("SetJavaScriptEnabled")
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        profile = new SshProfile(getIntent().getStringExtra("host"), getIntent().getStringExtra("user"), getIntent().getIntExtra("port", 22));
        LinearLayout root = new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL); root.setFitsSystemWindows(true);
        LinearLayout toolbar = new LinearLayout(this);
        button(toolbar, getString(R.string.ssh_leave), this::finish);
        button(toolbar, getString(R.string.ssh_reconnect), this::login);
        button(toolbar, "修复连接", () -> { authorizationAttempted = false; repairAuthorization(); });
        root.addView(toolbar);
        status = new TextView(this); status.setPadding(12, 8, 12, 8); status.setText(profile.label()); root.addView(status);
        web = new WebView(this);
        web.setBackgroundColor(0xff101112);
        WebSettings settings = web.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setAllowFileAccess(false); settings.setAllowContentAccess(false);
        settings.setBlockNetworkLoads(true); settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setSaveFormData(false);
        web.addJavascriptInterface(new Bridge(), "PiuTerminal");
        web.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) { return true; }
            @Override public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
                String path = request.getUrl().getPath();
                try {
                    if (!"https".equals(request.getUrl().getScheme()) || !"terminal.piulink.invalid".equals(request.getUrl().getHost()) ||
                            path == null || !path.matches("/terminal/[A-Za-z0-9_./-]+") || path.contains(".."))
                        return new WebResourceResponse("text/plain", "UTF-8", new ByteArrayInputStream(new byte[0]));
                    String mime = path.endsWith(".js") ? "application/javascript" : path.endsWith(".css") ? "text/css" : "text/html";
                    return new WebResourceResponse(mime, "UTF-8", getAssets().open(path.substring(1)));
                } catch (IOException e) { return new WebResourceResponse("text/plain", "UTF-8", new ByteArrayInputStream(new byte[0])); }
            }
        });
        root.addView(web, new LinearLayout.LayoutParams(-1, 0, 1));
        HorizontalScrollView keys = new HorizontalScrollView(this);
        LinearLayout rail = new LinearLayout(this);
        String[] labels = {"Esc", "Tab", "Ctrl-C", "Ctrl-D", "Ctrl-B", "↑", "↓", "←", "→"};
        String[] codes = {"\u001b", "\t", "\u0003", "\u0004", "\u0002", "\u001b[A", "\u001b[B", "\u001b[D", "\u001b[C"};
        for (int i = 0; i < labels.length; i++) { final String code = codes[i]; button(rail, labels[i], () -> send(code)); }
        keys.addView(rail); root.addView(keys); setContentView(root);
        web.loadUrl("https://terminal.piulink.invalid/terminal/index.html");
    }
    private void button(LinearLayout parent, String label, Runnable action) {
        Button button = new Button(this); button.setText(label); button.setMinWidth(0);
        button.setOnClickListener(v -> action.run()); parent.addView(button);
    }
    @Override protected void onStart() {
        super.onStart(); active = true;
        if (ready) web.post(this::login);
    }
    @Override protected void onStop() {
        active = false; disconnect();
        if (dialog != null) dialog.dismiss();
        status.setText(R.string.ssh_detached);
        super.onStop();
    }
    @Override protected void onDestroy() {
        disconnect(); work.shutdownNow(); writes.shutdownNow();

        web.removeJavascriptInterface("PiuTerminal"); web.destroy(); super.onDestroy();
    }
    private void setActiveHttp(java.net.HttpURLConnection http) {
        activeHttp = http;
    }

    private void disconnect() {
        writes.clearPending();
        generation++; terminalGeneration++;
        outputGate.reset(terminalGeneration);
        ArrayBlockingQueue<Boolean> pending = trustAnswer;
        if (pending != null) pending.offer(false);
        java.net.HttpURLConnection http = activeHttp; activeHttp = null;
        if (http != null) new Thread(http::disconnect, "ssh-http-cancel").start();
        TerminalTransport old = connection; connection = null;
        if (old != null) { old.cancel(); new Thread(old::close, "ssh-detach").start(); }
    }
    private void later(int token, Runnable action) {
        runOnUiThread(() -> { if (active && !isFinishing() && generation == token) action.run(); });
    }
    private void login() {
        if (!ready || !active || isFinishing()) return;
        disconnect();
        if (dialog != null) dialog.dismiss();
        final int token = generation;
        status.setText("正在使用设备密钥连接…");
        work.execute(() -> {
            try (DeviceSshIdentity identity = DeviceSshIdentity.load(this)) {
                // Profiles created from the terminal tab predate platform metadata.
                // Resolve the Host platform before choosing the remote shell command.
                boolean windows = getSharedPreferences("ssh_platform", 0).getBoolean(profile.host, getIntent().getBooleanExtra("windows_account", false));
                java.net.HttpURLConnection probe = null;
                try {
                    probe = ComputerSsh.openCompanionRequest(this, profile.host, "/v1/ssh", null,
                            "GET", null, 2000, 2000, this::setActiveHttp);
                    if (probe.getResponseCode() == 200) {
                        try (InputStream in = probe.getInputStream()) {
                            String bodyText = ComputerSsh.readBoundedUtf8(in);
                            windows = "windows".equals(new org.json.JSONObject(bodyText).optString("platform"));
                            getSharedPreferences("ssh_platform", 0).edit().putBoolean(profile.host, windows).apply();
                        }
                    }
                } catch (Exception ignored) { }
                finally { activeHttp = null; if (probe != null) probe.disconnect(); }
                final boolean hostWindows = windows;
                byte[] key = identity.privateKey.clone();
                runOnUiThread(() -> {
                    if (!active || isFinishing() || generation != token) { Arrays.fill(key, (byte) 0); return; }
                    getIntent().putExtra("windows_account", hostWindows);
                    connect(key);
                });
            } catch (Exception e) { later(token, () -> showError(e)); }
        });
    }
    private void repairAuthorization() {
        disconnect();
        final int token = generation;
        authorizationAttempted = true;
        status.setText("正在修复电脑连接，请在电脑上确认此设备…");
        work.execute(() -> {
            java.net.HttpURLConnection request = null;
            try (DeviceSshIdentity identity = DeviceSshIdentity.load(this)) {
                byte[] body = new org.json.JSONObject().put("publicKey", identity.publicKey)
                    .put("deviceName", android.os.Build.MANUFACTURER + " " + android.os.Build.MODEL).toString().getBytes(java.nio.charset.StandardCharsets.UTF_8);
                request = ComputerSsh.openCompanionRequest(this, profile.host, "/v1/ssh/authorize", null,
                        "POST", body, 7000, 120000, this::setActiveHttp);
                int code = request.getResponseCode();
                if (code != 200) throw new IOException(code == 403 ? "电脑未允许连接，请确认后重试。" : "电脑授权服务暂不可用 (HTTP " + code + ")");
                String responseText;
                try (InputStream in = request.getInputStream()) {
                    responseText = ComputerSsh.readBoundedUtf8(in);
                }
                org.json.JSONObject result = new org.json.JSONObject(responseText);
                String user = result.getString("username"); int port = result.getInt("port");
                boolean windows = "windows".equals(result.optString("platform"));
                later(token, () -> {
                    SshProfile previous = profile;
                    profile = new SshProfile(profile.host, user, port);
                    try {
                        profile.replace(this, previous);
                    } catch (IllegalStateException error) {
                        status.setText(R.string.ssh_storage_error);
                        return;
                    }
                    getIntent().putExtra("windows_account", windows);
                    getSharedPreferences("ssh_platform", 0).edit().putBoolean(profile.host, windows).apply();
                    login();
                });
            } catch (Exception error) { later(token, () -> status.setText("连接修复未完成：" + error.getMessage() + "。点击修复连接重试。")); }
            finally { activeHttp = null; if (request != null) request.disconnect(); }
        });
    }
    private void connect(byte[] key) {
        final int token = generation;
        TerminalTransport ssh;
        ssh = new SshConnection();
        connection = ssh;
        // Cache WebView dimensions even when its first resize preceded login.
        ssh.resize(terminalColumns, terminalRows);
        status.setText(R.string.ssh_connecting);
        work.execute(() -> {
            try {
                ssh.connect(profile, key, getSharedPreferences("ssh_trusted_hosts", 0), (endpoint, fingerprint) -> {
                    ArrayBlockingQueue<Boolean> answer = new ArrayBlockingQueue<>(1); trustAnswer = answer;
                    if (connection != ssh) answer.offer(false);
                    later(token, () -> {
                        dialog = new TouchDialog(this).setTitle(R.string.ssh_trust_title)
                            .setMessage(endpoint + "\n\n" + fingerprint + "\n\n" + getString(R.string.ssh_trust_detail))
                            .setPositiveButton(R.string.ssh_trust, (d, w) -> answer.offer(true))
                            .setNegativeButton(android.R.string.cancel, (d, w) -> answer.offer(false))
                            .setOnCancelListener(d -> answer.offer(false)).show();
                    });
                    try { return Boolean.TRUE.equals(answer.poll(90, TimeUnit.SECONDS)); }
                    catch (Exception e) { return false; }
                    finally { trustAnswer = null; }
                });
                openShell(ssh, token);
            } catch (Exception e) {
                ssh.close();
                later(token, () -> { connection = null; showError(e); });
            } finally { if (key != null) Arrays.fill(key, (byte) 0); }
        });
    }
    private void showError(Exception e) {
        StringBuilder diagnostic = new StringBuilder();
        Throwable cause = e;
        while (cause != null) {
            if (cause.getMessage() != null) diagnostic.append(' ').append(cause.getMessage());
            cause = cause.getCause();
        }
        String message = diagnostic.toString().toLowerCase(Locale.ROOT);
        int explanation = message.contains("auth fail") || message.contains("authentication") ? R.string.ssh_auth_failed :
                message.contains("unknownhost") || message.contains("unknown host") ? R.string.ssh_unknown_host :
                message.contains("timeout") || message.contains("timed out") ? R.string.ssh_timeout :
                message.contains("refused") ? R.string.ssh_refused :
                message.contains("hostkey") || message.contains("host key") ? R.string.ssh_host_key_failed :
                R.string.ssh_failed;
        if (explanation == R.string.ssh_auth_failed && !authorizationAttempted) { repairAuthorization(); return; }
        status.setText(getString(explanation) + "\n" + e.getClass().getSimpleName() + ": " + e.getMessage());
    }
    private void openShell(TerminalTransport ssh, int token) throws Exception {
        final int streamToken = ++terminalGeneration;
        outputGate.reset(streamToken);
        if (terminalGeneration != streamToken || connection != ssh) return;
        InputStream stream = ssh.openShell(getIntent().getBooleanExtra("windows_account", getSharedPreferences("ssh_platform", 0).getBoolean(profile.host, false)));
        later(token, () -> {
            if (terminalGeneration == streamToken) {
                web.evaluateJavascript("resetTerminal()", null);
                status.setText(profile.label() + " · " + getString(R.string.ssh_connected));
            }
        });
        new Thread(() -> {
            try (InputStream output = stream) {
                byte[] buffer = new byte[8192]; int count;
                while (terminalGeneration == streamToken && (count = output.read(buffer)) != -1) {
                    String encoded = Base64.encodeToString(buffer, 0, count, Base64.NO_WRAP);
                    TerminalOutputGate.Frame frame = outputGate.begin(streamToken);
                    if (frame == null) break;
                    later(token, () -> {
                        if (terminalGeneration == streamToken) web.evaluateJavascript("receive('" + encoded + "'," + frame.id + ")", null);
                        else outputGate.acknowledge(frame.id);
                    });
                    if (!frame.rendered.await(10, TimeUnit.SECONDS)) throw new IOException("Terminal rendering timed out");
                }
                later(token, () -> { if (terminalGeneration == streamToken) {
                    if (ssh.sessionEnded()) status.setText("PowerShell 会话已结束。点击重连将打开新会话。");
                    else status.setText(R.string.ssh_detached);
                }});
            } catch (Exception e) {
                later(token, () -> { if (terminalGeneration == streamToken) showError(e); });
            }
            finally {
                ssh.close();
                later(token, () -> { if (connection == ssh) connection = null; });
            }
        }, "ssh-output").start();
    }
    private void send(String data) {
        TerminalTransport ssh = connection; int token = generation, streamToken = terminalGeneration;
        if (!active || ssh == null || data == null) return;
        if (data.length() > 65536) { status.setText(R.string.ssh_input_too_large); return; }
        OutputStream destination = ssh.captureInput();
        if (destination == null) return;
        if (!writes.submit(() -> {
            if (terminalGeneration != streamToken) return;
            try { ssh.send(destination, data); } catch (IOException e) { later(token, () -> {
                if (terminalGeneration == streamToken) showError(e);
            }); }
        })) status.setText(R.string.ssh_input_busy);
    }
    private final class Bridge {
        @JavascriptInterface public void ready() { runOnUiThread(() -> { ready = true; login(); }); }
        @JavascriptInterface public void input(String data) { runOnUiThread(() -> send(data)); }
        @JavascriptInterface public void drained(int frameId) { outputGate.acknowledge(frameId); }
        @JavascriptInterface public void resize(int cols, int rows) {
            runOnUiThread(() -> {
                terminalColumns = Math.max(2, Math.min(500, cols));
                terminalRows = Math.max(2, Math.min(300, rows));
                TerminalTransport ssh = connection;
                int streamToken = terminalGeneration;
                int width = terminalColumns, height = terminalRows;
                if (active && ssh != null) writes.submit(() -> {
                    if (connection == ssh && terminalGeneration == streamToken) ssh.resize(width, height);
                });
            });
        }
    }
}
