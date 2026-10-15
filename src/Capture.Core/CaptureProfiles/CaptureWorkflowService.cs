using System.Diagnostics;
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
    IProfileApplicator? applicator = null,
    IAiPageSeparator? aiSeparator = null,
    IPageStraightener? straightener = null,
    IImageRotator? rotator = null,
    IOcrEngine? ocr = null)
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
        CancellationToken cancellationToken = default,
        IProgress<string>? status = null)
    {
        if (!profile.Enabled)
            throw new InvalidOperationException("This capture profile is disabled.");
        ValidateInputs(sourcePaths);
        paths.EnsureCreated();
        var scratch = CreateScratch("capture-plan-");
        try
        {
            var analysis = await AnalyzeAsync(profile, sourcePaths, source, scratch, cancellationToken, status).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default,
        IProgress<string>? stage = null)
    {
        if (applicator is null)
            throw new InvalidOperationException("Index preview is unavailable.");

        ValidateInputs(sourcePaths);
        paths.EnsureCreated();
        var scratch = CreateScratch("capture-preview-");
        try
        {
            var analysis = await AnalyzeAsync(profile, sourcePaths, source, scratch, cancellationToken, stage).ConfigureAwait(false);
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
                    // See CapturePlanMaterializer.MaterializeDocumentAsync: a profile with no document
                    // types at all has nothing to extract/validate, so there's nothing for a reviewer
                    // to complete — but a profile that defines types and still failed to match this
                    // document is a genuine classification miss and stays NeedsReview.
                    var status = profile.DocumentTypes.Count == 0 ? DocumentStatus.Ready : DocumentStatus.NeedsReview;

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

                    var reviewNote = CapturePlanMaterializer.ReviewNote(plannedDocument, analysis.Sources);
                    if (reviewNote is not null)
                        status = DocumentStatus.NeedsReview;
                    previewDocuments.Add(new CapturePreviewDocument(
                        documentIndex + 1,
                        plannedDocument.Type?.Name ?? "Unclassified",
                        prepared.SourceFiles,
                        prepared.Pages.Count,
                        status,
                        documentValues,
                        reviewNote?.Split('\n')));
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
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        var analyzedInputs = new List<AnalyzedInput>();
        var pagesRead = 0;
        var pagesFound = 0;
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

            // Blank-page removal runs before any other ingestion work below — OCR, barcode/zone/blank-
            // page rule evaluation, batch/document classification — so a discarded page is invisible to
            // every rule, including a BlankPage separation rule (batch or document-type): such a rule can
            // never match while this is enabled, since its trigger page is gone before rule evaluation
            // starts. Survivors are renumbered so position-based rules (EveryNPages) count only real
            // content pages, as if the blanks never existed; originalPageNumbers preserves each
            // survivor's true page position in the source file for CapturePlanMaterializer's
            // single-source-PDF fast path, which extracts pages directly from the original PDF by number.
            IReadOnlyDictionary<int, int>? originalPageNumbers = null;
            IReadOnlyList<int> removedBlankPages = [];
            if (profile.RemoveBlankPagesOnIngestion)
            {
                var survivors = rasters.Where(raster => !blanks.IsBlank(raster.ImagePath, profile.RemoveBlankPagesInkPercent)).ToList();
                var mapping = new Dictionary<int, int>();
                var renumbered = new List<RasterPage>(survivors.Count);
                for (var survivorIndex = 0; survivorIndex < survivors.Count; survivorIndex++)
                {
                    mapping[survivorIndex + 1] = survivors[survivorIndex].PageNumber;
                    renumbered.Add(survivors[survivorIndex] with { PageNumber = survivorIndex + 1 });
                }
                removedBlankPages = rasters.Except(survivors).Select(raster => raster.PageNumber).ToList();
                rasters = renumbered;
                originalPageNumbers = mapping;
            }

            pagesFound += rasters.Count;
            var correctedRasters = rasters.ToArray();
            var correctedPages = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
            // How long each page took — for the documents' history, to show where an import's time went.
            var readTimes = new System.Collections.Concurrent.ConcurrentDictionary<int, TimeSpan>();
            var straightenTimes = new System.Collections.Concurrent.ConcurrentDictionary<int, TimeSpan>();

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
                        // The page's place in the source file: with blank pages removed, PageNumber has
                        // been renumbered, and a scanned PDF's page would otherwise be read from the
                        // wrong page of the file.
                        SourcePageNumber = originalPageNumbers is { } map && map.TryGetValue(raster.PageNumber, out var original)
                            ? original
                            : raster.PageNumber,
                        ImagePath = raster.ImagePath,
                        Width = raster.Width,
                        Height = raster.Height,
                        Dpi = raster.Dpi
                    };
                    var pageNumber = raster.PageNumber;
                    var timer = Stopwatch.StartNew();
                    var lattice = await latticeBuilder.BuildPageAsync(transientDocument, transientPage, ct, profile.OcrOptions).ConfigureAwait(false);
                    var readTime = timer.Elapsed;
                    var straightenTimer = Stopwatch.StartNew();
                    var straightenedRaster = profile.StraightenPages && lattice.Source != LatticeSource.PdfText
                        ? await StraightenAsync(raster, lattice, ct).ConfigureAwait(false)
                        : null;
                    if (profile.StraightenPages && lattice.Source != LatticeSource.PdfText)
                        straightenTimes[pageNumber] = straightenTimer.Elapsed;
                    if (straightenedRaster is { } straightened)
                    {
                        // Read again from the corrected image itself — for a scanned PDF too, whose
                        // pages would otherwise be read from the uncorrected file.
                        raster = straightened;
                        correctedRasters[index] = straightened;
                        correctedPages[raster.PageNumber] = 0;
                        var imageDocument = new CaptureDocument
                        {
                            OriginalFileName = transientDocument.OriginalFileName,
                            StoredPath = raster.ImagePath,
                            Source = source
                        };
                        lattice = await latticeBuilder.BuildPageAsync(imageDocument, new DocumentPage
                        {
                            DocumentId = imageDocument.Id,
                            PageNumber = raster.PageNumber,
                            SourcePageNumber = raster.PageNumber,
                            ImagePath = raster.ImagePath,
                            Width = raster.Width,
                            Height = raster.Height,
                            Dpi = raster.Dpi
                        }, ct, profile.OcrOptions).ConfigureAwait(false);
                        readTime += straightenTimer.Elapsed - straightenTimes[pageNumber];
                    }
                    readTimes[pageNumber] = readTime;

                    var analyzedBarcodes = new List<AnalyzedBarcode>();
                    foreach (var rule in allRules.Where(rule => rule.Type == SeparationStrategyType.Barcode))
                    {
                        var barcode = barcodes.Decode(raster.ImagePath, rule.Zone);
                        if (barcode is not null)
                            analyzedBarcodes.Add(new(barcode.Text, barcode.Format, barcode.Confidence, rule.Id));
                    }

                    // Separator-sheet rules always scan the whole page, so one decode serves all of them;
                    // the planner matches the decoded value against each rule's sheet kind. Tagged per rule
                    // (not left untagged) so an ordinary Barcode rule never also fires on a separator sheet.
                    var sheetRules = allRules.Where(rule => rule.Type == SeparationStrategyType.CaptureSeparator).ToList();
                    if (sheetRules.Count > 0 && barcodes.Decode(raster.ImagePath, null) is { } sheetBarcode)
                        foreach (var rule in sheetRules)
                            analyzedBarcodes.Add(new(sheetBarcode.Text, sheetBarcode.Format, sheetBarcode.Confidence, rule.Id));

                    var zoneTexts = allRules
                        .Where(rule => rule.Type == SeparationStrategyType.OcrZone && rule.Zone is not null)
                        .ToDictionary(rule => rule.Id, rule => ZonalExtractor.Extract(lattice, rule.Zone!).Text);
                    var blankRules = allRules.Where(rule => rule.Type == SeparationStrategyType.BlankPage).ToList();
                    var blankMatches = blankRules
                        .Where(rule => blanks.IsBlank(raster.ImagePath, rule.BlankInkPercent))
                        .Select(rule => rule.Id)
                        .ToHashSet();
                    status?.Report($"Reading page {Interlocked.Increment(ref pagesRead)} of {pagesFound}…");
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
                correctedRasters,
                latticeMap,
                await ContentHashAsync(sourcePath, cancellationToken).ConfigureAwait(false))
            {
                OriginalPageNumbers = originalPageNumbers,
                RemovedBlankPages = removedBlankPages,
                CorrectedPages = correctedPages.Keys.ToHashSet(),
                ReadTimes = new Dictionary<int, TimeSpan>(readTimes),
                StraightenTimes = new Dictionary<int, TimeSpan>(straightenTimes)
            };
        }

        if (profile.AiSeparation)
        {
            var aiTimer = Stopwatch.StartNew();
            analyzedInputs = await AddAiVerdictsAsync(profile, analyzedInputs, materializationSources, cancellationToken, status).ConfigureAwait(false);
            var aiPages = analyzedInputs.Sum(input => input.Pages.Count(page => page.Ai is not null));
            if (aiPages > 0)
                foreach (var id in materializationSources.Keys.ToList())
                    materializationSources[id] = materializationSources[id] with { AiSeparationTime = aiTimer.Elapsed, AiSeparationPages = aiPages };
        }

        return new AnalysisResult(analyzedInputs, materializationSources);
    }

    // Below this tilt a page is left as it is: re-drawing it for a fraction of a degree only softens it.
    private const double MinSkewDegrees = 0.4;

    /// <summary>Straightens a tilted page and turns one that's upside down or sideways, rewriting
    /// <paramref name="raster"/>'s image in place. Returns the corrected page, or null when it was fine.
    /// Which way up reads best is only tried when the page read poorly, so an upright page costs one
    /// tilt measurement and nothing more.</summary>
    private async Task<RasterPage?> StraightenAsync(RasterPage raster, PageLattice lattice, CancellationToken cancellationToken)
    {
        if (straightener is null)
            return null;

        var changed = false;
        var (width, height) = (raster.Width, raster.Height);
        var skew = straightener.DetectSkew(raster.ImagePath);
        if (Math.Abs(skew) >= MinSkewDegrees)
        {
            (width, height) = straightener.Rotate(raster.ImagePath, -skew);
            changed = true;
        }

        if (rotator is not null && ocr is not null
            && (lattice.Words.Count == 0 || LatticeQuality.IsPoorOcr(lattice.Words, 60))
            && !blanks.IsBlank(raster.ImagePath, 1f))
        {
            var turn = await BestOrientationAsync(raster.ImagePath, changed ? null : lattice, cancellationToken).ConfigureAwait(false);
            if (turn != 0)
            {
                (width, height) = rotator.Rotate(raster.ImagePath, turn);
                changed = true;
                // A sideways page couldn't be measured for tilt until it was turned.
                if (turn != 180 && straightener.DetectSkew(raster.ImagePath) is var after && Math.Abs(after) >= MinSkewDegrees)
                    (width, height) = straightener.Rotate(raster.ImagePath, -after);
            }
        }

        return changed ? raster with { Width = width, Height = height } : null;
    }

    /// <summary>The clockwise turn (0, 90, 180 or 270) that makes the page read best. Upside-down or
    /// sideways text comes back from OCR as low-confidence fragments, so the turn that reads as the most
    /// confident real words wins — by a clear margin, or the page is left as it is.</summary>
    private async Task<int> BestOrientationAsync(string imagePath, PageLattice? asIs, CancellationToken cancellationToken)
    {
        var scores = new Dictionary<int, int>
        {
            [0] = asIs is not null
                ? ReadableCharacters(asIs.Words.Select(word => (word.Text, word.Confidence)))
                : await ScoreAsync(imagePath, 0, cancellationToken).ConfigureAwait(false)
        };
        foreach (var turn in new[] { 180, 90, 270 })
            scores[turn] = await ScoreAsync(imagePath, turn, cancellationToken).ConfigureAwait(false);

        var best = scores.MaxBy(pair => pair.Value);
        return best.Key != 0 && best.Value >= 40 && best.Value > scores[0] * 1.5 ? best.Key : 0;
    }

    private async Task<int> ScoreAsync(string imagePath, int turn, CancellationToken cancellationToken)
    {
        var copy = Path.Combine(Path.GetDirectoryName(imagePath)!, $"{Path.GetFileNameWithoutExtension(imagePath)}-turn{turn}{Path.GetExtension(imagePath)}");
        try
        {
            File.Copy(imagePath, copy, overwrite: true);
            if (turn != 0)
                rotator!.Rotate(copy, turn);
            var result = await ocr!.RecognizeAsync(copy, cancellationToken).ConfigureAwait(false);
            return ReadableCharacters(result.Words.Select(word => (word.Text, word.Confidence)));
        }
        finally
        {
            try { File.Delete(copy); }
            catch (IOException) { }
        }
    }

    // Characters in confidently read words of three or more letters — what upright text has plenty of and
    // turned text has almost none of.
    internal static int ReadableCharacters(IEnumerable<(string Text, float Confidence)> words) =>
        words.Where(word => word.Confidence >= 70 && word.Text.Count(char.IsLetter) >= 3)
            .Sum(word => word.Text.Length);

    /// <summary>Asks the AI, page by page across every input, where documents start and what type each
    /// is. A file that's its own document always starts one on its first page. Without a configured AI
    /// the pages are left without a verdict: rules and the fallback type decide, and the planner flags the
    /// documents for review.</summary>
    private async Task<List<AnalyzedInput>> AddAiVerdictsAsync(
        CaptureProfile profile,
        List<AnalyzedInput> inputs,
        IReadOnlyDictionary<string, CaptureMaterializationSource> sources,
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        if (aiSeparator is not { IsConfigured: true } || inputs.All(input => input.Pages.Count == 0))
            return inputs;

        var pages = new List<AiSeparationPage>();
        foreach (var input in inputs)
        {
            var source = sources[input.Id];
            for (var index = 0; index < input.Pages.Count; index++)
            {
                var page = input.Pages[index];
                var raster = source.RasterPages.FirstOrDefault(item => item.PageNumber == page.PageNumber);
                var text = source.Lattices.TryGetValue(page.PageNumber, out var lattice)
                    ? LatticeText.Build(lattice.Words).Text
                    : page.Text;
                pages.Add(new AiSeparationPage(
                    text,
                    raster?.ImagePath,
                    raster is not null && blanks.IsBlank(raster.ImagePath, profile.RemoveBlankPagesInkPercent),
                    ForceNew: index == 0 && profile.FileIsDocumentBoundary));
            }
        }

        status?.Report($"Sending pages to the AI to separate documents… 0 of {pages.Count}");
        var aiProgress = status is null ? null : new InlineProgress<int>(done =>
            status.Report($"Sending pages to the AI to separate documents… {done} of {pages.Count}"));
        var verdicts = await aiSeparator.AnalyzeAsync(
            pages, CapturePlanner.AiTypes(profile), profile.AiSeparationGuidance, aiProgress, cancellationToken).ConfigureAwait(false);
        var next = 0;
        return inputs
            .Select(input => input with { Pages = input.Pages.Select(page => page with { Ai = verdicts[next++] with { Provider = aiSeparator.Name } }).ToList() })
            .ToList();
    }

    // Passes each report straight on: the caller's own Progress<T> already moves it to the UI thread.
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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
