package com.limelight.piulink;

import java.io.IOException;
import java.net.Socket;
import java.security.KeyStore;
import java.security.PrivateKey;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.List;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/** Both endpoints present their streaming identity and pin the paired peer. */
public final class PairedSocketTls {
    private PairedSocketTls() {}

    public static SSLSocket handshake(Socket socket, Collection<X509Certificate> trusted, boolean client,
                                      X509Certificate identity, PrivateKey privateKey) throws Exception {
        if (trusted.isEmpty()) throw new IOException("请先完成串流配对");
        KeyStore store = KeyStore.getInstance(KeyStore.getDefaultType());
        store.load(null, null);
        char[] password = new char[0];
        store.setKeyEntry("piulink", privateKey, password, new X509Certificate[] {identity});
        KeyManagerFactory km = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        km.init(store, password);
        X509TrustManager pins = new X509TrustManager() {
            private void check(X509Certificate[] chain) throws CertificateException {
                if (chain == null || chain.length == 0) throw new CertificateException("Missing peer certificate");
                if (!trusted.contains(chain[0])) throw new CertificateException("Paired certificate mismatch");
            }
            public void checkClientTrusted(X509Certificate[] chain, String auth) throws CertificateException { check(chain); }
            public void checkServerTrusted(X509Certificate[] chain, String auth) throws CertificateException { check(chain); }
            public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
        };
        SSLContext tls = SSLContext.getInstance("TLS");
        tls.init(km.getKeyManagers(), new TrustManager[] {pins}, null);
        SSLSocket secure = (SSLSocket)tls.getSocketFactory().createSocket(socket,
                socket.getInetAddress().getHostAddress(), socket.getPort(), true);
        try {
            secure.setUseClientMode(client);
            if (!client) secure.setNeedClientAuth(true);
            List<String> protocols = new ArrayList<>();
            Collections.addAll(protocols, secure.getEnabledProtocols());
            protocols.retainAll(java.util.Arrays.asList("TLSv1.2", "TLSv1.3"));
            secure.setEnabledProtocols(protocols.toArray(new String[0]));
            secure.startHandshake();
            return secure;
        } catch (Exception failure) {
            secure.close();
            throw failure;
        }
    }
}
