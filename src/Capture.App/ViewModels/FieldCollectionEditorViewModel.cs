using System.Collections.ObjectModel;
using Capture.Core.Import;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public enum FieldScriptTestKind { Expression, Button, PostProcess }
public enum FieldValueSourceKind { None, BoundaryRule, Template, Custom }
public sealed record FieldValueSourceChoice(
    string Key,
    string Label,
    FieldValueSourceKind Kind,
    Guid? BoundaryRuleId = null,
    string? Template = null);

public partial class FieldCollectionEditorViewModel : ViewModelBase
{
    private readonly Func<FieldRow, string, string, Task<string?>>? _editScript;
    private readonly Func<FieldRow, FieldScriptTestKind, Task<string>>? _testScript;
    private readonly Action<FieldRow, string?>? _fieldChanged;
    private readonly Action? _suggestKeyFromSample;
    private readonly Action? _suggestValueFromSample;
    private readonly Func<Task>? _extractAiSample;
    private readonly IEnumerable<SeparationStrategyRow> _boundaryRules;
    private readonly IEnumerable<FieldRow> _additionalSourceFields;
    private readonly string _boundaryRuleLabel;
    private readonly bool _isBatchScope;
    private readonly Action? _openScriptingHelp;
    private bool _syncingValueSource;

    public FieldCollectionEditorViewModel(
        IEnumerable<IndexField>? fields = null,
        Func<FieldRow, string, string, Task<string?>>? editScript = null,
        Func<FieldRow, FieldScriptTestKind, Task<string>>? testScript = null,
        Action<FieldRow, string?>? fieldChanged = null,
        Action? suggestKeyFromSample = null,
        Action? suggestValueFromSample = null,
        Func<Task>? extractAiSample = null,
        IEnumerable<SeparationStrategyRow>? boundaryRules = null,
        string boundaryRuleLabel = "Trigger rule",
        IEnumerable<FieldRow>? additionalSourceFields = null,
        bool isBatchScope = false,
        Action? openScriptingHelp = null)
    {
        _editScript = editScript;
        _testScript = testScript;
        _fieldChanged = fieldChanged;
        _suggestKeyFromSample = suggestKeyFromSample;
        _suggestValueFromSample = suggestValueFromSample;
        _extractAiSample = extractAiSample;
        _boundaryRules = boundaryRules ?? [];
        _additionalSourceFields = additionalSourceFields ?? [];
        _boundaryRuleLabel = boundaryRuleLabel;
        _isBatchScope = isBatchScope;
        _openScriptingHelp = openScriptingHelp;
        FieldKinds = Enum.GetValues<FieldKind>()
            .Where(kind => isBatchScope || kind != FieldKind.BatchSeparatorValue)
            .ToList();
        foreach (var field in fields ?? [])
        {
            var row = new FieldRow(field, isBatchScope);
            Watch(row);
            Fields.Add(row);
        }
        RepartitionByHidden();
        RefreshValueSourceOptions();
    }

    public ObservableCollection<FieldRow> Fields { get; } = [];
    public ObservableCollection<FieldValueSourceChoice> ValueSourceOptions { get; } = [];
    public Action? SelectionChanged { get; set; }

    [ObservableProperty] private FieldRow? _selectedField;
    [ObservableProperty] private FieldValueSourceChoice? _selectedValueSource;
    [ObservableProperty] private FieldKind _newFieldKind = FieldKind.Text;
    public IReadOnlyList<FieldKind> FieldKinds { get; }

    public FieldRow Add(IndexField field)
    {
        var row = new FieldRow(field, _isBatchScope);
        Watch(row);
        Fields.Add(row);
        SelectedField = row;
        RepartitionByHidden();
        return row;
    }

