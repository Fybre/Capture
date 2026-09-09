namespace Capture.App.Services;

public interface IFileDialogService
{
    object? Host { get; set; }

    Task<IReadOnlyList<string>> PickFilesAsync();

    Task<string?> PickFileAsync(string title);

    Task<string?> PickFolderAsync();

    /// <summary>Opens a single JSON file picker (profile/settings import) — unlike <see cref="PickFileAsync"/>,
    /// which filters to importable document formats.</summary>
    Task<string?> PickJsonFileAsync(string title);

    /// <summary>Opens a save-file picker for a single JSON file (profile/settings export).</summary>
    Task<string?> PickSaveJsonFileAsync(string title, string suggestedFileName);
}
