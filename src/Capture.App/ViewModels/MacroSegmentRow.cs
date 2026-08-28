using System.Windows.Input;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public sealed partial class MacroSegmentRow : ObservableObject
{
    public MacroSegmentRow(MacroSegment segment)
    {
        Segment = segment;
        _text = segment.Text ?? string.Empty;
        _counterWidth = segment.CounterWidth;
    }

    public MacroSegment Segment { get; }

    public Action? Changed { get; set; }

    public Action<MacroSegmentRow>? RemoveRequested { get; set; }

    public string KindLabel => Segment.Kind switch
    {
        MacroSegmentKind.Literal => "Text",
        MacroSegmentKind.DocumentCounter => "Doc #",
        MacroSegmentKind.BatchCounter => "Batch #",
        MacroSegmentKind.DateTime => "Date/time",
        MacroSegmentKind.Field => "Field",
        MacroSegmentKind.ProfileName => "Profile name",
        _ => Segment.Kind.ToString()
    };

    public bool IsLiteral => Segment.Kind == MacroSegmentKind.Literal;

    public bool IsCounter =>
        Segment.Kind is MacroSegmentKind.DocumentCounter or MacroSegmentKind.BatchCounter;

    public bool IsDateTime => Segment.Kind == MacroSegmentKind.DateTime;

    public bool IsField => Segment.Kind == MacroSegmentKind.Field;

    public IReadOnlyList<string> FieldChoices { get; set; } = [];

    public void NotifyChoices() => OnPropertyChanged(nameof(FieldChoices));

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private int _counterWidth;

    public ICommand RemoveCommand => new RelayCommand(() => RemoveRequested?.Invoke(this));

    partial void OnTextChanged(string value)
    {
        Segment.Text = value;
        Changed?.Invoke();
    }

    partial void OnCounterWidthChanged(int value)
    {
        Segment.CounterWidth = Math.Max(0, value);
        Changed?.Invoke();
    }
}
