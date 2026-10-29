using System.Text.Json;
using Microsoft.Win32;

namespace Fluxer.Core.Platform.Windows;

/// HKCU registrations: native messaging host (per browser, Chromium and Firefox), magnet: protocol, .torrent files,
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

    /// Firefox reads its own key and a manifest with allowed_extensions instead of allowed_origins.
    public const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts\" + HostName;
    private const string FirefoxManifest = HostName + ".firefox.json";

    private static IEnumerable<(string Browser, string Key)> AllKeys => BrowserKeys.Append(("Firefox", FirefoxKey));

    public static void Register(string appExe, string hostExe, string dataDirectory, IEnumerable<string> extensionIds,
        IEnumerable<string> firefoxIds, bool fileAssociations)
    {
        Directory.CreateDirectory(dataDirectory);
        var manifestPath = Path.Combine(dataDirectory, HostName + ".json");
        File.WriteAllText(manifestPath, HostManifest(hostExe, "allowed_origins", extensionIds.Select(id => $"chrome-extension://{id}/")));
        foreach (var (_, key) in BrowserKeys)
            using (var k = Registry.CurrentUser.CreateSubKey(key)) k.SetValue("", manifestPath);

        var firefoxPath = Path.Combine(dataDirectory, FirefoxManifest);
        File.WriteAllText(firefoxPath, HostManifest(hostExe, "allowed_extensions", firefoxIds));
        using (var k = Registry.CurrentUser.CreateSubKey(FirefoxKey)) k.SetValue("", firefoxPath);

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

    /// The host manifest Chromium (allowed_origins) or Firefox (allowed_extensions) reads.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    public static string HostManifest(string hostExe, string allowKey, IEnumerable<string> allowed) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = "Fluxer download manager",
            ["path"] = hostExe,
            ["type"] = "stdio",
            [allowKey] = allowed.ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true });

    public static void Unregister(string dataDirectory)
    {
        foreach (var (_, key) in AllKeys) Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + Aumid, false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
        using (var magnet = Registry.CurrentUser.OpenSubKey(@"Software\Classes\magnet\shell\open\command"))
            if (magnet?.GetValue("") is string cmd && cmd.Contains("Fluxer", StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\magnet", false);
        using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent"))
            if (ext?.GetValue("") as string == ProgId)
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.torrent", false);
        foreach (var file in new[] { HostName + ".json", FirefoxManifest })
            try { File.Delete(Path.Combine(dataDirectory, file)); } catch (IOException) { }
    }

    /// The installed Chromium browser (Chrome first) and its extensions page, which only the browser itself can open.
    public static (string Exe, string Page)? ExtensionsPage()
    {
        foreach (var (exe, page) in new[] { ("chrome.exe", "chrome://extensions/"), ("msedge.exe", "edge://extensions/"), ("brave.exe", "brave://extensions/") })
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (k?.GetValue("") is string path && File.Exists(path.Trim('"'))) return (path.Trim('"'), page);
            }
        return null;
    }

    /// For the settings page: which browsers point at a manifest whose host exe exists.
    public static IReadOnlyList<(string Browser, bool Ok)> Status()
    {
        var list = new List<(string, bool)>();
        foreach (var (browser, key) in AllKeys)
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
