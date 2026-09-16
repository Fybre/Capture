using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Capture.App.Hosting;
using Capture.App.Services;
using Capture.App.ViewModels;
using Capture.App.Views;
using Capture.Core.Paths;
using Capture.Core.Watch;
using Capture.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection()
            .AddCapture()
            .BuildServiceProvider();

        var paths = services.GetRequiredService<IAppPaths>();

        // Applied synchronously, before the window is created, so the first frame already renders in
        // the user's saved theme — MainViewModel.InitializeAsync (which loads the rest of WatchSettings
        // and re-applies the theme anyway) only runs after the window's Opened event, which is too late
        // to avoid a visible flash of the OS-default theme on a mismatched system.
        var (theme, hasCompletedFirstRunSetup) = ReadStartupPreferences(paths);
        RequestedThemeVariant = ThemeVariantMapper.Map(theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Neither the wizard nor MainWindow exists yet at this point, so the default
            // OnLastWindowClose would tear the whole app down the instant the wizard window closes —
            // restored to the normal OnMainWindowClose once MainWindow is actually up.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (hasCompletedFirstRunSetup)
            {
                CreateAndShowMainWindow(services, desktop);
            }
            else
            {
                var wizardViewModel = services.GetRequiredService<FirstRunWizardViewModel>();
                var wizard = new FirstRunWizardWindow(wizardViewModel);
                // Fires whether the user clicked "Get Started" or just closed the window — either way
                // the app proceeds to MainWindow; skipping the wizard just means it (harmlessly) shows
                // again next launch since HasCompletedFirstRunSetup was never set.
                wizard.Closed += (_, _) => CreateAndShowMainWindow(services, desktop);
                wizard.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateAndShowMainWindow(ServiceProvider services, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var mainViewModel = services.GetRequiredService<MainViewModel>();
        var mainWindow = new MainWindow
        {
            DataContext = mainViewModel
        };
        desktop.MainWindow = mainWindow;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
        // Never detached — MainWindow is the fallback toast target for the app's whole lifetime.
        services.GetRequiredService<IToastService>().AttachHost(mainWindow);

        // Cascades to every singleton IDisposable DI resolved, including PresidioSidecarLauncher —
        // without this, the sidecar child process (once bundled) would be orphaned on app exit.
        desktop.Exit += (_, _) => services.Dispose();

        // The menu structure itself is declared in App.axaml (Application.Name + NativeMenu.Menu,
        // loaded during Initialize() — setting NativeMenu.Menu here imperatively instead compiled
        // fine but the native menu bridge never picked it up, confirmed live). x:Name doesn't
        // generate code-behind fields for NativeMenuItem the way it does for visual controls
        // (confirmed: CS0103), so look the items up by position instead, then wire behavior now
        // that the real ViewModel/command exists — reuses the same About dialog already wired to
        // the in-app "More" menu (MainViewModel.OpenAboutCommand) instead of a separate one.
        var appMenu = NativeMenu.GetMenu(this)!;
        var aboutItem = (NativeMenuItem)appMenu.Items[0];
        var quitItem = (NativeMenuItem)appMenu.Items[2];
        aboutItem.Click += (_, _) => mainViewModel.OpenAboutCommand.Execute(null);
        quitItem.Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta);
        quitItem.Click += (_, _) => desktop.Shutdown();
    }

    // A synchronous, best-effort peek at just the Theme/HasCompletedFirstRunSetup fields —
    // deliberately not going through IWatchSettingsStore.LoadAsync (async file I/O, plus
    // OS-credential-store round-trips for the secret fields we don't need here). Any failure just
    // means the window opens at the OS-default theme for one frame (corrected moments later by
    // MainViewModel.InitializeAsync) and the wizard shows again if it's genuinely a fresh install.
    private static (AppTheme Theme, bool HasCompletedFirstRunSetup) ReadStartupPreferences(IAppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.SettingsPath))
                return (AppTheme.System, false);

            var settings = JsonSerializer.Deserialize<WatchSettings>(File.ReadAllText(paths.SettingsPath), LatticeJson.Options);
            return (settings?.Theme ?? AppTheme.System, settings?.HasCompletedFirstRunSetup ?? false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (AppTheme.System, false);
        }
    }
}
