using System.Collections.ObjectModel;
using Capture.Core.Indexing;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public sealed record PageScopeChoice(PageScope Value, string Label);
public sealed record BarcodeScanAreaChoice(BarcodeScanArea Value, string Label);
public enum BarcodeScanArea { EntirePage, SelectedArea }

public sealed partial class FieldRow : ObservableObject
{
    public FieldRow(IndexField field, bool isBatchScope = false)
    {
        Field = field;
        IsBatchScope = isBatchScope;
        _boundaryRuleId = field.BoundaryRuleId;
        _name = field.Name;
        _format = field.Format;
        _mandatory = field.Mandatory;
        _sensitive = field.Sensitive;
        _hidden = field.HideFromIndexing;
        _isReadOnly = field.IsReadOnly;
        _defaultValueTemplate = field.DefaultValueTemplate ?? string.Empty;
        _lookupKeyTemplate = field.LookupKeyTemplate ?? string.Empty;
        _scriptExpression = field.ScriptExpression ?? string.Empty;
        _postProcessScript = field.PostProcessScript ?? string.Empty;
        _buttonLabel = field.ButtonLabel ?? string.Empty;
        _buttonScriptSource = field.ButtonScriptSource;
        _buttonTimeoutSeconds = field.ButtonTimeoutSeconds;
        _keyPattern = field.KeyPattern ?? string.Empty;
        _valuePattern = field.ValuePattern ?? string.Empty;
        _barcodeFormat = field.BarcodeFormat;
        _barcodeScanArea = field.Zone is null ? BarcodeScanArea.EntirePage : BarcodeScanArea.SelectedArea;
        _occurrence = field.Occurrence;
        _pageScope = field.PageScope;
        _pageNumber = Math.Max(1, field.PageNumber);
        _aiPrompt = field.AiPrompt ?? string.Empty;
        _liveValue = string.Empty;
        var type = AiFieldCatalog.Find(field.AiTypeId);
        _selectedClassification = type?.Classification ?? AiFieldCatalog.Classifications[0];
        RefreshAiTypes();
        _selectedAiType = type ?? AiTypes.FirstOrDefault();
        foreach (var option in field.LookupOptions ?? [])
            LookupOptions.Add(WrapLookupOption(option));
        _selectedDefaultLookupOption = LookupOptions.FirstOrDefault(option =>
            string.Equals(option.Value, field.LookupDefaultValue, StringComparison.Ordinal));
    }

    public IndexField Field { get; }

    /// <summary>True when this row belongs to the profile's shared batch fields rather than a document
    /// type's own fields — batch fields never create page redactions, even when marked Sensitive, since
    /// they aren't tied to a page zone on any one document. Drives the inline warning shown next to the
    /// Sensitive checkbox for a batch field.</summary>
    public bool IsBatchScope { get; }

    public Guid Id => Field.Id;

    public bool IsZonal => Field.Kind == FieldKind.Zonal;

    public bool IsBarcode => Field.Kind == FieldKind.Barcode;

    public bool IsZoneField => IsZonal || IsBarcode;

    public bool IsKeyValue => Field.Kind == FieldKind.KeyValue;

    public bool IsRegex => Field.Kind == FieldKind.Regex;

    public bool IsAi => Field.Kind == FieldKind.Ai;

    public bool IsBatchSeparatorValue => Field.Kind == FieldKind.BatchSeparatorValue;

    public bool IsText => Field.Kind == FieldKind.Text;

    public bool IsLookup => Field.Kind == FieldKind.Lookup;

    public bool IsScript => Field.Kind == FieldKind.Script;

    public bool IsButton => Field.Kind == FieldKind.Button;

    public bool CanPopulateValue => Field.Kind is not (FieldKind.Script or FieldKind.Button or FieldKind.Lookup);

    /// <summary>Whether this field's Kind can have a <see cref="PostProcessScript"/> attached — every
    /// kind except Script (already fully script-driven) and Button (must only change on click).</summary>
    public bool AllowsPostProcessScript => Field.Kind is not (FieldKind.Script or FieldKind.Button);

    public bool IsPatternField => IsKeyValue || IsRegex;

