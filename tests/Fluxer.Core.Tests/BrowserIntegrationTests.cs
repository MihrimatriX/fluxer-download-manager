using System.Text.Json;
using Fluxer.Core.Platform.Windows;
using Fluxer.Ipc;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class BrowserIntegrationTests
{
    private const string ChromeId = "dckkomckipcaglacbgfmbgmapfckpkja";
    private const string FirefoxId = "fluxer@fluxer-download-manager";

    [Fact]
    public void AllowedCaller_Chrome_ReadsOriginArgument()
    {
        Assert.Equal(ChromeId, Protocol.AllowedCaller([$"chrome-extension://{ChromeId}/"]));
        // Chrome on Windows also passes --parent-window=<hwnd> before or after the origin.
        Assert.Equal(ChromeId, Protocol.AllowedCaller(["--parent-window=0", $"chrome-extension://{ChromeId}/"]));
        Assert.Null(Protocol.AllowedCaller(["chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/"]));
    }

    [Fact]
    public void AllowedCaller_Firefox_ReadsAddonIdAfterManifestPath()
    {
        Assert.Equal(FirefoxId, Protocol.AllowedCaller([@"C:\Users\u\AppData\Local\Fluxer\com.fluxer.host.firefox.json", FirefoxId]));
        Assert.Null(Protocol.AllowedCaller([@"C:\x\com.fluxer.host.firefox.json", "evil@example.com"]));
        Assert.Null(Protocol.AllowedCaller([FirefoxId])); // the ID must come second, after the manifest path
        Assert.Null(Protocol.AllowedCaller([]));
    }

    [Fact]
    public void AllowedCaller_ChromeOriginWins_SoAFirefoxIdCannotBeSmuggledIn()
    {
        Assert.Null(Protocol.AllowedCaller(["chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", FirefoxId]));
    }

    [Fact]
    public void HostManifest_UsesTheAllowListKeyEachBrowserReads()
    {
        using var chrome = JsonDocument.Parse(ShellIntegration.HostManifest(@"C:\F\host.exe", "allowed_origins", [$"chrome-extension://{ChromeId}/"]));
        using var firefox = JsonDocument.Parse(ShellIntegration.HostManifest(@"C:\F\host.exe", "allowed_extensions", Protocol.AllowedFirefoxIds));

        Assert.Equal(ShellIntegration.HostName, firefox.RootElement.GetProperty("name").GetString());
        Assert.Equal("stdio", firefox.RootElement.GetProperty("type").GetString());
        Assert.Equal(@"C:\F\host.exe", firefox.RootElement.GetProperty("path").GetString());
        Assert.Equal(FirefoxId, firefox.RootElement.GetProperty("allowed_extensions")[0].GetString());
        Assert.False(firefox.RootElement.TryGetProperty("allowed_origins", out _));
        Assert.Equal($"chrome-extension://{ChromeId}/", chrome.RootElement.GetProperty("allowed_origins")[0].GetString());
    }
}
