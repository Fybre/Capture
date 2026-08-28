using Capture.Core.Import;

namespace Capture.Core.Watch;

public sealed class WatchSettler
{
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _settle;

    public WatchSettler(TimeSpan settle)
    {
        _settle = settle < TimeSpan.Zero ? TimeSpan.Zero : settle;
    }

    public void Note(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !ImportFormats.IsSupported(path) || _claimed.Contains(path))
            return;
        _seen[path] = DateTimeOffset.Now;
    }

    public IReadOnlyList<string> TakeReady(DateTimeOffset now, Func<string, bool>? isReady = null)
    {
        var ready = new List<string>();
        foreach (var pair in _seen.ToList())
        {
            if (!File.Exists(pair.Key))
            {
                _seen.Remove(pair.Key);
                continue;
            }

            if (now - pair.Value < _settle)
                continue;
            if (isReady is not null && !isReady(pair.Key))
                continue;
            ready.Add(pair.Key);
            _seen.Remove(pair.Key);
            _claimed.Add(pair.Key);
        }

        return ready;
    }

    public void ReleaseGone(Func<string, bool> exists)
    {
        _claimed.RemoveWhere(path => !exists(path));
    }
}
