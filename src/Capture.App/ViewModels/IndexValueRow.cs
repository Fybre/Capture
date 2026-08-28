using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

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
    }

    public IndexValue Value { get; }

    public string Name => Value.FieldName;

    public bool IsBatch => Value.Level == IndexLevel.Batch;

    public string ScopeLabel => IsBatch ? "Batch" : "Document";

    public string? Locale { get; }

    public Action? Changed { get; set; }

    [ObservableProperty]
    private string _text;

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

        Value.Value = value ?? string.Empty;
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

    public void SetTextSilent(string text)
    {
        _suppress = true;
        Text = text;
        _suppress = false;
    }
}
