using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

/// <summary>Evaluates each value's <see cref="IndexValue.Condition"/> against the other fields and records
/// the result in <see cref="IndexValue.IsConditionInactive"/>. Run after extraction and after every review
/// edit, since a change to one field can switch others on or off.</summary>
public static class FieldConditions
{
    /// <summary>Updates <paramref name="values"/>. Rules look up fields among <paramref name="values"/>
    /// first, then <paramref name="outerValues"/> (a document's batch fields). Returns true when any
    /// field changed state or was cleared.</summary>
    public static bool Apply(IReadOnlyList<IndexValue> values, IReadOnlyList<IndexValue>? outerValues = null)
    {
        var changed = false;
        foreach (var value in values.Where(value => value.Condition is not { HasRules: true } && value.IsConditionInactive))
        {
            value.IsConditionInactive = false;
            changed = true;
        }

        // A field that tests an inactive field sees it as empty, so a chain (A depends on B, which
        // depends on C) settles over a few passes. The pass limit stops a circular set of rules from
        // looping forever; such a set just ends in whatever state the last pass left it.
        for (var pass = 0; pass <= values.Count; pass++)
        {
            var passChanged = false;
            foreach (var value in values)
            {
                if (value.Condition is not { HasRules: true } condition)
                    continue;
                var inactive = !IsActive(condition, name => Resolve(name, value, values, outerValues));
                if (value.IsConditionInactive == inactive)
                    continue;
                value.IsConditionInactive = inactive;
                passChanged = true;
            }

            if (!passChanged)
                break;
            changed = true;
        }

        foreach (var value in values.Where(value => value.IsConditionInactive && value.Condition?.ClearWhenInactive == true))
        {
            if (value.Value.Length == 0)
                continue;
            value.Value = string.Empty;
            value.Confidence = 0;
            value.ValidationError = null;
            changed = true;
        }

        return changed;
    }

    /// <summary>Whether <paramref name="condition"/> matches. <paramref name="resolve"/> finds a field
    /// by name, returning null when the document has no such field (which then counts as empty).</summary>
    public static bool IsActive(FieldCondition? condition, Func<string, IndexValue?> resolve)
    {
        if (condition is not { HasRules: true })
            return true;

        var rules = condition.Rules.Where(rule => !string.IsNullOrWhiteSpace(rule.FieldName)).ToList();
        return condition.Match == ConditionMatch.Any
            ? rules.Any(rule => Matches(rule, resolve(rule.FieldName.Trim())))
            : rules.All(rule => Matches(rule, resolve(rule.FieldName.Trim())));
    }

    public static bool Matches(FieldConditionRule rule, IndexValue? field)
    {
        var value = field is null || field.IsConditionInactive ? string.Empty : field.Value;
        return rule.Operator switch
        {
            ConditionOperator.IsFilledIn => !string.IsNullOrWhiteSpace(value),
            ConditionOperator.IsEmpty => string.IsNullOrWhiteSpace(value),
            ConditionOperator.IsNotOneOf => !IsOneOf(value, rule.Values, field),
            _ => IsOneOf(value, rule.Values, field)
        };
    }

    private static bool IsOneOf(string value, IReadOnlyList<string> candidates, IndexValue? field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        // A lookup field stores the option's value; also accept its display label, so a rule written
        // against either still matches.
        var label = field?.LookupOptions.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal))?.Key;
        return candidates.Any(candidate =>
            Same(value, candidate, field?.Format) || (label is not null && Same(label, candidate, null)));
    }

    private static bool Same(string value, string candidate, FieldFormat? format)
    {
        if (format == FieldFormat.Boolean
            && IndexFormat.TryParseBoolean(value, out var left)
            && IndexFormat.TryParseBoolean(candidate, out var right))
            return left == right;
        return string.Equals(value.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static IndexValue? Resolve(string name, IndexValue self, IReadOnlyList<IndexValue> values, IReadOnlyList<IndexValue>? outerValues) =>
        values.FirstOrDefault(item => !ReferenceEquals(item, self) && string.Equals(item.FieldName, name, StringComparison.OrdinalIgnoreCase))
        ?? outerValues?.FirstOrDefault(item => string.Equals(item.FieldName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A plain-English description of <paramref name="condition"/>, e.g. "Document Kind is
    /// Invoice or Credit Note, and Amount is filled in". Empty when there are no rules.</summary>
    public static string Describe(FieldCondition? condition, Func<string, IReadOnlyList<LookupOption>>? optionsFor = null)
    {
        if (condition is not { HasRules: true })
            return string.Empty;

        var parts = condition.Rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.FieldName))
            .Select(rule => DescribeRule(rule, optionsFor?.Invoke(rule.FieldName) ?? []))
            .ToList();
        return JoinList(parts, condition.Match == ConditionMatch.Any ? "or" : "and");
    }

    private static string DescribeRule(FieldConditionRule rule, IReadOnlyList<LookupOption> options)
    {
        var name = rule.FieldName.Trim();
        var values = rule.Values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal))?.Key is { Length: > 0 } key ? key : value.Trim())
            .ToList();
        return rule.Operator switch
        {
            ConditionOperator.IsFilledIn => $"{name} is filled in",
            ConditionOperator.IsEmpty => $"{name} is empty",
            ConditionOperator.IsNotOneOf => values.Count == 0 ? $"{name} is anything" : $"{name} is not {JoinList(values, "or")}",
            _ => values.Count == 0 ? $"{name} is (no values chosen)" : $"{name} is {JoinList(values, "or")}"
        };
    }

    private static string JoinList(IReadOnlyList<string> items, string conjunction) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} {conjunction} {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, {conjunction} {items[^1]}"
    };
}
