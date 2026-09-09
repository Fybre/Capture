using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Capture.App.ViewModels;

/// <summary>Editable wrapper around one profile-level <see cref="FieldScript"/> — same "write straight
/// back to the wrapped model object on every property change" shape as <see cref="FieldRow"/>/
/// <see cref="ExportDefinitionRow"/>.</summary>
public sealed partial class ScriptRow : ObservableObject
{
    public ScriptRow(FieldScript script)
    {
        Script = script;
        _name = script.Name;
        _enabled = script.Enabled;
        _trigger = script.Trigger;
        _source = script.Source;
        _timeoutSeconds = script.TimeoutSeconds;
    }

    public FieldScript Script { get; }

    public Guid Id => Script.Id;

    public IReadOnlyList<ScriptTriggerChoice> TriggerChoices { get; } =
    [
        new(ScriptTrigger.AfterFieldsPopulated, "After fields are populated"),
        new(ScriptTrigger.BeforeExport, "Before export"),
        new(ScriptTrigger.AfterExport, "After export")
    ];

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value) => Script.Name = value;

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) => Script.Enabled = value;

    [ObservableProperty]
    private ScriptTrigger _trigger;

    partial void OnTriggerChanged(ScriptTrigger value) => Script.Trigger = value;

    [ObservableProperty]
    private string _source;

    partial void OnSourceChanged(string value) => Script.Source = value;

    [ObservableProperty]
    private int _timeoutSeconds;

    partial void OnTimeoutSecondsChanged(int value) => Script.TimeoutSeconds = Math.Max(1, value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string _testResult = string.Empty;

    [ObservableProperty]
    private bool _isTesting;

    public bool HasTestResult => !string.IsNullOrWhiteSpace(TestResult);
}

public sealed record ScriptTriggerChoice(ScriptTrigger Value, string Label);
