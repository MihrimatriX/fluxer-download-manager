using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Stats;
using Xunit;

namespace Fluxer.Core.Tests;

public sealed class SiteRuleTests
{
    [Fact]
    public void SiteRuleFor_PicksTheMostSpecificPattern()
    {
        var s = new AppSettings
        {
            Sites = [new SiteRule { Host = "example.org", MaxConnections = 4 }, new SiteRule { Host = "*.cdn.example.org", MaxConnections = 1 }],
        };
        Assert.Equal(1, s.SiteRuleFor("a.cdn.example.org")?.MaxConnections);
        Assert.Equal(4, s.SiteRuleFor("www.example.org")?.MaxConnections);
        Assert.Equal(4, s.SiteRuleFor("EXAMPLE.org")?.MaxConnections);
        Assert.Null(s.SiteRuleFor("notexample.org"));
        Assert.Null(s.SiteRuleFor(null));
    }

    [Fact]
    public void NewRequest_RuleUserAgentWins_RefererOnlyFillsAGap()
    {
        var rule = new SiteRule { Host = "example.org", UserAgent = "Rule/1", Referrer = "https://example.org/" };
        var ctx = new RequestContext(null, "Browser/1", null);
        using var a = HttpEngine.NewRequest(HttpMethod.Get, "https://example.org/f", ctx, rule);
        Assert.Equal("Rule/1", string.Join(" ", a.Headers.UserAgent));
        Assert.Equal("https://example.org/", a.Headers.Referrer?.ToString());

        using var b = HttpEngine.NewRequest(HttpMethod.Get, "https://example.org/f", ctx with { Referrer = "https://page.test/" }, rule);
        Assert.Equal("https://page.test/", b.Headers.Referrer?.ToString());

        using var c = HttpEngine.NewRequest(HttpMethod.Get, "https://example.org/f", ctx, null);
        Assert.Equal("Browser/1", string.Join(" ", c.Headers.UserAgent));
        Assert.Null(c.Headers.Authorization);
    }

    [Fact]
    public void Login_IsSentAsBasicAuthorization()
    {
        // The password is DPAPI-encrypted, which exists only on Windows; the user name alone works everywhere.
        var rule = new SiteRule { Host = "example.org", UserName = "ali" };
        using var req = HttpEngine.NewRequest(HttpMethod.Get, "https://example.org/f", null, rule);
        Assert.Equal("Basic " + Convert.ToBase64String("ali:"u8.ToArray()), req.Headers.Authorization?.ToString());
        if (!OperatingSystem.IsWindows()) return;
        rule.Password = "gizli:şifre";
        Assert.Equal("gizli:şifre", rule.Password);
        Assert.Equal("Basic " + Convert.ToBase64String("ali:gizli:şifre"u8.ToArray()), rule.Authorization());
    }

    [Fact]
    public void Password_FromAnotherMachineOrAGarbledFile_IsJustMissing()
    {
        Assert.Null(new SiteRule { PasswordProtected = "not base64!" }.Password);
    }
}

public sealed class TransferMeterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyGrowthCounts_FromTheFirstSighting()
    {
        var m = new TransferMeter();
        m.Observe([(1, 500, 0)], T0);                            // already had 500 when first seen
        Assert.Equal(0, m.DownWithin(TimeSpan.FromHours(1), T0));
        m.Observe([(1, 800, 0)], T0.AddSeconds(2));
        m.Observe([(1, 100, 0)], T0.AddSeconds(4));              // restarted from scratch: no negative
        m.Observe([(1, 300, 0), (2, 50, 0)], T0.AddSeconds(6));
        Assert.Equal(500, m.DownWithin(TimeSpan.FromHours(1), T0.AddSeconds(6)));
        Assert.Equal((500L, 0L), m.TakePending());
        Assert.Equal((0L, 0L), m.TakePending());
    }

    [Fact]
    public void Window_FreesWhenTheOldestCountedMinuteLeavesIt()
    {
        var m = new TransferMeter();
        m.Observe([(1, 0, 0)], T0);
        m.Observe([(1, 1000, 0)], T0.AddSeconds(10));
        m.Observe([(1, 3000, 0)], T0.AddMinutes(30));
        var now = T0.AddMinutes(40);
        Assert.Equal(3000, m.DownWithin(TimeSpan.FromHours(1), now));
        Assert.Equal(T0.AddMinutes(61), m.WindowFreesAt(TimeSpan.FromHours(1), now));
        Assert.Equal(2000, m.DownWithin(TimeSpan.FromHours(1), T0.AddMinutes(61)));
        Assert.Equal(0, m.DownWithin(TimeSpan.FromMinutes(5), now));
    }

    [Fact]
    public void ADownloadFinishedBetweenTwoLooks_IsCounted()
    {
        var m = new TransferMeter();
        m.Baseline(7, 0, 0);                                   // added
        m.Observe([(7, 5000, 0)], T0);                         // first look: already done
        m.Baseline(8, 100, 0);                                 // a resumed one keeps its earlier bytes out
        m.Observe([(7, 5000, 0), (8, 300, 0)], T0.AddSeconds(2));
        Assert.Equal(5200, m.DownWithin(TimeSpan.FromHours(1), T0.AddSeconds(2)));
    }

    [Fact]
    public void PerMinute_LinesUpWithTheClock()
    {
        var m = new TransferMeter();
        m.Observe([(1, 0, 0)], T0);
        m.Observe([(1, 100, 0)], T0.AddSeconds(5));
        m.Observe([(1, 400, 0)], T0.AddMinutes(2));
        var (down, up) = m.PerMinute(T0.AddMinutes(3), 5);
        Assert.Equal([0L, 100L, 0L, 300L, 0L], down);
        Assert.All(up, u => Assert.Equal(0, u));
    }

    [Fact]
    public void Snapshot_SurvivesARestart()
    {
        var m = new TransferMeter();
        m.Observe([(1, 0, 0)], T0);
        m.Observe([(1, 700, 20)], T0.AddSeconds(5));
        var restored = new TransferMeter();
        restored.Restore(m.Snapshot(), T0.AddMinutes(10));
        Assert.Equal(700, restored.DownWithin(TimeSpan.FromHours(1), T0.AddMinutes(10)));
        var dayLater = new TransferMeter();
        dayLater.Restore(m.Snapshot(), T0.AddHours(25));
        Assert.Equal(0, dayLater.DownWithin(TimeSpan.FromHours(24), T0.AddHours(25)));
    }
}
