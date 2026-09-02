namespace Capture.App.Services;

public interface IHelpWindowService
{
    void Show(object owner);

    /// <summary>Opens (or activates) Help already scrolled to the Scripting tab — used by the
    /// "Scripting help" shortcuts next to the script editors in the Profile Designer.</summary>
    void ShowScripting(object owner);
}
