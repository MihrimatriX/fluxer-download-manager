using System.IO.Compression;
using Fluxer.Core.Automation;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class ExtractorTests : IDisposable
{
    // Made with zip -P, py7zr (encrypted headers) and Python's tarfile/zipfile; password "secret".
    private const string Enc7z = "N3q8ryccAASaV04R0AAAAAAAAAAqAAAAAAAAAMgKOcm7TEwIKSWI1X//J9CavuWTau96YzdYKGl0ExzMQrl2Jkc5sBRSwAre2rgaKKj3diwxfsEMSU64xca4AqSkUjrSvQJcGDWgHjdwDj27uwnu/nEc7YNJd2pUaHNr6KblyoMJDHZjq+/bfMGZl0k5wlNjkgqs/AVdO44C+8+ilXhvEOB2iCByamv0S8SxDbVMc73T0Cny/9BR2f5hT0r4KCes7wHMimeVZrvjvewLTR2DGyIGAm7kQWIm6awBUgduxyjuvux9giJqMficINtKdQ2XFwYQAQmAwAAHCwEAASQG8QcBElMPU14d3Uow3PZ9+JeKK5HoaQyAtwAA";
    private const string EncZip = "UEsDBAoACQAAAAB5XV0gMDo2EgAAAAYAAAAKABwAZG9jcy9hLnR4dFVUCQADUGHjalBh42p1eAsAAQQAAAAABAAAAAAGC3xIrzFVDcjufY8tXPRRHUhQSwcIIDA6NhIAAAAGAAAAUEsDBAoACQAAAAB5XV2oYTjdEgAAAAYAAAAKABwAZG9jcy9iLnR4dFVUCQADUGHjalBh42p1eAsAAQQAAAAABAAAAAAYSwVfwNMhNE+Aml+OPeMJEexQSwcIqGE43RIAAAAGAAAAUEsBAh4DCgAJAAAAAHldXSAwOjYSAAAABgAAAAoAGAAAAAAAAQAAAKSBAAAAAGRvY3MvYS50eHRVVAUAA1Bh42p1eAsAAQQAAAAABAAAAABQSwECHgMKAAkAAAAAeV1dqGE43RIAAAAGAAAACgAYAAAAAAABAAAApIFmAAAAZG9jcy9iLnR4dFVUBQADUGHjanV4CwABBAAAAAAEAAAAAFBLBQYAAAAAAgACAKAAAADMAAAAAAA=";
    private const string SlipZip = "UEsDBBQAAAAAAAB5XV2DFtyMAQAAAAEAAAALAAAALi4vZXZpbC50eHR4UEsDBBQAAAAAAAB5XV0VJtv7AQAAAAEAAAAGAAAAb2sudHh0eVBLAQIUAxQAAAAAAAB5XV2DFtyMAQAAAAEAAAALAAAAAAAAAAAAAACAAQAAAAAuLi9ldmlsLnR4dFBLAQIUAxQAAAAAAAB5XV0VJtv7AQAAAAEAAAAGAAAAAAAAAAAAAACAASoAAABvay50eHRQSwUGAAAAAAIAAgBtAAAATwAAAAAA";
    private const string TarGz = "H4sICFBh42oC/2RhdGEudGFyAO3VPQrCMAAF4MyeIidIkzRJ6yA4OnqFaAMKLYE2Yo9vq4MgtE4N2rxv6S9keu+xjGX7o+0PzlauJYvgL1NXznP1vh/fCy6FJLQnEdy6YNvheJImWdImXBu3E8U2l6VRJWe5VEZLtSGwepU/d9nCZ4yhLrSezv9wL7QsuDFcy2f+tSoI1THz33of5v779v1PMfQ/+h/9n3b/Wxb6BcttDLVRaqb/zUf/D0+KUB4z/4n2/8XVtUfSk4X9x/5j/xPf/9MP7r/G/sdw921dIekAAAAAAAAAAACr9ABom6zVACgAAA==";
    private const string Small7z = "N3q8ryccAATMlBAhwwAAAAAAAAAVAAAAAAAAAETXHTjgIPoARF0AOZhK9Uws0YSxRLMiMLpZQxRioRv6mWQx7AArTtC8XTFnoJr3RP7mssKmqR0zM+zaqo43WvVwRjgRxfKZy085n/ZRWAAA4ACMAG9dAACBMweuD9KN0v1Au5RkHH7rYmP2uZeSKuQg6aUJAdwRjpArSIxkVUH80GhVmPb6EcoP1dS+GW9LKc4NJ1vJ7WHzI/+/4EmK42dk/Vqu6pycIMEFxEmZqWohXfx41kJWRxaZU2ij3I0Pn42y/aAAAAAXBkwBCXcABwsBAAEhIQEYDICNAAA=";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fluxer-extract-" + Guid.NewGuid().ToString("N"));

    public ExtractorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Put(string name, string base64) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, Convert.FromBase64String(base64)); return p; }
    private string Touch(string name) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, []); return p; }
    private string[] Listing() => [.. Directory.EnumerateFileSystemEntries(_dir).Select(Path.GetFileName).Order()!];

    private string Zip(string name, params string[] entries)
    {
        var p = Path.Combine(_dir, name);
        using var z = ZipFile.Open(p, ZipArchiveMode.Create);
        foreach (var e in entries)
            using (var w = new StreamWriter(z.CreateEntry(e).Open())) w.Write("içerik " + e);
        return p;
    }

    [Fact]
    public void Describe_KnowsThePartNamingSchemes()
    {
        foreach (var f in new[] { "movie.part1.rar", "movie.part2.rar", "movie.part3.rar", "old.rar", "old.r00", "old.r01", "split.zip", "split.z01", "split.z02", "big.7z.001", "big.7z.002" })
            Touch(f);

        var rar = Extractor.Describe(Path.Combine(_dir, "movie.part2.rar"))!;
        Assert.Equal("movie.rar", rar.Key);
        Assert.Equal(["movie.part1.rar", "movie.part2.rar", "movie.part3.rar"], rar.Parts.Select(Path.GetFileName));
        Assert.True(rar.Complete);

        var old = Extractor.Describe(Path.Combine(_dir, "old.r01"))!;
        Assert.Equal(["old.rar", "old.r00", "old.r01"], old.Parts.Select(Path.GetFileName));
        Assert.Equal("old.rar", Extractor.Describe(Path.Combine(_dir, "old.rar"))!.Key);

        var zip = Extractor.Describe(Path.Combine(_dir, "split.z02"))!;
        Assert.Equal(["split.zip", "split.z01", "split.z02"], zip.Parts.Select(Path.GetFileName));
        Assert.Equal("big.7z", Extractor.Describe(Path.Combine(_dir, "big.7z.002"))!.Key);

        Assert.Equal("data.tar.gz", Extractor.Describe(Path.Combine(_dir, "data.tar.gz"))?.Key);
        Assert.Null(Extractor.Describe(Path.Combine(_dir, "notes.txt")));
        Assert.Null(Extractor.Describe(Path.Combine(_dir, "photo.jpg")));
    }

    [Fact]
    public void Describe_AGapOrAMissingLead_IsIncomplete()
    {
        Touch("a.part1.rar"); Touch("a.part3.rar");
        Touch("b.r00");
        Touch("c.z01");
        Assert.False(Extractor.Describe(Path.Combine(_dir, "a.part3.rar"))!.Complete);
        Assert.False(Extractor.Describe(Path.Combine(_dir, "b.r00"))!.Complete);
        Assert.False(Extractor.Describe(Path.Combine(_dir, "c.z01"))!.Complete);
    }

    [Fact]
    public void OneFolderInside_LandsAsThatFolder_WithoutReplacingAnything()
    {
        var zip = Zip("docs.zip", "docs/a.txt", "docs/b.txt");
        var r = Extractor.Extract(Extractor.Describe(zip)!, [], CancellationToken.None);
        Assert.Equal(Extractor.Outcome.Done, r.Outcome);
        Assert.Equal(Path.Combine(_dir, "docs"), r.Folder);
        Assert.Equal(2, r.Files);
        Assert.Equal("içerik docs/a.txt", File.ReadAllText(Path.Combine(_dir, "docs", "a.txt")));

        var again = Extractor.Extract(Extractor.Describe(zip)!, [], CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "docs (2)"), again.Folder);
    }

    [Fact]
    public void SeveralItemsInside_GoIntoAFolderNamedAfterTheArchive()
    {
        var zip = Zip("pack.zip", "a.txt", "b/c.txt");
        var r = Extractor.Extract(Extractor.Describe(zip)!, [], CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "pack"), r.Folder);
        Assert.True(File.Exists(Path.Combine(_dir, "pack", "b", "c.txt")));
        Assert.Equal(["pack", "pack.zip"], Listing());
    }

    [Theory]
    [InlineData("enc.zip", nameof(EncZip))]
    [InlineData("enc.7z", nameof(Enc7z))]
    public void Encrypted_TriesThePasswords_AndLeavesNothingBehindWithoutOne(string name, string fixture)
    {
        var path = Put(name, (string)typeof(ExtractorTests).GetField(fixture, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!);
        var set = Extractor.Describe(path)!;

        Assert.Equal(Extractor.Outcome.NeedsPassword, Extractor.Extract(set, [], CancellationToken.None).Outcome);
        Assert.Equal(Extractor.Outcome.NeedsPassword, Extractor.Extract(set, ["wrong"], CancellationToken.None).Outcome);
        Assert.Equal([name], Listing());

        var r = Extractor.Extract(set, ["wrong", "secret"], CancellationToken.None);
        Assert.Equal(Extractor.Outcome.Done, r.Outcome);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(_dir, "docs", "a.txt")));
    }

    [Fact]
    public void PathOutsideTheFolder_IsRefused()
    {
        var path = Put("slip.zip", SlipZip);
        var r = Extractor.Extract(Extractor.Describe(path)!, [], CancellationToken.None);
        Assert.Equal(Extractor.Outcome.Failed, r.Outcome);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.txt")));
        Assert.Equal(["slip.zip"], Listing());
    }

    [Fact]
    public void TarGzipped()
    {
        var r = Extractor.Extract(Extractor.Describe(Put("data.tar.gz", TarGz))!, [], CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "docs"), r.Folder);
        Assert.Equal("world\n", File.ReadAllText(Path.Combine(_dir, "docs", "b.txt")));
    }

    [Fact]
    public void A7zSplitIntoNumberedParts()
    {
        var bytes = Convert.FromBase64String(Small7z);
        for (var i = 0; i < 3; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"notes.7z.00{i + 1}"), bytes[(i * bytes.Length / 3)..((i + 1) * bytes.Length / 3)]);
        var set = Extractor.Describe(Path.Combine(_dir, "notes.7z.003"))!;
        Assert.True(set.Complete);
        var r = Extractor.Extract(set, [], CancellationToken.None);
        Assert.Equal(Extractor.Outcome.Done, r.Outcome);
        Assert.Equal("ikinci dosya\n", File.ReadAllText(Path.Combine(_dir, "notes", "b.txt")));
        Assert.Equal("notes", Extractor.ArchiveName(set));
    }
}
