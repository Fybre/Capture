using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capture.Scanner;

/// <summary>Windows Image Acquisition automation implementation. All COM work runs on a dedicated
/// STA thread so device discovery and physical transfers never block Avalonia's UI thread.</summary>
[SupportedOSPlatform("windows")]
public sealed class WiaScanSource : IScanSource
{
    private const int WiaDeviceTypeScanner = 1;
    private const int WiaIpsXres = 6147;
    private const int WiaIpsYres = 6148;
    private const int WiaIpsCurIntent = 6146;
    private const int WiaIntentImageTypeColor = 0x1;
    private const int WiaIntentImageTypeGrayscale = 0x2;
    private const int WiaSourceFeeder = 0x1;
    private const int WiaSourceFlatbed = 0x2;
    private const int WiaSourceDuplex = 0x4;
    private const string WiaFormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    private const string WiaFormatTiff = "{B96B3CB1-0728-11D3-9D7B-0000F81EF32E}";
    private const string FlatbedCategory = "{FB607B1F-43F3-488B-855B-FB703EC342A6}";
    private const string FeederCategory = "{FE131934-F84C-42AD-8DA4-6129CDDD7288}";

    public bool IsAvailable => OperatingSystem.IsWindows() && Type.GetTypeFromProgID("WIA.DeviceManager") is not null;

    public Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return Task.FromResult<IReadOnlyList<ScanDevice>>([]);

