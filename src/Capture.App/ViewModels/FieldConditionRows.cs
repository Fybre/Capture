using System.Collections.ObjectModel;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public sealed record ConditionOperatorChoice(ConditionOperator Value, string Label);

public sealed record ConditionMatchChoice(ConditionMatch Value, string Label);

public sealed record InactiveBehaviorChoice(InactiveFieldBehavior Value, string Label);

/// <summary>One value a rule can test for, when the tested field has a fixed set (a lookup's options,
/// or Yes/No). <see cref="Value"/> is what's stored and compared; <see cref="Label"/> is what's shown.</summary>
public sealed partial class ConditionValueChoice : ObservableObject
{
    public ConditionValueChoice(string label, string value, bool isSelected, Action changed)
    {
        Label = label;
        Value = value;
        _isSelected = isSelected;
        _changed = changed;
    }

    private readonly Action _changed;

    public string Label { get; }

    public string Value { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

/// <summary>Designer row for one <see cref="FieldConditionRule"/> on the selected field's Conditional tab.</summary>
public sealed partial class ConditionRuleRow : ObservableObject
{
    private readonly FieldRow _owner;
    private bool _loading;

    public ConditionRuleRow(FieldConditionRule rule, FieldRow owner)
    {
        Rule = rule;
        _owner = owner;
        _loading = true;
        _fieldName = rule.FieldName;
        _selectedOperator = OperatorChoices.FirstOrDefault(choice => choice.Value == rule.Operator) ?? OperatorChoices[0];
        _valuesText = string.Join(Environment.NewLine, rule.Values);
        _loading = false;
        RefreshChoices();
    }

    public FieldConditionRule Rule { get; }

    // Instance accessors below: the field editor uses reflection bindings, which don't see statics.
    public IReadOnlyList<ConditionOperatorChoice> Operators => OperatorChoices;

    public static IReadOnlyList<ConditionOperatorChoice> OperatorChoices { get; } =
    [
        new(ConditionOperator.IsOneOf, "is one of"),
        new(ConditionOperator.IsNotOneOf, "is not one of"),
        new(ConditionOperator.IsFilledIn, "is filled in"),
        new(ConditionOperator.IsEmpty, "is empty")
    ];

    /// <summary>Names of the fields this rule can test: the owner's siblings (and, for a document field,
    /// the batch fields), excluding the owner itself and Button fields, which hold no value.</summary>
    public ObservableCollection<string> FieldChoices { get; } = [];

    public ObservableCollection<ConditionValueChoice> ValueChoices { get; } = [];

    [ObservableProperty]
    private string? _fieldName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsValues))]
    [NotifyPropertyChangedFor(nameof(HasValueChoices))]
    [NotifyPropertyChangedFor(nameof(IsFreeText))]
    private ConditionOperatorChoice _selectedOperator;

    /// <summary>One value per line, for a field without a fixed set of values.</summary>
    [ObservableProperty]
    private string _valuesText;

    public bool NeedsValues => SelectedOperator.Value is ConditionOperator.IsOneOf or ConditionOperator.IsNotOneOf;

    public bool HasValueChoices => NeedsValues && ValueChoices.Count > 0;

    public bool IsFreeText => NeedsValues && ValueChoices.Count == 0;

    [ObservableProperty]
    private string _valueHint = string.Empty;

    [RelayCommand]
    private void Remove() => _owner.RemoveConditionRule(this);

    partial void OnFieldNameChanged(string? value)
    {
        if (_loading)
            return;
        Rule.FieldName = value ?? string.Empty;
        // Values chosen for a different field rarely make sense for this one.
        Rule.Values = [];
        _loading = true;
        ValuesText = string.Empty;
        _loading = false;
        RefreshValueChoices();
        _owner.SyncCondition();
    }

    partial void OnSelectedOperatorChanged(ConditionOperatorChoice value)
    {
        if (_loading)
            return;
        Rule.Operator = value.Value;
        _owner.SyncCondition();
    }

    partial void OnValuesTextChanged(string value)
    {
        if (_loading || HasValueChoices)
            return;
        Rule.Values = (value ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _owner.SyncCondition();
    }

    /// <summary>Re-reads the available fields and the tested field's options — they can change while the
    /// designer is open (fields added or renamed, lookup options edited).</summary>
    public void RefreshChoices()
    {
        var current = FieldName;
        _loading = true;
        FieldChoices.Clear();
        foreach (var name in _owner.ConditionFieldNames())
            FieldChoices.Add(name);
        if (!string.IsNullOrEmpty(current) && !FieldChoices.Contains(current, StringComparer.OrdinalIgnoreCase))
            FieldChoices.Add(current);
        // Clearing the list can make the ComboBox write null back; reassign so it reselects.
        FieldName = null;
        FieldName = FieldChoices.FirstOrDefault(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) ?? current;
        _loading = false;
        RefreshValueChoices();
    }

    public void RenameField(string oldName, string newName)
    {
        if (!string.Equals(Rule.FieldName, oldName, StringComparison.OrdinalIgnoreCase))
            return;
        Rule.FieldName = newName;
        _loading = true;
        FieldName = newName;
        _loading = false;
        RefreshChoices();
    }

    private void RefreshValueChoices()
    {
        ValueChoices.Clear();
        var field = _owner.FindConditionField(FieldName);
        if (field is { IsLookup: true })
        {
            foreach (var option in field.LookupOptions.Where(option => !string.IsNullOrWhiteSpace(option.Value)))
            {
                var label = string.IsNullOrWhiteSpace(option.Key) ? option.Value : option.Key;
                ValueChoices.Add(new ConditionValueChoice(label, option.Value, IsChosen(option.Value, option.Key), SyncChoices));
            }
            ValueHint = ValueChoices.Count == 0 ? "This dropdown has no options yet." : "Tick the options that apply.";
        }
        else if (field is { Format: FieldFormat.Boolean })
        {
            ValueChoices.Add(new ConditionValueChoice("Yes", "Yes", IsChosen("Yes", "true"), SyncChoices));
            ValueChoices.Add(new ConditionValueChoice("No", "No", IsChosen("No", "false"), SyncChoices));
            ValueHint = "Tick Yes, No, or both.";
        }
        else
        {
            ValueHint = "One value per line. Matching ignores upper/lower case and surrounding spaces.";
        }

        OnPropertyChanged(nameof(HasValueChoices));
        OnPropertyChanged(nameof(IsFreeText));
    }

    private bool IsChosen(string value, string alternative) =>
        Rule.Values.Any(item =>
            string.Equals(item.Trim(), value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Trim(), alternative, StringComparison.OrdinalIgnoreCase));

    private void SyncChoices()
    {
        Rule.Values = ValueChoices.Where(choice => choice.IsSelected).Select(choice => choice.Value).ToList();
        _owner.SyncCondition();
    }
}
