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
}

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
    IEnumerable<IPostIndexStep>? postIndexSteps = null)
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

                if (!batchValuesSaved)
                {
                    var batchNumber = await documents.GetBatchNumberAsync(batch.Id, cancellationToken).ConfigureAwait(false);
                    var (batchLattices, batchPages) = PrepareBatchExtractionInput(plannedBatch, sources);
                    var batchValues = await applicator.ApplyAsync(
                        profile.Batch.Fields, profile.Batch.Scripts, profile.Batch.SharedScriptSource,
                        batchLattices, profile.Name,
                        context: new DefaultValueContext
                        {
                            BatchNumber = batchNumber,
                            ScriptScope = Capture.Core.Scripting.ScriptScopeKind.Batch,
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
                    var document = await MaterializeDocumentAsync(profile, plannedDocument, batch.Id, sources, cancellationToken).ConfigureAwait(false);
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
                        var values = await applicator.ApplyAsync(
                            type.Fields, type.Scripts, type.SharedScriptSource, documentLattices, type.Name,
                            context: new DefaultValueContext
                            {
                                BatchNumber = batchNumber,
                                DocumentNumber = documentNumber,
                                ScriptScope = Capture.Core.Scripting.ScriptScopeKind.Document,
                                DocumentType = type.Name,
                                BatchValues = materializedBatchValues,
                                TriggerMatches = plannedDocument.BoundaryMatches.Select(match => new Capture.Core.Scripting.ScriptTriggerMatchInfo(match.RuleId, match.CapturedValue, match.Confidence)).ToList()
                            },
                            pages: documentPages, existingValues: boundaryValues,
                            document: document, cancellationToken: cancellationToken).ConfigureAwait(false);
                        await indexes.SaveAsync(document.Id, values, cancellationToken).ConfigureAwait(false);
                        document.Status = IndexFormat.StatusFor(materializedBatchValues.Concat(values), type.AutoReadyThreshold);
                        await documents.UpdateAsync(document, cancellationToken).ConfigureAwait(false);
                        await RunPostIndexStepsAsync(document, documentPages, values, type, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    progress?.Report(new(createdDocuments.Count, total, $"Created {document.OriginalFileName}"));
                }

                if (!batchValuesSaved)
                    await indexes.SaveBatchAsync(batch.Id, plannedBatch.CapturedValues, cancellationToken).ConfigureAwait(false);

            }

            if (closeBatchWhenFinished && openBatches is not null)
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
        var id = Guid.NewGuid();
        paths.EnsureCreated();
        Directory.CreateDirectory(paths.DocumentPagesDirectory(id));
        var originalName = sourceIds.Count == 1 ? Path.GetFileName(primarySource.SourcePath) : $"Capture {id:N}.pdf";
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

        if (sourceIds.Count == 1)
            await subsetWriter.WritePagesAsync(primarySource.SourcePath, planned.SourcePages.Select(page => page.PageNumber).ToList(), output, cancellationToken).ConfigureAwait(false);
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
            Status = DocumentStatus.NeedsReview,
            PageCount = pageRows.Count,
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
