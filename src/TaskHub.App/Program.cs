using System.Reflection;
using System.Windows.Threading;
using Serilog;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;
using TaskHub.Platform.Windows;

namespace TaskHub.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var startup = StartupOptions.Parse(args);
        var paths = AppPaths.Resolve();
        paths.EnsureCreated();

        // 1. Single instance: forward our command line to the running instance and exit.
        using var guard = new SingleInstanceGuard(SingleInstanceGuard.BuildKey(AppPaths.AppName, paths.DataRoot));
        if (!guard.TryAcquire())
        {
            var delivered = guard
                .SendToPrimaryAsync(args, TimeSpan.FromSeconds(3), beforeSend: Foreground.AllowAnyProcess)
                .GetAwaiter().GetResult();
            return delivered ? 0 : 1;
        }

        // 2. Config + logging, before anything else can fail.
        var config = ConfigLoader.LoadOrCreate(paths.ConfigFile);
        Log.Logger = Logging.CreateLogger(paths, config.Options.Logging, out var levelSwitch);

        try
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            Log.Information("TaskHub {Version} starting (pid {Pid}, data {DataRoot}, args [{Args}])",
                version, Environment.ProcessId, paths.DataRoot, string.Join(' ', args));
            if (config.Created) Log.Information("Created default config at {Path}", paths.ConfigFile);
            if (config.Error is not null) Log.Warning("Config file is invalid, using defaults: {Error}", config.Error);
            if (config.AddedSections is { Count: > 0 } added)
                Log.Information("Added new settings sections to {Path}: {Sections}", paths.ConfigFile, string.Join(", ", added));

            // 3. Host (services) + WPF on this STA thread. The host is started from App.OnStartup,
            //    once the dispatcher is running, so services may safely marshal to the UI thread.
            var ui = new WpfUiDispatcher(Dispatcher.CurrentDispatcher);
            using var host = AppHost.Build(args, paths, config.Options, ui, guard, levelSwitch, startup, version);

            var app = new App(host, startup, version, config.Error);
            app.InitializeComponent();
            var exitCode = app.Run();
            Log.Information("TaskHub exited with code {ExitCode}", exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "TaskHub terminated unexpectedly");
            return 2;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}

/// <summary>Command-line switches.</summary>
public sealed record StartupOptions(bool Background)
{
    public static StartupOptions Parse(IEnumerable<string> args) =>
        new(Background: args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase)));
}
