namespace Capture.Scanner;

public sealed record ScanDevice(string Id, string Name);

public sealed record ScanOptions(string DeviceId, int Dpi, bool Duplex);

public sealed record ScannedPage(byte[] PngBytes, int Width, int Height);

public interface IScanSource
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<ScannedPage> ScanAsync(ScanOptions options, CancellationToken cancellationToken = default);
}
