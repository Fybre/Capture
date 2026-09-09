using Avalonia.Controls;
using Capture.App.Views;

namespace Capture.App.Services;

/// <summary>Keeps one modeless Help window alive at a time so users can follow the instructions while
/// continuing to work in the main window.</summary>
public sealed class HelpWindowService : IHelpWindowService
{
    private readonly IToastService _toasts;
    private HelpWindow? _window;

    public HelpWindowService(IToastService toasts)
    {
        _toasts = toasts;
    }

    public void Show(object owner) => ShowInternal(owner, selectScripting: false);

    public void ShowScripting(object owner) => ShowInternal(owner, selectScripting: true);

    private void ShowInternal(object owner, bool selectScripting)
    {
        if (owner is not Window ownerWindow)
            return;

        if (_window is null)
        {
            var window = new HelpWindow();
            _window = window;
            _toasts.AttachHost(window);
            window.Closed += (_, _) =>
            {
                _toasts.DetachHost(window);
                _window = null;
            };
            window.Show(ownerWindow);
        }

        if (selectScripting)
            _window.SelectScriptingTab();
        _window.Activate();
    }
}
