using System.Runtime.InteropServices;
using System.Security;
using Avalonia.Threading;
using Fluxer.Core.Platform.Windows;
using Serilog;

namespace Fluxer.App.Services;

/// Windows toast notifications for an unpackaged app: the AUMID is registered under
/// HKCU\Software\Classes\AppUserModelId (ShellIntegration), no Start menu shortcut needed.
/// The three WinRT classes involved are called through their ABI directly: the Windows SDK projection
/// that would do it for us weighs ~55 MB in the package, for this one call.
public static unsafe partial class Notifier
{
    public static void Show(string title, string body)
    {
        var xml = $"""
            <toast><visual><binding template="ToastGeneric">
              <text>{SecurityElement.Escape(title)}</text>
              <text>{SecurityElement.Escape(body)}</text>
            </binding></visual></toast>
            """;
        // Engine events arrive on worker threads; the UI thread is an initialized STA.
        Dispatcher.UIThread.Post(() =>
        {
            try { ShowXml(xml); }
            catch (Exception e) { Log.Warning(e, "Toast failed"); }
        });
    }

    // Windows.Data.Xml.Dom.IXmlDocumentIO, IXmlDocument; Windows.UI.Notifications.IToastNotificationFactory,
    // IToastNotificationManagerStatics. Slots 0–5 are IUnknown + IInspectable; each interface's own methods follow.
    private static readonly Guid IidXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid IidXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");
    private static readonly Guid IidToastFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid IidToastManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");

    private static void ShowXml(string xml)
    {
        nint doc = 0, io = 0, content = 0, factory = 0, toast = 0, statics = 0, notifier = 0;
        try
        {
            using (var cls = new HString("Windows.Data.Xml.Dom.XmlDocument")) Check(RoActivateInstance(cls.Handle, out doc));
            Check(Marshal.QueryInterface(doc, IidXmlDocumentIO, out io));
            using (var text = new HString(xml))
                Check(((delegate* unmanaged<nint, nint, int>)Slot(io, 6))(io, text.Handle));          // LoadXml
            Check(Marshal.QueryInterface(doc, IidXmlDocument, out content));

            factory = Factory("Windows.UI.Notifications.ToastNotification", IidToastFactory);
            Check(((delegate* unmanaged<nint, nint, nint*, int>)Slot(factory, 6))(factory, content, &toast)); // CreateToastNotification

            statics = Factory("Windows.UI.Notifications.ToastNotificationManager", IidToastManagerStatics);
            using (var id = new HString(ShellIntegration.Aumid))
                Check(((delegate* unmanaged<nint, nint, nint*, int>)Slot(statics, 7))(statics, id.Handle, &notifier)); // CreateToastNotifierWithId
            Check(((delegate* unmanaged<nint, nint, int>)Slot(notifier, 6))(notifier, toast));                     // IToastNotifier.Show
        }
        finally
        {
            foreach (var p in (ReadOnlySpan<nint>)[notifier, statics, toast, factory, content, io, doc])
                if (p != 0) Marshal.Release(p);
        }
    }

    private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

    private static nint Factory(string runtimeClass, Guid iid)
    {
        using var cls = new HString(runtimeClass);
        Check(RoGetActivationFactory(cls.Handle, iid, out var factory));
        return factory;
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    private readonly struct HString : IDisposable
    {
        public readonly nint Handle;
        public HString(string s) => Check(WindowsCreateString(s, s.Length, out Handle));
        public void Dispose() => _ = WindowsDeleteString(Handle);
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint activatableClassId, out nint instance);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);
}
