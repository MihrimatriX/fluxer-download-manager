using System.ComponentModel;
using System.Diagnostics;

namespace Fluxer.Core.Platform.Windows;

/// Inbound Windows Firewall rule for the torrent listener. Fluxer installs per user with no admin step, so
/// nothing adds one, and Windows then drops every peer that tries to connect to us: only the peers we dial
/// out to (the minority that accept incoming connections) are ever used, which is why a torrent was slower
/// than in a client whose installer added a rule. Reading needs no admin; adding costs one UAC prompt.
public static class FirewallRule
{
    /// Looked up by our own rule name: netsh prints localized labels and OEM-encoded paths, the name is stable.
    public static bool Exists(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{name}\" dir=in")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception) { return true; }   // no netsh: nothing we could do about it, so don't ask
    }

    /// One elevated netsh that also replaces a stale rule of the same name. False when the user declines UAC.
    public static bool TryAdd(string name, string program)
    {
        var args = $"/c netsh advfirewall firewall delete rule name=\"{name}\" >nul & "
                   + $"netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow program=\"{program}\" enable=yes profile=any";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", args)
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }  // "No" on the UAC prompt
    }
}
