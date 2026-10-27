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
        ("webhook secret", new Regex(@"(?<![A-Za-z0-9])whsec_[A-Za-z0-9+/=]{20,}", Opts)),           // Stripe, Svix
        ("gitlab token", new Regex(@"(?<![A-Za-z0-9])glpat-[A-Za-z0-9_\-]{20,}", Opts)),
        ("npm token", new Regex(@"(?<![A-Za-z0-9])npm_[A-Za-z0-9]{36,}", Opts)),
        ("hugging face token", new Regex(@"(?<![A-Za-z0-9])hf_[A-Za-z0-9]{30,}", Opts)),
        ("pypi token", new Regex(@"(?<![A-Za-z0-9])pypi-AgE[A-Za-z0-9_\-]{20,}", Opts)),
        ("sendgrid key", new Regex(@"(?<![A-Za-z0-9])SG\.[A-Za-z0-9_\-]{16,32}\.[A-Za-z0-9_\-]{16,64}", Opts)),
        ("shopify token", new Regex(@"(?<![A-Za-z0-9])shp(at|ss|ca|pa)_[a-fA-F0-9]{32}", Opts)),
        ("slack token", new Regex(@"(?<![A-Za-z0-9])xapp-[A-Za-z0-9\-]{10,}", Opts)),
        ("digitalocean token", new Regex(@"(?<![A-Za-z0-9])do[opr]_v1_[a-fA-F0-9]{32,}", Opts)),
        ("google token", new Regex(@"(?<![A-Za-z0-9])ya29\.[A-Za-z0-9_\-]{20,}", Opts)),
        ("facebook token", new Regex(@"(?<![A-Za-z0-9])EAA[A-Za-z0-9]{40,}", Opts)),
    };

    /// Names of secrets, for a value with no ':' or '=' after them ("Signing secret  whsec_…", or the value
    /// on the next line) and for long hex values near them. Longer names first.
    const string Names = @"signing[ _\-]?secret|webhook[ _\-]?secret|signing[ _\-]?key|shared[ _\-]?secret|app[ _\-]?secret"
        + @"|consumer[ _\-]?secret|encryption[ _\-]?key|client[ _\-]?secret|secret[ _\-]?access[ _\-]?key|secret[ _\-]?key"
        + @"|auth[ _\-]?token|api[ _\-]?key|access[ _\-]?token|refresh[ _\-]?token|private[ _\-]?key|password|token|secret";

    /// A name, then the value after spaces or on the next line (one line at most), maybe past a Reveal or
    /// Copy button. The value counts only when it looks like a key (LooksLikeKey).
    static readonly Regex near = new(
        @"(?i)(?<![A-Za-z])(?:" + Names + @")(?![A-Za-z])(?<sep>[ \t]*[:=]?(?:[ \t]+(?:reveal|show|copy|hide|view)(?![A-Za-z]))?(?:[ \t]*\r?\n)?[ \t]*)[""']?(?<v>[A-Za-z0-9_+/=\-]{6,})",
        Opts);
    /// Hex values (32 to 128 digits, maybe split in two by OCR) are secrets only near a name, so commit
    /// hashes and checksums on their own stay. Twilio keys: SK + 32 hex digits.
    static readonly Regex hexRun = new(@"(?<![A-Za-z0-9_\-])(?:SK)?[0-9a-fA-F]{8,130}(?: [0-9a-fA-F]{8,130})?(?![A-Za-z0-9_\-])", Opts);
    static readonly Regex hexName = new(@"(?i)(?<![A-Za-z])(?:" + Names + @"|account[ _\-]?sid|twilio)(?![A-Za-z])", Opts);
    static readonly Regex keyChars = new(@"^[A-Za-z0-9_+/=\-]+$", Opts);
    static readonly Regex pathWord = new(@"^[A-Z]?[a-z]{3,}$", Opts);

    /// Rules where only the `v` group is the secret: what follows a label.
    static readonly Regex labeled = new(
        @"(?i)(?<![A-Za-z])(password|passwd|passcode|pwd|api[ _\-]?key|signing[ _\-]?secret|webhook[ _\-]?secret|signing[ _\-]?key|shared[ _\-]?secret|app[ _\-]?secret|consumer[ _\-]?secret|encryption[ _\-]?key|secret[ _\-]?key|(?:aws[ _\-]?)?secret[ _\-]?access[ _\-]?key|access[ _\-]?key|account[ _\-]?key|private[ _\-]?key|client[ _\-]?secret|access[ _\-]?token|auth[ _\-]?token|refresh[ _\-]?token|bearer)[ \t]*[:=][ \t]*[""']?(?<v>[^\s""';]{4,})",
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
            foreach (Match m in re.Matches(text))
            {
                // OCR may read one key as two words: take the second half too.
                int end = kind is "private key" or "jwt" ? m.Index + m.Length : Extend(text, m.Index + m.Length);
                hits.Add(new Hit(m.Index, end - m.Index, kind));
            }
        foreach (Match m in near.Matches(text))
        {
            var v = m.Groups["v"];
            if (m.Groups["sep"].Length == 0) continue;  // "token1234…" is one word
            int end = Extend(text, v.Index + v.Length);
            if (LooksLikeKey(text[v.Index..end])) hits.Add(new Hit(v.Index, end - v.Index, "secret"));
        }
        foreach (Match m in hexRun.Matches(text))
        {
            var hex = m.Value.Replace(" ", "");
            if (hex.StartsWith("SK")) hex = hex[2..];
            if (hex.Length is < 32 or > 130 || hex.Count(char.IsAsciiDigit) < 2 || hex.Count(char.IsAsciiLetter) < 2) continue;
            if (NearName(text, m.Index)) hits.Add(new Hit(m.Index, m.Length, "secret"));
        }
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

    /// A secret's name on the same line before `at`, or on the line above.
    static bool NearName(string text, int at)
    {
        int line = at > 0 ? text.LastIndexOf('\n', at - 1) : -1;
        int from = line > 0 ? text.LastIndexOf('\n', line - 1) + 1 : 0;
        return hexName.IsMatch(text[from..at]);
    }

    /// Where a key ends when OCR split it in two at a space: the next word joins it when it is made of key
    /// characters and has letters and digits (so "Copy", a date or a sentence do not).
    static int Extend(string text, int end)
    {
        if (end + 1 >= text.Length || text[end] != ' ') return end;
        int e = end + 1;
        while (e < text.Length && !char.IsWhiteSpace(text[e])) e++;
        var w = text[(end + 1)..e];
        if (w.Length < 4 || !keyChars.IsMatch(w) || !w.Any(char.IsAsciiDigit) || !w.Any(char.IsAsciiLetter)) return end;
        return e;
    }

    /// A value after a secret's name that is a key, not a word, a button or a masked placeholder: 16 or more
    /// key characters (one space allowed, from OCR), mostly letters and digits with at least two of each,
    /// and not a path or words joined by dashes.
    public static bool LooksLikeKey(string v)
    {
        var s = v.Replace(" ", "");
        if (s.Length < 16 || s.Length > 512 || !keyChars.IsMatch(s)) return false;
        int letters = s.Count(char.IsAsciiLetter), digits = s.Count(char.IsAsciiDigit);
        if (letters < 2 || digits < 2 || letters + digits < s.Length * 0.8) return false;
        var parts = s.Split('-', '_');
        if (parts.Length > 3 && parts.All(p => p.Length < 12)) return false;
        if (s.StartsWith('/') || s.Contains("//") || (s.Contains('/') && s.Split('/').Any(p => pathWord.IsMatch(p)))) return false;
        return true;
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

    /// Known key prefixes as OCR may read them, with the "_" or "-" after them lost or turned into a space.
    static readonly Regex stem = new(@"^(?:whsec|sk|rk|gh[pousr]|github|xox[abposre]|glpat|npm|hf|pypi|shp(?:at|ss|ca|pa)|xapp|do[opr]|ya29|AKIA|ASIA|AIza)$", Opts);
    static readonly Regex nameOnly = new(@"(?i)(?<![A-Za-z])(?:" + Names + @")(?![A-Za-z])", Opts);
    static readonly Regex runWord = new(@"[A-Za-z0-9_+/=.\-]+", Opts);
    /// A known prefix or a secret's name somewhere in rough OCR text: worth a careful read before saving.
    static readonly Regex hint = new(@"(?<![A-Za-z0-9])(?:whsec|sk[-_ ]|rk[-_ ]|gh[pousr][-_ ]|github[-_ ]pat|xox[abposre]|glpat|npm[-_ ]|hf[-_ ]|pypi|shp(?:at|ss|ca|pa)|xapp|do[opr][-_ ]v1|ya29|AKIA|ASIA|AIza|eyJ)", Opts);

    /// True when `text` names a secret or has a key prefix, even one OCR broke up: the Mac reads such a screen
    /// carefully, not quickly, before it looks for what to blur.
    public static bool MightHave(string text) =>
        !string.IsNullOrEmpty(text) && (hint.IsMatch(text) || nameOnly.IsMatch(text) || Has(text));

    /// Secrets in OCR lines that Find misses because OCR put spaces inside a key ("whsec K70w 0aQn…"): a run
    /// of words with one space between them that starts with a known prefix, or that follows a secret's name
    /// (on the same line or the line above) from its first word with letters and digits, and that looks like
    /// a key once the spaces are taken out. Ranges are within each line.
    public static List<(int Line, int Start, int Length)> FindInLines(IReadOnlyList<string> lines)
    {
        var out_ = new List<(int, int, int)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i] ?? "";
            var words = runWord.Matches(line).Cast<Match>().ToList();
            if (words.Count < 2) continue;
            bool named = nameOnly.IsMatch(line) || (i > 0 && nameOnly.IsMatch(lines[i - 1] ?? ""));
            for (int w = 0; w < words.Count;)
            {
                int end = w;
                while (end + 1 < words.Count && words[end + 1].Index == words[end].Index + words[end].Length + 1) end++;
                if (Loose(words.GetRange(w, end - w + 1), named) is (int a, int b)) out_.Add((i, a, b - a));
                w = end + 1;
            }
        }
        return out_;
    }

    /// Start and end of a key inside one run of words, or null.
    static (int, int)? Loose(List<Match> words, bool named)
    {
        int from = words.FindIndex(m => stem.IsMatch(Head(m.Value)));
        if (from < 0 && named) from = words.FindIndex(m => IsMixed(m.Value));
        if (from < 0 || words.Count - from < 2) return null;
        var part = words.Skip(from).Select(m => m.Value).ToList();
        var head = Head(part[0]);
        bool keyish;
        if (stem.IsMatch(head))
        {
            var rest = (part[0].Length > head.Length ? part[0][(head.Length + 1)..] : "") + string.Concat(part.Skip(1));
            keyish = rest.Length >= 16 && LooksLikeKey(rest);
        }
        else keyish = LooksLikeKey(string.Concat(part));
        if (!keyish || !part.Any(p => p.Length >= 4 && IsMixed(p))) return null;
        var last = words[^1];
        return (words[from].Index, last.Index + last.Length);
    }

    /// A word up to its first "_", "-" or ".".
    static string Head(string w)
    {
        int k = w.IndexOfAny(new[] { '_', '-', '.' });
        return k < 0 ? w : w[..k];
    }

    static bool IsMixed(string s) => s.Any(char.IsAsciiDigit) && s.Any(char.IsAsciiLetter);

    /// Where each secret in `secrets` (found in text read another way, such as Accessibility) shows in the
    /// OCR `lines`, even when OCR misread it a little: a stretch of 10 key characters of the secret in the
    /// line, ignoring spaces, case-sensitive but with O/0 and l/1/I taken as the same. Ranges are within each line.
    public static List<(int Line, int Start, int Length)> Locate(IEnumerable<string> secrets, IReadOnlyList<string> lines)
    {
        const int chunk = 10;
        var keys = secrets.Select(Fold).Where(k => k.Length >= chunk).Distinct().ToList();
        var out_ = new List<(int, int, int)>();
        if (keys.Count == 0) return out_;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i] ?? "";
            var sb = new StringBuilder();
            var at = new List<int>();
            for (int c = 0; c < line.Length; c++)
                if (!char.IsWhiteSpace(line[c])) { sb.Append(FoldChar(line[c])); at.Add(c); }
            var folded = sb.ToString();
            int lo = int.MaxValue, hi = -1;
            foreach (var k in keys)
                for (int s = 0; s + chunk <= k.Length; s += chunk / 2)
                {
                    var piece = k.Substring(s, chunk);
                    for (int f = folded.IndexOf(piece, StringComparison.Ordinal); f >= 0; f = folded.IndexOf(piece, f + 1, StringComparison.Ordinal))
                    {
                        lo = Math.Min(lo, at[f]);
                        hi = Math.Max(hi, at[f + chunk - 1] + 1);
                    }
                }
            if (hi > lo) out_.Add((i, lo, hi - lo));
        }
        return out_;
    }

    /// The secrets themselves (not where they are) in `text`.
    public static List<string> Values(string text) => Find(text).Select(h => text.Substring(h.Start, h.Length)).ToList();

    static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (!char.IsWhiteSpace(c)) sb.Append(FoldChar(c));
        return sb.ToString();
    }

    static char FoldChar(char c) => c switch { 'O' => '0', 'o' => '0', 'l' => '1', 'I' => '1', '|' => '1', _ => c };

    /// Which of `words` (one line after another, as OCR reads them) are part of a secret, by index.
    /// Words of a line are joined with spaces and lines with new lines, so a card number read as four
    /// words or a key over several lines is still found. Keys OCR broke up with spaces (FindInLines) count
    /// too, and so do the `known` secrets (from Accessibility text) wherever OCR shows them (Locate).
    public static HashSet<int> WordsToHide(IReadOnlyList<(string Text, int Line)> words, IEnumerable<string>? known = null)
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
        var all = sb.ToString();
        // The same text, line by line, with where each line starts in it.
        var lines = all.Split('\n');
        var lineStart = new int[lines.Length];
        for (int l = 1; l < lines.Length; l++) lineStart[l] = lineStart[l - 1] + lines[l - 1].Length + 1;
        var hits = Find(all).Select(h => (h.Start, h.End)).ToList();
        foreach (var (l, a, n) in FindInLines(lines)) hits.Add((lineStart[l] + a, lineStart[l] + a + n));
        if (known != null)
            foreach (var (l, a, n) in Locate(known, lines)) hits.Add((lineStart[l] + a, lineStart[l] + a + n));
        if (hits.Count == 0) return hide;
        for (int i = 0; i < words.Count; i++)
        {
            int a = starts[i], b = a + words[i].Text.Length;
            foreach (var (hs, he) in hits)
                if (a < he && hs < b) { hide.Add(i); break; }
        }
        return hide;
    }

    /// The boxes (fractions of the picture) of the words in `text` that are part of a secret, also where the
    /// `known` secrets (found in Accessibility text) show.
    public static List<RectangleF> Boxes(FrameText text, IEnumerable<string>? known = null)
    {
        var words = text.Words;
        var hide = WordsToHide(words.Select(w => (w.Text, w.Line)).ToList(), known);
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
        if (s.Contains('/') && (s.Contains("//") || s.Split('/').Any(p => pathWord.IsMatch(p)))) return false;
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
            or "reveal" or "copy" or "view" or "hide" or "regenerate" or "roll" or "generate" or "create" or "revoke"
            or "delete" or "manage" or "edit" or "expired" or "invalid" or "missing" or "[redacted]");
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
