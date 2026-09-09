using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Models;
using Capture.Core.Store;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Scripting;
using Capture.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public enum CaptureDesignerSection { Overview, Batch, DocumentType, TestCapture }
public enum SampleZoneTarget
{
    BatchRule,
    RecognitionRule,
    DocumentStartRule,
    FieldZone,
    FieldSearchArea,
    FieldKeyPattern,
    FieldValuePattern
}
public sealed record PageDispositionChoice(PageDisposition Value, string Label);
public sealed record TestCaptureIndexRow(string Name, string Value, string Confidence, string Issue)
{
    public bool HasIssue => !string.IsNullOrWhiteSpace(Issue);
}
public sealed record TestCaptureDocumentRow(
    string Heading,
    string Detail,
    string Status,
    IReadOnlyList<TestCaptureIndexRow> Indexes)
{
    public bool HasIndexes => Indexes.Count > 0;
}
public sealed record TestCaptureBatchRow(
    string Heading,
    IReadOnlyList<TestCaptureIndexRow> Indexes,
    IReadOnlyList<TestCaptureDocumentRow> Documents)
{
    public bool HasIndexes => Indexes.Count > 0;
}

public sealed partial class CaptureDesignerNode : ObservableObject
{
    public CaptureDesignerNode(string label, CaptureDesignerSection section, DocumentTypeDefinition? documentType = null)
    {
        _label = label;
        Section = section;
        DocumentType = documentType;
    }

    [ObservableProperty] private string _label;
    public CaptureDesignerSection Section { get; }
    public DocumentTypeDefinition? DocumentType { get; }
}

public partial class CaptureProfileDesignerViewModel : ViewModelBase
{
    private static readonly SeparationStrategyType[] RuleTypes = Enum.GetValues<SeparationStrategyType>();
    private readonly ICaptureProfileStore _store;
    private readonly CaptureWorkflowService? _workflow;
    private readonly IFileDialogService? _dialogs;
    private readonly IAppPaths? _paths;
    private readonly IPdfRasterizer? _pdfs;
    private readonly IImagePageImporter? _images;
    private readonly ILatticeBuilder? _latticeBuilder;
    private readonly IBarcodeDecoder? _barcodes;
    private readonly IBlankPageDetector? _blanks;
    private readonly IProfileApplicator? _applicator;
    private readonly IScriptEditorDialogService? _scriptEditor;
    private readonly object? _scriptEditorOwner;
    private readonly IFieldScriptRunner? _scriptRunner;
    private readonly IRedactionEntitySetStore? _redactionEntitySets;
    private readonly IPiiDetector? _piiDetector;
    private readonly IThereforeCategoryPickerDialogService? _thereforeCategoryPicker;
    private readonly IAiExtractor? _ai;
    private readonly IHelpWindowService? _help;
    private bool _changingRedactionSet;
    private string _savedSnapshot = string.Empty;
    private IReadOnlyList<RasterPage> _samplePages = [];
    private PageLattice? _sampleLattice;
    private int _sampleLoadVersion;
    private bool _changingEditorSelection;

    public CaptureProfileDesignerViewModel(CaptureProfile profile, ICaptureProfileStore store,
        CaptureWorkflowService? workflow = null, IFileDialogService? dialogs = null,
        IAppPaths? paths = null,
        IPdfRasterizer? pdfs = null, IImagePageImporter? images = null,
        ILatticeBuilder? latticeBuilder = null, IBarcodeDecoder? barcodes = null,
        IBlankPageDetector? blanks = null, IProfileApplicator? applicator = null,
        IScriptEditorDialogService? scriptEditor = null, object? scriptEditorOwner = null,
        IFieldScriptRunner? scriptRunner = null,
        IRedactionEntitySetStore? redactionEntitySets = null,
        IPiiDetector? piiDetector = null,
        IThereforeCategoryPickerDialogService? thereforeCategoryPicker = null,
        IAiExtractor? ai = null,
        IHelpWindowService? help = null)
    {
        Profile = profile;
        _store = store;
        _workflow = workflow;
        _dialogs = dialogs;
        _paths = paths;
        _pdfs = pdfs;
        _images = images;
        _latticeBuilder = latticeBuilder;
        _barcodes = barcodes;
        _blanks = blanks;
        _applicator = applicator;
        _scriptEditor = scriptEditor;
        _scriptEditorOwner = scriptEditorOwner;
        _scriptRunner = scriptRunner;
        _redactionEntitySets = redactionEntitySets;
        _piiDetector = piiDetector;
        _thereforeCategoryPicker = thereforeCategoryPicker;
        _ai = ai;
        _help = help;
        foreach (var set in BuiltInRedactionSets.All) RedactionSets.Add(set);
        BatchRules = Editor(profile.Batch.StartRules, "Starts a new batch", allowNone: true);
        BatchFields = CreateFieldEditor(
            profile.Batch.Fields,
            BatchRules.Strategies,
            "Batch-start rule",
            isBatchScope: true);
        BatchRules.SelectionChanged = () => SelectRuleForDrawing(SampleZoneTarget.BatchRule, BatchRules);
        BatchRules.StrategyChanged = OnRuleChanged;
        BatchFields.SelectionChanged = () => SelectFieldForDrawing(BatchFields);
        BatchFields.Fields.CollectionChanged += (_, _) =>
        {
            foreach (var export in DocumentExports)
                export.RefreshBatchFieldOptions(BatchFields.Fields);
        };
        BatchScripts = CreateScriptEditor(profile.Batch.Scripts, profile.Batch.SharedScriptSource,
            "Batch", BatchFields, ScriptScopeKind.Batch);
        RefreshNavigation();
        _selectedNode = Navigation[0];
        RefreshEnablementIssues();
        _savedSnapshot = JsonSerializer.Serialize(Profile);
    }

    /// <summary>True if the profile's working state (after flushing the currently edited section back
    /// into <see cref="Profile"/>) differs from what was last saved — checked fresh each time rather than
    /// cached, since edits happen through many different editor sub-view-models rather than one central
    /// mutation point.</summary>
    public bool HasUnsavedChanges()
    {
        Flush();
        return JsonSerializer.Serialize(Profile) != _savedSnapshot;
    }

    [ObservableProperty] private bool _isDirty;

    private void RefreshDirtyState() => IsDirty = HasUnsavedChanges();

    public CaptureProfile Profile { get; }
    public ObservableCollection<CaptureDesignerNode> Navigation { get; } = [];
    public RuleSetEditorViewModel BatchRules { get; }
    public FieldCollectionEditorViewModel BatchFields { get; }
    public ScriptCollectionEditorViewModel BatchScripts { get; }
    public IReadOnlyList<PageDispositionChoice> PageDispositionChoices { get; } =
    [
        new(PageDisposition.IncludeInNewDocument, "Keep the matching page"),
        new(PageDisposition.Consume, "Remove the matching page")
    ];

    public RuleSetEditorViewModel? RecognitionRules { get; private set; }
    public RuleSetEditorViewModel? DocumentStartRules { get; private set; }
    public FieldCollectionEditorViewModel? DocumentFields { get; private set; }
    public ScriptCollectionEditorViewModel? DocumentScripts { get; private set; }
    public ObservableCollection<ExportDefinitionRow> DocumentExports { get; } = [];
    public ObservableCollection<RedactionEntitySet> RedactionSets { get; } = [];
    public ObservableCollection<IndexHighlight> SampleHighlights { get; } = [];
    [ObservableProperty] private CaptureDesignerNode? _selectedNode;
    [ObservableProperty] private string _validationSummary = string.Empty;
    [ObservableProperty] private string _saveConfirmation = string.Empty;
    [ObservableProperty] private string _testCaptureSummary = "Choose representative files to preview this capture profile. Nothing will be imported.";
    [ObservableProperty] private IReadOnlyList<TestCaptureBatchRow> _testCaptureResults = [];
    [ObservableProperty] private Bitmap? _sampleImage;
    [ObservableProperty] private IReadOnlyList<LatticeWord> _sampleWords = [];
    [ObservableProperty] private int _samplePageNumber = 1;
    [ObservableProperty] private int _samplePageCount;
    [ObservableProperty] private string _sampleStatus = "Choose a representative PDF or image.";
    [ObservableProperty] private bool _showSampleOcr;
    [ObservableProperty] private SampleZoneTarget _selectedSampleZoneTarget = SampleZoneTarget.BatchRule;
    [ObservableProperty] private RedactionEntitySet? _selectedRedactionSet;

