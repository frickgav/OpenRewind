using OpenRewind;
using Xunit;

public class UpdateCheckTests
{
    const string Release = """
    {
      "tag_name": "v0.3.0", "html_url": "https://github.com/frickgav/openscreen/releases/tag/v0.3.0",
      "draft": false, "prerelease": false,
      "assets": [
        { "name": "OpenRewind-mac.dmg", "browser_download_url": "https://example.com/mac.dmg" },
        { "name": "OpenRewind-windows-x64.zip", "browser_download_url": "https://example.com/x64.zip", "digest": "sha256:abc" }
      ]
    }
    """;

    [Fact]
    public void ReadsTheReleaseForThisPC()
    {
        var r = UpdateCheck.Parse(Release, "OpenRewind-windows-x64.zip");
        Assert.NotNull(r);
        Assert.Equal("0.3.0", r!.Version);
        Assert.Equal("https://example.com/x64.zip", r.AssetUrl);
        Assert.Equal("sha256:abc", r.Digest);
        Assert.Null(UpdateCheck.Parse(Release, "OpenRewind-windows-arm64.zip"));
        Assert.Null(UpdateCheck.Parse(Release.Replace("\"prerelease\": false", "\"prerelease\": true"), "OpenRewind-mac.dmg"));
        Assert.Null(UpdateCheck.Parse("not json", "OpenRewind-mac.dmg"));
    }

    [Fact]
    public void ComparesVersionsByNumber()
    {
        Assert.True(UpdateCheck.IsNewer("0.10.0", "0.9.2"));
        Assert.True(UpdateCheck.IsNewer("v0.2.3", "0.2.2.0"));
        Assert.False(UpdateCheck.IsNewer("0.2.2", "0.2.2.0"));
        Assert.False(UpdateCheck.IsNewer("0.2.2", "0.2.2+abc123"));
        Assert.False(UpdateCheck.IsNewer("0.2.1", "0.2.2"));
        Assert.True(UpdateCheck.IsNewer("1.0", "0.99.99"));
    }

    [Fact]
    public void ChecksTheDownloadAgainstItsDigest()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "hello");
            const string sha = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
            Assert.True(UpdateCheck.Verify(f, "sha256:" + sha));
            Assert.True(UpdateCheck.Verify(f, "sha256:" + sha.ToUpperInvariant()));
            Assert.False(UpdateCheck.Verify(f, "sha256:" + new string('0', 64)));
            Assert.True(UpdateCheck.Verify(f, null));
        }
        finally { File.Delete(f); }
    }
}
