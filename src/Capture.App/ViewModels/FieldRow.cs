using System.Collections.ObjectModel;
using Capture.Core.Indexing;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Capture.App.ViewModels;

public sealed partial class FieldRow : ObservableObject
{
    public FieldRow(IndexField field)
    {
        Field = field;
        _name = field.Name;
        _format = field.Format;
        _mandatory = field.Mandatory;
        _level = field.Level;
        _keyPattern = field.KeyPattern ?? string.Empty;
        _valuePattern = field.ValuePattern ?? string.Empty;
        _occurrence = field.Occurrence;
        _pageScope = field.PageScope;
        _aiPrompt = field.AiPrompt ?? string.Empty;
        _liveValue = string.Empty;
        var type = AiFieldCatalog.Find(field.AiTypeId);
        _selectedClassification = type?.Classification ?? AiFieldCatalog.Classifications[0];
        RefreshAiTypes();
        _selectedAiType = type ?? AiTypes.FirstOrDefault();
        foreach (var segment in field.Macro ?? [])
            Segments.Add(WrapSegment(segment));
    }

    public IndexField Field { get; }

    public Guid Id => Field.Id;

    public bool IsZonal => Field.Kind == FieldKind.Zonal;

    public bool IsBarcode => Field.Kind == FieldKind.Barcode;

    public bool IsZoneField => IsZonal || IsBarcode;

    public bool IsKeyValue => Field.Kind == FieldKind.KeyValue;

    public bool IsRegex => Field.Kind == FieldKind.Regex;

    public bool IsMacro => Field.Kind == FieldKind.Macro;

    public bool IsAi => Field.Kind == FieldKind.Ai;

    public bool IsBatchSeparatorValue => Field.Kind == FieldKind.BatchSeparatorValue;

    public bool IsPatternField => IsKeyValue || IsRegex;

    /// <summary>Barcode, Key/value, and Regex all share the same First/Number/Any page-scope selector.
    /// Zonal stays pinned to a single page — its zone rectangle is only meaningful on the page it was
    /// drawn on, so First/Any wouldn't mean anything for it without also re-locating the rectangle.</summary>
    public bool HasPageScope => IsBarcode || IsPatternField;

    public bool IsPageNumberScope => Field.PageScope == PageScope.Number;

    public ObservableCollection<MacroSegmentRow> Segments { get; } = [];

    public IReadOnlyList<string> FieldChoices { get; set; } = [];

    public bool HasSearchZone => Field.SearchZone is not null;

    public string KindDisplay => Field.Kind switch
    {
        FieldKind.KeyValue => "Key/value",
        FieldKind.Regex => "Regex",
        FieldKind.Macro => "Macro",
        FieldKind.Barcode => "Barcode",
        FieldKind.Ai => "AI",
        FieldKind.BatchSeparatorValue => "Batch separator value",
        _ => "Zone"
    };

    public MatchOccurrence[] Occurrences { get; } = Enum.GetValues<MatchOccurrence>();

    public PageScope[] PageScopes { get; } = Enum.GetValues<PageScope>();

    public IndexLevel[] Levels { get; } = Enum.GetValues<IndexLevel>();

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private FieldFormat _format;

    [ObservableProperty]
    private bool _mandatory;

    [ObservableProperty]
    private IndexLevel _level;

    [ObservableProperty]
    private string _keyPattern;

    [ObservableProperty]
    private string _valuePattern;

    [ObservableProperty]
    private MatchOccurrence _occurrence;

    [ObservableProperty]
    private PageScope _pageScope;

    [ObservableProperty]
    private string _selectedClassification = string.Empty;

    [ObservableProperty]
    private AiFieldType? _selectedAiType;

    [ObservableProperty]
    private string _aiPrompt = string.Empty;

    public IReadOnlyList<string> Classifications { get; } = AiFieldCatalog.Classifications;

    public ObservableCollection<AiFieldType> AiTypes { get; } = [];

    [ObservableProperty]
    private string _liveValue;

    [ObservableProperty]
    private string _liveFormat = string.Empty;

    [ObservableProperty]
    private float _liveConfidence;

    public ZoneRect? MatchBounds { get; set; }

    public bool HasLiveFormat => !string.IsNullOrWhiteSpace(LiveFormat);

    public string ConfidenceDisplay =>
        !string.IsNullOrWhiteSpace(LiveFormat)
            ? string.IsNullOrEmpty(LiveValue) ? LiveFormat : $"{LiveFormat} · {LiveConfidence:0}%"
            : LiveConfidence <= 0 && string.IsNullOrEmpty(LiveValue)
                ? "—"
                : $"{LiveConfidence:0}%";

    public string PageDisplay => $"{LevelLabel}{KindDisplay} · {PageDescription}";

