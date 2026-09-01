using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Capture.App.Hosting;
using Capture.App.Services;
using Capture.App.ViewModels;
using Capture.App.Views;
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
}
