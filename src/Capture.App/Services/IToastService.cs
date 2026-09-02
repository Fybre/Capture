using Avalonia.Controls;

namespace Capture.App.Services;

/// <summary>Transient popup notifications shown alongside (never instead of) each window's own
/// StatusText status bar — for terminal outcomes of explicit actions (save, delete, export, test
/// connection, etc.), not in-progress messages.</summary>
public interface IToastService
{
    /// <summary>Registers a window as a toast target and makes it the active one — call when the
    /// window opens. The most recently attached window is where the next toast appears.</summary>
    void AttachHost(TopLevel host);

    /// <summary>Un-registers a window — call when it closes. Whichever window was attached before
    /// it (if any) becomes active again automatically.</summary>
    void DetachHost(TopLevel host);

    void ShowSuccess(string message);

    void ShowError(string message);

    /// <summary>Neutral, non-error notice. <paramref name="onClick"/> (e.g. opening a URL) fires if
    /// the user clicks the toast body before it expires.</summary>
    void ShowInfo(string message, Action? onClick = null);
}
