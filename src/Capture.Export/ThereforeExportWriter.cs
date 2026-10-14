using System.Text.RegularExpressions;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Watch;
using Capture.Therefore;

namespace Capture.Export;

public sealed class ThereforeExportWriter : IExportWriter
{
    private readonly IThereforeClient _client;
    private readonly IWatchSettingsStore _watchSettings;
    private readonly IExportAttachmentProcessor _attachments;

    public ThereforeExportWriter(IThereforeClient client, IWatchSettingsStore watchSettings, IExportAttachmentProcessor attachments)
    {
        _client = client;
        _watchSettings = watchSettings;
        _attachments = attachments;
    }

    public ExportType Type => ExportType.Therefore;

    // Matches the DocNo Therefore's own error text names when CreateDocument's follow-up step can't
    // load the document it just (or thought it just) saved — see the catch block below.
    private static readonly Regex FailedToLoadDocNoPattern = new(@"for DocNo:\s*(\d+)", RegexOptions.Compiled);

    public async Task<ExportResult> ExportAsync(
        ExportDefinition definition, ExportDocumentContext context, CancellationToken cancellationToken = default)
    {
        // Declared outside the try so the catch block below can still reach it — CreateDocumentAsync
        // needs it to double-check a failure before giving up (see that catch block's comment).
        ThereforeConnectionSettings? connection = null;
        try
        {
            var settings = await _watchSettings.LoadAsync(cancellationToken).ConfigureAwait(false);
            var saved = settings.FindConnection(definition.ThereforeConnectionId, ConnectionType.Therefore);
            if (saved is null)
            {
                return new ExportResult(false, definition.ThereforeConnectionId is null
                    ? $"\"{definition.Name}\": no Therefore connection — add one in Settings ▸ Connections."
                    : $"\"{definition.Name}\": its Therefore connection was removed — choose another in the profile's export settings.");
            }
            if (!saved.IsConfigured)
                return new ExportResult(false, $"\"{definition.Name}\": the Therefore connection \"{saved.Name}\" isn't fully set up — finish it in Settings ▸ Connections.");
            if (definition.ThereforeCategoryNo is not { } categoryNo)
                return new ExportResult(false, $"\"{definition.Name}\": no Therefore category selected.");

            connection = ThereforeConnectionSettings.From(saved);

            var valuesByField = context.IndexValues.ToDictionary(value => value.FieldId, value => value.Value);
            var items = definition.ThereforeFieldMappings
                .Where(IsConfigured)
                .Select(mapping => BuildIndexDataItem(mapping, ResolveValue(mapping, valuesByField), context.Locale))
                .ToList();

            List<ThereforeStream>? streams = null;
            if (definition.FileMode != ExportFileMode.None)
            {
                var sourcePath = await _attachments.ResolveAsync(definition, context.Document, cancellationToken).ConfigureAwait(false);
                var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                streams = [new ThereforeStream(0, Path.GetFileName(sourcePath), Convert.ToBase64String(bytes))];
            }

            var request = new ThereforeCreateDocumentRequest
            {
                CategoryNo = categoryNo,
                IndexDataItems = items,
                Streams = streams,
                CheckInComments = "Created via Capture"
            };

            var result = await _client.CreateDocumentAsync(connection, request, cancellationToken).ConfigureAwait(false);
            return new ExportResult(true, $"\"{definition.Name}\": created Therefore document #{result.DocNo}")
            {
                Reference = $"Therefore document {result.DocNo}"
            };
        }
        catch (Exception ex)
        {
            // CreateDocument's own follow-up step can fail with "Failed to load index data for DocNo: X"
            // when a Therefore workflow triggered by the save (e.g. one that moves the document to a
            // different category) wins a race against that follow-up — the document was genuinely
            // created and saved, just no longer where CreateDocument expects to find it a moment later.
            // Confirmed live against a real tenant: the named DocNo existed, fully saved, under a
            // different category than requested. Treat that specific case as a success instead of
            // leaving a document that's actually already in Therefore stuck failed in the inbox, where
            // retrying would create a duplicate.
            if (connection is not null
                && FailedToLoadDocNoPattern.Match(ex.Message) is { Success: true } match
                && int.TryParse(match.Groups[1].Value, out var docNo)
                && await _client.DocumentExistsAsync(connection, docNo, cancellationToken).ConfigureAwait(false))
            {
                return new ExportResult(true,
                    $"\"{definition.Name}\": created Therefore document #{docNo} (confirmed after a save/workflow-timing conflict)")
                {
                    Reference = $"Therefore document {docNo}"
                };
            }

            return new ExportResult(false, $"\"{definition.Name}\": {ex.Message}");
        }
    }

    /// <param name="locale">The document type's locale: values are read the way the field validated
    /// them, e.g. "$1,234.50" or a day-first date.</param>
    internal static object BuildIndexDataItem(ThereforeFieldMapping mapping, string value, string? locale = null)
    {
        var fieldName = string.IsNullOrEmpty(mapping.IndexDataFieldName) ? mapping.Caption : mapping.IndexDataFieldName;
        return (ThereforeFieldType)mapping.FieldType switch
        {
            ThereforeFieldType.Int => IndexFormat.TryParseInteger(value, locale, out var intValue)
                ? ThereforeIndexData.Int(mapping.FieldNo, fieldName, intValue)
                : ThereforeIndexData.String(mapping.FieldNo, fieldName, value),
            ThereforeFieldType.Money => IndexFormat.TryParseMoney(value, locale, out var moneyValue)
                ? ThereforeIndexData.Money(mapping.FieldNo, fieldName, moneyValue)
                : ThereforeIndexData.String(mapping.FieldNo, fieldName, value),
            ThereforeFieldType.Date => IndexFormat.TryParseDate(value, locale, out var dateValue)
                ? ThereforeIndexData.Date(mapping.FieldNo, fieldName, dateValue)
                : ThereforeIndexData.String(mapping.FieldNo, fieldName, value),
            ThereforeFieldType.Logical => ThereforeIndexData.Logical(mapping.FieldNo, fieldName, IsTruthy(value)),
            // Table/Custom, and any unrecognized value, fall back to string — flagged in the plan as
            // the one combination not yet exercised against a real tenant.
            _ => ThereforeIndexData.String(mapping.FieldNo, fieldName, value)
        };
    }

    internal static bool IsConfigured(ThereforeFieldMapping mapping) => mapping.IndexFieldId is not null;

    internal static string ResolveValue(ThereforeFieldMapping mapping, IReadOnlyDictionary<Guid, string> valuesByField) =>
        mapping.IndexFieldId is { } fieldId
            ? valuesByField.GetValueOrDefault(fieldId, string.Empty)
            : string.Empty;

    private static bool IsTruthy(string value) => IndexFormat.TryParseBoolean(value, out var result) && result;
}
