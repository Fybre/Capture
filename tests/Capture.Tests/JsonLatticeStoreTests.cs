using Capture.Core.Lattice;
using Capture.Core.Paths;
using Capture.Storage;

namespace Capture.Tests;

public class JsonLatticeStoreTests
{
    [Fact]
    public async Task Roundtrips_lattice()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-json-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new JsonLatticeStore(paths);
        var id = Guid.NewGuid();
        var lattice = new PageLattice
        {
            PageNumber = 1,
            PixelWidth = 800,
            PixelHeight = 1000,
            Dpi = 150,
            Source = LatticeSource.PdfText,
            Words =
            [
                new LatticeWord { Text = "Invoice", Confidence = 100, X = 0.1f, Y = 0.2f, Width = 0.15f, Height = 0.03f }
            ]
        };

        await store.SaveAsync(id, lattice);
        var loaded = await store.GetAsync(id, 1);

        Assert.NotNull(loaded);
        Assert.Equal(LatticeSource.PdfText, loaded!.Source);
        var word = Assert.Single(loaded.Words);
        Assert.Equal("Invoice", word.Text);
        Assert.Equal(0.1f, word.X, 3);
    }
}