    [ObservableProperty]
    private string? _barcodeFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarcodeAreaStatus))]
    private BarcodeScanArea _barcodeScanArea;

    public IReadOnlyList<BarcodeScanAreaChoice> BarcodeScanAreaOptions { get; } =
    [
        new(BarcodeScanArea.EntirePage, "Entire page"),
        new(BarcodeScanArea.SelectedArea, "Selected area")
    ];

    public string BarcodeAreaStatus => BarcodeScanArea == BarcodeScanArea.EntirePage
        ? "Capture scans the entire selected page for this barcode."
        : Field.Zone is null
            ? "Draw the barcode area on the sample preview."
            : $"Using the area drawn on page {Field.Zone.PageNumber}.";

    /// <summary>Zonal, Barcode, Key/value, and Regex fields share the same First/Number/Any page scope.</summary>
    public bool HasPageScope => IsZoneField || IsPatternField;

    public bool IsPageNumberScope => Field.PageScope == PageScope.Number;

    public bool HasSearchZone => Field.SearchZone is not null;

    public string KindDisplay => Field.Kind switch
    {
        FieldKind.KeyValue => "Key/value",
        FieldKind.Regex => "Regex",
        FieldKind.Barcode => "Barcode",
        FieldKind.Ai => "AI",
        FieldKind.BatchSeparatorValue => "Batch separator value",
        FieldKind.Text => "Text",
        FieldKind.Lookup => "Lookup",
        FieldKind.Script => "Script",
        FieldKind.Button => "Button",
        _ => "Zone"
    };

    public MatchOccurrence[] Occurrences { get; } = Enum.GetValues<MatchOccurrence>();

    public IReadOnlyList<PageScopeChoice> PageScopes { get; } =
    [
        new(PageScope.First, "First page"),
        new(PageScope.Number, "Specific page"),
        new(PageScope.Any, "Any page")
    ];

    public FieldFormat[] Formats { get; } = Enum.GetValues<FieldFormat>();

    public IReadOnlyList<string> BarcodeFormats => BarcodePatterns.KnownFormats;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private FieldFormat _format;

    [ObservableProperty]
    private bool _mandatory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBatchSensitiveHint))]
    private bool _sensitive;

    /// <summary>Gates the inline "this won't create a page redaction" note shown next to a batch field's
    /// Sensitive checkbox — surfaced at the point of the mistake rather than only in the section-level
    /// hint text above the whole field list.</summary>
    public bool ShowBatchSensitiveHint => IsBatchScope && Sensitive;

    [ObservableProperty]
    private bool _hidden;

    /// <summary>UI-only marker (not persisted) — true for the first row in a hidden run, so the field
    /// list can draw a divider above it. Maintained by <see cref="FieldCollectionEditorViewModel"/>.</summary>
    [ObservableProperty]
    private bool _isFirstHidden;

    [ObservableProperty]
    private bool _isReadOnly;

    [ObservableProperty]
    private Guid? _boundaryRuleId;

    /// <summary>Initial value for Text fields, or a fallback when an extracted field is blank.</summary>
    [ObservableProperty]
    private string _defaultValueTemplate;

    [ObservableProperty]
    private string _keyPattern;

    [ObservableProperty]
    private string _valuePattern;

    [ObservableProperty]
    private MatchOccurrence _occurrence;

    [ObservableProperty]
    private PageScope _pageScope;

    [ObservableProperty]
    private int _pageNumber = 1;

    [ObservableProperty]
    private string _selectedClassification = string.Empty;

    [ObservableProperty]
    private AiFieldType? _selectedAiType;

    [ObservableProperty]
    private string _aiPrompt = string.Empty;

    public IReadOnlyList<string> Classifications { get; } = AiFieldCatalog.Classifications;

    public ObservableCollection<AiFieldType> AiTypes { get; } = [];

    public ObservableCollection<LookupOptionRow> LookupOptions { get; } = [];

    [ObservableProperty]
    private LookupOptionRow? _selectedDefaultLookupOption;

    /// <summary>Only meaningful when <see cref="IsLookup"/> — see <see cref="IndexField.LookupKeyTemplate"/>.</summary>
    [ObservableProperty]
    private string _lookupKeyTemplate;

    /// <summary>Only meaningful when <see cref="IsScript"/> — see <see cref="IndexField.ScriptExpression"/>.</summary>
    [ObservableProperty]
    private string _scriptExpression;

    /// <summary>Only meaningful when <see cref="AllowsPostProcessScript"/> — see
    /// <see cref="IndexField.PostProcessScript"/>.</summary>
    [ObservableProperty]
    private string _postProcessScript;

    /// <summary>Only meaningful when <see cref="IsButton"/> — see <see cref="IndexField.ButtonLabel"/>.</summary>
    [ObservableProperty]
    private string _buttonLabel;

    /// <summary>Only meaningful when <see cref="IsButton"/> — see <see cref="IndexField.ButtonScriptSource"/>.</summary>
    [ObservableProperty]
    private string _buttonScriptSource;

    /// <summary>Only meaningful when <see cref="IsButton"/> — see <see cref="IndexField.ButtonTimeoutSeconds"/>.</summary>
    [ObservableProperty]
    private int _buttonTimeoutSeconds;

    [ObservableProperty]
    private string _liveValue;

    [ObservableProperty]
    private string _liveFormat = string.Empty;

    [ObservableProperty]
    private float _liveConfidence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScriptTestResult))]
    private string _scriptTestResult = string.Empty;

    public ZoneRect? MatchBounds { get; set; }

    public bool HasLiveFormat => !string.IsNullOrWhiteSpace(LiveFormat);
    public bool HasScriptTestResult => !string.IsNullOrWhiteSpace(ScriptTestResult);

    public string ConfidenceDisplay =>
        !string.IsNullOrWhiteSpace(LiveFormat)
            ? string.IsNullOrEmpty(LiveValue) ? LiveFormat : $"{LiveFormat} · {LiveConfidence:0}%"
            : LiveConfidence <= 0 && string.IsNullOrEmpty(LiveValue)
                ? "—"
                : $"{LiveConfidence:0}%";

    public string PageDisplay => $"{KindDisplay} · {PageDescription}";

    private string PageDescription
    {
        get
        {
            if (HasPageScope)
            {
                if (IsZoneField && Field.Zone is { } fieldZone)
                    return $"selected area page {fieldZone.PageNumber}";
                if (IsPatternField && Field.SearchZone is { } searchZone)
                    return $"search area page {searchZone.PageNumber}";
                return Field.PageScope switch
                    {
                        PageScope.Any => "any page",
                        PageScope.First => "first page",
                        _ => $"page {Field.PageNumber}"
                    };
            }

            return Field.Kind switch
            {
                FieldKind.Ai => "all pages",
                FieldKind.BatchSeparatorValue => "from batch trigger",
                FieldKind.Text when BoundaryRuleId is not null => "from trigger rule",
                FieldKind.Text when !string.IsNullOrWhiteSpace(DefaultValueTemplate) => "populated value",
                FieldKind.Text or FieldKind.Lookup => "manual entry",
                _ => $"page {Field.PageNumber}"
            };
        }
    }

    public void NotifySearchZone()
    {
        OnPropertyChanged(nameof(HasSearchZone));
        NotifyPage();
    }

    public void NotifyPage() => OnPropertyChanged(nameof(PageDisplay));

    public void SetZone(ZoneRect? zone)
    {
        Field.Zone = zone;
        BarcodeScanArea = zone is null ? BarcodeScanArea.EntirePage : BarcodeScanArea.SelectedArea;
        OnPropertyChanged(nameof(BarcodeAreaStatus));
        NotifyPage();
    }

    partial void OnBarcodeScanAreaChanged(BarcodeScanArea value)
    {
        if (value == BarcodeScanArea.EntirePage)
            Field.Zone = null;
        OnPropertyChanged(nameof(BarcodeAreaStatus));
        NotifyPage();
    }

    partial void OnNameChanged(string value) => Field.Name = value;

    partial void OnFormatChanged(FieldFormat value) => Field.Format = value;

    partial void OnMandatoryChanged(bool value) => Field.Mandatory = value;

    partial void OnSensitiveChanged(bool value) => Field.Sensitive = value;

    partial void OnHiddenChanged(bool value) => Field.HideFromIndexing = value;

    partial void OnIsReadOnlyChanged(bool value) => Field.IsReadOnly = value;

    partial void OnBoundaryRuleIdChanged(Guid? value)
    {
        Field.BoundaryRuleId = value;
        NotifyPage();
    }

    partial void OnDefaultValueTemplateChanged(string value)
    {
        Field.DefaultValueTemplate = string.IsNullOrWhiteSpace(value) ? null : value;
        NotifyPage();
    }

    partial void OnLookupKeyTemplateChanged(string value) =>
        Field.LookupKeyTemplate = string.IsNullOrWhiteSpace(value) ? null : value;

    partial void OnScriptExpressionChanged(string value) =>
        Field.ScriptExpression = string.IsNullOrWhiteSpace(value) ? null : value;

    partial void OnPostProcessScriptChanged(string value) =>
        Field.PostProcessScript = string.IsNullOrWhiteSpace(value) ? null : value;

    partial void OnButtonLabelChanged(string value) =>
        Field.ButtonLabel = string.IsNullOrWhiteSpace(value) ? null : value;

    partial void OnButtonScriptSourceChanged(string value) => Field.ButtonScriptSource = value;

    partial void OnButtonTimeoutSecondsChanged(int value) => Field.ButtonTimeoutSeconds = Math.Max(1, value);

    partial void OnKeyPatternChanged(string value) => Field.KeyPattern = value;

    partial void OnValuePatternChanged(string value) => Field.ValuePattern = value;

    partial void OnBarcodeFormatChanged(string? value) =>
        Field.BarcodeFormat = string.IsNullOrWhiteSpace(value) ? null : value;

    partial void OnOccurrenceChanged(MatchOccurrence value) => Field.Occurrence = value;

    partial void OnPageScopeChanged(PageScope value)
    {
        Field.PageScope = value;
        OnPropertyChanged(nameof(PageDisplay));
        OnPropertyChanged(nameof(IsPageNumberScope));
    }

    partial void OnPageNumberChanged(int value)
    {
        Field.PageNumber = Math.Max(1, value);
        if (Field.PageScope == PageScope.Number && Field.Zone is not null)
            Field.Zone.PageNumber = Field.PageNumber;
        NotifyPage();
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

    [RelayCommand]
    private void AddLookupOption()
    {
        LookupOptions.Add(WrapLookupOption(new LookupOption()));
        SyncLookupOptions();
    }

    private LookupOptionRow WrapLookupOption(LookupOption option) => new(option)
    {
        Changed = SyncLookupOptions,
        RemoveRequested = RemoveLookupOption
    };

    private void RemoveLookupOption(LookupOptionRow row)
    {
        LookupOptions.Remove(row);
        SyncLookupOptions();
    }

    private void SyncLookupOptions()
    {
        if (SelectedDefaultLookupOption is not null && !LookupOptions.Contains(SelectedDefaultLookupOption))
            SelectedDefaultLookupOption = null;

        Field.LookupOptions = LookupOptions.Select(item => item.Option).ToList();
        Field.LookupDefaultValue = SelectedDefaultLookupOption?.Value;
        OnPropertyChanged(nameof(LookupOptions));
    }

    partial void OnSelectedDefaultLookupOptionChanged(LookupOptionRow? value) =>
        Field.LookupDefaultValue = value?.Value;

    [RelayCommand]
    private void ClearLookupDefault() => SelectedDefaultLookupOption = null;
}

public sealed partial class LookupOptionRow : ObservableObject
{
    public LookupOptionRow(LookupOption option)
    {
        Option = option;
        _key = option.Key;
        _value = option.Value;
    }

    public LookupOption Option { get; }
    public Action? Changed { get; set; }
    public Action<LookupOptionRow>? RemoveRequested { get; set; }

    [ObservableProperty]
    private string _key;

    [ObservableProperty]
    private string _value;

    partial void OnKeyChanged(string value)
    {
        Option.Key = value;
        Changed?.Invoke();
    }

    partial void OnValueChanged(string value)
    {
        Option.Value = value;
        Changed?.Invoke();
    }

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);
}
