using System.Runtime.InteropServices;

namespace Fluxer.Core.Platform.Windows;

public enum ScanResult { Clean, Blocked, Unavailable }

/// Hands a finished download to Windows Attachment Services, the same path browsers use: it writes the
/// Mark of the Web and asks the registered antivirus (Defender or a third-party AMSI/IOfficeAntiVirus
/// provider) to scan the file. A file the antivirus rejects is usually deleted or quarantined by it.
public static partial class AttachmentScanner
{
    // Identifies Fluxer to Attachment Services so its per-client prompts/state are kept apart.
    private static readonly Guid ClientGuid = new("5b0a7f61-8c43-4d0e-9a52-6f1e2c7d9b30");
    private const int InetESecurityProblem = unchecked((int)0x800C000E);

    /// Blocking (the antivirus scans synchronously); call it off the UI thread. Attachment Services is an
    /// apartment-threaded COM object, so it runs on its own STA thread like browsers do.
    public static ScanResult Scan(string path, string? sourceUrl, string? referrerUrl)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return ScanResult.Unavailable;
        var result = ScanResult.Unavailable;
        var thread = new Thread(() => result = ScanOnSta(path, sourceUrl, referrerUrl)) { IsBackground = true, Name = "Fluxer AV scan" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static unsafe ScanResult ScanOnSta(string path, string? sourceUrl, string? referrerUrl)
    {
        // Called through IAttachmentExecute's vtable (shobjidl_core.h) rather than [ComImport]: built-in COM
        // interop is switched off in the trimmed build.
        var init = CoInitializeEx(0, CoinitApartmentThreaded);
        nint ae = 0;
        try
        {
            if (CoCreateInstance(ClsidAttachmentServices, 0, ClsctxInprocServer, IidAttachmentExecute, out ae) < 0 || ae == 0)
                return ScanResult.Unavailable;
            var guid = ClientGuid;
            if (((delegate* unmanaged<nint, Guid*, int>)Slot(ae, 4))(ae, &guid) < 0  // SetClientGuid
                || Call(ae, 3, "Fluxer") < 0                                          // SetClientTitle
                || Call(ae, 5, path) < 0                                              // SetLocalPath
                // Source must be a URL; without one Attachment Services falls back to the Internet zone anyway.
                || !string.IsNullOrEmpty(sourceUrl) && Call(ae, 7, sourceUrl) < 0       // SetSource
                || !string.IsNullOrEmpty(referrerUrl) && Call(ae, 8, referrerUrl) < 0)  // SetReferrer
                return ScanResult.Unavailable;
            var hr = ((delegate* unmanaged<nint, int>)Slot(ae, 11))(ae);                // Save
            return hr >= 0 ? ScanResult.Clean
                : hr == InetESecurityProblem || !File.Exists(path) ? ScanResult.Blocked
                : ScanResult.Unavailable;
        }
        finally
        {
            if (ae != 0) Marshal.Release(ae);
            if (init >= 0) CoUninitialize();
        }
    }

    private static unsafe nint Slot(nint obj, int index) => (*(nint**)obj)[index];

    /// A method taking one LPCWSTR.
    private static unsafe int Call(nint obj, int slot, string value)
    {
        fixed (char* p = value) return ((delegate* unmanaged<nint, char*, int>)Slot(obj, slot))(obj, p);
    }

    private const int CoinitApartmentThreaded = 0x2, ClsctxInprocServer = 0x1;
    private static readonly Guid ClsidAttachmentServices = new("4125dd96-e03a-4103-8f70-e0597d803b9c");
    private static readonly Guid IidAttachmentExecute = new("73db1241-1e85-4581-8e4f-a81e1d0f8c57");

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, int coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, int context, in Guid iid, out nint instance);
}
