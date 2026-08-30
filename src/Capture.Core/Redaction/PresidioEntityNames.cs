namespace Capture.Core.Redaction;

/// <summary>A labeled cluster of entity type codes shown together in the redaction-set editor.</summary>
public sealed record EntityGroup(string Name, IReadOnlyList<string> Entities);

/// <summary>Friendly display names for Presidio's entity type codes — used anywhere a code like
/// "US_SSN" needs to read as "SSN" to a reviewer, e.g. the redaction review list, the "Redact" action's
/// tooltip, and the profile designer's entity checklist.</summary>
public static class PresidioEntityNames
{
    /// <summary>Every entity type offered wherever a user picks what to detect — the profile designer's
    /// Redaction checklist and the Inbox's manual "Redact" inline picker. Mirrors what the bundled
    /// Presidio sidecar's default <c>AnalyzerEngine</c> actually loads for English (its
    /// <c>RecognizerRegistry.load_predefined_recognizers()</c> pulls in every locale-agnostic and
    /// country-specific recognizer, not just the US-centric ones — including Australia's AU_TFN/
    /// AU_MEDICARE/AU_ABN/AU_ACN) — so nothing the sidecar can actually find is hidden from the picker.</summary>
    public static readonly IReadOnlyList<string> StandardEntityTypes =
    [
        "PERSON", "EMAIL_ADDRESS", "PHONE_NUMBER", "LOCATION", "DATE_TIME", "IP_ADDRESS", "URL", "ORGANIZATION", "NRP",
        "CREDIT_CARD", "IBAN_CODE", "CRYPTO",
        "US_SSN", "US_DRIVER_LICENSE", "US_BANK_NUMBER", "US_PASSPORT", "MEDICAL_LICENSE",
        "AU_TFN", "AU_MEDICARE", "AU_ABN", "AU_ACN",
    ];

    /// <summary>Groups <see cref="StandardEntityTypes"/> for the redaction-set editor's checklist, so a
    /// custom set is built by toggling a handful of labeled clusters rather than scanning a flat list of
    /// 21 codes. Mirrors the grouping <see cref="BuiltInRedactionSets"/> composes its own sets from.</summary>
    public static readonly IReadOnlyList<EntityGroup> Groups =
    [
        new("Common", ["PERSON", "EMAIL_ADDRESS", "PHONE_NUMBER", "LOCATION", "DATE_TIME", "IP_ADDRESS", "URL", "ORGANIZATION", "NRP"]),
        new("Financial", ["CREDIT_CARD", "IBAN_CODE", "CRYPTO"]),
        new("Government IDs (US)", ["US_SSN", "US_DRIVER_LICENSE", "US_BANK_NUMBER", "US_PASSPORT", "MEDICAL_LICENSE"]),
        new("Government IDs (AU)", ["AU_TFN", "AU_MEDICARE", "AU_ABN", "AU_ACN"]),
    ];

    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["PERSON"] = "Name",
        ["EMAIL_ADDRESS"] = "Email address",
        ["PHONE_NUMBER"] = "Phone number",
        ["US_SSN"] = "Social Security number",
        ["CREDIT_CARD"] = "Credit card number",
        ["LOCATION"] = "Location",
        ["DATE_TIME"] = "Date",
        ["IP_ADDRESS"] = "IP address",
        ["US_DRIVER_LICENSE"] = "Driver's licence number",
        ["URL"] = "URL",
        ["ORGANIZATION"] = "Organization",
        ["NRP"] = "Nationality/religion/political group",
        ["US_BANK_NUMBER"] = "Bank account number",
        ["US_PASSPORT"] = "Passport number",
        ["IBAN_CODE"] = "IBAN",
        ["CRYPTO"] = "Cryptocurrency wallet address",
        ["MEDICAL_LICENSE"] = "Medical licence number",
        ["AU_TFN"] = "Tax File Number (AU)",
        ["AU_MEDICARE"] = "Medicare number (AU)",
        ["AU_ABN"] = "Business Number (ABN)",
        ["AU_ACN"] = "Company Number (ACN)",
    };

    /// <summary>A friendly name for a Presidio entity type code, e.g. "US_SSN" → "Social Security
    /// number". Falls back to the raw code, title-cased with underscores turned into spaces, for any
    /// entity type not in the table above (Presidio's recognizer set can grow independently of this).</summary>
    public static string Describe(string entityType)
    {
        if (Names.TryGetValue(entityType, out var friendly))
            return friendly;

        return string.IsNullOrWhiteSpace(entityType)
            ? entityType
            : string.Join(' ', entityType.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    }
}
