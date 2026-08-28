using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Capture.Core.Import;

namespace Capture.App.Services;

public sealed class FileDialogService : IFileDialogService
{
    public object? Host { get; set; }

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var provider = GetStorageProvider();
        if (provider is null)
            return Array.Empty<string>();

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import documents",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Documents")
                {
                    Patterns = ImportFormats.AllExtensions.Select(ext => "*" + ext).ToArray()
                }
            ]
        });

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
    }

    public async Task<string?> PickFileAsync(string title)
    {
        var provider = GetStorageProvider();
        if (provider is null)
            return null;

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Documents")
                {
                    Patterns = ImportFormats.AllExtensions.Select(ext => "*" + ext).ToArray()
                }
            ]
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync()
    {
        var provider = GetStorageProvider();
        if (provider is null)
            return null;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Import folder",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private IStorageProvider? GetStorageProvider()
    {
        return Host as TopLevel is { } topLevel
            ? topLevel.StorageProvider
            : null;
    }
}
