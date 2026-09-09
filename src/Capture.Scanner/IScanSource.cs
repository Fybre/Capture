namespace Capture.Scanner;

public sealed record ScanDevice(
    string Id,
    string Name,
    IReadOnlyList<int>? SupportedDpis = null,
    bool SupportsFlatbed = true,
    bool SupportsFeeder = false,
    bool SupportsDuplex = false,
    bool SupportsColor = true,
    bool SupportsGrayscale = true);

public enum ScanSourceKind
{
    Flatbed = 0,
    Feeder = 1
}

public enum ScanColorMode
{
    Color = 0,
    Grayscale = 1
}

public sealed record ScanOptions(
    string DeviceId,
    int Dpi,
    bool Duplex,
    ScanColorMode ColorMode = ScanColorMode.Color,
    ScanSourceKind Source = ScanSourceKind.Flatbed);

/// <summary>A scanner-owned temporary image handed to the caller. Once yielded, the caller owns the
/// file and must delete it after import (including on partial-job failure).</summary>
public sealed record ScannedPage(string FilePath, int Width, int Height, int Dpi);

public interface IScanSource
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<ScannedPage> ScanAsync(ScanOptions options, CancellationToken cancellationToken = default);
}
