package com.limelight.piulink.transfer;

import android.content.Context;
import android.net.Network;
import android.os.Build;
import android.provider.Settings;
import org.json.JSONObject;
import com.limelight.LimeLog;
import com.limelight.computers.ComputerDatabaseManager;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.binding.PlatformBinding;
import com.limelight.utils.NetHelper;
import javax.net.ssl.HttpsURLConnection;
import java.io.*;
import java.net.*;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;

public final class FileTransferClient implements AutoCloseable {
    private static final ExecutorService pairingRefreshWorker = Executors.newSingleThreadExecutor();
    private static final java.util.concurrent.atomic.AtomicBoolean pairingRefreshRunning = new java.util.concurrent.atomic.AtomicBoolean();
    private static final ConcurrentHashMap<String, Object> pairingLocks = new ConcurrentHashMap<>();
    private static final ScheduledExecutorService watchdog = Executors.newSingleThreadScheduledExecutor(r -> {
        Thread thread = new Thread(r, "PiuLink transfer timeout"); thread.setDaemon(true); return thread;
    });
    private static volatile long lastPairingRefreshMs;
    private final Context context;
    private final String host;
    private final String deviceId;
    private volatile boolean cancelled;
    private volatile Socket activeSocket;
    private volatile HttpURLConnection activePairing;
    private String token;
    private ComputerDetails pairedComputer;
    private int port = 48012;

    public FileTransferClient(Context context, String host) {
        this.context = context.getApplicationContext(); this.host = host;
        this.deviceId = deviceId(context);
    }

    public static void bootstrap(Context context, String host) throws Exception {
        try (FileTransferClient client = new FileTransferClient(context, host)) { client.pair(); }
    }

    public static void bootstrap(Context context, String host, ComputerDetails computer) throws Exception {
        try (FileTransferClient client = new FileTransferClient(context, host)) { client.pair(computer); }
    }

    public static void refreshKnownPairings(Context context) {
        refreshKnownPairings(context, false);
    }

    public static void refreshKnownPairings(Context context, boolean networkChanged) {
        Context application = context.getApplicationContext();
        long now = android.os.SystemClock.elapsedRealtime();
        if ((!networkChanged && lastPairingRefreshMs != 0 && now - lastPairingRefreshMs < 15000) ||
                !pairingRefreshRunning.compareAndSet(false, true)) return;
        lastPairingRefreshMs = now;
        pairingRefreshWorker.execute(() -> {
            try {
                ComputerDatabaseManager database = new ComputerDatabaseManager(application);
                try {
                    java.util.List<ComputerDetails> computers = database.getAllComputers();
                    for (ComputerDetails computer : computers) {
                        if (computer.serverCert == null) continue;
                        try (TransferHostResolver resolver = new TransferHostResolver()) {
                            String host = resolver.resolve(application, computer);
                            try (FileTransferClient client = new FileTransferClient(application, host)) {
                                // The paired streaming certificate can restore a missing or
                                // rotated transfer token without another approval gesture.
                                client.pair(false, tokenForComputer(application, computer), computer);
                            }
                        } catch (Exception error) {
                            LimeLog.errEvent("transfer.android.pair_refresh_failed", "file-transfer", "file receiver address refresh failed", error, null, LimeLog.fields("host_identity_known", true));
                        }
                    }
                } finally { database.close(); }
            } catch (Exception error) {
                LimeLog.errEvent("transfer.android.pair_refresh_failed", "file-transfer", "file receiver address refresh failed", error, null, null);
            } finally { pairingRefreshRunning.set(false); }
        });
    }

    private static String tokenForComputer(Context context, ComputerDetails computer) {
        if (computer.uuid == null) return null;
        return context.getSharedPreferences("file_transfer", 0).getString("host:" + computer.uuid + ":token", null);
    }

    private static boolean matchesAddress(ComputerDetails computer, String sourceAddress) {
        String normalizedSource = NetHelper.stripIpv6Zone(sourceAddress);
        ComputerDetails.AddressTuple[] addresses = { computer.activeAddress, computer.manualAddress,
                computer.localAddress, computer.remoteAddress, computer.ipv6Address };
        for (ComputerDetails.AddressTuple address : addresses) {
            if (address != null && normalizedSource.equalsIgnoreCase(NetHelper.stripIpv6Zone(address.address))) return true;
        }
        return false;
    }

