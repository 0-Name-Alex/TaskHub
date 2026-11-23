using TaskHub.Core.Events;

namespace TaskHub.Core.Tests.Events;

public class TopicPatternTests
{
    [Theory]
    [InlineData("*", "task.created", true)]
    [InlineData("task.*", "task.created", true)]
    [InlineData("task.*", "task.sub.thing", true)]
    [InlineData("task.*", "task", false)]
    [InlineData("task.*", "tasks.created", false)]
    [InlineData("task.created", "task.created", true)]
    [InlineData("task.created", "task.created.extra", false)]
    [InlineData("system.battery.*", "system.battery.low", true)]
    public void Matches(string pattern, string type, bool expected) =>
        Assert.Equal(expected, TopicPattern.Matches(pattern, type));

    [Theory]
    [InlineData("task.created", true)]
    [InlineData("system.battery.low", true)]
    [InlineData("a", true)]
    [InlineData("hotkey_failed-2", true)]
    [InlineData("Task.Created", false)]
    [InlineData("task..created", false)]
    [InlineData(".task", false)]
    [InlineData("task.", false)]
    [InlineData("task created", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidType(string? type, bool expected) =>
        Assert.Equal(expected, TopicPattern.IsValidType(type));

    [Theory]
    [InlineData("*", true)]
    [InlineData("task.*", true)]
    [InlineData("task.created", true)]
    [InlineData("*.created", false)]
    [InlineData("task.*.x", false)]
    [InlineData(".*", false)]
    public void IsValidPattern(string pattern, bool expected) =>
        Assert.Equal(expected, TopicPattern.IsValidPattern(pattern));
}
