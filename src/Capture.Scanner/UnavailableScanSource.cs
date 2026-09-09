namespace Capture.Scanner;

public sealed class UnavailableScanSource : IScanSource
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ScanDevice>>(Array.Empty<ScanDevice>());
    }

    public IAsyncEnumerable<ScannedPage> ScanAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Scanner support is not implemented yet.");
    }
}