    private string PageDescription
    {
        get
        {
            if (HasPageScope)
            {
                return Field.SearchZone is not null ? $"region page {Field.SearchZone.PageNumber}"
                    : Field.PageScope switch
                    {
                        PageScope.Any => "any page",
                        PageScope.First => "first page",
                        _ => $"page {Field.PageNumber}"
                    };
            }

            return Field.Kind switch
            {
                FieldKind.Macro => "computed",
                FieldKind.Zonal => $"page {Field.Zone?.PageNumber ?? Field.PageNumber}",
                FieldKind.Ai => "all pages",
                FieldKind.BatchSeparatorValue => "from batch trigger",
                _ => $"page {Field.PageNumber}"
            };
        }
    }

    private string LevelLabel => Field.Level == IndexLevel.Batch ? "Batch · " : string.Empty;

    public void NotifySearchZone()
    {
        OnPropertyChanged(nameof(HasSearchZone));
        NotifyPage();
    }

    public void NotifyPage() => OnPropertyChanged(nameof(PageDisplay));

    partial void OnNameChanged(string value) => Field.Name = value;

    partial void OnFormatChanged(FieldFormat value) => Field.Format = value;

    partial void OnMandatoryChanged(bool value) => Field.Mandatory = value;

    partial void OnLevelChanged(IndexLevel value)
    {
        Field.Level = value;
        OnPropertyChanged(nameof(PageDisplay));
    }

    partial void OnKeyPatternChanged(string value) => Field.KeyPattern = value;

    partial void OnValuePatternChanged(string value) => Field.ValuePattern = value;

    partial void OnOccurrenceChanged(MatchOccurrence value) => Field.Occurrence = value;

    partial void OnPageScopeChanged(PageScope value)
    {
        Field.PageScope = value;
        Field.PageScopeConfigured = true;
        OnPropertyChanged(nameof(PageDisplay));
        OnPropertyChanged(nameof(IsPageNumberScope));
    }

    partial void OnSelectedClassificationChanged(string value)
    {
        RefreshAiTypes();
        if (SelectedAiType is null || !string.Equals(SelectedAiType.Classification, value, StringComparison.Ordinal))
            SelectedAiType = AiTypes.FirstOrDefault();
    }

    public bool IsCustomAiType => SelectedAiType?.Id == AiFieldCatalog.CustomTypeId;

    public string AiPromptLabel => IsCustomAiType
        ? "Describe what to extract"
        : "Extra instruction (optional)";

    public string AiPromptWatermark => IsCustomAiType
        ? "e.g. The container number stamped on the shipping label"
        : "e.g. Prefer the bill-to name";

    partial void OnSelectedAiTypeChanged(AiFieldType? value)
    {
        if (value is null)
            return;

        OnPropertyChanged(nameof(IsCustomAiType));
        OnPropertyChanged(nameof(AiPromptLabel));
        OnPropertyChanged(nameof(AiPromptWatermark));

        // Every row carries an AiTypes/SelectedAiType pair (needed so switching a field TO AI mid-edit
        // works), but only an AI-kind field's own Name/Format should ever come from it — otherwise a
        // stray ComboBox rebind while a non-AI field is being deselected can rename it to "Custom Field".
        if (Field.Kind != FieldKind.Ai)
            return;

        Field.AiTypeId = value.Id;
        Name = value.Name;
        Format = value.Format;
        OnPropertyChanged(nameof(PageDisplay));
    }

    partial void OnAiPromptChanged(string value) => Field.AiPrompt = value;

    private void RefreshAiTypes()
    {
        var selected = SelectedAiType?.Id;
        AiTypes.Clear();
        foreach (var type in AiFieldCatalog.ForClassification(SelectedClassification))
            AiTypes.Add(type);
        SelectedAiType = AiTypes.FirstOrDefault(item => item.Id == selected) ?? SelectedAiType;
    }

    partial void OnLiveConfidenceChanged(float value) => OnPropertyChanged(nameof(ConfidenceDisplay));

    partial void OnLiveFormatChanged(string value)
    {
        OnPropertyChanged(nameof(ConfidenceDisplay));
        OnPropertyChanged(nameof(HasLiveFormat));
    }

    public void AddSegment(MacroSegment segment)
    {
        Segments.Add(WrapSegment(segment));
        SyncMacro();
    }

    public void RemoveSegment(MacroSegmentRow row)
    {
        Segments.Remove(row);
        SyncMacro();
    }

    public void SetFieldChoices(IReadOnlyList<string> names)
    {
        FieldChoices = names;
        OnPropertyChanged(nameof(FieldChoices));
        foreach (var segment in Segments)
        {
            segment.FieldChoices = names;
            segment.NotifyChoices();
        }
    }

    private MacroSegmentRow WrapSegment(MacroSegment segment)
    {
        var row = new MacroSegmentRow(segment)
        {
            Changed = SyncMacro,
            RemoveRequested = RemoveSegment
        };
        return row;
    }

    private void SyncMacro()
    {
        Field.Macro = Segments.Select(item => item.Segment).ToList();
        OnPropertyChanged(nameof(FieldRow.Segments));
    }
}
