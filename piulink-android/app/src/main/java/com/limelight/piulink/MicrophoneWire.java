package com.limelight.piulink;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.UUID;

final class MicrophoneWire {
    static final int SAMPLE_RATE = 16000;
    static final int FRAME_SAMPLES = SAMPLE_RATE / 50;
    static final int ACK_INTERVAL = 5;
    private static final String PROTOCOL = "piulink-microphone-v1";

    static final class Rejected extends IOException {
        final int status;
        Rejected(int status) { super("Host response " + status); this.status = status; }
    }

    static String open(InputStream input, OutputStream output, String session, String host) throws IOException {
        try {
            if (session == null || !UUID.fromString(session).toString().equalsIgnoreCase(session))
                throw new IOException("Missing stream session");
        } catch (IllegalArgumentException error) { throw new IOException("Invalid stream session", error); }
        if (host == null || host.chars().anyMatch(c -> c <= 32 || c >= 127)) throw new IOException("Invalid host");
        String header = "GET /v1/microphone HTTP/1.1\r\nHost: " + host + "\r\nConnection: Upgrade\r\n" +
                "Upgrade: " + PROTOCOL + "\r\nX-PiuLink-Session: " + session + "\r\n\r\n";
        output.write(header.getBytes(StandardCharsets.US_ASCII));
        output.flush();
        String[] status = readLine(input).split(" ", 3);
        if (status.length < 2 || !status[0].startsWith("HTTP/1.") || !status[1].matches("[0-9]{3}"))
            throw new IOException("Invalid microphone response");
        int code = Integer.parseInt(status[1]);
        if (code != 101) throw new Rejected(code);
        String name = null;
        boolean upgraded = false, connection = false;
        for (int i = 0; i < 16; i++) {
            String line = readLine(input);
            if (line.isEmpty()) {
                if (upgraded && connection && name != null && !name.isEmpty()) return name;
                break;
            }
            int pos = line.indexOf(':');
            if (pos < 1) throw new IOException("Invalid microphone header");
            String key = line.substring(0, pos), value = line.substring(pos + 1).trim();
            if (key.equalsIgnoreCase("Upgrade")) upgraded = PROTOCOL.equals(value);
            if (key.equalsIgnoreCase("Connection")) connection = "Upgrade".equalsIgnoreCase(value);
            if (key.equalsIgnoreCase("X-PiuLink-Input")) {
                try { name = new String(Base64.getDecoder().decode(value), StandardCharsets.UTF_8); }
                catch (IllegalArgumentException e) { throw new IOException("Invalid device name", e); }
                if (name.length() > 256 || name.chars().anyMatch(Character::isISOControl)) throw new IOException("Invalid device name");
            }
        }
        throw new IOException("Incomplete microphone response");
    }

    private static String readLine(InputStream input) throws IOException {
        StringBuilder line = new StringBuilder();
        while (line.length() < 1536) {
            int b = input.read();
            if (b < 0) throw new EOFException();
            if (b == '\r') {
                if (input.read() != '\n') throw new IOException("Invalid line ending");
                return line.toString();
            }
            if (b < 32 || b > 126) throw new IOException("Invalid header character");
            line.append((char)b);
        }
        throw new IOException("Oversize microphone header");
    }

    static void encode(short[] samples, byte[] frame) {
        for (int i = 0; i < samples.length; i++) {
            frame[2*i] = (byte)samples[i];
            frame[2*i+1] = (byte)(samples[i] >> 8);
        }
    }

    static void readAck(InputStream input) throws IOException {
        if (input.read() != 1) throw new IOException("Microphone stream closed");
    }
}
