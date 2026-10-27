using OpenRewind;

public class SecretsTests
{
    // Test keys are put together at run time so secret scanners do not flag this file. None is real.
    public static TheoryData<string, string> KnownSecrets() => new()
    {
        { "key " + "sk-" + "proj-AbCdEf0123456789xyzABCDEFG here", "key [redacted] here" },
        { "sk-" + "ant-api03-abcdefghijklmnopqrstuvwxyz012345", "[redacted]" },
        { "token " + "ghp" + "_0123456789abcdefghijABCDEFGHIJ0123 ok", "token [redacted] ok" },
        { "github" + "_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRS", "[redacted]" },
        { "slack " + "xox" + "b-1234567890-abcdefghij", "slack [redacted]" },
        { "AWS " + "AKIA" + "ABCDEFGHIJKLMNOP.", "AWS [redacted]." },
        { "AI" + "za" + "SyA1234567890abcdefghijklmnopqrstuv", "[redacted]" },
        { "stripe " + "sk" + "_live_" + "0000aaaa1111bbbb2222", "stripe [redacted]" },
        { "Authorization: Bearer abcDEF123456789xyzXYZ987654", "Authorization: Bearer [redacted]" },
        { "password: hunter2!", "password: [redacted]" },
        { "PWD=Tr0ub4dor&3 next", "PWD=[redacted] next" },
        { "api_key = \"q8Hk2LmZ\"", "api_key = \"[redacted]\"" },
    };

    [Theory]
    [MemberData(nameof(KnownSecrets))]
    public void RedactsKnownSecrets(string text, string expected) => Assert.Equal(expected, Secrets.Redact(text));

    [Fact]
    public void RedactsJwtsAndPrivateKeys()
    {
        var jwt = "eyJ" + "hbGciOiJIUzI1NiJ9." + "eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        Assert.Equal("id [redacted] end", Secrets.Redact($"id {jwt} end"));
        var pem = "before\n-----BEGIN OPENSSH " + "PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\nQyNTUxOQAAACD\n-----END OPENSSH PRIVATE KEY-----\nafter";
        Assert.Equal("before\n[redacted]\nafter", Secrets.Redact(pem));
        // Cut off by the edge of the screen: everything after the start is hidden.
        Assert.Equal("x [redacted]", Secrets.Redact("x -----BEGIN RSA " + "PRIVATE KEY-----\nMIIEpAIBAAKCAQEA"));
    }

    [Theory]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("4111111111111111")]
    [InlineData("5555 5555 5555 4444")]
    [InlineData("3782 822463 10005")]
    [InlineData("6011111111111117")]
    public void RedactsCardNumbers(string card)
    {
        Assert.Equal("card [redacted] exp", Secrets.Redact($"card {card} exp"));
    }

    [Theory]
    [InlineData("4111 1111 1111 1112")]          // fails Luhn
    [InlineData("1234567812345670")]             // passes Luhn, but no card starts with 1
    [InlineData("+1 (415) 555-2671")]            // phone
    [InlineData("+44 20 7946 0958")]
    [InlineData("2026-10-23 14:05:00")]          // dates and times
    [InlineData("Order 12345 shipped")]           // short numbers
    [InlineData("41 111 11111 11111")]           // odd groups
    [InlineData("0000 0000 0000 0000")]
    [InlineData("Invoice 4111111111111111.50")] // an amount
    public void LeavesOtherNumbersAlone(string text) => Assert.Equal(text, Secrets.Redact(text));

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    [InlineData("commit 3f786850e387550fdab836ed7e6dc881de23001b")]  // git hash: hex, one case
    [InlineData("id 123e4567-e89b-12d3-a456-426614174000")]           // uuid
    [InlineData("AbstractSingletonProxyFactoryBeanConfiguration")]    // no digits
    [InlineData("feature-branch-name-for-the-2024-q3-release-v2")]   // words with dashes
    [InlineData("Forgot password? Reset it here")]
    [InlineData("Password: required")]
    [InlineData("Password: ********")]
    [InlineData("Meet me at 3pm on 10/23/2026, call 555-123-4567")]
    [InlineData("https://docs.google.com/document/d/1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs74OgvE2upms/edit")]
    [InlineData("skeleton sk-short")]
    public void LeavesOrdinaryTextAlone(string text) => Assert.Equal(text, Secrets.Redact(text));

    [Fact]
    public void RedactsLongRandomStrings()
    {
        Assert.Equal("secret [redacted]", Secrets.Redact("secret Zx9Qm2Lp7Wv4Rt8Yb3Nc6Hd1Kf5Gs0Ja"));
        Assert.True(Secrets.IsRandom("Zx9Qm2Lp7Wv4Rt8Yb3Nc6Hd1Kf5Gs0Ja"));
        Assert.False(Secrets.IsRandom("Zx9Qm2Lp7Wv4"));
    }

    [Fact]
    public void RedactingTwiceChangesNothing()
    {
        var once = Secrets.Redact("password: hunter22 and " + "sk-" + "proj-AbCdEf0123456789xyzABCDEFG");
        Assert.Equal(once, Secrets.Redact(once));
    }

    [Fact]
    public void RedactsSecretsInWebAddresses()
    {
        Assert.Equal("https://api.example.com/v1/x?token=[redacted]&page=2",
            Secrets.RedactUrl("https://api.example.com/v1/x?token=abc123&page=2"));
        Assert.Equal("https://user:[redacted]@host.example/a",
            Secrets.RedactUrl("https://user:pa55word@host.example/a"));
        Assert.Equal("https://example.com/search?q=cats", Secrets.RedactUrl("https://example.com/search?q=cats"));
        Assert.Equal("https://s3.example/o?X-Amz-Signature=[redacted]", Secrets.RedactUrl("https://s3.example/o?X-Amz-Signature=deadbeef"));
    }

    [Fact]
    public void FindsWhichWordsToBlur()
    {
        // OCR reads a card number as four words and a label and its value as two.
        var words = new List<(string, int)>
        {
            ("Card", 0), ("4111", 0), ("1111", 0), ("1111", 0), ("1111", 0),
            ("Password:", 1), ("hunter22", 1),
            ("Hello", 2), ("world", 2),
            ("ghp" + "_0123456789abcdefghijABCDEFGHIJ0123", 3),
        };
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 9 }, Secrets.WordsToHide(words).OrderBy(i => i));
    }

    [Fact]
    public void LuhnCheck()
    {
        Assert.True(Secrets.Luhn("79927398713"));
        Assert.False(Secrets.Luhn("79927398710"));
    }
}

