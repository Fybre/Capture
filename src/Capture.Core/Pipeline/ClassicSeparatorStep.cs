using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;

namespace Capture.Core.Pipeline;

/// <summary>
/// Default <see cref="IPreIndexStep"/> — wraps <see cref="PageSeparator.SplitAsync"/> exactly as-is.
/// <see cref="PreIndexContext.ImportProfile"/> alone drives splitting (its <c>Strategies</c> list,
/// combined via <c>MatchMode</c>); every resulting segment is tagged with the first candidate Indexing
/// Profile (if any) — still always 0-or-1 today, the future classification seam later.
/// </summary>
public sealed class ClassicSeparatorStep : IPreIndexStep
{
    private readonly IBarcodeDecoder? _barcodes;
    private readonly IBlankPageDetector? _blanks;
    private readonly ILatticeBuilder? _latticeBuilder;

    public ClassicSeparatorStep(IBarcodeDecoder? barcodes = null, IBlankPageDetector? blanks = null, ILatticeBuilder? latticeBuilder = null)
    {
        _barcodes = barcodes;
        _blanks = blanks;
        _latticeBuilder = latticeBuilder;
    }

    public async Task<IReadOnlyList<ClassifiedSplit>> RunAsync(PreIndexContext context, CancellationToken cancellationToken = default)
    {
        var profile = context.CandidateProfiles.FirstOrDefault();
        IReadOnlyList<PageSplit> splits;
        if (context.ImportProfile is null)
        {
            splits = [AllPages(context)];
        }
        else
        {
            var latticeProvider = _latticeBuilder is null
                ? null
                : PageLatticeProviderFactory.Create(_latticeBuilder, context.SourcePath);
            splits = await PageSeparator.SplitAsync(context.Pages, context.ImportProfile, _barcodes, _blanks, latticeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        return splits
            .Select(split => new ClassifiedSplit
            {
                Profile = profile,
                SourcePages = split.SourcePages,
                SeparatorValues = split.SeparatorValues
            })
            .ToList();
    }

    private static PageSplit AllPages(PreIndexContext context)
    {
        var split = new PageSplit();
        foreach (var page in context.Pages)
            split.SourcePages.Add(page.PageNumber);
        return split;
    }
}
