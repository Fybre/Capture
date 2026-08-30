using System.Globalization;
using System.Text;

namespace Capture.Core.Indexing;

/// <summary>Evaluates a Text field's <c>DefaultValueTemplate</c> — plain text passes through
/// unchanged; <c>{Token}</c> placeholders are resolved against a <see cref="DefaultValueContext"/>.
/// <c>{{</c>/<c>}}</c> produce a literal brace. An unterminated <c>{</c> (no matching <c>}</c>) is
/// passed through literally rather than throwing — a typo in a template must never break indexing.</summary>
public static class DefaultValueTemplateEvaluator
{
    public static bool TryEvaluate(
        string? template,
        DefaultValueContext context,
        out string value,
        out string? validationError)
    {
        try
        {
            value = Evaluate(template, context);
            validationError = null;
            return true;
        }
        catch (FormatException)
        {
            value = string.Empty;
            validationError = "Invalid default value format";
            return false;
        }
    }

    public static string Evaluate(string? template, DefaultValueContext context)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        var result = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var ch = template[i];
            if (ch == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    result.Append('{');
                    i += 2;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    result.Append(template, i, template.Length - i);
                    break;
                }

                result.Append(ResolveToken(template[(i + 1)..close], context));
                i = close + 1;
                continue;
            }

            if (ch == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                result.Append('}');
                i += 2;
                continue;
            }

            result.Append(ch);
            i++;
        }

        return result.ToString();
    }

    private static string ResolveToken(string token, DefaultValueContext context)
    {
        var separator = token.IndexOf('|');
        var name = (separator >= 0 ? token[..separator] : token).Trim();
        var param = separator >= 0 ? token[(separator + 1)..] : null;

        if (string.Equals(name, "Doc#", StringComparison.OrdinalIgnoreCase))
            return FormatCounter(context.DocumentNumber, param);
        if (string.Equals(name, "Batch#", StringComparison.OrdinalIgnoreCase))
            return FormatCounter(context.BatchNumber, param);
        if (string.Equals(name, "Date", StringComparison.OrdinalIgnoreCase))
            return FormatTimestamp(context.Timestamp, param, "yyyy-MM-dd");
        if (string.Equals(name, "Time", StringComparison.OrdinalIgnoreCase))
            return FormatTimestamp(context.Timestamp, param, "HH:mm:ss");
        if (string.Equals(name, "ProfileName", StringComparison.OrdinalIgnoreCase))
            return context.ProfileName ?? string.Empty;

        // Anything else is a field-name reference. The caller (ProfileApplicator.ApplyDefaults) omits
        // any field that itself carries a default from this dictionary, so a template that tries to
        // chain off another default simply resolves empty here — no cycle detection needed.
        return context.Fields.TryGetValue(name, out var value) ? value : string.Empty;
    }

    private static string FormatCounter(int value, string? format) =>
        string.IsNullOrEmpty(format)
            ? value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(format, CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset timestamp, string? format, string defaultFormat) =>
        timestamp.DateTime.ToString(string.IsNullOrEmpty(format) ? defaultFormat : format, CultureInfo.InvariantCulture);
}
