using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Capture.App.Services;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Scripting;
using Capture.Therefore;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class ProfileDesignerViewModel : ViewModelBase
{
    private readonly IProfileSampleService _samples;
    private readonly IProfileStore _store;
    private readonly IRedactionEntitySetStore _redactionSets;
    private readonly IFileDialogService _dialogs;
    private readonly IThereforeCategoryPickerDialogService _thereforeCategoryPicker;
    private readonly IToastService _toasts;
    private readonly IHelpWindowService _help;
    private readonly List<string> _pageImages = [];
    private readonly Dictionary<int, PageLattice> _lattices = [];
    private int _loadGeneration;

    private readonly IBarcodeDecoder? _barcodes;
    private readonly IAiExtractor? _ai;
    private readonly IFieldScriptRunner? _scripts;

    public ProfileDesignerViewModel(
        IndexingProfile profile,
        bool isNew,
        IProfileSampleService samples,
        IProfileStore store,
        IRedactionEntitySetStore redactionSets,
        IFileDialogService dialogs,
        IThereforeCategoryPickerDialogService thereforeCategoryPicker,
        IToastService toasts,
        IHelpWindowService help,
        IBarcodeDecoder? barcodes = null,
        IAiExtractor? ai = null,
        IFieldScriptRunner? scripts = null)
    {
        Profile = profile;
        IsNew = isNew;
        _samples = samples;
        _store = store;
        _redactionSets = redactionSets;
        _dialogs = dialogs;
        _thereforeCategoryPicker = thereforeCategoryPicker;
        _toasts = toasts;
        _help = help;
        _barcodes = barcodes;
        _ai = ai;
        _scripts = scripts;
        _name = profile.Name;
        _separationTrigger = profile.Separation.Trigger;
        _separationPageCount = Math.Max(1, profile.Separation.PageCount);
        _separationDiscardPage = profile.Separation.DiscardSeparatorPage;
        _redactionEnabled = profile.Redaction.Enabled;
        _redactionDetectPii = profile.Redaction.DetectPii;
        _redactionScoreThreshold = profile.Redaction.ScoreThresholdPercent;
        _redactionBypassScoreThreshold = profile.Redaction.BypassReviewScoreThresholdPercent;
        _removeAfterExport = profile.RemoveAfterExport;
        _sampleFileName = profile.SampleFileName;
        foreach (var field in profile.Fields)
            Fields.Add(Wrap(field));
        RefreshBarcodeFieldOptions();
        _separationBarcodeField = BarcodeFieldOptions.FirstOrDefault(field => field.Id == profile.Separation.BarcodeFieldId);
        foreach (var export in profile.Exports)
            Exports.Add(new ExportDefinitionRow(export, Fields));
        Exports.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoExports));
        foreach (var script in profile.Scripts)
            Scripts.Add(new ScriptRow(script));
        Scripts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoScripts));
        Fields.CollectionChanged += (_, _) =>
        {
            RefreshBarcodeFieldOptions();
            foreach (var export in Exports)
                export.RefreshFieldOptions(Fields);
        };
    }

    private void RefreshBarcodeFieldOptions()
    {
        var selectedId = SeparationBarcodeField?.Id;
        BarcodeFieldOptions.Clear();
        foreach (var field in Fields.Where(item => item.IsBarcode))
            BarcodeFieldOptions.Add(field);

        if (selectedId is { } id)
            SeparationBarcodeField = BarcodeFieldOptions.FirstOrDefault(field => field.Id == id);

        OnPropertyChanged(nameof(HasBarcodeFieldOptions));
    }

    public bool HasBarcodeFieldOptions => BarcodeFieldOptions.Count > 0;

    public IndexingProfile Profile { get; }

    public bool IsNew { get; private set; }

    public bool Saved { get; private set; }

    public ObservableCollection<FieldRow> Fields { get; } = [];

    public ObservableCollection<ExportDefinitionRow> Exports { get; } = [];

    public bool HasNoExports => Exports.Count == 0;

    public ObservableCollection<ScriptRow> Scripts { get; } = [];

    public bool HasNoScripts => Scripts.Count == 0;

    /// <summary>False when scripting is off in Settings (WatchSettings.AllowFieldScripts) — purely
    /// informational, shown as an inline hint so a profile author knows a saved script won't actually
    /// run during real import/export yet. Doesn't gate anything here: "Run test" (both profile-level and
    /// per-field) always works regardless, since it's a single-document action the author takes
    /// themselves while editing — only ProfileApplicator's real pipeline checks this for unattended
    /// (batch/watch-folder) execution.</summary>
    public bool IsScriptingAvailable => _scripts?.IsAvailable ?? false;

    [ObservableProperty]
    private bool _removeAfterExport;

    public FieldFormat[] Formats { get; } = Enum.GetValues<FieldFormat>();

    public ICommand? CloseCommand { get; set; }

    public string PageLabel => PageCount == 0 ? "—" : $"{CurrentPageNumber} / {PageCount}";

    public enum PatternSuggestTarget
    {
        None,
        Key,
        Value
    }

    public PatternSuggestTarget SuggestTarget { get; set; }

    public string Hint => "Draw a rectangle for a zone or barcode. Click in key/value/regex, then draw to suggest a pattern. Otherwise draw to set a search region.";

    public IReadOnlyList<DocumentSeparationTrigger> SeparationTriggerOptions { get; } = Enum.GetValues<DocumentSeparationTrigger>();

    public ObservableCollection<FieldRow> BarcodeFieldOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSeparationBarcode))]
    [NotifyPropertyChangedFor(nameof(IsSeparationBlank))]
    [NotifyPropertyChangedFor(nameof(IsSeparationEveryNPages))]
    private DocumentSeparationTrigger _separationTrigger;

    [ObservableProperty]
    private FieldRow? _separationBarcodeField;

    [ObservableProperty]
    private int _separationPageCount = 1;

    [ObservableProperty]
    private bool _separationDiscardPage;

    public bool IsSeparationBarcode => SeparationTrigger == DocumentSeparationTrigger.Barcode;

    public bool IsSeparationBlank => SeparationTrigger == DocumentSeparationTrigger.BlankPage;

    public bool IsSeparationEveryNPages => SeparationTrigger == DocumentSeparationTrigger.EveryNPages;

    public ObservableCollection<RedactionEntitySet> RedactionSetOptions { get; } = [];

    [ObservableProperty]
    private RedactionEntitySet? _selectedRedactionSet;

    [ObservableProperty]
    private bool _redactionEnabled;

    [ObservableProperty]
    private bool _redactionDetectPii = true;

    [ObservableProperty]
    private int _redactionScoreThreshold = 50;

    [ObservableProperty]
    private int _redactionBypassScoreThreshold = 100;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private FieldRow? _selectedField;

    [ObservableProperty]
    private Bitmap? _pageImage;

    [ObservableProperty]
    private IReadOnlyList<IndexHighlight> _highlights = [];

    [ObservableProperty]
    private bool _showOcrWords;

    /// <summary>The current page's recognized OCR/PDF-text words, for the "Show OCR text" overlay
    /// toggle — lets someone drawing a zone/pattern see exactly where extraction thinks text is,
    /// rather than guessing why a draw came back empty. Computed from the same <see cref="_lattices"/>
    /// used for extraction itself, so it's always in sync; <see cref="RefreshHighlights"/> is the
    /// existing "something about the page changed" choke point, so it raises this too.</summary>
    public IReadOnlyList<LatticeWord> CurrentPageWords =>
        _lattices.TryGetValue(CurrentPageNumber, out var lattice) ? lattice.Words : [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _currentPageNumber = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _pageCount;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExtractAiCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeSampleCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunScriptTestCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunFieldScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunButtonFieldTestCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _sampleFileName;

    public async Task InitializeAsync()
    {
        RedactionSetOptions.Clear();
        foreach (var set in BuiltInRedactionSets.All)
            RedactionSetOptions.Add(set);
        foreach (var set in await _redactionSets.GetAllAsync().ConfigureAwait(true))
            RedactionSetOptions.Add(set);

        SelectedRedactionSet = (Profile.Redaction.EntitySetId is { } setId
            ? RedactionSetOptions.FirstOrDefault(set => set.Id == setId)
            : null) ?? RedactionSetOptions.FirstOrDefault(set => set.Id == BuiltInRedactionSets.CoreId);

        await LoadSampleAsync().ConfigureAwait(true);
        StatusText = PageCount == 0 ? "No sample pages" : Hint;
    }

    // Shared by InitializeAsync (first load) and ChangeSampleAsync (swapping in a different sample
    // file for an existing profile) — reloads the on-disk page images/lattices for Profile.Id from
    // scratch, since ProfileSampleService.PrepareAsync may have just replaced them entirely.
    private async Task LoadSampleAsync()
    {
        _pageImages.Clear();
        _pageImages.AddRange(_samples.GetPageImagePaths(Profile.Id));
        _lattices.Clear();
        PageCount = _pageImages.Count;
        CurrentPageNumber = 1;
        for (var page = 1; page <= PageCount; page++)
        {
            var lattice = await _samples.GetLatticeAsync(Profile.Id, page).ConfigureAwait(true);
            if (lattice is not null)
                _lattices[page] = lattice;
        }

        await ShowPageAsync().ConfigureAwait(true);
        RefreshAllExtracts();
    }

    [RelayCommand(CanExecute = nameof(CanChangeSample))]
    private async Task ChangeSampleAsync()
    {
        var path = await _dialogs.PickFileAsync("Choose a sample document").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsBusy = true;
        try
        {
            StatusText = "Preparing sample…";
            await _samples.PrepareAsync(Profile, path).ConfigureAwait(true);
            SampleFileName = Profile.SampleFileName;
            await LoadSampleAsync().ConfigureAwait(true);
            StatusText = PageCount == 0 ? "No sample pages" : "Sample updated";
            if (PageCount == 0) _toasts.ShowError(StatusText); else _toasts.ShowSuccess(StatusText);
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

    private bool CanChangeSample() => !IsBusy;

    [RelayCommand]
    private void CompleteZone(NormalizedRect rect)
    {
        if (rect.Width < 0.004f || rect.Height < 0.004f)
            return;

        if (SelectedField is { IsPatternField: true } patternField && SuggestTarget != PatternSuggestTarget.None)
        {
            SuggestPatternFromZone(patternField, rect);
            return;
        }

        if (SelectedField is { IsPatternField: true })
        {
            ApplySearchZone(SelectedField, rect);
            return;
        }

        if (SelectedField is { IsZoneField: true })
        {
            AssignZone(SelectedField, rect);
            return;
        }

        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.Zonal),
            Kind = FieldKind.Zonal,
            Format = FieldFormat.String,
            PageNumber = CurrentPageNumber,
            Zone = new ZoneRect
            {
                PageNumber = CurrentPageNumber,
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height
            }
        };

        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        Extract(row);
        RefreshHighlights();
    }

    [RelayCommand]
    private void AddZone()
    {
        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.Zonal),
            Kind = FieldKind.Zonal,
            Format = FieldFormat.String,
            PageNumber = CurrentPageNumber
        };

        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = "Draw on the page to set the zone";
    }

    [RelayCommand]
    private void AddText()
    {
        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.Text),
            Kind = FieldKind.Text,
            Format = FieldFormat.String
        };
        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = "Value entered manually in the indexing panel";
    }

    [RelayCommand]
    private void AddLookup()
    {
        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.Lookup),
            Kind = FieldKind.Lookup,
            Format = FieldFormat.String
        };
        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = "Add display labels and their exported values";
    }

    [RelayCommand]
    private void AddScriptField()
    {
        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.Script),
            Kind = FieldKind.Script,
            Format = FieldFormat.String
        };
        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = "Enter a C# expression — other fields are read-only, its result becomes this field's value";
    }

    [RelayCommand]
    private void AddButtonField()
    {
        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.Button),
            Kind = FieldKind.Button,
            Format = FieldFormat.String
        };
        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = "Enter a button label and a script — unlike a Script field, it can write to any field, but only runs when clicked";
    }

    [RelayCommand]
    private void AddBarcode()
    {
        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.Barcode),
            Kind = FieldKind.Barcode,
            Format = FieldFormat.String,
            PageNumber = CurrentPageNumber,
            PageScope = PageScope.Number,
            PageScopeConfigured = true
        };

        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = "Draw a zone around the barcode, or leave empty to scan the whole page";
    }

    [RelayCommand]
    private void AddKeyValue()
    {
        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.KeyValue),
            Kind = FieldKind.KeyValue,
            Format = FieldFormat.String,
            KeyPattern = string.Empty,
            ValuePattern = ValuePatterns.For(FieldFormat.String),
            Occurrence = MatchOccurrence.First,
            PageScope = PageScope.First,
            PageNumber = CurrentPageNumber
        };

        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = @"Enter a key pattern, e.g. Invoice\s*No";
    }

    [RelayCommand]
    private void AddRegex()
    {
        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.Regex),
            Kind = FieldKind.Regex,
            Format = FieldFormat.String,
            ValuePattern = @"(.+)",
            Occurrence = MatchOccurrence.First,
            PageScope = PageScope.First,
            PageNumber = CurrentPageNumber
        };

        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = @"Enter a regex, e.g. PO[-\s]?(\d+) — group 1 is the value if present.";
    }

    [RelayCommand]
    private void AddAi()
    {
        var type = AiFieldCatalog.All[0];
        var field = new IndexField
        {
            Name = type.Name,
            Kind = FieldKind.Ai,
            Format = type.Format,
            AiTypeId = type.Id,
            PageNumber = CurrentPageNumber
        };
        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = _ai?.IsConfigured == true
            ? "Choose a field type, then Extract with AI"
            : "Configure an OpenAI endpoint in Settings";
    }

    [RelayCommand]
    private void AddBatchSeparatorValue()
    {
        var field = new IndexField
        {
            Name = NextFieldName(FieldKind.BatchSeparatorValue),
            Kind = FieldKind.BatchSeparatorValue,
            Format = FieldFormat.String,
            PageNumber = CurrentPageNumber
        };

        var row = Wrap(field);
        Fields.Add(row);
        SelectedField = row;
        StatusText = "Value supplied at import time from the batch profile's barcode/regex trigger — nothing to configure here.";
    }

    [RelayCommand(CanExecute = nameof(CanExtractAi))]
    private async Task ExtractAiAsync()
    {
        var rows = Fields.Where(item => item.IsAi).ToList();
        if (rows.Count == 0)
            return;
        if (_ai is null || !_ai.IsConfigured)
        {
            StatusText = "Configure an OpenAI endpoint in Settings";
            return;
        }

        IsBusy = true;
        StatusText = rows.Count == 1 ? "Extracting with AI…" : $"Extracting {rows.Count} AI fields…";
        try
        {
            var text = DocumentText.FromLattices(_lattices.Values);
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText = "No document text to send";
                return;
            }

            var extracted = await _ai.ExtractAsync(text, rows.Select(item => item.Field).ToList()).ConfigureAwait(true);
            foreach (var row in rows)
            {
                if (!extracted.TryGetValue(row.Id, out var hit))
                    continue;
                row.LiveValue = hit.Value;
                row.LiveConfidence = hit.Confidence;
            }

            StatusText = extracted.Count == 0 ? "AI returned no values" : $"Extracted {extracted.Count} AI field(s)";
            if (extracted.Count == 0) _toasts.ShowError(StatusText); else _toasts.ShowSuccess(StatusText);
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

    [RelayCommand]
    private void ClearSearchZone()
    {
        if (SelectedField is not { IsPatternField: true })
            return;

        SelectedField.Field.SearchZone = null;
        SelectedField.NotifySearchZone();
        Extract(SelectedField);
        RefreshHighlights();
        StatusText = "Search region cleared";
    }

    [RelayCommand]
    private void ChangeZone(NormalizedRect rect)
    {
        if (SelectedField is { IsPatternField: true })
        {
            ApplySearchZone(SelectedField, rect);
            return;
        }

        if (SelectedField?.Field.Zone is null)
            return;

        var zone = SelectedField.Field.Zone;
        zone.X = Math.Clamp(rect.X, 0, 1);
        zone.Y = Math.Clamp(rect.Y, 0, 1);
        zone.Width = Math.Clamp(rect.Width, 0.002f, 1);
        zone.Height = Math.Clamp(rect.Height, 0.002f, 1);
        zone.PageNumber = CurrentPageNumber;
        SelectedField.Field.PageNumber = CurrentPageNumber;
        SelectedField.NotifyPage();
        Extract(SelectedField);
        RefreshHighlights();
    }

    [RelayCommand]
    private void SelectHighlight(Guid id)
    {
        var row = Fields.FirstOrDefault(item => item.Id == id);
        if (row is not null)
            SelectedField = row;
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPage()
    {
        CurrentPageNumber--;
        _ = ShowPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage()
    {
        CurrentPageNumber++;
        _ = ShowPageAsync();
    }

    [RelayCommand]
    private void DeleteField()
    {
        if (SelectedField is null)
            return;

        var index = Fields.IndexOf(SelectedField);
        Fields.Remove(SelectedField);
        SelectedField = Fields.Count == 0 ? null : Fields[Math.Clamp(index, 0, Fields.Count - 1)];
        RefreshHighlights();
    }

    [RelayCommand]
    private void AddExportDefinition()
    {
        // Expanded by default — a newly added export has nothing configured yet, so its settings
        // should be immediately visible rather than needing an extra click to reveal them.
        var row = new ExportDefinitionRow(new ExportDefinition(), Fields) { IsExpanded = true };
        Exports.Add(row);
    }

    [RelayCommand]
    private void RemoveExportDefinition(ExportDefinitionRow row) => Exports.Remove(row);

    [RelayCommand]
    private void AddScript() => Scripts.Add(new ScriptRow(new FieldScript()));

    [RelayCommand]
    private void RemoveScript(ScriptRow row) => Scripts.Remove(row);

    [RelayCommand]
    private void ShowScriptingHelp()
    {
        if (_dialogs.Host is { } host)
            _help.ShowScripting(host);
    }

    /// <summary>Snapshots every field's current designer-preview value into real IndexValue objects — the
    /// same shape ProfileApplicator hands scripts at real import time, built from whatever the field
    /// panel is currently showing (LiveValue/LiveConfidence) rather than a live document.</summary>
    private List<IndexValue> BuildPreviewIndexValues() =>
        Fields.Select(row => new IndexValue
        {
            FieldId = row.Id,
            FieldName = row.Name,
            Format = row.Format,
            Value = row.LiveValue ?? string.Empty,
            Confidence = row.LiveConfidence
        }).ToList();

    private ScriptExecutionContext BuildScriptPreviewContext(List<IndexValue> values) => new()
    {
        ProfileName = Name,
        DocumentNumber = 1,
        BatchNumber = 1,
        Timestamp = DateTimeOffset.Now,
        Values = values,
        Document = new ScriptDocumentInfo
        {
            FileName = SampleFileName ?? string.Empty,
            FileExtension = string.IsNullOrEmpty(SampleFileName) ? string.Empty : Path.GetExtension(SampleFileName).ToLowerInvariant(),
            PageCount = _pageImages.Count,
            Text = DocumentText.FromLattices(_lattices.Values)
        }
    };

    [RelayCommand(CanExecute = nameof(CanRunScript))]
    private Task RunScriptTestAsync(ScriptRow row) => RunImperativeScriptAsync(row.Script);

    [RelayCommand(CanExecute = nameof(CanRunScript))]
    private Task RunButtonFieldTestAsync()
    {
        if (SelectedField is not { IsButton: true } row)
            return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(row.ButtonScriptSource))
        {
            StatusText = "Enter a script first";
            return Task.CompletedTask;
        }

        // The real field's Id, not a fresh Guid, so the compiled-script cache is reused across repeated
        // "Run test" clicks (same reasoning as the real review-panel button handler).
        return RunImperativeScriptAsync(new FieldScript
        {
            Id = row.Id,
            Name = row.Name,
            Source = row.ButtonScriptSource,
            TimeoutSeconds = row.ButtonTimeoutSeconds
        });
    }

    /// <summary>Shared by RunScriptTestAsync (a profile-level FieldScript) and RunButtonFieldTestAsync
    /// (an ephemeral FieldScript built from a Button field) — both run imperative, mutable-Fields
    /// scripts against the designer's live sample state and reflect any mutation back into the visible
    /// field rows the same way.</summary>
    private async Task RunImperativeScriptAsync(FieldScript script)
    {
        if (_scripts is null)
            return;

        IsBusy = true;
        StatusText = "Running script…";
        try
        {
            var values = BuildPreviewIndexValues();
            var result = await _scripts.RunProfileScriptAsync(script, BuildScriptPreviewContext(values)).ConfigureAwait(true);
            if (!result.Success)
            {
                StatusText = $"Script failed: {result.ErrorMessage}";
                _toasts.ShowError(StatusText);
                return;
            }

            // Reflect any mutations the script made back into the visible field rows.
            foreach (var value in values)
            {
                var fieldRow = Fields.FirstOrDefault(item => item.Id == value.FieldId);
                if (fieldRow is null)
                    continue;
                fieldRow.LiveValue = value.Value;
                fieldRow.LiveConfidence = value.Confidence;
            }

            StatusText = "Script ran successfully";
            _toasts.ShowSuccess(StatusText);
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

    [RelayCommand(CanExecute = nameof(CanRunScript))]
    private async Task RunFieldScriptAsync()
    {
        if (SelectedField is not { IsScript: true } row || _scripts is null)
            return;
        if (string.IsNullOrWhiteSpace(row.ScriptExpression))
        {
            StatusText = "Enter an expression first";
            return;
        }

        IsBusy = true;
        StatusText = "Running script…";
        try
        {
            var context = BuildScriptPreviewContext(BuildPreviewIndexValues());
            var result = await _scripts.RunFieldExpressionAsync(row.Id, row.ScriptExpression, context).ConfigureAwait(true);
            if (!result.Success)
            {
                StatusText = $"Script failed: {result.ErrorMessage}";
                _toasts.ShowError(StatusText);
                return;
            }

            row.LiveValue = result.Value ?? string.Empty;
            row.LiveConfidence = 100;
            StatusText = "Script ran successfully";
            _toasts.ShowSuccess(StatusText);
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

    private bool CanRunScript() => !IsBusy && _scripts is not null;

    [RelayCommand]
    private void ToggleExportExpanded(ExportDefinitionRow row) => row.IsExpanded = !row.IsExpanded;

    [RelayCommand]
    private async Task BrowseExportFolderAsync(ExportDefinitionRow row)
    {
        var folder = await _dialogs.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
            row.OutputFolder = folder;
    }

    [RelayCommand]
    private async Task BrowseThereforeCategoryAsync(ExportDefinitionRow row)
    {
        if (_dialogs.Host is not { } host)
            return;

        var selection = await _thereforeCategoryPicker.ShowAsync(host).ConfigureAwait(true);
        if (selection is null)
            return;

        row.Definition.ThereforeCategoryNo = selection.CategoryNo;
        row.Definition.ThereforeCategoryName = selection.CategoryName;
        // Fresh IndexFieldId = null every time — re-browsing a category resets mappings, since the
        // field set may have changed.
        row.Definition.ThereforeFieldMappings = selection.Fields.Select(field => new ThereforeFieldMapping
        {
            FieldNo = field.FieldNo,
            Caption = field.Caption,
            IndexDataFieldName = field.IndexDataFieldName,
            FieldType = (int)field.FieldType,
            Mandatory = field.Mandatory,
            IndexFieldId = null
        }).ToList();
        row.RefreshThereforeMappings();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Profile.Name = string.IsNullOrWhiteSpace(Name) ? "Untitled profile" : Name.Trim();
        Profile.Separation = new DocumentSeparation
        {
            Trigger = SeparationTrigger,
            BarcodeFieldId = SeparationBarcodeField?.Id,
            PageCount = Math.Max(1, SeparationPageCount),
            DiscardSeparatorPage = SeparationDiscardPage
        };
        Profile.Redaction = new RedactionSettings
        {
            Enabled = RedactionEnabled,
            DetectPii = RedactionDetectPii,
            EntitySetId = SelectedRedactionSet?.Id,
            Entities = SelectedRedactionSet?.Entities.ToList() ?? [],
            ScoreThresholdPercent = Math.Clamp(RedactionScoreThreshold, 0, 100),
            BypassReviewScoreThresholdPercent = Math.Clamp(RedactionBypassScoreThreshold, 0, 100),
            Language = "en"
        };
        Profile.Fields = Fields.Select(row => row.Field).ToList();
        Profile.Exports = Exports.Select(row => row.Definition).ToList();
        Profile.Scripts = Scripts.Select(row => row.Script).ToList();
        Profile.RemoveAfterExport = RemoveAfterExport;
        await _store.SaveAsync(Profile);
        Saved = true;
        IsNew = false;
        StatusText = "Saved";
        _toasts.ShowSuccess($"Saved \"{Profile.Name}\"");
    }

    partial void OnSelectedFieldChanged(FieldRow? value)
    {
        OnPropertyChanged(nameof(SelectedFieldPageNumber));

        if (value is not null && value.Field.PageNumber != CurrentPageNumber && value.Field.PageNumber >= 1)
        {
            CurrentPageNumber = value.Field.PageNumber;
            _ = ShowPageAsync();
            return;
        }

        RefreshHighlights();
        if (value is not null)
            Extract(value);
    }

    /// <summary>Lets the user see and correct which page a Zonal/Barcode field's zone targets, instead of
    /// it only ever being set implicitly by whichever page happened to be showing when the zone was drawn.
    /// Jumps the viewer to the new page so a wrong zone can be spotted and redrawn immediately.</summary>
    public int? SelectedFieldPageNumber
    {
        get => SelectedField?.Field.PageNumber;
        set
        {
            if (SelectedField is null || value is null)
                return;

            var page = Math.Clamp(value.Value, 1, Math.Max(1, PageCount));
            if (page == SelectedField.Field.PageNumber)
                return;

            SelectedField.Field.PageNumber = page;
            if (SelectedField.Field.Zone is not null)
                SelectedField.Field.Zone.PageNumber = page;
            SelectedField.NotifyPage();
            OnPropertyChanged();

            CurrentPageNumber = page;
            _ = ShowPageAsync();
            RefreshHighlights();
        }
    }

    partial void OnNameChanged(string value)
    {
        Profile.Name = value;
    }

    partial void OnSeparationTriggerChanged(DocumentSeparationTrigger value)
    {
        // Default "remove separator page" on for the common case when a user freshly switches to
        // Blank pages, so the first click through doesn't require a second one to get today's
        // long-standing default (blank pages were always discarded before this was configurable).
        if (value == DocumentSeparationTrigger.BlankPage && !SeparationDiscardPage)
            SeparationDiscardPage = true;
    }

    private bool CanExtractAi() => !IsBusy;

    private bool CanGoPrevious() => !IsBusy && CurrentPageNumber > 1;

    private bool CanGoNext() => !IsBusy && CurrentPageNumber < PageCount;

    private async Task ShowPageAsync()
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        SetPageImage(null);

        if (CurrentPageNumber < 1 || CurrentPageNumber > _pageImages.Count)
        {
            Highlights = [];
            return;
        }

        var path = _pageImages[CurrentPageNumber - 1];
        var bitmap = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }).ConfigureAwait(true);

        if (generation != _loadGeneration)
        {
            bitmap.Dispose();
            return;
        }

        SetPageImage(bitmap);

        if (!_lattices.ContainsKey(CurrentPageNumber))
        {
            var lattice = await _samples.GetLatticeAsync(Profile.Id, CurrentPageNumber).ConfigureAwait(true);
            if (generation != _loadGeneration)
                return;
            if (lattice is not null)
                _lattices[CurrentPageNumber] = lattice;
        }

        RefreshHighlights();
        if (SelectedField is not null)
            Extract(SelectedField);
    }

    private void Extract(FieldRow row)
    {
        row.MatchBounds = null;

        if (row.IsAi)
        {
            row.LiveFormat = "AI";
            return;
        }

        if (row.IsScript)
        {
            // Evaluating a script needs the async runner (and every other field's current preview
            // value) — see RunFieldScriptAsync. Nothing to compute synchronously here.
            row.LiveFormat = "Script";
            return;
        }

        if (row.IsButton)
        {
            // A Button field's value only ever changes via RunButtonFieldTestAsync/the real review
            // panel's button click — there's nothing to extract automatically.
            row.LiveFormat = "Button";
            return;
        }

        if (row.IsBatchSeparatorValue)
        {
            row.LiveValue = string.Empty;
            row.LiveFormat = "Batch trigger";
            row.LiveConfidence = 0;
            StatusText = "This field's value comes from whichever batch profile trigger fires at import time — there's nothing to preview here.";
            return;
        }

        if (row.IsBarcode)
        {
            var page = row.Field.Zone?.PageNumber ?? row.Field.PageNumber;
            if (page < 1 || page > _pageImages.Count)
                page = CurrentPageNumber;
            if (_barcodes is null || page < 1 || page > _pageImages.Count)
            {
                row.LiveValue = string.Empty;
                row.LiveFormat = string.Empty;
                row.LiveConfidence = 0;
                return;
            }

            var decoded = _barcodes.Decode(_pageImages[page - 1], row.Field.Zone);
            row.LiveValue = decoded?.Text ?? string.Empty;
            row.LiveFormat = BarcodePatterns.DisplayType(decoded?.Format);
            row.LiveConfidence = decoded?.Confidence ?? 0;
            if (decoded is not null)
            {
                row.Field.BarcodeFormat = decoded.Format;
                if (row.Field.Zone is null && decoded.Bounds is not null)
                {
                    decoded.Bounds.PageNumber = page;
                    row.Field.Zone = decoded.Bounds;
                    row.Field.PageNumber = page;
                    row.NotifyPage();
                    RefreshHighlights();
                }

                StatusText = $"Found {row.LiveFormat}: {decoded.Text}";
            }
            else
            {
                StatusText = "No barcode found on this page — draw a zone around it";
            }

            return;
        }

        if (row.IsText && !string.IsNullOrEmpty(row.DefaultValueTemplate))
        {
            // Mirrors ProfileApplicator.ApplyDefaults's anti-chaining rule: a field that itself has a
            // default is never offered as a {FieldName} reference for another default's preview.
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Fields)
            {
                if (item.Id == row.Id || (item.IsText && !string.IsNullOrEmpty(item.DefaultValueTemplate)))
                    continue;
                fields[item.Name] = item.LiveValue ?? string.Empty;
            }

            var previewContext = new DefaultValueContext
            {
                DocumentNumber = 1,
                BatchNumber = 1,
                Timestamp = DateTimeOffset.Now,
                ProfileName = Name,
                Fields = fields
            };
            if (DefaultValueTemplateEvaluator.TryEvaluate(
                    row.DefaultValueTemplate,
                    previewContext,
                    out var preview,
                    out var templateError))
            {
                row.LiveValue = preview;
                row.LiveConfidence = 100;
            }
            else
            {
                row.LiveValue = string.Empty;
                row.LiveConfidence = 0;
                StatusText = templateError ?? "Invalid default value";
            }
            return;
        }

        if (row.IsKeyValue || row.IsRegex)
        {
            var result = row.IsRegex
                ? RegexExtractor.Extract(_lattices.Values.ToList(), row.Field)
                : KeyValueExtractor.Extract(_lattices.Values.ToList(), row.Field);
            row.LiveValue = result.Text;
            row.LiveConfidence = result.Confidence;
            row.MatchBounds = result.Bounds;
            if (result.Bounds is not null)
                row.Field.PageNumber = result.PageNumber;
            return;
        }

        if (row.Field.Zone is null)
        {
            row.LiveValue = string.Empty;
            row.LiveConfidence = 0;
            return;
        }

        if (!_lattices.TryGetValue(row.Field.PageNumber, out var lattice))
            return;

        var zonal = ZonalExtractor.Extract(lattice, row.Field.Zone);
        row.LiveValue = zonal.Text;
        row.LiveConfidence = zonal.Confidence;
    }

    private void RefreshAllExtracts()
    {
        foreach (var row in Fields)
            Extract(row);
    }

    private void RefreshHighlights()
    {
        Highlights = Fields.SelectMany(row => HighlightsFor(row, CurrentPageNumber)).ToList();
        OnPropertyChanged(nameof(CurrentPageWords));
    }

    private IEnumerable<IndexHighlight> HighlightsFor(FieldRow row, int pageNumber)
    {
        if (row.IsZoneField && row.Field.Zone is not null && row.Field.PageNumber == pageNumber)
        {
            yield return new IndexHighlight
            {
                FieldId = row.Id,
                FieldName = row.Name,
                X = row.Field.Zone.X,
                Y = row.Field.Zone.Y,
                Width = row.Field.Zone.Width,
                Height = row.Field.Zone.Height,
                IsSelected = SelectedField?.Id == row.Id,
                CanEdit = true
            };
            yield break;
        }

        if (!row.IsPatternField)
            yield break;

        if (row.MatchBounds is not null && row.MatchBounds.PageNumber == pageNumber)
        {
            yield return new IndexHighlight
            {
                FieldId = row.Id,
                FieldName = row.Name,
                X = row.MatchBounds.X,
                Y = row.MatchBounds.Y,
                Width = row.MatchBounds.Width,
                Height = row.MatchBounds.Height,
                IsSelected = false,
                CanEdit = false
            };
        }

        if (row.Field.SearchZone is not null && row.Field.SearchZone.PageNumber == pageNumber)
        {
            yield return new IndexHighlight
            {
                FieldId = row.Id,
                FieldName = row.Name,
                X = row.Field.SearchZone.X,
                Y = row.Field.SearchZone.Y,
                Width = row.Field.SearchZone.Width,
                Height = row.Field.SearchZone.Height,
                IsSelected = SelectedField?.Id == row.Id,
                CanEdit = true,
                IsSearchZone = true
            };
        }
    }

    public void BeginSuggestKey()
    {
        SuggestTarget = PatternSuggestTarget.Key;
        StatusText = "Draw on the page to suggest a key pattern";
    }

    public void BeginSuggestValue()
    {
        SuggestTarget = PatternSuggestTarget.Value;
        StatusText = "Draw on the page to suggest a value pattern";
    }

    private void SuggestPatternFromZone(FieldRow row, NormalizedRect rect)
    {
        if (!_lattices.TryGetValue(CurrentPageNumber, out var lattice))
        {
            StatusText = "No text on this page to suggest from";
            return;
        }

        var zone = new ZoneRect
        {
            PageNumber = CurrentPageNumber,
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height
        };
        var sample = ZonalExtractor.Extract(lattice, zone).Text;
        if (string.IsNullOrWhiteSpace(sample))
        {
            StatusText = "No text in that selection";
            return;
        }

        if (SuggestTarget == PatternSuggestTarget.Key && row.IsKeyValue)
        {
            row.KeyPattern = PatternSuggester.ForKey(sample);
            StatusText = $"Key pattern from “{TrimSample(sample)}”";
        }
        else
        {
            row.ValuePattern = PatternSuggester.ForValue(sample, row.Format);
            StatusText = $"Value pattern from “{TrimSample(sample)}”";
        }

        Extract(row);
        RefreshHighlights();
    }

    private static string TrimSample(string sample)
    {
        sample = sample.Trim();
        return sample.Length <= 40 ? sample : sample[..40] + "…";
    }

    private void AssignZone(FieldRow row, NormalizedRect rect)
    {
        row.Field.PageNumber = CurrentPageNumber;
        row.Field.Zone = new ZoneRect
        {
            PageNumber = CurrentPageNumber,
            X = Math.Clamp(rect.X, 0, 1),
            Y = Math.Clamp(rect.Y, 0, 1),
            Width = Math.Clamp(rect.Width, 0.002f, 1),
            Height = Math.Clamp(rect.Height, 0.002f, 1)
        };
        row.NotifyPage();
        Extract(row);
        RefreshHighlights();
        StatusText = $"Zone set for {row.Name}";
    }

    private void ApplySearchZone(FieldRow row, NormalizedRect rect)
    {
        row.Field.SearchZone = new ZoneRect
        {
            PageNumber = CurrentPageNumber,
            X = Math.Clamp(rect.X, 0, 1),
            Y = Math.Clamp(rect.Y, 0, 1),
            Width = Math.Clamp(rect.Width, 0.002f, 1),
            Height = Math.Clamp(rect.Height, 0.002f, 1)
        };
        row.Field.PageScope = PageScope.Number;
        row.Field.PageNumber = CurrentPageNumber;
        row.PageScope = PageScope.Number;
        row.NotifySearchZone();
        Extract(row);
        RefreshHighlights();
        StatusText = "Search region set";
    }

    private FieldRow Wrap(IndexField field)
    {
        var row = new FieldRow(field);
        row.PropertyChanged += OnFieldPropertyChanged;
        return row;
    }

    private void OnFieldPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FieldRow row)
            return;

        if (e.PropertyName is nameof(FieldRow.LiveValue)
            or nameof(FieldRow.LiveFormat)
            or nameof(FieldRow.HasLiveFormat)
            or nameof(FieldRow.LiveConfidence)
            or nameof(FieldRow.Name)
            or nameof(FieldRow.Mandatory)
            or nameof(FieldRow.PageDisplay)
            or nameof(FieldRow.ConfidenceDisplay)
            or nameof(FieldRow.HasSearchZone)
            or nameof(FieldRow.KindDisplay)
            or nameof(FieldRow.Level)
            or nameof(FieldRow.SelectedClassification)
            or nameof(FieldRow.SelectedAiType)
            or nameof(FieldRow.AiPrompt)
            or nameof(FieldRow.AiTypes))
            return;

        if (e.PropertyName == nameof(FieldRow.PageScope) && row.PageScope == PageScope.Number)
            row.Field.PageNumber = CurrentPageNumber;

        Extract(row);
        RefreshHighlights();
    }

    private string NextFieldName(FieldKind kind)
    {
        var n = Fields.Count + 1;
        string name;
        do
        {
            name = $"Field{n}_{kind}";
            n++;
        } while (Fields.Any(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)));

        return name;
    }

    private void SetPageImage(Bitmap? bitmap)
    {
        var previous = PageImage;
        PageImage = bitmap;
        previous?.Dispose();
    }
}
