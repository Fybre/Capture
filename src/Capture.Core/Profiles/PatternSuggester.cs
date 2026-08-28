using System.Text;
using System.Text.RegularExpressions;

namespace Capture.Core.Profiles;

public static class PatternSuggester
{
    public static string ForKey(string text)
    {
        text = CollapseWhitespace(text);
        if (text.Length == 0)
            return string.Empty;

        var pattern = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                pattern.Append(@"\s*");
                continue;
            }

            if (IsWordChar(text[i]))
            {
                var start = i;
                while (i < text.Length && IsWordChar(text[i]))
                    i++;
                pattern.Append(Regex.Escape(text[start..i]));
                continue;
            }

            var punct = text[i];
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            pattern.Append(@"\s*");
            pattern.Append(Regex.Escape(punct.ToString()));
            pattern.Append('?');
        }

        return pattern.ToString();
    }

    public static string ForValue(string text, FieldFormat format)
    {
        if (format != FieldFormat.String)
            return ValuePatterns.For(format);

        var sample = CollapseWhitespace(text);
        if (sample.Length == 0)
            return ValuePatterns.For(FieldFormat.String);

        if (Regex.IsMatch(sample, @"^-?\d+$"))
            return ValuePatterns.For(FieldFormat.Integer);
        if (Regex.IsMatch(sample, @"^\$?\s*\d{1,3}(?:,\d{3})*(?:\.\d{2})?$"))
            return ValuePatterns.For(FieldFormat.Money);
        if (Regex.IsMatch(sample, @"^\d{1,2}[./-]\d{1,2}[./-]\d{2,4}$"))
            return ValuePatterns.For(FieldFormat.Date);
        if (Regex.IsMatch(sample, @"^[A-Za-z0-9]+$"))
            return @"[A-Za-z0-9]+";
        if (Regex.IsMatch(sample, @"^[A-Za-z0-9][A-Za-z0-9\-_/]*$"))
            return @"[A-Za-z0-9][A-Za-z0-9\-_\/]*";
        if (!sample.Contains(' '))
            return @"\S+";
        return @".+";
    }

    private static bool IsWordChar(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var inSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inSpace)
                {
                    builder.Append(' ');
                    inSpace = true;
                }
                continue;
            }

            inSpace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }
}