    private static String fingerprint(String value) {
        try {
            byte[] digest = MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8));
            StringBuilder result = new StringBuilder(16);
            for (int i = 0; i < 8; i++) result.append(String.format(java.util.Locale.US, "%02x", digest[i] & 0xff));
            return result.toString();
        } catch (Exception error) { return "unavailable"; }
    }

    static String deviceId(Context context) {
        String androidId = Settings.Secure.getString(context.getContentResolver(), Settings.Secure.ANDROID_ID);
        if (androidId != null && !androidId.isEmpty()) return "android-" + androidId.replaceAll("[^A-Za-z0-9_-]", "");
        synchronized (FileTransferClient.class) {
            android.content.SharedPreferences saved = context.getSharedPreferences("file_transfer_identity", 0);
            String id = saved.getString("id", null);
            if (id == null) {
                id = "android-" + java.util.UUID.randomUUID();
                if (!saved.edit().putString("id", id).commit()) throw new IllegalStateException("无法保存设备标识");
            }
            return id;
        }
    }

    public void pair() throws Exception {
        for (ComputerDetails computer : FileTransferTls.pairedComputers(context)) {
            if (matchesAddress(computer, host)) { pair(computer); return; }
        }
        throw new IOException("请先在 PiuLink 中完成电脑的串流配对");
    }
    public void pair(ComputerDetails computer) throws Exception {
        pair(true, tokenForComputer(context, computer), computer);
    }

    /** Invoked by an explicit host open or share. Passive discovery never requests approval. */
    public void prepareForSending(ComputerDetails computer, Runnable waitingForApproval) throws Exception {
        String saved = tokenForComputer(context, computer);
        try {
            pair(false, saved, computer);
            FileReceiveService.start(context);
        } catch (PairingRejectedException rejected) {
            if (!"pairing_denied".equals(rejected.reason)) throw rejected;
            waitingForApproval.run();
            pair(true, saved, computer);
        }
    }
    static String recoverReceiveToken(Context context, String address, ComputerDetails sender) throws Exception {
        try (FileTransferClient client = new FileTransferClient(context, address)) {
            client.pair(false, tokenForComputer(context, sender), sender);
            return client.token;
        }
    }
    private void pair(boolean allowApproval, String knownToken, ComputerDetails computer) throws Exception {
        final String operationId = java.util.UUID.randomUUID().toString().replace("-", "").substring(0, 12);
        final long started = android.os.SystemClock.elapsedRealtime();
        LimeLog.infoEvent("transfer.android.pair_started", "file-transfer", "file transfer pairing started", operationId, LimeLog.fields("interactive", allowApproval));
        synchronized (pairingLocks.computeIfAbsent(host, ignored -> new Object())) {
            ensureOpen();
            token = knownToken;
            if (computer == null || computer.uuid == null || computer.serverCert == null) throw new IOException("请先完成电脑的串流配对");
            LimeLog.infoEvent("transfer.android.pair_attempt", "file-transfer", "checking saved transfer authorization " +
                            fingerprint(deviceId) + "/" + (token == null ? "none" : fingerprint(token)), operationId,
                    LimeLog.fields("device_fingerprint", fingerprint(deviceId), "token_fingerprint", token == null ? "none" : fingerprint(token)));
            String cleanHost = NetHelper.stripIpv6Zone(host);
            String address = NetHelper.formatAuthorityHost(cleanHost);
            URL url = new URL("https://" + address + ":48011/v1/transfer/pair");
            Network network = NetHelper.getBindableLocalNetworkForAddress(context, cleanHost);
            for (;;) {
                HttpsURLConnection connection = (HttpsURLConnection)(network == null ? url.openConnection() : network.openConnection(url));
                activePairing = connection;
                boolean writing = false;
                try {
                    ensureOpen();
                    new NvHTTP(new ComputerDetails.AddressTuple(cleanHost, 48011), 0, "",
                            computer.serverCert, PlatformBinding.getCryptoProvider(context))
                            .configurePairedCompanion(connection);
                    connection.setInstanceFollowRedirects(false);
                    connection.setConnectTimeout(7000); connection.setReadTimeout(allowApproval ? 120000 : 10000);
                    connection.setRequestMethod("POST"); connection.setDoOutput(true);
                    connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                    JSONObject json = new JSONObject().put("deviceId", deviceId).put("deviceName", Build.MANUFACTURER + " " + Build.MODEL)
                            .put("refreshOnly", !allowApproval).put("transport", "tls-v2");
                    if (token != null) json.put("token", token);
                    byte[] bytes = json.toString().getBytes(StandardCharsets.UTF_8); connection.setFixedLengthStreamingMode(bytes.length);
                    try (OutputStream output = connection.getOutputStream()) { writing = true; output.write(bytes); }
                    int code = connection.getResponseCode();
                    if (code == 403) {
                        String reason = "unknown";
                        try (InputStream error = connection.getErrorStream()) {
                            if (error != null) {
                                byte[] body = new byte[512];
                                int count = error.read(body);
                                if (count > 0) reason = new JSONObject(new String(body, 0, count, StandardCharsets.UTF_8))
                                        .optString("error", "unknown");
                            }
                        } catch (Exception ignored) { }
                        throw new PairingRejectedException(reason);
                    }
                    if (code != 200) throw new IOException("电脑文件传输服务不可用 (HTTP " + code + ")");
                    ByteArrayOutputStream response = new ByteArrayOutputStream();
                    try (InputStream input = connection.getInputStream()) {
                        byte[] buffer = new byte[1024]; int count;
                        while ((count = input.read(buffer)) != -1) { if (response.size()+count > 8192) throw new IOException("响应过大"); response.write(buffer,0,count); }
                    }
                    JSONObject result = new JSONObject(response.toString("UTF-8"));
                    if (!"tls-v2".equals(result.optString("transport")))
                        throw new IOException("请更新电脑上的 PiuLink Host，以使用加密文件传输");
                    pairedComputer = computer;
                    String issued = result.getString("token"); int receivedPort = result.getInt("port");
                    int tokenBytes = issued.getBytes(StandardCharsets.UTF_8).length;
                    if (tokenBytes < 32 || tokenBytes > 128 || receivedPort < 1 || receivedPort > 65535) throw new IOException("电脑返回了无效的传输参数");
                    ensureOpen();
                    if (!context.getSharedPreferences("file_transfer", 0).edit()
                            .putString("host:" + computer.uuid + ":token", issued).remove(host + ":token").remove(host + ":port").commit()) throw new IOException("无法保存电脑授权");
                    token = issued; port = receivedPort;
                    LimeLog.infoEvent("transfer.android.pair_completed", "file-transfer", "file transfer pairing completed", operationId, LimeLog.fields("port", receivedPort, "elapsed_ms", android.os.SystemClock.elapsedRealtime() - started));
                    // A refresh is called BY this service. Do not recursively start it again.
                    if (allowApproval) FileReceiveService.start(context);
                    break;
                } catch (Exception error) {
                    if (writing || cancelled || network == null || !NetHelper.isNetworkBindingDenied(error)) throw error;
                    NetHelper.noteDirectNetworkBindingDenied(network);
                    network = null;
                } finally { activePairing = null; connection.disconnect(); }
            }
        }
    }

    private static final class PairingRejectedException extends IOException {
        final String reason;
        PairingRejectedException(String reason) {
            super("电脑拒绝了文件传输请求（" + reason + "），请在电脑上确认文件授权");
            this.reason = reason;
        }
    }

    public void send(String name, long size, InputStream input) throws Exception {
        send(name, size, input, null);
    }

    public void send(String name, long size, InputStream input, java.util.function.LongConsumer fileProgress) throws Exception {
        final String operationId = java.util.UUID.randomUUID().toString().replace("-", "").substring(0, 12);
        final long started = android.os.SystemClock.elapsedRealtime();
        ensureOpen(); TransferIo.validateSize(size);
        LimeLog.infoEvent("transfer.android.send_started", "file-transfer", "outgoing file transfer started", operationId, LimeLog.fields("size_bytes", size, "name_length", name == null ? 0 : name.length()));
        if (pairedComputer == null || token == null || port < 1 || port > 65535) throw new IOException("请先完成文件传输配对");
        byte[] device = deviceId.getBytes(StandardCharsets.UTF_8), secret = token.getBytes(StandardCharsets.UTF_8), filename = name.getBytes(StandardCharsets.UTF_8);
        if (filename.length < 1 || filename.length > 512 || device.length < 8 || device.length > 128 || secret.length < 32 || secret.length > 128)
            throw new IOException("文件名或传输授权无效");
        String cleanHost = NetHelper.stripIpv6Zone(host);
        Network lan = NetHelper.getBindableLocalNetworkForAddress(context, cleanHost);
        Socket transport = new Socket();
        try {
            LimeLog.debugEvent("transfer.android.connecting", "file-transfer", "connecting to file receiver", operationId, LimeLog.fields("port", port));
            try {
                connectTransport(transport, lan, cleanHost);
            } catch (IOException | SecurityException denied) {
                if (lan == null || cancelled || (!(denied instanceof SecurityException) &&
                        !NetHelper.isNetworkBindingDenied(denied))) throw denied;
                NetHelper.noteDirectNetworkBindingDenied(lan);
                try { transport.close(); } catch (IOException ignored) { }
                transport = new Socket();
                connectTransport(transport, null, cleanHost);
            }
            try (Socket socket = FileTransferTls.handshake(context, transport,
                    java.util.Collections.singletonList(pairedComputer), true)) {
            LimeLog.infoEvent("transfer.android.connected", "file-transfer", "connected to file receiver", operationId, LimeLog.fields("port", port));
            socket.setSoTimeout(180000); socket.setTcpNoDelay(true); socket.setKeepAlive(true);
            AtomicLong progress = new AtomicLong(System.nanoTime());
            java.util.concurrent.atomic.AtomicBoolean timedOut = new java.util.concurrent.atomic.AtomicBoolean();
            ScheduledFuture<?> guard = watchdog.scheduleAtFixedRate(() -> {
                if (System.nanoTime() - progress.get() > TimeUnit.MINUTES.toNanos(2)) {
                    timedOut.set(true);
                    try { socket.close(); } catch (IOException ignored) { }
                }
            }, 1, 1, TimeUnit.SECONDS);
            try {
                OutputStream output = new BufferedOutputStream(socket.getOutputStream(), 65536);
                ByteBuffer header = ByteBuffer.allocate(22).order(ByteOrder.BIG_ENDIAN);
                header.put("PIULINK1".getBytes(StandardCharsets.US_ASCII)).putShort((short)device.length)
                        .putShort((short)secret.length).putShort((short)filename.length).putLong(size);
                output.write(header.array()); output.write(device); output.write(secret); output.write(filename);
                final long[] lastReport = {0};
                TransferIo.copyExactly(input, output, size, copied -> {
                    long now = System.nanoTime();
                    progress.set(now);
                    if (fileProgress != null && copied < size && now - lastReport[0] >= TimeUnit.MILLISECONDS.toNanos(250)) {
                        lastReport[0] = now;
                        fileProgress.accept(copied);
                    }
                });
                output.flush();
                if (fileProgress != null) fileProgress.accept(size);
                LimeLog.infoEvent("transfer.android.payload_sent", "file-transfer", "file payload sent; waiting for acknowledgement", operationId, LimeLog.fields("size_bytes", size));
                // Saving ACK has its own 3-minute read deadline, not the payload idle deadline.
                guard.cancel(false);
                if (socket.getInputStream().read() != 1) throw new IOException("电脑未返回成功保存确认，请检查 Downloads/PiuLink 后再重试");
                LimeLog.infoEvent("transfer.android.send_completed", "file-transfer", "outgoing file transfer acknowledged", operationId, LimeLog.fields("size_bytes", size, "elapsed_ms", android.os.SystemClock.elapsedRealtime() - started));
            } catch (IOException error) {
                LimeLog.errEvent("transfer.android.send_failed", "file-transfer", "outgoing file transfer failed", error, operationId, LimeLog.fields("size_bytes", size, "timed_out", timedOut.get(), "elapsed_ms", android.os.SystemClock.elapsedRealtime() - started));
                if (timedOut.get()) throw new IOException("传输连续 2 分钟没有进展，连接已中止", error);
                throw error;
            } finally { guard.cancel(false); }
            }
        } finally {
            activeSocket = null;
            try { transport.close(); } catch (IOException ignored) { }
        }
    }

    private void connectTransport(Socket transport, Network network, String cleanHost) throws IOException {
        activeSocket = transport;
        ensureOpen();
        if (network != null) network.bindSocket(transport);
        transport.connect(new InetSocketAddress(cleanHost, port), 10000);
        transport.setSoTimeout(10000);
    }

    private void ensureOpen() throws InterruptedIOException {
        if (cancelled || Thread.currentThread().isInterrupted()) throw new InterruptedIOException("传输已取消");
    }
    @Override public void close() {
        cancelled = true;
        Socket socket = activeSocket;
        if (socket != null) try { socket.close(); } catch (IOException ignored) { }
        HttpURLConnection connection = activePairing;
        if (connection != null) connection.disconnect();
    }
}
