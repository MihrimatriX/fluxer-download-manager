using System.IO.Compression;
using System.Security.Cryptography;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Xunit;

namespace Fluxer.Integration.Tests;

public sealed class ToolManagerTests
{
    [Fact]
    public async Task VerifiedTool_IsInstalled_AndTamperedOne_IsRejectedAndRemoved()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 300_000;
        server.FileName = "yt-dlp.exe";
        var good = Convert.ToHexString(server.ExpectedSha256());
        var bad = new string('0', 64);
        server.Texts["/good.sums"] = $"{bad}  other.exe\n{good}  yt-dlp.exe\n";
        server.Texts["/bad.sums"] = $"{bad}  yt-dlp.exe\n";

        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http) { VerifyRetryDelay = TimeSpan.Zero };
        Directory.CreateDirectory(tools.Directory);

        await tools.InstallAsync(new ToolManager.Tool("yt-dlp", server.Url(), server.BaseUrl + "/good.sums", "yt-dlp.exe", []), CancellationToken.None);
        Assert.True(File.Exists(tools.YtDlpPath));

        File.Delete(tools.YtDlpPath);
        await Assert.ThrowsAsync<ToolVerificationException>(() =>
            tools.InstallAsync(new ToolManager.Tool("yt-dlp", server.Url(), server.BaseUrl + "/bad.sums", "yt-dlp.exe", []), CancellationToken.None));
        Assert.False(File.Exists(tools.YtDlpPath));
        Assert.Equal(["tools.json"], Files(tools)); // the unverified download is gone too
    }

    [Fact]
    public async Task ChecksumFromANewerRelease_IsRetriedOnce()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 300_000;
        server.FileName = "yt-dlp.exe";
        var good = Convert.ToHexString(server.ExpectedSha256());
        server.Texts["/sums"] = $"{new string('0', 64)}  yt-dlp.exe\n";

        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http) { VerifyRetryDelay = TimeSpan.Zero };
        Directory.CreateDirectory(tools.Directory);
        // The release flips while the first attempt downloads, as when "latest" moves between the two requests.
        tools.Progress += (_, _) => server.Texts["/sums"] = $"{good}  yt-dlp.exe\n";

        await tools.InstallAsync(new ToolManager.Tool("yt-dlp", server.Url(), server.BaseUrl + "/sums", "yt-dlp.exe", []), CancellationToken.None);
        Assert.True(File.Exists(tools.YtDlpPath));
        Assert.Equal(2, server.Requests);
    }

    [Fact]
    public async Task Update_DownloadsOnlyWhenThePublishedHashChanges()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 300_000;
        server.FileName = "yt-dlp.exe";
        void Publish() => server.Texts["/sums"] = $"{Convert.ToHexString(server.ExpectedSha256())}  yt-dlp.exe\n";
        Publish();
        var tool = new ToolManager.Tool("yt-dlp", server.Url(), server.BaseUrl + "/sums", "yt-dlp.exe", []);

        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http);
        Directory.CreateDirectory(tools.Directory);
        await tools.InstallAsync(tool, CancellationToken.None);

        Assert.Empty(await tools.UpdateAsync([tool], CancellationToken.None));
        Assert.Equal(1, server.Requests);

        server.Version = 2;
        Publish();
        Assert.Equal(["yt-dlp"], await tools.UpdateAsync([tool], CancellationToken.None));
        Assert.Equal(server.ExpectedSha256(), SHA256.HashData(File.ReadAllBytes(tools.YtDlpPath)));
        Assert.Equal(["tools.json", "yt-dlp.exe"], Files(tools));

        // Several media downloads failing together ask at once; they share one check and one download.
        server.Version = 3;
        Publish();
        var first = tools.UpdateAsync([tool], CancellationToken.None);
        var second = tools.UpdateAsync([tool], CancellationToken.None);
        Assert.Same(first, second);
        Assert.Equal(["yt-dlp"], await first);
        Assert.Equal(3, server.Requests);
    }

    [Fact]
    public async Task Update_TrustsAnExeInstalledBeforeTheManifest_WhenItsHashMatches()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 300_000;
        server.FileName = "yt-dlp.exe";
        server.Texts["/sums"] = $"{Convert.ToHexString(server.ExpectedSha256())}  yt-dlp.exe\n";

        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http);
        Directory.CreateDirectory(tools.Directory);
        File.WriteAllBytes(tools.YtDlpPath, Enumerable.Range(0, 300_000).Select(i => FakeServer.ByteAt(i, 1)).ToArray());

        Assert.Empty(await tools.UpdateAsync([new ToolManager.Tool("yt-dlp", server.Url(), server.BaseUrl + "/sums", "yt-dlp.exe", [])], CancellationToken.None));
        Assert.Equal(0, server.Requests);
        Assert.Contains("yt-dlp", File.ReadAllText(Path.Combine(tools.Directory, "tools.json")));
    }

    [Fact]
    public async Task Update_LeavesAYoungZipInstallAlone()
    {
        await using var server = await FakeServer.StartAsync();
        server.Blobs["/a.zip"] = Zip("ffmpeg.exe");
        server.Texts["/sums"] = $"{Sha(server.Blobs["/a.zip"])}  a.zip\n";
        var tool = new ToolManager.Tool("ffmpeg", server.BaseUrl + "/a.zip", server.BaseUrl + "/sums", "a.zip", ["ffmpeg.exe"], MinUpdateAge: TimeSpan.FromDays(30));

        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http);
        Directory.CreateDirectory(tools.Directory);
        await tools.InstallAsync(tool, CancellationToken.None);

        server.Texts["/sums"] = $"{new string('1', 64)}  a.zip\n"; // a newer daily build
        Assert.Empty(await tools.UpdateAsync([tool], CancellationToken.None));
    }

    [Fact]
    public async Task StalledDownload_FailsAsStalled_AndLeavesNothingBehind()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 300_000;
        server.FileName = "yt-dlp.exe";
        server.RateFor = _ => 1; // first chunk, then silence
        server.Texts["/sums"] = $"{Convert.ToHexString(server.ExpectedSha256())}  yt-dlp.exe\n";

        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http) { StallTimeout = TimeSpan.FromSeconds(1) };
        Directory.CreateDirectory(tools.Directory);

        await Assert.ThrowsAsync<ToolStalledException>(() =>
            tools.InstallAsync(new ToolManager.Tool("yt-dlp", server.Url(), server.BaseUrl + "/sums", "yt-dlp.exe", []), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(tools.Directory));
    }

    [Fact]
    public async Task CancelledDownload_IsACancellation_NotAStall()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 300_000;
        server.FileName = "yt-dlp.exe";
        server.RateFor = _ => 1;
        server.Texts["/sums"] = $"{Convert.ToHexString(server.ExpectedSha256())}  yt-dlp.exe\n";

        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http) { StallTimeout = TimeSpan.FromSeconds(30) };
        Directory.CreateDirectory(tools.Directory);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tools.InstallAsync(new ToolManager.Tool("yt-dlp", server.Url(), server.BaseUrl + "/sums", "yt-dlp.exe", []), cts.Token));
        Assert.Empty(Directory.GetFiles(tools.Directory));
    }

    [Fact]
    public async Task Zip_IsInstalledOnlyWhenEveryEntryIsThere()
    {
        await using var server = await FakeServer.StartAsync();
        server.Blobs["/full.zip"] = Zip("bin/ffmpeg.exe", "bin/ffprobe.exe");
        server.Blobs["/partial.zip"] = Zip("bin/ffmpeg.exe");
        server.Texts["/sums"] = $"{Sha(server.Blobs["/full.zip"])}  full.zip\n{Sha(server.Blobs["/partial.zip"])}  partial.zip\n";

        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http);
        Directory.CreateDirectory(tools.Directory);
        string[] exes = ["ffmpeg.exe", "ffprobe.exe"];

        // An archive without ffprobe must not leave ffmpeg.exe behind looking installed.
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            tools.InstallAsync(new ToolManager.Tool("ffmpeg", server.BaseUrl + "/partial.zip", server.BaseUrl + "/sums", "partial.zip", exes), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(tools.Directory));

        await tools.InstallAsync(new ToolManager.Tool("ffmpeg", server.BaseUrl + "/full.zip", server.BaseUrl + "/sums", "full.zip", exes), CancellationToken.None);
        Assert.Equal([.. exes, "tools.json"], Files(tools));
        Assert.Equal("ffprobe.exe", File.ReadAllText(Path.Combine(tools.Directory, "ffprobe.exe")));
    }

    [Fact]
    public void IsInstalled_RequiresFfprobe()
    {
        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http);
        Directory.CreateDirectory(tools.Directory);
        foreach (var f in new[] { "yt-dlp.exe", "deno.exe", "ffmpeg.exe" }) File.WriteAllText(Path.Combine(tools.Directory, f), "");
        Assert.False(tools.IsInstalled);
        File.WriteAllText(Path.Combine(tools.Directory, "ffprobe.exe"), "");
        Assert.True(tools.IsInstalled);
    }

    [Fact]
    public async Task SignedChecksums_AreRequired_WhenAToolHasAKey()
    {
        await using var server = await FakeServer.StartAsync();
        server.Size = 300_000;
        server.FileName = "yt-dlp.exe";
        // TestSums names the hash of FakeServer's 300 000-byte version-1 file; TestSig is a detached
        // signature over it by TestKey (made with gpg for this test).
        server.Texts["/sums"] = TestSums;
        server.Blobs["/sums.sig"] = Convert.FromBase64String(TestSig);
        server.Blobs["/other.sig"] = Convert.FromBase64String(TestSig)[..^1];

        using var h = new Harness();
        using var http = new HttpEngine();
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http) { VerifyRetryDelay = TimeSpan.Zero };
        Directory.CreateDirectory(tools.Directory);
        ToolManager.Tool Signed(string sig) => new("yt-dlp", server.Url(), server.BaseUrl + "/sums", "yt-dlp.exe", [],
            SignatureUrl: server.BaseUrl + sig, PublicKey: TestKey);

        await Assert.ThrowsAsync<ToolVerificationException>(() => tools.InstallAsync(Signed("/other.sig"), CancellationToken.None));
        Assert.Equal(0, server.Requests); // refused before the exe was downloaded
        await tools.InstallAsync(Signed("/sums.sig"), CancellationToken.None);
        Assert.True(File.Exists(tools.YtDlpPath));
    }

    private const string TestSums = "EA46857AAC9BE70DCA728B9CBC8D6C6FC4118A4D40B6F73559ACCE607A39D85B  yt-dlp.exe\n";
    private const string TestSig = "iQEzBAABCgAdFiEENWMUlxfRt8ySuS88X3xrSZ1HoT8FAmrUvnkACgkQX3xrSZ1HoT+bDwgAtvdt6Oh4zinoKcmYl8WW9hR2YvYPArwUPzX6qMVDzI0M1UhSi5lnc533HPbhcrfohDGCu7ZKfSx13yUn/VzXXs3IWbhsio45Z2LEGODZkIIz9mWlJSExiquFAiUKVyQPR1j88wd3l/wweL3OuScl56nFcruJanlxooGGfZADZ7KKPZjv1n1zXhJEFuoc+aX5rvEJWr3BGGVzEMaxldr7EN+Ll2LeFsWSBrml7qdgChzQRM1LxScqlSx+xEkBMka0SeKc4y2nH1zto2orW2tPaA4Xk/66yYttbFtCDC2Kr8Jsv+Y2RGZCqTOdFtCisEGZl7cTi+RkCJHAM9pvwYtMNw==";
    private const string TestKey = """
        -----BEGIN PGP PUBLIC KEY BLOCK-----

        mQENBGrUvnkBCADUFyhR4J7b96j0YaIC773w0HTXW9b8keI39H/Jlu6z/mn/eozJ
        xhkU8h3kgLI5Nz2u0rQMapBRH13uWPok+RD+SB4cYtgsCmaFcrdFg6KdRrSI/hYZ
        gTf+ROL1XnxKHtk6oHPiy4tc1Ai8DT1HeSbDo7rYZymZXgUDKbVUysUQM/3g59PI
        ilvYGsbz5ZhjILOOPpG7TP3s9IMlXzMuokCC2TTlgn+TthRTEzv+XTMfjilSV58S
        V2eQ9h6ie0kTnkdFd1lENkwB/67pCwFZUDgOPVGXsgqFfC+G4G51FSzSHew+XT8u
        BB34BDuVSzbolJt381Q91xnvIpMSBO0RQy7xABEBAAG0IkZsdXhlciB0ZXN0IDx0
        ZXN0QGV4YW1wbGUuaW52YWxpZD6JAVEEEwEKADsWIQQ1YxSXF9G3zJK5LzxffGtJ
        nUehPwUCatS+eQIbAwULCQgHAgIiAgYVCgkICwIEFgIDAQIeBwIXgAAKCRBffGtJ
        nUehP0esB/98ur3KPdAGXNthr60jCV78z6nyn2U9TeZhbBWrod8/YY6I2IS8r1E0
        WkEX2aFafd5738YHyqK6Wv990za/ygwat9Yn4r8dbjd6FEuNkYmAgRsKrOzYjFqP
        ihxROu9y5QaY66jWcTc2c4Fa3Sk6X1RDmE3K+X+Nw7XT7bho+bSheCA15ghcvofx
        h7hWvlGC3YexIjEbbUyng/C2IehSpC5hFT41zf7u9glM7EAL8dCwojhO0xyEnC49
        lmcBi28bnWPmXmqWkSLVfWpEs/3aG74UhmaRaA9EAi8Zrqje+c3HyzvsjXoeTNEh
        6zwEdpVqtS25hES9oG9qIvHmIWd7XAVW
        =XHye
        -----END PGP PUBLIC KEY BLOCK-----
        """;

    private static byte[] Zip(params string[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(e).Open());
                w.Write(Path.GetFileName(e));
            }
        return ms.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string[] Files(ToolManager tools) => [.. Directory.GetFiles(tools.Directory).Select(f => Path.GetFileName(f)!).Order()];
}
