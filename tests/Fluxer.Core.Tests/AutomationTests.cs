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