        return RunStaAsync<IReadOnlyList<ScanDevice>>(ListDevices, cancellationToken);
    }

    public async IAsyncEnumerable<ScannedPage> ScanAsync(
        ScanOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Windows Image Acquisition is not available.");

        var page = await RunStaAsync(() => Scan(options), cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            try { File.Delete(page.FilePath); }
            catch (IOException) { }
            cancellationToken.ThrowIfCancellationRequested();
        }
        yield return page;
    }

    private static IReadOnlyList<ScanDevice> ListDevices()
    {
        dynamic? manager = null;
        var devices = new List<ScanDevice>();
        try
        {
            manager = CreateDeviceManager();
            foreach (dynamic info in manager.DeviceInfos)
            {
                try
                {
                    if ((int)info.Type != WiaDeviceTypeScanner)
                        continue;

                    var id = (string)info.DeviceID;
                    var name = TryGetPropertyValue(info.Properties, "Name") as string ?? id;
                    devices.Add(ReadCapabilities(info, id, name));
                }
                finally
                {
                    Release(info);
                }
            }
        }
        finally
        {
            Release(manager);
        }

        return devices;
    }

    private static ScanDevice ReadCapabilities(dynamic deviceInfo, string id, string name)
    {
        dynamic? device = null;
        try
        {
            device = deviceInfo.Connect();
            dynamic? flatbed = FindItem(device, ScanSourceKind.Flatbed, allowFallback: false);
            dynamic? feeder = FindItem(device, ScanSourceKind.Feeder, allowFallback: false);
            try
            {
                var resolutionItem = flatbed ?? feeder;
                IReadOnlyList<int> dpis = resolutionItem is null
                    ? []
                    : AllowedIntValues(resolutionItem.Properties, WiaIpsXres);
                var duplex = feeder is not null && SupportsFlag(feeder.Properties, "Document Handling Select", WiaSourceDuplex);
                return new ScanDevice(id, name, dpis, flatbed is not null, feeder is not null, duplex);
            }
            finally
            {
                Release(flatbed);
                Release(feeder);
            }
        }
        catch (Exception)
        {
            // A busy device can still be listed and selected; scan will surface the concrete error.
            return new ScanDevice(id, name);
        }
        finally
        {
            Release(device);
        }
    }

    private static ScannedPage Scan(ScanOptions options)
    {
        dynamic? manager = null;
        dynamic? deviceInfo = null;
        dynamic? device = null;
        dynamic? item = null;
        dynamic? imageFile = null;
        var feederScan = options.Source == ScanSourceKind.Feeder;
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"capture-wia-{Guid.NewGuid():N}{(feederScan ? ".tiff" : ".png")}");
        var succeeded = false;
        try
        {
            manager = CreateDeviceManager();
            foreach (dynamic candidate in manager.DeviceInfos)
            {
                if ((string)candidate.DeviceID == options.DeviceId)
                {
                    deviceInfo = candidate;
                    break;
                }
                Release(candidate);
            }

            if (deviceInfo is null)
                throw new InvalidOperationException($"Scanner '{options.DeviceId}' was not found.");

            device = deviceInfo.Connect();
            item = FindItem(device, options.Source, allowFallback: options.Source == ScanSourceKind.Flatbed)
                ?? throw new InvalidOperationException(options.Source == ScanSourceKind.Feeder
                    ? "The selected scanner has no document feeder."
                    : "The selected scanner has no transferable scan source.");

            var effectiveDpi = SetNearestIntProperty(item.Properties, WiaIpsXres, options.Dpi);
            SetNearestIntProperty(item.Properties, WiaIpsYres, effectiveDpi);
            SetIntProperty(item.Properties, WiaIpsCurIntent,
                options.ColorMode == ScanColorMode.Grayscale ? WiaIntentImageTypeGrayscale : WiaIntentImageTypeColor);

            var sourceFlags = options.Source == ScanSourceKind.Feeder ? WiaSourceFeeder : WiaSourceFlatbed;
            if (options.Duplex)
            {
                if (options.Source != ScanSourceKind.Feeder)
                    throw new InvalidOperationException("Duplex scanning requires the document feeder.");
                if (!SupportsFlag(item.Properties, "Document Handling Select", WiaSourceDuplex))
                    throw new InvalidOperationException("The selected document feeder does not support duplex scanning.");
                sourceFlags |= WiaSourceDuplex;
            }
            SetNamedIntPropertyIfPresent(item.Properties, "Document Handling Select", sourceFlags);
            if (feederScan)
                SetNamedIntPropertyIfPresent(item.Properties, "Pages", 0); // all loaded sheets

            // TIFF can carry every sheet/side returned by an ADF; SkiaImagePageImporter expands its
            // frames into individual pages. Flatbed stays PNG to avoid an unnecessary container.
            imageFile = item.Transfer(feederScan ? WiaFormatTiff : WiaFormatPng);
            imageFile.SaveFile(outputPath);
            var width = Convert.ToInt32(imageFile.Width);
            var height = Convert.ToInt32(imageFile.Height);
            var actualDpi = Convert.ToInt32(imageFile.HorizontalResolution);
            succeeded = true;
            return new ScannedPage(outputPath, width, height, actualDpi > 0 ? actualDpi : effectiveDpi);
        }
        finally
        {
            Release(imageFile);
            Release(item);
            Release(device);
            Release(deviceInfo);
            Release(manager);
            if (!succeeded && File.Exists(outputPath))
            {
                try { File.Delete(outputPath); }
                catch (IOException) { }
            }
        }
    }

    private static dynamic? FindItem(dynamic device, ScanSourceKind source, bool allowFallback)
    {
        var wanted = source == ScanSourceKind.Feeder ? FeederCategory : FlatbedCategory;
        dynamic? fallback = null;
        foreach (dynamic candidate in device.Items)
        {
            var category = Convert.ToString(TryGetPropertyValue(candidate.Properties, "Item Category"));
            if (string.Equals(category, wanted, StringComparison.OrdinalIgnoreCase))
            {
                Release(fallback);
                return candidate;
            }
            if (allowFallback && fallback is null)
                fallback = candidate;
            else
                Release(candidate);
        }
        return fallback;
    }

    private static dynamic CreateDeviceManager()
    {
        var type = Type.GetTypeFromProgID("WIA.DeviceManager")
            ?? throw new InvalidOperationException("WIA.DeviceManager is not registered on this machine.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create a WIA.DeviceManager instance.");
    }

    private static object? TryGetPropertyValue(dynamic properties, string name)
    {
        foreach (dynamic property in properties)
        {
            try
            {
                if (string.Equals((string)property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.get_Value();
            }
            finally
            {
                Release(property);
            }
        }
        return null;
    }

    private static IReadOnlyList<int> AllowedIntValues(dynamic properties, int propertyId)
    {
        foreach (dynamic property in properties)
        {
            try
            {
                if ((int)property.PropertyID != propertyId)
                    continue;
                var values = new List<int>();
                try
                {
                    foreach (dynamic value in property.SubTypeValues)
                        values.Add(Convert.ToInt32(value));
                }
                catch (Exception)
                {
                    var min = Convert.ToInt32(property.SubTypeMin);
                    var max = Convert.ToInt32(property.SubTypeMax);
                    var step = Math.Max(1, Convert.ToInt32(property.SubTypeStep));
                    for (var value = min; value <= max && values.Count < 100; value += step)
                        values.Add(value);
                }
                return values.Where(value => value > 0).Distinct().Order().ToList();
            }
            finally
            {
                Release(property);
            }
        }
        return [];
    }

    private static int SetNearestIntProperty(dynamic properties, int propertyId, int requested)
    {
        IReadOnlyList<int> allowed = AllowedIntValues(properties, propertyId);
        var selected = allowed.Count == 0 ? requested : allowed.MinBy(value => Math.Abs(value - requested));
        SetIntProperty(properties, propertyId, selected);
        return selected;
    }

    private static void SetIntProperty(dynamic properties, int propertyId, int value)
    {
        foreach (dynamic property in properties)
        {
            try
            {
                if ((int)property.PropertyID != propertyId)
                    continue;
                object boxed = value;
                property.set_Value(ref boxed);
                return;
            }
            finally
            {
                Release(property);
            }
        }
        throw new InvalidOperationException($"The scanner does not expose required WIA property {propertyId}.");
    }

    private static bool SupportsFlag(dynamic properties, string name, int flag)
    {
        foreach (dynamic property in properties)
        {
            try
            {
                if (!string.Equals((string)property.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    foreach (dynamic value in property.SubTypeValues)
                    {
                        if ((Convert.ToInt32(value) & flag) != 0)
                            return true;
                    }
                }
                catch (Exception) { }
                return (Convert.ToInt32(property.get_Value()) & flag) != 0;
            }
            finally
            {
                Release(property);
            }
        }
        return false;
    }

    private static void SetNamedIntPropertyIfPresent(dynamic properties, string name, int value)
    {
        foreach (dynamic property in properties)
        {
            try
            {
                if (!string.Equals((string)property.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                object boxed = value;
                property.set_Value(ref boxed);
                return;
            }
            finally
            {
                Release(property);
            }
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Capture WIA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // WIA Automation exposes no safe cancellation primitive for an in-flight Transfer. Once the
        // STA job has started, let it finish and let ScanAsync discard its result if cancellation was
        // requested; abandoning this task would leave an unobserved COM operation holding the device.
        return completion.Task;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
