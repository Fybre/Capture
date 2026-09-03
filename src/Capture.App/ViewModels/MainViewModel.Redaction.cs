using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Capture.App.Services;
using Capture.Core.Batches;
using Capture.Core.Diagnostics;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Scripting;
using Capture.Core.Store;
using Capture.Core.Watch;
using Capture.Export;
using Capture.Scanner;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<Guid, int> _redactionPersistGenerations = [];

    public ObservableCollection<RedactionCandidateRow> RedactionCandidates { get; } = [];

    // Deliberately NOT gated on RedactionStatus: the redacted PDF is a derived artifact regenerated
    // from the original pages plus whatever's currently confirmed, not a one-shot irreversible action —
    // so the checklist (and the button below it) stay available to adjust and re-apply even after a
    // document has already been redacted (manually or via the profile's auto-bypass threshold), not
    // just while it's sitting in PendingReview.
    public bool HasRedactionCandidates => RedactionCandidates.Count > 0;

    /// <summary>True once the selected document has an applied, on-disk redacted PDF to show/open —
    /// drives the "Redacted" confirmation panel, shown alongside (not instead of) the still-editable
    /// checklist below it.</summary>
    public bool HasRedactedFile =>
        SelectedDocument?.Document.RedactionStatus == RedactionStatus.Applied
        && !string.IsNullOrEmpty(SelectedDocument.Document.RedactedPath);

    /// <summary>"Apply" the first time; "Re-apply" once a redacted file already exists, since clicking
    /// it again regenerates (overwrites) that file from the current checkbox state.</summary>
    public string ApplyRedactionsButtonLabel => HasRedactedFile
        ? $"Re-apply redactions ({RedactionCandidates.Count})"
        : $"Apply redactions ({RedactionCandidates.Count})";

    public string ManualRedactionButtonLabel => IsAddingManualRedaction
        ? "Done adding redactions"
        : "Add manual redaction";

    public bool HasSelectedManualRedaction => SelectedRedactionCandidate?.IsManual == true;

    public string RedactSelectedTooltip =>
        "Choose which PII types to detect and redact now, regardless of any profile's Redaction setting.";

    /// <summary>Backs the inline "which redaction set to use" picker opened by the "Redact" toolbar
    /// button — the built-in sets plus whatever custom sets exist, populated fresh each time it opens
    /// since custom sets can change via Settings between uses.</summary>
    public ObservableCollection<RedactionEntitySet> RedactEntitySetOptions { get; } = [];

    [ObservableProperty]
    private RedactionEntitySet? _selectedRedactEntitySet;

    [ObservableProperty]
    private bool _isRedactPickerOpen;

    private IReadOnlyList<DocumentRow> _redactPickerRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManualRedactionButtonLabel))]
    private bool _isAddingManualRedaction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedManualRedaction))]
    [NotifyCanExecuteChangedFor(nameof(RemoveManualRedactionCommand))]
    private RedactionCandidateRow? _selectedRedactionCandidate;

    // Manual "redact now" is available on any document regardless of its profile's Redaction.Enabled
    // flag (or even having a profile at all) — Enabled only gates the automatic post-index pipeline.
    // Clicking "Redact" doesn't run detection immediately: it opens an inline picker (RedactEntitySetOptions
    // + IsRedactPickerOpen) so the reviewer can choose which redaction set to use first, since there's
    // no profile config to fall back on to say what should be detected.
    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private async Task RedactSelectedAsync()
    {
        _redactPickerRows = GetActingRows();
        if (_redactPickerRows.Count == 0)
            return;

        RedactEntitySetOptions.Clear();
        foreach (var set in BuiltInRedactionSets.All)
            RedactEntitySetOptions.Add(set);
        foreach (var set in await _redactionSets.GetAllAsync().ConfigureAwait(true))
            RedactEntitySetOptions.Add(set);

        SelectedRedactEntitySet = RedactEntitySetOptions.FirstOrDefault(set => set.Id == BuiltInRedactionSets.CoreId);
        IsRedactPickerOpen = true;
    }

    [RelayCommand]
    private void CancelRedact()
    {
        IsRedactPickerOpen = false;
        _redactPickerRows = [];
    }

    [RelayCommand]
    private async Task ConfirmRedactAsync()
    {
        var rows = _redactPickerRows;
        IsRedactPickerOpen = false;
        _redactPickerRows = [];
        if (rows.Count == 0)
            return;

        var entities = SelectedRedactEntitySet?.Entities.ToList() ?? [];

        IsBusy = true;
        try
        {
            var failures = 0;
            foreach (var row in rows)
            {
                if (row.Document.Status == DocumentStatus.Error)
                    continue;

                try
                {
                    var settings = new RedactionSettings { Entities = entities };
                    var pages = await _store.GetPagesAsync(row.Id).ConfigureAwait(true);
                    await _redactionDetection.DetectAsync(row.Document, pages, row.Indexes, settings).ConfigureAwait(true);
                    row.NotifyIndexes();
                }
                catch (Exception ex)
                {
                    failures++;
                    Trace.TraceError($"Manual redaction failed for document {row.Id}: {ex}");
                }
            }

            if (SelectedDocument is not null && rows.Contains(SelectedDocument))
            {
                await LoadRedactionCandidatesAsync(SelectedDocument).ConfigureAwait(true);
                RefreshIndexHighlights();
                ApplyRedactionsCommand.NotifyCanExecuteChanged();
            }

            StatusText = failures == 0
                ? $"Redaction checked for {rows.Count} document(s)"
                : $"Redaction checked for {rows.Count} document(s) — {failures} failed";
            if (failures == 0) _toasts.ShowSuccess(StatusText); else _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadRedactionCandidatesAsync(DocumentRow? row)
    {
        RedactionCandidates.Clear();
        SelectedRedactionCandidate = null;

        if (row is not null)
        {
            var candidates = await _redactionCandidates.GetAsync(row.Id).ConfigureAwait(true);
            foreach (var candidate in candidates)
                RedactionCandidates.Add(CreateRedactionCandidateRow(candidate));
        }

        OnPropertyChanged(nameof(HasRedactionCandidates));
        OnPropertyChanged(nameof(HasRedactedFile));
        OnPropertyChanged(nameof(ApplyRedactionsButtonLabel));
    }

    private RedactionCandidateRow CreateRedactionCandidateRow(RedactionCandidate candidate)
    {
        var candidateRow = new RedactionCandidateRow(candidate);
        candidateRow.Selected = () => SelectedRedactionCandidate = candidateRow;
        candidateRow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RedactionCandidateRow.IsConfirmed))
                return;
            RefreshIndexHighlights();
            ApplyRedactionsCommand.NotifyCanExecuteChanged();
        };
        return candidateRow;
    }

    [RelayCommand(CanExecute = nameof(CanToggleManualRedactionMode))]
    private void ToggleManualRedactionMode() => IsAddingManualRedaction = !IsAddingManualRedaction;

    private bool CanToggleManualRedactionMode() =>
        !IsBusy && SelectedDocument is not null && PageImage is not null;

    [RelayCommand]
    private void AddManualRedaction(NormalizedRect rect)
    {
        if (!IsAddingManualRedaction || SelectedDocument is null
            || rect.Width < 0.004f || rect.Height < 0.004f)
            return;

        var candidate = new RedactionCandidate
        {
            Source = RedactionSource.Manual,
            Label = "Manual redaction",
            PageNumber = CurrentPageNumber,
            X = Math.Clamp(rect.X, 0, 1),
            Y = Math.Clamp(rect.Y, 0, 1),
            Width = Math.Clamp(rect.Width, 0.002f, 1),
            Height = Math.Clamp(rect.Height, 0.002f, 1),
            Score = 1f,
            Decision = RedactionDecision.Confirmed
        };
        var row = CreateRedactionCandidateRow(candidate);
        RedactionCandidates.Add(row);
        SelectedRedactionCandidate = row;
        RefreshIndexHighlights();
        SchedulePersistRedactionCandidates();
        StatusText = "Manual redaction added; draw another or click Done adding redactions";
    }

    [RelayCommand]
    private void ChangeManualRedaction(NormalizedRect rect)
    {
        if (!IsAddingManualRedaction || SelectedRedactionCandidate is not { IsManual: true } row)
            return;

        row.Candidate.X = Math.Clamp(rect.X, 0, 1);
        row.Candidate.Y = Math.Clamp(rect.Y, 0, 1);
        row.Candidate.Width = Math.Clamp(rect.Width, 0.002f, 1);
        row.Candidate.Height = Math.Clamp(rect.Height, 0.002f, 1);
        RefreshIndexHighlights();
        SchedulePersistRedactionCandidates();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveManualRedaction))]
    private void RemoveManualRedaction()
    {
        if (SelectedRedactionCandidate is not { IsManual: true } row)
            return;

        RedactionCandidates.Remove(row);
        SelectedRedactionCandidate = null;
        RefreshIndexHighlights();
        SchedulePersistRedactionCandidates();
        StatusText = "Manual redaction removed";
    }

    private bool CanRemoveManualRedaction() =>
        !IsBusy && SelectedRedactionCandidate?.IsManual == true;

    private void SchedulePersistRedactionCandidates()
    {
        if (SelectedDocument is null)
            return;

        var documentId = SelectedDocument.Id;
        var candidates = RedactionCandidates.Select(row => row.Candidate).ToList();
        var generation = _redactionPersistGenerations.TryGetValue(documentId, out var current)
            ? current + 1
            : 1;
        _redactionPersistGenerations[documentId] = generation;
        _ = PersistRedactionCandidatesAfterDelayAsync(documentId, candidates, generation);
    }

    private async Task PersistRedactionCandidatesAfterDelayAsync(
        Guid documentId,
        IReadOnlyList<RedactionCandidate> candidates,
        int generation)
    {
        await Task.Delay(200).ConfigureAwait(true);
        if (!_redactionPersistGenerations.TryGetValue(documentId, out var latest) || latest != generation)
            return;

        try
        {
            await _redactionCandidates.SaveAsync(documentId, candidates).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't save redaction edits: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenRedactedFile()
    {
        var path = SelectedDocument?.Document.RedactedPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            StatusText = "Redacted file not found on disk";
            return;
        }

        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"\"{path}\"")
                    : new ProcessStartInfo("xdg-open", $"\"{path}\"");
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the redacted file: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyRedactions))]
    private async Task ApplyRedactionsAsync()
    {
        if (SelectedDocument is null || RedactionCandidates.Count == 0)
            return;

        IsBusy = true;
        try
        {
            var document = SelectedDocument.Document;
            var candidates = RedactionCandidates.Select(row => row.Candidate).ToList();
            _redactionPersistGenerations[document.Id] =
                _redactionPersistGenerations.TryGetValue(document.Id, out var generation) ? generation + 1 : 1;
            await _redactionCandidates.SaveAsync(document.Id, candidates).ConfigureAwait(true);

            var pages = await _store.GetPagesAsync(document.Id).ConfigureAwait(true);
            await _redactionApplier.ApplyAsync(document, pages, candidates).ConfigureAwait(true);

            // The checklist stays populated (and editable) after this — applying doesn't "use up" the
            // candidates, it just regenerates the redacted file from whatever's currently confirmed, so
            // rejecting a false positive and clicking the button again is exactly how you fix one.
            SelectedDocument.NotifyIndexes();
            RefreshIndexHighlights();
            OnPropertyChanged(nameof(HasRedactedFile));
            OnPropertyChanged(nameof(ApplyRedactionsButtonLabel));
            ApplyRedactionsCommand.NotifyCanExecuteChanged();
            StatusText = document.RedactionStatus == RedactionStatus.Applied
                ? $"Redacted PDF saved to {document.RedactedPath}"
                : $"Redaction failed: {document.RedactionError}";
            if (document.RedactionStatus == RedactionStatus.Applied) _toasts.ShowSuccess(StatusText); else _toasts.ShowError(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApplyRedactions() =>
        !IsBusy && SelectedDocument is not null && RedactionCandidates.Count > 0;

    partial void OnIsAddingManualRedactionChanged(bool value)
    {
        if (value)
            StatusText = "Draw a rectangle on the page; select a manual rectangle to move or resize it";
    }

    partial void OnSelectedRedactionCandidateChanged(RedactionCandidateRow? value)
    {
        if (value is not null)
            SelectedIndex = null;
        RefreshRowSelectionFlags();

        if (value is not null && value.PageNumber >= 1 && value.PageNumber != CurrentPageNumber)
        {
            CurrentPageNumber = value.PageNumber;
            _ = ShowPageAsync();
            return;
        }

        RefreshIndexHighlights();
    }
}
