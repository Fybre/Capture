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

    public ScriptTrigger[] TriggerOptions { get; } = Enum.GetValues<ScriptTrigger>();

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
}
