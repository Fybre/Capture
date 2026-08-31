using Avalonia.Controls;
using Avalonia.Controls.Notifications;

namespace Capture.App.Services;

public sealed class ToastService : IToastService
{
    private readonly Dictionary<TopLevel, WindowNotificationManager> _managers = [];
    private readonly List<TopLevel> _hostStack = [];

    public void AttachHost(TopLevel host)
    {
        _hostStack.Remove(host);
        _hostStack.Add(host);
        if (!_managers.ContainsKey(host))
        {
            _managers[host] = new WindowNotificationManager(host)
            {
                Position = NotificationPosition.BottomRight,
                MaxItems = 3
            };
        }
    }

    public void DetachHost(TopLevel host)
    {
        _hostStack.Remove(host);
        _managers.Remove(host);
    }

    public void ShowSuccess(string message) => Show("Success", message, NotificationType.Success);

    public void ShowError(string message) => Show("Error", message, NotificationType.Error);

    private void Show(string title, string message, NotificationType type)
    {
        if (_hostStack.Count == 0)
            return;

        var active = _hostStack[^1];
        if (_managers.TryGetValue(active, out var manager))
            manager.Show(new Notification(title, message, type));
    }
}
