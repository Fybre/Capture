using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using NTwain;
using NTwain.Data;
using NTwain.Events;
using SkiaSharp;

namespace Capture.Scanner;

/// <summary>Scanners with a TWAIN driver — most production document scanners (Canon imageFORMULA,
/// Ricoh/Fujitsu fi, Kodak Alaris, Epson DS…), which often have no WIA driver or only a basic one.
/// Uses NTwain, which runs TWAIN's message pump and transfers on threads of its own, with native
/// transfers (a bitmap per page), which every TWAIN driver supports. Pages are saved as PNGs and
/// yielded as they arrive. Device ids are <see cref="IdPrefix"/> plus the driver's product name.</summary>
[SupportedOSPlatform("windows")]
public sealed class TwainScanSource : IScanSource, IDisposable
{
    public const string IdPrefix = "twain:";

    // Resolutions offered when a driver gives a range rather than a list.
    private static readonly int[] CommonDpis = [75, 100, 150, 200, 240, 300, 400, 600, 1200];

    // One TWAIN session per process (a TWAIN rule); one operation on it at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TwainAppSession? _session;
    private ScanJob? _job;

    /// <summary>TWAIN needs the 64-bit data source manager, which scanner drivers install (Windows
    /// doesn't ship it), built for this process's architecture: TWAIN drivers are x64, so on Windows
    /// on ARM only the x64 build (running emulated) can use them.</summary>
    public bool IsAvailable => OperatingSystem.IsWindows() && UnavailableReason is null;

    /// <summary>Why <see cref="IsAvailable"/> is false, or null when TWAIN can be used.</summary>
    public string? UnavailableReason
    {
        get
        {
            if (DsmPath() is not { } path)
                return "twaindsm.dll (the TWAIN data source manager, installed by scanner drivers) wasn't found.";
            var dsm = PeArchitecture(path);
            return dsm is null || dsm == RuntimeInformation.ProcessArchitecture
                ? null
                : $"{path} is {dsm} but Capture is running as {RuntimeInformation.ProcessArchitecture}; "
                  + $"TWAIN drivers can only be used by a {dsm} build of Capture.";
        }
    }