    [RelayCommand]
    private void AddSelectedKind() => Add(new IndexField { Name = NextName(NewFieldKind.ToString()), Kind = NewFieldKind });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedField is null) return;
        var index = Fields.IndexOf(SelectedField);
        Fields.RemoveAt(index);
        SelectedField = Fields.Count == 0 ? null : Fields[Math.Min(index, Fields.Count - 1)];
        RepartitionByHidden();
    }

    /// <summary>Moves the field identified by <paramref name="fromId"/> to sit at the position currently
    /// held by <paramref name="toId"/> — the drag-and-drop analog of the old Up/Down buttons, driven by
    /// FieldCollectionEditorView's drag-handle wiring. RepartitionByHidden afterwards keeps the same
    /// visible/hidden boundary the old buttons were clamped against — a drag across it just snaps back.</summary>
    public void ReorderField(Guid fromId, Guid toId)
    {
        var from = Fields.FirstOrDefault(field => field.Field.Id == fromId);
        var to = Fields.FirstOrDefault(field => field.Field.Id == toId);
        if (from is null || to is null || ReferenceEquals(from, to)) return;
        Fields.Move(Fields.IndexOf(from), Fields.IndexOf(to));
        RepartitionByHidden();
    }

    /// <summary>Keeps hidden fields grouped after every visible field, in a stable partition — a Hidden
    /// checkbox toggle or an added/removed field can otherwise leave a hidden field sitting above a
    /// visible one, which would put the divider (<see cref="FieldRow.IsFirstHidden"/>) in the wrong
    /// place. Uses ObservableCollection.Move rather than Clear+re-Add so SelectedField/selection state
    /// survives the reshuffle.</summary>
    private void RepartitionByHidden()
    {
        var target = Fields.Where(field => !field.Hidden).Concat(Fields.Where(field => field.Hidden)).ToList();
        for (var i = 0; i < target.Count; i++)
        {
            var currentIndex = Fields.IndexOf(target[i]);
            if (currentIndex != i)
                Fields.Move(currentIndex, i);
        }

        FieldRow? previous = null;
        foreach (var field in Fields)
        {
            field.IsFirstHidden = field.Hidden && previous?.Hidden != true;
            previous = field;
        }

        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanEditScript))]
    private async Task PopOutFieldScriptAsync(FieldRow? row)
    {
        if (row is null || _editScript is null) return;
        var edited = await _editScript(row, $"Script expression — {row.Name}", row.ScriptExpression);
        if (edited is not null) row.ScriptExpression = edited;
    }

    [RelayCommand(CanExecute = nameof(CanEditScript))]
    private async Task PopOutButtonScriptAsync(FieldRow? row)
    {
        if (row is null || _editScript is null) return;
        var edited = await _editScript(row, $"Button script — {row.Name}", row.ButtonScriptSource);
        if (edited is not null) row.ButtonScriptSource = edited;
    }

    [RelayCommand(CanExecute = nameof(CanEditScript))]
    private async Task PopOutPostProcessScriptAsync(FieldRow? row)
    {
        if (row is null || _editScript is null) return;
        var edited = await _editScript(row, $"Post-process script — {row.Name}", row.PostProcessScript);
        if (edited is not null) row.PostProcessScript = edited;
    }

    [RelayCommand(CanExecute = nameof(CanTestScript))]
    private Task TestFieldScriptAsync(FieldRow? row) => TestScriptAsync(row, FieldScriptTestKind.Expression);

    [RelayCommand(CanExecute = nameof(CanTestScript))]
    private Task TestButtonScriptAsync(FieldRow? row) => TestScriptAsync(row, FieldScriptTestKind.Button);

    [RelayCommand(CanExecute = nameof(CanTestScript))]
    private Task TestPostProcessScriptAsync(FieldRow? row) => TestScriptAsync(row, FieldScriptTestKind.PostProcess);

    private async Task TestScriptAsync(FieldRow? row, FieldScriptTestKind kind)
    {
        if (row is null || _testScript is null) return;
        row.ScriptTestResult = await _testScript(row, kind);
    }

    [RelayCommand(CanExecute = nameof(CanSuggestKeyFromSample))]
    private void SuggestKeyFromSample() => _suggestKeyFromSample?.Invoke();

    [RelayCommand(CanExecute = nameof(CanSuggestValueFromSample))]
    private void SuggestValueFromSample() => _suggestValueFromSample?.Invoke();

    [RelayCommand(CanExecute = nameof(CanExtractAiSample))]
    private Task ExtractAiSampleAsync() => _extractAiSample?.Invoke() ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanOpenScriptingHelp))]
    private void OpenScriptingHelp() => _openScriptingHelp?.Invoke();

    public List<IndexField> ToModels() => Fields.Select(row => row.Field).ToList();

    partial void OnSelectedFieldChanged(FieldRow? value)
    {
        RefreshValueSourceOptions();
        NotifyCommands();
        SelectionChanged?.Invoke();
    }

    partial void OnSelectedValueSourceChanged(FieldValueSourceChoice? value)
    {
        if (_syncingValueSource || SelectedField is null || value is null)
            return;

        _syncingValueSource = true;
        try
        {
            switch (value.Kind)
            {
                case FieldValueSourceKind.None:
                    SelectedField.BoundaryRuleId = null;
                    SelectedField.DefaultValueTemplate = string.Empty;
                    break;
                case FieldValueSourceKind.BoundaryRule:
                    SelectedField.BoundaryRuleId = value.BoundaryRuleId;
                    SelectedField.DefaultValueTemplate = string.Empty;
                    break;
                case FieldValueSourceKind.Template:
                    SelectedField.BoundaryRuleId = null;
                    SelectedField.DefaultValueTemplate = value.Template ?? string.Empty;
                    break;
            }
        }
        finally
        {
            _syncingValueSource = false;
        }
    }
    private bool HasSelection() => SelectedField is not null;
    private bool CanEditScript(FieldRow? row) => row is not null && _editScript is not null;
    private bool CanTestScript(FieldRow? row) => row is not null && _testScript is not null;
    private bool CanSuggestKeyFromSample() => SelectedField?.IsKeyValue == true && _suggestKeyFromSample is not null;
    private bool CanSuggestValueFromSample() => SelectedField?.IsPatternField == true && _suggestValueFromSample is not null;
    private bool CanExtractAiSample() => SelectedField?.IsAi == true && _extractAiSample is not null;
    private bool CanOpenScriptingHelp() => _openScriptingHelp is not null;
    public void RefreshValueSourceOptions()
    {
        _syncingValueSource = true;
        try
        {
            ValueSourceOptions.Clear();
            var isBatchSeparatorValue = SelectedField?.IsBatchSeparatorValue == true;
            ValueSourceOptions.Add(new(
                "none",
                isBatchSeparatorValue ? "Choose batch-start rule…" : "Normal extraction or manual entry",
                FieldValueSourceKind.None));

            foreach (var rule in _boundaryRules.Where(rule =>
                         rule.Type is SeparationStrategyType.Barcode or SeparationStrategyType.Regex or SeparationStrategyType.OcrZone))
            {
                ValueSourceOptions.Add(new(
                    $"rule:{rule.Id:N}",
                    $"{_boundaryRuleLabel} — {rule.DisplayLabel}",
                    FieldValueSourceKind.BoundaryRule,
                    BoundaryRuleId: rule.Id));
            }

            // This sentinel stores one batch-start rule's captured value. Unlike ordinary fields,
            // it does not evaluate field or macro templates, so only show sources that can work.
            if (!isBatchSeparatorValue)
            {
                foreach (var source in Fields.Where(field => field != SelectedField))
                    AddFieldSource(source, "Field");
                foreach (var source in _additionalSourceFields.Where(field =>
                             field != SelectedField && Fields.All(local => !string.Equals(local.Name, field.Name, StringComparison.OrdinalIgnoreCase))))
                    AddFieldSource(source, "Batch field");

                if (!_isBatchScope)
                    AddTemplate("macro:document", "Document number — {Doc#}", "{Doc#}");
                AddTemplate("macro:batch", "Batch number — {Batch#}", "{Batch#}");
                AddTemplate("macro:page", "Page number — {Page#}", "{Page#}");
                AddTemplate("macro:page-count", "Page count — {PageCount}", "{PageCount}");
                AddTemplate("macro:date", "Current date — {Date}", "{Date}");
                AddTemplate("macro:time", "Current time — {Time}", "{Time}");
                AddTemplate("macro:profile", _isBatchScope ? "Profile name — {ProfileName}" : "Document type — {ProfileName}", "{ProfileName}");
            }

            SyncSelectedValueSource();
        }
        finally
        {
            _syncingValueSource = false;
        }
    }

    private void AddFieldSource(FieldRow source, string prefix) =>
        AddTemplate($"field:{source.Id:N}", $"{prefix} — {source.Name}", $"{{{source.Name}}}");

    private void AddTemplate(string key, string label, string template) =>
        ValueSourceOptions.Add(new(key, label, FieldValueSourceKind.Template, Template: template));

    private void SyncSelectedValueSource()
    {
        _syncingValueSource = true;
        try
        {
            var custom = ValueSourceOptions.FirstOrDefault(option => option.Kind == FieldValueSourceKind.Custom);
            if (custom is not null)
                ValueSourceOptions.Remove(custom);

            var selected = SelectedField;
            FieldValueSourceChoice? choice = null;
            if (selected?.BoundaryRuleId is { } ruleId)
                choice = ValueSourceOptions.FirstOrDefault(item => item.BoundaryRuleId == ruleId);
            else if (!string.IsNullOrWhiteSpace(selected?.DefaultValueTemplate))
                choice = ValueSourceOptions.FirstOrDefault(item =>
                    item.Kind == FieldValueSourceKind.Template && item.Template == selected.DefaultValueTemplate);

            if (selected is not null && choice is null &&
                (selected.BoundaryRuleId is not null || !string.IsNullOrWhiteSpace(selected.DefaultValueTemplate)))
            {
                choice = new("custom", "Custom value template", FieldValueSourceKind.Custom);
                ValueSourceOptions.Add(choice);
            }

            SelectedValueSource = choice ?? ValueSourceOptions[0];
        }
        finally
        {
            _syncingValueSource = false;
        }
    }

    private void Watch(FieldRow row) => row.PropertyChanged += (_, args) =>
    {
        if (!_syncingValueSource && row == SelectedField &&
            args.PropertyName == nameof(FieldRow.DefaultValueTemplate) && row.BoundaryRuleId is not null)
        {
            _syncingValueSource = true;
            row.BoundaryRuleId = null;
            _syncingValueSource = false;
        }
        if (!_syncingValueSource && args.PropertyName == nameof(FieldRow.Name))
            RefreshValueSourceOptions();
        else if (!_syncingValueSource && row == SelectedField &&
                 args.PropertyName is nameof(FieldRow.BoundaryRuleId) or nameof(FieldRow.DefaultValueTemplate))
            SyncSelectedValueSource();
        if (args.PropertyName == nameof(FieldRow.Hidden))
            RepartitionByHidden();
        _fieldChanged?.Invoke(row, args.PropertyName);
    };
    private void NotifyCommands()
    {
        RemoveCommand.NotifyCanExecuteChanged();
        SuggestKeyFromSampleCommand.NotifyCanExecuteChanged();
        SuggestValueFromSampleCommand.NotifyCanExecuteChanged();
        ExtractAiSampleCommand.NotifyCanExecuteChanged();
    }
    private string NextName(string prefix)
    {
        var used = Fields.Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var candidate = $"{prefix} {number}";
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
