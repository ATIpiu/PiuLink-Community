package com.limelight.nvstream.http;

import com.limelight.nvstream.jni.MoonBridge;
import org.xmlpull.v1.XmlPullParserException;

import com.limelight.LimeLog;

import java.security.cert.Certificate;
import java.io.*;
import java.security.*;
import java.security.cert.*;
import java.util.Arrays;
import java.util.Locale;

public class PairingManager {
    // Used only when a PiuLink Host explicitly advertises trusted local-network pairing.
    // The transport's certificate challenge still runs and pins the client certificate.
    private NvHTTP http;
    
    private PrivateKey pk;
    private X509Certificate cert;
    private byte[] pemCertBytes;

    private X509Certificate pairedCert;
    
    public enum PairState {
        NOT_PAIRED,
        PAIRED,
        PIN_WRONG,
        FAILED,
        ALREADY_IN_PROGRESS
    }
    
    public PairingManager(NvHTTP http, LimelightCryptoProvider cryptoProvider) {
        this.http = http;
        this.cert = cryptoProvider.getClientCertificate();
        this.pemCertBytes = cryptoProvider.getPemEncodedClientCertificate();
        this.pk = cryptoProvider.getClientPrivateKey();
    }
    
    final private static char[] hexArray = "0123456789ABCDEF".toCharArray();
    private static String bytesToHex(byte[] bytes) {
        char[] hexChars = new char[bytes.length * 2];
        for ( int j = 0; j < bytes.length; j++ ) {
            int v = bytes[j] & 0xFF;
            hexChars[j * 2] = hexArray[v >>> 4];
            hexChars[j * 2 + 1] = hexArray[v & 0x0F];
        }
        return new String(hexChars);
    }
    
    private static byte[] hexToBytes(String s) {
        int len = s.length();
        if (len % 2 != 0) {
            throw new IllegalArgumentException("Illegal string length: "+len);
        }

        byte[] data = new byte[len / 2];
        for (int i = 0; i < len; i += 2) {
            data[i / 2] = (byte) ((Character.digit(s.charAt(i), 16) << 4)
                                 + Character.digit(s.charAt(i+1), 16));
        }
        return data;
    }
    
    private X509Certificate extractPlainCert(String text) throws XmlPullParserException, IOException
    {
        // Plaincert may be null if another client is already trying to pair
        String certText = NvHTTP.getXmlString(text, "plaincert", false);
        if (certText != null) {
            byte[] certBytes = hexToBytes(certText);

            try {
                CertificateFactory cf = CertificateFactory.getInstance("X.509");
                return (X509Certificate)cf.generateCertificate(new ByteArrayInputStream(certBytes));
            } catch (CertificateException e) {
                e.printStackTrace();
                throw new RuntimeException(e);
            }
        }
        else {
            return null;
        }
    }
    
    private byte[] generateRandomBytes(int length)
    {
        byte[] rand = new byte[length];
        new SecureRandom().nextBytes(rand);
        return rand;
    }
    
    private static Signature getSha256SignatureInstanceForKey(Key key) throws NoSuchAlgorithmException {
        switch (key.getAlgorithm()) {
            case "RSA":
                return Signature.getInstance("SHA256withRSA");
            case "EC":
                return Signature.getInstance("SHA256withECDSA");
            default:
                throw new NoSuchAlgorithmException("Unhandled key algorithm: " + key.getAlgorithm());
        }
    }
    
    private static boolean verifySignature(byte[] data, byte[] signature, Certificate cert) {
        try {
            Signature sig = PairingManager.getSha256SignatureInstanceForKey(cert.getPublicKey());
            sig.initVerify(cert.getPublicKey());
            sig.update(data);
            return sig.verify(signature);
        } catch (NoSuchAlgorithmException | SignatureException | InvalidKeyException e) {
            e.printStackTrace();
            throw new RuntimeException(e);
        }
    }
    
    private static byte[] signData(byte[] data, PrivateKey key) {
        try {
            Signature sig = PairingManager.getSha256SignatureInstanceForKey(key);
            sig.initSign(key);
            sig.update(data);
            return sig.sign();
        } catch (NoSuchAlgorithmException | SignatureException | InvalidKeyException e) {
            e.printStackTrace();
            throw new RuntimeException(e);
        }
    }

    private static byte[] concatBytes(byte[] a, byte[] b) {
        byte[] c = new byte[a.length + b.length];
        System.arraycopy(a, 0, c, 0, a.length);
        System.arraycopy(b, 0, c, a.length, b.length);
        return c;
    }
    
    public static String generatePinString() {
        SecureRandom r = new SecureRandom();
        return String.format((Locale)null, "%d%d%d%d",
                r.nextInt(10), r.nextInt(10),
                r.nextInt(10), r.nextInt(10));
    }

    public X509Certificate getPairedCert() {
        return pairedCert;
    }
    
    public PairState pair(String serverInfo, String pin) throws IOException, XmlPullParserException {
        pairedCert = null;
        // The JNI callback uses this provisional pin only within this handshake.
        final X509Certificate[] candidate = new X509Certificate[1];
        int result = MoonBridge.rustPair(http.getServerMajorVersion(serverInfo) >= 7,
                pin.getBytes(java.nio.charset.StandardCharsets.US_ASCII), (stage,data) -> {
            switch (stage) {
                case 0: return generateRandomBytes(48);
                case 1: {
                    String command = "phrase=getservercert&salt=" + bytesToHex(data) + "&clientcert=" + bytesToHex(pemCertBytes);
                    String response = http.executePairingCommand(command, false);
                    if (!accepted(response)) return new byte[]{0};
                    candidate[0] = extractPlainCert(response);
                    if (candidate[0] == null) return new byte[]{2};
                    http.setServerCert(candidate[0]);
                    return new byte[]{1};
                }
                case 2: case 4: {
                    boolean challenge = stage == 2;
                    String response = http.executePairingCommand((challenge ? "clientchallenge=" : "serverchallengeresp=") + bytesToHex(data), true);
                    if (!accepted(response)) return new byte[0];
                    String payload = NvHTTP.getXmlString(response, challenge ? "challengeresponse" : "pairingsecret", true);
                    return payload.length() <= 32768 ? hexToBytes(payload) : new byte[0];
                }
                case 3: return cert.getSignature();
                case 5: return new byte[]{(byte)(data.length > 16 && verifySignature(Arrays.copyOf(data,16),
                        Arrays.copyOfRange(data,16,data.length),candidate[0]) ? 1 : 0)};
                case 6: return candidate[0].getSignature();
                case 7: {
                    byte[] proof = concatBytes(data,signData(data,pk));
                    return new byte[]{(byte)(accepted(http.executePairingCommand("clientpairingsecret=" + bytesToHex(proof), true)) ? 1 : 0)};
                }
                case 8: http.unpair(); return new byte[0];
                case 9: return new byte[]{(byte)(accepted(http.executePairingChallenge()) ? 1 : 0)};
                default: throw new AssertionError(stage);
            }
        });
        switch (result) {
            case 1:
                pairedCert = candidate[0];
                return PairState.PAIRED;
            case 2: return PairState.PIN_WRONG;
            case 4: return PairState.ALREADY_IN_PROGRESS;
            default: return PairState.FAILED;
        }
    }
    private static boolean accepted(String response) throws IOException, XmlPullParserException {
        return "1".equals(NvHTTP.getXmlString(response, "paired", true));
    }
}
