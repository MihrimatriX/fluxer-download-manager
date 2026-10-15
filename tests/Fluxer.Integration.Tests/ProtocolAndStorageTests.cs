using System.Text;
using System.Text.Json;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Fluxer.Core.Storage;
using Fluxer.Ipc;
using Xunit;

namespace Fluxer.Integration.Tests;

/// 0.7: popup messages (batch, command, rich status) and the media referrer that captured streams need.
public sealed class ProtocolAndStorageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fluxer-proto-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Media_referrer_survives_a_restart_and_migrations_run_once()
    {
        var path = Path.Combine(_dir, "fluxer.db");
        long id;
        using (var db = new Database(path))
        {
            var r = new DownloadRecord
            {
                Kind = DownloadKind.Media, FileName = "clip", SaveDirectory = _dir,
                Media = new MediaInfo { PageUrl = "https://cdn.example.com/master.m3u8", Title = "clip", Referrer = "https://example.com/watch/1" },
            };
            db.Insert(r);
            id = r.Id;
        }
        // Reopening runs the migration check again: must not fail on the already-added column.
        using (var db = new Database(path))
        {
            var back = db.LoadAll().Single(x => x.Id == id);
            Assert.Equal("https://example.com/watch/1", back.Media!.Referrer);
            Assert.Equal("https://cdn.example.com/master.m3u8", back.Media.PageUrl);
        }
    }

    [Fact]
    public void Ytdlp_gets_the_referrer_only_when_there_is_one()
    {
        using var http = new HttpEngine();
        var tools = new ToolManager(_dir, http);
        var with = YtDlp.BaseStartInfo(tools, null, "https://example.com/watch/1").ArgumentList.ToList();
        var i = with.IndexOf("--referer");
        Assert.True(i >= 0 && with[i + 1] == "https://example.com/watch/1");
        Assert.DoesNotContain("--referer", YtDlp.BaseStartInfo(tools, null).ArgumentList);
    }

    private static Envelope Parse(string json) => Envelope.Parse(Encoding.UTF8.GetBytes(json))!;

    [Fact]
    public void Batch_message_from_the_extension_parses()
    {
        var msg = Parse("""
            {"protocolVersion":1,"id":"a","type":"download.batch","payload":{
              "pageUrl":"https://example.com/files","referrer":"https://example.com/files","userAgent":"UA","incognito":false,
              "cookies":[{"name":"s","value":"1","domain":"example.com","path":"/","secure":true,"httpOnly":true}],
              "items":[{"url":"https://example.com/a.zip"},{"url":"https://cdn.example.com/b.pdf","fileName":"b.pdf"}]}}
            """);
        Assert.Equal(MessageTypes.DownloadBatch, msg.Type);
        var b = msg.PayloadAs<DownloadBatch>()!;
        Assert.Equal(2, b.Items.Length);
        Assert.Equal("b.pdf", b.Items[1].FileName);
        Assert.Null(b.Items[0].FileName);
        Assert.Equal("s", b.Cookies![0].Name);
    }

    [Theory]
    [InlineData("""{"action":"pause","id":42}""", "pause", 42L)]
    [InlineData("""{"action":"pauseAll"}""", "pauseAll", null)]
    public void Command_message_parses(string payload, string action, long? id)
    {
        var c = Parse($$"""{"protocolVersion":1,"id":"a","type":"command","payload":{{payload}}}""").PayloadAs<Command>()!;
        Assert.Equal(action, c.Action);
        Assert.Equal(id, c.Id);
        Assert.Contains(action, CommandActions.PerDownload.Concat(CommandActions.Global));
    }

    [Fact]
    public void Status_reply_is_camel_case_and_keeps_old_fields()
    {
        var reply = Envelope.Create("a", MessageTypes.Status, new StatusReply(1, 2, 300, [new StatusItem("x.bin", 0.5, 300)],
            [new StatusEntry(7, "x.bin", "downloading", "http", 0.5, 300, 50, 100), new StatusEntry(8, "y", "queued", "media", 0, 0, 0, null)]));
        using var doc = JsonDocument.Parse(reply.ToBytes());
        var p = doc.RootElement.GetProperty("payload");
        Assert.Equal(1, p.GetProperty("active").GetInt32());
        Assert.Equal("x.bin", p.GetProperty("top")[0].GetProperty("name").GetString());   // what 0.6 extensions read
        var first = p.GetProperty("items")[0];
        Assert.Equal(7, first.GetProperty("id").GetInt64());
        Assert.Equal("downloading", first.GetProperty("state").GetString());
        Assert.Equal(100, first.GetProperty("totalBytes").GetInt64());
        Assert.False(p.GetProperty("items")[1].TryGetProperty("totalBytes", out _));  // unknown size: omitted, not 0
    }
}