    public string PresidioStatus => _piiDetector?.IsConfigured == true
        ? "Presidio is available and will start automatically when PII detection runs."
        : "Presidio is not available in this build. Sensitive fields will still be redacted.";
    public bool HasNoDocumentExports => DocumentExports.Count == 0;
    public bool HasTestCaptureResults => TestCaptureResults.Count > 0;
    public IReadOnlyList<string> EnablementIssues { get; private set; } = [];
    public bool HasEnablementIssues => EnablementIssues.Count > 0;
    public string ProfileName
    {
        get => Profile.Name;
        set
        {
            if (Profile.Name == value) return;
            Profile.Name = value;
            OnPropertyChanged();
            RefreshEnablementIssues();
        }
    }
    public bool ProfileEnabled
    {
        get => Profile.Enabled;
        set
        {
            if (Profile.Enabled == value) return;
            Profile.Enabled = value;
            OnPropertyChanged();
            SaveConfirmation = string.Empty;
            RefreshEnablementIssues();
        }
    }
    public IReadOnlyList<DocumentTypeDefinition> FallbackDocumentTypes => Profile.DocumentTypes.ToList();
    public Guid? FallbackDocumentTypeId
    {
        get => Profile.DefaultDocumentTypeId;
        set
        {
            if (Profile.DefaultDocumentTypeId == value) return;
            Profile.DefaultDocumentTypeId = value;
            OnPropertyChanged();
            RefreshEnablementIssues();
        }
    }
    public string DocumentTypeName
    {
        get => SelectedDocumentType?.Name ?? string.Empty;
        set
        {
            if (SelectedDocumentType is not { } type || type.Name == value) return;
            type.Name = value;
            if (SelectedNode is not null) SelectedNode.Label = $"  {value}";
            OnPropertyChanged();
            OnPropertyChanged(nameof(FallbackDocumentTypes));
            RefreshEnablementIssues();
        }
    }

    public async Task InitializeAsync()
    {
        if (_redactionEntitySets is not null)
        {
            foreach (var set in await _redactionEntitySets.GetAllAsync())
                if (RedactionSets.All(existing => existing.Id != set.Id))
                    RedactionSets.Add(set);
        }

        RefreshSelectedRedactionSet();
    }

    public bool HasSample => SampleImage is not null;
    public bool CanChooseSample => IsBatchSelected || IsDocumentSelected;
    public bool CanPreviousSamplePage => SamplePageNumber > 1;
    public bool CanNextSamplePage => SamplePageNumber < SamplePageCount;
    public string SamplePageDisplay => SamplePageCount == 0 ? "No sample" : $"Page {SamplePageNumber} of {SamplePageCount}";
    public bool IsRuleDrawingTarget => SelectedSampleZoneTarget is SampleZoneTarget.BatchRule or SampleZoneTarget.RecognitionRule or SampleZoneTarget.DocumentStartRule;
    public bool IsFieldDrawingTarget => !IsRuleDrawingTarget;
    public bool CanEditSampleArea => DrawingTargetSupportsArea();
    public bool CanClearSampleArea => CurrentSampleArea() is not null;
    public bool CanDrawOnSample => HasSample && CanEditSampleArea;
    public bool CanTestSelectedRule => HasSample && IsRuleDrawingTarget && CurrentSelectedRule() is not null;
    public bool CanTestSelectedField => HasSample && IsFieldDrawingTarget && CurrentFields()?.SelectedField is not null;
    public string ClearAreaLabel => SelectedSampleZoneTarget switch
    {
        SampleZoneTarget.FieldSearchArea => "Clear search area",
        SampleZoneTarget.FieldZone => "Clear field zone",
        _ => "Clear rule area"
    };
    public string DrawingTargetLabel => SelectedSampleZoneTarget switch
    {
        SampleZoneTarget.FieldKeyPattern when CurrentFields()?.SelectedField is { } field => $"Pick key text for {field.Name}",
        SampleZoneTarget.FieldValuePattern when CurrentFields()?.SelectedField is { } field => $"Pick value text for {field.Name}",
        _ when IsRuleDrawingTarget => CurrentSelectedRule() is { } rule
            ? RuleUsesArea(rule) ? rule.DisplayLabel : $"{rule.DisplayLabel} (whole page)"
            : "No rule selected",
        _ => CurrentFields()?.SelectedField is { } field
            ? DrawingTargetSupportsArea() ? field.Name : $"{field.Name} (no area)"
            : "No field selected"
    };
    public string DrawingHint
    {
        get
        {
            if (IsRuleDrawingTarget)
            {
                var rule = CurrentSelectedRule();
                if (rule is null) return "Select a rule on the right to draw its area.";
                if (!RuleUsesArea(rule)) return $"“{rule.DisplayLabel}” checks the whole page; no area is needed.";
                return $"Draw or adjust the area used by “{rule.DisplayLabel}”.";
            }

            var field = CurrentFields()?.SelectedField;
            if (field is null) return "Select a field on the right to draw its area.";
            if (!DrawingTargetSupportsArea()) return $"“{field.Name}” does not use this type of area.";
            return SelectedSampleZoneTarget switch
            {
                SampleZoneTarget.FieldSearchArea => $"Limit where “{field.Name}” searches for text.",
                SampleZoneTarget.FieldKeyPattern => "Draw around representative key text; a tolerant regular expression will be suggested.",
                SampleZoneTarget.FieldValuePattern => "Draw around a representative value; a regular expression will be suggested.",
                _ => $"Draw the value area for “{field.Name}”."
            };
        }
    }

    public bool IsOverviewSelected => SelectedNode?.Section == CaptureDesignerSection.Overview;
    public bool IsBatchSelected => SelectedNode?.Section == CaptureDesignerSection.Batch;
    public bool IsDocumentSelected => SelectedNode?.Section == CaptureDesignerSection.DocumentType;
    public bool IsEditorSelected => IsBatchSelected || IsDocumentSelected;
    public bool IsTestSelected => SelectedNode?.Section == CaptureDesignerSection.TestCapture;
    public DocumentTypeDefinition? SelectedDocumentType => SelectedNode?.DocumentType;

    partial void OnSelectedNodeChanging(CaptureDesignerNode? value) => FlushSelectedDocument();
    partial void OnSelectedNodeChanged(CaptureDesignerNode? value)
    {
        if (value?.DocumentType is { } type) LoadDocument(type);
        RefreshEnablementIssues();
        OnPropertyChanged(nameof(IsOverviewSelected));
        OnPropertyChanged(nameof(IsBatchSelected));
        OnPropertyChanged(nameof(IsDocumentSelected));
        OnPropertyChanged(nameof(IsEditorSelected));
        OnPropertyChanged(nameof(IsTestSelected));
        OnPropertyChanged(nameof(SelectedDocumentType));
        OnPropertyChanged(nameof(DocumentTypeName));
        OnPropertyChanged(nameof(CanChooseSample));
        SelectedSampleZoneTarget = value?.Section == CaptureDesignerSection.Batch
            ? SampleZoneTarget.BatchRule
            : SampleZoneTarget.RecognitionRule;
        _ = LoadCurrentSampleAsync();
    }

    partial void OnSelectedSampleZoneTargetChanged(SampleZoneTarget value) => RefreshSampleToolState();

    partial void OnSelectedRedactionSetChanged(RedactionEntitySet? value)
    {
        if (_changingRedactionSet || SelectedDocumentType is not { } type || value is null) return;
        type.Redaction.EntitySetId = value.Id;
        type.Redaction.Entities = value.Entities.ToList();
        RefreshDirtyState();
    }

