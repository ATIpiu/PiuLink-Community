package com.limelight.piulink.transfer;

import android.content.Context;
import com.limelight.binding.PlatformBinding;
import com.limelight.computers.ComputerDatabaseManager;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.LimelightCryptoProvider;
import com.limelight.piulink.PairedSocketTls;
import java.net.Socket;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Collection;
import java.util.List;
import javax.net.ssl.SSLSocket;

/** Both directions present the streaming identity and pin the other end's paired certificate. */
final class FileTransferTls {
    static List<ComputerDetails> pairedComputers(Context context) {
        ComputerDatabaseManager db = new ComputerDatabaseManager(context);
        try {
            List<ComputerDetails> paired = new ArrayList<>();
            for (ComputerDetails computer : db.getAllComputers())
                if (computer.serverCert != null) paired.add(computer);
            return paired;
        } finally { db.close(); }
    }

    static ComputerDetails peer(Collection<ComputerDetails> trusted, X509Certificate certificate)
            throws CertificateException {
        for (ComputerDetails pc : trusted) if (certificate.equals(pc.serverCert)) return pc;
        throw new CertificateException("电脑证书与已配对设备不符");
    }

    static SSLSocket handshake(Context context, Socket socket, Collection<ComputerDetails> trusted,
                               boolean client) throws Exception {
        LimelightCryptoProvider keys = PlatformBinding.getCryptoProvider(context);
        return handshake(socket, trusted, client, keys.getClientCertificate(), keys.getClientPrivateKey());
    }

    static SSLSocket handshake(Socket socket, Collection<ComputerDetails> trusted, boolean client,
                               X509Certificate identity, java.security.PrivateKey privateKey) throws Exception {
        List<X509Certificate> certificates = new ArrayList<>();
        for (ComputerDetails computer : trusted) {
            if (computer.serverCert != null) certificates.add(computer.serverCert);
        }
        return PairedSocketTls.handshake(socket, certificates, client, identity, privateKey);
    }
}
