using Microsoft.Win32.SafeHandles;

namespace Fluxer.Core.Platform.Windows;

public static class SparseFile
{
    /// Creates (or reopens) a file, marks it sparse and sizes it, then returns an async handle.
    /// Sparse matters: without it NTFS zero-fills up to the write offset synchronously on the first
    /// write past the valid data length, stalling the writer for seconds on large files.
    public static SafeFileHandle OpenPreallocated(string path, long length)
    {
        using (var sync = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            if (length > 0 && RandomAccess.GetLength(sync) != length)
            {
                Native.SetSparse(sync);
                RandomAccess.SetLength(sync, length);
            }
        }
        return File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
    }

    public static SafeFileHandle OpenAppend(string path) =>
        File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
}
