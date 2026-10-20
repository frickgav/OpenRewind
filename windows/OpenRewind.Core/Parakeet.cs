using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OpenRewind;

/// NVIDIA Parakeet TDT 0.6B v3 (CC-BY-4.0) on ONNX Runtime, from the sherpa-onnx int8 export:
/// encoder, decoder (prediction network) and joiner, plus tokens.txt. The Mac app does the same
/// steps in ParakeetEngine.swift. Pure parts (features, decoding, segments) are in ParakeetMath.
public sealed class ParakeetEngine : IDisposable
{
    public const string Tag = "parakeet-tdt-0.6b-v3";
    readonly InferenceSession encoder, decoder, joiner;
    readonly string[] tokens;
    readonly int blank;

    public ParakeetEngine(string dir, int threads)
    {
        using var opts = new SessionOptions
        {
            IntraOpNumThreads = threads, InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        encoder = new InferenceSession(Path.Combine(dir, "encoder.int8.onnx"), opts);
        decoder = new InferenceSession(Path.Combine(dir, "decoder.int8.onnx"), opts);
        joiner = new InferenceSession(Path.Combine(dir, "joiner.int8.onnx"), opts);
        tokens = ParakeetMath.ReadTokens(File.ReadAllLines(Path.Combine(dir, "tokens.txt")));
        blank = Array.IndexOf(tokens, "<blk>");
        if (blank < 0) blank = tokens.Length - 1;
    }

    public void Dispose() { encoder.Dispose(); decoder.Dispose(); joiner.Dispose(); }

    /// 16 kHz mono samples → segments with times in seconds from the start.
    public List<Segment> Transcribe(float[] samples)
    {
        if (samples.Length < ParakeetMath.Hop * 10) return new();
        var (feats, frames) = ParakeetMath.Features(samples);
        var (enc, encFrames) = Encode(feats, frames);
        var hits = Decode(enc, encFrames);
        return ParakeetMath.Segments(hits, tokens, samples.Length / (double)ParakeetMath.SampleRate);
    }

    (float[] Data, int Frames) Encode(float[] feats, int frames)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("audio_signal", new DenseTensor<float>(feats, new[] { 1, ParakeetMath.Mels, frames })),
            NamedOnnxValue.CreateFromTensor("length", new DenseTensor<long>(new long[] { frames }, new[] { 1 })),
        };
        using var outs = encoder.Run(inputs);
        var o = outs.First(v => v.Name == "outputs").AsTensor<float>();
        var len = (int)outs.First(v => v.Name == "encoded_lengths").AsTensor<long>()[0];
        // [1, 1024, T] → one 1024-vector per frame, so the joiner gets contiguous slices.
        int dim = o.Dimensions[1], t = o.Dimensions[2];
        var src = o.ToArray();
        var data = new float[t * dim];
        for (int d = 0; d < dim; d++)
            for (int i = 0; i < t; i++) data[i * dim + d] = src[d * t + i];
        return (data, Math.Min(len, t));
    }

    List<(int Token, int Frame)> Decode(float[] enc, int frames)
    {
        const int encDim = 1024, predDim = 640;
        var stateNames = decoder.InputNames.Where(k => k != "targets" && k != "target_length").ToArray();
        var h = new float[2 * predDim];
        var c = new float[2 * predDim];
        float[] decOut = Array.Empty<float>();
        void Predict(int token)
        {
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("targets", new DenseTensor<int>(new[] { token }, new[] { 1, 1 })),
                NamedOnnxValue.CreateFromTensor("target_length", new DenseTensor<int>(new[] { 1 }, new[] { 1 })),
                NamedOnnxValue.CreateFromTensor(stateNames[0], new DenseTensor<float>(h, new[] { 2, 1, predDim })),
                NamedOnnxValue.CreateFromTensor(stateNames[1], new DenseTensor<float>(c, new[] { 2, 1, predDim })),
            };
            using var outs = decoder.Run(inputs);
            var list = outs.ToList();
            // Outputs, in order: decoder output [1, 640, 1], lengths, then the two new states.
            decOut = list[0].AsTensor<float>().ToArray();
            h = list[2].AsTensor<float>().ToArray();
            c = list[3].AsTensor<float>().ToArray();
        }
        float[] Joint(int t)
        {
            var e = new float[encDim];
            Array.Copy(enc, t * encDim, e, 0, encDim);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("encoder_outputs", new DenseTensor<float>(e, new[] { 1, encDim, 1 })),
                NamedOnnxValue.CreateFromTensor("decoder_outputs", new DenseTensor<float>(decOut, new[] { 1, predDim, 1 })),
            };
            using var outs = joiner.Run(inputs);
            return outs.First().AsTensor<float>().ToArray();
        }
        Predict(blank);  // the prediction network starts from the blank token
        return ParakeetMath.GreedyTdt(frames, tokens.Length, blank, Joint, Predict);
    }

}

