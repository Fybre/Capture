using Capture.Core.Import;

namespace Capture.Core.Watch;

public sealed class WatchSettler
{
    // A claimed file whose processing result is never reported back (e.g. a caller that ignores
    // failures) would otherwise stay claimed forever — bound the automatic-retry window instead.
    private const int MaxRetryAttempts = 5;

    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
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
        foreach (var path in _claimed.Where(path => !exists(path)).ToList())
            _attempts.Remove(path);
        _claimed.RemoveWhere(path => !exists(path));
    }

    /// <summary>
    /// Releases a claimed path that failed to process (e.g. it couldn't be moved to the processed/error
    /// folder) so it can be retried on a later flush, instead of staying claimed — and therefore silently
    /// never offered again — forever. Bounded by <see cref="MaxRetryAttempts"/>: once exceeded, the path
    /// stays claimed (quarantined, no further automatic retries) and this returns false so the caller can
    /// surface that it needs manual attention.
    /// </summary>
    public bool ReleaseFailed(string path)
    {
        if (!_claimed.Contains(path))
            return false;

        var attempts = _attempts.GetValueOrDefault(path) + 1;
        _attempts[path] = attempts;
        if (attempts >= MaxRetryAttempts)
            return false;

        _claimed.Remove(path);
        _seen[path] = DateTimeOffset.Now;
        return true;
    }
}
