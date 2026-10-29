using Fluxer.Core.Automation;
using Fluxer.Core.Downloads;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class DownloadHooksTests
{
    private static DownloadRecord Record(string name = "a b.zip") => new()
    {
        Id = 7, FileName = name, SaveDirectory = @"D:\Downloads\Archives", Category = "Archives", TotalBytes = 1234,
        Http = new HttpInfo { Url = "https://example.org/a%20b.zip" },
    };

    [Fact]
    public void Tokenize_QuotesGroupAndDisappear()
    {
        Assert.Equal(["C:\\Program Files\\x.exe", "--in", "%F", "two words"],
            DownloadHooks.Tokenize("\"C:\\Program Files\\x.exe\" --in \"%F\"   \"two words\""));
        Assert.Empty(DownloadHooks.Tokenize("   "));
        Assert.Equal(["a", ""], DownloadHooks.Tokenize("a \"\""));
    }

    [Fact]
    public void Expand_FillsEveryPlaceholder()
    {
        var r = Record();
        Assert.Equal(r.FullPath + @"|a b.zip|D:\Downloads\Archives|Archives|1234|https://example.org/a%20b.zip|completed|7",
            DownloadHooks.Expand("%F|%N|%D|%K|%Z|%U|%E|%I", r, HookEvent.Completed));
    }

    [Fact]
    public void Build_PassesValuesAsSeparateArgumentsAndEnvironment()
    {
        var r = Record("x&y.zip");
        var psi = DownloadHooks.Build("tool.exe --file %F --size %Z", r, HookEvent.Failed, out var refused);
        Assert.Null(refused);
        Assert.NotNull(psi);
        Assert.Equal("tool.exe", psi.FileName);
        Assert.Equal(["--file", r.FullPath, "--size", "1234"], psi.ArgumentList);
        Assert.Equal("failed", psi.Environment["FLUXER_EVENT"]);
        Assert.Equal("x&y.zip", psi.Environment["FLUXER_NAME"]);
    }

    [Fact]
    public void Build_RefusesCmdMetacharactersForBatchFiles()
    {
        Assert.Null(DownloadHooks.Build(@"C:\s\move.cmd %F", Record("x&calc.zip"), HookEvent.Completed, out var refused));
        Assert.Equal("unsafe-batch-argument", refused);
        Assert.NotNull(DownloadHooks.Build(@"C:\s\move.cmd %F", Record("plain.zip"), HookEvent.Completed, out _));
        // The script can still read everything from the environment.
        Assert.NotNull(DownloadHooks.Build(@"C:\s\move.bat", Record("x&calc.zip"), HookEvent.Completed, out _));
    }

    [Fact]
    public void Build_RunsPowerShellScriptsThroughPowerShell()
    {
        var psi = DownloadHooks.Build(@"C:\s\after.ps1 %N", Record(), HookEvent.Completed, out _)!;
        Assert.Equal("powershell.exe", psi.FileName);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\s\after.ps1", "a b.zip"], psi.ArgumentList);
    }

    [Fact]
    public void Payload_UsesTheFinalUrlWhenKnown()
    {
        var r = Record();
        r.Http!.FinalUrl = "https://cdn.example.org/a.zip";
        var p = DownloadHooks.Payload(r, HookEvent.Completed, "1.0.0");
        Assert.Equal(("completed", "https://cdn.example.org/a.zip", "http"), (p.Event, p.Url, p.Kind));
    }
}

