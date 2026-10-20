namespace OpenRewind;

/// Sentence vectors for Search by Meaning. Stored as one signed byte per number, scaled so the
/// largest is ±127: a quarter of the size of floats, and the ranking barely changes.
/// The Mac app stores and scores them the same way (Meaning.swift).
public static class Vectors
{
    /// Average of the token vectors (`hidden` is tokens × dim), then length 1: what
    /// sentence-transformers does for all-MiniLM-L6-v2.
    public static float[] MeanPool(ReadOnlySpan<float> hidden, int tokens, int dim)
    {
        var v = new float[dim];
        for (int t = 0; t < tokens; t++)
            for (int i = 0; i < dim; i++) v[i] += hidden[t * dim + i];
        for (int i = 0; i < dim; i++) v[i] /= Math.Max(1, tokens);
        return Normalize(v);
    }

    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var n = Math.Sqrt(sum);
        if (n == 0) return v;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / n);
        return v;
    }

    public static byte[] Quantize(ReadOnlySpan<float> v)
    {
        float max = 0;
        foreach (var x in v) max = Math.Max(max, Math.Abs(x));
        var q = new byte[v.Length];
        if (max == 0) return q;
        for (int i = 0; i < v.Length; i++) q[i] = (byte)(sbyte)Math.Clamp((int)MathF.Round(v[i] / max * 127), -127, 127);
        return q;
    }

    public static float[] Dequantize(ReadOnlySpan<byte> q)
    {
        var v = new float[q.Length];
        for (int i = 0; i < q.Length; i++) v[i] = (sbyte)q[i];
        return v;
    }

    /// Cosine similarity of a stored vector and a query of length 1. The stored scale cancels out.
    public static float Score(ReadOnlySpan<byte> stored, ReadOnlySpan<float> query)
    {
        if (stored.Length != query.Length) return float.NegativeInfinity;
        float dot = 0, norm = 0;
        for (int i = 0; i < stored.Length; i++)
        {
            float x = (sbyte)stored[i];
            dot += x * query[i];
            norm += x * x;
        }
        return norm == 0 ? 0 : dot / MathF.Sqrt(norm);
    }
}

/// Keeps the k best-scoring items seen so far (a min-heap), so a search can stream every row
/// and hold only k of them.
public sealed class TopK<T>
{
    readonly int k;
    readonly PriorityQueue<T, float> heap = new();

    public TopK(int k) { this.k = Math.Max(1, k); }

    public int Count => heap.Count;

    /// The score an item needs to get in, once the list is full.
    public float Floor => heap.Count < k ? float.NegativeInfinity : heap.TryPeek(out _, out var s) ? s : float.NegativeInfinity;

    public void Add(T item, float score)
    {
        if (float.IsNaN(score)) return;
        if (heap.Count < k) heap.Enqueue(item, score);
        else if (score > Floor) heap.EnqueueDequeue(item, score);
    }

    /// Best first.
    public List<(T Item, float Score)> Best()
    {
        var all = heap.UnorderedItems.Select(x => (x.Element, x.Priority)).ToList();
        all.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return all;
    }
}
