using Capture.Core.Watch;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

/// <summary>Backs the single-page setup screen shown once, before MainWindow, on a fresh install —
/// see App.axaml.cs and WatchSettings.HasCompletedFirstRunSetup. Only surfaces the handful of
/// preferences worth deciding up front; everything else keeps its normal default and stays reachable
/// from the full Settings window later.</summary>
public partial class FirstRunWizardViewModel : ViewModelBase
{
    private readonly IWatchSettingsStore _store;

    public FirstRunWizardViewModel(IWatchSettingsStore store)
    {
        _store = store;
    }

    public IReadOnlyList<WorkspaceMode> StartViewOptions { get; } = Enum.GetValues<WorkspaceMode>();

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = Enum.GetValues<AppTheme>();

    [ObservableProperty]
    private WorkspaceMode _startView = WorkspaceMode.Preview;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    private bool _checkForUpdatesOnStartup;

    [ObservableProperty]
    private bool _allowFieldScripts;

    public Action? Close { get; set; }

    [RelayCommand]
    private async Task GetStartedAsync()
    {
        // Loads first (rather than starting from a bare `new WatchSettings()`) so this only ever
        // touches the four fields below — nothing else this wizard doesn't ask about gets silently
        // reset to its default on a settings file that, in practice, already has other values (e.g.
        // an imported/pre-seeded config dropped in before first launch).
        var settings = await _store.LoadAsync();
        settings.StartView = StartView;
        settings.Theme = Theme;
        settings.CheckForUpdatesOnStartup = CheckForUpdatesOnStartup;
        settings.AllowFieldScripts = AllowFieldScripts;
        settings.HasCompletedFirstRunSetup = true;
        await _store.SaveAsync(settings);
        Close?.Invoke();
    }
}
