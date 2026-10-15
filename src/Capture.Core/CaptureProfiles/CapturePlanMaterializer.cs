using System.Diagnostics;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
using Capture.Core.Store;

namespace Capture.Core.CaptureProfiles;

public sealed record CaptureMaterializationSource(
    string Id,
    string SourcePath,
    DocumentSource Source,
    IReadOnlyList<RasterPage> RasterPages,
    IReadOnlyDictionary<int, PageLattice> Lattices,
    string? ContentHash = null)
{
    /// <summary>One stable id for this source occurrence, shared by every document split from it.</summary>
    public Guid SourceImportId { get; init; } = Guid.NewGuid();

    /// <summary>Maps a (possibly renumbered) <see cref="RasterPage.PageNumber"/> back to the true page
    /// position in <see cref="SourcePath"/> — null unless CaptureProfile.RemoveBlankPagesOnIngestion
    /// discarded and renumbered pages for this source. Needed only by the single-source-PDF fast path in
    /// CapturePlanMaterializer, which extracts pages directly from the original PDF by page number and
    /// would otherwise pull the wrong pages once numbering no longer matches the source file.</summary>
    public IReadOnlyDictionary<int, int>? OriginalPageNumbers { get; init; }

    /// <summary>Pages (by <see cref="RasterPage.PageNumber"/>) whose image was straightened or turned
    /// — see CaptureProfile.StraightenPages. A document with any of them is built from the corrected
    /// images rather than copied from the original PDF, which still has them crooked.</summary>
    public IReadOnlySet<int> CorrectedPages { get; init; } = new HashSet<int>();

    /// <summary>Pages of <see cref="SourcePath"/> dropped as blank — see
    /// CaptureProfile.RemoveBlankPagesOnIngestion.</summary>
    public IReadOnlyList<int> RemovedBlankPages { get; init; } = [];

    /// <summary>How long reading each page's text took (by <see cref="RasterPage.PageNumber"/>) —
    /// rendering, OCR and, for a straightened page, reading it again.</summary>
    public IReadOnlyDictionary<int, TimeSpan> ReadTimes { get; init; } = new Dictionary<int, TimeSpan>();

    /// <summary>How long checking and straightening each page took.</summary>
    public IReadOnlyDictionary<int, TimeSpan> StraightenTimes { get; init; } = new Dictionary<int, TimeSpan>();

    /// <summary>The AI separation call for the whole import this source was part of, and how many pages
    /// it covered — pages are sent together, so the time can't be split per document.</summary>
    public TimeSpan? AiSeparationTime { get; init; }
    public int AiSeparationPages { get; init; }
}

/// <summary>How long the slow parts of materialising one document took, for its history.</summary>
public sealed record ImportTimings(TimeSpan? Build = null, TimeSpan? Fields = null, TimeSpan? PostIndex = null);

public sealed record CaptureMaterializationProgress(int CompletedDocuments, int TotalDocuments, string Message);
public sealed record CaptureMaterializationResult(IReadOnlyList<CaptureDocument> Documents, IReadOnlyList<Guid> BatchIds);

