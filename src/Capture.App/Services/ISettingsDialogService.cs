namespace Capture.App.Services;

public interface ISettingsDialogService
{
    Task<bool> ShowAsync(object owner);
}
