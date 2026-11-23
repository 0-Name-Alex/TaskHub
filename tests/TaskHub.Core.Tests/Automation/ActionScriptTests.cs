using System.Text.Json.Nodes;
using TaskHub.Core.Automation;

namespace TaskHub.Core.Tests.Automation;

public class ActionScriptTests
{
    [Fact]
    public void Parses_each_kind_of_line()
    {
        var actions = ActionScript.Parse("""
            # comment lines and blank lines are ignored

            open https://meet.example.com/standup
            open "C:\Program Files\App\app.exe" --profile work
            run python "C:\scripts\sort it.py" {path}
            notify Stand-up in 5 | Grab a coffee
            add Pay {name} tomorrow #finance
            done
            snooze 15
            dnd on
            copy {task.title}
            wait 2.5
            log hello
            screenshot
            clip
            """);

        Assert.Equal(
            [ActionNames.Open, ActionNames.Open, ActionNames.Run, ActionNames.Notify, ActionNames.TaskAdd, ActionNames.TaskComplete,
             ActionNames.TaskSnooze, ActionNames.Dnd, ActionNames.ClipboardSet, ActionNames.Wait, ActionNames.Log,
             ActionNames.CaptureScreenshot, ActionNames.CaptureClipboard],
            actions.Select(a => a.Name));

        Assert.Equal("https://meet.example.com/standup", actions[0].Get("target"));
        Assert.Equal(@"C:\Program Files\App\app.exe", actions[1].Get("target"));
        Assert.Equal("--profile work", actions[1].Get("args"));
        Assert.Equal("python", actions[2].Get("program"));
        Assert.Equal("\"C:\\scripts\\sort it.py\" {path}", actions[2].Get("args"));
        Assert.Equal("Stand-up in 5", actions[3].Get("title"));
        Assert.Equal("Grab a coffee", actions[3].Get("body"));
        Assert.Equal("Pay {name} tomorrow #finance", actions[4].Get("text"));
        Assert.Equal("15", actions[6].Get("minutes"));
        Assert.Equal("on", actions[7].Get("state"));
        Assert.Equal("2.5", actions[9].Get("seconds"));
        Assert.Equal("open https://meet.example.com/standup", actions[0].Describe());
    }

    [Theory]
    [InlineData("explode now", "Unknown action")]
    [InlineData("open", "needs")]
    [InlineData("snooze soon", "minutes")]
    [InlineData("dnd maybe", "on, off or toggle")]
    [InlineData("wait 1000", "seconds")]
    [InlineData("done please", "doesn't take")]
    [InlineData("open \"C:\\unterminated", "quote")]
    public void Bad_lines_explain_themselves(string line, string mentions)
    {
        var ex = Assert.Throws<AutomationException>(() => ActionScript.Parse(line));
        Assert.Contains(mentions, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Errors_name_the_line()
    {
        Assert.False(ActionScript.TryParse("open https://x.example\nfrobnicate", out _, out var error));
        Assert.StartsWith("Line 2:", error);
    }

    [Fact]
    public void Splits_arguments_with_quotes_and_literal_backslashes()
    {
        Assert.Equal(["-File", @"C:\my scripts\x.ps1", "--name", "a b", ""],
            ActionScript.SplitArguments(@"-File ""C:\my scripts\x.ps1"" --name ""a b"" """""));
        Assert.Empty(ActionScript.SplitArguments("   "));
    }

    [Fact]
    public void Templates_fill_fields_and_keep_other_braces()
    {
        var ctx = new JsonObject
        {
            ["task"] = new JsonObject { ["title"] = "Pay rent", ["tags"] = new JsonArray("home", "money") },
            ["percent"] = 15,
        };
        Assert.Equal("Pay rent (home, money) 15%", Template.Render("{task.title} ({task.tags}) {percent}%", ctx));
        Assert.Equal("missing: []", Template.Render("missing: [{nope.nothing}]", ctx));
        Assert.Equal("{\"json\": 1} {literal}", Template.Render("{\"json\": 1} {{literal}}", ctx));
    }
}
