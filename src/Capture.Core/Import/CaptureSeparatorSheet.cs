namespace Capture.Core.Import;

/// <summary>Which preprinted Capture separator sheet a <see cref="SeparationStrategyType.CaptureSeparator"/>
/// rule responds to. Batch and document sheets carry different barcode values so a profile using both
/// can tell them apart — a batch sheet never also starts a new document by accident.</summary>
public enum CaptureSeparatorKind
{
    Document = 0,
    Batch = 1
}

/// <summary>The fixed barcode values printed on Capture's own separator sheets (More ▸ Separator sheets)
/// and matched by <see cref="SeparationStrategyType.CaptureSeparator"/> rules, so a user can print a sheet
/// and pick a rule without designing or configuring a barcode themselves. Each sheet carries the same
/// value as both a QR code and a Code 128 barcode — whichever survives the scan is enough. These values
/// are a stable contract with sheets already printed: never change them.</summary>
public static class CaptureSeparatorSheet
{
    public const string DocumentValue = "CAPSEP-DOC";
    public const string BatchValue = "CAPSEP-BATCH";

    public static string ValueFor(CaptureSeparatorKind kind) =>
        kind == CaptureSeparatorKind.Batch ? BatchValue : DocumentValue;

    public static string TitleFor(CaptureSeparatorKind kind) =>
        kind == CaptureSeparatorKind.Batch ? "Batch separator" : "Document separator";

    public static bool Matches(CaptureSeparatorKind kind, string? barcodeText) =>
        string.Equals(barcodeText?.Trim(), ValueFor(kind), StringComparison.OrdinalIgnoreCase);
}
