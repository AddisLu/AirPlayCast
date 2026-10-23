using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AirPlayCast;

/// <summary>
/// Captures what Windows is playing on an output device (WASAPI loopback) and serves it as
/// 44.1 kHz stereo 16-bit, filling silence when nothing is playing.
/// </summary>
public sealed class LoopbackSource : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _output;
    private readonly float[] _scratch = new float[AlacEncoder.FramesPerPacket * 2];
    private readonly TimeSpan _minBuffered = TimeSpan.FromMilliseconds(40);
    private readonly TimeSpan _maxBuffered = TimeSpan.FromMilliseconds(250);
    private readonly bool _isFloat;
    private bool _primed;
    private float _peak;
    private long _peakAt;

    public MMDevice Device { get; }
    public string DeviceName => Device.FriendlyName;

    /// <summary>Peak level (0..1) of what Windows is playing right now; 0 when nothing renders.</summary>
    public float Level => Environment.TickCount64 - _peakAt > 150 ? 0 : _peak;

    /// <summary>Raised when capture stops on its own (device unplugged, format change...).</summary>
    public event Action<Exception?>? Stopped;

    public LoopbackSource(MMDevice? device = null)
    {
        Device = device ?? new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _capture = new WasapiLoopbackCapture(Device);
        var fmt = _capture.WaveFormat;
        _isFloat = fmt.BitsPerSample == 32 && (fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                   (fmt is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT));
        _buffer = new BufferedWaveProvider(fmt)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _capture.DataAvailable += (_, e) =>
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            if (_isFloat) TrackPeak(e.Buffer, e.BytesRecorded);
        };
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);

        ISampleProvider s = _buffer.ToSampleProvider();
        if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
        else if (s.WaveFormat.Channels > 2) s = new FrontPairSampleProvider(s);
        if (s.WaveFormat.SampleRate != RaopClient.SampleRate) s = new WdlResamplingSampleProvider(s, RaopClient.SampleRate);
        _output = s;
    }

    public void Start() => _capture.StartRecording();

    private void TrackPeak(byte[] data, int count)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data.AsSpan(0, count - count % 4));
        float peak = 0;
        foreach (var v in samples) peak = Math.Max(peak, Math.Abs(v));
        _peak = Math.Min(1, peak);
        _peakAt = Environment.TickCount64;
    }

    /// <summary>Called by the RAOP sender for every packet.</summary>
    public void Fill(Span<short> pcm)
    {
        var buffered = _buffer.BufferedDuration;
        if (buffered > _maxBuffered)
        {
            // Capture clock runs faster than ours, or we were idle: drop the excess.
            int drop = (int)((buffered - _minBuffered).TotalSeconds * _buffer.WaveFormat.AverageBytesPerSecond);
            drop -= drop % _buffer.WaveFormat.BlockAlign;
            _buffer.Read(new byte[drop], 0, drop);
        }
        if (!_primed && _buffer.BufferedDuration >= _minBuffered) _primed = true;
        if (_primed && _buffer.BufferedDuration < TimeSpan.FromMilliseconds(10)) _primed = false;

        if (!_primed)
        {
            pcm.Clear();
            return;
        }

        int got = _output.Read(_scratch, 0, pcm.Length);
        for (int i = 0; i < pcm.Length; i++)
        {
            float v = i < got ? _scratch[i] : 0;
            pcm[i] = (short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue);
        }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }

    public static List<MMDevice> OutputDevices() =>
        new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

    /// <summary>Keeps front-left/front-right of a multichannel stream.</summary>
    private sealed class FrontPairSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] _buf = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            int ch = source.WaveFormat.Channels;
            int frames = count / 2;
            if (_buf.Length < frames * ch) _buf = new float[frames * ch];
            int read = source.Read(_buf, 0, frames * ch) / ch;
            for (int f = 0; f < read; f++)
            {
                buffer[offset + f * 2] = _buf[f * ch];
                buffer[offset + f * 2 + 1] = _buf[f * ch + 1];
            }
            return read * 2;
        }
    }
}