public sealed class ListTransferTests
{
    [Fact]
    public void Json_RoundTripsEveryKindWithoutCookies()
    {
        var http = new DownloadRecord
        {
            Kind = DownloadKind.Http, FileName = "a.zip", SaveDirectory = "D:/dl", Category = "Archives", Tags = ["work"], TotalBytes = 10,
            State = DownloadState.Completed, CompletedAt = 5,
            Http = new HttpInfo { Url = "https://example.org/a.zip", Cookies = "sid=secret", Mirrors = ["https://m.example.org/a.zip"] },
        };
        var torrent = new DownloadRecord
        {
            Kind = DownloadKind.Torrent, FileName = "t", State = DownloadState.Downloading,
            Torrent = new TorrentInfo { InfoHash = "ABC", Magnet = "magnet:?xt=urn:btih:ABC", TorrentFile = [1, 2, 3] },
        };
        var media = new DownloadRecord { Kind = DownloadKind.Media, FileName = "v", Media = new MediaInfo { PageUrl = "https://video.example.org/w", AudioOnly = true } };

        var json = Storage.ListTransfer.ToJson([http, torrent, media]);
        Assert.DoesNotContain("secret", json);
        var back = Storage.ListTransfer.ParseExport(json)!.Items.Select(Storage.ListTransfer.ToRecord).ToList();

        Assert.Equal((DownloadKind.Http, DownloadState.Completed, "https://example.org/a.zip", null), (back[0]!.Kind, back[0]!.State, back[0]!.Http!.Url, back[0]!.Http!.Cookies));
        Assert.Equal(["work"], back[0]!.Tags);
        Assert.Equal(["https://m.example.org/a.zip"], back[0]!.Http!.Mirrors);
        Assert.Equal((DownloadState.Paused, "ABC"), (back[1]!.State, back[1]!.Torrent!.InfoHash));
        Assert.Equal([1, 2, 3], back[1]!.Torrent!.TorrentFile!);
        Assert.True(back[2]!.Media!.AudioOnly);
    }

    [Fact]
    public void Text_IsOneLinkPerLine_AndParsesBack()
    {
        var a = new DownloadRecord { Http = new HttpInfo { Url = "https://example.org/1" } };
        var b = new DownloadRecord { Torrent = new TorrentInfo { InfoHash = "X", Magnet = "magnet:?xt=urn:btih:X" } };
        var text = Storage.ListTransfer.ToText([a, b]);
        Assert.Null(Storage.ListTransfer.ParseExport(text));
        Assert.Equal(["https://example.org/1", "magnet:?xt=urn:btih:X"], Storage.ListTransfer.ParseLinks(text + "not a link\nftp://x\n"));
    }

    [Fact]
    public void ParseExport_RejectsOtherJson() => Assert.Null(Storage.ListTransfer.ParseExport("{\"items\":[]}"));

    [Theory]
    [InlineData("daily", 0, true)]
    [InlineData("daily", 3600, false)]
    [InlineData("weekly", 2 * 86400, false)]
    [InlineData("weekly", 8 * 86400, true)]
    [InlineData("off", 0, false)]
    public void Backups_AreDueAfterTheirInterval(string interval, long agoSeconds, bool due)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var last = agoSeconds == 0 && interval != "off" ? 0 : now.ToUnixTimeSeconds() - agoSeconds;
        Assert.Equal(due, Storage.Backups.Due(interval, last, now));
    }
}

public sealed class DatabaseBackupTests
{
    [Fact]
    public void Tags_Persist_AndABackupOpensWithTheSameRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluxer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var db = new Storage.Database(Path.Combine(dir, "fluxer.db")))
            {
                db.Insert(new DownloadRecord { Kind = DownloadKind.Http, FileName = "a", SaveDirectory = dir, Tags = ["x", "y"], Http = new HttpInfo { Url = "https://e.org/a" } });
                Assert.Equal(["x", "y"], db.LoadAll()[0].Tags);
                var backup = Storage.Backups.Create(db, dir, new DateTime(2026, 10, 29, 12, 0, 0));
                using var copy = new Storage.Database(backup);
                Assert.Equal("https://e.org/a", copy.LoadAll().Single().Http!.Url);
            }
            Assert.Single(Storage.Backups.List(dir));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

public sealed class UrlPatternTests
{
    [Fact]
    public void ZeroPaddedRange_KeepsTheWidth()
    {
        var urls = Http.UrlPattern.Expand("https://e.org/foto_[001-120].jpg", out var n)!;
        Assert.Equal(120, n);
        Assert.Equal(("https://e.org/foto_001.jpg", "https://e.org/foto_120.jpg"), (urls[0], urls[^1]));
    }

