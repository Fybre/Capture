using Capture.Core.Redaction;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

/// <summary>One row in the redaction review list. Wraps a <see cref="RedactionCandidate"/> — toggling
/// <see cref="IsConfirmed"/> writes straight back to the candidate's Decision so "Apply redactions"
/// always sees the reviewer's latest choice.</summary>
public sealed partial class RedactionCandidateRow : ObservableObject
{
    public RedactionCandidateRow(RedactionCandidate candidate)
    {
        Candidate = candidate;
        _isConfirmed = candidate.Decision != RedactionDecision.Rejected;
    }

    public RedactionCandidate Candidate { get; }

    public Guid Id => Candidate.Id;

    /// <summary>Which PII classification triggered this candidate — a friendly name for a Presidio
    /// entity type code (e.g. "US_SSN" → "Social Security number") for a detected match, or the
    /// Sensitive-marked field's own name for a field-sourced candidate.</summary>
    public string Label => Candidate.Source == RedactionSource.Presidio
        ? PresidioEntityNames.Describe(Candidate.Label)
        : Candidate.Label;

    public string? PreviewText => Candidate.PreviewText;

    public string SourceDisplay => Candidate.Source switch
    {
        RedactionSource.Presidio => "Detected",
        RedactionSource.SensitiveField => "Sensitive field",
        RedactionSource.Manual => "Manual",
        _ => Candidate.Source.ToString()
    };

    public string ScoreDisplay => Candidate.Source == RedactionSource.Manual
        ? string.Empty
        : $"{Candidate.Score * 100:0}%";

    public bool IsManual => Candidate.Source == RedactionSource.Manual;

    public int PageNumber => Candidate.PageNumber;

    [ObservableProperty]
    private bool _isConfirmed;

    partial void OnIsConfirmedChanged(bool value) =>
        Candidate.Decision = value ? RedactionDecision.Confirmed : RedactionDecision.Rejected;

    /// <summary>Invoked when this row is clicked — MainViewModel wires this to select the row and
    /// highlight its region in the preview, the reverse of clicking the highlight itself.</summary>
    public Action? Selected { get; set; }

    [RelayCommand]
    private void Select() => Selected?.Invoke();

    /// <summary>True while this is the candidate selected via either a click on it or a click on its
    /// highlight in the preview — drives the row's highlighted background in the review list.</summary>
    [ObservableProperty]
    private bool _isSelected;
}
