using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Capture.App.Services;

namespace Capture.App.Views;

public partial class AboutWindow : Window
{
    private readonly IUpdateCheckService? _updateCheck;
    private readonly IToastService? _toasts;

    /// <summary>Parameterless overload kept for the XAML previewer — AboutDialogService always uses
    /// the other constructor, which is the only one wired to real services.</summary>
    public AboutWindow() : this(null, null)
    {
    }

    public AboutWindow(IUpdateCheckService? updateCheck, IToastService? toasts)
    {
        _updateCheck = updateCheck;
        _toasts = toasts;
        InitializeComponent();

        var assembly = typeof(AboutWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = informationalVersion?.Split('+', 2)[0]
            ?? assembly.GetName().Version?.ToString(3)
            ?? "Development build";

        VersionText.Text = $"Version {version}";
        RuntimeText.Text = $"{RuntimeInformation.FrameworkDescription}  •  {GetPlatformName()}";
        CopyrightText.Text = $"© {DateTime.Now.Year} Capture contributors";
    }

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsMacOS())
            return "macOS";
        if (OperatingSystem.IsWindows())
            return "Windows";
        if (OperatingSystem.IsLinux())
            return "Linux";
        return RuntimeInformation.OSDescription;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || string.IsNullOrWhiteSpace(url))
            return;

        // ProcessStartInfo.UseShellExecute is the standard cross-platform way to hand a URL to the
        // OS's default browser — Process.Start(url) alone doesn't work on Windows for non-executable
        // paths, and passing false here would try (and fail) to execute the URL as a local file.
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // Manual, on-demand equivalent of MainViewModel's startup check — works even when "Check for
    // updates on startup" is turned off in Settings, since the user asking here is explicit intent.
    private async void OnCheckForUpdatesClick(object? sender, RoutedEventArgs e)
    {
        if (_updateCheck is null)
            return;

        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            var result = await _updateCheck.CheckForUpdateAsync();
            if (result.IsUpdateAvailable)
            {
                var releaseUrl = result.ReleaseUrl;
                _toasts?.ShowInfo(
                    $"Capture {result.LatestVersion} is available — click to view the release.",
                    releaseUrl is null ? null : () => Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }));
            }
            else
            {
                _toasts?.ShowSuccess("You're on the latest version.");
            }
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }
}
