namespace Capture.App.Services;

public interface IFileDialogService
{
    object? Host { get; set; }

    Task<IReadOnlyList<string>> PickFilesAsync();

    Task<string?> PickFileAsync(string title);

    Task<string?> PickFolderAsync();
}
