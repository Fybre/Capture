namespace Capture.Scanner;

/// <summary>Lists the scanners of several sources together — on Windows, WIA and TWAIN, since many
/// scanners have only one of the two and many have both — and sends each scan to the source the
/// device came from. Devices are labelled with their source's name, e.g. "Canon R50 (TWAIN)", so a
/// scanner listed by both can be told apart.</summary>
public sealed class CompositeScanSource(params (string Label, IScanSource Source)[] sources) : IScanSource
{
    private readonly Dictionary<string, IScanSource> _owners = new(StringComparer.Ordinal);

    public bool IsAvailable => sources.Any(entry => entry.Source.IsAvailable);

    /// <summary>One source failing to list (a broken driver) doesn't hide the others' scanners; if
    /// every available source fails, the first error is thrown.</summary>
    public async Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<ScanDevice>();
        Exception? firstError = null;
        var listed = 0;
        foreach (var (label, source) in sources.Where(entry => entry.Source.IsAvailable))
        {
            try
            {
                foreach (var device in await source.ListDevicesAsync(cancellationToken).ConfigureAwait(false))
                {
                    lock (_owners)
                        _owners[device.Id] = source;
                    devices.Add(device with { Name = $"{device.Name} ({label})" });
                }
                listed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstError ??= ex;
            }
        }

        if (listed == 0 && firstError is not null)
            throw firstError;
        return devices;
    }

    public IAsyncEnumerable<ScannedPage> ScanAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        IScanSource? owner;
        lock (_owners)
            _owners.TryGetValue(options.DeviceId, out owner);
        // Not listed this session (a device saved in Settings from an earlier run): TWAIN ids are
        // recognisable by their prefix; anything else goes to the first source.
        owner ??= options.DeviceId.StartsWith(TwainScanSource.IdPrefix, StringComparison.Ordinal)
            ? sources.FirstOrDefault(entry => entry.Source is TwainScanSource).Source
            : sources.FirstOrDefault(entry => entry.Source is not TwainScanSource).Source;
        return (owner ?? throw new InvalidOperationException("No scanner source is available.")).ScanAsync(options, cancellationToken);
    }
}
