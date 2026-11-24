package com.limelight.piulink.transfer;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.os.SystemClock;
import com.limelight.binding.PlatformBinding;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.utils.NetHelper;
import javax.net.ssl.HttpsURLConnection;
import java.io.IOException;
import java.io.InterruptedIOException;
import java.net.URL;
import java.util.LinkedHashMap;

/** Select a reachable saved address, not whichever localAddress happens to be non-null. */
public final class TransferHostResolver implements AutoCloseable {
    private static final class VerifiedRoute {
        final java.security.cert.X509Certificate certificate;
        final Network network;
        final ComputerDetails.AddressTuple address;
        final long time = SystemClock.elapsedRealtime();

        VerifiedRoute(ComputerDetails computer, Network network, ComputerDetails.AddressTuple address) {
            certificate = computer.serverCert;
            this.network = network;
            this.address = new ComputerDetails.AddressTuple(address.address, address.port);
        }
    }
    // The receiver also refreshes other paired hosts in the background.
    private static final android.util.LruCache<String, VerifiedRoute> recentRoutes = new android.util.LruCache<>(32);
    private volatile boolean closed;
    private volatile HttpsURLConnection active;

    public String resolve(Context context, ComputerDetails computer) throws IOException {
        if (computer.serverCert == null) throw new IOException("请先在 PiuLink 主页完成这台电脑的串流配对，再分享文件");
        LinkedHashMap<String, ComputerDetails.AddressTuple> candidates = new LinkedHashMap<>();
        // The library just authenticated this address. The saved database record lacks its live activeAddress.
        Network networkAtStart = context.getSystemService(ConnectivityManager.class).getActiveNetwork();
        VerifiedRoute previous = computer.uuid != null ? recentRoutes.get(computer.uuid) : null;
        if (previous != null && computer.serverCert.equals(previous.certificate) && networkAtStart != null
                && networkAtStart.equals(previous.network) && SystemClock.elapsedRealtime() - previous.time < 120000)
            add(candidates, previous.address);
        add(candidates, computer.activeAddress); add(candidates, computer.manualAddress);
        add(candidates, computer.localAddress); add(candidates, computer.remoteAddress); add(candidates, computer.ipv6Address);
        IOException last = null;
        for (ComputerDetails.AddressTuple candidate : candidates.values()) {
            ensureOpen();
            String cleanHost = NetHelper.stripIpv6Zone(candidate.address);
            String authority = NetHelper.formatAuthorityHost(cleanHost);
            URL url = new URL("https://" + authority + ":48011/v1/ssh");
            Network network = NetHelper.getBindableLocalNetworkForAddress(context, cleanHost);
            for (;;) {
                HttpsURLConnection connection = null;
                try {
                    connection = (HttpsURLConnection)(network == null ? url.openConnection() : network.openConnection(url));
                    active = connection; ensureOpen();
                    new NvHTTP(new ComputerDetails.AddressTuple(cleanHost, candidate.port), 0, "",
                            computer.serverCert, PlatformBinding.getCryptoProvider(context))
                            .configurePairedCompanion(connection);
                    connection.setInstanceFollowRedirects(false);
                    connection.setConnectTimeout(3000); connection.setReadTimeout(3000);
                    // The GET does not grant SSH access. A successful TLS handshake pins this host's identity.
                    if (connection.getResponseCode() == 200) {
                        if (computer.uuid != null && networkAtStart != null)
                            recentRoutes.put(computer.uuid, new VerifiedRoute(computer, networkAtStart, candidate));
                        return cleanHost;
                    }
                    last = new IOException("Host 辅助服务未就绪");
                    break;
                } catch (IOException error) {
                    if (!closed && network != null && NetHelper.isNetworkBindingDenied(error)) {
                        NetHelper.noteDirectNetworkBindingDenied(network);
                        network = null;
                        continue;
                    }
                    last = error;
                    break;
                } finally { active = null; if (connection != null) connection.disconnect(); }
            }
        }
        ensureOpen();
        throw new IOException("无法通过已保存地址连接这台电脑。请确认 Host 在线、Tailscale 已连接，或在主页刷新地址。", last);
    }
    private static void add(LinkedHashMap<String, ComputerDetails.AddressTuple> candidates, ComputerDetails.AddressTuple address) {
        if (address != null && address.address != null && !address.address.trim().isEmpty()) candidates.putIfAbsent(address.address, address);
    }
    private void ensureOpen() throws InterruptedIOException {
        if (closed || Thread.currentThread().isInterrupted()) throw new InterruptedIOException("连接已取消");
    }
    @Override public void close() { closed = true; HttpsURLConnection connection = active; if (connection != null) connection.disconnect(); }
}
