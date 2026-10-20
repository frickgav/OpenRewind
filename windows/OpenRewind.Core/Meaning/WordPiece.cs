using System.Globalization;
using System.Text;

namespace OpenRewind;

/// The BERT "uncased" tokenizer that all-MiniLM-L6-v2 was trained with: clean the text, lower
/// case, drop accents, split on spaces and punctuation, put spaces around CJK characters, then
/// cut each word into the longest pieces the vocabulary has ("unaffable" → "una", "##ffa", "##ble").
/// Matches Hugging Face's BertTokenizer for this model (see the tests).
public sealed class WordPiece
{
    readonly Dictionary<string, int> vocab;
    public readonly int MaxLength;
    public readonly int Cls, Sep, Unk;
    /// A word longer than this becomes [UNK], as in BERT.
    const int MaxWordChars = 100;

    public WordPiece(IReadOnlyList<string> tokens, int maxLength = 256)
    {
        vocab = new Dictionary<string, int>(tokens.Count, StringComparer.Ordinal);
        for (int i = 0; i < tokens.Count; i++) vocab.TryAdd(tokens[i], i);
        MaxLength = maxLength;
        Cls = Id("[CLS]"); Sep = Id("[SEP]"); Unk = Id("[UNK]");
    }

    int Id(string t) => vocab.TryGetValue(t, out var i) ? i : throw new InvalidDataException($"vocabulary has no {t}");

    /// vocab.txt: one token per line, the line number is its id.
    public static WordPiece Load(string vocabPath, int maxLength = 256) =>
        new(File.ReadAllLines(vocabPath, Encoding.UTF8), maxLength);

    /// [CLS] pieces… [SEP], cut to MaxLength.
    public long[] Encode(string text)
    {
        var ids = new List<long>(64) { Cls };
        foreach (var piece in Pieces(text))
        {
            if (ids.Count >= MaxLength - 1) break;
            ids.Add(piece);
        }
        ids.Add(Sep);
        return ids.ToArray();
    }

    /// The pieces as text, for tests and debugging.
    public List<string> Tokenize(string text)
    {
        var names = vocab.ToDictionary(kv => kv.Value, kv => kv.Key);
        return Pieces(text).Select(id => names[id]).ToList();
    }

    IEnumerable<int> Pieces(string text)
    {
        foreach (var word in Words(text))
        {
            if (word.Length > MaxWordChars && word.EnumerateRunes().Count() > MaxWordChars) { yield return Unk; continue; }
            var found = new List<int>();
            int start = 0;
            bool bad = false;
            while (start < word.Length)
            {
                int end = word.Length;
                int match = -1;
                while (start < end)
                {
                    var sub = start > 0 ? "##" + word[start..end] : word[start..end];
                    if (vocab.TryGetValue(sub, out var id)) { match = id; break; }
                    end--;
                    // Never split a surrogate pair.
                    if (end > start && char.IsLowSurrogate(word[end]) && char.IsHighSurrogate(word[end - 1])) end--;
                }
                if (match < 0) { bad = true; break; }
                found.Add(match);
                start = end;
            }
            if (bad) yield return Unk;
            else foreach (var id in found) yield return id;
        }
    }

    /// Clean, lower case, strip accents, then split into words and single punctuation marks.
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        void Flush() { if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); } }
        foreach (var r in Normalize(text).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r)) { Flush(); continue; }
            if (IsPunctuation(r) || IsCjk(r.Value)) { Flush(); words.Add(r.ToString()); continue; }
            cur.Append(r.ToString());
        }
        Flush();
        return words;
    }

    /// Control characters out, every kind of space to " ", lower case, accents off (NFD, then
    /// drop the combining marks), as the BERT normalizer in Hugging Face's tokenizers does (the one
    /// sentence-transformers runs): first every character of category C except \t \n \r is dropped
    /// (so \v, \f and U+0085 go), then what is left of Unicode's White_Space (including U+2028 and
    /// U+2029) becomes a space.
    static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var r in text.EnumerateRunes())
        {
            if (r.Value == 0 || r.Value == 0xFFFD) continue;
            if (r.Value is '\t' or '\n' or '\r') { sb.Append(' '); continue; }
            if (IsControl(r)) continue;
            if (Rune.IsWhiteSpace(r)) { sb.Append(' '); continue; }
            sb.Append(r.ToString());
        }
        var lower = sb.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var out_ = new StringBuilder(lower.Length);
        foreach (var r in lower.EnumerateRunes())
            if (Rune.GetUnicodeCategory(r) != UnicodeCategory.NonSpacingMark) out_.Append(r.ToString());
        return out_.ToString();
    }

    static bool IsControl(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate;

    /// ASCII symbols count as punctuation too ("$", "+", "^"), as in BERT.
    static bool IsPunctuation(Rune r)
    {
        int c = r.Value;
        if (c is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126) return true;
        return Rune.GetUnicodeCategory(r) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    /// CJK ideographs get spaces around them, so each one is its own word.
    static bool IsCjk(int c) =>
        c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2A6DF or >= 0x2A700 and <= 0x2B73F
          or >= 0x2B740 and <= 0x2B81F or >= 0x2B820 and <= 0x2CEAF or >= 0xF900 and <= 0xFAFF or >= 0x2F800 and <= 0x2FA1F;
}