/// The parts of Parakeet that need no model: log-mel features exactly like NeMo's preprocessor,
/// TDT greedy decoding, and turning tokens into timed lines. Mirrored in ParakeetMath.swift.
public static class ParakeetMath
{
    public const int SampleRate = 16000, Mels = 128, NFft = 512, Hop = 160, Win = 400;
    /// The encoder keeps one frame in 8, so each decoded step is 80 ms.
    public const double FrameSeconds = 0.08;
    /// Durations the TDT joiner predicts after the vocabulary scores.
    public static readonly int[] Durations = { 0, 1, 2, 3, 4 };
    const int MaxTokensPerFrame = 10;

    static readonly Lazy<float[]> Window = new(HannInFft);
    static readonly Lazy<(float[] Bank, int[] From, int[] To)> Mel = new(() =>
    {
        var bank = MelFilters();
        int nb = NFft / 2 + 1;
        // Each filter is a narrow triangle: only add up the bins where it is not zero.
        var from = new int[Mels];
        var to = new int[Mels];
        for (int m = 0; m < Mels; m++)
        {
            int a = 0, b = nb;
            while (a < nb && bank[m * nb + a] == 0) a++;
            while (b > a && bank[m * nb + b - 1] == 0) b--;
            from[m] = a; to[m] = b;
        }
        return (bank, from, to);
    });

    /// Log-mel features, [Mels, frames] row by row, normalized per mel band (NeMo's "per_feature").
    /// Pre-emphasis 0.97, 25 ms Hann window in a 512-point FFT every 10 ms, centered with zero padding,
    /// power spectrum, Slaney mel filters to 8 kHz, log(x + 2^-24).
    public static (float[] Feats, int Frames) Features(float[] samples)
    {
        var (melBank, melFrom, melTo) = Mel.Value;
        var window = Window.Value;
        int n = samples.Length, frames = 1 + n / Hop, bins = NFft / 2 + 1;
        var x = new double[n + NFft];  // NFft/2 zeros on each side
        int pad = NFft / 2;
        for (int i = 0; i < n; i++) x[pad + i] = i == 0 ? samples[0] : samples[i] - 0.97 * samples[i - 1];
        var logMel = new double[Mels * frames];
        var re = new double[NFft];
        var im = new double[NFft];
        var power = new double[bins];
        for (int f = 0; f < frames; f++)
        {
            int o = f * Hop;
            for (int i = 0; i < NFft; i++) { re[i] = x[o + i] * window[i]; im[i] = 0; }
            Fft(re, im);
            for (int k = 0; k < bins; k++) power[k] = re[k] * re[k] + im[k] * im[k];
            for (int m = 0; m < Mels; m++)
            {
                double s = 0;
                int row = m * bins;
                for (int k = melFrom[m]; k < melTo[m]; k++) s += melBank[row + k] * power[k];
                logMel[m * frames + f] = Math.Log(s + 5.960464477539063e-08);
            }
        }
        var feats = new float[Mels * frames];
        for (int m = 0; m < Mels; m++)
        {
            double mean = 0;
            for (int f = 0; f < frames; f++) mean += logMel[m * frames + f];
            mean /= frames;
            double v = 0;
            for (int f = 0; f < frames; f++) { var d = logMel[m * frames + f] - mean; v += d * d; }
            var std = Math.Sqrt(v / Math.Max(1, frames - 1)) + 1e-5;
            for (int f = 0; f < frames; f++) feats[m * frames + f] = (float)((logMel[m * frames + f] - mean) / std);
        }
        return (feats, frames);
    }

