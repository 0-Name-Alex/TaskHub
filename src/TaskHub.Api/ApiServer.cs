using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Contracts;
using TaskHub.Api.Endpoints;
using TaskHub.Api.Security;
using TaskHub.Core.Hosting;
using TaskHub.Core.Services;

namespace TaskHub.Api;

public sealed class ApiServerOptions
{
    /// <summary>0 = pick a free port (tests).</summary>
    public int Port { get; init; } = 47821;
    public required string TokenFile { get; init; }
    /// <summary>Where to publish api.json for clients; null = don't.</summary>
    public string? DiscoveryFile { get; init; }
    public string Version { get; init; } = "0.0.0";
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}

/// <summary>
/// Hosts the local API on Kestrel inside the app process. It runs as its own small web app that
/// borrows the application services (TaskService etc.) from the main container, so the API, UI
/// and CLI all act on exactly the same state. If the port is taken, the app keeps running and
/// logs the problem rather than failing to start.
/// </summary>
public sealed class ApiServer(IServiceProvider root, ApiServerOptions options, ILogger<ApiServer> log) : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;

    public int Port { get; private set; }
    public bool IsRunning => _app is not null;
    public ApiTokenStore? Tokens { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Tokens = new ApiTokenStore(options.TokenFile);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "TaskHub.Api" });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(root.GetRequiredService<ILoggerFactory>());
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true); // no "Press Ctrl+C" in the app log
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));

        // Borrow application services from the main container.
        builder.Services.AddSingleton(root.GetRequiredService<TaskService>());
        builder.Services.AddSingleton(root.GetRequiredService<ImportExportService>());
        builder.Services.AddSingleton(root.GetRequiredService<IClock>());
        if (root.GetService<Core.Notifications.NotificationCenter>() is { } notifications)
            builder.Services.AddSingleton(notifications);
        builder.Services.AddSingleton(root.GetRequiredService<Core.Events.IEventBus>());
        builder.Services.AddSingleton(Tokens);
        builder.Services.AddSingleton(new LocalApiGuardOptions { AllowedOrigins = options.AllowedOrigins });
        builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));

        var app = builder.Build();
        app.Use(ErrorHandling);
        app.UseMiddleware<LocalApiGuardMiddleware>();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        TaskEndpoints.Map(app, options.Version);
        NotificationEndpoints.Map(app);
        EventStreamEndpoints.Map(app);

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            log.LogError(ex, "Local API could not listen on 127.0.0.1:{Port} (port in use?). Change [api] port in config.toml.", options.Port);
            await app.DisposeAsync();
            return;
        }

        _app = app;
        Port = ResolvePort(app);
        log.LogInformation("Local API listening on http://127.0.0.1:{Port}", Port);

        if (options.DiscoveryFile is { } discovery)
            new ApiDiscovery(Port, options.TokenFile, Environment.ProcessId, options.Version).Write(discovery);
    }

    private static int ResolvePort(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return address is not null ? new Uri(address).Port : 0;
    }

    /// <summary>Maps service exceptions to HTTP errors with a JSON body.</summary>
    private async Task ErrorHandling(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, message) = ex switch
            {
                TaskValidationException or ArgumentException or FormatException => (StatusCodes.Status400BadRequest, ex.Message),
                BadHttpRequestException bad => (bad.StatusCode, "Malformed request: " + bad.Message),
                JsonException => (StatusCodes.Status400BadRequest, "Malformed JSON: " + ex.Message),
                InvalidOperationException when ex.Message.Contains("JSON", StringComparison.OrdinalIgnoreCase) => (StatusCodes.Status400BadRequest, ex.Message),
                TaskNotFoundException => (StatusCodes.Status404NotFound, ex.Message),
                OperationCanceledException => (499, "Request cancelled."),
                _ => (StatusCodes.Status500InternalServerError, "Internal error; see the TaskHub log."),
            };
            if (status >= 500) log.LogError(ex, "API request {Method} {Path} failed", context.Request.Method, context.Request.Path);
            context.Response.Clear();
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ApiError(message), ApiJson.Options);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null) return;
        if (options.DiscoveryFile is { } discovery)
        {
            try { File.Delete(discovery); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _app = null;
    }
}

public static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddTaskHubApi(this IServiceCollection services, Func<IServiceProvider, ApiServerOptions> options)
    {
        services.AddSingleton<ApiServerOptions>(options);
        services.AddSingleton<ApiServer>();
        services.AddHostedService(sp => sp.GetRequiredService<ApiServer>());
        return services;
    }
}
