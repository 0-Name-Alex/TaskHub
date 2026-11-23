using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;

namespace TaskHub.App;

internal static class Logging
{
    public static Logger CreateLogger(AppPaths paths, LoggingOptions options, out LoggingLevelSwitch levelSwitch)
    {
        levelSwitch = new LoggingLevelSwitch(ParseLevel(options.MinimumLevel));
        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(paths.LogsDir, "taskhub-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedDays,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
    }

    public static LogEventLevel ParseLevel(string? level) =>
        Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogEventLevel.Information;
}
