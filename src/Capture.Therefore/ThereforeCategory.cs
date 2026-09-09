namespace Capture.Therefore;

/// <summary>Mirrors the confirmed live <c>CategoryFields[].FieldType</c> values (verified against a
/// real tenant — see the "Therefore export support" plan notes). <see cref="Label"/>,
/// <see cref="NumericCounter"/>, and <see cref="TextCounter"/> are excluded from the category picker
/// entirely — Label is a read-only on-screen caption for its neighbor field (confirmed by
/// `ColName: ""` and a shared `Caption`), and the counters are server-generated sequence fields.</summary>
public enum ThereforeFieldType
{
    String = 1,
    Int = 2,
    Date = 3,
    Label = 4,
    Money = 5,
    Logical = 6,
    NumericCounter = 8,
    TextCounter = 9,
    Table = 10,
    Custom = 99
}

public sealed record ThereforeTreeNode(
    int ItemNo,
    int ItemType,
    string Name,
    IReadOnlyList<ThereforeTreeNode> Children)
{
    /// <summary>Only <c>ItemType == 2</c> nodes are queryable/document-bearing categories — folders
    /// (1) and case definitions (3) are containers only.</summary>
    public bool IsCategory => ItemType == 2;
}

/// <param name="IndexDataFieldName">The machine identifier writes use as "FieldName" in a typed
/// IndexData item — distinct from <paramref name="Caption"/> (the human label). Confirmed live: e.g.
/// Caption "Invoice No" pairs with IndexDataFieldName "Invoice_No".</param>
public sealed record ThereforeCategoryField(
    int FieldNo,
    string Caption,
    string IndexDataFieldName,
    ThereforeFieldType FieldType,
    bool Mandatory,
    bool IsSingleKeyword,
    bool IsMultipleKeyword);

public sealed record ThereforeCategoryInfo(
    int CategoryNo,
    string Name,
    IReadOnlyList<ThereforeCategoryField> Fields);
