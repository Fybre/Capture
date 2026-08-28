namespace Capture.Core.Profiles;

public static class ValuePatterns
{
    public static string For(FieldFormat format) => format switch
    {
        FieldFormat.Integer => @"-?\d+",
        FieldFormat.Money => @"\$?\s*\d{1,3}(?:,\d{3})*(?:\.\d{2})?",
        FieldFormat.Date => @"\d{1,2}[./-]\d{1,2}[./-]\d{2,4}",
        FieldFormat.DateTime => @"\d{1,2}[./-]\d{1,2}[./-]\d{2,4}(?:\s+\d{1,2}:\d{2})?",
        FieldFormat.Boolean => @"(?i)true|false|yes|no",
        _ => @"\S+"
    };
}
