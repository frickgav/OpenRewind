using Microsoft.ML.OnnxRuntime;

namespace OpenRewind;

/// Search by Meaning on Windows: all-MiniLM-L6-v2 run with ONNX Runtime on the CPU, on this computer.
/// Text → WordPiece tokens → the model → mean of the token vectors → length 1 (what the model was trained for).
/// One thread, so indexing stays light; a sentence takes a few milliseconds.
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    readonly InferenceSession session;
    readonly WordPiece tokenizer;
    readonly object gate = new();
    static readonly string[] Outputs = { "last_hidden_state" };

    public string ModelId => MiniLm.ModelId;

    OnnxEmbedder(InferenceSession session, WordPiece tokenizer) { this.session = session; this.tokenizer = tokenizer; }

    /// Loads the model from `dir` after checking both files against their pinned SHA-256.
    /// Throws when a file is missing or not the pinned one.
    public static OnnxEmbedder Load(string dir)
    {
        foreach (var f in MiniLm.Files)
            if (!MiniLm.Verify(Path.Combine(dir, f.Name), f))
                throw new InvalidDataException($"{f.Name} is missing or not the expected file");
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        try
        {
            var session = new InferenceSession(Path.Combine(dir, MiniLm.Model.Name), options);
            return new OnnxEmbedder(session, WordPiece.Load(Path.Combine(dir, MiniLm.Vocab.Name), MiniLm.MaxTokens));
        }
        finally { options.Dispose(); }
    }

    public float[] Embed(string text)
    {
        var ids = tokenizer.Encode(text);
        int n = ids.Length;
        var mask = new long[n];
        Array.Fill(mask, 1L);
        var types = new long[n];
        var shape = new long[] { 1, n };
        lock (gate)
        {
            using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
            using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
            using var typesValue = OrtValue.CreateTensorValueFromMemory(types, shape);
            using var run = new RunOptions();
            var names = new[] { "input_ids", "attention_mask", "token_type_ids" };
            using var results = session.Run(run, names, new[] { idsValue, maskValue, typesValue }, Outputs);
            var hidden = results[0].GetTensorDataAsSpan<float>();
            return Vectors.MeanPool(hidden, n, MiniLm.Dimension);
        }
    }

    public void Dispose() { lock (gate) session.Dispose(); }
}
