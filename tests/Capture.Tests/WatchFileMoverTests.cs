using Capture.Core.Watch;

namespace Capture.Tests;

public class WatchFileMoverTests
{
    [Fact]
    public void Moves_success_to_processed_and_failure_to_error()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ok = Path.Combine(root, "ok.pdf");
        var bad = Path.Combine(root, "bad.pdf");
        File.WriteAllText(ok, "ok");
        File.WriteAllText(bad, "bad");

        var processed = WatchFileMover.Move(ok, root, success: true);
        var error = WatchFileMover.Move(bad, root, success: false);

        Assert.False(File.Exists(ok));
        Assert.False(File.Exists(bad));
        Assert.Equal(Path.Combine(root, "processed", "ok.pdf"), processed);
        Assert.Equal(Path.Combine(root, "error", "bad.pdf"), error);
        Assert.True(File.Exists(processed));
        Assert.True(File.Exists(error));
    }

    [Fact]
    public void Unique_name_when_destination_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-watch-" + Guid.NewGuid().ToString("N"));
        var processed = Path.Combine(root, "processed");
        Directory.CreateDirectory(processed);
        File.WriteAllText(Path.Combine(processed, "dup.pdf"), "old");
        var source = Path.Combine(root, "dup.pdf");
        File.WriteAllText(source, "new");

        var dest = WatchFileMover.Move(source, root, success: true);

        Assert.NotEqual(Path.Combine(processed, "dup.pdf"), dest);
        Assert.StartsWith(Path.Combine(processed, "dup-"), dest);
        Assert.True(File.Exists(dest));
    }
}