/// The cases the Mac CI checks with `features-check` (main.swift), so both apps redact alike.
public class SecretsSharedCases
{
    [Theory]
    [InlineData("password: hunter2!", "password: [redacted]")]
    [InlineData("card 4111 1111 1111 1111 exp", "card [redacted] exp")]
    [InlineData("Meeting at 10:30 on 2026-10-18, call 555-123-4567", "Meeting at 10:30 on 2026-10-18, call 555-123-4567")]
    [InlineData("Forgot password? Reset it here", "Forgot password? Reset it here")]
    public void SameAsTheMacCheck(string input, string want) => Assert.Equal(want, Secrets.Redact(input));

    [Fact]
    public void StripeKeyLikeTheMacCheck() =>
        Assert.Equal("key [redacted]", Secrets.Redact("key " + "sk" + "_live_" + "4eC39HqLyjWDarjtT1zdp7dc"));
}

/// Secret formats with '/' (base64), named like secrets in settings, and passwords inside addresses in text.
public class SecretsMoreFormats
{
    static readonly string awsSecret = "wJalrXUtnFEMI" + "/K7MDENG/" + "bPxRfiCYEXAMPLEKEY";
    static readonly string azureKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsu" + "Fq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "AWS_SECRET_ACCESS_KEY=" + awsSecret, "AWS_SECRET_ACCESS_KEY=[redacted]" },
        new object[] { "aws_secret_access_key = " + awsSecret, "aws_secret_access_key = [redacted]" },
        new object[] { "DefaultEndpointsProtocol=https;AccountName=acme;AccountKey=" + azureKey + ";EndpointSuffix=core.windows.net",
                       "DefaultEndpointsProtocol=https;AccountName=acme;AccountKey=[redacted];EndpointSuffix=core.windows.net" },
        new object[] { "export GITHUB_TOKEN=" + "abc123def456" + "ghi789", "export GITHUB_TOKEN=[redacted]" },
        new object[] { "CLIENT_SECRET: " + "s3cr3t-value-99", "CLIENT_SECRET: [redacted]" },
        new object[] { "db at postgres://app:" + "Tr0ub4dor" + "@db.internal:5432/main", "db at postgres://app:[redacted]@db.internal:5432/main" },
        new object[] { "key " + awsSecret + " end", "key [redacted] end" },
        new object[] { "blob " + azureKey, "blob [redacted]" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Finds(string input, string want) => Assert.Equal(want, Secrets.Redact(input));

    [Theory]
    [InlineData("/Users/sam/Projects/OpenRewind2024/Sources/openrewind/Recorder.swift")]
    [InlineData("Users/Sam/Documents/Invoices2024Q3/Final/Report2/Summary9")]
    [InlineData("https://github.com/frickgav/OpenRewind/pull/57/files")]
    [InlineData("SORT_KEY=timestamp PRIMARY_KEY=id CACHE_KEY=user_profile")]
    [InlineData("see http://localhost:8080/api and mailto:sam@example.com")]
    [InlineData("src/components/Timeline/TimelineView2.swift and Sources/openrewind/AITools.swift")]
    public void LeavesPathsAndSettingsAlone(string text) => Assert.Equal(text, Secrets.Redact(text));
}

