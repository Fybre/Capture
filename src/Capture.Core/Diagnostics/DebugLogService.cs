using System.Diagnostics;
using Capture.Core.Paths;

namespace Capture.Core.Diagnostics;

/// <summary>Turns the app's existing <see cref="Trace"/> calls (TraceInformation/TraceWarning/
/// TraceError, already scattered across imports, exports, and watch-folder handling) into a file on
/// disk when debug mode is enabled — no per-call-site changes needed to start or stop capturing them.</summary>
public interface IDebugLogService
{
    string LogFilePath { get; }
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class DebugLogService : IDebugLogService, IDisposable
{
    private readonly object _gate = new();
    private TextWriterTraceListener? _listener;

    public DebugLogService(IAppPaths paths)
    {
        LogFilePath = paths.DebugLogPath;
    }

    public string LogFilePath { get; }

    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == IsEnabled)
                return;

            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                _listener = new TextWriterTraceListener(stream, "CaptureDebugLog");
                Trace.Listeners.Add(_listener);
                Trace.AutoFlush = true;
                Trace.TraceInformation("--- Debug logging enabled ---");
            }
            else
            {
                Trace.TraceInformation("--- Debug logging disabled ---");
                if (_listener is not null)
                {
                    Trace.Listeners.Remove(_listener);
                    _listener.Flush();
                    _listener.Dispose();
                    _listener = null;
                }
            }

            IsEnabled = enabled;
        }
    }

    public void Dispose() => SetEnabled(false);
}
