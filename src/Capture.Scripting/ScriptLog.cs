using System.Diagnostics;

namespace Capture.Scripting;

/// <summary>Exposed to scripts as <c>Log</c>. Writes through the same <see cref="Trace"/> calls the
/// rest of the app already uses — <see cref="Capture.Core.Diagnostics.IDebugLogService"/> attaches a
/// file listener to <see cref="Trace"/> when debug mode is on, so script log lines land in the same
/// debug log file with no separate logging plumbing needed.</summary>
public sealed class ScriptLog
{
    private readonly string _prefix;

    internal ScriptLog(string scriptName)
    {
        _prefix = $"[Script:{scriptName}]";
    }

    public void Info(string message) => Trace.TraceInformation($"{_prefix} {message}");

    public void Warn(string message) => Trace.TraceWarning($"{_prefix} {message}");

    public void Error(string message) => Trace.TraceError($"{_prefix} {message}");
}
