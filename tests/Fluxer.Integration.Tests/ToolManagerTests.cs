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
        var tools = new ToolManager(Path.Combine(h.Dir, "tools"), http);
        Directory.CreateDirectory(tools.Directory);

        await tools.InstallAsync(new ToolManager.Tool("yt-dlp", server.Url(), server.BaseUrl + "/good.sums", "yt-dlp.exe", []), CancellationToken.None);
        Assert.True(File.Exists(tools.YtDlpPath));

        File.Delete(tools.YtDlpPath);
        await Assert.ThrowsAsync<ToolVerificationException>(() =>
            tools.InstallAsync(new ToolManager.Tool("yt-dlp", server.Url(), server.BaseUrl + "/bad.sums", "yt-dlp.exe", []), CancellationToken.None));
        Assert.False(File.Exists(tools.YtDlpPath));
        Assert.Empty(Directory.GetFiles(tools.Directory)); // the unverified download is gone too
    }
}
