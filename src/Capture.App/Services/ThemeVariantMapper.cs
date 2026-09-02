using Avalonia.Styling;
using Capture.Core.Watch;

namespace Capture.App.Services;

public static class ThemeVariantMapper
{
    public static ThemeVariant Map(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };
}
