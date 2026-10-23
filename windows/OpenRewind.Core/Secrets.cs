using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenRewind;

/// Finds secrets in screen text before it is saved: API keys and tokens, private keys, card numbers and
/// whatever follows a "password:" label. Matching text becomes [redacted] and the words are blurred in the
/// screenshot (see Recorder). The Mac app has the same rules in Secrets.swift; keep the two in step.
/// Kept strict on purpose: ordinary words, dates, phone numbers and short numbers never match.
public static class Secrets
{
    public const string Mask = "[redacted]";

    /// One secret in a text: where it starts, how long it is, and which rule found it.
    public readonly record struct Hit(int Start, int Length, string Kind)
    {
        public int End => Start + Length;
    }

    const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// Rules whose whole match is the secret.
    static readonly (string Kind, Regex Re)[] whole =
    {
        ("private key", new Regex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----[\s\S]*?(-----END [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----|\z)", Opts)),
        ("api key", new Regex(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_\-]{20,}", Opts)),                   // OpenAI, Anthropic
        ("api key", new Regex(@"(?<![A-Za-z0-9])(sk|rk)_(live|test)_[A-Za-z0-9]{16,}", Opts)),       // Stripe secret keys
        ("github token", new Regex(@"(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{30,}", Opts)),
        ("github token", new Regex(@"(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{40,}", Opts)),
        ("slack token", new Regex(@"(?<![A-Za-z0-9])xox[abposre]-[A-Za-z0-9\-]{10,}", Opts)),
        ("aws key", new Regex(@"(?<![A-Za-z0-9])(AKIA|ASIA)[0-9A-Z]{16}(?![A-Za-z0-9])", Opts)),
        ("google key", new Regex(@"(?<![A-Za-z0-9])AIza[0-9A-Za-z_\-]{35}(?![A-Za-z0-9_\-])", Opts)),
        ("jwt", new Regex(@"(?<![A-Za-z0-9])eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", Opts)),
    };

    /// Rules where only the `v` group is the secret: what follows a label.
    static readonly Regex labeled = new(
        @"(?i)(?<![A-Za-z])(password|passwd|passcode|pwd|api[ _\-]?key|secret[ _\-]?key|(?:aws[ _\-]?)?secret[ _\-]?access[ _\-]?key|access[ _\-]?key|account[ _\-]?key|private[ _\-]?key|client[ _\-]?secret|access[ _\-]?token|auth[ _\-]?token|refresh[ _\-]?token|bearer)[ \t]*[:=][ \t]*[""']?(?<v>[^\s""';]{4,})",
        Opts);
    /// Environment variables and settings named like a secret: FOO_SECRET=…, GITHUB_TOKEN=…, STRIPE_KEY=….
    static readonly Regex envVar = new(
        @"(?<![A-Za-z0-9_])[A-Z][A-Z0-9_]*_(SECRET|TOKEN|KEY|PASSWORD|PASS|PWD|APIKEY|CREDENTIALS?)[ \t]*[:=][ \t]*[""']?(?<v>[^\s""';]{8,})", Opts);
    /// A password inside an address in text: postgres://user:pass@host, https://me:pw@example.com.
    static readonly Regex userinfo = new(@"(?<![A-Za-z0-9+.\-])[a-zA-Z][a-zA-Z0-9+.\-]*://[^/@:\s]+:(?<v>[^/@\s]{3,})@", Opts);
    static readonly Regex bearer = new(@"(?<![A-Za-z])Bearer[ \t]+(?<v>[A-Za-z0-9._~+/\-]{20,}=*)", Opts);

    /// 13 to 19 digits, maybe in groups split by single spaces or dashes.
    static readonly Regex digits = new(@"(?<![\d\-.,/:])\d(?:[ \-]?\d){12,18}(?![\d\-]|[.,/:]\d)", Opts);

    /// Long runs of letters, digits and token punctuation, checked for randomness.
    /// '/' may be inside (base64: AWS and Azure keys) but not first; IsRandom keeps paths out.
    static readonly Regex token = new(@"(?<![A-Za-z0-9+_\-/.])[A-Za-z0-9+_\-][A-Za-z0-9+/_\-]{31,}={0,2}(?![A-Za-z0-9+/_=\-])", Opts);

    /// Every secret in `text`, in order, without overlaps.
    public static List<Hit> Find(string text)
    {
        var hits = new List<Hit>();
        if (string.IsNullOrEmpty(text)) return hits;
        foreach (var (kind, re) in whole)
            foreach (Match m in re.Matches(text)) hits.Add(new Hit(m.Index, m.Length, kind));
        foreach (var re in new[] { labeled, bearer, envVar, userinfo })
            foreach (Match m in re.Matches(text))
            {
                var v = m.Groups["v"];
                if (!LooksLikeValue(v.Value)) continue;
                // SORT_KEY=timestamp: a plain word is a setting, not a secret.
                if (re == envVar && Regex.IsMatch(v.Value, "^[a-z_.]+$")) continue;
                hits.Add(new Hit(v.Index, v.Length, "password"));
            }
        foreach (Match m in digits.Matches(text))
            if (IsCardNumber(m.Value)) hits.Add(new Hit(m.Index, m.Length, "card number"));
        foreach (Match m in token.Matches(text))
            if (IsRandom(m.Value)) hits.Add(new Hit(m.Index, m.Length, "token"));
        return Merge(hits);
    }

    /// True when `text` has a secret in it.
    public static bool Has(string text) => Find(text).Count > 0;

    /// `text` with every secret replaced by [redacted].
    public static string Redact(string text)
    {
        var hits = Find(text);
        if (hits.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        int at = 0;
        foreach (var h in hits)
        {
            sb.Append(text, at, h.Start - at).Append(Mask);
            at = h.End;
        }
        return sb.Append(text, at, text.Length - at).ToString();
    }

    /// Query parameters that carry secrets; their values are always hidden.
    static readonly HashSet<string> secretParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "refresh_token", "id_token", "auth", "auth_token", "api_key", "apikey", "key",
        "secret", "client_secret", "password", "pass", "pwd", "sig", "signature", "code", "session", "sessionid",
        "sid", "x-amz-signature", "x-amz-credential", "x-amz-security-token", "otp",
    };

    /// A web address with secrets taken out: a password before the host, the values of secret query
    /// parameters (token=, key=, sig=…) and anything the text rules find.
    public static string RedactUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var s = Regex.Replace(url, @"^([a-zA-Z][a-zA-Z0-9+.\-]*://[^/@:\s]+):[^/@\s]+@", "$1:" + Mask + "@");
        s = Regex.Replace(s, @"([?&#])([^=&#\s]+)=([^&#\s]*)", m =>
            secretParams.Contains(Uri.UnescapeDataString(m.Groups[2].Value)) && m.Groups[3].Length > 0
                ? m.Groups[1].Value + m.Groups[2].Value + "=" + Mask : m.Value);
        return Redact(s);
    }

    /// Which of `words` (one line after another, as OCR reads them) are part of a secret, by index.
    /// Words of a line are joined with spaces and lines with new lines, so a card number read as four
    /// words or a key over several lines is still found.
    public static HashSet<int> WordsToHide(IReadOnlyList<(string Text, int Line)> words)
    {
        var hide = new HashSet<int>();
        if (words.Count == 0) return hide;
        var sb = new StringBuilder();
        var starts = new int[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            if (i > 0) sb.Append(words[i].Line != words[i - 1].Line ? '\n' : ' ');
            starts[i] = sb.Length;
            sb.Append(words[i].Text);
        }
        var hits = Find(sb.ToString());
        if (hits.Count == 0) return hide;
        for (int i = 0; i < words.Count; i++)
        {
            int a = starts[i], b = a + words[i].Text.Length;
            foreach (var h in hits)
                if (a < h.End && h.Start < b) { hide.Add(i); break; }
        }
        return hide;
    }

    /// The boxes (fractions of the picture) of the words in `text` that are part of a secret.
    public static List<RectangleF> Boxes(FrameText text)
    {
        var words = text.Words;
        var hide = WordsToHide(words.Select(w => (w.Text, w.Line)).ToList());
        return hide.OrderBy(i => i).Select(i => words[i].Box).ToList();
    }

    /// Card numbers: 13–19 digits that pass the Luhn check, start like a real card (Visa, Mastercard,
    /// Amex, Discover, JCB, Diners, UnionPay) and are either one run or in groups of four (Amex 4-6-5).
    public static bool IsCardNumber(string s)
    {
        var groups = s.Split(' ', '-');
        var d = string.Concat(groups);
        if (d.Length < 13 || d.Length > 19 || !d.All(char.IsAsciiDigit)) return false;
        if (groups.Length > 1)
        {
            var sizes = string.Join(",", groups.Select(g => g.Length));
            bool fours = groups.Take(groups.Length - 1).All(g => g.Length == 4) && groups[^1].Length is >= 1 and <= 4;
            if (!fours && sizes != "4,6,5" && sizes != "4,6,4") return false;
        }
        if (d.Distinct().Count() < 2) return false;  // 0000…
        if (!(d[0] is '3' or '4' or '5' or '6' || d.StartsWith("22") || d.StartsWith("23") || d.StartsWith("24")
              || d.StartsWith("25") || d.StartsWith("26") || d.StartsWith("27"))) return false;
        if (d[0] == '3' && !(d.StartsWith("34") || d.StartsWith("37") || d.StartsWith("35") || d.StartsWith("36") || d.StartsWith("30") || d.StartsWith("38") || d.StartsWith("39"))) return false;
        return Luhn(d);
    }

    public static bool Luhn(string digits)
    {
        int sum = 0;
        bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (dbl) { n *= 2; if (n > 9) n -= 9; }
            sum += n;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    /// A long string that looks machine-made: at least 32 characters with upper and lower case letters and
    /// digits mixed, and close to random (Shannon entropy). Hex hashes and ids (one case only), file names
    /// and CamelCase words (no digits) never match.
    public static bool IsRandom(string s)
    {
        if (s.Length < 32) return false;
        int upper = 0, lower = 0, digit = 0;
        foreach (var c in s)
        {
            if (char.IsAsciiLetterUpper(c)) upper++;
            else if (char.IsAsciiLetterLower(c)) lower++;
            else if (char.IsAsciiDigit(c)) digit++;
        }
        // Base64 with '/' or '+' (AWS and Azure keys) may happen to have fewer digits.
        bool base64 = s.Length >= 40 && (s.Contains('/') || s.Contains('+'));
        if (upper < 2 || lower < 2 || (digit < 2 && !base64)) return false;
        // Words joined by dashes or underscores ("my-long-branch-name-2024-v2") are not random.
        var parts = s.Split('-', '_');
        if (parts.Length > 3 && parts.All(p => p.Length < 12)) return false;
        // Paths ("Users/sam/Projects/App2024/Sources"): a word between slashes, or "//".
        if (s.Contains('/') && (s.Contains("//") || s.Split('/').Any(p => Regex.IsMatch(p, "^[A-Z]?[a-z]{3,}$")))) return false;
        return Entropy(s) >= 4.2;
    }

    /// Bits per character.
    public static double Entropy(string s)
    {
        if (s.Length == 0) return 0;
        var counts = new Dictionary<char, int>();
        foreach (var c in s) counts[c] = counts.GetValueOrDefault(c) + 1;
        double e = 0;
        foreach (var n in counts.Values)
        {
            double p = (double)n / s.Length;
            e -= p * Math.Log2(p);
        }
        return e;
    }

    /// What follows "password:" is a value, not more of the sentence ("Password: required", "pwd = ****").
    static bool LooksLikeValue(string v)
    {
        if (v.All(c => c is '*' or '•' or '●' or '·' or '.')) return false;
        var lower = v.ToLowerInvariant().TrimEnd('.', ',', ';', ')');
        return lower is not ("required" or "optional" or "hidden" or "none" or "null" or "undefined" or "true" or "false"
            or "forgot" or "reset" or "change" or "show" or "enter" or "your" or "must" or "should" or "cannot" or "needs"
            or "[redacted]");
    }

    /// Sorted, with overlapping hits joined.
    static List<Hit> Merge(List<Hit> hits)
    {
        hits.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var out_ = new List<Hit>();
        foreach (var h in hits)
        {
            if (out_.Count > 0 && h.Start < out_[^1].End)
            {
                var last = out_[^1];
                if (h.End > last.End) out_[^1] = last with { Length = h.End - last.Start };
                continue;
            }
            out_.Add(h);
        }
        return out_;
    }
}
