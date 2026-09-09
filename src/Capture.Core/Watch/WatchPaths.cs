using Capture.Core.Import;

namespace Capture.Core.Watch;

public static class WatchPaths
{
    public static bool IsWatchable(string path, string watchRoot) =>
        Resolve(path, watchRoot) is not null;

    public static string? Resolve(string path, string watchRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(watchRoot))
            return null;

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name) || name[0] == '.' || !ImportFormats.IsSupported(name))
            return null;

        if (IsOutputPath(path, watchRoot))
            return null;

        return Path.GetFullPath(Path.Combine(watchRoot, name));
    }

    private static bool IsOutputPath(string path, string watchRoot)
    {
        var root = Path.GetFullPath(watchRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));

        foreach (var folder in new[] { "processed", "error" })
        {
            var prefix = Path.Combine(root, folder);
            if (full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(prefix + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
