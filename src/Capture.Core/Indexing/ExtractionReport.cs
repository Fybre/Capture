namespace Capture.Core.Indexing;

/// <summary>What happened while a document's fields were read — which AI read which fields, and
/// anything that went wrong — collected for the document's history. Pass one in
/// <see cref="DefaultValueContext.Report"/> to have it filled.</summary>
public sealed class ExtractionReport
{
    /// <summary>What was done, e.g. "AI (gpt-4o-mini at api.openai.com) filled 3 of 4 fields: …".</summary>
    public List<string> Notes { get; } = [];

    /// <summary>What failed, e.g. an AI call or a script.</summary>
    public List<string> Problems { get; } = [];
}
