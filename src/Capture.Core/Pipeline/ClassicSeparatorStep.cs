using Capture.Core.Import;
using Capture.Core.Indexing;

namespace Capture.Core.Pipeline;

/// <summary>
/// Default <see cref="IPreIndexStep"/> — wraps today's single-profile <see cref="PageSeparator.Split"/>
/// exactly as-is. Preserves current behavior: the first candidate profile (if any) drives blank-page
/// and barcode-separator splitting, and every resulting segment is tagged with that same profile.
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
        var splits = profile is null
            ? [AllPages(context)]
            : PageSeparator.Split(context.Pages, profile, _barcodes, _blanks);

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
