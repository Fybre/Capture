using Capture.Core.Profiles;
using Capture.LocalAi;

namespace Capture.Tests;

public class LocalExtractGrammarTests
{
    [Fact]
    public void Build_matches_the_spike_validated_grammar_shape_for_three_fields()
    {
        var a = new IndexField { Id = Guid.Parse("11111111-1111-1111-1111-111111111111") };
        var b = new IndexField { Id = Guid.Parse("22222222-2222-2222-2222-222222222222") };
        var c = new IndexField { Id = Guid.Parse("33333333-3333-3333-3333-333333333333") };

        var grammar = LocalExtractGrammar.Build([a, b, c]);

        // Same skeletal shape as the local-llm-spike project's hand-written extract.gbnf (which
        // achieved 8/8 JSON validity), just with GUID keys instead of fixed field names. Each key
        // literal is itself JSON-quoted (\"...\") — a GBNF literal's own delimiting quotes never
        // appear in the generated text, so the JSON quotes the parser expects around the key have to
        // be part of the literal's content, escaped, not just the GBNF token delimiters.
        var expected = string.Join(
            "\n",
            "root ::= \"{\" ws \"\\\"values\\\"\" ws \":\" ws \"{\" ws " +
            "\"\\\"11111111111111111111111111111111\\\"\" ws \":\" ws field ws \",\" ws " +
            "\"\\\"22222222222222222222222222222222\\\"\" ws \":\" ws field ws \",\" ws " +
            "\"\\\"33333333333333333333333333333333\\\"\" ws \":\" ws field ws \"}\" ws \"}\"",
            "field ::= \"{\" ws \"\\\"value\\\"\" ws \":\" ws string ws \",\" ws \"\\\"confidence\\\"\" ws \":\" ws confidence ws \"}\"",
            "confidence ::= \"100\" | [1-9] [0-9] | [0-9]",
            "string ::= \"\\\"\" ([^\"\\\\\\x7F\\x00-\\x1F] | \"\\\\\" ([\"\\\\bfnrt] | \"u\" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]))* \"\\\"\"",
            "ws ::= [ \\t\\n]*");

        Assert.Equal(expected, grammar);
    }

    [Fact]
    public void Build_uses_the_undashed_guid_format_matching_AiExtractPrompt()
    {
        var field = new IndexField { Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") };

        var grammar = LocalExtractGrammar.Build([field]);

        // The key literal must produce a JSON-quoted string in the model's OUTPUT (e.g.
        // "aaaa...eeee"), not just contain the bare hex digits inside GBNF's own token-delimiting
        // quotes — a real regression: the bare form parses as an invalid, unquoted JSON object key
        // and AiExtractPrompt.Parse silently drops the whole response.
        Assert.Contains("\"\\\"aaaaaaaabbbbccccddddeeeeeeeeeeee\\\"\"", grammar);
        Assert.DoesNotContain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", grammar);
    }

    [Fact]
    public void Build_handles_a_single_field_with_no_trailing_separator()
    {
        var field = new IndexField { Id = Guid.Parse("11111111-1111-1111-1111-111111111111") };

        var grammar = LocalExtractGrammar.Build([field]);
        var rootLine = grammar.Split('\n')[0];

        Assert.DoesNotContain("\",\"", rootLine);
        Assert.Contains("\"\\\"11111111111111111111111111111111\\\"\" ws \":\" ws field ws \"}\" ws \"}\"", rootLine);
    }

    [Fact]
    public void Build_produces_balanced_braces_and_quotes_for_many_fields()
    {
        var fields = Enumerable.Range(0, 8).Select(_ => new IndexField { Id = Guid.NewGuid() }).ToList();

        var grammar = LocalExtractGrammar.Build(fields);

        Assert.Equal(grammar.Count(ch => ch == '{'), grammar.Count(ch => ch == '}'));
        foreach (var field in fields)
            Assert.Contains(field.Id.ToString("N"), grammar);
    }
}
