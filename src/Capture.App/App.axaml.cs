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

        // Applied synchronously, before the window is created, so the first frame already renders in
        // the user's saved theme — MainViewModel.InitializeAsync (which loads the rest of WatchSettings
        // and re-applies the theme anyway) only runs after the window's Opened event, which is too late
        // to avoid a visible flash of the OS-default theme on a mismatched system.
        RequestedThemeVariant = ThemeVariantMapper.Map(ReadThemePreference(services.GetRequiredService<IAppPaths>()));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = services.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };
            desktop.MainWindow = mainWindow;
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

        base.OnFrameworkInitializationCompleted();
    }

    // A synchronous, best-effort peek at just the Theme field — deliberately not going through
    // IWatchSettingsStore.LoadAsync (async file I/O, plus OS-credential-store round-trips for the
    // secret fields we don't need here). Any failure just means the window opens at the OS-default
    // theme for one frame, which InitializeAsync corrects moments later anyway.
    private static AppTheme ReadThemePreference(IAppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.SettingsPath))
                return AppTheme.System;

            var settings = JsonSerializer.Deserialize<WatchSettings>(File.ReadAllText(paths.SettingsPath), LatticeJson.Options);
            return settings?.Theme ?? AppTheme.System;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return AppTheme.System;
        }
    }
}
