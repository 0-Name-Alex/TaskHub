using TaskHub.Core.Hosting;

namespace TaskHub.Core.Tests.Hosting;

public class SingleInstanceGuardTests
{
    private static string UniqueKey() => "TaskHubTest." + Guid.NewGuid().ToString("N")[..12];

    // Named mutexes are re-entrant on the owning thread, so "another process" is simulated
    // with a dedicated thread.
    private static Task<bool> TryAcquireOnOtherThread(SingleInstanceGuard guard)
    {
        var tcs = new TaskCompletionSource<bool>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(guard.TryAcquire()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.Start();
        return tcs.Task;
    }

    [Fact]
    public async Task Second_instance_cannot_acquire()
    {
        var key = UniqueKey();
        using var first = new SingleInstanceGuard(key);
        Assert.True(first.TryAcquire());

        using var second = new SingleInstanceGuard(key);
        Assert.False(await TryAcquireOnOtherThread(second));
    }

    [Fact]
    public async Task Different_keys_do_not_conflict()
    {
        using var a = new SingleInstanceGuard(UniqueKey());
        using var b = new SingleInstanceGuard(UniqueKey());
        Assert.True(a.TryAcquire());
        Assert.True(await TryAcquireOnOtherThread(b));
    }

    [Fact]
    public async Task Second_instance_forwards_its_arguments_to_the_first()
    {
        var key = UniqueKey();
        using var primary = new SingleInstanceGuard(key);
        Assert.True(primary.TryAcquire());
        var received = new TaskCompletionSource<string[]>();
        primary.StartServer(args =>
        {
            received.TrySetResult(args);
            return Task.CompletedTask;
        });

        using var secondary = new SingleInstanceGuard(key);
        var beforeSendCalled = false;
        var ok = await secondary.SendToPrimaryAsync(["--show", "quick add \"milk\""], TimeSpan.FromSeconds(5), () => beforeSendCalled = true);

        Assert.True(ok);
        Assert.True(beforeSendCalled);
        Assert.Equal(["--show", "quick add \"milk\""], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Server_handles_several_launches_in_a_row()
    {
        var key = UniqueKey();
        using var primary = new SingleInstanceGuard(key);
        Assert.True(primary.TryAcquire());
        var count = 0;
        primary.StartServer(_ =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        for (var i = 0; i < 3; i++)
        {
            using var s = new SingleInstanceGuard(key);
            Assert.True(await s.SendToPrimaryAsync([$"run{i}"], TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Send_fails_when_no_primary_is_listening()
    {
        using var lonely = new SingleInstanceGuard(UniqueKey());
        Assert.False(await lonely.SendToPrimaryAsync(["x"], TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void Keys_differ_per_data_folder_and_are_pipe_safe()
    {
        var a = SingleInstanceGuard.BuildKey("TaskHub", Path.Combine(Path.GetTempPath(), "a"));
        var b = SingleInstanceGuard.BuildKey("TaskHub", Path.Combine(Path.GetTempPath(), "b"));
        Assert.NotEqual(a, b);
        Assert.Equal(a, SingleInstanceGuard.BuildKey("TaskHub", Path.Combine(Path.GetTempPath(), "a")));
        Assert.Matches("^TaskHub\\.[0-9A-F]{16}$", a);
    }
}