    [RelayCommand]
    private async Task ChooseSampleAsync()
    {
        if (_dialogs is null || _paths is null || !CanChooseSample) return;
        var selected = await _dialogs.PickFilesAsync();
        if (selected.Count == 0) return;
        var source = selected.FirstOrDefault(ImportFormats.IsSupported);
        if (source is null)
        {
            SampleStatus = $"'{Path.GetFileName(selected[0])}' isn't a supported sample format — choose a PDF or image file instead.";
            return;
        }

        var scope = CurrentSampleScope();
        if (scope is null) return;
        var directory = Path.Combine(_paths.CaptureProfileDirectory(Profile.Id), "samples", scope.Value.Key);
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(source);
        var stored = Path.Combine(directory, "sample" + (string.IsNullOrWhiteSpace(extension) ? ".bin" : extension));
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(stored), StringComparison.Ordinal))
            File.Copy(source, stored, overwrite: true);
        scope.Value.SetPath(stored);
        await LoadSampleAsync(stored, directory);
    }

    [RelayCommand]
    private async Task PreviousSamplePageAsync()
    {
        if (!CanPreviousSamplePage) return;
        SamplePageNumber--;
        await LoadSamplePageAsync();
    }

    [RelayCommand]
    private async Task NextSamplePageAsync()
    {
        if (!CanNextSamplePage) return;
        SamplePageNumber++;
        await LoadSamplePageAsync();
    }

    [RelayCommand]
    private void SetSampleZone(NormalizedRect rect)
    {
        var zone = new ZoneRect { PageNumber = SamplePageNumber, X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
        string? detectedStatus = null;
        switch (SelectedSampleZoneTarget)
        {
            case SampleZoneTarget.BatchRule when BatchRules.SelectedStrategy is { } rule:
                rule.SetZone(zone); rule.ZonePageNumber = SamplePageNumber; detectedStatus = ConfigureRuleFromSample(rule, zone); break;
            case SampleZoneTarget.RecognitionRule when RecognitionRules?.SelectedStrategy is { } rule:
                rule.SetZone(zone); rule.ZonePageNumber = SamplePageNumber; detectedStatus = ConfigureRuleFromSample(rule, zone); break;
            case SampleZoneTarget.DocumentStartRule when DocumentStartRules?.SelectedStrategy is { } rule:
                rule.SetZone(zone); rule.ZonePageNumber = SamplePageNumber; detectedStatus = ConfigureRuleFromSample(rule, zone); break;
            case SampleZoneTarget.FieldZone when CurrentFields()?.SelectedField is { } field:
                field.SetZone(zone); field.PageNumber = SamplePageNumber; detectedStatus = ConfigureFieldFromSample(field, zone); RefreshFieldSample(field); break;
            case SampleZoneTarget.FieldSearchArea when CurrentFields()?.SelectedField is { } field:
                field.Field.SearchZone = zone; field.NotifySearchZone(); RefreshFieldSample(field); break;
            case SampleZoneTarget.FieldKeyPattern when CurrentFields()?.SelectedField is { IsKeyValue: true } field:
                detectedStatus = SuggestFieldPattern(field, zone, key: true); break;
            case SampleZoneTarget.FieldValuePattern when CurrentFields()?.SelectedField is { IsPatternField: true } field:
                detectedStatus = SuggestFieldPattern(field, zone, key: false); break;
            default:
                SampleStatus = "Select a rule or field for the chosen drawing target first."; return;
        }
        SampleStatus = detectedStatus ?? "Area updated. Use the handles to move or resize it.";
        RefreshSampleHighlights();
        OnPropertyChanged(nameof(CanClearSampleArea));
    }

    [RelayCommand]
    private void SuggestFieldKeyFromSample()
    {
        if (CurrentFields()?.SelectedField is not { IsKeyValue: true }) return;
        SelectedSampleZoneTarget = SampleZoneTarget.FieldKeyPattern;
        SampleStatus = "Draw around the key text on the sample.";
    }

    [RelayCommand]
    private void SuggestFieldValueFromSample()
    {
        if (CurrentFields()?.SelectedField is not { IsPatternField: true }) return;
        SelectedSampleZoneTarget = SampleZoneTarget.FieldValuePattern;
        SampleStatus = "Draw around a representative value on the sample.";
    }

    [RelayCommand]
    private void ClearSampleZone()
    {
        var cleared = false;
        switch (SelectedSampleZoneTarget)
        {
            case SampleZoneTarget.BatchRule when BatchRules.SelectedStrategy is { Zone: not null } rule: rule.SetZone(null); rule.TestResult = null; cleared = true; break;
            case SampleZoneTarget.RecognitionRule when RecognitionRules?.SelectedStrategy is { Zone: not null } rule: rule.SetZone(null); rule.TestResult = null; cleared = true; break;
            case SampleZoneTarget.DocumentStartRule when DocumentStartRules?.SelectedStrategy is { Zone: not null } rule: rule.SetZone(null); rule.TestResult = null; cleared = true; break;
            case SampleZoneTarget.FieldZone when CurrentFields()?.SelectedField is { Field.Zone: not null } field: field.SetZone(null); RefreshFieldSample(field); cleared = true; break;
            case SampleZoneTarget.FieldSearchArea when CurrentFields()?.SelectedField is { Field.SearchZone: not null } field: field.Field.SearchZone = null; field.NotifySearchZone(); RefreshFieldSample(field); cleared = true; break;
        }
        if (cleared)
            SampleStatus = SelectedSampleZoneTarget switch
            {
                SampleZoneTarget.FieldSearchArea => "Search area cleared.",
                SampleZoneTarget.FieldZone => "Field zone cleared.",
                _ => "Rule area cleared."
            };
        RefreshSampleHighlights();
        OnPropertyChanged(nameof(CanClearSampleArea));
    }

    [RelayCommand]
    private void TestSelectedRule()
    {
        var rule = CurrentSelectedRule();
        var page = _samplePages.FirstOrDefault(item => item.PageNumber == SamplePageNumber);
        if (rule is null || page is null || _sampleLattice is null) { SampleStatus = "Select a rule and load a sample first."; return; }
        try
        {
            var (matched, detail) = EvaluateRule(rule, page);
            rule.TestResult = $"{(matched ? "MATCH" : "NO MATCH")} — {detail}";
            SampleStatus = rule.TestResult;
        }
        catch (Exception ex) { rule.TestResult = $"Invalid rule: {ex.Message}"; SampleStatus = rule.TestResult; }
    }

    [RelayCommand]
    private void TestAllRules()
    {
        var editor = CurrentRuleEditor();
        var page = _samplePages.FirstOrDefault(item => item.PageNumber == SamplePageNumber);
        if (editor is null || editor.Strategies.Count == 0 || page is null || _sampleLattice is null)
        {
            SampleStatus = "Choose a rule set with strategies and load a sample first.";
            return;
        }

        var matches = 0;
        foreach (var rule in editor.Strategies)
        {
            try
            {
                var (matched, detail) = EvaluateRule(rule, page);
                if (matched) matches++;
                rule.TestResult = $"{(matched ? "MATCH" : "NO MATCH")} — {detail}";
            }
            catch (Exception ex)
            {
                rule.TestResult = $"INVALID — {ex.Message}";
            }
        }

        var overall = editor.MatchMode switch
        {
            SeparationMatchMode.All => matches == editor.Strategies.Count,
            SeparationMatchMode.AtLeast => matches >= Math.Max(1, editor.MatchMinimum),
            SeparationMatchMode.None => false,
            _ => matches > 0
        };
        SampleStatus = $"{matches} of {editor.Strategies.Count} rules matched — overall {(overall ? "MATCH" : "NO MATCH")}.";
    }

    private (bool Matched, string Detail) EvaluateRule(SeparationStrategyRow rule, RasterPage page) => rule.Type switch
    {
        SeparationStrategyType.EveryNPages => (SamplePageNumber % Math.Max(1, rule.PageCount) == 0, $"page {SamplePageNumber} of every {Math.Max(1, rule.PageCount)}"),
        SeparationStrategyType.Regex => MatchText(string.Join(" ", _sampleLattice!.Words.Select(word => word.Text)), rule.TextPattern),
        SeparationStrategyType.OcrZone => MatchText(rule.Zone is null ? string.Empty : ZonalExtractor.Extract(_sampleLattice!, rule.Zone).Text, rule.TextPattern),
        SeparationStrategyType.Barcode => MatchBarcode(page.ImagePath, rule),
        SeparationStrategyType.BlankPage => (_blanks?.IsBlank(page.ImagePath, rule.BlankInkPercent) == true, $"blank threshold {rule.BlankInkPercent}%"),
        _ => (false, "unsupported rule type")
    };

    [RelayCommand]
    private void TestSelectedField()
    {
        var editor = CurrentFields();
        var field = editor?.SelectedField;
        var page = _samplePages.FirstOrDefault(item => item.PageNumber == SamplePageNumber);
        if (field is null || page is null || _sampleLattice is null || _applicator is null) { SampleStatus = "Select a field and load a sample first."; return; }
        var sampleType = new DocumentTypeDefinition { Name = "Sample", Fields = editor!.Fields.Select(row => row.Field).ToList() };
        var ambientFields = IsBatchSelected
            ? new Dictionary<string, string>()
            : BatchFields.Fields.ToDictionary(row => row.Name, row => row.LiveValue, StringComparer.OrdinalIgnoreCase);
        var values = _applicator.Apply(sampleType, [_sampleLattice],
            context: new DefaultValueContext
            {
                DocumentNumber = 1,
                BatchNumber = 1,
                PageNumber = page.PageNumber,
                PageCount = SamplePageCount,
                Fields = ambientFields
            }, pages:
        [new DocumentPage { DocumentId = Guid.Empty, PageNumber = page.PageNumber, SourcePageNumber = page.PageNumber, ImagePath = page.ImagePath, Width = page.Width, Height = page.Height, Dpi = page.Dpi }]);
        var value = values.Single(item => item.FieldId == field.Id);
        field.LiveValue = value.Value;
        field.LiveConfidence = value.Confidence;
        field.LiveFormat = value.ValidationError ?? string.Empty;
        field.MatchBounds = value.Bounds;
        SampleStatus = string.IsNullOrEmpty(value.Value) ? "No value extracted on this sample page." : $"Extracted “{value.Value}” ({value.Confidence:0}%).";
        RefreshSampleHighlights();
    }

    [RelayCommand]
    private async Task ExtractAiSampleAsync()
    {
        var fields = CurrentFields()?.Fields.Where(field => field.IsAi).ToList() ?? [];
        if (fields.Count == 0) { SampleStatus = "Add or select an AI field first."; return; }
        if (_ai is null || !_ai.IsConfigured) { SampleStatus = "Configure an AI provider in Settings first."; return; }
        if (_samplePages.Count == 0 || _latticeBuilder is null) { SampleStatus = "Choose a sample first."; return; }

        try
        {
            SampleStatus = fields.Count == 1 ? "Extracting AI field…" : $"Extracting {fields.Count} AI fields…";
            var lattices = await BuildAllSampleLatticesAsync();
            var text = DocumentText.FromLattices(lattices);
            if (string.IsNullOrWhiteSpace(text)) { SampleStatus = "The sample contains no extracted text."; return; }
            var extracted = await _ai.ExtractAsync(text, fields.Select(field => field.Field).ToList());
            foreach (var field in fields)
                if (extracted.TryGetValue(field.Id, out var value))
                {
                    field.LiveValue = value.Value;
                    field.LiveConfidence = value.Confidence;
                }
            SampleStatus = extracted.Count == 0
                ? "AI returned no values."
                : $"AI extracted {extracted.Count} field{(extracted.Count == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex)
        {
            SampleStatus = $"AI extraction failed: {ex.Message}";
        }
    }

    private async Task<IReadOnlyList<PageLattice>> BuildAllSampleLatticesAsync()
    {
        var scope = CurrentSampleScope();
        var document = new CaptureDocument
        {
            OriginalFileName = scope?.Path is { } path ? Path.GetFileName(path) : "sample",
            StoredPath = scope?.Path ?? string.Empty,
            Source = DocumentSource.Import,
            PageCount = _samplePages.Count
        };
        var lattices = new List<PageLattice>(_samplePages.Count);
        foreach (var raster in _samplePages)
        {
            if (raster.PageNumber == SamplePageNumber && _sampleLattice is not null)
            {
                lattices.Add(_sampleLattice);
                continue;
            }
            lattices.Add(await _latticeBuilder!.BuildPageAsync(document, new DocumentPage
            {
                DocumentId = document.Id,
                PageNumber = raster.PageNumber,
                SourcePageNumber = raster.PageNumber,
                ImagePath = raster.ImagePath,
                Width = raster.Width,
                Height = raster.Height,
                Dpi = raster.Dpi
            }));
        }
        return lattices.OrderBy(lattice => lattice.PageNumber).ToList();
    }

    [RelayCommand]
    private void AddDocumentType()
    {
        FlushSelectedDocument();
        var type = new DocumentTypeDefinition { Name = $"Document type {Profile.DocumentTypes.Count + 1}" };
        Profile.DocumentTypes.Add(type);
        Profile.DefaultDocumentTypeId ??= type.Id;
        RefreshNavigation(type.Id);
        RefreshDirtyState();
    }

    [RelayCommand]
    private void CopyDocumentType()
    {
        if (SelectedDocumentType is not { } selected) return;
        FlushSelectedDocument();
        var copy = JsonSerializer.Deserialize<DocumentTypeDefinition>(JsonSerializer.Serialize(selected))!;
        AssignNewIds(copy);
        copy.Name = UniqueDocumentTypeName(selected.Name);
        Profile.DocumentTypes.Add(copy);
        RefreshNavigation(copy.Id);
        RefreshDirtyState();
    }

    private string UniqueDocumentTypeName(string baseName)
    {
        var existing = new HashSet<string>(Profile.DocumentTypes.Select(type => type.Name), StringComparer.OrdinalIgnoreCase);
        var candidate = $"{baseName} copy";
        var suffix = 2;
        while (existing.Contains(candidate))
            candidate = $"{baseName} copy {suffix++}";
        return candidate;
    }

    [RelayCommand]
    private void RemoveDocumentType()
    {
        if (SelectedDocumentType is not { } selected) return;
        Profile.DocumentTypes.Remove(selected);
        if (Profile.DefaultDocumentTypeId == selected.Id) Profile.DefaultDocumentTypeId = null;
        RefreshNavigation();
        RefreshDirtyState();
    }

    [RelayCommand]
    private void MoveDocumentTypeUp() => MoveDocumentType(-1);

    [RelayCommand]
    private void MoveDocumentTypeDown() => MoveDocumentType(1);

    [RelayCommand]
    private async Task SaveAsync()
    {
        Flush();
        SaveConfirmation = string.Empty;
        RefreshEnablementIssues();
        var errors = EnablementIssues;
        ValidationSummary = Profile.Enabled ? string.Join(Environment.NewLine, errors) : string.Empty;

        // Never discard the user's edits: a profile that can't be enabled yet is still saved, just as a
        // disabled draft, rather than dropping everything typed since the last successful save.
        var forcedDisable = Profile.Enabled && errors.Count > 0;
        if (forcedDisable) Profile.Enabled = false;

        await _store.SaveAsync(Profile);
        _savedSnapshot = JsonSerializer.Serialize(Profile);
        OnPropertyChanged(nameof(ProfileEnabled));
        RefreshEnablementIssues();
        SaveConfirmation = forcedDisable
            ? $"Saved as a disabled draft — {errors.Count} setup item{(errors.Count == 1 ? string.Empty : "s")} must be fixed before it can be enabled."
            : Profile.Enabled
                ? "Capture profile saved and enabled."
                : errors.Count == 0
                    ? "Disabled profile saved. It is ready to enable when required."
                    : $"Disabled draft saved — {errors.Count} setup item{(errors.Count == 1 ? string.Empty : "s")} remain.";
    }

    [RelayCommand]
    private async Task RunTestCaptureAsync()
    {
        if (_workflow is null || _dialogs is null) return;
        Flush();
        RefreshEnablementIssues();
        var errors = EnablementIssues;
        if (errors.Count > 0) { ValidationSummary = string.Join(Environment.NewLine, errors); return; }
        var files = await _dialogs.PickFilesAsync();
        if (files.Count == 0) return;
        TestCaptureResults = [];
        OnPropertyChanged(nameof(HasTestCaptureResults));
        TestCaptureSummary = "Analysing files…";
        var preview = await _workflow.PreviewWithIndexesAsync(Profile, files, DocumentSource.Import);
        var plan = preview.Plan;
        var documentCount = plan.Batches.Sum(batch => batch.Documents.Count);
        var pageCount = plan.Batches.SelectMany(batch => batch.Documents).Sum(document => document.SourcePages.Count);
        TestCaptureSummary = $"Preview only — {documentCount} document{(documentCount == 1 ? string.Empty : "s")} in " +
            $"{plan.Batches.Count} batch{(plan.Batches.Count == 1 ? string.Empty : "es")}; " +
            $"{pageCount} pages kept; {plan.ConsumedPages.Count} removed; {plan.Diagnostics.Count} diagnostic{(plan.Diagnostics.Count == 1 ? string.Empty : "s")}. Nothing was imported.";
        TestCaptureResults = preview.Batches.Select((batch, batchIndex) =>
        {
            var plannedBatch = plan.Batches[batchIndex];
            return new TestCaptureBatchRow(
                $"Batch {batch.Number}",
                batch.IndexValues.Select(value => PreviewIndex(value, 0)).ToList(),
                batch.Documents.Select((document, documentIndex) =>
                {
                    var threshold = plannedBatch.Documents[documentIndex].Type?.AutoReadyThreshold ?? 0;
                    return new TestCaptureDocumentRow(
                        $"Document {document.Number} — {document.DocumentType}",
                        $"{document.SourceFiles} · {document.PageCount} page{(document.PageCount == 1 ? string.Empty : "s")}",
                        document.Status == DocumentStatus.Ready ? "Ready" : "Needs review",
                        document.IndexValues.Select(value => PreviewIndex(value, threshold)).ToList());
                }).ToList());
        }).ToList();
        OnPropertyChanged(nameof(HasTestCaptureResults));
    }

    private static TestCaptureIndexRow PreviewIndex(IndexValue value, int confidenceThreshold)
    {
        var issue = value.ValidationError
            ?? (value.IsMissing ? "Required value missing"
                : confidenceThreshold > 0 && value.IsLowConfidence(confidenceThreshold)
                    ? $"Below {confidenceThreshold}% ready threshold"
                    : string.Empty);
        var confidence = value.IsManual
            ? "Manual"
            : string.IsNullOrWhiteSpace(value.Value) && value.Confidence <= 0
                ? "No confidence"
                : $"{value.Confidence:0}% confidence";
        return new TestCaptureIndexRow(
            value.FieldName,
            string.IsNullOrWhiteSpace(value.Value) ? "No value" : value.Value,
            confidence,
            issue);
    }

    [RelayCommand]
    private void AddExport()
    {
        if (DocumentFields is null) return;
        DocumentExports.Add(new ExportDefinitionRow(
            new Capture.Core.Profiles.ExportDefinition(), DocumentFields.Fields, BatchFields.Fields) { IsExpanded = true });
        OnPropertyChanged(nameof(HasNoDocumentExports));
        RefreshEnablementIssues();
    }

    [RelayCommand]
    private void RemoveExport(ExportDefinitionRow? row)
    {
        if (row is null) return;
        DocumentExports.Remove(row);
        OnPropertyChanged(nameof(HasNoDocumentExports));
        RefreshEnablementIssues();
    }

    [RelayCommand]
    private void ToggleExportExpanded(ExportDefinitionRow? row)
    {
        if (row is not null) row.IsExpanded = !row.IsExpanded;
    }

    [RelayCommand]
    private async Task BrowseExportFolderAsync(ExportDefinitionRow? row)
    {
        if (row is null || _dialogs is null) return;
        var folder = await _dialogs.PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder)) row.OutputFolder = folder;
        RefreshEnablementIssues();
    }

    [RelayCommand]
    private async Task BrowseThereforeCategoryAsync(ExportDefinitionRow? row)
    {
        if (row is null || _thereforeCategoryPicker is null || _dialogs?.Host is not { } host) return;
        var selection = await _thereforeCategoryPicker.ShowAsync(host);
        if (selection is null) return;

        var existingMappings = row.Definition.ThereforeCategoryNo == selection.CategoryNo
            ? row.Definition.ThereforeFieldMappings
                .GroupBy(mapping => mapping.FieldNo)
                .ToDictionary(group => group.Key, group => group.First())
            : new Dictionary<int, ThereforeFieldMapping>();

        row.Definition.ThereforeCategoryNo = selection.CategoryNo;
        row.Definition.ThereforeCategoryName = selection.CategoryName;
        row.Definition.ThereforeFieldMappings = selection.Fields.Select(field =>
        {
            existingMappings.TryGetValue(field.FieldNo, out var existing);
            return new ThereforeFieldMapping
            {
                FieldNo = field.FieldNo,
                Caption = field.Caption,
                IndexDataFieldName = field.IndexDataFieldName,
                FieldType = (int)field.FieldType,
                Mandatory = field.Mandatory,
                ValueSource = existing?.ValueSource ?? ThereforeMappingValueSource.IndexField,
                IndexFieldId = existing?.IndexFieldId,
                ConstantValue = existing?.ConstantValue ?? string.Empty
            };
        }).ToList();
        row.RefreshThereforeMappings();
        RefreshEnablementIssues();
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Profile.Name)) errors.Add("Profile name is required.");
        if (Profile.DocumentTypes.Count == 0) errors.Add("Add at least one document type.");
        if (Profile.DocumentTypes.Any(type => string.IsNullOrWhiteSpace(type.Name))) errors.Add("Every document type needs a name.");
        if (Profile.DefaultDocumentTypeId is null) errors.Add("Choose a fallback document type.");
        else if (Profile.DocumentTypes.All(type => type.Id != Profile.DefaultDocumentTypeId)) errors.Add("The fallback document type no longer exists.");

        foreach (var duplicateGroup in Profile.DocumentTypes
                     .Select(type => type.Name)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            errors.Add($"Document type name '{duplicateGroup.Key}' is used more than once — rename one so the fallback list and exports aren't ambiguous.");

        var ambiguousStart = Profile.DocumentTypes
            .SelectMany(type => type.StartRules.Rules.Select(rule => (type.Name, rule.Type, Pattern: rule.TextPattern ?? rule.BarcodeValuePattern)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Pattern))
            .GroupBy(item => (item.Type, item.Pattern), item => item.Name)
            .FirstOrDefault(group => group.Select(name => name).Distinct().Count() > 1);
        if (ambiguousStart is not null) errors.Add($"Ambiguous first-page rule '{ambiguousStart.Key.Pattern}'.");

        // Recognition rules decide which document type a page belongs to — an overlap here is more
        // consequential than a duplicate start rule, since classification becomes first-match-wins.
        var ambiguousRecognition = Profile.DocumentTypes
            .SelectMany(type => type.RecognitionRules.Rules.Select(rule => (type.Name, rule.Type, Pattern: rule.TextPattern ?? rule.BarcodeValuePattern)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Pattern))
            .GroupBy(item => (item.Type, item.Pattern), item => item.Name)
            .FirstOrDefault(group => group.Select(name => name).Distinct().Count() > 1);
        if (ambiguousRecognition is not null) errors.Add($"Ambiguous recognition rule '{ambiguousRecognition.Key.Pattern}' — multiple document types would match the same page.");

        foreach (var duplicateGroup in Profile.Batch.Fields
                     .Select(field => field.Name)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            errors.Add($"Batch field name '{duplicateGroup.Key}' is used more than once.");

        foreach (var type in Profile.DocumentTypes)
            foreach (var duplicateGroup in type.Fields
                         .Select(field => field.Name)
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
                errors.Add($"Field name '{duplicateGroup.Key}' is used more than once in document type '{type.Name}'.");

        foreach (var type in Profile.DocumentTypes)
            foreach (var export in type.Exports.Where(export => export.Enabled))
            {
                var exportLabel = $"export '{export.Name}' on document type '{type.Name}'";
                switch (export.Type)
                {
                    case ExportType.None:
                        errors.Add($"Choose an export type for {exportLabel}.");
                        break;
                    case ExportType.Csv:
                        if (string.IsNullOrWhiteSpace(export.OutputFolder))
                            errors.Add($"Choose an output folder for {exportLabel}.");
                        break;
                    case ExportType.Therefore:
                        if (export.ThereforeCategoryNo is null)
                            errors.Add($"Choose a Therefore category for {exportLabel}.");
                        break;
                }
            }

        return errors;
    }

    private void RefreshEnablementIssues()
    {
        FlushSelectedDocument();
        EnablementIssues = Validate();
        OnPropertyChanged(nameof(EnablementIssues));
        OnPropertyChanged(nameof(HasEnablementIssues));
        RefreshDirtyState();
    }

    private void Flush()
    {
        FlushSelectedDocument();
        Profile.Batch.StartRules = ToRuleSet(BatchRules);
        Profile.Batch.Fields = BatchFields.ToModels();
        Profile.Batch.Scripts = BatchScripts.ToModels();
        Profile.Batch.SharedScriptSource = BatchScripts.SharedSource;
    }

    private void FlushSelectedDocument()
    {
        if (SelectedNode?.DocumentType is not { } type || RecognitionRules is null || DocumentStartRules is null || DocumentFields is null || DocumentScripts is null) return;
        type.RecognitionRules = ToRuleSet(RecognitionRules);
        type.StartRules = ToRuleSet(DocumentStartRules);
        type.Fields = DocumentFields.ToModels();
        type.Scripts = DocumentScripts.ToModels();
        type.SharedScriptSource = DocumentScripts.SharedSource;
        type.Exports = DocumentExports.Select(row => row.Definition).ToList();
    }

    private void LoadDocument(DocumentTypeDefinition type)
    {
        RecognitionRules = Editor(type.RecognitionRules, "Identify document type");
        DocumentStartRules = Editor(type.StartRules, "Detect first page of a new document");
        DocumentFields = CreateFieldEditor(
            type.Fields,
            DocumentStartRules.Strategies,
            "First-page rule",
            BatchFields.Fields);
        RecognitionRules.SelectionChanged = () => SelectRuleForDrawing(SampleZoneTarget.RecognitionRule, RecognitionRules);
        DocumentStartRules.SelectionChanged = () => SelectRuleForDrawing(SampleZoneTarget.DocumentStartRule, DocumentStartRules);
        RecognitionRules.StrategyChanged = OnRuleChanged;
        DocumentStartRules.StrategyChanged = OnRuleChanged;
        DocumentFields.SelectionChanged = () => SelectFieldForDrawing(DocumentFields);
        DocumentFields.Fields.CollectionChanged += (_, _) =>
        {
            foreach (var export in DocumentExports)
                export.RefreshFieldOptions(DocumentFields.Fields);
        };
        DocumentScripts = CreateScriptEditor(type.Scripts, type.SharedScriptSource,
            type.Name, DocumentFields, ScriptScopeKind.Document);
        RefreshSelectedRedactionSet();
        DocumentExports.Clear();
        foreach (var export in type.Exports)
            DocumentExports.Add(new ExportDefinitionRow(export, DocumentFields.Fields, BatchFields.Fields));
        OnPropertyChanged(nameof(HasNoDocumentExports));
        OnPropertyChanged(nameof(RecognitionRules));
        OnPropertyChanged(nameof(DocumentStartRules));
        OnPropertyChanged(nameof(DocumentFields));
        OnPropertyChanged(nameof(DocumentScripts));
        OnPropertyChanged(nameof(DocumentExports));
    }

    private void MoveDocumentType(int offset)
    {
        if (SelectedDocumentType is not { } selected) return;
        FlushSelectedDocument();
        var oldIndex = Profile.DocumentTypes.IndexOf(selected);
        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= Profile.DocumentTypes.Count) return;
        Profile.DocumentTypes.RemoveAt(oldIndex);
        Profile.DocumentTypes.Insert(newIndex, selected);
        RefreshNavigation(selected.Id);
        RefreshDirtyState();
    }

    private void RefreshNavigation(Guid? selectType = null)
    {
        Navigation.Clear();
        Navigation.Add(new("Overview", CaptureDesignerSection.Overview));
        Navigation.Add(new("Batch", CaptureDesignerSection.Batch));
        foreach (var type in Profile.DocumentTypes) Navigation.Add(new($"  {type.Name}", CaptureDesignerSection.DocumentType, type));
        Navigation.Add(new("Test Capture", CaptureDesignerSection.TestCapture));
        OnPropertyChanged(nameof(FallbackDocumentTypes));
        OnPropertyChanged(nameof(FallbackDocumentTypeId));
        RefreshEnablementIssues();
        SelectedNode = selectType is { } id
            ? Navigation.First(item => item.DocumentType?.Id == id)
            : Navigation[0];
    }

    private (string Key, string? Path, Action<string> SetPath)? CurrentSampleScope()
    {
        if (IsBatchSelected)
            return ("batch", Profile.Batch.SampleFileName, path => Profile.Batch.SampleFileName = path);
        if (SelectedDocumentType is { } type)
            return (type.Id.ToString("N"), type.SampleFileName, path => type.SampleFileName = path);
        return null;
    }

    private async Task LoadCurrentSampleAsync()
    {
        // Invalidate an in-flight load even when the newly selected designer scope has no sample.
        _sampleLoadVersion++;
        var scope = CurrentSampleScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.Path) || !File.Exists(scope.Value.Path))
        {
            SampleImage?.Dispose();
            SampleImage = null;
            SampleWords = [];
            _samplePages = [];
            _sampleLattice = null;
            SamplePageCount = 0;
            SamplePageNumber = 1;
            SampleStatus = "Choose a representative PDF or image.";
            NotifySampleState();
            return;
        }

        var directory = Path.GetDirectoryName(scope.Value.Path)!;
        await LoadSampleAsync(scope.Value.Path, directory);
    }

    private async Task LoadSampleAsync(string sourcePath, string scopeDirectory)
    {
        if (_pdfs is null || _images is null || _latticeBuilder is null) return;
        var version = ++_sampleLoadVersion;
        try
        {
            SampleStatus = "Preparing sample preview…";
            var pagesDirectory = Path.Combine(scopeDirectory, "pages");
            Directory.CreateDirectory(pagesDirectory);
            var pages = ImportFormats.IsPdf(sourcePath)
                ? await _pdfs.RasterizeAsync(sourcePath, pagesDirectory, 200)
                : await _images.ImportAsync(sourcePath, pagesDirectory);
            if (version != _sampleLoadVersion) return;
            _samplePages = pages;
            SamplePageCount = pages.Count;
            SamplePageNumber = pages.Count == 0 ? 0 : 1;
            await LoadSamplePageAsync();
        }
        catch (Exception ex)
        {
            SampleStatus = $"Could not load sample: {ex.Message}";
            _samplePages = [];
            SamplePageCount = 0;
            NotifySampleState();
        }
    }

    private async Task LoadSamplePageAsync()
    {
        if (_latticeBuilder is null) return;
        var raster = _samplePages.FirstOrDefault(page => page.PageNumber == SamplePageNumber);
        if (raster is null) return;

        SampleImage?.Dispose();
        SampleImage = new Bitmap(raster.ImagePath);
        var scope = CurrentSampleScope();
        var document = new CaptureDocument
        {
            OriginalFileName = scope?.Path is { } path ? Path.GetFileName(path) : "sample",
            StoredPath = scope?.Path ?? raster.ImagePath,
            Source = DocumentSource.Import,
            PageCount = SamplePageCount
        };
        var page = new DocumentPage
        {
            DocumentId = document.Id,
            PageNumber = raster.PageNumber,
            SourcePageNumber = raster.PageNumber,
            ImagePath = raster.ImagePath,
            Width = raster.Width,
            Height = raster.Height,
            Dpi = raster.Dpi
        };
        _sampleLattice = await _latticeBuilder.BuildPageAsync(document, page);
        SampleWords = _sampleLattice.Words;
        SampleStatus = $"Sample ready — {SampleWords.Count} OCR/text words on page {SamplePageNumber}.";
        if (CurrentFields()?.SelectedField is { } field)
            RefreshFieldSample(field);
        RefreshSampleHighlights();
        NotifySampleState();
    }

    private void NotifySampleState()
    {
        OnPropertyChanged(nameof(HasSample));
        OnPropertyChanged(nameof(CanPreviousSamplePage));
        OnPropertyChanged(nameof(CanNextSamplePage));
        OnPropertyChanged(nameof(SamplePageDisplay));
        OnPropertyChanged(nameof(CanDrawOnSample));
        OnPropertyChanged(nameof(CanEditSampleArea));
        OnPropertyChanged(nameof(CanClearSampleArea));
        OnPropertyChanged(nameof(ClearAreaLabel));
        OnPropertyChanged(nameof(CanTestSelectedRule));
        OnPropertyChanged(nameof(CanTestSelectedField));
        OnPropertyChanged(nameof(DrawingHint));
        OnPropertyChanged(nameof(DrawingTargetLabel));
    }

    private void SelectRuleForDrawing(SampleZoneTarget target, RuleSetEditorViewModel editor)
    {
        if (_changingEditorSelection) return;
        _changingEditorSelection = true;
        try
        {
            if (editor.SelectedStrategy is not null)
            {
                SelectedSampleZoneTarget = target;
                CurrentFields()!.SelectedField = null;
                if (target != SampleZoneTarget.RecognitionRule && RecognitionRules is not null)
                    RecognitionRules.SelectedStrategy = null;
                if (target != SampleZoneTarget.DocumentStartRule && DocumentStartRules is not null)
                    DocumentStartRules.SelectedStrategy = null;
            }
        }
        finally { _changingEditorSelection = false; }
        RefreshSampleToolState();
    }

    private void SelectFieldForDrawing(FieldCollectionEditorViewModel editor)
    {
        if (_changingEditorSelection) return;
        _changingEditorSelection = true;
        try
        {
            if (editor.SelectedField is { } field)
            {
                SelectedSampleZoneTarget = field.IsPatternField ? SampleZoneTarget.FieldSearchArea : SampleZoneTarget.FieldZone;
                BatchRules.SelectedStrategy = null;
                if (RecognitionRules is not null) RecognitionRules.SelectedStrategy = null;
                if (DocumentStartRules is not null) DocumentStartRules.SelectedStrategy = null;
            }
        }
        finally { _changingEditorSelection = false; }
        RefreshSampleToolState();
        if (editor.SelectedField is { } selected)
            RefreshFieldSample(selected);
    }

    private void RefreshSampleToolState()
    {
        OnPropertyChanged(nameof(IsRuleDrawingTarget));
        OnPropertyChanged(nameof(IsFieldDrawingTarget));
        OnPropertyChanged(nameof(CanDrawOnSample));
        OnPropertyChanged(nameof(CanEditSampleArea));
        OnPropertyChanged(nameof(CanTestSelectedRule));
        OnPropertyChanged(nameof(CanTestSelectedField));
        OnPropertyChanged(nameof(DrawingHint));
        OnPropertyChanged(nameof(DrawingTargetLabel));
        RefreshSampleHighlights();
    }

    private bool DrawingTargetSupportsArea() => SelectedSampleZoneTarget switch
    {
        SampleZoneTarget.BatchRule => RuleUsesArea(BatchRules.SelectedStrategy),
        SampleZoneTarget.RecognitionRule => RuleUsesArea(RecognitionRules?.SelectedStrategy),
        SampleZoneTarget.DocumentStartRule => RuleUsesArea(DocumentStartRules?.SelectedStrategy),
        SampleZoneTarget.FieldZone => CurrentFields()?.SelectedField?.IsZoneField == true,
        SampleZoneTarget.FieldSearchArea => CurrentFields()?.SelectedField?.IsPatternField == true,
        SampleZoneTarget.FieldKeyPattern => CurrentFields()?.SelectedField?.IsKeyValue == true,
        SampleZoneTarget.FieldValuePattern => CurrentFields()?.SelectedField?.IsPatternField == true,
        _ => false
    };

    private static bool RuleUsesArea(SeparationStrategyRow? rule) =>
        rule?.Type is SeparationStrategyType.Barcode or SeparationStrategyType.OcrZone;

    private SeparationStrategyRow? CurrentSelectedRule() => SelectedSampleZoneTarget switch
    {
        SampleZoneTarget.BatchRule => BatchRules.SelectedStrategy,
        SampleZoneTarget.RecognitionRule => RecognitionRules?.SelectedStrategy,
        SampleZoneTarget.DocumentStartRule => DocumentStartRules?.SelectedStrategy,
        _ => null
    };

    private RuleSetEditorViewModel? CurrentRuleEditor() => SelectedSampleZoneTarget switch
    {
        SampleZoneTarget.BatchRule => BatchRules,
        SampleZoneTarget.RecognitionRule => RecognitionRules,
        SampleZoneTarget.DocumentStartRule => DocumentStartRules,
        _ when IsBatchSelected => BatchRules,
        _ => RecognitionRules
    };

    private FieldCollectionEditorViewModel? CurrentFields() => IsBatchSelected ? BatchFields : DocumentFields;

    private ZoneRect? CurrentSampleArea() => SelectedSampleZoneTarget switch
    {
        SampleZoneTarget.BatchRule => BatchRules.SelectedStrategy?.Zone,
        SampleZoneTarget.RecognitionRule => RecognitionRules?.SelectedStrategy?.Zone,
        SampleZoneTarget.DocumentStartRule => DocumentStartRules?.SelectedStrategy?.Zone,
        SampleZoneTarget.FieldZone => CurrentFields()?.SelectedField?.Field.Zone,
        SampleZoneTarget.FieldSearchArea => CurrentFields()?.SelectedField?.Field.SearchZone,
        _ => null
    };

    private string? ConfigureRuleFromSample(SeparationStrategyRow rule, ZoneRect zone)
    {
        if (rule.IsBarcode)
        {
            var page = _samplePages.FirstOrDefault(item => item.PageNumber == SamplePageNumber);
            var decoded = page is null ? null : _barcodes?.Decode(page.ImagePath, zone);
            if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
                return "Barcode area set, but no barcode was detected there.";

            rule.BarcodeFormat = decoded.Format;
            rule.BarcodeValuePattern = $"^{Regex.Escape(decoded.Text)}$";
            return $"Detected {BarcodePatterns.DisplayType(decoded.Format)}: {decoded.Text}";
        }

        if (rule.IsOcrZone && _sampleLattice is not null)
        {
            var text = ZonalExtractor.Extract(_sampleLattice, zone).Text.Trim();
            if (text.Length == 0)
                return "Text area set, but no OCR text was detected there.";
            rule.TextPattern = $"^{Regex.Escape(text)}$";
            return $"Detected text: {TrimSample(text)}";
        }

        return null;
    }

    private string? ConfigureFieldFromSample(FieldRow field, ZoneRect zone)
    {
        if (!field.IsBarcode) return null;
        var page = _samplePages.FirstOrDefault(item => item.PageNumber == SamplePageNumber);
        var decoded = page is null ? null : _barcodes?.Decode(page.ImagePath, zone);
        if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
            return "Barcode field area set, but no barcode was detected there.";

        field.BarcodeFormat = decoded.Format;
        field.ValuePattern = $"^{Regex.Escape(decoded.Text)}$";
        return $"Detected {BarcodePatterns.DisplayType(decoded.Format)}: {decoded.Text}";
    }

    private string SuggestFieldPattern(FieldRow field, ZoneRect zone, bool key)
    {
        if (_sampleLattice is null) return "No OCR text is available on this sample page.";
        var text = ZonalExtractor.Extract(_sampleLattice, zone).Text;
        if (string.IsNullOrWhiteSpace(text)) return "No OCR text was found in that selection.";

        if (key)
            field.KeyPattern = PatternSuggester.ForKey(text);
        else
            field.ValuePattern = PatternSuggester.ForValue(text, field.Format);
        return $"Suggested {(key ? "key" : "value")} pattern from “{TrimSample(text)}”.";
    }

    private static string TrimSample(string value)
    {
        value = value.Trim();
        return value.Length <= 40 ? value : value[..40] + "…";
    }

    private static (bool Matched, string Detail) MatchText(string text, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return (!string.IsNullOrWhiteSpace(text), $"zone text: {text}");
        return (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), $"text: {text}");
    }

    private (bool Matched, string Detail) MatchBarcode(string imagePath, SeparationStrategyRow rule)
    {
        var decoded = _barcodes?.Decode(imagePath, rule.Zone);
        if (decoded is null) return (false, "no barcode decoded");
        var formatMatches = string.IsNullOrWhiteSpace(rule.BarcodeFormat)
            || string.Equals(rule.BarcodeFormat, decoded.Format, StringComparison.OrdinalIgnoreCase);
        var valueMatches = string.IsNullOrWhiteSpace(rule.BarcodeValuePattern)
            || Regex.IsMatch(decoded.Text, rule.BarcodeValuePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return (formatMatches && valueMatches, $"{decoded.Format}: {decoded.Text}");
    }

    private void RefreshSampleHighlights()
    {
        SampleHighlights.Clear();
        if (SamplePageNumber <= 0) return;

        void Add(Guid id, string name, ZoneRect? zone, bool selected, bool search = false)
        {
            if (zone is null || zone.PageNumber != SamplePageNumber) return;
            SampleHighlights.Add(new IndexHighlight
            {
                FieldId = id, FieldName = name, X = zone.X, Y = zone.Y, Width = zone.Width, Height = zone.Height,
                IsSelected = selected, CanEdit = selected, IsSearchZone = search
            });
        }

        if (IsBatchSelected)
        {
            if (BatchRules.RulesEnabled)
                foreach (var rule in BatchRules.Strategies)
                    Add(rule.Id, rule.DisplayLabel, rule.Zone, SelectedSampleZoneTarget == SampleZoneTarget.BatchRule && rule == BatchRules.SelectedStrategy);
        }
        else
        {
            foreach (var rule in RecognitionRules?.Strategies ?? [])
                Add(rule.Id, rule.DisplayLabel, rule.Zone, SelectedSampleZoneTarget == SampleZoneTarget.RecognitionRule && rule == RecognitionRules?.SelectedStrategy);
            foreach (var rule in DocumentStartRules?.Strategies ?? [])
                Add(rule.Id, rule.DisplayLabel, rule.Zone, SelectedSampleZoneTarget == SampleZoneTarget.DocumentStartRule && rule == DocumentStartRules?.SelectedStrategy);
        }

        foreach (var field in CurrentFields()?.Fields ?? [])
        {
            Add(field.Id, field.Name, field.Field.Zone, SelectedSampleZoneTarget == SampleZoneTarget.FieldZone && field == CurrentFields()?.SelectedField);
            Add(field.Id, field.Name, field.Field.SearchZone, SelectedSampleZoneTarget == SampleZoneTarget.FieldSearchArea && field == CurrentFields()?.SelectedField, search: true);
            Add(field.Id, field.Name, field.MatchBounds, selected: false);
        }
    }

    private static RuleSetEditorViewModel Editor(RuleSet rules, string title, bool allowNone = false) =>
        new(rules.Rules, rules.MatchMode, rules.MatchMinimum, RuleTypes, title, allowNone: allowNone);

    private FieldCollectionEditorViewModel CreateFieldEditor(
        IEnumerable<IndexField> fields,
        IEnumerable<SeparationStrategyRow>? boundaryRules = null,
        string boundaryRuleLabel = "Trigger rule",
        IEnumerable<FieldRow>? additionalSourceFields = null,
        bool isBatchScope = false) => new(
        fields,
        _scriptEditor is null || _scriptEditorOwner is null
            ? null
            : (_, title, source) => _scriptEditor.EditAsync(_scriptEditorOwner, title, source),
        _scriptRunner is null ? null : TestFieldScriptAsync,
        OnFieldChanged,
        SuggestFieldKeyFromSample,
        SuggestFieldValueFromSample,
        ExtractAiSampleAsync,
        boundaryRules,
        boundaryRuleLabel,
        additionalSourceFields,
        isBatchScope,
        OpenScriptingHelp);

    private void OpenScriptingHelp()
    {
        if (_help is not null && _scriptEditorOwner is not null)
            _help.ShowScripting(_scriptEditorOwner);
    }

    private void OnFieldChanged(FieldRow field, string? propertyName)
    {
        RefreshDirtyState();
        if (field != CurrentFields()?.SelectedField) return;
        if (propertyName == nameof(FieldRow.BarcodeScanArea))
        {
            SampleStatus = field.BarcodeScanArea == BarcodeScanArea.EntirePage
                ? "This barcode field will scan the entire selected page."
                : field.Field.Zone is null
                    ? "Draw the barcode area on the sample preview."
                    : "This barcode field will use the selected area.";
            RefreshSampleToolState();
        }
        if (propertyName is nameof(FieldRow.Format)
            or nameof(FieldRow.KeyPattern)
            or nameof(FieldRow.ValuePattern)
            or nameof(FieldRow.Occurrence)
            or nameof(FieldRow.PageScope)
            or nameof(FieldRow.PageNumber)
            or nameof(FieldRow.BarcodeFormat))
            RefreshFieldSample(field);
    }

    private void OnRuleChanged(SeparationStrategyRow rule, string? propertyName)
    {
        RefreshDirtyState();
        if (rule != CurrentSelectedRule() || propertyName != nameof(SeparationStrategyRow.BarcodeScanArea))
            return;
        SampleStatus = rule.BarcodeScanArea == BarcodeScanArea.EntirePage
            ? "This barcode rule will scan the entire page."
            : rule.Zone is null
                ? "Draw the barcode area on the sample preview."
                : "This barcode rule will use the selected area.";
        RefreshSampleToolState();
    }

    private void RefreshFieldSample(FieldRow field)
    {
        if (_sampleLattice is null || _applicator is null || field != CurrentFields()?.SelectedField) return;
        TestSelectedField();
    }

    private async Task<string> TestFieldScriptAsync(FieldRow row, FieldScriptTestKind kind)
    {
        if (_scriptRunner is null) return "Script testing is unavailable.";
        var source = kind switch
        {
            FieldScriptTestKind.Button => row.ButtonScriptSource,
            FieldScriptTestKind.PostProcess => row.PostProcessScript,
            _ => row.ScriptExpression
        };
        if (string.IsNullOrWhiteSpace(source)) return "Add C# source before running the test.";

        var editor = CurrentFields();
        if (editor is null) return "Select a batch or document type first.";
        var values = editor.Fields.Select(field => new IndexValue
        {
            FieldId = field.Id,
            FieldName = field.Name,
            Format = field.Format,
            Kind = field.Field.Kind,
            Value = field.LiveValue,
            Confidence = field.LiveConfidence
        }).ToList();
        var batchValues = IsBatchSelected
            ? null
            : BatchFields.Fields.Select(field => new IndexValue
            {
                FieldId = field.Id,
                FieldName = field.Name,
                Format = field.Format,
                Kind = field.Field.Kind,
                Value = field.LiveValue,
                Confidence = field.LiveConfidence
            }).ToList();
        var lattices = _sampleLattice is null ? Array.Empty<PageLattice>() : new[] { _sampleLattice };
        var context = new ScriptExecutionContext
        {
            ProfileName = Profile.Name,
            DocumentNumber = 1,
            BatchNumber = 1,
            Timestamp = DateTimeOffset.Now,
            Values = values,
            Document = ScriptDocumentInfo.From(lattices, null),
            Scope = IsBatchSelected ? ScriptScopeKind.Batch : ScriptScopeKind.Document,
            DocumentType = IsBatchSelected ? null : SelectedDocumentType?.Name,
            BatchValues = batchValues
        };
        var shared = IsBatchSelected ? BatchScripts.SharedSource : DocumentScripts?.SharedSource ?? string.Empty;

        ScriptRunResult result;
        if (kind == FieldScriptTestKind.Button)
        {
            result = await _scriptRunner.RunProfileScriptAsync(new FieldScript
            {
                Id = row.Id,
                Name = row.Name,
                Source = source,
                TimeoutSeconds = row.ButtonTimeoutSeconds
            }, context, sharedSource: shared);
            if (result.Success)
                foreach (var value in values)
                {
                    var field = editor.Fields.FirstOrDefault(candidate => candidate.Id == value.FieldId);
                    if (field is null) continue;
                    field.LiveValue = value.Value;
                    field.LiveConfidence = value.Confidence;
                }
        }
        else
        {
            result = await _scriptRunner.RunFieldExpressionAsync(row.Id, source, context, sharedSource: shared);
            if (result.Success)
            {
                row.LiveValue = result.Value ?? string.Empty;
                if (result.Confidence is { } confidence)
                    row.LiveConfidence = confidence;
                else if (kind == FieldScriptTestKind.Expression)
                    row.LiveConfidence = 100;
            }
        }

        if (!result.Success)
            return $"Test failed: {result.ErrorMessage}";

        var elapsed = Math.Max(0, (int)Math.Round(result.Elapsed.TotalMilliseconds));
        if (kind == FieldScriptTestKind.Button)
            return $"Test passed ({elapsed} ms).";

        return string.IsNullOrEmpty(result.Value)
            ? $"Test ran successfully but returned an empty value ({elapsed} ms)."
            : $"Test passed ({elapsed} ms) — returned “{result.Value}”.";
    }

    private ScriptCollectionEditorViewModel CreateScriptEditor(
        IEnumerable<FieldScript> scripts,
        string sharedSource,
        string scopeName,
        FieldCollectionEditorViewModel fields,
        ScriptScopeKind scope) => new(
            scripts,
            sharedSource,
            editSource: _scriptEditor is null || _scriptEditorOwner is null
                ? null
                : row => _scriptEditor.EditAsync(
                    _scriptEditorOwner,
                    $"{scopeName} script — {row.Name}",
                    row.Source),
            editSharedSource: _scriptEditor is null || _scriptEditorOwner is null
                ? null
                : source => _scriptEditor.EditAsync(
                    _scriptEditorOwner,
                    $"{scopeName} shared functions",
                    source),
            testSource: _scriptRunner is null
                ? null
                : (row, shared) => TestScriptAsync(row, shared, scopeName, fields, scope),
            openScriptingHelp: OpenScriptingHelp);

    private async Task<string> TestScriptAsync(
        ScriptRow row,
        string sharedSource,
        string scopeName,
        FieldCollectionEditorViewModel fields,
        ScriptScopeKind scope)
    {
        if (_scriptRunner is null) return "Script testing is unavailable.";
        if (string.IsNullOrWhiteSpace(row.Source)) return "Add C# source before running the test.";

        var lattices = _sampleLattice is null ? Array.Empty<PageLattice>() : new[] { _sampleLattice };
        IReadOnlyList<IndexValue> values = _applicator?.Apply(
            new DocumentTypeDefinition { Name = scopeName, Fields = fields.ToModels() }, lattices)
            ?? fields.ToModels().Select(field => new IndexValue
            {
                FieldId = field.Id,
                FieldName = field.Name,
                Format = field.Format,
                Kind = field.Kind
            }).ToList();
        var before = values.ToDictionary(value => value.FieldId, value => value.Value);
        var context = new ScriptExecutionContext
        {
            ProfileName = Profile.Name,
            DocumentNumber = 1,
            BatchNumber = 1,
            Timestamp = DateTimeOffset.Now,
            Values = values,
            Document = ScriptDocumentInfo.From(lattices, null),
            Scope = scope,
            DocumentType = scope == ScriptScopeKind.Document ? scopeName : null
        };

        var result = await _scriptRunner.RunProfileScriptAsync(row.Script, context, sharedSource: sharedSource);
        if (!result.Success) return $"Test failed: {result.ErrorMessage}";

        var changes = values
            .Where(value => !string.Equals(before[value.FieldId], value.Value, StringComparison.Ordinal))
            .Select(value => $"{value.FieldName} = {value.Value}")
            .ToList();
        var elapsed = Math.Max(0, (int)Math.Round(result.Elapsed.TotalMilliseconds));
        return changes.Count == 0
            ? $"Test passed ({elapsed} ms). No field values changed."
            : $"Test passed ({elapsed} ms). {string.Join("; ", changes)}";
    }

    private void RefreshSelectedRedactionSet()
    {
        if (SelectedDocumentType is not { } type) return;
        var selected = type.Redaction.EntitySetId is { } id
            ? RedactionSets.FirstOrDefault(set => set.Id == id)
            : null;
        selected ??= RedactionSets.FirstOrDefault(set => set.Id == BuiltInRedactionSets.CoreId);

        _changingRedactionSet = true;
        SelectedRedactionSet = selected;
        _changingRedactionSet = false;

        if (selected is not null && type.Redaction.EntitySetId != selected.Id)
        {
            type.Redaction.EntitySetId = selected.Id;
            type.Redaction.Entities = selected.Entities.ToList();
        }
    }

    private static void AssignNewIds(DocumentTypeDefinition copy)
    {
        copy.Id = Guid.NewGuid();

        var ruleIds = copy.RecognitionRules.Rules
            .Concat(copy.StartRules.Rules)
            .ToDictionary(rule => rule.Id, _ => Guid.NewGuid());
        foreach (var rule in copy.RecognitionRules.Rules.Concat(copy.StartRules.Rules))
            rule.Id = ruleIds[rule.Id];

        var fieldIds = copy.Fields.ToDictionary(field => field.Id, _ => Guid.NewGuid());
        foreach (var field in copy.Fields)
        {
            field.Id = fieldIds[field.Id];
            if (field.BoundaryRuleId is { } boundaryId && ruleIds.TryGetValue(boundaryId, out var newBoundaryId))
                field.BoundaryRuleId = newBoundaryId;
        }

        foreach (var script in copy.Scripts)
            script.Id = Guid.NewGuid();

        foreach (var export in copy.Exports)
        {
            export.Id = Guid.NewGuid();
            export.FieldIds = export.FieldIds
                .Where(fieldIds.ContainsKey)
                .Select(fieldId => fieldIds[fieldId])
                .ToList();
            foreach (var mapping in export.ThereforeFieldMappings)
                if (mapping.IndexFieldId is { } fieldId && fieldIds.TryGetValue(fieldId, out var newFieldId))
                    mapping.IndexFieldId = newFieldId;
        }
    }
    private static RuleSet ToRuleSet(RuleSetEditorViewModel editor) => new() { Rules = editor.ToModels(), MatchMode = editor.MatchMode, MatchMinimum = editor.MatchMinimum };
}
