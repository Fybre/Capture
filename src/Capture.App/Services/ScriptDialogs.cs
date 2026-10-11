using Avalonia.Threading;
using Capture.Core.Scripting;

namespace Capture.App.Services;

/// <summary>Shows a Button field script's <c>Message</c> and <c>Confirm</c> dialogs over the window the
/// button was clicked in. Scripts run off the UI thread, so each dialog is shown on it.</summary>
public sealed class ScriptDialogs(IConfirmDialogService confirm, Func<object?> owner) : IScriptDialogs
{
    public Task MessageAsync(string title, string message) =>
        Dispatcher.UIThread.InvokeAsync(() => owner() is { } window
            ? confirm.ShowMessageAsync(window, title, message)
            : Task.CompletedTask);

    public Task<bool> ConfirmAsync(string title, string question) =>
        Dispatcher.UIThread.InvokeAsync(() => owner() is { } window
            ? confirm.ConfirmAsync(window, title, question, confirmText: "Yes", cancelText: "No")
            : Task.FromResult(false));
}
