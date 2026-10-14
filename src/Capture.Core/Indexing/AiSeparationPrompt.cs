using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Capture.Core.Indexing;

/// <summary>One part of a page-separation request: text, or a page image (by file path) that providers
/// able to see images send in place.</summary>
public sealed record AiPromptPart(string? Text, string? ImagePath = null);

public sealed record AiSeparationRequest(string SystemMessage, IReadOnlyList<AiPromptPart> Parts, bool ClassifyOnly)
{
    /// <summary>The request as plain text, for providers that don't take images.</summary>
    public string UserText => string.Concat(Parts.Where(part => part.Text is not null).Select(part => part.Text));
}

/// <summary>Prompts for AI document separation, ported from the docsplitter boundary detector. Each
/// page is judged with the pages either side of it for context. The rules in the system message each
/// fixed a real mis-split there — keep them when changing it.</summary>
public static class AiSeparationPrompt
{
    public const string OtherTypeId = "other";
    private const int HeadChars = 500;
    private const int TailChars = 300;

    /// <summary>The most of a profile's guidance sent: the local model has a 4k-token context to share
    /// with the rules, the types and three page excerpts.</summary>
    public const int MaxGuidanceChars = 1500;

    public static string SystemMessage(IReadOnlyList<AiSeparationType> types, string? guidance = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a document boundary detector. You analyse scanned pages from multi-document batches.");
        builder.AppendLine();
        builder.AppendLine("Your task is to determine whether the CURRENT PAGE begins a NEW logical document or continues the previous one, and what type of document it belongs to.");
        builder.AppendLine();
        builder.AppendLine("Respond ONLY with valid JSON — no markdown fences, no extra text — matching this schema exactly:");
        builder.AppendLine("{");
        builder.AppendLine("  \"is_new_document\": <boolean>,");
        builder.AppendLine("  \"document_type\": <one of the document type ids below, or \"other\">,");
        builder.AppendLine("  \"suggested_type\": <string — when document_type is \"other\", a short general name for what the document is, e.g. \"Bank Statement\"; otherwise \"\">,");
        builder.AppendLine("  \"confidence\": <integer 0-100>,");
        builder.AppendLine("  \"reasoning\": <string, max 40 words>");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("Rules:");
        builder.AppendLine("- is_new_document: true if this page clearly starts a fresh, independent document");
        builder.AppendLine("- document_type: the id of the type the document this page belongs to is; \"other\" if none fits");
        builder.AppendLine("- suggested_type: only for \"other\" — prefer general names (\"Timesheet\", not \"Weekly Timesheet for Acme\")");
        builder.AppendLine("- confidence: your certainty in both decisions — use less than 70 when genuinely ambiguous");
        builder.AppendLine("- reasoning: brief visual/textual evidence supporting your decision");
        builder.AppendLine();
        builder.AppendLine("A new document is indicated by things like: a new header/letterhead, a different date or reference number, a change in formatting style, a cover page, or a clear subject change.");
        builder.AppendLine("A continuation is indicated by: sequential page numbers (\"page 2 of 3\"), continued mid-sentence text, or explicit \"continued\" markers.");
        builder.AppendLine();
        builder.AppendLine("IMPORTANT — extracted text is authoritative: When extracted text is provided, it is more reliable than visual appearance alone. If any reference number, document number, date, order number, total, or other unique identifier appears in the extracted text of the CURRENT PAGE and differs from the same field on the PREVIOUS PAGE, that is definitive evidence of a new document — even if the pages look visually identical.");
        builder.AppendLine();
        builder.AppendLine("IMPORTANT — same template ≠ continuation: Many documents in a batch share identical letterheads, logos, or form layouts. Do NOT treat visual similarity alone as evidence of continuation. A shared template with differing content identifiers always means a new document.");
        builder.AppendLine();
        // Keep this even though the catalog's Invoice and Remittance Advice descriptions say it too: on
        // the local model, dropping it doubled the split errors on same-template invoices.
        builder.AppendLine("IMPORTANT — remittance slip on an invoice: Many invoices include a detachable remittance slip or payment advice section printed at the bottom of the same page. This does NOT make the page a remittance advice — it is still an invoice. Only classify a page as a remittance advice if the entire page is a standalone remittance/payment confirmation with no invoice charges or line items.");
        builder.AppendLine();
        if (Guidance(guidance) is { } text)
        {
            builder.AppendLine("IMPORTANT — rules for this batch, from the user (follow them over the general rules above):");
            builder.AppendLine(text);
            builder.AppendLine();
        }

        builder.AppendLine("Document types:");
        foreach (var type in types)
        {
            builder.Append("- id=\"").Append(type.Id).Append("\" name=\"").Append(type.Name).Append('"');
            if (!string.IsNullOrWhiteSpace(type.Description))
                builder.Append(" description=\"").Append(type.Description.Trim()).Append('"');
            builder.AppendLine();
        }

        builder.Append("- id=\"").Append(OtherTypeId).Append("\" name=\"Other\" description=\"Anything that fits none of the types above.\"");
        return builder.ToString();
    }

