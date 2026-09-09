using System.Security.Cryptography;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;

namespace Capture.Core.CaptureProfiles;

public sealed record CaptureWorkflowResult(CapturePlan Plan, CaptureMaterializationResult Materialized);

/// <summary>Single workflow entry point: acquire and cache page analysis, plan, then optionally materialize.</summary>
public sealed class CaptureWorkflowService(
    IAppPaths paths,
    IPdfRasterizer pdfs,
    IImagePageImporter images,
    ILatticeBuilder latticeBuilder,
    IBarcodeDecoder barcodes,
    IBlankPageDetector blanks,
    CapturePlanner planner,
    CapturePlanMaterializer materializer,
    IProfileApplicator? applicator = null)
{
    private const int AnalysisDpi = 200;

    public async Task<CaptureWorkflowResult> ExecuteAsync(
        CaptureProfile profile,
        IReadOnlyList<string> sourcePaths,
        DocumentSource source,
        string inputChannel,
        bool startNewBatch = false,
        bool closeBatchWhenFinished = false,
        IProgress<CaptureMaterializationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!profile.Enabled)
            throw new InvalidOperationException("This capture profile is disabled.");
        ValidateInputs(sourcePaths);
        paths.EnsureCreated();
        var scratch = CreateScratch("capture-plan-");
        try
        {
            var analysis = await AnalyzeAsync(profile, sourcePaths, source, scratch, cancellationToken).ConfigureAwait(false);
            var plan = planner.Plan(profile, analysis.Inputs);
            var result = await materializer.MaterializeAsync(
                profile, plan, analysis.Sources, progress, inputChannel,
                startNewBatch, closeBatchWhenFinished, cancellationToken).ConfigureAwait(false);
            return new CaptureWorkflowResult(plan, result);
        }
        finally
        {
            TryDeleteScratch(scratch);
        }
    }

    /// <summary>Runs production-equivalent input analysis and deterministic planning without creating
    /// batches, documents, indexes, redactions, or exports.</summary>
    public async Task<CapturePlan> PreviewAsync(
        CaptureProfile profile,
        IReadOnlyList<string> sourcePaths,
        DocumentSource source,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(sourcePaths);
        paths.EnsureCreated();
        var scratch = CreateScratch("capture-preview-");
        try
        {
            var analysis = await AnalyzeAsync(profile, sourcePaths, source, scratch, cancellationToken).ConfigureAwait(false);
            return planner.Plan(profile, analysis.Inputs);
        }
        finally
        {
            TryDeleteScratch(scratch);
        }
    }

    /// <summary>Runs analysis, planning, and field application without persisting documents or invoking
    /// post-index actions such as redaction and export. Profile scripts retain their normal settings gate.</summary>
    public async Task<CapturePreviewResult> PreviewWithIndexesAsync(
        CaptureProfile profile,
        IReadOnlyList<string> sourcePaths,
        DocumentSource source,
        CancellationToken cancellationToken = default)
    {
        if (applicator is null)
            throw new InvalidOperationException("Index preview is unavailable.");

        ValidateInputs(sourcePaths);
        paths.EnsureCreated();
        var scratch = CreateScratch("capture-preview-");
        try
        {
            var analysis = await AnalyzeAsync(profile, sourcePaths, source, scratch, cancellationToken).ConfigureAwait(false);
            var plan = planner.Plan(profile, analysis.Inputs);
            var previewBatches = new List<CapturePreviewBatch>(plan.Batches.Count);

            for (var batchIndex = 0; batchIndex < plan.Batches.Count; batchIndex++)
            {
                var plannedBatch = plan.Batches[batchIndex];
                var preparedDocuments = plannedBatch.Documents
                    .Select(document => PreparePreviewDocument(document, analysis.Sources, source))
                    .ToList();
                IReadOnlyList<IndexValue> batchValues = plannedBatch.CapturedValues;

                var batchSourcePages = BatchExtractionSourcePages(plannedBatch);
                if (batchSourcePages.Count > 0)
                {
                    var batchInput = PreparePreviewDocument(
                        new PlannedDocument(Guid.NewGuid(), null, batchSourcePages, []),
                        analysis.Sources,
                        source);
                    batchValues = await applicator.ApplyAsync(
                        profile.Batch.Fields,
                        profile.Batch.Scripts,
                        profile.Batch.SharedScriptSource,
                        batchInput.Lattices,
                        profile.Name,
                        context: new DefaultValueContext
                        {
                            BatchNumber = batchIndex + 1,
                            ScriptScope = Capture.Core.Scripting.ScriptScopeKind.Batch,
                            TriggerMatches = (plannedBatch.BoundaryMatches ?? [])
                                .Select(match => new Capture.Core.Scripting.ScriptTriggerMatchInfo(match.RuleId, match.CapturedValue, match.Confidence))
                                .ToList()
                        },
                        pages: batchInput.Pages,
                        existingValues: plannedBatch.CapturedValues,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                var previewDocuments = new List<CapturePreviewDocument>(preparedDocuments.Count);
                for (var documentIndex = 0; documentIndex < preparedDocuments.Count; documentIndex++)
                {
                    var prepared = preparedDocuments[documentIndex];
                    var plannedDocument = plannedBatch.Documents[documentIndex];
                    IReadOnlyList<IndexValue> documentValues = [];
                    var status = DocumentStatus.NeedsReview;

                    if (plannedDocument.Type is { } type)
                    {
                        var matchesById = plannedDocument.BoundaryMatches.ToDictionary(match => match.RuleId);
                        var boundaryValues = type.Fields
                            .Where(field => field.BoundaryRuleId is { } ruleId && matchesById.ContainsKey(ruleId))
                            .Select(field => new IndexValue
                            {
                                FieldId = field.Id,
                                FieldName = field.Name,
                                Value = matchesById[field.BoundaryRuleId!.Value].CapturedValue ?? string.Empty,
                                Confidence = (float)(matchesById[field.BoundaryRuleId.Value].Confidence ?? 0)
                            }).ToList();
                        documentValues = await applicator.ApplyAsync(
                            type.Fields,
                            type.Scripts,
                            type.SharedScriptSource,
                            prepared.Lattices,
                            type.Name,
                            context: new DefaultValueContext
                            {
                                BatchNumber = batchIndex + 1,
                                DocumentNumber = documentIndex + 1,
                                ScriptScope = Capture.Core.Scripting.ScriptScopeKind.Document,
                                DocumentType = type.Name,
                                BatchValues = batchValues,
                                TriggerMatches = plannedDocument.BoundaryMatches
                                    .Select(match => new Capture.Core.Scripting.ScriptTriggerMatchInfo(match.RuleId, match.CapturedValue, match.Confidence))
                                    .ToList()
                            },
                            pages: prepared.Pages,
                            existingValues: boundaryValues,
                            document: prepared.Document,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        status = IndexFormat.StatusFor(batchValues.Concat(documentValues), type.AutoReadyThreshold);
                    }

                    previewDocuments.Add(new CapturePreviewDocument(
                        documentIndex + 1,
                        plannedDocument.Type?.Name ?? "Unclassified",
                        prepared.SourceFiles,
                        prepared.Pages.Count,
                        status,
                        documentValues));
                }

                previewBatches.Add(new CapturePreviewBatch(batchIndex + 1, batchValues, previewDocuments));
            }

            return new CapturePreviewResult(plan, previewBatches);
        }
        finally
        {
            TryDeleteScratch(scratch);
        }
    }

    private async Task<AnalysisResult> AnalyzeAsync(
        CaptureProfile profile,
        IReadOnlyList<string> sourcePaths,
        DocumentSource source,
        string scratch,
        CancellationToken cancellationToken)
    {
        var analyzedInputs = new List<AnalyzedInput>();
        var materializationSources = new Dictionary<string, CaptureMaterializationSource>(StringComparer.Ordinal);
        var allRules = new[] { profile.Batch.StartRules }
            .Concat(profile.DocumentTypes.SelectMany(type => new[] { type.StartRules, type.RecognitionRules }))
            .Where(ruleSet => ruleSet.MatchMode != SeparationMatchMode.None)
            .SelectMany(ruleSet => ruleSet.Rules)
            .ToList();

        for (var inputIndex = 0; inputIndex < sourcePaths.Count; inputIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = sourcePaths[inputIndex];
            var inputId = $"input-{inputIndex + 1}";
            var rasterDirectory = Path.Combine(scratch, inputId);
            Directory.CreateDirectory(rasterDirectory);
            var rasters = ImportFormats.IsPdf(sourcePath)
                ? await pdfs.RasterizeAsync(sourcePath, rasterDirectory, AnalysisDpi, cancellationToken).ConfigureAwait(false)
                : await images.ImportAsync(sourcePath, rasterDirectory, cancellationToken).ConfigureAwait(false);

            // Each page's OCR/lattice build, barcode decode, zone extraction, and blank-page check are
            // independent of every other page — OCR in particular spawns its own Tesseract process per
            // page, so running these serially wastes every core beyond the first on a multi-page import.
            // Results are written into a page-indexed array so the fan-out doesn't disturb page order.
            var pageResults = new (int PageNumber, PageLattice Lattice, AnalyzedPage Page)[rasters.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, rasters.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
                async (index, ct) =>
                {
                    var raster = rasters[index];
                    var transientDocument = new CaptureDocument
                    {
                        OriginalFileName = Path.GetFileName(sourcePath),
                        StoredPath = sourcePath,
                        Source = source
                    };
                    var transientPage = new DocumentPage
                    {
                        DocumentId = transientDocument.Id,
                        PageNumber = raster.PageNumber,
                        SourcePageNumber = raster.PageNumber,
                        ImagePath = raster.ImagePath,
                        Width = raster.Width,
                        Height = raster.Height,
                        Dpi = raster.Dpi
                    };
                    var lattice = await latticeBuilder.BuildPageAsync(transientDocument, transientPage, ct).ConfigureAwait(false);

                    var analyzedBarcodes = new List<AnalyzedBarcode>();
                    foreach (var rule in allRules.Where(rule => rule.Type == SeparationStrategyType.Barcode))
                    {
                        var barcode = barcodes.Decode(raster.ImagePath, rule.Zone);
                        if (barcode is not null)
                            analyzedBarcodes.Add(new(barcode.Text, barcode.Format, barcode.Confidence, rule.Id));
                    }

                    var zoneTexts = allRules
                        .Where(rule => rule.Type == SeparationStrategyType.OcrZone && rule.Zone is not null)
                        .ToDictionary(rule => rule.Id, rule => ZonalExtractor.Extract(lattice, rule.Zone!).Text);
                    var blankRules = allRules.Where(rule => rule.Type == SeparationStrategyType.BlankPage).ToList();
                    var blankMatches = blankRules
                        .Where(rule => blanks.IsBlank(raster.ImagePath, rule.BlankInkPercent))
                        .Select(rule => rule.Id)
                        .ToHashSet();
                    pageResults[index] = (raster.PageNumber, lattice, new AnalyzedPage(
                        inputId,
                        raster.PageNumber,
                        string.Join(" ", lattice.Words.Select(word => word.Text)),
                        analyzedBarcodes,
                        blankMatches.Count > 0,
                        zoneTexts,
                        blankMatches));
                }).ConfigureAwait(false);

            var analyzedPages = new List<AnalyzedPage>(pageResults.Length);
            var latticeMap = new Dictionary<int, PageLattice>();
            foreach (var (pageNumber, lattice, page) in pageResults)
            {
                latticeMap[pageNumber] = lattice;
                analyzedPages.Add(page);
            }

            analyzedInputs.Add(new AnalyzedInput(inputId, analyzedPages));
            materializationSources[inputId] = new CaptureMaterializationSource(
                inputId,
                sourcePath,
                source,
                rasters,
                latticeMap,
                await ContentHashAsync(sourcePath, cancellationToken).ConfigureAwait(false));
        }

        return new AnalysisResult(analyzedInputs, materializationSources);
    }

    private string CreateScratch(string prefix)
    {
        var scratch = Path.Combine(paths.WorkDirectory, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        return scratch;
    }

    private static PreparedPreviewDocument PreparePreviewDocument(
        PlannedDocument planned,
        IReadOnlyDictionary<string, CaptureMaterializationSource> sources,
        DocumentSource source)
    {
        var documentId = Guid.NewGuid();
        var lattices = new List<PageLattice>(planned.SourcePages.Count);
        var pages = new List<DocumentPage>(planned.SourcePages.Count);
        for (var index = 0; index < planned.SourcePages.Count; index++)
        {
            var sourcePage = planned.SourcePages[index];
            var sourceInfo = sources[sourcePage.InputId];
            var raster = sourceInfo.RasterPages.Single(page => page.PageNumber == sourcePage.PageNumber);
            var lattice = sourceInfo.Lattices[sourcePage.PageNumber];
            var pageNumber = index + 1;
            lattices.Add(new PageLattice
            {
                PageNumber = pageNumber,
                PixelWidth = lattice.PixelWidth,
                PixelHeight = lattice.PixelHeight,
                Dpi = lattice.Dpi,
                Source = lattice.Source,
                Words = lattice.Words
            });
            pages.Add(new DocumentPage
            {
                DocumentId = documentId,
                PageNumber = pageNumber,
                SourcePageNumber = sourcePage.PageNumber,
                ImagePath = raster.ImagePath,
                Width = raster.Width,
                Height = raster.Height,
                Dpi = raster.Dpi
            });
        }

        var sourceFiles = planned.SourcePages
            .Select(page => Path.GetFileName(sources[page.InputId].SourcePath))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var name = sourceFiles.Count == 1 ? sourceFiles[0] : string.Join(", ", sourceFiles);
        var document = new CaptureDocument
        {
            Id = documentId,
            OriginalFileName = name,
            StoredPath = planned.SourcePages.Count == 0 ? string.Empty : sources[planned.SourcePages[0].InputId].SourcePath,
            Source = source,
            PageCount = pages.Count,
            ProfileId = planned.Type?.Id
        };
        return new PreparedPreviewDocument(document, pages, lattices, name);
    }

    private static IReadOnlyList<SourcePage> BatchExtractionSourcePages(PlannedBatch batch)
    {
        var pages = new List<SourcePage>();
        if (batch.BoundaryPage is { } boundary)
            pages.Add(boundary);
        if (batch.Documents.FirstOrDefault() is { } firstDocument)
            pages.AddRange(firstDocument.SourcePages);
        return pages.Distinct().ToList();
    }

    private static void ValidateInputs(IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0)
            throw new ArgumentException("At least one input is required.", nameof(sourcePaths));
    }

    private static void TryDeleteScratch(string scratch)
    {
        try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
        catch { }
    }

    private static async Task<string> ContentHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private sealed record AnalysisResult(
        List<AnalyzedInput> Inputs,
        Dictionary<string, CaptureMaterializationSource> Sources);

    private sealed record PreparedPreviewDocument(
        CaptureDocument Document,
        IReadOnlyList<DocumentPage> Pages,
        IReadOnlyList<PageLattice> Lattices,
        string SourceFiles);
}
