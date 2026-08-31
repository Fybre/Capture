using System.Globalization;
using Avalonia.Data.Converters;
using Capture.Core.Watch;

namespace Capture.App.Converters;

public sealed class AiProviderDisplayConverter : IValueConverter
{
    public static readonly AiProviderDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            AiProvider.OpenAiCompatible => "OpenAI-compatible (cloud)",
            AiProvider.Local => "Local (on this device)",
            AiProvider.None => "None (disabled)",
            _ => value?.ToString() ?? string.Empty
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
