using Microsoft.Extensions.Logging.Abstractions;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Automation;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;
using TaskHub.Core.Launcher;

namespace TaskHub.Core.Tests.Automation;

internal sealed class FakeSystemStatus : ISystemStatus
{
    public TimeSpan? Idle { get; set; } = TimeSpan.Zero;
    public PowerState? Power { get; set; }
    public List<DriveSpace> Drives { get; } = [];

    public TimeSpan? GetIdleTime() => Idle;
    public PowerState? GetPower() => Power;
    public IReadOnlyList<DriveSpace> GetFixedDrives() => Drives;
}

public class SystemMonitorTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public async Task Idle_battery_and_disk_events_fire_once_per_crossing()
    {
        await using var bus = TestBus.Create();
        using var rec = new TestBus.Recorder(bus, "system.*");
        var clock = new TestClock(new DateTimeOffset(2026, 11, 3, 4, 0, 0, TimeSpan.Zero));
        var status = new FakeSystemStatus { Power = new PowerState(false, 80) };
        var options = new AutomationOptions { IdleMinutes = 10, BatteryLowPercent = 20, DiskLowGb = 10 };
        var monitor = new SystemMonitor(status, bus, clock, () => options, NullLogger<SystemMonitor>.Instance);

        monitor.Check();
        status.Idle = TimeSpan.FromMinutes(11);
        monitor.Check();
        status.Idle = TimeSpan.FromMinutes(12);
        monitor.Check();                                      // still idle: no second event
        status.Idle = TimeSpan.FromSeconds(3);
        monitor.Check();                                      // input again → active

        status.Power = new PowerState(true, 50);              // unplugged
        monitor.Check();
        status.Power = new PowerState(true, 19);
        monitor.Check();
        status.Power = new PowerState(true, 18);
        monitor.Check();                                      // already reported
        status.Power = new PowerState(false, 18);             // plugged in
        monitor.Check();

        status.Drives.Add(new DriveSpace("C:", 5 * Gb, 500 * Gb));
        status.Drives.Add(new DriveSpace("D:", 200 * Gb, 1000 * Gb));
        monitor.Check(forceDisks: true);
        monitor.Check(forceDisks: true);

        var events = await rec.WaitForAsync(e => e.Count >= 6);
        Assert.Equal(
            [EventTypes.SystemIdle, EventTypes.SystemActive, EventTypes.PowerBattery, EventTypes.BatteryLow, EventTypes.PowerAc, EventTypes.DiskLow],
            events.Select(e => e.Type).Distinct());
        Assert.Single(events, e => e.Type == EventTypes.SystemIdle);
        Assert.Single(events, e => e.Type == EventTypes.BatteryLow);
        Assert.Equal(19, events.Single(e => e.Type == EventTypes.BatteryLow).DataAs<BatteryEvent>()!.Percent);
        var disk = Assert.Single(events, e => e.Type == EventTypes.DiskLow).DataAs<DiskLowEvent>()!;
        Assert.Equal("C:", disk.Drive);
        Assert.Equal(5, disk.FreeGb);
        Assert.Equal(11, events.Single(e => e.Type == EventTypes.SystemIdle).DataAs<IdleEvent>()!.IdleMinutes);
    }
}

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("code", "Visual Studio Code")]
    [InlineData("vis", "Visual Studio Code")]
    [InlineData("stu cod", "Visual Studio Code")]
    [InlineData("ntpd", "Notepad")]
    [InlineData("NOTE", "Notepad")]
    public void Matches(string query, string name) => Assert.True(FuzzyMatcher.Score(query, name) > 0);

    [Theory]
    [InlineData("xyz", "Notepad")]
    [InlineData("code zebra", "Visual Studio Code")]
    public void Does_not_match(string query, string name) => Assert.Equal(0, FuzzyMatcher.Score(query, name));

    [Fact]
    public void Better_matches_score_higher()
    {
        int S(string q, string n) => FuzzyMatcher.Score(q, n);
        Assert.True(S("notepad", "Notepad") > S("note", "Notepad"));
        Assert.True(S("note", "Notepad") > S("pad", "Notepad"));
        Assert.True(S("vsc", "Visual Studio Code") > S("vsc", "Very slow cat food"[..4] + "sc"));
        Assert.True(S("pad", "Notepad") > S("ntpd", "Notepad"));
        Assert.True(FuzzyMatcher.Score("wiki", "Team", "docs wiki") > 0);
    }
}

