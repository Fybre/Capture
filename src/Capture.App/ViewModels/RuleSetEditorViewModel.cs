using System.Collections.ObjectModel;
using Capture.Core.Import;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public sealed record StrategyTypeChoice(SeparationStrategyType Value, string Label);
public sealed record MatchModeChoice(SeparationMatchMode Value, string Label);

/// <summary>
/// Reusable editor state for one ordered set of separation rules. Scope-specific designers retain
/// responsibility for evaluating rules against their sample and deciding what a match means.
/// </summary>
public partial class RuleSetEditorViewModel : ViewModelBase
{
    private readonly SeparationStrategyType _defaultStrategyType;

    public RuleSetEditorViewModel(
        IEnumerable<SeparationStrategy> strategies,
        SeparationMatchMode matchMode,
        int matchMinimum,
        IReadOnlyList<SeparationStrategyType> strategyTypeOptions,
        string decisionTitle,
        SeparationStrategyType defaultStrategyType = SeparationStrategyType.EveryNPages,
        bool allowNone = false)
    {
        DecisionTitle = decisionTitle;
        StrategyTypeOptions = strategyTypeOptions;
        _defaultStrategyType = defaultStrategyType;
        MatchModeChoices = allowNone
            ? [
                new(SeparationMatchMode.None, "None — keep using the current batch"),
                new(SeparationMatchMode.Any, "Match any rule"),
                new(SeparationMatchMode.All, "Match every rule"),
                new(SeparationMatchMode.AtLeast, "Match at least N rules")
            ]
            : [
                new(SeparationMatchMode.Any, "Match any rule"),
                new(SeparationMatchMode.All, "Match every rule"),
                new(SeparationMatchMode.AtLeast, "Match at least N rules")
            ];
        _matchMode = allowNone || matchMode != SeparationMatchMode.None
            ? matchMode
            : SeparationMatchMode.Any;
        _matchMinimum = Math.Max(1, matchMinimum);

        foreach (var strategy in strategies)
        {
            var row = new SeparationStrategyRow(strategy);
            Watch(row);
            Strategies.Add(row);
        }
    }

    public IReadOnlyList<MatchModeChoice> MatchModeChoices { get; }

    public string DecisionTitle { get; }

    public IReadOnlyList<SeparationStrategyType> StrategyTypeOptions { get; }

    public IReadOnlyList<StrategyTypeChoice> StrategyTypeChoices => StrategyTypeOptions
        .Select(type => new StrategyTypeChoice(type, type switch
        {
            SeparationStrategyType.EveryNPages => "Every N pages",
            SeparationStrategyType.BlankPage => "Blank page",
            SeparationStrategyType.Barcode => "Barcode",
            SeparationStrategyType.Regex => "Page text",
            SeparationStrategyType.OcrZone => "Text in an area",
            _ => type.ToString()
        })).ToList();

    public IReadOnlyList<string> BarcodeFormatOptions => BarcodePatterns.KnownFormats;

    public ObservableCollection<SeparationStrategyRow> Strategies { get; } = [];
    public Action? SelectionChanged { get; set; }
    public Action<SeparationStrategyRow, string?>? StrategyChanged { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAtLeast))]
    [NotifyPropertyChangedFor(nameof(RulesEnabled))]
    [NotifyPropertyChangedFor(nameof(IsNone))]
    private SeparationMatchMode _matchMode;

    [ObservableProperty]
    private int _matchMinimum = 1;

    public bool IsAtLeast => MatchMode == SeparationMatchMode.AtLeast;
    public bool RulesEnabled => MatchMode != SeparationMatchMode.None;
    public bool IsNone => MatchMode == SeparationMatchMode.None;

    [ObservableProperty]
    private SeparationStrategyRow? _selectedStrategy;

    partial void OnMatchModeChanged(SeparationMatchMode value)
    {
        if (value == SeparationMatchMode.None)
            SelectedStrategy = null;
    }

    partial void OnSelectedStrategyChanged(SeparationStrategyRow? value) => SelectionChanged?.Invoke();

    [RelayCommand]
    private void AddStrategy()
    {
        var row = new SeparationStrategyRow(new SeparationStrategy
        {
            Id = Guid.NewGuid(),
            Type = _defaultStrategyType
        });
        Watch(row);
        Strategies.Add(row);
        SelectedStrategy = row;
    }

    [RelayCommand]
    private void RemoveStrategy(SeparationStrategyRow? row)
    {
        if (row is null)
            return;

        Strategies.Remove(row);
        if (SelectedStrategy == row)
            SelectedStrategy = null;
    }

    public List<SeparationStrategy> ToModels() =>
        Strategies.Select(row => row.ToModel()).ToList();

    private void Watch(SeparationStrategyRow row) => row.PropertyChanged += (_, args) =>
    {
        if (row == SelectedStrategy && args.PropertyName == nameof(SeparationStrategyRow.Type))
            SelectionChanged?.Invoke();
        StrategyChanged?.Invoke(row, args.PropertyName);
    };
}

