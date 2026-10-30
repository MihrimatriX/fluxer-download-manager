using Avalonia.Threading;
using Serilog;

namespace Fluxer.App.Services;

/// Logs every exception nobody observed (fire-and-forget tasks, UI thread, other threads) and leaves a
/// marker when Fluxer dies from one, so the next start can offer the diagnostics package.
/// Nothing leaves the machine: the user decides whether to save and share the zip.
public static class CrashGuard
{
    private static string MarkerPath => Path.Combine(Program.DataDirectory, "last-crash.txt");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "Unhandled exception (terminating={Terminating})", e.IsTerminating);
            if (e.IsTerminating) WriteMarker(ex);
            Log.CloseAndFlush();
        };
        // A faulted task that is never awaited (`_ = FooAsync()`) would otherwise vanish without a trace.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    /// The UI thread: log and keep running. Downloads live on their own tasks and checkpoints, so one
    /// broken handler should not take every running download down with the window.
    public static void InstallUiHandler() =>
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error(e.Exception, "Unhandled exception on the UI thread");
            e.Handled = true;
        };

    public static void WriteMarker(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Program.DataDirectory);
            File.WriteAllText(MarkerPath, $"{DateTimeOffset.Now:O}\n{ex?.GetType().FullName}: {ex?.Message}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// Returns the previous crash's summary once, then forgets it.
    public static string? TakeMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var text = File.ReadAllText(MarkerPath);
            File.Delete(MarkerPath);
            return text;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
