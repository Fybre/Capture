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

            // Without an explicit NativeMenu, Avalonia falls back to a generic macOS app menu titled
            // "Avalonia Application" with a boilerplate "About Avalonia Application" item — the app's
            // real name (from Info.plist's CFBundleName) doesn't override this. Supplying our own is the
            // documented fix, and it reuses the same About dialog already wired to the in-app "More"
            // menu (MainViewModel.OpenAboutCommand) instead of a separate/duplicate implementation.
            var aboutItem = new NativeMenuItem("About Capture");
            aboutItem.Click += (_, _) => mainViewModel.OpenAboutCommand.Execute(null);
            var quitItem = new NativeMenuItem("Quit Capture") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
            quitItem.Click += (_, _) => desktop.Shutdown();
            var appMenu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem("Capture")
                    {
                        Menu = new NativeMenu
                        {
                            Items = { aboutItem, new NativeMenuItemSeparator(), quitItem }
                        }
                    }
                }
            };
            NativeMenu.SetMenu(this, appMenu);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
