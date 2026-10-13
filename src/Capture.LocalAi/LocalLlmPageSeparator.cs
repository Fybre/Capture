using System.Text;
using Capture.Core.Indexing;

namespace Capture.LocalAi;

/// <summary>AI document separation on the local model: text only (it can't read images), one page at
/// a time (there's one model), and a grammar so every answer is the verdict JSON.</summary>
public sealed class LocalLlmPageSeparator : AiPageSeparatorBase
{
    private const int MaxTokens = 240;
    private readonly LocalLlmExtractor _model;

    public LocalLlmPageSeparator(LocalLlmExtractor model)
    {
        _model = model;
    }

    public override bool IsConfigured => _model.IsConfigured;

    protected override bool IncludeImages => false;

    protected override int MaxParallel => 1;

    protected override Task<string> CompleteAsync(AiSeparationRequest request, CancellationToken cancellationToken) =>
        _model.CompleteAsync(request.SystemMessage, request.UserText, Grammar(TypeIds(request.SystemMessage)), MaxTokens, cancellationToken);

    // The type ids are listed in the system message as id="…"; reading them back keeps the grammar in
    // step with the prompt without passing the type list separately.
    private static IReadOnlyList<string> TypeIds(string systemMessage)
    {
        var ids = new List<string>();
        const string marker = "- id=\"";
        var at = 0;
        while ((at = systemMessage.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
        {
            at += marker.Length;
            var end = systemMessage.IndexOf('"', at);
            if (end < 0)
                break;
            ids.Add(systemMessage[at..end]);
            at = end;
        }

        return ids;
    }

    internal static string Grammar(IReadOnlyList<string> typeIds)
    {
        var types = new StringBuilder();
        foreach (var id in typeIds.Distinct())
        {
            if (types.Length > 0)
                types.Append(" | ");
            types.Append("\"\\\"").Append(id.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\\\"\"");
        }

        return string.Join(
            "\n",
            "root ::= \"{\" ws \"\\\"is_new_document\\\"\" ws \":\" ws boolean ws \",\" ws \"\\\"document_type\\\"\" ws \":\" ws type ws \",\" ws \"\\\"suggested_type\\\"\" ws \":\" ws string ws \",\" ws \"\\\"confidence\\\"\" ws \":\" ws confidence ws \",\" ws \"\\\"reasoning\\\"\" ws \":\" ws string ws \"}\"",
            "boolean ::= \"true\" | \"false\"",
            "type ::= " + (types.Length > 0 ? types.ToString() : "\"\\\"other\\\"\""),
            "confidence ::= \"100\" | [1-9] [0-9] | [0-9]",
            "string ::= \"\\\"\" ([^\"\\\\\\x7F\\x00-\\x1F] | \"\\\\\" ([\"\\\\bfnrt] | \"u\" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]))* \"\\\"\"",
            "ws ::= [ \\t\\n]*");
    }
}
