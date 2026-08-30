namespace Capture.Therefore;

public sealed record ThereforeStream(int StreamNo, string FileName, string FileDataBase64JSON);

/// <summary>Fields are top-level request members for <c>CreateDocument</c> — no wrapper object.
/// <see cref="IndexDataItems"/> entries come from <see cref="ThereforeIndexData"/>'s builders, each a
/// dictionary with exactly one populated typed key (e.g. <c>{"StringIndexData": {...}}</c>) — kept as
/// plain dictionaries rather than a polymorphic C# type hierarchy since that's what actually
/// round-trips through <see cref="System.Text.Json"/> without a custom converter.</summary>
public sealed class ThereforeCreateDocumentRequest
{
    public required int CategoryNo { get; init; }
    public required IReadOnlyList<object> IndexDataItems { get; init; }
    public IReadOnlyList<ThereforeStream>? Streams { get; init; }
    public bool DoFillDependentFields { get; init; } = true;

    /// <summary>4 = "No check" (see the Therefore skill's pitfall #33) — deliberately bypasses the
    /// category's own auto-append/duplicate rules rather than silently rejecting a scan-triggered
    /// export the category wasn't configured to expect.</summary>
    public int WithAutoAppendMode { get; init; } = 4;

    public string? CheckInComments { get; init; }
}

public sealed record ThereforeCreateDocumentResult(int DocNo);

/// <summary>Builders for the typed <c>IndexDataItems</c> entries <c>PreprocessIndexData</c>/
/// <c>CreateDocument</c> expect — see <see cref="ThereforeFieldType"/> for the field-type table these
/// map from.</summary>
public static class ThereforeIndexData
{
    public static object String(int fieldNo, string fieldName, string? value) =>
        new { StringIndexData = new { FieldNo = fieldNo, FieldName = fieldName, DataValue = value ?? string.Empty } };

    public static object Int(int fieldNo, string fieldName, long value) =>
        new { IntIndexData = new { FieldNo = fieldNo, FieldName = fieldName, DataValue = value } };

    public static object Money(int fieldNo, string fieldName, decimal value) =>
        new { MoneyIndexData = new { FieldNo = fieldNo, FieldName = fieldName, DataValue = value } };

    public static object Date(int fieldNo, string fieldName, DateTime value) =>
        new { DateIndexData = new { FieldNo = fieldNo, FieldName = fieldName, DataValue = value } };

    public static object Logical(int fieldNo, string fieldName, bool value) =>
        new { LogicalIndexData = new { FieldNo = fieldNo, FieldName = fieldName, DataValue = value } };
}