    /// A symmetric 400-point Hann window in the middle of the 512-point frame (as torch.stft pads it).
    static float[] HannInFft()
    {
        var w = new float[NFft];
        int off = (NFft - Win) / 2;
        for (int i = 0; i < Win; i++) w[off + i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Win - 1)));
        return w;
    }

    static double HzToMel(double f)
    {
        const double fSp = 200.0 / 3, minLogHz = 1000, minLogMel = minLogHz / fSp;
        double logStep = Math.Log(6.4) / 27;
        return f >= minLogHz ? minLogMel + Math.Log(f / minLogHz) / logStep : f / fSp;
    }

    static double MelToHz(double m)
    {
        const double fSp = 200.0 / 3, minLogHz = 1000, minLogMel = minLogHz / fSp;
        double logStep = Math.Log(6.4) / 27;
        return m >= minLogMel ? minLogHz * Math.Exp(logStep * (m - minLogMel)) : fSp * m;
    }

    /// librosa.filters.mel(sr=16000, n_fft=512, n_mels=128, fmin=0, fmax=8000, norm="slaney"), [Mels, bins].
    public static float[] MelFilters()
    {
        int bins = NFft / 2 + 1;
        var pts = new double[Mels + 2];
        double lo = HzToMel(0), hi = HzToMel(SampleRate / 2.0);
        for (int i = 0; i < pts.Length; i++) pts[i] = MelToHz(lo + (hi - lo) * i / (Mels + 1));
        var w = new float[Mels * bins];
        for (int m = 0; m < Mels; m++)
        {
            double l = pts[m], c = pts[m + 1], r = pts[m + 2], norm = 2.0 / (r - l);
            for (int k = 0; k < bins; k++)
            {
                double hz = (double)k * SampleRate / NFft;
                double up = (hz - l) / (c - l), down = (r - hz) / (r - c);
                w[m * bins + k] = (float)(Math.Max(0, Math.Min(up, down)) * norm);
            }
        }
        return w;
    }

    /// In-place radix-2 FFT; the length must be a power of two.
    public static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    /// TDT greedy search. `joint(t)` scores encoder frame t against the current prediction-network
    /// state: vocabulary (with blank) first, then one score per duration. `emit(token)` feeds a
    /// non-blank token back into the prediction network. Returns each token with its frame.
    public static List<(int Token, int Frame)> GreedyTdt(int frames, int vocab, int blank, Func<int, float[]> joint, Action<int> emit)
    {
        var hits = new List<(int, int)>();
        int t = 0, sameFrame = 0;
        while (t < frames)
        {
            var logits = joint(t);
            int token = ArgMax(logits, 0, vocab);
            int skip = Durations[ArgMax(logits, vocab, Durations.Length)];
            if (token != blank)
            {
                hits.Add((token, t));
                emit(token);
                sameFrame++;
            }
            if (skip > 0) { t += skip; sameFrame = 0; }
            else if (token == blank || sameFrame >= MaxTokensPerFrame) { t++; sameFrame = 0; }
        }
        return hits;
    }

    static int ArgMax(float[] v, int start, int count)
    {
        int best = start;
        for (int i = start + 1; i < start + count; i++) if (v[i] > v[best]) best = i;
        return best - start;
    }

    /// tokens.txt: "<piece> <id>" per line, in id order.
    public static string[] ReadTokens(IEnumerable<string> lines)
    {
        var map = new SortedDictionary<int, string>();
        foreach (var line in lines)
        {
            var sp = line.LastIndexOf(' ');
            if (sp <= 0 || !int.TryParse(line[(sp + 1)..], out var id)) continue;
            map[id] = line[..sp];
        }
        var outArr = new string[map.Count == 0 ? 0 : map.Keys.Max() + 1];
        foreach (var (id, piece) in map) outArr[id] = piece;
        for (int i = 0; i < outArr.Length; i++) outArr[i] ??= "";
        return outArr;
    }

    /// A pause this long between words starts a new line.
    const double PauseSeconds = 0.8;
    /// Lines longer than this are cut at the next word, like Whisper's segments.
    const double MaxLineSeconds = 12;
    /// A word piece is said over about this long after the step it was decoded at.
    const double TokenTail = 0.3;

    /// Joins SentencePiece pieces ("▁" starts a word) into lines that end at sentence ends, pauses,
    /// or after about 12 s, each with start and end times in seconds.
    public static List<Segment> Segments(IReadOnlyList<(int Token, int Frame)> hits, IReadOnlyList<string> tokens, double audioSeconds)
    {
        var lines = new List<Segment>();
        var text = new System.Text.StringBuilder();
        double start = 0, last = 0;
        bool endsSentence = false;
        void Flush(double end)
        {
            var s = text.ToString().Trim();
            if (s.Length > 0 && s.Any(char.IsLetterOrDigit)) lines.Add(new Segment(Round(start), Round(Math.Max(end, start)), s));
            text.Clear();
        }
        foreach (var (token, frame) in hits)
        {
            if (token < 0 || token >= tokens.Count) continue;
            var piece = tokens[token];
            if (piece.Length == 0 || (piece.StartsWith('<') && piece.EndsWith('>'))) continue;
            double at = frame * FrameSeconds;
            bool newWord = piece.StartsWith('▁');
            if (text.Length > 0 && newWord && (endsSentence || at - last >= PauseSeconds || at - start >= MaxLineSeconds))
                Flush(Math.Min(last + TokenTail, at));
            if (text.Length == 0) start = at;
            text.Append(piece.Replace('▁', ' '));
            last = at;
            var trimmed = piece.TrimEnd();
            endsSentence = trimmed.Length > 0 && ".?!…。？！".Contains(trimmed[^1]);
        }
        Flush(Math.Min(last + TokenTail, Math.Max(audioSeconds, last)));
        return lines;
    }

    static double Round(double s) => Math.Round(s, 2);
}
