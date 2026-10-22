namespace Fluxer.Core.Platform.Windows;

public static class MarkOfTheWeb
{
    /// Writes the Zone.Identifier ADS (Internet zone) so SmartScreen/Office treat the file as downloaded.
    public static void Apply(string path, string? hostUrl, string? referrerUrl)
    {
        var lines = new List<string> { "[ZoneTransfer]", "ZoneId=3" };
        if (!string.IsNullOrEmpty(referrerUrl)) lines.Add("ReferrerUrl=" + referrerUrl);
        if (!string.IsNullOrEmpty(hostUrl)) lines.Add("HostUrl=" + hostUrl);
        try { File.WriteAllLines(path + ":Zone.Identifier", lines); }
        catch (IOException) { } // FAT32/exFAT have no ADS; not fatal
    }
}
