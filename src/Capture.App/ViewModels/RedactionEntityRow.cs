using Capture.Core.Redaction;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Capture.App.ViewModels;

/// <summary>One checkbox row in the Settings redaction-set editor's grouped entity checklist (see
/// <see cref="RedactionEntityGroupRow"/>).</summary>
public sealed partial class RedactionEntityRow : ObservableObject
{
    public RedactionEntityRow(string name, bool isSelected)
    {
        Name = name;
        _isSelected = isSelected;
    }

    /// <summary>The raw Presidio entity type code (e.g. "US_SSN") — what actually gets saved to
    /// RedactionSettings.Entities.</summary>
    public string Name { get; }

    /// <summary>A friendly name for the checklist UI, e.g. "US_SSN" → "Social Security number".</summary>
    public string DisplayName => PresidioEntityNames.Describe(Name);

    [ObservableProperty]
    private bool _isSelected;
}
