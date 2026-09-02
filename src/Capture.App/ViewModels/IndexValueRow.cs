using System.Globalization;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public sealed partial class IndexValueRow : ObservableObject
{
    private readonly int _threshold;
    private bool _suppress;

    public IndexValueRow(IndexValue value, int threshold, string? locale, bool scriptingAvailable = false)
    {
        Value = value;
        _threshold = threshold;
        Locale = locale;
        ScriptingAvailable = scriptingAvailable;
        _text = value.Value;
        LookupChoices = value.LookupOptions
            .Select(option => new LookupChoice(option.Key, option.Value))
            .ToList();
        _selectedLookup = LookupChoices.FirstOrDefault(option =>
            string.Equals(option.Value, value.Value, StringComparison.Ordinal));
        _selectedDate = ParseDate(value.Value);
    }

    public IndexValue Value { get; }

    public string Name => Value.FieldName;

    public bool IsBatch => Value.Level == IndexLevel.Batch;

    public string ScopeLabel => IsBatch ? "Batch" : "Document";

    public string? Locale { get; }

    public Action? Changed { get; set; }

    public IReadOnlyList<LookupChoice> LookupChoices { get; }

    public bool IsLookup => Value.Kind == FieldKind.Lookup;

    public bool IsReadOnly => Value.IsReadOnly;

    public bool IsDate => !IsLookup && Value.Format == FieldFormat.Date;

    public bool IsButton => Value.Kind == FieldKind.Button;

    public bool IsTextEntry => !IsLookup && !IsDate && !IsButton;

    public string ButtonLabel => string.IsNullOrEmpty(Value.ButtonLabel) ? Value.FieldName : Value.ButtonLabel;

    /// <summary>Whatever the button's script last wrote to this field's own value, if anything —
    /// shown as a small persistent status line under the button (unlike a toast, this survives after
    /// the notification fades). Not the button's clickable state — see <see cref="ScriptingAvailable"/>.</summary>
    public string ButtonStatus => Value.Value;

    /// <summary>False when scripting is off in Settings (WatchSettings.AllowFieldScripts) — greys the
    /// button out with an explanatory tooltip instead of only failing after a click. Set once at row
    /// construction from MainViewModel, which already knows this.</summary>
    public bool ScriptingAvailable { get; }

    public string ButtonTooltip => ScriptingAvailable
        ? "Runs this button's script against the current document"
        : "Scripting is off — turn on \"Allow profile scripts\" in Settings";

    /// <summary>True while this row's button script is running — disables the button and swaps its
    /// label to a "Running…" state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunButton))]
    [NotifyPropertyChangedFor(nameof(ButtonContent))]
    private bool _isRunning;

    public bool CanRunButton => ScriptingAvailable && !IsRunning;

    public string ButtonContent => IsRunning ? "Running…" : ButtonLabel;

    /// <summary>Invoked when this row is clicked in the review list — MainViewModel wires this to
    /// select the row and highlight its zone in the preview, the reverse of clicking the highlight
    /// itself (see SelectIndexHighlightCommand).</summary>
    public Action? Selected { get; set; }

    [RelayCommand]
    private void Select() => Selected?.Invoke();

    /// <summary>True while this is the row selected via either a click on it or a click on its
    /// highlight in the preview — drives the row's highlighted background in the review list.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private LookupChoice? _selectedLookup;

    [ObservableProperty]
    private DateTime? _selectedDate;

    public string Flag
    {
        get
        {
            if (Value.IsMissing)
                return "Missing";
            if (Value.ValidationError is not null)
                return Value.ValidationError;
            if (Value.IsLowConfidence(_threshold))
                return "Low conf";
            return string.Empty;
        }
    }

    public bool HasFlag => Flag.Length > 0;

    public bool HasFormatError => Value.ValidationError is not null;

    public string ConfidenceDisplay => Value.IsManual
        ? "Manual"
        : $"{Value.Confidence:0}%";

    public double ConfidenceValue => Value.IsManual ? 100 : Value.Confidence;

    partial void OnTextChanged(string value)
    {
        if (_suppress)
            return;

        Commit(value ?? string.Empty);
    }

    partial void OnSelectedLookupChanged(LookupChoice? value)
    {
        if (_suppress)
            return;

        SetTextAndCommit(value?.Value ?? string.Empty);
    }

    partial void OnSelectedDateChanged(DateTime? value)
    {
        if (_suppress)
            return;

        SetTextAndCommit(value?.ToString("d", Culture()) ?? string.Empty);
    }

    private void SetTextAndCommit(string value)
    {
        _suppress = true;
        Text = value;
        _suppress = false;
        Commit(value);
    }

    private void Commit(string value)
    {
        Value.Value = value;
        Value.IsManual = true;
        Value.Confidence = 100;
        Value.ValidationError = IndexFormat.Validate(Value.Value, Value.Format, Locale);
        OnPropertyChanged(nameof(Flag));
        OnPropertyChanged(nameof(HasFlag));
        OnPropertyChanged(nameof(HasFormatError));
        OnPropertyChanged(nameof(ConfidenceDisplay));
        OnPropertyChanged(nameof(ConfidenceValue));
        Changed?.Invoke();
    }

    /// <summary>Re-syncs this row's cached display state from <see cref="Value"/> — needed after
    /// something outside the normal Commit() path (a button script) mutates Value.Value/Confidence
    /// directly, since Value itself doesn't raise property-change notifications the UI could bind to.</summary>
    public void Refresh()
    {
        _suppress = true;
        Text = Value.Value;
        SelectedLookup = LookupChoices.FirstOrDefault(option => string.Equals(option.Value, Value.Value, StringComparison.Ordinal));
        SelectedDate = ParseDate(Value.Value);
        _suppress = false;
        OnPropertyChanged(nameof(Flag));
        OnPropertyChanged(nameof(HasFlag));
        OnPropertyChanged(nameof(HasFormatError));
        OnPropertyChanged(nameof(ConfidenceDisplay));
        OnPropertyChanged(nameof(ConfidenceValue));
        OnPropertyChanged(nameof(ButtonStatus));
    }

    private DateTime? ParseDate(string value) =>
        DateTime.TryParse(value, Culture(), DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : null;

    private CultureInfo Culture()
    {
        if (!string.IsNullOrWhiteSpace(Locale))
        {
            try
            {
                return CultureInfo.GetCultureInfo(Locale);
            }
            catch (CultureNotFoundException)
            {
                // Fall through to the machine's culture, matching IndexFormat validation.
            }
        }

        return CultureInfo.CurrentCulture;
    }

    public void SetTextSilent(string text)
    {
        _suppress = true;
        Text = text;
        _suppress = false;
    }
}
