using System.Text;
using Capture.Core.Profiles;

namespace Capture.LocalAi;

/// <summary>Generates a GBNF grammar that forces the local model's output into the exact JSON shape
/// <c>AiExtractPrompt.Parse</c> already understands, for a given profile's field set. Grammar-
/// constrained decoding was the decisive fix for JSON reliability found by the local-llm-spike
/// project — prompt wording alone reduced but never eliminated malformed/runaway output, while a
/// grammar makes invalid JSON structurally impossible regardless of what the model generates. The
/// spike's grammar (extract.gbnf) was hand-written for one fixed 3-field test; this generalizes the
/// same shape to any field list.</summary>
public static class LocalExtractGrammar
{
    public static string Build(IReadOnlyList<IndexField> fields)
    {
        var root = new StringBuilder();
        root.Append("root ::= \"{\" ws \"\\\"values\\\"\" ws \":\" ws \"{\" ws ");
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                root.Append("\",\" ws ");
            root.Append(GbnfQuotedJsonKeyLiteral(fields[i].Id.ToString("N")));
            root.Append(" ws \":\" ws field ws ");
        }
        root.Append("\"}\" ws \"}\"");

        return string.Join(
            "\n",
            root.ToString(),
            "field ::= \"{\" ws \"\\\"value\\\"\" ws \":\" ws string ws \",\" ws \"\\\"confidence\\\"\" ws \":\" ws confidence ws \"}\"",
            "confidence ::= \"100\" | [1-9] [0-9] | [0-9]",
            "string ::= \"\\\"\" ([^\"\\\\\\x7F\\x00-\\x1F] | \"\\\\\" ([\"\\\\bfnrt] | \"u\" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]))* \"\\\"\"",
            "ws ::= [ \\t\\n]*");
    }

    // Produces the GBNF source for a literal that matches/outputs a JSON-quoted key, e.g. for id
    // 6b3354... this returns the 36-character GBNF token "\"6b3354...\"" (with the backslash-escaped
    // quotes it takes to make the *generated JSON* contain literal " characters around the id — GBNF's
    // own delimiting quotes never appear in the output, so they don't count). Field ids are GUIDs
    // ("N" format: 32 hex chars, no quotes/backslashes possible), but escape defensively rather than
    // assuming — a literal grammar rule must never let a raw quote/backslash break out of the string
    // token it's embedded in.
    private static string GbnfQuotedJsonKeyLiteral(string key) =>
        $"\"\\\"{EscapeGbnfLiteral(key)}\\\"\"";

    private static string EscapeGbnfLiteral(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
