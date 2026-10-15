using Avalonia;
using Fluxer.Core.Platform.Windows;
using Fluxer.Ipc;
using Serilog;
using Velopack;

namespace Fluxer.App;

internal static class Program
{
    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fluxer");

    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => ShellIntegration.Unregister(DataDirectory))
            .Run();

        Args = args;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(DataDirectory, "logs", "fluxer-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10, fileSizeLimitBytes: 20 * 1024 * 1024)
            .CreateLogger();

        // Single instance: the second process forwards its arguments and exits.
        using var mutex = new Mutex(true, @"Local\" + Protocol.PipeName, out var first);
        if (!first)
        {
            ForwardToRunningInstance(args);
            return 0;
        }

        try
        {
            Log.Information("Fluxer {Version} starting {Args}", typeof(Program).Assembly.GetName().Version, args);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Unhandled exception");
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
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
