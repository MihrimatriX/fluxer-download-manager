using Avalonia;
using Fluxer.Core.Platform.Windows;
using Fluxer.Ipc;
using Serilog;
using Velopack;

namespace Fluxer.App;

internal static class Program
{
    /// %LOCALAPPDATA%\Fluxer, or the "data" folder next to the app when portable.txt sits there (portable mode).
    public static readonly string DataDirectory = PortableData.Resolve();

    public static string[] Args { get; private set; } = [];

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "Fluxer.exe");

    public const string RestartArg = "--restarted";

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                ShellIntegration.Unregister(DataDirectory);
                Autostart.RemoveIfFor(ExePath);
            })
            .Run();

        Args = args;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(DataDirectory, "logs", "fluxer-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10, fileSizeLimitBytes: 20 * 1024 * 1024)
            .CreateLogger();
        Services.CrashGuard.Install();

        // Single instance: the second process forwards its arguments and exits. A restart (portable mode switch,
        // restored backup) waits for the old process to let go instead.
        using var mutex = new Mutex(true, @"Local\" + Protocol.PipeName, out var first);
        if (!first && args.Contains(RestartArg))
        {
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            ForwardToRunningInstance(args);
            return 0;
        }
        args = [.. args.Where(a => a != RestartArg)];
        Args = args;
        PortableData.ApplyPending(DataDirectory);

        try
        {
            Log.Information("Fluxer {Version} starting {Args}", typeof(Program).Assembly.GetName().Version, args);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Unhandled exception");
            Services.CrashGuard.WriteMarker(e);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ForwardToRunningInstance(string[] args)
    {
        var msg = Envelope.Create(Guid.NewGuid().ToString(), MessageTypes.InstanceArgs, new InstanceArgs(args));
        try { PipeClient.SendAsync(msg.ToBytes(), TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) { Log.Warning(e, "Could not reach running instance"); }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseWin32().UseSkia().UseHarfBuzz().LogToTrace();
}
