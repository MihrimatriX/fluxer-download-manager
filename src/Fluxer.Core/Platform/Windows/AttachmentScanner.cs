using System.Runtime.InteropServices;

namespace Fluxer.Core.Platform.Windows;

public enum ScanResult { Clean, Blocked, Unavailable }

/// Hands a finished download to Windows Attachment Services, the same path browsers use: it writes the
/// Mark of the Web and asks the registered antivirus (Defender or a third-party AMSI/IOfficeAntiVirus
/// provider) to scan the file. A file the antivirus rejects is usually deleted or quarantined by it.
public static class AttachmentScanner
{
    // Identifies Fluxer to Attachment Services so its per-client prompts/state are kept apart.
    private static readonly Guid ClientGuid = new("5b0a7f61-8c43-4d0e-9a52-6f1e2c7d9b30");
    private const int InetESecurityProblem = unchecked((int)0x800C000E);

    public static ScanResult Scan(string path, string? sourceUrl, string? referrerUrl)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return ScanResult.Unavailable;
        IAttachmentExecute? ae = null;
        try
        {
            ae = (IAttachmentExecute)new AttachmentServices();
            ae.SetClientGuid(ClientGuid);
            ae.SetClientTitle("Fluxer");
            ae.SetLocalPath(path);
            // Source must be a URL; without one Attachment Services falls back to the Internet zone anyway.
            if (!string.IsNullOrEmpty(sourceUrl)) ae.SetSource(sourceUrl);
            if (!string.IsNullOrEmpty(referrerUrl)) ae.SetReferrer(referrerUrl);
            var hr = ae.Save();
            return hr >= 0 ? ScanResult.Clean
                : hr == InetESecurityProblem || !File.Exists(path) ? ScanResult.Blocked
                : ScanResult.Unavailable;
        }
        catch (COMException e) when (e.HResult == InetESecurityProblem) { return ScanResult.Blocked; }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException) { return ScanResult.Unavailable; }
        finally
        {
            if (ae is not null) Marshal.ReleaseComObject(ae);
        }
    }

    [ComImport, Guid("4125dd96-e03a-4103-8f70-e0597d803b9c")]
    private class AttachmentServices;

    // Vtable order matters: shobjidl_core.h, IAttachmentExecute.
    [ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecute
    {
        void SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetClientGuid(in Guid guid);
        void SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string localPath);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
        void SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
        [PreserveSig] int CheckPolicy();
        [PreserveSig] int Prompt(IntPtr hwnd, int prompt, out int action);
        [PreserveSig] int Save();
        [PreserveSig] int Execute(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out IntPtr process);
        [PreserveSig] int SaveWithUI(IntPtr hwnd);
        void ClearClientState();
    }
}
