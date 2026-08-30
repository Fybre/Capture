using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Capture.App.ViewModels;

/// <summary>One profile field offered in an <see cref="ExportDefinitionRow"/>'s field checklist. Wraps
/// the profile designer's own <see cref="FieldRow"/> (rather than copying its name) so a field rename
/// while the designer is open is reflected here for free via <c>{Binding Field.Name}</c>.</summary>
public sealed partial class FieldSelectionRow : ObservableObject
{
    public FieldSelectionRow(FieldRow field, bool isSelected)
    {
        Field = field;
        _isSelected = isSelected;
    }

    public FieldRow Field { get; }

    public Guid Id => Field.Id;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One Therefore category field awaiting a mapping to a profile field, shown in a
/// <see cref="ExportDefinitionRow"/>'s Therefore mapping list.</summary>
public sealed partial class ThereforeFieldMappingRow : ObservableObject
{
    public ThereforeFieldMappingRow(ThereforeFieldMapping mapping, IReadOnlyList<FieldRow> profileFields)
    {
        Mapping = mapping;
        ProfileFields = profileFields;
        _selectedField = profileFields.FirstOrDefault(field => field.Id == mapping.IndexFieldId);
    }

    public ThereforeFieldMapping Mapping { get; }

    public IReadOnlyList<FieldRow> ProfileFields { get; }

    public string Caption => Mapping.Caption;

    public bool Mandatory => Mapping.Mandatory;

    [ObservableProperty]
    private FieldRow? _selectedField;

    partial void OnSelectedFieldChanged(FieldRow? value) => Mapping.IndexFieldId = value?.Id;
}

/// <summary>Editable wrapper around one <see cref="ExportDefinition"/> — same "write straight back to
/// the wrapped model object on every property change" shape as <see cref="FieldRow"/>.</summary>
public sealed partial class ExportDefinitionRow : ObservableObject
{
    private IReadOnlyList<FieldRow> _profileFields;

    public ExportDefinitionRow(ExportDefinition definition, IReadOnlyList<FieldRow> profileFields)
    {
        Definition = definition;
        _profileFields = profileFields;
        _name = definition.Name;
        _enabled = definition.Enabled;
        _type = definition.Type;
        _outputFolder = definition.OutputFolder;
        _outputMode = definition.OutputMode;
        _sharedFileName = definition.SharedFileName;
        _fileNamePattern = definition.FileNamePattern;
        _fileMode = definition.FileMode;
        _includeHeader = definition.IncludeHeader;
        RefreshFieldOptions(profileFields);
        RefreshThereforeMappings();
    }

    public ExportDefinition Definition { get; }

    /// <summary>Whether this export's settings are shown — purely a UI affordance, not persisted on
    /// the model. Defaults false for existing exports loaded from the profile; ProfileDesignerViewModel
    /// sets it true for a freshly-added one.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public IReadOnlyList<ExportType> TypeOptions { get; } =
        Enum.GetValues<ExportType>().Where(type => type != ExportType.None).ToList();

    public IReadOnlyList<ExportOutputMode> OutputModeOptions { get; } = Enum.GetValues<ExportOutputMode>();

    public IReadOnlyList<ExportFileMode> FileModeOptions { get; } = Enum.GetValues<ExportFileMode>();

    public bool IsAppendMode => OutputMode == ExportOutputMode.AppendToSharedFile;

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value) => Definition.Name = value;

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) => Definition.Enabled = value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCsv))]
    [NotifyPropertyChangedFor(nameof(IsTherefore))]
    private ExportType _type;

    partial void OnTypeChanged(ExportType value) => Definition.Type = value;

    public bool IsCsv => Type == ExportType.Csv;

    public bool IsTherefore => Type == ExportType.Therefore;

    [ObservableProperty]
    private string _outputFolder;

    partial void OnOutputFolderChanged(string value) => Definition.OutputFolder = value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppendMode))]
    private ExportOutputMode _outputMode;

    partial void OnOutputModeChanged(ExportOutputMode value) => Definition.OutputMode = value;

    [ObservableProperty]
    private string _sharedFileName;

    partial void OnSharedFileNameChanged(string value) => Definition.SharedFileName = value;

    [ObservableProperty]
    private string _fileNamePattern;

    partial void OnFileNamePatternChanged(string value) => Definition.FileNamePattern = value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileMode))]
    private ExportFileMode _fileMode;

    partial void OnFileModeChanged(ExportFileMode value) => Definition.FileMode = value;

    public bool HasFileMode => FileMode != ExportFileMode.None;

    [ObservableProperty]
    private bool _includeHeader;

    partial void OnIncludeHeaderChanged(bool value) => Definition.IncludeHeader = value;

    public ObservableCollection<FieldSelectionRow> FieldOptions { get; } = [];

    /// <summary>Rebuilds the checklist from the profile's current field list — called on init and again
    /// whenever a field is added/removed while the designer is open (mirrors
    /// ProfileDesignerViewModel.RefreshBarcodeFieldOptions).</summary>
    public void RefreshFieldOptions(IReadOnlyList<FieldRow> profileFields)
    {
        _profileFields = profileFields;

        // Empty FieldIds means "all fields" (the model's own convention) — reflect that as every
        // checkbox starting checked, rather than none.
        var selectedIds = Definition.FieldIds.Count > 0
            ? Definition.FieldIds.ToHashSet()
            : profileFields.Select(field => field.Id).ToHashSet();

        foreach (var row in FieldOptions)
            row.PropertyChanged -= OnFieldOptionChanged;
        FieldOptions.Clear();

        foreach (var field in profileFields)
        {
            var row = new FieldSelectionRow(field, selectedIds.Contains(field.Id));
            row.PropertyChanged += OnFieldOptionChanged;
            FieldOptions.Add(row);
        }
    }

    private void OnFieldOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FieldSelectionRow.IsSelected))
            return;

        var selected = FieldOptions.Where(row => row.IsSelected).Select(row => row.Id).ToList();
        // Every field checked collapses back to empty ("all fields", including ones added later) —
        // only a genuine subset needs to be recorded explicitly.
        Definition.FieldIds = selected.Count == FieldOptions.Count ? [] : selected;
    }

    public string ThereforeCategoryDisplay => Definition.ThereforeCategoryNo is null
        ? "No category selected"
        : $"{Definition.ThereforeCategoryName} (#{Definition.ThereforeCategoryNo})";

    public ObservableCollection<ThereforeFieldMappingRow> ThereforeMappings { get; } = [];

    /// <summary>Called after ProfileDesignerViewModel updates Definition.ThereforeCategoryNo/Name/
    /// ThereforeFieldMappings directly (following a category browse) — Definition itself isn't
    /// observable, so this is how the UI learns to refresh.</summary>
    public void RefreshThereforeMappings()
    {
        ThereforeMappings.Clear();
        foreach (var mapping in Definition.ThereforeFieldMappings)
            ThereforeMappings.Add(new ThereforeFieldMappingRow(mapping, _profileFields));

        OnPropertyChanged(nameof(ThereforeCategoryDisplay));
    }
}
