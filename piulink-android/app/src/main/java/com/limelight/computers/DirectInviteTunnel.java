package com.limelight.computers;

import android.content.Context;
import android.os.SystemClock;
import android.util.Base64;
import com.limelight.LimeLog;
import com.limelight.binding.PlatformBinding;
import com.limelight.nvstream.http.ComputerDetails.AddressTuple;
import com.limelight.nvstream.http.LimelightCryptoProvider;
import com.limelight.nvstream.http.NvHTTP;
import org.json.JSONObject;
import org.json.JSONException;
import mobile.Mobile;
import mobile.Session;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.InputStream;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import java.util.concurrent.TimeUnit;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.Signature;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.security.spec.MGF1ParameterSpec;
import java.util.Arrays;
import java.util.HashMap;
import java.util.UUID;
import javax.crypto.Cipher;
import javax.crypto.spec.OAEPParameterSpec;
import javax.crypto.spec.PSource;

public final class DirectInviteTunnel {
    private static final HashMap<String,Session> sessions = new HashMap<>();
    private static final java.util.concurrent.ConcurrentHashMap<String,AddressTuple> activeAddresses = new java.util.concurrent.ConcurrentHashMap<>();
    static AddressTuple preferred(String uuid) {
        return uuid == null ? null : activeAddresses.get(uuid.toLowerCase(java.util.Locale.ROOT));
    }
    public static boolean isEndpoint(String address) {
        if (address == null || !address.startsWith("127.")) return false;
        for (AddressTuple endpoint : activeAddresses.values()) if (address.equals(endpoint.address)) return true;
        return false;
    }
    private static final SecureRandom random = new SecureRandom();
    private static final OkHttpClient signaling = new OkHttpClient.Builder()
            .followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false)
            .connectTimeout(4, TimeUnit.SECONDS).readTimeout(7, TimeUnit.SECONDS)
            .callTimeout(9, TimeUnit.SECONDS).build();

    private static JSONObject post(String route, String room, JSONObject body) throws IOException, JSONException {
        byte[] data = body == null ? new byte[0] : body.toString().getBytes(StandardCharsets.UTF_8);
        Request request = new Request.Builder().url(HostInvite.BASE + "v2/" + route)
                .header("Authorization", "Bearer " + room)
                .post(RequestBody.create(data, MediaType.get("application/json"))).build();
        for (int attempt = 1; ; attempt++) {
            try {
                return postOnce(route, request);
            } catch (IOException failure) {
                if (!HostInvite.isTransient(failure)) throw failure;
                if (attempt == 3) throw new IOException("P2P " + route + " unavailable after 3 attempts", failure);
                LimeLog.info("PiuLink P2P retrying " + route + " after a network interruption");
            }
        }
    }

    private static JSONObject postOnce(String route, Request request) throws IOException, JSONException {
        try (Response result = signaling.newCall(request).execute()) {
            int status = result.code();
            if (status == 404) throw new FileNotFoundException("P2P host offline");
            if (status >= 502 && status <= 504) throw new HostInvite.ServiceUnavailable(status);
            if (status != 200) throw new IOException("P2P " + route + " HTTP " + status);
            if (result.body() == null) throw new IOException("P2P " + route + " returned an empty reply");
            try (InputStream in = result.body().byteStream()) {
                ByteArrayOutputStream response = new ByteArrayOutputStream();
                byte[] chunk = new byte[1024];
                int count;
                while ((count = in.read(chunk)) != -1) {
                    if (response.size() + count > 8192) throw new IOException("P2P reply too large");
                    response.write(chunk, 0, count);
                }
                return new JSONObject(response.toString("UTF-8"));
            }
        }
    }

    private static String encode(byte[] bytes) { return Base64.encodeToString(bytes, Base64.NO_WRAP); }

    static synchronized AddressTuple connect(Context ctx, HostInvite invite, X509Certificate pinned)
            throws IOException, InterruptedException {
        byte[] key = new byte[32];
        long start = SystemClock.elapsedRealtime();
        try {
            byte[] tag = MessageDigest.getInstance("SHA-256").digest(invite.uuid.getBytes(StandardCharsets.US_ASCII));
            String listener = "127." + (1 + (tag[0] & 0xff) % 254) + "." + (tag[1] & 0xff) + "." + (1 + (tag[2] & 0xff) % 254);
            AddressTuple address = new AddressTuple(listener, NvHTTP.DEFAULT_HTTP_PORT);
            Session existing = sessions.get(invite.uuid);
            if (existing != null && existing.alive()) return address;
            sessions.entrySet().removeIf(e -> {
                if (e.getValue().alive()) return false;
                activeAddresses.remove(e.getKey());
                e.getValue().stop();
                return true;
            });
            if (sessions.size() >= 4) throw new IOException("Too many active P2P hosts");
            LimeLog.info("PiuLink P2P starting at " + start);
            // Only the holder of this exact Host key can decrypt and authenticate
            // the session. A paired device already has the trusted certificate.
            X509Certificate cert = pinned;
            if (cert == null) {
                JSONObject identity = post("identity", invite.room, null);
                byte[] peer = Base64.decode(identity.getString("cert"), Base64.DEFAULT);
                cert = (X509Certificate)CertificateFactory.getInstance("X.509")
                        .generateCertificate(new ByteArrayInputStream(peer));
            }

            random.nextBytes(key);
            Cipher cipher = Cipher.getInstance("RSA/ECB/OAEPPadding");
            cipher.init(Cipher.ENCRYPT_MODE, cert.getPublicKey(),
                    new OAEPParameterSpec("SHA-256","MGF1",MGF1ParameterSpec.SHA256,PSource.PSpecified.DEFAULT));
            String wrapped = encode(cipher.doFinal(key));
            String id = UUID.randomUUID().toString().replace("-", "");
            long time = System.currentTimeMillis() / 1000;
            LimelightCryptoProvider local = PlatformBinding.getCryptoProvider(ctx);
            Signature signature = Signature.getInstance("SHA256withRSA");
            signature.initSign(local.getClientPrivateKey());
            signature.update(("piulink-punch-v2\n" + invite.room + "\n" + id + "\n" + time + "\n" + wrapped)
                    .getBytes(StandardCharsets.US_ASCII));
            JSONObject offer = new JSONObject().put("id", id).put("time", time)
                    .put("cert", encode(local.getClientCertificate().getEncoded()))
                    .put("wrapped", wrapped).put("signature", encode(signature.sign()));
            String ticket = post("open", invite.room, offer).getString("ticket");
            JSONObject lease = new JSONObject().put("leaseId", id).put("ticket", ticket)
                    .put("transportKey", Base64.encodeToString(key, Base64.URL_SAFE | Base64.NO_PADDING | Base64.NO_WRAP))
                    .put("sourceClientId", "source").put("hostClientId", "host");
            if (Thread.currentThread().isInterrupted()) throw new InterruptedException();
            Session session = Mobile.start(new JSONObject().put("role","source").put("clientId","source")
                    .put("listenHost",listener).put("lease",lease).toString());
            if (Thread.currentThread().isInterrupted()) { session.stop(); throw new InterruptedException(); }
            sessions.put(invite.uuid,session);
            activeAddresses.put(invite.uuid,address);
            monitor(invite.uuid, session);
            LimeLog.info("PiuLink P2P ready in " + (SystemClock.elapsedRealtime()-start) + " ms, selected peer " + session.peerAddress());
            return address;
        } catch (InterruptedException cancelled) { throw cancelled; }
        catch (Exception failure) {
            LimeLog.warning("PiuLink P2P failed: " + failure.getMessage());
            if (failure instanceof IOException) throw (IOException)failure;
            throw new IOException("Could not establish a direct P2P session", failure);
        } finally { Arrays.fill(key,(byte)0); }
    }

    static synchronized void close(String uuid) {
        if (uuid == null) return;
        String id = uuid.toLowerCase(java.util.Locale.ROOT);
        activeAddresses.remove(id);
        Session session = sessions.remove(id);
        if (session != null) session.stop();
    }

    private static void monitor(String id, Session session) {
        Thread worker = new Thread(() -> {
            long lastMedia = SystemClock.elapsedRealtime(), previousBytes = 0;
            try {
                while (session.alive()) {
                    Thread.sleep(10000);
                    JSONObject stats = new JSONObject(session.statsJSON());
                    long bytes = stats.optLong("UDPBytesSent") + stats.optLong("UDPBytesRecv");
                    if (bytes != previousBytes) {
                        lastMedia = SystemClock.elapsedRealtime();
                        previousBytes = bytes;
                        LimeLog.info("PiuLink P2P media bytes=" + bytes + " dropped=" + stats.optLong("DroppedDatagrams"));
                    } else if (SystemClock.elapsedRealtime() - lastMedia >= 180000) break;
                }
            } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
            catch (JSONException ignored) { }
            finally { synchronized (DirectInviteTunnel.class) { if (sessions.get(id) == session) close(id); } }
        }, "PiuLink P2P session");
        worker.setDaemon(true);
        worker.start();
    }
}
