namespace Capture.Core.Watch;

public static class WatchFileMover
{
    public static string Move(string sourcePath, string watchRoot, bool success)
    {
        var folder = Path.Combine(watchRoot, success ? "processed" : "error");
        Directory.CreateDirectory(folder);
        var fileName = Path.GetFileName(sourcePath);

        for (var attempt = 0; ; attempt++)
        {
            var dest = UniquePath(folder, fileName, attempt);
            try
            {
                File.Move(sourcePath, dest, overwrite: false);
                return dest;
            }
            catch (IOException) when (attempt < 5)
            {
                // Destination was created by another process/tick between UniquePath and Move; retry with a new name.
            }
        }
    }

    private static string UniquePath(string folder, string fileName, int attempt)
    {
        var dest = Path.Combine(folder, fileName);
        if (attempt == 0 && !File.Exists(dest))
            return dest;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var suffix = attempt <= 1
            ? DateTime.Now.ToString("yyyyMMddHHmmssfff")
            : $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        return Path.Combine(folder, $"{name}-{suffix}{extension}");
    }
}
