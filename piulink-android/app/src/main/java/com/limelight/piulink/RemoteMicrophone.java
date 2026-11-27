package com.limelight.piulink;

import android.Manifest;
import android.annotation.SuppressLint;
import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.media.audiofx.AcousticEchoCanceler;
import android.net.Network;
import android.os.Build;
import android.os.SystemClock;

import com.limelight.R;
import com.limelight.binding.PlatformBinding;
import com.limelight.nvstream.http.LimelightCryptoProvider;
import com.limelight.utils.NetHelper;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.security.cert.X509Certificate;
import java.util.Collections;
import java.util.concurrent.atomic.AtomicLong;
import javax.net.ssl.SSLException;
import javax.net.ssl.SSLSocket;

/** One explicitly enabled foreground microphone. A stopped instance is never reused. */
public final class RemoteMicrophone {
    public interface Listener {
        void onReady(RemoteMicrophone source, String recordingDevice);
        void onStopped(RemoteMicrophone source, int message);
    }

    private final Context context;
    private final String host, session;
    private final X509Certificate pinnedHost;
    private final Listener listener;
    private volatile Socket transport = new Socket();
    private final Thread worker = new Thread(this::run, "PiuLink microphone");
    private volatile boolean stopped;
    private final AtomicLong acknowledged = new AtomicLong();

    public RemoteMicrophone(Context context, String host, String session, X509Certificate pinnedHost, Listener listener) {
        this.context = context.getApplicationContext();
        this.host = host;
        this.session = session;
        this.pinnedHost = pinnedHost;
        this.listener = listener;
    }

    public void start() { worker.start(); }

    public void stop() {
        stopped = true;
        closeTransport();
        worker.interrupt();
    }

    private void closeTransport() {
        try { transport.close(); } catch (IOException ignored) { }
    }

    private void run() {
        int message = R.string.mic_connection_lost;
        boolean tlsVerified = false;
        try {
            Network lan = NetHelper.getBindableLocalNetworkForAddress(context, NetHelper.stripIpv6Zone(host));
            try {
                connectTransport(lan);
            } catch (IOException | SecurityException denied) {
                if (lan == null || stopped || (!(denied instanceof SecurityException) &&
                        !NetHelper.isNetworkBindingDenied(denied))) throw denied;
                NetHelper.noteDirectNetworkBindingDenied(lan);
                closeTransport();
                transport = new Socket();
                connectTransport(null);
            }
            LimelightCryptoProvider keys = PlatformBinding.getCryptoProvider(context);
            try (SSLSocket tls = PairedSocketTls.handshake(transport, Collections.singletonList(pinnedHost), true,
                    keys.getClientCertificate(), keys.getClientPrivateKey())) {
                tlsVerified = true;
                String address = transport.getInetAddress().getHostAddress();
                String authority = (address.contains(":") ? "[" + address + "]" : address) + ":48011";
                InputStream input = tls.getInputStream();
                OutputStream output = tls.getOutputStream();
                String device = MicrophoneWire.open(input, output, session, authority);
                tls.setSoTimeout(5000);
                if (!stopped) capture(input, output, device);
            }
        } catch (MicrophoneWire.Rejected rejected) {
            switch (rejected.status) {
                case 404: message = R.string.mic_host_update; break;
                case 409: message = R.string.mic_busy; break;
                case 503: message = R.string.mic_no_windows_input; break;
                case 400: case 403: message = R.string.mic_session_required; break;
            }
        } catch (CaptureFailure | SecurityException e) {
            message = R.string.mic_capture_failed;
        } catch (SSLException e) {
            message = !tlsVerified ? R.string.mic_secure_connection : R.string.mic_connection_lost;
        } catch (Exception ignored) {
            // The socket may also have been closed by mute, backgrounding or the ack reader.
        } finally {
            closeTransport();
            if (!stopped) listener.onStopped(this, message);
        }
    }

