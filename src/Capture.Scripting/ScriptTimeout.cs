using System.Diagnostics;

namespace Capture.Scripting;

/// <summary>A script's time limit, which stops counting while the script waits on someone to answer a
/// dialog — reading a message shouldn't time the script out.</summary>
internal sealed class ScriptTimeout
{
    private readonly CancellationTokenSource _cts;
    private readonly TimeSpan _limit;
    private readonly Stopwatch _running = Stopwatch.StartNew();

    public ScriptTimeout(CancellationTokenSource cts, TimeSpan limit)
    {
        _cts = cts;
        _limit = limit;
        cts.CancelAfter(limit);
    }

    public async Task<T> PausedAsync<T>(Func<Task<T>> wait)
    {
        _running.Stop();
        _cts.CancelAfter(Timeout.InfiniteTimeSpan);
        try
        {
            return await wait().ConfigureAwait(false);
        }
        finally
        {
            _running.Start();
            var left = _limit - _running.Elapsed;
            if (left <= TimeSpan.Zero)
                _cts.Cancel();
            else
                _cts.CancelAfter(left);
        }
    }
}
