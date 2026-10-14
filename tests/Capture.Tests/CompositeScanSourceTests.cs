using System.Runtime.CompilerServices;
using Capture.Scanner;

namespace Capture.Tests;

public class CompositeScanSourceTests
{
    [Fact]
    public async Task Devices_from_every_source_are_listed_with_their_source_and_scans_go_back_to_it()
    {
        var wia = new FakeSource("wia-1", "Canon LiDE 400");
        var twain = new FakeSource("twain:Canon R50", "Canon R50");
        var composite = new CompositeScanSource(("WIA", wia), ("TWAIN", twain));

        var devices = await composite.ListDevicesAsync();
        await foreach (var _ in composite.ScanAsync(new ScanOptions("twain:Canon R50", 300, false))) { }
        await foreach (var _ in composite.ScanAsync(new ScanOptions("wia-1", 300, false))) { }

        Assert.Equal(["Canon LiDE 400 (WIA)", "Canon R50 (TWAIN)"], devices.Select(device => device.Name));
        Assert.Equal(["twain:Canon R50"], twain.Scanned);
        Assert.Equal(["wia-1"], wia.Scanned);
    }

    [Fact]
    public async Task One_broken_source_does_not_hide_the_others_but_all_failing_is_reported()
    {
        var working = new FakeSource("wia-1", "Flatbed");
        var broken = new FakeSource("twain:x", "x") { Failure = new InvalidOperationException("driver crashed") };

        var devices = await new CompositeScanSource(("WIA", working), ("TWAIN", broken)).ListDevicesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CompositeScanSource(("TWAIN", broken)).ListDevicesAsync());

        Assert.Equal("Flatbed (WIA)", Assert.Single(devices).Name);
        Assert.Equal("driver crashed", error.Message);
    }

    [Fact]
    public async Task Unavailable_sources_are_skipped()
    {
        var off = new FakeSource("a", "Off") { Available = false };
        var on = new FakeSource("b", "On");
        var composite = new CompositeScanSource(("WIA", off), ("TWAIN", on));

        Assert.True(composite.IsAvailable);
        Assert.Equal("On (TWAIN)", Assert.Single(await composite.ListDevicesAsync()).Name);
        Assert.False(new CompositeScanSource(("WIA", off)).IsAvailable);
    }

    private sealed class FakeSource(string id, string name) : IScanSource
    {
        public bool Available { get; init; } = true;
        public Exception? Failure { get; init; }
        public List<string> Scanned { get; } = [];
        public bool IsAvailable => Available;

        public Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default) =>
            Failure is not null ? Task.FromException<IReadOnlyList<ScanDevice>>(Failure) : Task.FromResult<IReadOnlyList<ScanDevice>>([new ScanDevice(id, name)]);

        public async IAsyncEnumerable<ScannedPage> ScanAsync(ScanOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Scanned.Add(options.DeviceId);
            await Task.CompletedTask;
            yield break;
        }
    }
}
