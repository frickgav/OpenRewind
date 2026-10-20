namespace OpenRewind;

/// Turns interleaved float audio at any rate into 16 kHz mono (what Whisper wants).
/// Linear interpolation is plenty for speech; it keeps its position between buffers.
public sealed class Resampler
{
    public const int Rate = 16_000;
    double pos;          // position of the next output sample, in input samples, relative to the buffer start
    float last;          // last mono input sample of the previous buffer
    bool haveLast;

    public float[] Convert(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        if (channels < 1 || sampleRate < 1) return Array.Empty<float>();
        int n = interleaved.Length / channels;
        if (n == 0) return Array.Empty<float>();
        var mono = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        double step = (double)sampleRate / Rate;
        var out_ = new List<float>((int)(n / step) + 2);
        // Index -1 is the last sample of the previous buffer, so interpolation is seamless across buffers.
        while (pos < n - 1)
        {
            int i = (int)Math.Floor(pos);
            double frac = pos - i;
            float a = i < 0 ? (haveLast ? last : mono[0]) : mono[i];
            float b = mono[i + 1];
            out_.Add((float)(a + (b - a) * frac));
            pos += step;
        }
        pos -= n;
        last = mono[n - 1];
        haveLast = true;
        return out_.ToArray();
    }
}

/// Collects 16 kHz samples and cuts them into fixed-length WAV chunks.
/// Silent chunks are dropped.
public sealed class ChunkWriter
{
    public readonly string Device;
    public readonly bool IsInput;
    readonly double seconds;
    readonly Config config;
    readonly Database db;
    readonly object gate = new();
    readonly List<float> samples = new();
    DateTime startedAt = DateTime.UtcNow;

    /// When the last samples arrived. The engine's watchdog restarts a stream that went quiet.
    public DateTime LastAppend { get; private set; } = DateTime.UtcNow;

    public ChunkWriter(string device, bool isInput, double seconds, Config config, Database db)
    {
        Device = device; IsInput = isInput; this.seconds = seconds; this.config = config; this.db = db;
    }

    public void Append(float[] s)
    {
        if (s.Length == 0) return;
        float[]? take = null; DateTime at;
        float[]? early = null; DateTime earlyAt = default;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            // Windows loopback sends nothing while nothing plays. Close the clip at a break,
            // so every sample's time is still start + offset.
            if (samples.Count > 0 && (now - LastAppend).TotalSeconds > 2)
            {
                early = samples.ToArray(); earlyAt = startedAt;
                samples.Clear();
            }
            LastAppend = now;
            if (samples.Count == 0) startedAt = now.AddSeconds(-(double)s.Length / Resampler.Rate);
            samples.AddRange(s);
            at = startedAt;
            if (samples.Count >= seconds * Resampler.Rate) { take = samples.ToArray(); samples.Clear(); }
        }
        if (early != null && early.Length > Resampler.Rate) Flush(early, earlyAt);
        if (take != null) Flush(take, at);
    }

    /// Write whatever is buffered (used on shutdown).
    public void FlushRemaining()
    {
        float[] take; DateTime at;
        lock (gate) { take = samples.ToArray(); at = startedAt; samples.Clear(); }
        if (take.Length > Resampler.Rate) Flush(take, at);
    }

    void Flush(float[] s, DateTime startedAt)
    {
        double sum = 0;
        foreach (var x in s) sum += x * x;
        var rms = Math.Sqrt(sum / Math.Max(s.Length, 1));
        if (rms < 0.002)  // basically silence: do not store or transcribe
        {
            Log.Write($"audio {Device}: skipped silent {s.Length / Resampler.Rate}s clip (level {rms:0.0000})");
            return;
        }
        var ts = Time.Iso(startedAt);
        var dir = Path.Combine(config.DataDir, "audio", ts[..10]);
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{ts.Replace(':', '-')}-{(IsInput ? "mic" : "system")}.wav");
            Wav.Write(s, Resampler.Rate, file);
            var id = db.InsertChunk(file, ts, Device, IsInput);
            Log.Write($"audio chunk {id} {Device} {(double)s.Length / Resampler.Rate:0}s");
        }
        catch (Exception e) { Log.Write($"audio write failed: {e.Message}"); }
    }
}

public static class Wav
{
    /// 16-bit PCM mono WAV, encrypted when Encrypt Recordings is on (see RecordingFile).
    public static void Write(float[] samples, int sampleRate, string path) => RecordingFile.Write(path, Bytes(samples, sampleRate));

    public static byte[] Bytes(float[] samples, int sampleRate)
    {
        var ms = new MemoryStream(44 + samples.Length * 2);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int bytes = samples.Length * 2;
            w.Write("RIFF"u8); w.Write(36 + bytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(bytes);
            foreach (var s in samples) w.Write((short)(Math.Clamp(s, -1f, 1f) * 32767));
        }
        return ms.ToArray();
    }

    /// Read a PCM WAV (8/16/24/32-bit int or 32-bit float, any rate and channel count) as 16 kHz mono.
    /// Decrypted first when it is an encrypted recording.
    public static float[] Load16k(string path) => Load16k(RecordingFile.Read(path));

    /// The same, from the bytes of a WAV file.
    public static float[] Load16k(byte[] wav)
    {
        using var r = new BinaryReader(new MemoryStream(wav));
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV file");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAV file");
        int format = 1, channels = 1, rate = 16000, bits = 16;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            var id = new string(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (format == 0xFFFE && size >= 40) { r.ReadBytes(8); format = r.ReadInt16(); r.ReadBytes(size - 26); }
                else r.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                var data = r.ReadBytes(size);
                int bps = bits / 8, n = data.Length / bps;
                var f = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int o = i * bps;
                    f[i] = (format, bits) switch
                    {
                        (3, 32) => BitConverter.ToSingle(data, o),
                        (_, 8) => (data[o] - 128) / 128f,
                        (_, 16) => BitConverter.ToInt16(data, o) / 32768f,
                        (_, 24) => ((data[o] | data[o + 1] << 8 | (sbyte)data[o + 2] << 16)) / 8388608f,
                        (_, 32) => BitConverter.ToInt32(data, o) / 2147483648f,
                        _ => throw new InvalidDataException($"unsupported WAV: format {format}, {bits} bit"),
                    };
                }
                return new Resampler().Convert(f, channels, rate);
            }
            else r.ReadBytes(size + (size & 1));
        }
        throw new InvalidDataException("WAV has no data");
    }
}