    public async Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return [];

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var session = Session();
                var devices = new List<ScanDevice>();
                foreach (var source in session.GetSources())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = ((string)source.ProductName).Trim();
                    if (name.Length > 0)
                        devices.Add(ReadCapabilities(session, source, name));
                }

                return (IReadOnlyList<ScanDevice>)devices;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Opens the source briefly to ask what it can do. A driver that won't open (scanner off, network
    // scanner unreachable) is still listed, with default capabilities; scanning reports the problem.
    private static ScanDevice ReadCapabilities(TwainAppSession session, TWIdentityWrapper source, string name)
    {
        var id = IdPrefix + name;
        if (!session.OpenSource(source).IsSuccess)
            return new ScanDevice(id, name);
        try
        {
            var caps = session.Caps;
            var feeder = caps.CAP_FEEDERENABLED.Supports.HasFlag(TWQC.SET);
            // A feeder-only scanner usually only accepts "feeder on".
            var flatbed = !feeder || Values(caps.CAP_FEEDERENABLED.Get()).Contains(TW_BOOL.False);
            var duplex = caps.CAP_DUPLEX.GetCurrent().FirstOrDefault() is TWDX.X1PASSDUPLEX or TWDX.X2PASSDUPLEX;
            var pixelTypes = Values(caps.ICAP_PIXELTYPE.Get());
            return new ScanDevice(
                id,
                name,
                Resolutions(caps.ICAP_XRESOLUTION.Get()),
                flatbed,
                feeder,
                duplex,
                pixelTypes.Count == 0 || pixelTypes.Contains(TWPT.RGB),
                pixelTypes.Count == 0 || pixelTypes.Contains(TWPT.GRAY));
        }
        catch (Exception)
        {
            return new ScanDevice(id, name);
        }
        finally
        {
            session.CloseSource();
        }
    }

    public async IAsyncEnumerable<ScannedPage> ScanAsync(
        ScanOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("TWAIN isn't installed on this computer.");
        if (!options.DeviceId.StartsWith(IdPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Not a TWAIN scanner.", nameof(options));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var job = new ScanJob(options);
        var transferred = new HashSet<string>(StringComparer.Ordinal);
        TwainAppSession? session = null;
        try
        {
            session = await Task.Run(() => Start(job), cancellationToken).ConfigureAwait(false);
            using var stop = cancellationToken.Register(() => Stop(session, job, error: null));

            // Waits for each page, not the whole stack; the first page can take as long as the
            // driver's own settings window is open.
            var pageTimeout = TimeSpan.FromSeconds(Math.Max(120, Math.Min(900, options.Dpi * 0.5)));
            var firstPageTimeout = options.ShowDriverSettings ? TimeSpan.FromMinutes(30) : pageTimeout;
            var timedOut = false;
            while (true)
            {
                bool more;
                using (var wait = new CancellationTokenSource(transferred.Count == 0 ? firstPageTimeout : pageTimeout))
                {
                    try
                    {
                        more = await job.Pages.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!timedOut)
                    {
                        // Ask the driver to stop, then give it a little longer to hand over what it has.
                        timedOut = true;
                        Stop(session, job, "Timed out waiting for the scanner to send the next page.");
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                if (!more)
                    break;
                while (job.Pages.Reader.TryRead(out var page))
                {
                    transferred.Add(page.FilePath); // ownership transfers with the yield
                    yield return page;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (job.Error is { } error)
                throw new InvalidOperationException(error);
        }
        finally
        {
            if (session is not null)
                await Task.Run(() => Finish(session)).ConfigureAwait(false);
            _job = null;
            _gate.Release();

            // Pages written but never handed over — cancelled, or still queued.
            job.Pages.Writer.TryComplete();
            while (job.Pages.Reader.TryRead(out var page))
            {
                if (transferred.Contains(page.FilePath))
                    continue;
                try { File.Delete(page.FilePath); }
                catch (IOException) { }
            }
        }
    }

    // Opens the scanner, sets it up from the options and starts it. Settings a driver rejects are left
    // as the driver has them, except the feeder/flatbed choice, which has to hold.
    private TwainAppSession Start(ScanJob job)
    {
        var session = Session();
        var options = job.Options;
        var name = options.DeviceId[IdPrefix.Length..];
        var source = session.GetSources().FirstOrDefault(item => ((string)item.ProductName).Trim() == name)
            ?? throw new InvalidOperationException($"The scanner \"{name}\" isn't available. Check it's on and connected.");

        var opened = session.OpenSource(source);
        if (!opened.IsSuccess)
            throw new InvalidOperationException($"Could not open the scanner: {Describe(opened)}");

        _job = job;
        try
        {
            Configure(session, options);
        }
        catch
        {
            // Close the source again, or the next scan or device list finds the driver busy.
            _job = null;
            session.CloseSource();
            throw;
        }

        return session;
    }

    private static void Configure(TwainAppSession session, ScanOptions options)
    {
        var caps = session.Caps;
        caps.ICAP_XFERMECH.Set(TWSX.NATIVE);
        var feeder = options.Source == ScanSourceKind.Feeder;
        if (caps.CAP_FEEDERENABLED.Supports.HasFlag(TWQC.SET))
        {
            var sts = caps.CAP_FEEDERENABLED.Set(feeder ? TW_BOOL.True : TW_BOOL.False);
            if (!sts.IsSuccess)
                throw new InvalidOperationException(feeder
                    ? "The selected scanner has no document feeder."
                    : "The selected scanner has no flatbed.");
        }
        else if (feeder)
        {
            throw new InvalidOperationException("The selected scanner has no document feeder.");
        }

        if (feeder)
        {
            caps.CAP_AUTOFEED.Set(TW_BOOL.True);
            if (options.Duplex && !caps.CAP_DUPLEXENABLED.Set(TW_BOOL.True).IsSuccess)
                throw new InvalidOperationException("The selected scanner can't scan both sides.");
            if (!options.Duplex)
                caps.CAP_DUPLEXENABLED.Set(TW_BOOL.False);
        }

        caps.ICAP_PIXELTYPE.Set(options.ColorMode == ScanColorMode.Grayscale ? TWPT.GRAY : TWPT.RGB);
        caps.ICAP_BITDEPTH.Set((ushort)8);
        caps.ICAP_XRESOLUTION.Set((TW_FIX32)(float)options.Dpi);
        caps.ICAP_YRESOLUTION.Set((TW_FIX32)(float)options.Dpi);
        // Every page in the feeder, not just one.
        caps.CAP_XFERCOUNT.Set((short)-1);

        var enabled = session.EnableSource(options.ShowDriverSettings ? SourceEnableOption.ShowUI : SourceEnableOption.NoUI);
        if (!enabled.IsSuccess)
            throw new InvalidOperationException($"The scanner didn't start: {Describe(enabled)}");
    }

    // Stops a scan: before transfers start the source is just disabled; during them the next transfer
    // stops the feeder and the pages already scanned still come through.
    private static void Stop(TwainAppSession session, ScanJob job, string? error)
    {
        if (error is not null)
            job.Error ??= error;
        job.StopRequested = true;
        if (session.State == STATE.S5)
        {
            session.DisableSource();
            job.Pages.Writer.TryComplete();
        }
    }

    private static void Finish(TwainAppSession session)
    {
        if (session.State >= STATE.S5)
            session.DisableSource();
        if (session.State >= STATE.S4)
            session.CloseSource();
    }

    private TwainAppSession Session()
    {
        if (_session is { State: >= STATE.S3 })
            return _session;

        _session ??= CreateSession();
        var opened = _session.OpenDsm();
        if (!opened.IsSuccess)
            throw new InvalidOperationException($"Could not start TWAIN: {Describe(opened)}");
        return _session;
    }

    private TwainAppSession CreateSession()
    {
        var session = new TwainAppSession(new TWIdentityWrapper(
            "Fybre", "Capture", "Capture", typeof(TwainScanSource).Assembly.GetName().Version ?? new Version(1, 0),
            "Capture document capture", TWLG.ENGLISH, TWCY.AUSTRALIA, DG.IMAGE | DG.CONTROL | DG.APP2));
        session.TransferReady += (_, e) =>
        {
            if (_job is not { } job)
                return;
            if (job.Error is not null && !job.StopRequested)
                e.Cancel = CancelType.EndNow;
            else if (job.StopRequested)
                e.Cancel = CancelType.Graceful;
        };
        session.Transferred += (_, e) =>
        {
            if (_job is { } job)
                SavePage(job, e);
        };
        session.TransferError += (_, e) =>
        {
            if (_job is { } job)
                job.Error ??= e.Exception?.Message ?? (e.Code is { } code ? Describe(code) : $"The scan failed ({e.Phase}).");
        };
        session.SourceDisabled += (_, _) => _job?.Pages.Writer.TryComplete();
        return session;
    }

    // Native transfers arrive as a bitmap (BMP) or, from a few drivers, a TIFF. The data is only valid
    // during the event, so the page is decoded and saved here.
    private static void SavePage(ScanJob job, TransferredEventArgs e)
    {
        try
        {
            if (e.Data is not { } data)
            {
                job.Error ??= "The scanner sent a page in a form Capture can't read.";
                return;
            }

            using var stream = data.AsStream();
            using var bitmap = SKBitmap.Decode(stream)
                ?? throw new InvalidOperationException("The scanner sent a page image Capture can't decode.");
            var path = Path.Combine(Path.GetTempPath(), $"capture-twain-{job.Id:N}-{++job.PageCount:D4}.png");
            using (var output = File.Create(path))
            {
                if (!bitmap.Encode(output, SKEncodedImageFormat.Png, 90))
                    throw new InvalidOperationException("Could not save the scanned page.");
            }

            var dpi = (int)Math.Round((float)e.ImageInfo.XResolution);
            job.Pages.Writer.TryWrite(new ScannedPage(path, bitmap.Width, bitmap.Height, dpi > 0 ? dpi : job.Options.Dpi));
        }
        catch (Exception ex)
        {
            job.Error ??= ex.Message;
        }
    }

    private static List<T> Values<T>(ValueContainer<T> container) where T : struct =>
        container.ContainerType switch
        {
            TWON.ONEVALUE => [container.OneValue],
            TWON.ENUMERATION => container.EnumValue?.Items.ToList() ?? [],
            TWON.ARRAY => container.ArrayValue?.ToList() ?? [],
            TWON.RANGE when container.RangeValue is { } range => range.Enumerate().Take(1000).ToList(),
            _ => []
        };

    private static List<int> Resolutions(ValueContainer<TW_FIX32> container)
    {
        if (container.ContainerType == TWON.RANGE && container.RangeValue is { } range)
        {
            var min = (float)range.MinValue;
            var max = (float)range.MaxValue;
            return CommonDpis.Where(dpi => dpi >= min && dpi <= max).ToList();
        }

        return Values(container).Select(value => (int)Math.Round((float)value)).Where(dpi => dpi > 0).Distinct().Order().ToList();
    }

    private static string Describe(STS status) => status.ConditionCode switch
    {
        TWCC.PAPERJAM => "the paper jammed.",
        TWCC.PAPERDOUBLEFEED => "two sheets went through together (double feed).",
        TWCC.NOMEDIA => "there's no paper in the scanner.",
        TWCC.CHECKDEVICEONLINE => "the scanner isn't responding — check it's on and connected.",
        TWCC.DENIED => "the scanner is in use by another program.",
        TWCC.NODS => "the scanner's TWAIN driver couldn't be found.",
        TWCC.LOWMEMORY => "not enough memory.",
        TWCC.None or TWCC.BUMMER => $"the driver reported an error ({status.RC}).",
        var code => $"the driver reported {code} ({status.RC})."
    };

    // The DSM the loader would pick: next to the app first, then System32.
    private static string? DsmPath() =>
        new[] { AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.System) }
            .Select(folder => Path.Combine(folder, "twaindsm.dll"))
            .FirstOrDefault(File.Exists);

    // The machine type in a DLL's PE header, or null if it can't be read (then loading is left to try).
    private static Architecture? PeArchitecture(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            reader.BaseStream.Position = 0x3C;
            reader.BaseStream.Position = reader.ReadInt32() + 4;
            return reader.ReadUInt16() switch
            {
                0x014C => Architecture.X86,
                0x8664 => Architecture.X64,
                0xAA64 => Architecture.Arm64,
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_session is null)
            return;
        if (_session.State == STATE.S3)
            _session.CloseDsm();
        _session.Dispose();
        _session = null;
    }

    private sealed class ScanJob(ScanOptions options)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ScanOptions Options { get; } = options;
        public Channel<ScannedPage> Pages { get; } = Channel.CreateUnbounded<ScannedPage>();
        public int PageCount;
        public volatile string? Error;
        public volatile bool StopRequested;
    }
}