public class CaptureTextTests
{
    [Fact]
    public void First_line_becomes_the_title_and_the_rest_the_notes()
    {
        var (title, notes) = CaptureService.FromText("  Buy milk  \nand eggs\n");
        Assert.Equal("Buy milk", title);
        Assert.Equal("Buy milk  \nand eggs", notes);

        (title, notes) = CaptureService.FromText("just one line");
        Assert.Equal("just one line", title);
        Assert.Null(notes);

        (title, notes) = CaptureService.FromText("https://www.example.com/docs/page/");
        Assert.Equal("Link: example.com/docs/page", title);
        Assert.Equal("https://www.example.com/docs/page/", notes);

        (title, _) = CaptureService.FromText(new string('x', 500));
        Assert.Equal(CaptureService.MaxTitleLength, title.Length);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }
}

public class FileWatchServiceTests
{
    [Fact]
    public void Temporary_download_files_are_ignored_and_filters_apply()
    {
        Assert.True(FileWatchService.IsTemporary("report.pdf.crdownload"));
        Assert.True(FileWatchService.IsTemporary("~$budget.xlsx"));
        Assert.True(FileWatchService.IsTemporary("movie.mkv.part"));
        Assert.False(FileWatchService.IsTemporary("report.pdf"));

        var def = new WatchDefinition { Name = "d", Path = "x", Filters = ["*.pdf", "invoice-*"] };
        Assert.True(FileWatchService.MatchesFilters(def, "A.PDF"));
        Assert.True(FileWatchService.MatchesFilters(def, "invoice-12.txt"));
        Assert.False(FileWatchService.MatchesFilters(def, "notes.txt"));
        Assert.True(FileWatchService.MatchesFilters(def with { Filters = [] }, "anything"));
    }

    [Fact]
    public async Task New_files_publish_file_detected_once()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "inbox");
        Directory.CreateDirectory(folder);
        await using var bus = TestBus.Create();
        using var rec = new TestBus.Recorder(bus, EventTypes.FileDetected);
        var options = ConfigLoader.Parse($"[[watch]]\nname = \"in\"\npath = '{folder}'\nfilter = \"*.txt\"\n");
        using var config = new ConfigStore(AppPaths.UnderRoot(dir.Path), bus, NullLogger<ConfigStore>.Instance, options);
        using var watcher = new FileWatchService(config, bus, NullLogger<FileWatchService>.Instance);
        await watcher.StartAsync(default);
        Assert.True(Assert.Single(watcher.Status).Active);

        // Written in several bursts, like a download; plus a file the filter excludes.
        var path = Path.Combine(folder, "Hello.TXT");
        await File.WriteAllTextAsync(path, "one");
        await File.AppendAllTextAsync(path, " two");
        await File.WriteAllTextAsync(Path.Combine(folder, "skip.md"), "x");
        // A browser finishing a download: temp name → real name counts as "created".
        var temp = Path.Combine(folder, "report.txt.crdownload");
        await File.WriteAllTextAsync(temp, "data");
        File.Move(temp, Path.Combine(folder, "report.txt"));

        var events = await rec.WaitForAsync(e => e.Count >= 2, timeoutMs: 10_000);
        await Task.Delay(FileWatchService.Debounce + TimeSpan.FromMilliseconds(500));
        events = rec.Snapshot();
        Assert.Equal(2, events.Count);
        var hello = events.Select(e => e.DataAs<FileDetectedEvent>()!).Single(e => e.Name == "Hello.TXT");
        Assert.Equal("in", hello.Watch);
        Assert.Equal("created", hello.Change);
        Assert.Equal(".txt", hello.Extension);
        Assert.Equal(7, hello.Size);
        Assert.Equal(EventSources.Watcher, events[0].Source);
        Assert.Contains(events, e => e.DataAs<FileDetectedEvent>()!.Name == "report.txt" && e.DataAs<FileDetectedEvent>()!.Change == "created");

        await watcher.StopAsync(default);
    }
}
