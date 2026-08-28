namespace Capture.Core.Import;

public sealed class PageSplit
{
    public List<int> SourcePages { get; } = [];

    public Dictionary<Guid, string> SeparatorValues { get; } = [];
}