/// Secrets named without ':' or '=' (the value after spaces or on the next line), hex keys next to their
/// name, more key prefixes, and OCR quirks. The Mac CI checks the same cases (features-check in main.swift).
public class SecretsNamedAndPrefixed
{
    // Put together at run time so secret scanners do not flag this file. None is real.
    static readonly string hex = "8f14e45fceea167a" + "5a36dedd4bea2543";
    static readonly string whsec = "whsec_" + "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    public static IEnumerable<object[]> Found() => new[]
    {
        new object[] { "Signing Secret  " + hex, "Signing Secret  [redacted]" },
        new object[] { "Signing secret\n" + whsec, "Signing secret\n[redacted]" },
        new object[] { "Webhook secret  " + "Ab3dE5fG7hI9" + "jK1lM3nO5pQ7", "Webhook secret  [redacted]" },
        new object[] { "Client secret\t" + "GOCSPX-" + "1a2b3c4d5e6f7g8h9i0jKLMN", "Client secret\t[redacted]" },
        new object[] { "Secret key " + "Zx9Qm2Lp7Wv4" + "Rt8Yb3", "Secret key [redacted]" },
        new object[] { "Auth token  " + hex, "Auth token  [redacted]" },
        new object[] { "API key " + "AT9x8w7v6u" + "5t4s3r2q1p", "API key [redacted]" },
        new object[] { "Access token " + "Vc1Xa9Rt5Y" + "m2Kp8Lq3Wz7N", "Access token [redacted]" },
        new object[] { "Token\n" + "Vc1Xa9Rt5Y" + "m2Kp8Lq3Wz7N", "Token\n[redacted]" },
        new object[] { "Shared secret Copy " + "Ab3dE5fG7hI9" + "jK1lM3nO5pQ7", "Shared secret Copy [redacted]" },
        new object[] { "App secret: " + hex, "App secret: [redacted]" },
        new object[] { "Consumer secret " + hex, "Consumer secret [redacted]" },
        new object[] { "Signing key " + hex, "Signing key [redacted]" },
        new object[] { "Encryption key (AES-256) " + hex, "Encryption key (AES-256) [redacted]" },
        new object[] { "Auth Token  Reveal\n" + hex + " (hidden)", "Auth Token  Reveal\n[redacted] (hidden)" },
        new object[] { "Account SID " + "AC" + hex, "Account SID [redacted]" },
        new object[] { "API key SID " + "SK" + hex, "API key SID [redacted]" },
        new object[] { "endpoint " + whsec, "endpoint [redacted]" },
        new object[] { "glpat-" + "xYz12AbC34dEf56GhI78", "[redacted]" },
        new object[] { "npm_" + "a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8", "[redacted]" },
        new object[] { "hf_" + "aBcDeFgHiJkLmNoPqRsTuVwXyZ012345", "[redacted]" },
        new object[] { "pypi-AgE" + "IcHlwaS5vcmcCJDEyMzQ1Njc4", "[redacted]" },
        new object[] { "SG." + "aBcDeFgHiJkLmNoPqRsTuV" + "." + "wXyZ0123456789aBcDeFgHiJkLmNoPqRsTuVwXyZ0123", "[redacted]" },
        new object[] { "shpat_" + hex, "[redacted]" },
        new object[] { "xapp-" + "1-A0123456789-0123456789-abcdef", "[redacted]" },
        new object[] { "dop_v1_" + hex + hex, "[redacted]" },
        new object[] { "ya29." + "a0AfH6SMBx1y2z3AbCdEfGh", "[redacted]" },
        new object[] { "EAAG" + "m0PX4ZCpsBAaBcDeFgHiJkLmNoPqRsTuVwXyZ0123", "[redacted]" },
        // OCR read one key as two words.
        new object[] { "whsec_" + "MfKQ9r8GKYqrTwjUPD8I" + " LPZIa1b2c3 done", "[redacted] done" },
        new object[] { "Signing secret " + "8f14e45fceea167a" + " 5a36dedd4bea2543", "Signing secret [redacted]" },
    };

