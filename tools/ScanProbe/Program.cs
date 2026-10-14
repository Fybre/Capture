// ScanProbe: tries Capture's Windows scanner sources (WIA and TWAIN) outside the app, printing what
// each step does, for testing scanner drivers. See tools/ScanProbe/README.md.
using System.Diagnostics;
using Capture.Scanner;
using NTwain;
using NTwain.Data;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("ScanProbe tests the Windows scanner sources; run it on Windows.");
    return 2;
}

var command = args.FirstOrDefault() ?? "help";
try
{
    switch (command)
    {
        case "list":
            return await ListAsync();
        case "scan" when args.Length >= 2:
            return await ScanAsync(args[1], args[2..]);
        case "twain-caps" when args.Length >= 2:
            return TwainCaps(args[1]);
        default:
            Console.WriteLine("""
                Usage:
                  ScanProbe list
                  ScanProbe scan <device id> [--dpi 300] [--feeder] [--duplex] [--gray] [--ui] [--stop-after N] [--out folder]
                  ScanProbe twain-caps "<TWAIN product name>"
                """);
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex}");
    return 1;
}

static CompositeScanSource Sources() =>
    new(("WIA", new WiaScanSource()), ("TWAIN", new TwainScanSource()));

static async Task<int> ListAsync()
{
    Console.WriteLine($"Process: {(Environment.Is64BitProcess ? "64" : "32")}-bit");
    Console.WriteLine($"WIA available: {new WiaScanSource().IsAvailable}");
    Console.WriteLine($"TWAIN available (twaindsm.dll found): {new TwainScanSource().IsAvailable}");
    var watch = Stopwatch.StartNew();
    var devices = await Sources().ListDevicesAsync();
    Console.WriteLine($"{devices.Count} device(s) in {watch.Elapsed.TotalSeconds:0.0}s:");
    foreach (var device in devices)
    {
        Console.WriteLine($"- {device.Name}");
        Console.WriteLine($"    id: {device.Id}");
        Console.WriteLine($"    flatbed={device.SupportsFlatbed} feeder={device.SupportsFeeder} duplex={device.SupportsDuplex} color={device.SupportsColor} gray={device.SupportsGrayscale}");
        Console.WriteLine($"    dpi: {string.Join(", ", device.SupportedDpis ?? [])}");
    }
    return 0;
}

static async Task<int> ScanAsync(string deviceId, string[] options)
{
    string? Option(string name) => Array.IndexOf(options, name) is var at and >= 0 && at + 1 < options.Length ? options[at + 1] : null;
    var dpi = int.TryParse(Option("--dpi"), out var value) ? value : 300;
    var stopAfter = int.TryParse(Option("--stop-after"), out var limit) ? limit : 0;
    var outFolder = Option("--out") ?? Path.Combine(Environment.CurrentDirectory, $"scanprobe-{DateTime.Now:yyyyMMdd-HHmmss}");
    Directory.CreateDirectory(outFolder);
    var scanOptions = new ScanOptions(
        deviceId, dpi, options.Contains("--duplex"),
        options.Contains("--gray") ? ScanColorMode.Grayscale : ScanColorMode.Color,
        options.Contains("--feeder") ? ScanSourceKind.Feeder : ScanSourceKind.Flatbed,
        options.Contains("--ui"));
    Console.WriteLine($"Scanning: {scanOptions}");

    var sources = Sources();
    await sources.ListDevicesAsync(); // registers which source owns the id
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; Console.WriteLine("Stopping…"); stop.Cancel(); };
    var watch = Stopwatch.StartNew();
    var pages = 0;
    try
    {
        await foreach (var page in sources.ScanAsync(scanOptions, stop.Token))
        {
            pages++;
            var target = Path.Combine(outFolder, $"page-{pages:D4}{Path.GetExtension(page.FilePath)}");
            File.Move(page.FilePath, target);
            Console.WriteLine($"  page {pages}: {page.Width}x{page.Height} @ {page.Dpi} dpi, {new FileInfo(target).Length / 1024} KB, {watch.Elapsed.TotalSeconds:0.0}s");
            if (stopAfter > 0 && pages == stopAfter)
            {
                Console.WriteLine($"Stopping after {stopAfter} page(s)…");
                stop.Cancel();
            }
        }
        Console.WriteLine($"Done: {pages} page(s) in {watch.Elapsed.TotalSeconds:0.0}s -> {outFolder}");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"Stopped: {pages} page(s) kept in {watch.Elapsed.TotalSeconds:0.0}s -> {outFolder}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ended with an error after {pages} page(s), {watch.Elapsed.TotalSeconds:0.0}s: {ex.Message}");
        return 1;
    }
}

// Every capability the driver says it supports, with its current value and allowed values — what to
// look at when a setting doesn't take.
static int TwainCaps(string productName)
{
    if (!OperatingSystem.IsWindows())
        return 2;
    using var session = new TwainAppSession();
    var sts = session.OpenDsm();
    Console.WriteLine($"OpenDsm: {sts.RC} {sts.ConditionCode}");
    if (!sts.IsSuccess)
        return 1;
    try
    {
        var source = session.GetSources().FirstOrDefault(item => ((string)item.ProductName).Trim() == productName);
        if (source is null)
        {
            Console.WriteLine($"No TWAIN source named \"{productName}\". Sources: {string.Join(", ", session.GetSources().Select(item => (string)item.ProductName))}");
            return 1;
        }

        sts = session.OpenSource(source);
        Console.WriteLine($"OpenSource: {sts.RC} {sts.ConditionCode}  ({source.Manufacturer} {source.ProductFamily} {source.ProductName}, protocol {source.ProtocolMajor}.{source.ProtocolMinor})");
        if (!sts.IsSuccess)
            return 1;
        foreach (var cap in session.Caps.CAP_SUPPORTEDCAPS.GetCurrent().Order())
        {
            var current = session.GetCapCurrentBoxed(cap, out var currentValues);
            var allowed = session.GetCapValuesBoxed(cap, out var container);
            var allowedText = allowed.IsSuccess ? Describe(container) : $"({allowed.ConditionCode})";
            Console.WriteLine($"{cap,-36} current={(current.IsSuccess ? string.Join("|", currentValues) : current.ConditionCode.ToString())}  allowed={allowedText}  query={session.QueryCapSupport(cap)}");
        }
        session.CloseSource();
        return 0;
    }
    finally
    {
        session.CloseDsm();
    }

    static string Describe(ValueContainer<object> container) => container.ContainerType switch
    {
        TWON.ONEVALUE => $"{container.OneValue}",
        TWON.ENUMERATION => $"[{string.Join(", ", container.EnumValue?.Items ?? [])}]",
        TWON.ARRAY => $"[{string.Join(", ", container.ArrayValue ?? [])}]",
        TWON.RANGE when container.RangeValue is { } range => $"{range.MinValue}..{range.MaxValue} step {range.StepSize}",
        _ => container.ContainerType.ToString()
    };
}
