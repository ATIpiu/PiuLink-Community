using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PiuLink.Host.Services;

internal interface IMicrophoneOutput : IDisposable
{
    string RecordingDeviceName { get; }
    void WriteFrame(byte[] frame);
}

internal sealed class RemoteMicrophoneOutput : IMicrophoneOutput
{
    internal const int SampleRate = 16000;
    internal const int FrameBytes = SampleRate / 50 * sizeof(short);
    private readonly BufferedWaveProvider _buffer = new(new WaveFormat(SampleRate, 16, 1))
    {
        BufferDuration = TimeSpan.FromMilliseconds(160)
    };
    private readonly WasapiOut _player;
    private readonly MMDevice _render;
    private readonly MMDevice _capture;
    public string RecordingDeviceName { get; }

    // Only a matched virtual cable can feed a recording input. A normal speaker would
    // play private meeting audio locally without ever delivering it to the remote app.
    internal static (string Render, string Capture)? FindEndpoints(MMDeviceEnumerator devices)
    {
        static List<string> Owned(MMDeviceEnumerator devices, DataFlow flow)
        {
            var list = new List<string>();
            foreach (var device in devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) if (VirtualMicrophoneDevice.OwnsEndpoint(device.ID)) list.Add(device.ID);
            }
            return list;
        }

        var outputs = Owned(devices, DataFlow.Render);
        var inputs = Owned(devices, DataFlow.Capture);
        return outputs.Count == 1 && inputs.Count == 1 ? (outputs[0], inputs[0]) : null;
    }

    internal static RemoteMicrophoneOutput Open()
    {
        using var devices = new MMDeviceEnumerator();
        var pair = FindEndpoints(devices) ?? throw new IOException("No supported virtual microphone pair.");
        var render = devices.GetDevice(pair.Render);
        MMDevice capture;
        try { capture = devices.GetDevice(pair.Capture); }
        catch { render.Dispose(); throw; }
        return new RemoteMicrophoneOutput(render, capture);
    }

    internal static bool HasDedicatedPair()
    {
        using var devices = new MMDeviceEnumerator();
        return FindEndpoints(devices) is { } pair && VirtualMicrophoneDevice.OwnsEndpoint(pair.Capture);
    }

    private RemoteMicrophoneOutput(MMDevice render, MMDevice capture)
    {
        _render = render;
        _capture = capture;
        try
        {
            RecordingDeviceName = capture.FriendlyName;
            // Steam's driver passes raw samples between its endpoints. The Windows defaults
            // for its speaker and microphone can have different rates, channel counts and depths.
            bool steam = !VirtualMicrophoneDevice.HasNativeDriver;
            // This driver exposes up to +30 dB of recording gain. Compensate its
            // positive boost in our stream to keep ordinary speech below clipping.
            var voice = new VolumeWaveProvider16(_buffer);
            if (steam) voice.Volume = (float)Math.Pow(10, -Math.Max(0, capture.AudioEndpointVolume.MasterVolumeLevel) / 20);
            IWaveProvider source = steam ? new SteamMicrophonePcm(voice, capture) : voice;
            var mode = steam ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
            if (steam && !render.AudioClient.IsFormatSupported(mode, source.WaveFormat))
                throw new IOException("The render endpoint cannot match the recording device format.");
            _player = new WasapiOut(render, mode, true, 40);
            try
            {
                _player.Init(source);
                _player.Play();
            }
            catch { _player.Dispose(); throw; }
        }
        catch { render.Dispose(); capture.Dispose(); throw; }
    }

    private sealed class SteamMicrophonePcm : IWaveProvider
    {
        private readonly ISampleProvider _mono;
        private readonly bool _float;
        private float[] _samples = [];
        public WaveFormat WaveFormat { get; }

        internal SteamMicrophonePcm(IWaveProvider input, MMDevice capture)
        {
            var bytes = capture.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat]?.Value as byte[];
            if (bytes == null || bytes.Length < 18 ||
                (BinaryPrimitives.ReadUInt16LittleEndian(bytes) == 0xfffe && bytes.Length < 40))
                throw new IOException("Missing capture device format.");
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { WaveFormat = WaveFormat.MarshalFromPtr(pin.AddrOfPinnedObject()); }
            finally { pin.Free(); }
            var encoding = WaveFormat.AsStandardWaveFormat().Encoding;
            _float = encoding == WaveFormatEncoding.IeeeFloat;
            var pcm = encoding == WaveFormatEncoding.Pcm;
            if ((!_float && !pcm) || (_float && WaveFormat.BitsPerSample != 32) ||
                WaveFormat.BitsPerSample is not (16 or 24 or 32) || WaveFormat.Channels is < 1 or > 8)
                throw new IOException("Unsupported capture device format.");
            _mono = new WdlResamplingSampleProvider(input.ToSampleProvider(), WaveFormat.SampleRate);
        }

        public int Read(byte[] output, int offset, int count)
        {
            int frames = count / WaveFormat.BlockAlign;
            if (_samples.Length < frames) _samples = new float[frames];
            int read = _mono.Read(_samples, 0, frames);
            int bytes = WaveFormat.BitsPerSample / 8;
            for (int i = 0; i < read; i++)
            {
                int value = _float ? BitConverter.SingleToInt32Bits(_samples[i])
                    : (int)(Math.Clamp((double)_samples[i], -1, 1) * int.MaxValue) >> (32 - 8 * bytes);
                for (int channel = 0; channel < WaveFormat.Channels; channel++)
                    for (int b = 0; b < bytes; b++) output[offset++] = (byte)(value >> (b * 8));
            }
            return read * WaveFormat.BlockAlign;
        }
    }

    public void WriteFrame(byte[] frame)
    {
        if (_player.PlaybackState != PlaybackState.Playing)
            throw new IOException("The virtual recording device stopped.");
        // Drop stale speech after a network stall instead of replaying it late.
        if (_buffer.BufferedBytes + frame.Length > _buffer.BufferLength) _buffer.ClearBuffer();
        _buffer.AddSamples(frame, 0, frame.Length);
    }

    public void Dispose()
    {
        try { _player.Dispose(); }
        finally { _render.Dispose(); _capture.Dispose(); }
    }
}
