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