    [Theory]
    [MemberData(nameof(Found))]
    public void Finds(string input, string want) => Assert.Equal(want, Secrets.Redact(input));

    [Theory]
    [InlineData("commit 3f786850e387550fdab836ed7e6dc881de23001b")]
    [InlineData("Merged 3f786850e387550fdab836ed7e6dc881de23001b into main")]
    [InlineData("sha256 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("id 123e4567-e89b-12d3-a456-426614174000")]
    [InlineData("8f14e45fceea167a5a36dedd4bea2543")]
    [InlineData("Signing secret Reveal")]
    [InlineData("Signing secret  Reveal  Copy")]
    [InlineData("API key ••••••••")]
    [InlineData("API key •••••••••••• Reveal")]
    [InlineData("Webhook secret: Reveal")]
    [InlineData("Your access token expires in 30 days")]
    [InlineData("Keep the secret recipe safe until 2026")]
    [InlineData("The token was revoked on 2026-10-25")]
    [InlineData("API key rotation is scheduled for Q4 2026")]
    [InlineData("Token count: 1532 tokens used")]
    [InlineData("Access token\nSettings")]
    [InlineData("password\n/Users/sam/Projects/App2024/Sources")]
    [InlineData("Create a token for the release-2026-q4-build pipeline")]
    public void LeavesThisAlone(string text) => Assert.Equal(text, Secrets.Redact(text));

    // OCR quirks: a quick read turned "whsec_K70w…" into "whsec K70w… fcHU… j PP" (fake key).
    static HashSet<int> Hidden(string[] lines, IEnumerable<string>? known = null) =>
        Secrets.WordsToHide(lines.SelectMany((l, i) => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => (w, i))).ToList(), known);

    [Theory]
    [InlineData("whsec K70w0aQnRtYp fcHUzhwq8vMPsnF0G j PP")]
    [InlineData("whsec_K70w0aQnRtYp fcHUzhwq8vMPsnF0G")]
    [InlineData("ghp 1a2B3c4D5e6F7g8H9i0J kLmNoPqRsTuVwXyZ12")]
    [InlineData("sk proj 4fG7hJ2kL9mN3pQ8 rS1tU6vW0xY5zA")]
    public void HidesKeysOcrBrokeUp(string line)
    {
        var hide = Hidden(new[] { "Settings", line });
        Assert.DoesNotContain(0, hide);
        Assert.Contains(1, hide);     // the prefix word
        Assert.Contains(2, hide);     // the next piece
    }

    [Fact]
    public void HidesBrokenKeyUnderItsName()
    {
        var hide = Hidden(new[] { "Signing Secret", "K70w0aQnRtYp fcHUzhwq8vMPsnF0G" });
        Assert.Contains(2, hide);
        Assert.Contains(3, hide);
        Assert.DoesNotContain(0, hide);
    }

    [Theory]
    [InlineData("Password", "Must be 8 characters with 2 numbers")]
    [InlineData("API key", "Created on Oct 25 2026 by Gavien")]
    [InlineData("Settings", "npm install react-dom typescript")]
    [InlineData("Settings", "github.com/frickgav/OpenRewind pull 57")]
    [InlineData("Token", "Expires in 30 days")]
    [InlineData("Meeting notes", "sk 2 items left for Q4 review")]
    public void LeavesOrdinaryLinesAlone(string first, string second) => Assert.Empty(Hidden(new[] { first, second }));

    [Fact]
    public void FindsKnownSecretWhereOcrMisreadIt()
    {
        // Accessibility had the key; OCR read 0 as O, l as 1, and broke it up.
        const string key = "whsec_" + "K70w0aQnRtYplfcHUzhwq8vMPsnF0G";
        var hide = Hidden(new[] { "Webhooks", "whsec K7Ow0aQnRtYp1fcHU zhwq8vMPsnFOG", "Done" }, new[] { key });
        Assert.Contains(1, hide);
        Assert.Contains(2, hide);
        Assert.Contains(3, hide);
        Assert.DoesNotContain(0, hide);
        Assert.DoesNotContain(4, hide);
    }

    [Fact]
    public void KnownSecretNotOnScreenHidesNothing() =>
        Assert.Empty(Hidden(new[] { "README", "Install with brew" }, new[] { "whsec_" + "K70w0aQnRtYplfcHUzhwq8vMPsnF0G" }));

    [Theory]
    [InlineData("Signing Secret ••••••••", true)]
    [InlineData("whsec K70w0aQn", true)]
    [InlineData("Weekly report draft", false)]
    public void MightHave(string text, bool want) => Assert.Equal(want, Secrets.MightHave(text));
}
