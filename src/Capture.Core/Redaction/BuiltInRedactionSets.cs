namespace Capture.Core.Redaction;

/// <summary>The predefined redaction sets every install starts with — "Core" (locale-agnostic PII plus
/// financial entities) and two country-specific extensions. These are hardcoded, not stored: unlike
/// custom sets they never go through <see cref="IRedactionEntitySetStore"/>, so there's nothing to seed
/// or migrate and no way for a user to corrupt or lose them. Ids are fixed literals so a profile's
/// <c>RedactionSettings.EntitySetId</c> reference to one survives across restarts and rebuilds.</summary>
public static class BuiltInRedactionSets
{
    // DATE_TIME and ORGANIZATION are deliberately excluded — both come from Presidio's statistical NER
    // model rather than a deterministic pattern/regex recognizer, and are noticeably less reliable
    // (e.g. a postcode or state abbreviation tagged DATE_TIME, an email address tagged ORGANIZATION).
    // Available in a custom set via Settings if wanted.
    private static readonly IReadOnlyList<string> CoreEntities =
    [
        "PERSON", "EMAIL_ADDRESS", "PHONE_NUMBER", "LOCATION", "IP_ADDRESS", "URL",
        "NRP", "CREDIT_CARD", "IBAN_CODE", "CRYPTO",
    ];

    private static readonly IReadOnlyList<string> AuEntities = ["AU_TFN", "AU_MEDICARE", "AU_ABN", "AU_ACN"];

    private static readonly IReadOnlyList<string> UsEntities =
        ["US_SSN", "US_DRIVER_LICENSE", "US_BANK_NUMBER", "US_PASSPORT", "MEDICAL_LICENSE"];

    public static readonly Guid CoreId = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid CoreAuId = new("22222222-2222-2222-2222-222222222222");
    public static readonly Guid CoreUsId = new("33333333-3333-3333-3333-333333333333");

    public static IReadOnlyList<RedactionEntitySet> All { get; } =
    [
        new() { Id = CoreId, Name = "Core", Entities = CoreEntities.ToList() },
        new() { Id = CoreAuId, Name = "Core + AU", Entities = CoreEntities.Concat(AuEntities).ToList() },
        new() { Id = CoreUsId, Name = "Core + US", Entities = CoreEntities.Concat(UsEntities).ToList() },
    ];
}
