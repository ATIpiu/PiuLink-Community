package com.limelight.piulink.transfer;

import com.limelight.nvstream.http.ComputerDetails;
import org.bouncycastle.asn1.x500.X500Name;
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder;
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter;
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder;
import org.junit.Test;
import java.math.BigInteger;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.cert.CertificateException;
import java.util.Collections;
import java.util.Date;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import javax.net.ssl.SSLSocket;
import static org.junit.Assert.*;

public class FileTransferTlsTest {
    static final class Identity {
        final KeyPair key;
        final ComputerDetails computer = new ComputerDetails();

        Identity(String uuid) throws Exception {
            KeyPairGenerator generator = KeyPairGenerator.getInstance("RSA");
            generator.initialize(2048);
            key = generator.generateKeyPair();
            long now = System.currentTimeMillis();
            X500Name name = new X500Name("CN=" + uuid);
            computer.uuid = uuid;
            computer.serverCert = new JcaX509CertificateConverter().getCertificate(
                    new JcaX509v3CertificateBuilder(name, BigInteger.valueOf(now), new Date(now - 60000),
                            new Date(now + 3600000), name, key.getPublic())
                            .build(new JcaContentSignerBuilder("SHA256withRSA").build(key.getPrivate())));
        }
    }

    @Test public void bothDataDirectionsRequireTheirPairedCertificates() throws Exception {
        Identity host = new Identity("host"), phone = new Identity("phone");
        roundTrip(host, phone);
        roundTrip(phone, host);
        assertThrows(CertificateException.class, () ->
                FileTransferTls.peer(Collections.singletonList(host.computer), phone.computer.serverCert));
    }

    private void roundTrip(Identity sender, Identity receiver) throws Exception {
        ExecutorService worker = Executors.newSingleThreadExecutor();
        try (ServerSocket listener = new ServerSocket(0, 1, InetAddress.getLoopbackAddress())) {
            listener.setSoTimeout(5000);
            Future<Integer> received = worker.submit(() -> {
                try (Socket accepted = listener.accept()) {
                    accepted.setSoTimeout(5000);
                    try (SSLSocket secure = FileTransferTls.handshake(accepted, Collections.singletonList(sender.computer),
                            false, receiver.computer.serverCert, receiver.key.getPrivate())) {
                        assertEquals(sender.computer.serverCert, secure.getSession().getPeerCertificates()[0]);
                        int data = secure.getInputStream().read();
                        secure.getOutputStream().write(1);
                        secure.getOutputStream().flush();
                        return data;
                    }
                }
            });
            try (Socket socket = new Socket(listener.getInetAddress(), listener.getLocalPort())) {
                socket.setSoTimeout(5000);
                try (SSLSocket secure = FileTransferTls.handshake(socket, Collections.singletonList(receiver.computer),
                        true, sender.computer.serverCert, sender.key.getPrivate())) {
                    assertEquals(receiver.computer.serverCert, secure.getSession().getPeerCertificates()[0]);
                    secure.getOutputStream().write(123);
                    secure.getOutputStream().flush();
                    assertEquals(1, secure.getInputStream().read());
                }
            }
            assertEquals(123, received.get(5, TimeUnit.SECONDS).intValue());
        } finally { worker.shutdownNow(); }
    }
}
