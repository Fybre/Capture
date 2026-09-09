namespace Capture.App.Services;

/// <summary>Pops a script/expression out into a larger, resizable, syntax-highlighted editor window —
/// for scripts too long to comfortably edit in the compact inline textbox. Returns the edited text if
/// the user saved, or null if they cancelled (in which case the caller should leave the original text
/// untouched).</summary>
public interface IScriptEditorDialogService
{
    Task<string?> EditAsync(object owner, string title, string source);
}
