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
