using System.Security;
using Fluxer.Core.Platform.Windows;
using Serilog;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Fluxer.App.Services;

/// Windows toast notifications for an unpackaged app: the AUMID is registered under
/// HKCU\Software\Classes\AppUserModelId (ShellIntegration), no Start menu shortcut needed.
public static class Notifier
{
    public static void Show(string title, string body)
    {
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml($"""
                <toast><visual><binding template="ToastGeneric">
                  <text>{SecurityElement.Escape(title)}</text>
                  <text>{SecurityElement.Escape(body)}</text>
                </binding></visual></toast>
                """);
            ToastNotificationManager.CreateToastNotifier(ShellIntegration.Aumid).Show(new ToastNotification(xml));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Toast failed");
        }
    }
}
