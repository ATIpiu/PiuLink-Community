using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PiuLink.Host.Services;
using PiuLink.Host.Core.Sunshine;

internal static class RemoteMicrophoneTests
{
    private sealed class Sink : IMicrophoneOutput
    {
        public string RecordingDeviceName => "测试录音端";
        public byte[]? LastFrame;
        public void WriteFrame(byte[] frame) => LastFrame = frame.ToArray();
        public void Dispose() { }
    }

    public static async Task Run(bool deviceTest)
    {
        using var hostCert = CompanionContractTests.Certificate("Microphone Host");
        using var clientCert = CompanionContractTests.Certificate("Microphone Client");
        using var server = new DisplayCompanionServer(_ => Task.FromResult<SunshineDisplayState?>(null),
            (_, _, _, _, _) => Task.FromResult(false));
        var session = Guid.NewGuid().ToString();
        var owner = Convert.ToHexString(SHA256.HashData(clientCert.RawData));
        var active = true;
        server.LoadTlsCertificate = () => new X509Certificate2(hostCert.Export(X509ContentType.Pkcs12));
        server.AuthenticatePeer = peer => peer?.Equals(clientCert) == true;
        server.OwnsActiveSession = (peer, id, _) => Task.FromResult(Volatile.Read(ref active) && peer == owner && id == session);
        var sink = new Sink();
        int ensured = 0;
        server.EnsureMicrophone = (peer, id) => { if (peer == owner && id == session) ensured++; };
        server.OpenMicrophone = () => sink;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        async Task<(Stream Stream, Task Handling, string? Name)> Connect(int expected, string id, bool secure = true)
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            var handling = server.HandleAsync(await listener.AcceptTcpClientAsync(token), token);
            Stream stream = client.GetStream();
            if (secure)
            {
                var tls = new SslStream(stream, false, (_, cert, _, _) => cert?.Equals(hostCert) == true);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                    TargetHost = "localhost", ClientCertificates = new X509CertificateCollection { clientCert },
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, token);
                stream = tls;
            }
            var request = $"GET /v1/microphone HTTP/1.1\r\nHost: localhost\r\nConnection: Upgrade\r\nUpgrade: piulink-microphone-v1\r\nX-PiuLink-Session: {id}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
            using var header = new MemoryStream();
            var b = new byte[1];
            int tail = 0;
            do
            {
                await stream.ReadExactlyAsync(b, token);
                header.WriteByte(b[0]);
                tail = (tail << 8) | b[0];
            } while (tail != 0x0d0a0d0a && header.Length < 4096);
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            if (lines[0].Split(' ')[1] != expected.ToString()) throw new Exception($"Expected {expected}, got {lines[0]}");
            var name = lines.FirstOrDefault(x => x.StartsWith("X-PiuLink-Input: "))?.Split(' ')[1];
            if (expected != 101) { await handling; stream.Dispose(); client.Dispose(); }
            return (stream, handling, name == null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(name)));
        }

        await Connect(403, session, false);
        await Connect(400, "invalid");
        await Connect(403, Guid.NewGuid().ToString());
        server.OpenMicrophone = () => throw new IOException("no audio device");
        await Connect(503, session);
        server.OpenMicrophone = () => sink;
        var first = await Connect(101, session);
        using (first.Stream)
        {
            if (first.Name != sink.RecordingDeviceName) throw new Exception("Recording device name was lost.");
            await Connect(409, session);
            var samples = RandomNumberGenerator.GetBytes(RemoteMicrophoneOutput.FrameBytes);
            await first.Stream.WriteAsync(samples.AsMemory(0, 17), token);
            await first.Stream.WriteAsync(samples.AsMemory(17), token);
            var ack = new byte[1];
            await first.Stream.ReadExactlyAsync(ack, token);
            if (ack[0] != 1 || !samples.SequenceEqual(sink.LastFrame!)) throw new Exception("PCM frame/ack mismatch");
            Volatile.Write(ref active, false);
            await first.Handling.WaitAsync(TimeSpan.FromSeconds(4));
        }
        Volatile.Write(ref active, true);
        var retry = await Connect(101, session);
        await retry.Stream.WriteAsync(new byte[17], token);
        retry.Stream.Dispose();
        await retry.Handling;
        if (ensured != 2) throw new Exception($"Expected EnsureMicrophone to run twice for valid upgrades, observed {ensured}.");
        Console.WriteLine("PASS microphone TLS/session checks, busy/unavailable states, fragmented frames, acknowledgement, revocation and reopen.");
        if (!deviceTest) return;

        using var devices = new MMDeviceEnumerator();
        var pair = RemoteMicrophoneOutput.FindEndpoints(devices) ?? throw new Exception("No supported recording endpoint pair on this PC.");
        var route = SessionMicrophoneRoute.Capture() ?? throw new Exception("Missing microphone route.");
        try {
            route.Switch(restore: false);
            foreach (var role in route.Original.Keys) {
                using var selected = devices.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                if (selected.ID != pair.Capture) throw new Exception($"Failed to switch capture role {role}.");
            }
        } finally { route.Switch(restore: true); }
        foreach (var (role, original) in route.Original) {
            using var selected = devices.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            if (selected.ID != original) throw new Exception($"Failed to restore capture role {role}.");
        }
        Console.WriteLine("PASS switched and restored each Windows capture role.");
        using var input = devices.GetDevice(pair.Capture);
        using var capture = new WasapiCapture(input, true, 40);
        var format = capture.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat && !(format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16))
            throw new Exception("Unsupported test capture format " + format);
        var measured = new List<double>();
        capture.DataAvailable += (_, e) => {
            lock (measured)
                for (int i = 0; i + format.BlockAlign <= e.BytesRecorded; i += format.BlockAlign)
                    measured.Add(format.Encoding == WaveFormatEncoding.IeeeFloat
                        ? BitConverter.ToSingle(e.Buffer, i) : BitConverter.ToInt16(e.Buffer, i) / 32768.0);
        };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.RecordingStopped += (_, e) => {
            if (e.Exception != null) stopped.TrySetException(e.Exception); else stopped.TrySetResult();
        };
        capture.StartRecording();
        await Task.Delay(350, token);
        server.OpenMicrophone = RemoteMicrophoneOutput.Open;
        var live = await Connect(101, session);
        if (live.Name != input.FriendlyName) throw new Exception("Host returned the wrong recording endpoint.");
        using (live.Stream)
        {
            var frame = new byte[RemoteMicrophoneOutput.FrameBytes];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < 80; f++)
            {
                int hz = f < 40 ? 600 : 1100;
                for (int s = 0; s < frame.Length / 2; s++)
                    BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(s * 2), (short)(10000 * Math.Sin(2 * Math.PI * hz * s / 16000)));
                await live.Stream.WriteAsync(frame, token);
                if (f % 5 == 0) await live.Stream.ReadExactlyAsync(new byte[1], token);
                int wait = (f + 1) * 20 - (int)clock.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait, token);
            }
        }
        await live.Handling;
        await Task.Delay(500, token);
        capture.StopRecording();
        await stopped.Task.WaitAsync(token);
        Console.WriteLine($"Analyzing {measured.Count} samples from {input.FriendlyName} at {format}");
        VerifyTones(measured.ToArray(), format.SampleRate);
        Console.WriteLine($"PASS WASAPI recording endpoint {input.FriendlyName} ({format.SampleRate} Hz), 600 Hz then 1100 Hz, silence after close.");
    }

    private static void VerifyTones(double[] samples, int rate)
    {
        int block = rate / 20;
        double Amplitude(int start, int hz)
        {
            double x = 0, y = 0;
            for (int n = 0; n < block; n++)
            {
                var a = 2 * Math.PI * hz * n / rate;
                x += samples[start+n] * Math.Cos(a);
                y += samples[start+n] * Math.Sin(a);
            }
            return 2 * Math.Sqrt(x*x + y*y) / block;
        }
        var codes = new List<int>();
        for (int start = 0; start + block <= samples.Length; start += block)
        {
            var a = Amplitude(start, 600);
            var b = Amplitude(start, 1100);
            codes.Add(a > 0.004 && a > 4*b ? 1 : b > 0.004 && b > 4*a ? 2 : 0);
        }
        var peak = samples.Max(v => Math.Abs(v));
        if (peak is < 0.01 or > 0.6) throw new Exception($"Unexpected level for 0.305 full-scale input: {peak:F3}");
        Console.WriteLine($"Measured peak {peak:F3} for 0.305 full-scale input.");
        int second = codes.IndexOf(2);
        if (second < 0 || codes.Take(second).Count(x => x == 1) < 6 ||
            codes.Skip(second).Count(x => x == 2) < 6 || codes.TakeLast(4).Any(x => x != 0))
            throw new Exception("Recording endpoint did not reproduce the two tones and return to silence: " + string.Concat(codes));
    }
}
