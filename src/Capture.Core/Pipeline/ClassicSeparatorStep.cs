using Capture.Core.Import;
using Capture.Core.Indexing;

namespace Capture.Core.Pipeline;

/// <summary>
/// Default <see cref="IPreIndexStep"/> — wraps <see cref="PageSeparator.Split"/> exactly as-is.
/// <see cref="PreIndexContext.ImportProfile"/> alone drives blank-page/barcode/every-N-pages splitting;
/// every resulting segment is tagged with the first candidate Indexing Profile (if any) — still
/// always 0-or-1 today, the future classification seam later.
/// </summary>
public sealed class ClassicSeparatorStep : IPreIndexStep
{
    private readonly IBarcodeDecoder? _barcodes;
    private readonly IBlankPageDetector? _blanks;

    public ClassicSeparatorStep(IBarcodeDecoder? barcodes = null, IBlankPageDetector? blanks = null)
    {
        _barcodes = barcodes;
        _blanks = blanks;
    }

    public Task<IReadOnlyList<ClassifiedSplit>> RunAsync(PreIndexContext context, CancellationToken cancellationToken = default)
    {
        var profile = context.CandidateProfiles.FirstOrDefault();
        var splits = context.ImportProfile is null
            ? [AllPages(context)]
            : PageSeparator.Split(context.Pages, context.ImportProfile, _barcodes, _blanks);

        var classified = splits
            .Select(split => new ClassifiedSplit
            {
                Profile = profile,
                SourcePages = split.SourcePages,
                SeparatorValues = split.SeparatorValues
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ClassifiedSplit>>(classified);
    }

    private static PageSplit AllPages(PreIndexContext context)
    {
        var split = new PageSplit();
        foreach (var page in context.Pages)
            split.SourcePages.Add(page.PageNumber);
        return split;
    }
}
