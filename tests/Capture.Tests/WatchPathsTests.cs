using Capture.Core.Watch;

namespace Capture.Tests;

public class WatchPathsTests
{
    [Fact]
    public void Resolves_filename_into_watch_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-watch-root");
        var expected = Path.GetFullPath(Path.Combine(root, "invoice.pdf"));

        Assert.Equal(expected, WatchPaths.Resolve(Path.Combine(root, "invoice.pdf"), root));
        Assert.Equal(expected, WatchPaths.Resolve("invoice.pdf", root));
        Assert.True(WatchPaths.IsWatchable(Path.Combine(root, "invoice.pdf"), root));
    }

    [Fact]
    public void Rejects_processed_and_error_and_dotfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-watch-root");
        Assert.Null(WatchPaths.Resolve(Path.Combine(root, "processed", "invoice.pdf"), root));
        Assert.Null(WatchPaths.Resolve(Path.Combine("processed", "invoice.pdf"), root));
        Assert.Null(WatchPaths.Resolve(Path.Combine(root, "error", "invoice.pdf"), root));
        Assert.Null(WatchPaths.Resolve("._invoice.pdf", root));
        Assert.Null(WatchPaths.Resolve(".hidden.pdf", root));
        Assert.Null(WatchPaths.Resolve("notes.txt", root));
    }
}
