using System.Collections.ObjectModel;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class ScriptCollectionEditorViewModel : ViewModelBase
{
    private readonly ScriptTrigger _defaultTrigger;
    private readonly Func<ScriptRow, Task<string?>>? _editSource;
    private readonly Func<string, Task<string?>>? _editSharedSource;
    private readonly Func<ScriptRow, string, Task<string>>? _testSource;
    private readonly Action? _openScriptingHelp;

    public ScriptCollectionEditorViewModel(
        IEnumerable<FieldScript>? scripts = null,
        string? sharedSource = null,
        ScriptTrigger defaultTrigger = ScriptTrigger.AfterFieldsPopulated,
        Func<ScriptRow, Task<string?>>? editSource = null,
        Func<string, Task<string?>>? editSharedSource = null,
        Func<ScriptRow, string, Task<string>>? testSource = null,
        Action? openScriptingHelp = null)
    {
        _defaultTrigger = defaultTrigger;
        _editSource = editSource;
        _editSharedSource = editSharedSource;
        _testSource = testSource;
        _openScriptingHelp = openScriptingHelp;
        _sharedSource = sharedSource ?? string.Empty;
        foreach (var script in scripts ?? []) Scripts.Add(new ScriptRow(script));
    }

    public ObservableCollection<ScriptRow> Scripts { get; } = [];
    [ObservableProperty] private string _sharedSource;

    [RelayCommand]
    private void Add() => Scripts.Add(new ScriptRow(new FieldScript
    {
        Name = NextName(),
        Trigger = _defaultTrigger
    }));

    [RelayCommand(CanExecute = nameof(CanEditSource))]
    private async Task PopOutAsync(ScriptRow? row)
    {
        if (row is null || _editSource is null) return;
        var edited = await _editSource(row);
        if (edited is not null) row.Source = edited;
    }

    [RelayCommand(CanExecute = nameof(CanEditSharedSource))]
    private async Task PopOutSharedSourceAsync()
    {
        if (_editSharedSource is null) return;
        var edited = await _editSharedSource(SharedSource);
        if (edited is not null) SharedSource = edited;
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync(ScriptRow? row)
    {
        if (row is null || _testSource is null) return;
        row.IsTesting = true;
        row.TestResult = string.Empty;
        try
        {
            row.TestResult = await _testSource(row, SharedSource);
        }
        finally
        {
            row.IsTesting = false;
        }
    }

    [RelayCommand]
    private void Remove(ScriptRow? row)
    {
        if (row is not null) Scripts.Remove(row);
    }

    [RelayCommand(CanExecute = nameof(CanOpenScriptingHelp))]
    private void OpenScriptingHelp() => _openScriptingHelp?.Invoke();

    public List<FieldScript> ToModels() => Scripts.Select(row => row.Script).ToList();

    private bool CanEditSource(ScriptRow? row) => row is not null && _editSource is not null;
    private bool CanEditSharedSource() => _editSharedSource is not null;
    private bool CanTest(ScriptRow? row) => row is not null && _testSource is not null && !row.IsTesting;
    private bool CanOpenScriptingHelp() => _openScriptingHelp is not null;

    private string NextName()
    {
        var names = Scripts.Select(script => script.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains("New script")) return "New script";
        for (var number = 2; ; number++)
        {
            var candidate = $"New script {number}";
            if (!names.Contains(candidate)) return candidate;
        }
    }
}
