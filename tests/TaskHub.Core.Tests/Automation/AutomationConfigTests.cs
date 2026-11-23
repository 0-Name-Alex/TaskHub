using TaskHub.Core.Automation;
using TaskHub.Core.Configuration;

namespace TaskHub.Core.Tests.Automation;

public class AutomationConfigTests
{
    [Fact]
    public void Default_file_has_automation_defaults_and_no_rules()
    {
        var o = ConfigLoader.Parse(ConfigLoader.DefaultToml);
        Assert.True(o.Automation.RulesEnabled);
        Assert.Equal(10, o.Automation.IdleMinutes);
        Assert.Equal(20, o.Automation.BatteryLowPercent);
        Assert.False(o.Automation.AllowTaskPrograms);
        Assert.Equal(9, o.Launcher.MaxResults);
        Assert.Equal("Ctrl+Alt+V", o.Hotkeys.CaptureClipboard);
        Assert.Equal("Ctrl+Alt+S", o.Hotkeys.CaptureScreenshot);
        Assert.Empty(o.GetAutomationConfig().Rules);
        Assert.Empty(o.GetAutomationConfig().Errors);
    }

    [Fact]
    public void The_commented_examples_in_the_default_file_are_valid_when_uncommented()
    {
        var lines = ConfigLoader.DefaultToml.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(l => l.TrimStart().StartsWith("# [[", StringComparison.Ordinal) || l.TrimStart().StartsWith("# name", StringComparison.Ordinal)
                         || l.TrimStart().StartsWith("# when", StringComparison.Ordinal) || l.TrimStart().StartsWith("# if", StringComparison.Ordinal)
                         || l.TrimStart().StartsWith("# then", StringComparison.Ordinal) || l.TrimStart().StartsWith("# hotkey", StringComparison.Ordinal)
                         || l.TrimStart().StartsWith("# path", StringComparison.Ordinal) || l.TrimStart().StartsWith("# filter", StringComparison.Ordinal)
                         || l.TrimStart().StartsWith("# target", StringComparison.Ordinal) || l.TrimStart().StartsWith("# keywords", StringComparison.Ordinal)
                ? l.TrimStart()[2..]
                : l);
        // The examples sit inside [automation]; move them to the end so they don't swallow [launcher].
        var text = string.Join('\n', lines);
        var examplesStart = text.IndexOf("\n[[rules]]", StringComparison.Ordinal) + 1;
        var launcherStart = text.IndexOf("\n[launcher]", StringComparison.Ordinal) + 1;
        var reordered = text[..examplesStart] + text[launcherStart..] + "\n" + text[examplesStart..launcherStart];

        var o = ConfigLoader.Parse(reordered);
        var a = o.GetAutomationConfig();
        Assert.Equal(3, a.Rules.Count);
        Assert.All(a.Rules, r => Assert.Null(r.Error));
        Assert.Equal(["reminder.due"], a.Rules[0].When);
        Assert.Equal("Ctrl+Alt+F", a.Rules[2].Hotkey);
        Assert.Equal(ActionNames.Run, a.Rules[2].Then[1].Name);
        Assert.Equal("pwsh", a.Rules[2].Then[1].Get("program"));
        Assert.Equal(@"-File C:\scripts\focus.ps1", a.Rules[2].Then[1].Get("args"));
        Assert.Single(a.Watches);
        Assert.Equal(["*.pdf", "*.docx"], a.Watches[0].Filters);
        Assert.Single(a.Shortcuts);
    }

    [Fact]
    public void A_bad_rule_is_kept_with_an_error_instead_of_rejecting_the_file()
    {
        const string toml = """
            [[rules]]
            name = "Good"
            when = ["task.created", "task.updated"]
            if = "task.tags contains 'x'"
            then = ["notify Hi {task.title}", { action = "open.url", url = "https://example.com" }]
            cooldown = 30

            [[rules]]
            name = "Bad"
            when = "Not A Pattern!"
            if = "task.title =="
            then = ["explode"]

            [[rules]]
            name = "Good"
            hotkey = "Ctrl+Alt+1"
            then = "dnd toggle"

            [[rules]]
            then = []
            """;
        var rules = ConfigLoader.Parse(toml).GetAutomationConfig().Rules;

        Assert.Equal(4, rules.Count);
        var good = rules[0];
        Assert.Null(good.Error);
        Assert.True(good.IsRunnable);
        Assert.Equal(["task.created", "task.updated"], good.When);
        Assert.Equal(30, good.CooldownSeconds);
        Assert.Equal(ActionNames.Open, good.Then[1].Name);
        Assert.Equal("https://example.com", good.Then[1].Get("target"));

        var bad = rules[1];
        Assert.False(bad.IsRunnable);
        Assert.Contains("not an event pattern", bad.Error);
        Assert.Contains("if:", bad.Error);
        Assert.Contains("Unknown action", bad.Error);

        Assert.Contains("same name", rules[2].Error);
        Assert.Equal("rule:Good", rules[2].HotkeyId);
        Assert.Contains("needs a name", rules[3].Error);
    }

    [Fact]
    public void Automation_settings_are_validated()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Parse("[automation]\nidle_minutes = -1\n[launcher]\nmax_results = 1"));
        Assert.Contains("idle_minutes", ex.Message);
        Assert.Contains("max_results", ex.Message);
    }

    [Fact]
    public void Watches_get_defaults()
    {
        var w = ConfigLoader.Parse("[[watch]]\npath = \"~/Downloads\"\n[[watch]]\nname = \"x\"\n").GetAutomationConfig().Watches;
        Assert.Equal("downloads", w[0].Name);
        Assert.Equal(["created"], w[0].Events);
        Assert.Empty(w[0].Filters);
        Assert.Null(w[0].Error);
        Assert.Contains("needs a path", w[1].Error);
    }
}
