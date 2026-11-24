package com.limelight.computers;

import android.content.Context;
import android.os.SystemClock;

import com.limelight.LimeLog;
import com.limelight.binding.PlatformBinding;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.utils.NetHelper;

import java.io.EOFException;
import java.io.IOException;
import java.security.cert.X509Certificate;
import java.util.concurrent.Callable;
import java.util.concurrent.FutureTask;

/** The two authenticated, read-only requests needed to open the published desktop. */
final class DesktopLookup {
    private final String uuid;
    private final ComputerDetails.AddressTuple endpoint;
    private final int httpsPort;
    private final X509Certificate certificate;
    private static final long MAX_AGE_MS = 8000;
    private final long started = SystemClock.elapsedRealtime();
    private volatile long catalogReceived;
    private volatile long statusReceived;
    private volatile boolean failed;
    final FutureTask<String> apps;
    final FutureTask<Integer> status;

    DesktopLookup(Context context, ComputerDetails pc, String clientId) {
        uuid = pc.uuid;
        endpoint = new ComputerDetails.AddressTuple(pc.activeAddress.address, pc.activeAddress.port);
        httpsPort = pc.httpsPort;
        certificate = pc.serverCert;
        apps = request(context, clientId, "applist", http -> {
            String catalog = http.getAppListRaw();
            catalogReceived = SystemClock.elapsedRealtime();
            return catalog;
        });
        status = request(context, clientId, "serverinfo", http -> {
            int current = http.getPairedCurrentGame();
            statusReceived = SystemClock.elapsedRealtime();
            return current;
        });
    }

    boolean matches(ComputerDetails pc) {
        long now = SystemClock.elapsedRealtime();
        return !failed && uuid.equals(pc.uuid) && certificate.equals(pc.serverCert) && endpoint.equals(pc.activeAddress)
                && httpsPort == pc.httpsPort && recent(now, catalogReceived) && recent(now, statusReceived);
    }

    private boolean recent(long now, long received) {
        return now - (received == 0 ? started : received) <= MAX_AGE_MS;
    }

    private interface Read<T> { T get(NvHTTP http) throws Exception; }

    static boolean isTruncatedRead(IOException error) {
        for (Throwable cause = error; cause != null; cause = cause.getCause()) {
            if (cause instanceof javax.net.ssl.SSLException) return false;
            if (cause instanceof EOFException) return true;
        }
        return false;
    }

    private <T> T readOnce(Context context, String clientId, Read<T> read) throws Exception {
        NvHTTP http = new NvHTTP(endpoint, httpsPort, clientId, certificate, PlatformBinding.getCryptoProvider(context));
        boolean bound = NetHelper.bindToDirectLocalNetwork(context, http, endpoint.address);
        try {
            return read.get(http);
        } catch (IOException error) {
            if (!bound || !NetHelper.isNetworkBindingDenied(error)) throw error;
            return read.get(new NvHTTP(endpoint, httpsPort, clientId, certificate, PlatformBinding.getCryptoProvider(context)));
        }
    }

    private <T> FutureTask<T> request(Context context, String clientId, String name, Read<T> read) {
        Callable<T> action = () -> {
            long begin = SystemClock.elapsedRealtime();
            try {
                try {
                    return readOnce(context, clientId, read);
                } catch (IOException error) {
                    if (Thread.currentThread().isInterrupted() || !isTruncatedRead(error)) throw error;
                    return readOnce(context, clientId, read);
                }
            } catch (Exception error) {
                failed = true;
                throw error;
            } finally {
                LimeLog.info("Desktop lookup " + name + " finished in " + (SystemClock.elapsedRealtime() - begin) + " ms");
            }
        };
        return new FutureTask<>(action);
    }
}
