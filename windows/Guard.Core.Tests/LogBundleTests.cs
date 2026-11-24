using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class LogBundleTests
{
    [Fact]
    public void Picks_newest_guard_logs_and_newest_update_log()
    {
        var picked = LogBundle.Pick(new[] { "guard-20261120.log", "guard-20261122.log", "guard-20261121.log", "guard-20261119.log",
            "update-1.4.0.log", "update-1.4.1.log", "other.txt" });
        Assert.Equal(new[] { "update-1.4.1.log", "guard-20261120.log", "guard-20261121.log", "guard-20261122.log" }, picked);
    }

    [Fact]
    public void Joins_files_with_headers_and_drops_nul()
    {
        var text = LogBundle.Build(new[] { ("a.log", "one\0\0\r\n"), ("b.log", "two") });
        Assert.Equal("===== a.log =====\none\n\n===== b.log =====\ntwo\n", text);
    }

    [Fact]
    public void Too_long_keeps_the_newest_lines()
    {
        var text = LogBundle.Build(new[] { ("a.log", string.Join("\n", Enumerable.Range(1, 1000).Select(i => $"line {i}"))) }, 100);
        Assert.StartsWith("(начало обрезано)\n", text);
        Assert.Contains("line 1000", text);
        Assert.DoesNotContain("line 1\n", text);
        Assert.True(text.Length <= 120);
    }
}
