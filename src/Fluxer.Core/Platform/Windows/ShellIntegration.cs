using System.Text.Json;
using Microsoft.Win32;

namespace Fluxer.Core.Platform.Windows;

/// HKCU registrations: native messaging host (per browser), magnet: protocol, .torrent files,
/// and the toast AppUserModelID. Idempotent; the app re-applies it at every start so a moved
/// install or a browser installed later is repaired automatically.
public static class ShellIntegration
{
    public const string HostName = "com.fluxer.host";
    public const string Aumid = "Fluxer.DownloadManager";
    private const string ProgId = "Fluxer.Torrent";

    /// Chrome's key is also read by Opera and Vivaldi.
    public static readonly (string Browser, string Key)[] BrowserKeys =
    [
        ("Chrome", @"Software\Google\Chrome\NativeMessagingHosts\" + HostName),
        ("Edge", @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName),
        ("Brave", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\" + HostName),
        ("Chromium", @"Software\Chromium\NativeMessagingHosts\" + HostName),
    ];

    public static void Register(string appExe, string hostExe, string dataDirectory, IEnumerable<string> extensionIds, bool fileAssociations)
    {
        var manifestPath = Path.Combine(dataDirectory, HostName + ".json");
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = "Fluxer download manager",
            ["path"] = hostExe,
            ["type"] = "stdio",
            ["allowed_origins"] = extensionIds.Select(id => $"chrome-extension://{id}/").ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var (_, key) in BrowserKeys)
            using (var k = Registry.CurrentUser.CreateSubKey(key)) k.SetValue("", manifestPath);

        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
        {
            k.SetValue("DisplayName", "Fluxer");
            k.SetValue("IconUri", appExe);
        }

        if (!fileAssociations) return;
        var open = $"\"{appExe}\" \"%1\"";
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet"))
        {
            k.SetValue("", "URL:Magnet Link");
            k.SetValue("URL Protocol", "");
            using var cmd = k.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", open);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            k.SetValue("", "Torrent");
            using (var icon = k.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{appExe}\",0");
            using var cmd = k.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", open);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent"))
        {
            k.SetValue("", ProgId);
            k.SetValue("Content Type", "application/x-bittorrent");
        }
    }

    public static void Unregister(string dataDirectory)
    {
        foreach (var (_, key) in BrowserKeys) Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + Aumid, false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
        using (var magnet = Registry.CurrentUser.OpenSubKey(@"Software\Classes\magnet\shell\open\command"))
            if (magnet?.GetValue("") is string cmd && cmd.Contains("Fluxer", StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\magnet", false);
        using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent"))
            if (ext?.GetValue("") as string == ProgId)
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.torrent", false);
        try { File.Delete(Path.Combine(dataDirectory, HostName + ".json")); } catch (IOException) { }
    }

    /// For the settings page: which browsers point at a manifest whose host exe exists.
    public static IReadOnlyList<(string Browser, bool Ok)> Status()
    {
        var list = new List<(string, bool)>();
        foreach (var (browser, key) in BrowserKeys)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            var ok = false;
            if (k?.GetValue("") is string manifest && File.Exists(manifest))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    ok = doc.RootElement.TryGetProperty("path", out var p) && File.Exists(p.GetString());
                }
                catch (JsonException) { }
            }
            list.Add((browser, ok));
        }
        return list;
    }
}
