using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Capture.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
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

    private void OnLicenseLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || string.IsNullOrWhiteSpace(url))
            return;

        // ProcessStartInfo.UseShellExecute is the standard cross-platform way to hand a URL to the
        // OS's default browser — Process.Start(url) alone doesn't work on Windows for non-executable
        // paths, and passing false here would try (and fail) to execute the URL as a local file.
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
