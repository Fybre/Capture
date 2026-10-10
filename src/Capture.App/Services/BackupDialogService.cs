using Avalonia.Controls;
using Capture.App.Views;
using Capture.Storage;

namespace Capture.App.Services;

public interface IBackupDialogService
{
    /// <summary>Asks what to back up; null when cancelled.</summary>
    Task<BackupChoice?> ChooseBackupAsync(object owner, BackupInventory inventory);

    /// <summary>Asks which parts of <paramref name="backup"/> to restore; null when cancelled.</summary>
    Task<BackupParts?> ChooseRestoreAsync(object owner, CaptureBackup backup);
}

public sealed class BackupDialogService : IBackupDialogService
{
    public async Task<BackupChoice?> ChooseBackupAsync(object owner, BackupInventory inventory)
    {
        if (owner is not Window window)
            return null;
        var dialog = new BackupWindow(inventory);
        await dialog.ShowDialog(window);
        return dialog.Result;
    }

    public async Task<BackupParts?> ChooseRestoreAsync(object owner, CaptureBackup backup)
    {
        if (owner is not Window window)
            return null;
        var dialog = new BackupWindow(backup);
        await dialog.ShowDialog(window);
        return dialog.Result?.Parts;
    }
}
