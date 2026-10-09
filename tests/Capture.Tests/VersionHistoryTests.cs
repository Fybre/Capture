using Capture.App.Services;

namespace Capture.Tests;

public class VersionHistoryTests
{
    private static string HistoryPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Capture.App", "Assets", "VersionHistory.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Capture.App", "Assets", "VersionHistory.json");
    }

    private static IReadOnlyList<VersionHistoryEntry> Load(string? current = null)
    {
        using var stream = File.OpenRead(HistoryPath());
        return VersionHistory.Parse(stream, current);
    }

    [Fact]
    public void History_is_newest_first_with_unique_versions_and_changes_for_each()
    {
        var entries = Load();

        Assert.NotEmpty(entries);
        var versions = entries.Select(entry => VersionHistory.Normalize(entry.Version)).ToList();
        Assert.All(versions, Assert.NotNull);
        Assert.Equal(versions.OrderByDescending(version => version).ToList(), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.All(entries, entry => Assert.NotEmpty(entry.Changes));
    }

    [Fact]
    public void Only_the_newest_entry_may_be_undated()
    {
        var entries = Load();

        Assert.All(entries.Skip(1), entry => Assert.NotEqual(string.Empty, entry.DateDisplay));
    }

    [Theory]
    [InlineData("0.8.12")]
    [InlineData("v0.8.12")]
    [InlineData("0.8.12-ci.4")]
    public void The_running_version_is_marked_current(string running)
    {
        var current = Assert.Single(Load(running), entry => entry.IsCurrent);
        Assert.Equal("0.8.12", current.Version);
    }

    [Fact]
    public void A_development_build_marks_nothing_current()
    {
        Assert.DoesNotContain(Load("Development build"), entry => entry.IsCurrent);
    }
}
