package com.limelight.piulink;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.UUID;

public class MicrophoneWireTest {
    private static final String ID = UUID.randomUUID().toString();
    private static final String NAME = "麦克风 (Steam Streaming Microphone)";
    private static final String ACCEPT = "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: piulink-microphone-v1\r\nX-PiuLink-Input: " +
            Base64.getEncoder().encodeToString(NAME.getBytes(StandardCharsets.UTF_8)) + "\r\n\r\n";

    private static ByteArrayInputStream ascii(String string) {
        return new ByteArrayInputStream(string.getBytes(StandardCharsets.US_ASCII));
    }

    @Test public void upgradePreservesChineseNameAndLeavesFirstAcknowledgementForAudio() throws Exception {
        ByteArrayOutputStream request = new ByteArrayOutputStream();
        InputStream input = ascii(ACCEPT + (char)1);
        assertEquals(NAME, MicrophoneWire.open(input, request, ID, "[::1]:48011"));
        assertTrue(request.toString("US-ASCII").contains("Host: [::1]:48011\r\n"));
        assertTrue(request.toString("US-ASCII").endsWith("X-PiuLink-Session: " + ID + "\r\n\r\n"));
        MicrophoneWire.readAck(input);
        assertThrows(IOException.class, () -> MicrophoneWire.readAck(input));
    }

    @Test public void rejectedHostsAndMalformedUpgradesDoNotStartCapture() {
        for (int code : new int[] {400,403,404,409,503}) {
            MicrophoneWire.Rejected error = assertThrows(MicrophoneWire.Rejected.class, () ->
                    MicrophoneWire.open(ascii("HTTP/1.1 " + code + " Error\r\n"), new ByteArrayOutputStream(), ID, "localhost"));
            assertEquals(code, error.status);
        }
        for (String bad : new String[] { ACCEPT.replace("Connection: Upgrade\r\n", ""), ACCEPT.replace("microphone-v1","microphone-v99"),
                ACCEPT.replace("X-PiuLink-Input:", "Other:"), ACCEPT.replace("\r\n","\n") })
            assertThrows(IOException.class, () -> MicrophoneWire.open(ascii(bad), new ByteArrayOutputStream(), ID, "localhost"));
        ByteArrayOutputStream sent = new ByteArrayOutputStream();
        assertThrows(IOException.class, () -> MicrophoneWire.open(ascii(ACCEPT), sent, "broken", "localhost"));
        assertEquals(0, sent.size());
    }

    @Test public void frameHasTwentyMillisecondsOfSignedLittleEndianSamples() {
        assertEquals(320, MicrophoneWire.FRAME_SAMPLES);
        short[] source = {Short.MIN_VALUE, -1, 0, (short)0x1234, Short.MAX_VALUE};
        byte[] encoded = new byte[source.length * 2];
        MicrophoneWire.encode(source, encoded);
        assertArrayEquals(new byte[]{0,-128,-1,-1,0,0,0x34,0x12,-1,0x7f}, encoded);
    }
}
