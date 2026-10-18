using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OpenRewind;

/// One WASAPI stream (microphone, or loopback for "what you hear"), cut into 16 kHz WAV chunks.
sealed class AudioStream : IDisposable
{
    public readonly ChunkWriter Writer;
    readonly IWaveIn capture;
    readonly Resampler resampler = new();
    readonly string label;

    AudioStream(IWaveIn capture, ChunkWriter writer, string label)
    {
        this.capture = capture;
        Writer = writer;
        this.label = label;
        capture.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded);
        capture.RecordingStopped += (_, e) => { if (e.Exception != null) Log.Write($"{label} stopped: {e.Exception.Message}"); };
    }

    /// Default microphone. Windows asks nothing, but Settings → Privacy → Microphone must allow desktop apps.
    public static AudioStream Mic(ChunkWriter w) => new(new WasapiCapture(), w, "microphone");

    /// Everything the default speakers play, via WASAPI loopback. Needs no permission.
    public static AudioStream System(ChunkWriter w) => new(new WasapiLoopbackCapture(), w, "system audio");

    public void Start()
    {
        capture.StartRecording();
        Log.Write($"recording {label} ({capture.WaveFormat})");
    }

    void OnData(byte[] buf, int n)
    {
        if (n <= 0) return;
        var f = capture.WaveFormat;
        float[] samples;
        if (f.Encoding == WaveFormatEncoding.IeeeFloat || (f is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT))
        {
            samples = new float[n / 4];
            Buffer.BlockCopy(buf, 0, samples, 0, samples.Length * 4);
        }
        else if (f.BitsPerSample == 16)
        {
            samples = new float[n / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(buf, i * 2) / 32768f;
        }
        else if (f.BitsPerSample == 32)
        {
            samples = new float[n / 4];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt32(buf, i * 4) / 2147483648f;
        }
        else if (f.BitsPerSample == 24)
        {
            samples = new float[n / 3];
            for (int i = 0; i < samples.Length; i++)
            {
                int o = i * 3;
                samples[i] = (buf[o] | buf[o + 1] << 8 | (sbyte)buf[o + 2] << 16) / 8388608f;
            }
        }
        else return;
        Writer.Append(resampler.Convert(samples, f.Channels, f.SampleRate));
    }

    public void Dispose()
    {
        try { capture.StopRecording(); } catch { }
        capture.Dispose();
        Writer.FlushRemaining();
    }

    /// True when Windows has a device of this kind at all.
    public static bool HasDevice(DataFlow flow)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            return e.HasDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        catch { return false; }
    }
}
