namespace Capture.App.Services;

/// <summary>A generic blocking yes/no confirmation, for actions serious enough to need one but not
/// serious enough to warrant a dedicated dialog window — e.g. importing a profile that carries an
/// executable script. Returns true only if the user explicitly confirmed.</summary>
public interface IConfirmDialogService
{
    Task<bool> ConfirmAsync(object owner, string title, string message, string confirmText = "Continue", string cancelText = "Cancel");

    /// <summary>The same dialog with a single button, for a message that just needs acknowledging.</summary>
    Task ShowMessageAsync(object owner, string title, string message, string okText = "OK") =>
        ConfirmAsync(owner, title, message, okText, cancelText: string.Empty);
}
