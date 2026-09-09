using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Watch;
using Capture.Therefore;

namespace Capture.Export;

public sealed class ThereforeExportWriter : IExportWriter
{
    private readonly IThereforeClient _client;
    private readonly IWatchSettingsStore _watchSettings;

    public ThereforeExportWriter(IThereforeClient client, IWatchSettingsStore watchSettings)
    {
        _client = client;
        _watchSettings = watchSettings;
    }

    public ExportType Type => ExportType.Therefore;

    public async Task<ExportResult> ExportAsync(
        ExportDefinition definition, ExportDocumentContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _watchSettings.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.ThereforeConfigured)
                return new ExportResult(false, $"\"{definition.Name}\": Therefore connection isn't configured — set it up in Settings.");
            if (definition.ThereforeCategoryNo is not { } categoryNo)
                return new ExportResult(false, $"\"{definition.Name}\": no Therefore category selected.");

            var connection = new ThereforeConnectionSettings
            {
                BaseUrl = settings.ThereforeBaseUrl ?? string.Empty,
                TenantName = settings.ThereforeTenantName,
                AuthMethod = settings.ThereforeAuthMethod == Core.Watch.ThereforeAuthMethod.Bearer
                    ? global::Capture.Therefore.ThereforeAuthMethod.Bearer
                    : global::Capture.Therefore.ThereforeAuthMethod.Basic,
                Username = settings.ThereforeUsername,
                Password = settings.ThereforePassword,
                BearerToken = settings.ThereforeBearerToken
            };

            var valuesByField = context.IndexValues.ToDictionary(value => value.FieldId, value => value.Value);
            var items = definition.ThereforeFieldMappings
                .Where(IsConfigured)
                .Select(mapping => BuildIndexDataItem(mapping, ResolveValue(mapping, valuesByField)))
                .ToList();

            List<ThereforeStream>? streams = null;
            if (definition.FileMode != ExportFileMode.None)
            {
                var sourcePath = ExportSourceFile.Resolve(definition, context.Document);
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
            return new ExportResult(true, $"\"{definition.Name}\": created Therefore document #{result.DocNo}");
        }
        catch (Exception ex)
        {
            return new ExportResult(false, $"\"{definition.Name}\": {ex.Message}");
        }
    }

    internal static object BuildIndexDataItem(ThereforeFieldMapping mapping, string value)
    {
        var fieldName = string.IsNullOrEmpty(mapping.IndexDataFieldName) ? mapping.Caption : mapping.IndexDataFieldName;
        return (ThereforeFieldType)mapping.FieldType switch
        {
            ThereforeFieldType.Int => long.TryParse(value, out var intValue)
                ? ThereforeIndexData.Int(mapping.FieldNo, fieldName, intValue)
                : ThereforeIndexData.String(mapping.FieldNo, fieldName, value),
            ThereforeFieldType.Money => decimal.TryParse(value, out var moneyValue)
                ? ThereforeIndexData.Money(mapping.FieldNo, fieldName, moneyValue)
                : ThereforeIndexData.String(mapping.FieldNo, fieldName, value),
            ThereforeFieldType.Date => DateTime.TryParse(value, out var dateValue)
                ? ThereforeIndexData.Date(mapping.FieldNo, fieldName, dateValue)
                : ThereforeIndexData.String(mapping.FieldNo, fieldName, value),
            ThereforeFieldType.Logical => ThereforeIndexData.Logical(mapping.FieldNo, fieldName, IsTruthy(value)),
            // Table/Custom, and any unrecognized value, fall back to string — flagged in the plan as
            // the one combination not yet exercised against a real tenant.
            _ => ThereforeIndexData.String(mapping.FieldNo, fieldName, value)
        };
    }

    internal static bool IsConfigured(ThereforeFieldMapping mapping) =>
        mapping.ValueSource == ThereforeMappingValueSource.Constant
            || mapping.IndexFieldId is not null;

    internal static string ResolveValue(ThereforeFieldMapping mapping, IReadOnlyDictionary<Guid, string> valuesByField) =>
        mapping.ValueSource == ThereforeMappingValueSource.Constant
            ? mapping.ConstantValue
            : mapping.IndexFieldId is { } fieldId
                ? valuesByField.GetValueOrDefault(fieldId, string.Empty)
                : string.Empty;

    private static bool IsTruthy(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value == "1";
}
