using Capture.Core.Watch;

namespace Capture.Tests;

public class WatchSettlerTests
{
    [Fact]
    public void Does_not_release_file_before_settle()
    {
        var path = TempPdf();
        var settler = new WatchSettler(TimeSpan.FromSeconds(2));
        settler.Note(path);

        Assert.Empty(settler.TakeReady(DateTimeOffset.Now));
    }

    [Fact]
    public void Releases_supported_file_after_settle()
    {
        var path = TempPdf();
        var settler = new WatchSettler(TimeSpan.FromSeconds(2));
        settler.Note(path);

        var ready = settler.TakeReady(DateTimeOffset.Now.AddSeconds(3));

        Assert.Equal(path, Assert.Single(ready));
        Assert.Empty(settler.TakeReady(DateTimeOffset.Now.AddSeconds(4)));
    }

    [Fact]
    public void Ignores_unsupported_extensions()
    {
        var settler = new WatchSettler(TimeSpan.Zero);
        settler.Note(Path.Combine(Path.GetTempPath(), "notes.txt"));

        Assert.Empty(settler.TakeReady(DateTimeOffset.Now.AddMinutes(1)));
    }

    [Fact]
    public void Keeps_locked_file_until_unlocked()
    {
        var path = TempPdf();
        var settler = new WatchSettler(TimeSpan.Zero);
        settler.Note(path);

        Assert.Empty(settler.TakeReady(DateTimeOffset.Now, _ => false));
        Assert.Equal(path, Assert.Single(settler.TakeReady(DateTimeOffset.Now, _ => true)));
    }

    [Fact]
    public void Does_not_reemit_claimed_file_until_gone()
    {
        var path = TempPdf();
        var settler = new WatchSettler(TimeSpan.Zero);
        settler.Note(path);
        Assert.Equal(path, Assert.Single(settler.TakeReady(DateTimeOffset.Now)));

        settler.Note(path);
        Assert.Empty(settler.TakeReady(DateTimeOffset.Now.AddMinutes(1)));

        File.Delete(path);
        settler.ReleaseGone(File.Exists);
        File.WriteAllText(path, "x");
        settler.Note(path);
        Assert.Equal(path, Assert.Single(settler.TakeReady(DateTimeOffset.Now)));
    }

    [Fact]
    public void Drops_missing_files()
    {
        var path = TempPdf();
        var settler = new WatchSettler(TimeSpan.Zero);
        settler.Note(path);
        File.Delete(path);

        Assert.Empty(settler.TakeReady(DateTimeOffset.Now));
    }

    private static string TempPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(path, "x");
        return path;
    }
}
