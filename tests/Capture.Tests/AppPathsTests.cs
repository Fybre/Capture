using Capture.Core.Paths;

namespace Capture.Tests;

public class AppPathsTests
{
    [Fact]
    public void Document_paths_are_under_work_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-paths-test");
        var paths = new AppPaths(root);
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.Equal(Path.Combine(root, "capture.db"), paths.DatabasePath);
        Assert.StartsWith(paths.WorkDirectory, paths.DocumentDirectory(id));
        Assert.Equal(Path.Combine(paths.DocumentDirectory(id), "original.PDF"), paths.DocumentOriginalPath(id, "Invoice.PDF"));
        Assert.Equal(Path.Combine(paths.DocumentDirectory(id), "pages"), paths.DocumentPagesDirectory(id));
    }
}
