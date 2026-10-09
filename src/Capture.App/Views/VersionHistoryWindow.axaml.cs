using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Capture.App.Services;

namespace Capture.App.Views;

public partial class VersionHistoryWindow : Window
{
    /// <summary>Parameterless overload kept for the XAML previewer.</summary>
    public VersionHistoryWindow() : this(null)
    {
    }

    public VersionHistoryWindow(string? currentVersion)
    {
        InitializeComponent();
        try
        {
            Entries.ItemsSource = VersionHistory.Load(currentVersion);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Couldn't load version history: {ex.Message}");
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
