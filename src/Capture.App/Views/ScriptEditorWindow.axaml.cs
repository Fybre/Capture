using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit.Highlighting;

namespace Capture.App.Views;

public partial class ScriptEditorWindow : Window
{
    public bool Saved { get; private set; }

    public string Text => Editor.Text;

    public ScriptEditorWindow()
    {
        InitializeComponent();
        // Bundled with AvaloniaEdit itself (AvaloniaEdit.Highlighting.Resources.CSharp-Mode.xshd) —
        // no custom grammar file needed.
        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
    }

    public ScriptEditorWindow(string title, string source) : this()
    {
        Title = title;
        TitleText.Text = title;
        Editor.Text = source;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        Saved = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Saved = false;
        Close();
    }
}
