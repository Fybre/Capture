using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Capture.Core.Profiles;

/// <summary>Caches compiled <see cref="Regex"/> instances by pattern + options for <see cref="RegexExtractor"/>
/// and <see cref="KeyValueExtractor"/>, which both used to construct a brand-new <c>Regex</c> (a real parse,
/// not free) on every single field extraction — for a profile evaluated across many documents in a bulk
/// import or an unattended watch-folder run, that's the same pattern re-parsed once per document for no
/// reason. Unlike <see cref="Regex.Match(string, string)"/>'s own static-method cache (capped at
/// <see cref="Regex.CacheSize"/>, 15 by default, and shared process-wide with every other caller of that
/// overload anywhere in the app), this cache is dedicated to these two callers and never evicts, since a
/// profile's own field patterns are a small, bounded set for the lifetime of the process. An invalid
/// pattern is cached as null so a broken field doesn't re-attempt (and re-fail) parsing on every call
/// either.</summary>
internal static class CachedRegex
{
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex?> Cache = new();

    public static Regex? GetOrNull(string pattern, RegexOptions options, TimeSpan timeout) =>
        Cache.GetOrAdd((pattern, options), key =>
        {
            try
            {
                return new Regex(key.Pattern, key.Options, timeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        });
}