    private static string? Guidance(string? guidance)
    {
        var text = guidance?.Trim().ReplaceLineEndings();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length <= MaxGuidanceChars ? text : text[..MaxGuidanceChars].TrimEnd();
    }

    /// <summary>The request for page <paramref name="index"/>. The first page, and a page that must start
    /// a document anyway, is only classified; every other page is judged against the page before it,
    /// with the next page as extra context.</summary>
    public static AiSeparationRequest ForPage(
        IReadOnlyList<AiSeparationPage> pages, int index, IReadOnlyList<AiSeparationType> types, bool includeImages, string? guidance = null)
    {
        var page = pages[index];
        var classifyOnly = index == 0 || page.ForceNew;
        var parts = new List<AiPromptPart>();

        if (classifyOnly)
        {
            parts.Add(new AiPromptPart("This page always starts a new document.\n\nCURRENT PAGE (classify this one):\n"));
            AddPage(parts, page, includeImages);
            parts.Add(new AiPromptPart(
                "\nClassify this page: set is_new_document to true, choose document_type, give your confidence in the type, and a brief reasoning."));
        }
        else
        {
            parts.Add(new AiPromptPart("PREVIOUS PAGE (context only — do not classify this page):\n"));
            AddPage(parts, pages[index - 1], includeImages);
            parts.Add(new AiPromptPart("\nCURRENT PAGE (classify this one):\n"));
            AddPage(parts, page, includeImages);
            if (index + 1 < pages.Count && !pages[index + 1].ForceNew)
            {
                parts.Add(new AiPromptPart("\nNEXT PAGE (context only — do not classify this page):\n"));
                AddPage(parts, pages[index + 1], includeImages);
            }

            parts.Add(new AiPromptPart("\nIs the CURRENT PAGE the start of a new document, and what type of document does it belong to?"));
        }

        return new AiSeparationRequest(SystemMessage(types, guidance), parts, classifyOnly);
    }

    private static void AddPage(List<AiPromptPart> parts, AiSeparationPage page, bool includeImages)
    {
        if (includeImages && page.ImagePath is not null)
            parts.Add(new AiPromptPart(null, page.ImagePath));
        parts.Add(new AiPromptPart("Extracted text:\n" + Excerpt(page.Text) + "\n"));
    }

    /// <summary>The start and end of a page's text: headers carry document numbers and dates, footers
    /// carry totals and "page X of Y".</summary>
    public static string Excerpt(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
            return "(no text)";
        return text.Length <= HeadChars + TailChars
            ? text
            : text[..HeadChars] + " … " + text[^TailChars..];
    }

    /// <summary>Reads the model's answer. Unknown or missing types come back as null; confidence given as
    /// 0–1 is scaled to 0–100. Throws <see cref="FormatException"/> when there's no usable JSON.</summary>
    public static AiPageVerdict Parse(string raw, IReadOnlyList<AiSeparationType> types)
    {
        var json = AiExtractPrompt.StripFence(raw ?? string.Empty);
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new FormatException("The AI didn't answer with JSON.");

        try
        {
            using var document = JsonDocument.Parse(json[start..(end + 1)]);
            var root = document.RootElement;
            var isNew = root.TryGetProperty("is_new_document", out var newElement)
                && (newElement.ValueKind == JsonValueKind.True
                    || (newElement.ValueKind == JsonValueKind.String && bool.TryParse(newElement.GetString(), out var parsed) && parsed));
            var typeText = root.TryGetProperty("document_type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            var reason = root.TryGetProperty("reasoning", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            var typeId = MatchType(typeText, types);
            return new AiPageVerdict(isNew, typeId, ReadConfidence(root), reason, typeId is null ? Suggestion(root, typeText) : null);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The AI's answer wasn't valid JSON: {ex.Message}");
        }
    }

    // The AI's own name for a document none of the types fit: its suggested_type, or failing that a
    // document_type it made up instead of answering "other".
    private static string? Suggestion(JsonElement root, string? typeText)
    {
        var text = root.TryGetProperty("suggested_type", out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(text) && !string.Equals(typeText?.Trim(), OtherTypeId, StringComparison.OrdinalIgnoreCase))
            text = typeText;
        text = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? null : text.Length <= 60 ? text : text[..60].TrimEnd();
    }

    private static string? MatchType(string? text, IReadOnlyList<AiSeparationType> types)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        return (types.FirstOrDefault(type => string.Equals(type.Id, text, StringComparison.OrdinalIgnoreCase))
                ?? types.FirstOrDefault(type => string.Equals(type.Name, text, StringComparison.OrdinalIgnoreCase)))?.Id;
    }

    private static int ReadConfidence(JsonElement root)
    {
        if (!root.TryGetProperty("confidence", out var element))
            return 0;
        double value;
        if (element.ValueKind == JsonValueKind.Number)
            value = element.GetDouble();
        else if (element.ValueKind != JsonValueKind.String
                 || !double.TryParse(element.GetString()?.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return 0;
        if (value is > 0 and <= 1 && element.ValueKind == JsonValueKind.Number && element.GetRawText().Contains('.'))
            value *= 100;
        return (int)Math.Round(Math.Clamp(value, 0, 100));
    }
}
