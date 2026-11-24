package com.limelight.piulink.transfer;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.net.Uri;
import android.os.*;
import android.provider.MediaStore;
import android.webkit.MimeTypeMap;
import com.limelight.R;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.concurrent.*;
import com.limelight.LimeLog;

/** Receives files from previously approved computers without involving SSH. */
public final class FileReceiveService extends Service {
    private static final String CHANNEL = "piulink-files";
    private volatile ServerSocket listener;
    private final Object lifecycle = new Object();
    private volatile boolean stopping;
    private final Handler main = new Handler(Looper.getMainLooper());
    private android.net.ConnectivityManager connectivity;
    private android.net.ConnectivityManager.NetworkCallback networkCallback;
    private final Runnable refreshAddress = () -> FileTransferClient.refreshKnownPairings(this, true);
    private android.net.wifi.WifiManager.WifiLock wifiLock;
    private final ExecutorService workers = Executors.newFixedThreadPool(4);
    private final ScheduledExecutorService deadlines = Executors.newSingleThreadScheduledExecutor();
    private final java.util.Set<Socket> clients = ConcurrentHashMap.newKeySet();
    public static void start(Context context) {
        // A paired streaming identity is enough to request a transfer token.
        // Start the receiver on the first launch after pairing or loss of its cache.
        if (context.getSharedPreferences("file_transfer", 0).getAll().isEmpty() &&
                FileTransferTls.pairedComputers(context).isEmpty()) return;
        try {
            Intent service = new Intent(context, FileReceiveService.class);
            if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(service); else context.startService(service);
        }
        catch (RuntimeException e) { LimeLog.errEvent("transfer.android.receiver_start_failed", "file-transfer", "file receiver service start failed", e, null, null); }
    }
    private Notification notification(String text) {
        Notification.Builder builder = Build.VERSION.SDK_INT >= 26 ? new Notification.Builder(this, CHANNEL) : new Notification.Builder(this);
        builder.setSmallIcon(android.R.drawable.stat_sys_download_done).setContentTitle("PiuLink 文件传输").setContentText(text);
        Intent launch = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (launch != null) builder.setContentIntent(PendingIntent.getActivity(this, 48012, launch,
                Build.VERSION.SDK_INT >= 23 ? PendingIntent.FLAG_IMMUTABLE : 0));
        return builder.build();
    }
    @Override public void onCreate() {
        super.onCreate();
        if (Build.VERSION.SDK_INT >= 26)
            getSystemService(NotificationManager.class).createNotificationChannel(new NotificationChannel(CHANNEL, "文件传输", NotificationManager.IMPORTANCE_LOW));
        if (Build.VERSION.SDK_INT >= 29) startForeground(48012, notification("已准备好接收电脑文件"), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
        else startForeground(48012, notification("已准备好接收电脑文件"));
        android.net.wifi.WifiManager wifi = (android.net.wifi.WifiManager)getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        if (wifi != null) {
            wifiLock = wifi.createWifiLock(android.net.wifi.WifiManager.WIFI_MODE_FULL_HIGH_PERF, "PiuLink:FileReceiver");
            wifiLock.setReferenceCounted(false);
            // Acquire only for authorized transfers, not for the lifetime of the service.
        }
        connectivity = (android.net.ConnectivityManager)getSystemService(Context.CONNECTIVITY_SERVICE);
        if (connectivity != null) {
            networkCallback = new android.net.ConnectivityManager.NetworkCallback() {
                private void changed() {
                    synchronized (lifecycle) {
                        if (stopping) return;
                        main.removeCallbacks(refreshAddress);
                        main.postDelayed(refreshAddress, 2000); // Coalesce route changes, then publish the new LAN/Tailscale address.
                    }
                }
                @Override public void onAvailable(android.net.Network network) { changed(); }
                @Override public void onLost(android.net.Network network) { changed(); }
                @Override public void onLinkPropertiesChanged(android.net.Network network, android.net.LinkProperties properties) { changed(); }
            };
            try {
                connectivity.registerNetworkCallback(new android.net.NetworkRequest.Builder()
                        .addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)
                        .removeCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), networkCallback);
            } catch (RuntimeException error) { networkCallback = null; LimeLog.errEvent("transfer.android.network_callback_failed", "file-transfer", "network refresh callback unavailable", error, null, null); }
        }
        new Thread(() -> {
            try (ServerSocket server = new ServerSocket()) {
                synchronized (lifecycle) {
                    if (stopping) return;
                    server.setReuseAddress(true); server.bind(new InetSocketAddress(48012)); listener = server;
                }
                while (!stopping) {
                    Socket socket = server.accept();
                    synchronized (lifecycle) {
                        if (stopping || clients.size() >= 2) { socket.close(); continue; }
                        clients.add(socket);
                        try { workers.execute(() -> receive(socket)); }
                        catch (RejectedExecutionException error) { clients.remove(socket); socket.close(); }
                    }
                }
            } catch (IOException error) {
                if (!stopping) {
                    LimeLog.errEvent("transfer.android.listener_failed", "file-transfer", "file receiver cannot listen or accept", error, null, LimeLog.fields("port", 48012));
                    getSystemService(NotificationManager.class).notify(48013, notification("文件接收服务未就绪，请重新打开 PiuLink，并检查 TCP 48012 端口占用。"));
                    stopSelf();
                }
            }
        }, "PiuLink file receiver").start();
    }
    @Override public int onStartCommand(Intent intent, int flags, int id) {
        // Re-pairing with the existing token is silent and updates the Host's saved peer
        // address. This is required after Wi-Fi, DHCP, or Tailscale changes.
        FileTransferClient.refreshKnownPairings(this);
        return START_STICKY;
    }
    @Override public IBinder onBind(Intent intent) { return null; }

    private static String mimeType(String name) {
        int dot = name.lastIndexOf('.');
        String type = dot < 0 ? null : MimeTypeMap.getSingleton().getMimeTypeFromExtension(
                name.substring(dot + 1).toLowerCase(java.util.Locale.ROOT));
        return type == null ? "application/octet-stream" : type;
    }

    @SuppressWarnings("deprecation")
    private void notifySaved(Uri uri, File legacyFile, String name, String type, boolean acknowledged) {
        if (legacyFile != null) {
            name = legacyFile.getName();
            if (legacyFile.length() == 0) {
                Notification.Builder builder = Build.VERSION.SDK_INT >= 26 ?
                        new Notification.Builder(this, CHANNEL) : new Notification.Builder(this);
                builder.setSmallIcon(android.R.drawable.stat_sys_download_done).setContentTitle(name)
                        .setContentText(getString(acknowledged ? R.string.received_file_open : R.string.received_file_ack_lost))
                        .setOnlyAlertOnce(true).setAutoCancel(true);
                getSystemService(NotificationManager.class).notify(legacyFile.getAbsolutePath(), 48013, builder.build());
                return;
            }
            DownloadManager manager = (DownloadManager) getSystemService(DOWNLOAD_SERVICE);
            long id = manager.addCompletedDownload(name, name, false, type, legacyFile.getAbsolutePath(),
                    legacyFile.length(), false);
            uri = manager.getUriForDownloadedFile(id);
        }
        if (uri == null) throw new IllegalStateException("Missing committed download URI");
        Intent open = new Intent(this, OpenReceivedFileActivity.class).setDataAndType(uri, type);
        PendingIntent action = PendingIntent.getActivity(this, 0, open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Notification.Builder builder = Build.VERSION.SDK_INT >= 26 ?
                new Notification.Builder(this, CHANNEL) : new Notification.Builder(this);
        builder.setSmallIcon(android.R.drawable.stat_sys_download_done).setContentTitle(name)
                .setContentText(getString(acknowledged ? R.string.received_file_open : R.string.received_file_ack_lost))
                .setContentIntent(action).setOnlyAlertOnce(true).setAutoCancel(true);
        getSystemService(NotificationManager.class).notify(uri.toString(), 48013, builder.build());
    }

    private void receive(Socket socket) {
        Uri pending = null; File temporary = null;
        Uri savedUri = null;
        File savedLegacyFile = null;
        String savedName = null;
        String savedType = null;
        boolean acknowledged = false;
        PowerManager.WakeLock transferWake = null;
        boolean authorized = false;
        boolean committed = false;
        String stage = "header";
        String operationId = java.util.UUID.randomUUID().toString().replace("-", "").substring(0, 12);
        long started = android.os.SystemClock.elapsedRealtime();
        long expectedBytes = -1L;
        LimeLog.infoEvent("transfer.android.receive_started", "file-transfer", "incoming file transfer started", operationId, null);
        Socket connection = socket;
        ScheduledFuture<?> headerDeadline = null;
        try {
            headerDeadline = deadlines.schedule(() -> { try { socket.close(); } catch (IOException ignored) { } }, 10, TimeUnit.SECONDS);
            // Tailscale routes and phones waking from doze can pause an otherwise healthy
            // transfer. Keep reads bounded, but don't abort a valid payload after 30 seconds.
            connection.setKeepAlive(true);
            connection.setTcpNoDelay(true);
            connection.setSoTimeout(10000); // Short unauthenticated handshake timeout.
            final long headerStarted = SystemClock.elapsedRealtime();
            java.util.List<com.limelight.nvstream.http.ComputerDetails> computers = FileTransferTls.pairedComputers(this);
            javax.net.ssl.SSLSocket tls = FileTransferTls.handshake(this, socket, computers, false);
            connection = tls;
            com.limelight.nvstream.http.ComputerDetails sender = FileTransferTls.peer(computers,
                    (java.security.cert.X509Certificate)tls.getSession().getPeerCertificates()[0]);
            DataInputStream input = new DataInputStream(new BufferedInputStream(connection.getInputStream()));
            byte[] magic = new byte[8]; input.readFully(magic);
            if (!java.util.Arrays.equals(magic, "PIULINK1".getBytes(StandardCharsets.US_ASCII))) throw new IOException("Invalid protocol");
            int deviceLength = input.readUnsignedShort(), tokenLength = input.readUnsignedShort(), nameLength = input.readUnsignedShort();
            long size = input.readLong();
            expectedBytes = size;
            if (deviceLength < 8 || deviceLength > 128 || tokenLength < 32 || tokenLength > 128 || nameLength < 1 || nameLength > 512 || size < 0 || size > 2147483648L) throw new IOException("Invalid header");
            String device = readText(input, deviceLength), token = readText(input, tokenLength), name = readText(input, nameLength);
            if (!device.equals(FileTransferClient.deviceId(this))) throw new IOException("Wrong device");
            if (SystemClock.elapsedRealtime() - headerStarted > 10000) throw new IOException("Handshake timeout");
            headerDeadline.cancel(false);
            String savedToken = getSharedPreferences("file_transfer", 0).getString("host:" + sender.uuid + ":token", null);
            byte[] wireToken = token.getBytes(StandardCharsets.UTF_8);
            if (savedToken == null || !MessageDigest.isEqual(wireToken, savedToken.getBytes(StandardCharsets.UTF_8))) {
                // The TLS peer has already matched this paired host. Refresh via
                // its pinned companion endpoint and still require the wire token to match.
                savedToken = FileTransferClient.recoverReceiveToken(
                        this, com.limelight.utils.NetHelper.stripIpv6Zone(socket.getInetAddress().getHostAddress()), sender);
            }
            if (savedToken == null || !MessageDigest.isEqual(wireToken,
                    savedToken.getBytes(StandardCharsets.UTF_8))) throw new IOException("电脑的文件传输授权不匹配");
            stage = "authorized"; LimeLog.infoEvent("transfer.android.authorized", "file-transfer",
                    "incoming file transfer authorized", operationId, LimeLog.fields("size_bytes", size));
            authorized = true;
            connection.setSoTimeout(120000);
            name = TransferIo.safeName(name);
            savedName = name;
            savedType = mimeType(name);
            transferWake = ((PowerManager)getSystemService(Context.POWER_SERVICE))
                    .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PiuLink:FileReceive");
            transferWake.setReferenceCounted(false);
            transferWake.acquire(10 * 60 * 1000L);
            synchronized (lifecycle) { if (stopping) throw new IOException("Receiver stopped"); if (wifiLock != null) wifiLock.acquire(); }
            OutputStream output;
            File destination = null;
            if (Build.VERSION.SDK_INT >= 29) {
                ContentValues values = new ContentValues();
                values.put(MediaStore.Downloads.DISPLAY_NAME, name); values.put(MediaStore.Downloads.MIME_TYPE, savedType);
                values.put(MediaStore.Downloads.RELATIVE_PATH, "Download/PiuLink"); values.put(MediaStore.Downloads.IS_PENDING, 1);
                stage = "insert"; LimeLog.debugEvent("transfer.android.storage_insert", "file-transfer", "creating pending MediaStore item", operationId, null);
                pending = getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
                if (pending == null) throw new IOException("Cannot create download");
                stage = "open"; LimeLog.debugEvent("transfer.android.storage_open", "file-transfer", "opening destination stream", operationId, null);
                output = getContentResolver().openOutputStream(pending, "w");
            } else {
                File folder = new File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "PiuLink");
                if (!folder.isDirectory() && !folder.mkdirs()) throw new IOException("Cannot create download folder");
                destination = new File(folder, name); // The actual unique destination is selected at commit time.
                temporary = File.createTempFile(".piulink-", ".tmp", folder); output = new FileOutputStream(temporary);
            }
            if (output == null) throw new IOException("Cannot open download");
            stage = "copy"; LimeLog.infoEvent("transfer.android.payload_started", "file-transfer", "receiving file payload", operationId, LimeLog.fields("size_bytes", size));
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            try (OutputStream saved = new java.security.DigestOutputStream(output, digest)) {
                PowerManager.WakeLock wake = transferWake;
                final long payloadStarted = SystemClock.elapsedRealtime();
                final long[] renewed = { payloadStarted };
                TransferIo.copyExactly(input, saved, size, copied -> {
                    long now = SystemClock.elapsedRealtime();
                    if (now - payloadStarted > 60 * 60 * 1000L) throw new TransferDeadlineException();
                    if (now - renewed[0] > 5 * 60 * 1000L) { wake.acquire(10 * 60 * 1000L); renewed[0] = now; }
                });
                saved.flush();
            }
            byte[] hash = digest.digest();
            synchronized (commitLock) {
                if (Build.VERSION.SDK_INT >= 29) {
                    stage = "publish";
                    savedUri = findPublished(name, size, hash);
                    if (savedUri != null) {
                        if (getContentResolver().delete(pending, null, null) != 1) throw new IOException("Cannot discard duplicate download");
                    } else {
                        ContentValues complete = new ContentValues();
                        complete.put(MediaStore.Downloads.IS_PENDING, 0);
                        if (getContentResolver().update(pending, complete, null, null) != 1) throw new IOException("Cannot finish download");
                        savedUri = pending;
                    }
                    pending = null;
                } else {
                    savedLegacyFile = commitLegacy(temporary, destination, hash);
                    temporary = null;
                }
            }
            committed = true;
            stage = "ack"; LimeLog.infoEvent("transfer.android.committed", "file-transfer", "file committed locally; sending acknowledgement", operationId, LimeLog.fields("size_bytes", size));
            OutputStream acknowledgement = connection.getOutputStream();
            acknowledgement.write(1);
            acknowledgement.flush();
            acknowledged = true;
            LimeLog.infoEvent("transfer.android.receive_completed", "file-transfer", "file transfer completed and acknowledged", operationId, LimeLog.fields("size_bytes", size, "elapsed_ms", android.os.SystemClock.elapsedRealtime() - started));
        } catch (Exception e) {
            LimeLog.errEvent("transfer.android.receive_failed", "file-transfer", "file receive failed", e, operationId, LimeLog.fields("stage", stage, "expected_bytes", expectedBytes, "committed", committed, "elapsed_ms", android.os.SystemClock.elapsedRealtime() - started));
            if (authorized && !committed) {
                // Once committed, never claim a storage failure. A lost ACK means an unknown outcome to the sender.
                try { connection.getOutputStream().write(0); connection.getOutputStream().flush(); } catch (IOException ignored) { }
                getSystemService(NotificationManager.class).notify(48013, notification("文件接收失败（" + stage + "），请检查空间、权限和网络后重试。"));
            }
        }
        finally {
            if (headerDeadline != null) headerDeadline.cancel(false);
            if (connection != socket) try { connection.close(); } catch (IOException ignored) { }
            try { socket.close(); } catch (IOException ignored) { }
            synchronized (lifecycle) {
                clients.remove(socket);
                if (clients.isEmpty() && wifiLock != null && wifiLock.isHeld()) wifiLock.release();
            }
            if (transferWake != null && transferWake.isHeld()) transferWake.release();
            try {
                if (pending != null) getContentResolver().delete(pending, null, null);
                if (temporary != null && temporary.exists()) temporary.delete();
            } catch (RuntimeException cleanupError) { LimeLog.errEvent("transfer.android.cleanup_failed", "file-transfer", "incomplete download cleanup failed", cleanupError, operationId, null); }
            if (committed) {
                try { notifySaved(savedUri, savedLegacyFile, savedName, savedType, acknowledged); }
                catch (RuntimeException error) { LimeLog.errEvent("transfer.android.notification_failed", "file-transfer", "could not post saved file notification", error, operationId, null); }
            }
        }
    }
    private static String readText(DataInputStream input, int length) throws IOException {
        byte[] bytes = new byte[length]; input.readFully(bytes); return new String(bytes, StandardCharsets.UTF_8);
    }
    private static final Object commitLock = new Object();

    private static byte[] hashFile(InputStream source) throws IOException {
        try (InputStream stream = source) {
            if (stream == null) throw new FileNotFoundException();
            MessageDigest digest;
            try { digest = MessageDigest.getInstance("SHA-256"); }
            catch (java.security.NoSuchAlgorithmException e) { throw new AssertionError(e); }
            byte[] bytes = new byte[65536];
            int count;
            while ((count = stream.read(bytes)) != -1) digest.update(bytes, 0, count);
            return digest.digest();
        }
    }

    @android.annotation.TargetApi(29)
    private Uri findPublished(String name, long size, byte[] hash) throws IOException {
        Uri downloads = MediaStore.Downloads.EXTERNAL_CONTENT_URI;
        String[] columns = {MediaStore.Downloads._ID, MediaStore.Downloads.DISPLAY_NAME};
        int dot = name.lastIndexOf('.');
        String stem = dot > 0 ? name.substring(0, dot) : name;
        String ext = dot > 0 ? name.substring(dot) : "";
        java.util.regex.Pattern numbered = java.util.regex.Pattern.compile(java.util.regex.Pattern.quote(stem)
                + " \\(\\d+\\)" + java.util.regex.Pattern.quote(ext));
        try (android.database.Cursor rows = getContentResolver().query(downloads, columns,
                "relative_path=? AND is_pending=0 AND _size=?", new String[]{"Download/PiuLink/", Long.toString(size)}, null)) {
            while (rows != null && rows.moveToNext()) {
                String candidate = rows.getString(1);
                if (!name.equals(candidate) && (candidate == null || !numbered.matcher(candidate).matches())) continue;
                Uri item = ContentUris.withAppendedId(downloads, rows.getLong(0));
                try {
                    if (MessageDigest.isEqual(hash, hashFile(getContentResolver().openInputStream(item)))) return item;
                } catch (FileNotFoundException ignored) { /* Deleted after the query. */ }
            }
        }
        return null;
    }

    private static File commitLegacy(File temporary, File wanted, byte[] hash) throws IOException {
        for (int index = 0; index < 10000; index++) {
            File destination = index == 0 ? wanted : new File(wanted.getParentFile(), index + "-" + wanted.getName());
            if (destination.isFile() && destination.length() == temporary.length() &&
                    MessageDigest.isEqual(hash, hashFile(new FileInputStream(destination)))) {
                if (!temporary.delete()) throw new IOException("Cannot discard duplicate download");
                return destination;
            }
            if (Build.VERSION.SDK_INT >= 26) {
                try { java.nio.file.Files.move(temporary.toPath(), destination.toPath()); return destination; }
                catch (java.nio.file.FileAlreadyExistsException collision) { continue; }
            } else {
                // Legacy root flavor: synchronized prevents collisions between this service's receive workers.
                // Android <26 has no NIO no-replace move; external-app races remain unsupported.
                if (destination.exists()) continue;
                if (temporary.renameTo(destination)) return destination;
                throw new IOException("Cannot finish download");
            }
        }
        throw new IOException("Too many files with this name");
    }
    private static final class TransferDeadlineException extends RuntimeException {
        TransferDeadlineException() { super("Transfer exceeded one hour"); }
    }
    @Override public void onDestroy() {
        synchronized (lifecycle) {
            stopping = true;
            main.removeCallbacks(refreshAddress);
            try { if (listener != null) listener.close(); } catch (IOException ignored) { }
            for (Socket socket : clients) try { socket.close(); } catch (IOException ignored) { }
            workers.shutdownNow();
            deadlines.shutdownNow();
            if (wifiLock != null && wifiLock.isHeld()) wifiLock.release();
        }
        if (networkCallback != null && connectivity != null)
            try { connectivity.unregisterNetworkCallback(networkCallback); } catch (RuntimeException ignored) { }
        super.onDestroy();
    }
}