    private void connectTransport(Network network) throws IOException {
        Socket socket = transport;
        if (stopped) throw new java.io.InterruptedIOException();
        if (network != null) network.bindSocket(socket);
        socket.setTcpNoDelay(true);
        socket.setSendBufferSize(8192);
        socket.connect(new InetSocketAddress(host, 48011), 5000);
        socket.setSoTimeout(10000);
    }

    private static final class CaptureFailure extends Exception { }

    @SuppressLint("MissingPermission")
    private void capture(InputStream input, OutputStream output, String device) throws Exception {
        AudioManager audio = (AudioManager)context.getSystemService(Context.AUDIO_SERVICE);
        if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED ||
                audio == null || audio.isMicrophoneMute()) throw new CaptureFailure();
        int min = AudioRecord.getMinBufferSize(MicrophoneWire.SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
        if (min <= 0) throw new CaptureFailure();
        AudioRecord recorder;
        try {
            recorder = new AudioRecord(MediaRecorder.AudioSource.VOICE_COMMUNICATION, MicrophoneWire.SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(min, MicrophoneWire.FRAME_SAMPLES * 6));
        } catch (IllegalArgumentException | UnsupportedOperationException error) { throw new CaptureFailure(); }
        AcousticEchoCanceler echo = null;
        try {
            if (recorder.getState() != AudioRecord.STATE_INITIALIZED) throw new CaptureFailure();
            if (AcousticEchoCanceler.isAvailable()) {
                try {
                    echo = AcousticEchoCanceler.create(recorder.getAudioSessionId());
                    if (echo != null) echo.setEnabled(true);
                } catch (RuntimeException ignored) { /* Voice communication can use device processing. */ }
            }
            if (stopped) return;
            try { recorder.startRecording(); }
            catch (IllegalStateException e) { throw new CaptureFailure(); }
            if (recorder.getRecordingState() != AudioRecord.RECORDSTATE_RECORDING) throw new CaptureFailure();
            new Thread(() -> readAcknowledgements(input, device), "PiuLink microphone acknowledgements").start();
            short[] samples = new short[MicrophoneWire.FRAME_SAMPLES];
            byte[] frame = new byte[samples.length * 2];
            long sent = 0, lastSample = SystemClock.elapsedRealtime(), nextPrivacyCheck = 0;
            int used = 0;
            while (!stopped && !transport.isClosed()) {
                int read = recorder.read(samples, used, samples.length - used, AudioRecord.READ_NON_BLOCKING);
                long now = SystemClock.elapsedRealtime();
                if (now >= nextPrivacyCheck) {
                    nextPrivacyCheck = now + 500;
                    if (audio.isMicrophoneMute()) throw new CaptureFailure();
                    if (Build.VERSION.SDK_INT >= 29) {
                        android.media.AudioRecordingConfiguration config = recorder.getActiveRecordingConfiguration();
                        if (config != null && config.isClientSilenced()) throw new CaptureFailure();
                    }
                }
                if (read < 0 || now - lastSample > 2000) throw new CaptureFailure();
                if (read == 0) { Thread.sleep(5); continue; }
                lastSample = now;
                used += read;
                if (used == samples.length) {
                    used = 0;
                    // Keep sampling current speech during congestion. Never queue a long backlog.
                    if (sent - acknowledged.get() >= 20) continue;
                    MicrophoneWire.encode(samples, frame);
                    output.write(frame);
                    sent++;
                }
            }
        } finally {
            try {
                if (recorder.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING) recorder.stop();
            } finally {
                if (echo != null) echo.release();
                recorder.release();
            }
        }
    }

    private void readAcknowledgements(InputStream input, String device) {
        try {
            MicrophoneWire.readAck(input);
            acknowledged.set(1);
            if (!stopped) listener.onReady(this, device);
            while (!stopped) {
                MicrophoneWire.readAck(input);
                acknowledged.addAndGet(MicrophoneWire.ACK_INTERVAL);
            }
        } catch (IOException ignored) {
            closeTransport();
            worker.interrupt();
        }
    }
}
