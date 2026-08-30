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

    public IndexValueRow(IndexValue value, int threshold, string? locale)
    {
        Value = value;
        _threshold = threshold;
        Locale = locale;
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

    public bool IsTextEntry => !IsLookup && !IsDate;

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
