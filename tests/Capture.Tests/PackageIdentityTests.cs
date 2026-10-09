using Capture.Core.Paths;

namespace Capture.Tests;

public class PackageIdentityTests
{
    [Fact]
    public void Tests_and_unpackaged_builds_are_not_packaged_and_keep_the_normal_data_folder()
    {
        // Test runs are never inside an MSIX package, on any OS.
        Assert.False(PackageIdentity.IsPackaged);
        Assert.Null(PackageIdentity.FamilyName);
        Assert.DoesNotContain(Path.Combine("Packages", ""), new AppPaths().Root);
    }
}
