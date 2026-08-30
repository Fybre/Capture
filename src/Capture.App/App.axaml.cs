using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Capture.App.Hosting;
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
            desktop.MainWindow = new MainWindow
            {
                DataContext = services.GetRequiredService<MainViewModel>()
            };

            // Cascades to every singleton IDisposable DI resolved, including PresidioSidecarLauncher —
            // without this, the sidecar child process (once bundled) would be orphaned on app exit.
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
