using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TaskHub.Core.Configuration;
using TaskHub.Core.Notifications;
using TaskHub.Storage.Tests;

namespace TaskHub.Api.Tests;

/// <summary>Records banners instead of drawing them.</summary>
public sealed class RecordingSurface : IBannerSurface
{
    public List<NotificationRequest> Shown { get; } = [];
    public event EventHandler<BannerActionEventArgs>? ActionInvoked;
    public event EventHandler<BannerClosedEventArgs>? Closed;
    public void Show(NotificationRequest request, BannerStyle style) { lock (Shown) Shown.Add(request); }
    public void Close(Guid id) => Closed?.Invoke(this, new BannerClosedEventArgs(id, BannerCloseReason.Programmatic));
    public void Click(Guid id, string action) => ActionInvoked?.Invoke(this, new BannerActionEventArgs(id, action));
}

/// <summary>A real SQLite-backed TaskService with the real API server on a free loopback port.</summary>
public sealed class ApiHarness : IAsyncDisposable
{
    private ApiHarness(Harness core, ApiServer server, NotificationCenter notifications, RecordingSurface surface)
    {
        Core = core;
        Server = server;
        Notifications = notifications;
        Surface = surface;
    }

    public NotificationCenter Notifications { get; }
    public RecordingSurface Surface { get; }

    public Harness Core { get; }
    public ApiServer Server { get; }
    public string BaseUrl => $"http://127.0.0.1:{Server.Port}";
    public string Token => Server.Tokens!.Token;
    public string DiscoveryFile => Core.Paths.ApiDiscoveryFile;

    public static async Task<ApiHarness> CreateAsync()
    {
        var core = await Harness.CreateAsync();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(core.Tasks);
        services.AddSingleton(core.ImportExport);
        services.AddSingleton(core.Clock as TaskHub.Core.Hosting.IClock);
        services.AddSingleton(core.Bus);
        var surface = new RecordingSurface();
        var options = new NotificationOptions();
        var center = new NotificationCenter(surface, core.Bus, core.Clock, new NotificationHistory(), () => options, NullLogger<NotificationCenter>.Instance);
        services.AddSingleton(center);
        var root = services.BuildServiceProvider();

        var server = new ApiServer(root, new ApiServerOptions
        {
            Port = 0,
            TokenFile = core.Paths.ApiTokenFile,
            DiscoveryFile = core.Paths.ApiDiscoveryFile,
            Version = "test",
        }, NullLogger<ApiServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        Assert.True(server.IsRunning);
        return new ApiHarness(core, server, center, surface);
    }

    /// <summary>An HttpClient with the bearer token.</summary>
    public HttpClient Client(string? token = "valid")
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token == "valid" ? Token : token);
        return http;
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync(CancellationToken.None);
        await Core.DisposeAsync();
    }
}
