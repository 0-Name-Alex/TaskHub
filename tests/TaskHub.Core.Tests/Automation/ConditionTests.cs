using System.Text.Json.Nodes;
using TaskHub.Core.Automation;

namespace TaskHub.Core.Tests.Automation;

public class ConditionTests
{
    private static JsonObject Context() => new()
    {
        ["task"] = new JsonObject
        {
            ["title"] = "Call the bank",
            ["priority"] = "high",
            ["tags"] = new JsonArray("standup", "work"),
            ["notes"] = null,
        },
        ["name"] = "Invoice-2041.PDF",
        ["extension"] = ".pdf",
        ["percent"] = 15,
        ["missed"] = false,
        ["event"] = new JsonObject { ["type"] = "reminder.due", ["source"] = "core" },
    };

    [Theory]
    [InlineData("task.tags contains 'standup'", true)]
    [InlineData("task.tags contains 'STANDUP'", true)]
    [InlineData("task.tags contains 'home'", false)]
    [InlineData("task.tags not contains 'home'", true)]
    [InlineData("task.priority == 'high'", true)]
    [InlineData("task.priority = 'HIGH'", true)]
    [InlineData("task.priority != 'high'", false)]
    [InlineData("task.title startswith 'call'", true)]
    [InlineData("task.title endswith 'bank'", true)]
    [InlineData("task.title contains 'the'", true)]
    [InlineData("extension in ['.pdf', '.docx']", true)]
    [InlineData("extension not in ['.pdf']", false)]
    [InlineData("name matches '^invoice-\\d+'", true)]
    [InlineData("percent <= 15", true)]
    [InlineData("percent < 15", false)]
    [InlineData("percent > 9", true)]
    [InlineData("percent >= '20'", false)]
    [InlineData("missed", false)]
    [InlineData("not missed", true)]
    [InlineData("missed == false", true)]
    [InlineData("task.notes", false)]
    [InlineData("task.notes == ''", true)]
    [InlineData("nothing.here == null", true)]
    [InlineData("task.title", true)]
    [InlineData("event.type == 'reminder.due' and (task.priority == 'urgent' or task.tags contains 'work')", true)]
    [InlineData("event.type == 'reminder.due' && task.priority == 'urgent' || percent < 20", true)]
    [InlineData("not (percent < 20)", false)]
    [InlineData("task.title == 'It''s'", false)]
    public void Evaluates(string expression, bool expected) =>
        Assert.Equal(expected, Condition.Parse(expression).Evaluate(Context()));

    [Theory]
    [InlineData("", "empty")]
    [InlineData("task.title ==", "ends too early")]
    [InlineData("(percent > 1", "Missing ')'")]
    [InlineData("name matches '['", "Invalid pattern")]
    [InlineData("name matches other", "quoted pattern")]
    [InlineData("percent > 1 extra", "Unexpected 'extra'")]
    [InlineData("'open", "Unclosed")]
    [InlineData("a ! b", "Unexpected '!'")]
    public void Reports_syntax_errors(string expression, string mentions)
    {
        Assert.False(Condition.TryParse(expression, out _, out var error));
        Assert.Contains(mentions, error, StringComparison.OrdinalIgnoreCase);
    }
}