/// <summary>Coordinates persistence of a validated plan and rolls back newly-created documents on failure.</summary>
public sealed class CapturePlanMaterializer(
    IAppPaths paths,
    IDocumentStore documents,
    IIndexValueStore indexes,
    ILatticeStore lattices,
    IProfileApplicator applicator,
    IPdfSubsetWriter subsetWriter,
    IMergedDocumentWriter mergedWriter,
    IOpenBatchStore? openBatches = null,
    IEnumerable<IPostIndexStep>? postIndexSteps = null,
    IDocumentEventStore? events = null)
{
    private readonly IReadOnlyList<IPostIndexStep> _postIndexSteps = postIndexSteps?.ToList() ?? [];

    public async Task<CaptureMaterializationResult> MaterializeAsync(
        CaptureProfile profile,
        CapturePlan plan,
        IReadOnlyDictionary<string, CaptureMaterializationSource> sources,
        IProgress<CaptureMaterializationProgress>? progress = null,
        string inputChannel = "manual",
        bool startNewBatch = false,
        bool closeBatchWhenFinished = false,
        CancellationToken cancellationToken = default)
    {
        var createdDocuments = new List<CaptureDocument>();
        var createdBatches = new List<Guid>();
        var blankPagesReported = new HashSet<string>(StringComparer.Ordinal);
        var total = plan.Batches.Sum(batch => batch.Documents.Count);
        try
        {
            for (var batchIndex = 0; batchIndex < plan.Batches.Count; batchIndex++)
            {
                var plannedBatch = plan.Batches[batchIndex];
                cancellationToken.ThrowIfCancellationRequested();
                CaptureBatch? batch = null;
                var batchWasCreated = false;
                if (batchIndex == 0 && plannedBatch.IsGeneric && !profile.Batch.StartNewBatchForEachFile && !startNewBatch && openBatches is not null)
                    batch = await openBatches.GetOpenBatchAsync(profile.Id, inputChannel, cancellationToken).ConfigureAwait(false);

                if (batch is null)
                {
                    if (openBatches is not null)
                    {
                        var previous = await openBatches.GetOpenBatchAsync(profile.Id, inputChannel, cancellationToken).ConfigureAwait(false);
                        if (previous is not null) await openBatches.SetBatchStateAsync(previous.Id, BatchState.Closed, cancellationToken).ConfigureAwait(false);
                        batch = await openBatches.CreateScopedBatchAsync(profile.Id, inputChannel, cancellationToken).ConfigureAwait(false);
                    }
                    else batch = await documents.CreateBatchAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    batchWasCreated = true;
                    createdBatches.Add(batch.Id);
                }
                IReadOnlyList<IndexValue> materializedBatchValues = batchWasCreated
                    ? plannedBatch.CapturedValues
                    : await indexes.GetBatchAsync(batch.Id, cancellationToken).ConfigureAwait(false);
                // A batch created by an older build may already contain configured fields whose
                // values are all blank because batch barcode extraction had no page images. Allow the
                // next input to fill that wholly-empty set, but never replace any populated batch
                // value with data from a later document.
                var configuredBatchFieldIds = profile.Batch.Fields.Select(field => field.Id).ToHashSet();
                var hasPopulatedBatchValue = materializedBatchValues.Any(value =>
                    configuredBatchFieldIds.Contains(value.FieldId) && !string.IsNullOrWhiteSpace(value.Value));
                var hasMissingBatchValue = profile.Batch.Fields.Any(field =>
                    materializedBatchValues.All(value => value.FieldId != field.Id || string.IsNullOrWhiteSpace(value.Value)));
                var batchValuesSaved = !batchWasCreated && !(hasMissingBatchValue && !hasPopulatedBatchValue);

                ExtractionReport? batchReport = null;
                if (!batchValuesSaved)
                {
                    batchReport = new ExtractionReport();
                    var batchNumber = await documents.GetBatchNumberAsync(batch.Id, cancellationToken).ConfigureAwait(false);
                    var (batchLattices, batchPages) = PrepareBatchExtractionInput(plannedBatch, sources);
                    var batchValues = await applicator.ApplyAsync(
                        profile.Batch.Fields, profile.Batch.Scripts, profile.Batch.SharedScriptSource,
                        batchLattices, profile.Name,
                        context: new DefaultValueContext
                        {
                            BatchNumber = batchNumber,
                            ScriptScope = Capture.Core.Scripting.ScriptScopeKind.Batch,
                            Report = batchReport,
                            TriggerMatches = (plannedBatch.BoundaryMatches ?? []).Select(match => new Capture.Core.Scripting.ScriptTriggerMatchInfo(match.RuleId, match.CapturedValue, match.Confidence)).ToList()
                        },
                        pages: batchPages,
                        existingValues: plannedBatch.CapturedValues,
                        document: null, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await indexes.SaveBatchAsync(batch.Id, batchValues, cancellationToken).ConfigureAwait(false);
                    materializedBatchValues = batchValues;
                    batchValuesSaved = true;
                }

                foreach (var plannedDocument in plannedBatch.Documents)
                {
                    var buildTimer = Stopwatch.StartNew();
                    var document = await MaterializeDocumentAsync(profile, plannedDocument, batch.Id, sources, cancellationToken).ConfigureAwait(false);
                    var timings = new ImportTimings(Build: buildTimer.Elapsed);
                    createdDocuments.Add(document);

                    var documentPages = await documents.GetPagesAsync(document.Id, cancellationToken).ConfigureAwait(false);
                    var batchNumber = await documents.GetBatchNumberAsync(batch.Id, cancellationToken).ConfigureAwait(false);
                    var documentNumber = await documents.GetDocumentNumberInBatchAsync(batch.Id, document.Id, cancellationToken).ConfigureAwait(false);
                    var documentLattices = new List<PageLattice>();
                    foreach (var page in plannedDocument.SourcePages)
                    {
                        var lattice = sources[page.InputId].Lattices[page.PageNumber];
                        var renumbered = Renumber(lattice, documentLattices.Count + 1);
                        documentLattices.Add(renumbered);
                        await lattices.SaveAsync(document.Id, renumbered, cancellationToken).ConfigureAwait(false);
                    }

                    IReadOnlyList<IndexValue>? documentValues = null;
                    var report = new ExtractionReport();
                    if (batchReport is not null)
                    {
                        // Batch fields are read once, with the batch's first document.
                        report.Notes.AddRange(batchReport.Notes.Select(note => $"Batch fields: {note}"));
                        report.Problems.AddRange(batchReport.Problems.Select(problem => $"Batch fields: {problem}"));
                        batchReport = null;
                    }
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
                        var fieldsTimer = Stopwatch.StartNew();
                        var values = await applicator.ApplyAsync(
                            type.Fields, type.Scripts, type.SharedScriptSource, documentLattices, type.Name,
                            context: new DefaultValueContext
                            {
                                BatchNumber = batchNumber,
                                DocumentNumber = documentNumber,
                                ScriptScope = Capture.Core.Scripting.ScriptScopeKind.Document,
                                DocumentType = type.Name,
                                BatchValues = materializedBatchValues,
                                Report = report,
                                TriggerMatches = plannedDocument.BoundaryMatches.Select(match => new Capture.Core.Scripting.ScriptTriggerMatchInfo(match.RuleId, match.CapturedValue, match.Confidence)).ToList()
                            },
                            pages: documentPages, existingValues: boundaryValues,
                            document: document, cancellationToken: cancellationToken).ConfigureAwait(false);
                        timings = timings with { Fields = fieldsTimer.Elapsed };
                        await indexes.SaveAsync(document.Id, values, cancellationToken).ConfigureAwait(false);
                        documentValues = values;
                        document.Status = document.ReviewNote is not null
                            ? DocumentStatus.NeedsReview
                            : IndexFormat.StatusFor(materializedBatchValues.Concat(values), type.AutoReadyThreshold);
                        await documents.UpdateAsync(document, cancellationToken).ConfigureAwait(false);
                        var postIndexTimer = Stopwatch.StartNew();
                        await RunPostIndexStepsAsync(document, documentPages, values, type, cancellationToken)
                            .ConfigureAwait(false);
                        timings = timings with { PostIndex = postIndexTimer.Elapsed };
                    }
                    await events.RecordAsync(
                        ImportHistory(profile, plannedDocument, document, sources, batchNumber, documentValues, blankPagesReported, report, timings),
                        cancellationToken).ConfigureAwait(false);
                    progress?.Report(new(createdDocuments.Count, total, $"Created {document.OriginalFileName}"));
                }

                if (!batchValuesSaved)
                    await indexes.SaveBatchAsync(batch.Id, plannedBatch.CapturedValues, cancellationToken).ConfigureAwait(false);

            }

            // A StartNewBatchForEachFile batch is closed here too, regardless of the caller's own
            // closeBatchWhenFinished (manual imports pass false there) — it will never be joined by a
            // later import (line ~70 above only reuses an open batch when StartNewBatchForEachFile is
            // false), so leaving it BatchState.Open until some *later* import's "close the previous
            // batch" step (line ~78 above) got around to it made it look joinable/"Open" to a reviewer
            // in the meantime when it never actually was.
            if ((closeBatchWhenFinished || profile.Batch.StartNewBatchForEachFile) && openBatches is not null)
            {
                var open = await openBatches.GetOpenBatchAsync(profile.Id, inputChannel, cancellationToken).ConfigureAwait(false);
                if (open is not null)
                    await openBatches.SetBatchStateAsync(open.Id, BatchState.Closed, cancellationToken).ConfigureAwait(false);
            }
            return new CaptureMaterializationResult(createdDocuments, createdBatches);
        }
        catch
        {
            foreach (var document in createdDocuments.AsEnumerable().Reverse())
            {
                try { await documents.PurgeAsync(document.Id, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            foreach (var batchId in createdBatches.AsEnumerable().Reverse())
            {
                try { await documents.DeleteEmptyBatchAsync(batchId, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            throw;
        }
    }

    /// <summary>The start of a document's history: where it came from, how it was split and typed,
    /// what was done to its pages, how its fields were read, and whether it needs a person.</summary>
    public static IReadOnlyList<DocumentEvent> ImportHistory(
        CaptureProfile profile,
        PlannedDocument planned,
        CaptureDocument document,
        IReadOnlyDictionary<string, CaptureMaterializationSource> sources,
        int batchNumber,
        IReadOnlyList<IndexValue>? values,
        ISet<string> blankPagesReported,
        ExtractionReport? report = null,
        ImportTimings? timings = null)
    {
        var history = new List<DocumentEvent>();
        var inputIds = planned.SourcePages.Select(page => page.InputId).Distinct().ToList();
        var files = inputIds.Select(id => sources.TryGetValue(id, out var source) ? Path.GetFileName(source.SourcePath) : id).ToList();
        var pages = planned.SourcePages.Count == 1 ? "1 page" : $"{planned.SourcePages.Count} pages";
        var message = document.Source switch
        {
            DocumentSource.Scan => $"Scanned ({pages})",
            DocumentSource.Watch => $"Picked up from a watch folder: {string.Join(", ", files)} ({pages})",
            _ => $"Imported {string.Join(", ", files)} ({pages})"
        };
        var origin = inputIds.Select(id =>
        {
            var numbers = planned.SourcePages.Where(page => page.InputId == id).Select(page => page.PageNumber).ToList();
            var name = sources.TryGetValue(id, out var source) ? Path.GetFileName(source.SourcePath) : id;
            return $"{name}: {PageList(numbers)}";
        });
        history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Import, message,
            string.Join("\n", new[] { $"Capture profile: {profile.Name}", $"Batch {batchNumber}" }.Concat(origin)),
            timings?.Build));

        var withAi = planned.AiProvider is null ? string.Empty : " with AI";
        var aiSource = inputIds.Select(id => sources.TryGetValue(id, out var source) ? source : null).FirstOrDefault(source => source?.AiSeparationTime is not null);
        var aiTime = aiSource?.AiSeparationTime is { } took
            ? $" — {DocumentEvent.FormatDuration(took)} for the {aiSource.AiSeparationPages} page(s) of this import"
            : string.Empty;
        var reasons = (planned.AiProvider is { } provider ? [$"AI: {provider}{aiTime}"] : Array.Empty<string>())
            .Concat(planned.Decisions ?? [])
            .ToList();
        history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Decision,
            planned.Type is { } type ? $"Split off{withAi} and typed as {type.Name}" : $"Split off{withAi}, with no document type",
            reasons.Count > 0 ? string.Join("\n", reasons) : null));

        // How each page's text was read — the PDF's own text, or OCR — and how long that took.
        var pageInfo = planned.SourcePages
            .Select((page, index) =>
            {
                sources.TryGetValue(page.InputId, out var input);
                return (number: index + 1,
                    source: input is not null && input.Lattices.TryGetValue(page.PageNumber, out var lattice) ? lattice.Source : (LatticeSource?)null,
                    read: input is not null && input.ReadTimes.TryGetValue(page.PageNumber, out var read) ? read : (TimeSpan?)null,
                    straighten: input is not null && input.StraightenTimes.TryGetValue(page.PageNumber, out var check) ? check : (TimeSpan?)null);
            })
            .ToList();
        var readers = pageInfo
            .Where(item => item.source is not null)
            .GroupBy(item => item.source!.Value)
            .Select(group =>
            {
                var reader = group.Key switch
                {
                    LatticeSource.PdfText => "the PDF's own text",
                    LatticeSource.ExternalOcr => "Azure Document Intelligence",
                    _ => "OCR"
                };
                var times = group.Where(item => item.read is not null).Select(item => item.read!.Value).ToList();
                var average = times.Count > 0 ? $" (avg {DocumentEvent.FormatDuration(TimeSpan.FromTicks((long)times.Average(time => time.Ticks)))}/page)" : string.Empty;
                return $"{reader} on {PageList(group.Select(item => item.number).ToList())}{average}";
            })
            .ToList();
        var readTotal = pageInfo.Any(item => item.read is not null)
            ? pageInfo.Where(item => item.read is not null).Aggregate(TimeSpan.Zero, (sum, item) => sum + item.read!.Value)
            : (TimeSpan?)null;
        var straightenTotal = pageInfo.Any(item => item.straighten is not null)
            ? pageInfo.Where(item => item.straighten is not null).Aggregate(TimeSpan.Zero, (sum, item) => sum + item.straighten!.Value)
            : (TimeSpan?)null;
        if (readers.Count > 0)
            history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Processing, $"Read the text: {string.Join("; ", readers)}",
                straightenTotal is { } checking ? $"Checking for crooked pages: {DocumentEvent.FormatDuration(checking)}" : null,
                readTotal));

        var corrected = planned.SourcePages
            .Select((page, index) => (page, number: index + 1))
            .Where(item => sources.TryGetValue(item.page.InputId, out var source) && source.CorrectedPages.Contains(item.page.PageNumber))
            .Select(item => item.number)
            .ToList();
        if (corrected.Count > 0)
            history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Processing,
                $"Straightened or turned the right way up: {PageList(corrected)}"));
        foreach (var id in inputIds)
        {
            if (sources.TryGetValue(id, out var source) && source.RemovedBlankPages.Count > 0 && blankPagesReported.Add(id))
                history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Processing,
                    $"Removed blank pages from {Path.GetFileName(source.SourcePath)}: {PageList(source.RemovedBlankPages)}"));
        }

        if (values is not null && planned.Type is { } documentType)
        {
            // Field names only — a value could be sensitive, and the values are on the document anyway.
            var fields = values.Where(value => value.Kind != FieldKind.Button && !value.IsConditionInactive).ToList();
            var filled = fields.Count(value => !string.IsNullOrWhiteSpace(value.Value));
            var low = fields.Where(value => !string.IsNullOrWhiteSpace(value.Value) && value.IsLowConfidence(documentType.AutoReadyThreshold)).Select(value => value.FieldName).ToList();
            var missing = fields.Where(value => value.IsMissing).Select(value => value.FieldName).ToList();
            var invalid = fields.Where(value => value.ValidationError is not null).Select(value => value.FieldName).ToList();
            var notes = new List<string>(report?.Notes ?? []);
            if (low.Count > 0) notes.Add($"Not sure of: {string.Join(", ", low)}");
            if (missing.Count > 0) notes.Add($"Required but empty: {string.Join(", ", missing)}");
            if (invalid.Count > 0) notes.Add($"Not valid: {string.Join(", ", invalid)}");
            history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Processing,
                $"Read its fields: {filled} of {fields.Count} filled", notes.Count > 0 ? string.Join("\n", notes) : null, timings?.Fields));
        }

        foreach (var problem in report?.Problems ?? [])
            history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Error, problem));

        history.Add(document.Status switch
        {
            DocumentStatus.Ready => DocumentEvent.ByCapture(document.Id, DocumentEventKind.Review, "Ready to export, without review"),
            DocumentStatus.NeedsReview => DocumentEvent.ByCapture(document.Id, DocumentEventKind.Review, "Needs review", document.ReviewNote),
            _ => DocumentEvent.ByCapture(document.Id, DocumentEventKind.Review, $"Status: {document.Status}", document.ReviewNote)
        });

        switch (document.RedactionStatus)
        {
            case RedactionStatus.PendingReview:
                history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Redaction, "Found information to redact — waiting for review", duration: timings?.PostIndex));
                break;
            case RedactionStatus.Applied:
                history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Redaction, "Redacted automatically", duration: timings?.PostIndex));
                break;
            case RedactionStatus.Failed:
                history.Add(DocumentEvent.ByCapture(document.Id, DocumentEventKind.Error, "Redaction failed", document.RedactionError, timings?.PostIndex));
                break;
        }
        return history;
    }

    /// <summary>"page 3", "pages 1–4", "pages 2, 5, 7–9".</summary>
    public static string PageList(IReadOnlyList<int> pages)
    {
        var sorted = pages.Distinct().Order().ToList();
        var ranges = new List<string>();
        for (var index = 0; index < sorted.Count;)
        {
            var end = index;
            while (end + 1 < sorted.Count && sorted[end + 1] == sorted[end] + 1)
                end++;
            ranges.Add(end - index >= 1 ? $"{sorted[index]}–{sorted[end]}" : sorted[index].ToString(System.Globalization.CultureInfo.InvariantCulture));
            index = end + 1;
        }
        return (sorted.Count == 1 ? "page " : "pages ") + string.Join(", ", ranges);
    }

    /// <summary>Everything a reviewer should know about how this document was put together: AI
    /// separation decisions it wasn't sure of, and pages whose OCR didn't go as configured.</summary>
    internal static string? ReviewNote(PlannedDocument planned, IReadOnlyDictionary<string, CaptureMaterializationSource> sources)
    {
        var notes = (planned.ReviewReasons ?? [])
            .Concat(planned.SourcePages
                .Select(page => sources.TryGetValue(page.InputId, out var source) && source.Lattices.TryGetValue(page.PageNumber, out var lattice)
                    ? lattice.Warning
                    : null)
                .OfType<string>())
            .ToList();
        return notes.Count > 0 ? string.Join("\n", notes) : null;
    }

    private async Task<CaptureDocument> MaterializeDocumentAsync(
        CaptureProfile profile,
        PlannedDocument planned,
        Guid batchId,
        IReadOnlyDictionary<string, CaptureMaterializationSource> sources,
        CancellationToken cancellationToken)
    {
        if (planned.SourcePages.Count == 0) throw new InvalidOperationException("A planned document must contain a page.");
        var sourceIds = planned.SourcePages.Select(page => page.InputId).Distinct().ToList();
        var primarySource = sources[sourceIds[0]];
        // subsetWriter only knows how to carve pages out of an existing PDF (it opens SourcePath as one
        // — see PdfPigSubsetWriter) — a single-page scan or a single non-PDF image import also lands
        // here with sourceIds.Count == 1, and without this check would hand it a PNG, which PdfPig's
        // xref/trailer parser rejects outright ("could not find any xref tables or streams..."). Only a
        // genuinely single-PDF-source document can safely skip the (image-decoding) mergedWriter path.
        var isSingleSourcePdf = sourceIds.Count == 1 && ImportFormats.IsPdf(primarySource.SourcePath)
            && !planned.SourcePages.Any(page => primarySource.CorrectedPages.Contains(page.PageNumber));
        var id = Guid.NewGuid();
        paths.EnsureCreated();
        Directory.CreateDirectory(paths.DocumentPagesDirectory(id));
        // mergedWriter always writes real PDF bytes regardless of the source format, so the stored
        // file's name must carry a .pdf extension whenever it takes that path — every other IsPdf()
        // check in the app (PageManagementService, LatticeBuilder) trusts the extension, not the bytes.
        var originalName = isSingleSourcePdf
            ? Path.GetFileName(primarySource.SourcePath)
            : sourceIds.Count == 1
                ? Path.ChangeExtension(Path.GetFileName(primarySource.SourcePath), ".pdf")
                : $"Capture {id:N}.pdf";
        var output = paths.DocumentOriginalPath(id, originalName);

        var pageRows = new List<DocumentPage>();
        for (var index = 0; index < planned.SourcePages.Count; index++)
        {
            var sourcePage = planned.SourcePages[index];
            var source = sources[sourcePage.InputId];
            var raster = source.RasterPages.Single(page => page.PageNumber == sourcePage.PageNumber);
            var imagePath = Path.Combine(paths.DocumentPagesDirectory(id), $"{index + 1:D4}{Path.GetExtension(raster.ImagePath)}");
            File.Copy(raster.ImagePath, imagePath, overwrite: true);
            pageRows.Add(new DocumentPage { DocumentId = id, PageNumber = index + 1, SourcePageNumber = index + 1, ImagePath = imagePath, Width = raster.Width, Height = raster.Height, Dpi = raster.Dpi });
        }

        if (isSingleSourcePdf)
        {
            var sourcePageNumbers = planned.SourcePages
                .Select(page => primarySource.OriginalPageNumbers is { } map && map.TryGetValue(page.PageNumber, out var original) ? original : page.PageNumber)
                .ToList();
            await subsetWriter.WritePagesAsync(primarySource.SourcePath, sourcePageNumbers, output, cancellationToken).ConfigureAwait(false);
        }
        else
            await mergedWriter.WriteAsync(pageRows, output, cancellationToken).ConfigureAwait(false);

        var document = new CaptureDocument
        {
            Id = id,
            OriginalFileName = originalName,
            StoredPath = output,
            Source = primarySource.Source,
            BatchId = batchId,
            ProfileId = planned.Type?.Id,
            // A profile with zero document types (the built-in "None"/Unsorted profile, or a custom
            // one deliberately left empty) can never match a type — there are no index fields to
            // extract or validate, so there's nothing for a reviewer to complete and the document goes
            // straight to Ready. A profile that DOES define document types but failed to match this
            // particular document is a genuine classification miss and stays NeedsReview. When a type
            // IS matched, this gets overwritten by IndexFormat.StatusFor(...) below once its fields are
            // actually applied.
            Status = profile.DocumentTypes.Count == 0 ? DocumentStatus.Ready : DocumentStatus.NeedsReview,
            PageCount = pageRows.Count,
            ReviewNote = ReviewNote(planned, sources),
            ContentHash = sourceIds.Count == 1 ? primarySource.ContentHash : null,
            SourceImportId = sourceIds.Count == 1 ? primarySource.SourceImportId : null
        };
        await documents.SaveAsync(document, pageRows, cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>Builds the logical batch input before trigger-page removal. A consumed batch-start
    /// page is prepended to the first retained document, allowing ordinary barcode/zonal fields to
    /// read it just as they do when the page is kept. Distinct avoids presenting a kept trigger twice.</summary>
    private static (IReadOnlyList<PageLattice> Lattices, IReadOnlyList<DocumentPage> Pages)
        PrepareBatchExtractionInput(
            PlannedBatch batch,
            IReadOnlyDictionary<string, CaptureMaterializationSource> sources)
    {
        var sourcePages = new List<SourcePage>();
        if (batch.BoundaryPage is { } boundary)
            sourcePages.Add(boundary);
        if (batch.Documents.FirstOrDefault() is { } firstDocument)
            sourcePages.AddRange(firstDocument.SourcePages);

        var distinctPages = sourcePages.Distinct().ToList();
        var extractionDocumentId = Guid.NewGuid();
        var lattices = new List<PageLattice>(distinctPages.Count);
        var pages = new List<DocumentPage>(distinctPages.Count);
        for (var index = 0; index < distinctPages.Count; index++)
        {
            var sourcePage = distinctPages[index];
            var source = sources[sourcePage.InputId];
            var raster = source.RasterPages.Single(page => page.PageNumber == sourcePage.PageNumber);
            lattices.Add(Renumber(source.Lattices[sourcePage.PageNumber], index + 1));
            pages.Add(new DocumentPage
            {
                DocumentId = extractionDocumentId,
                PageNumber = index + 1,
                SourcePageNumber = sourcePage.PageNumber,
                ImagePath = raster.ImagePath,
                Width = raster.Width,
                Height = raster.Height,
                Dpi = raster.Dpi
            });
        }
        return (lattices, pages);
    }

    private async Task RunPostIndexStepsAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        IReadOnlyList<IndexValue> values,
        DocumentTypeDefinition documentType,
        CancellationToken cancellationToken)
    {
        var context = new PostIndexContext { Document = document, Pages = pages, DocumentIndexValues = values, Profile = documentType };
        foreach (var step in _postIndexSteps)
        {
            try { await step.RunAsync(context, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { Trace.TraceError($"Post-index step {step.GetType().Name} failed for document {document.Id}: {ex}"); }
        }
    }

    private static PageLattice Renumber(PageLattice source, int number) => new()
    {
        PageNumber = number,
        PixelWidth = source.PixelWidth,
        PixelHeight = source.PixelHeight,
        Dpi = source.Dpi,
        Source = source.Source,
        Words = source.Words
    };
}
