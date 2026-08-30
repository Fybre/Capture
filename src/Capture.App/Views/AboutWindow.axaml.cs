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
}
