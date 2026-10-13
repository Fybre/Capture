namespace Capture.Core.Indexing;

/// <summary>The page loop shared by every AI provider: blank pages are skipped, pages run in parallel up
/// to <see cref="MaxParallel"/>, and a page whose call fails continues the previous document with
/// confidence 0 so it is flagged for review instead of failing the capture.</summary>
public abstract class AiPageSeparatorBase : IAiPageSeparator
{
    public abstract bool IsConfigured { get; }

    /// <summary>Whether this provider sends page images.</summary>
    protected abstract bool IncludeImages { get; }

    protected virtual int MaxParallel => 4;

    /// <summary>Sends one request and returns the model's raw answer.</summary>
    protected abstract Task<string> CompleteAsync(AiSeparationRequest request, CancellationToken cancellationToken);

    public async Task<IReadOnlyList<AiPageVerdict>> AnalyzeAsync(
        IReadOnlyList<AiSeparationPage> pages,
        IReadOnlyList<AiSeparationType>? types,
        CancellationToken cancellationToken = default)
    {
        var results = new AiPageVerdict[pages.Count];
        if (pages.Count == 0)
            return results;

        var includeImages = IncludeImages;
        // Splitting only, the AI is still asked what each page is, with no types to choose from, and
        // the answer's type is dropped: on the local model, asking "is this a new document?" alone
        // merged unrelated documents far more often than asking it to identify the page as well.
        var promptTypes = types ?? [];
        using var gate = new SemaphoreSlim(Math.Max(1, MaxParallel));
        await Task.WhenAll(pages.Select(async (page, index) =>
        {
            var mustStart = index == 0 || page.ForceNew;
            if (page.IsBlank)
            {
                results[index] = new AiPageVerdict(mustStart, null, 100, "Blank page.");
                return;
            }

            // Splitting only, there's nothing to ask about a page that starts a document regardless.
            if (types is null && mustStart)
            {
                results[index] = new AiPageVerdict(true, null, 100, "First page.");
                return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var request = AiSeparationPrompt.ForPage(pages, index, promptTypes, includeImages);
                var raw = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                var verdict = AiSeparationPrompt.Parse(raw, promptTypes);
                if (types is null)
                    verdict = verdict with { TypeId = null, SuggestedType = null };
                results[index] = mustStart ? verdict with { StartsNewDocument = true } : verdict;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                results[index] = new AiPageVerdict(mustStart, null, 0, $"AI failed: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return results;
    }
}