    [Fact]
    public void PlainRange_StepLettersAndProduct()
    {
        Assert.Equal(["p1", "p2", "p3"], Http.UrlPattern.Expand("p[1-3]", out _)!);
        Assert.Equal(["0", "5", "10"], Http.UrlPattern.Expand("[0-10:5]", out _)!);
        Assert.Equal(["3", "2", "1"], Http.UrlPattern.Expand("[3-1]", out _)!);
        Assert.Equal(["a1", "a2", "b1", "b2"], Http.UrlPattern.Expand("[a-b][1-2]", out _)!);
        Assert.Equal(["X", "Y"], Http.UrlPattern.Expand("[X-Y]", out _)!);
    }

    [Fact]
    public void NotARange_StaysNull()
    {
        Assert.Null(Http.UrlPattern.Expand("http://[::1]:8080/a", out _));
        Assert.Null(Http.UrlPattern.Expand("https://e.org/q?a[]=1", out _));
        Assert.Null(Http.UrlPattern.Expand("https://e.org/[a-Z]", out _));
        Assert.False(Http.UrlPattern.Has("https://e.org/plain.zip"));
    }

    [Fact]
    public void TooMany_ReturnsEmptyWithTheCount()
    {
        var urls = Http.UrlPattern.Expand("[1-99999999]", out var n)!;
        Assert.Empty(urls);
        Assert.Equal(99999999, n);
        Assert.Empty(Http.UrlPattern.Expand("[1-100][1-100]", out var m)!);
        Assert.True(m > Http.UrlPattern.Max);
    }
}

public sealed class CategoryTests
{
    private static Downloads.AppSettings Settings()
    {
        var s = new Downloads.AppSettings { DownloadDirectory = "D:/dl" };
        s.CategoryList.Insert(0, new Downloads.Category { Key = "Courses", Sites = ["udemy.com", "*.coursera.org"] });
        s.CategoryList.Insert(1, new Downloads.Category { Key = "Work PDFs", Sites = ["intranet.example"], Extensions = ["pdf"], Folder = "E:/work" });
        return s;
    }

    [Fact]
    public void SiteRulesWin_ThenExtensions()
    {
        var s = Settings();
        Assert.Equal("Courses", s.CategoryFor("lesson.mp4", "https://www.udemy.com/x/lesson.mp4"));
        Assert.Equal("Courses", s.CategoryFor("notes.pdf", "https://api.coursera.org/n.pdf"));
        Assert.Equal("Work PDFs", s.CategoryFor("report.pdf", "https://intranet.example/r.pdf"));
        Assert.Equal("Documents", s.CategoryFor("report.docx", "https://intranet.example/r.docx"));
        Assert.Equal("Video", s.CategoryFor("clip.MP4", "https://notudemy.com/clip.mp4"));
        Assert.Null(s.CategoryFor("readme", null));
        Assert.Null(s.CategoryFor("x.torrent"));
    }

    [Fact]
    public void Folders_FollowTheSwitchUnlessTheCategoryHasItsOwn()
    {
        var s = Settings();
        Assert.Equal(Path.Combine("D:/dl", "Video"), s.FolderFor("Video"));
        Assert.Equal("E:/work", s.FolderFor("Work PDFs"));
        s.UseCategoryFolders = false;
        Assert.Equal("D:/dl", s.FolderFor("Video"));
        Assert.Equal("E:/work", s.FolderFor("Work PDFs"));
        Assert.Equal(Path.Combine("D:/dl", "Video"), s.CategoryFolder("Video"));
        Assert.Equal("D:/dl", s.FolderFor(null));
    }

    [Fact]
    public void ParseList_NormalisesExtensionsAndSites()
    {
        Assert.Equal(["mp4", "mkv", "webm"], Downloads.Category.ParseList(" .MP4, mkv;*.webm  mkv", extensions: true));
        Assert.Equal(["github.com", "*.example.org"], Downloads.Category.ParseList("GitHub.com *.example.org", extensions: false));
    }

    [Fact]
    public void OldSettings_WithoutCategoryList_GetTheDefaults()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<Downloads.AppSettings>("""{"Categories":{"Video":["mp4"]},"Theme":"Light"}""")!;
        Assert.Equal(Downloads.Category.BuiltIn, s.CategoryList.Select(c => c.Key));
    }
}
