using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TaskHub.Core.Hosting;

/// <summary>
/// Ensures one TaskHub per user session (per data folder). The first instance owns a named
/// mutex and listens on a named pipe; later launches forward their command line over the pipe
/// and exit. Both are scoped to the current user and Windows session.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    public SingleInstanceGuard(string instanceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey);
        _mutexName = @"Local\" + instanceKey;
        _pipeName = instanceKey;
    }

    /// <summary>
    /// Stable key for this user + session + data folder, so a dev instance with a different
    /// TASKHUB_HOME can run next to the real one.
    /// </summary>
    public static string BuildKey(string appName, string dataRoot)
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}|{GetSessionId()}|{Path.GetFullPath(dataRoot).ToUpperInvariant()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $"{appName}.{hash}";
    }

    private static int GetSessionId()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().SessionId; }
        catch (PlatformNotSupportedException) { return 0; }
    }

    public bool IsPrimary => _ownsMutex;

    /// <summary>True if this process is now the primary instance.</summary>
    public bool TryAcquire()
    {
        if (_ownsMutex) return true;
        if (_mutex is null)
        {
            _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
            if (createdNew)
            {
                _ownsMutex = true;
                return true;
            }
        }

        try
        {
            // The mutex exists but may be abandoned by a crashed instance that still had it.
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
        return _ownsMutex;
    }

    /// <summary>Starts listening for command lines from later launches.</summary>
    public void StartServer(Func<string[], Task> onMessage, ILogger? log = null)
    {
        if (!_ownsMutex) throw new InvalidOperationException("Only the primary instance can listen.");
        if (_serverTask is not null) return;
        log ??= NullLogger.Instance;
        _serverCts = new CancellationTokenSource();
        var ct = _serverCts.Token;
        _serverTask = Task.Run(() => ServeAsync(onMessage, log, ct), ct);
    }

    private async Task ServeAsync(Func<string[], Task> onMessage, ILogger log, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
                var message = line is null ? null : JsonSerializer.Deserialize<ForwardMessage>(line);

                if (message?.Args is { } args)
                {
                    log.LogInformation("Received activation from another launch: {Args}", string.Join(' ', args));
                    await onMessage(args).ConfigureAwait(false);
                    await writer.WriteLineAsync("ok").ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteLineAsync("bad-request").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Single-instance pipe error");
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sends this launch's command line to the primary instance, retrying briefly in case
    /// the primary is still starting up.
    /// </summary>
    public async Task<bool> SendToPrimaryAsync(string[] args, TimeSpan timeout, Action? beforeSend = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var remaining = deadline - DateTime.UtcNow;
                await client.ConnectAsync(TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 50, 1000)), CancellationToken.None)
                    .ConfigureAwait(false);

                beforeSend?.Invoke();
                await using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new ForwardMessage(args))).ConfigureAwait(false);
                var reply = await reader.ReadLineAsync().ConfigureAwait(false);
                return reply == "ok";
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
    }

    public async Task StopServerAsync()
    {
        if (_serverCts is null || _serverTask is null) return;
        await _serverCts.CancelAsync().ConfigureAwait(false);
        try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        _serverTask = null;
    }

    public void Dispose()
    {
        try { StopServerAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
        _serverCts?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* released from a different thread; handle close releases it */ }
            _ownsMutex = false;
        }
        _mutex?.Dispose();
    }

    private sealed record ForwardMessage(string[] Args);
}