/// <summary>Observable adapter for the existing flat <see cref="SeparationStrategy"/> model.</summary>
public sealed partial class SeparationStrategyRow : ObservableObject
{
    public SeparationStrategyRow(SeparationStrategy strategy)
    {
        Id = strategy.Id;
        _type = strategy.Type;
        _name = strategy.Name;
        _pageCount = Math.Max(1, strategy.PageCount);
        _blankInkPercent = strategy.BlankInkPercent;
        _zone = strategy.Zone;
        _barcodeScanArea = strategy.Zone is null ? BarcodeScanArea.EntirePage : BarcodeScanArea.SelectedArea;
        _zonePageNumber = Math.Max(1, strategy.ZonePageNumber);
        _barcodeFormat = strategy.BarcodeFormat;
        _barcodeValuePattern = strategy.BarcodeValuePattern;
        _textPattern = strategy.TextPattern;
    }

    public Guid Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBarcode))]
    [NotifyPropertyChangedFor(nameof(IsBlankPage))]
    [NotifyPropertyChangedFor(nameof(IsEveryNPages))]
    [NotifyPropertyChangedFor(nameof(IsRegex))]
    [NotifyPropertyChangedFor(nameof(IsOcrZone))]
    [NotifyPropertyChangedFor(nameof(NeedsZone))]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private SeparationStrategyType _type;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string? _name;

    [ObservableProperty]
    private int _pageCount = 1;

    [ObservableProperty]
    private int _blankInkPercent;

    [ObservableProperty]
    private ZoneRect? _zone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodeAreaStatus))]
    private BarcodeScanArea _barcodeScanArea;

    public IReadOnlyList<BarcodeScanAreaChoice> BarcodeScanAreaOptions { get; } =
    [
        new(BarcodeScanArea.EntirePage, "Entire page"),
        new(BarcodeScanArea.SelectedArea, "Selected area")
    ];

    public string BarcodeAreaStatus => BarcodeScanArea == BarcodeScanArea.EntirePage
        ? "The whole page is searched for a matching barcode."
        : Zone is null
            ? "Draw the barcode area on the sample preview."
            : $"Using the area drawn on page {Zone.PageNumber}.";

    [ObservableProperty]
    private int _zonePageNumber = 1;

    [ObservableProperty]
    private string? _barcodeFormat;

    [ObservableProperty]
    private string? _barcodeValuePattern;

    [ObservableProperty]
    private string? _textPattern;

    [ObservableProperty]
    private string? _testResult;

    public bool IsBarcode => Type == SeparationStrategyType.Barcode;
    public bool IsBlankPage => Type == SeparationStrategyType.BlankPage;
    public bool IsEveryNPages => Type == SeparationStrategyType.EveryNPages;
    public bool IsRegex => Type == SeparationStrategyType.Regex;
    public bool IsOcrZone => Type == SeparationStrategyType.OcrZone;
    public bool NeedsZone => IsBarcode || IsOcrZone;
    public string DisplayLabel => string.IsNullOrWhiteSpace(Name) ? Type switch
    {
        SeparationStrategyType.EveryNPages => "Every N pages",
        SeparationStrategyType.BlankPage => "Blank page",
        SeparationStrategyType.Barcode => "Barcode",
        SeparationStrategyType.Regex => "Page text",
        SeparationStrategyType.OcrZone => "Text in an area",
        _ => Type.ToString()
    } : Name!;

    public SeparationStrategy ToModel() => new()
    {
        Id = Id,
        Type = Type,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name,
        PageCount = Math.Max(1, PageCount),
        BlankInkPercent = BlankInkPercent,
        Zone = Zone,
        ZonePageNumber = ZonePageNumber,
        BarcodeFormat = string.IsNullOrWhiteSpace(BarcodeFormat) ? null : BarcodeFormat,
        BarcodeValuePattern = string.IsNullOrWhiteSpace(BarcodeValuePattern) ? null : BarcodeValuePattern,
        TextPattern = string.IsNullOrWhiteSpace(TextPattern) ? null : TextPattern
    };

    public void SetZone(ZoneRect? zone)
    {
        Zone = zone;
        BarcodeScanArea = zone is null ? BarcodeScanArea.EntirePage : BarcodeScanArea.SelectedArea;
        OnPropertyChanged(nameof(BarcodeAreaStatus));
    }

    partial void OnBarcodeScanAreaChanged(BarcodeScanArea value)
    {
        if (value == BarcodeScanArea.EntirePage)
        {
            Zone = null;
            TestResult = null;
        }
        OnPropertyChanged(nameof(BarcodeAreaStatus));
    }
}
