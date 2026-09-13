using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Export;

/// <summary>Posts a document's field values (and, optionally, its attached file as base64) as a JSON
/// body to an arbitrary HTTP endpoint. Field mapping is an explicit list
/// (<see cref="ExportDefinition.RestFieldMappings"/>), mirroring <see cref="ThereforeFieldMapping"/> —
/// deliberately not "every field by default" like <see cref="CsvExportWriter"/>.</summary>
public sealed class RestExportWriter : IExportWriter
{
    private readonly HttpClient _http;
    private readonly IExportAttachmentProcessor _attachments;

    public RestExportWriter(HttpClient http, IExportAttachmentProcessor attachments)
    {
        _http = http;
        _attachments = attachments;
    }

    public ExportType Type => ExportType.Rest;

    public async Task<ExportResult> ExportAsync(
        ExportDefinition definition, ExportDocumentContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(definition.RestUrl))
                return new ExportResult(false, $"\"{definition.Name}\": no REST URL configured.");

            var valuesByField = context.IndexValues.ToDictionary(value => value.FieldId, value => value.Value);
            var fields = new Dictionary<string, string>();
            foreach (var mapping in definition.RestFieldMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.JsonKey))
                    continue;
                fields[mapping.JsonKey] = mapping.IndexFieldId is { } fieldId
                    ? valuesByField.GetValueOrDefault(fieldId, string.Empty)
                    : string.Empty;
            }

            string? fileName = null;
            string? fileContentBase64 = null;
            if (definition.FileMode != ExportFileMode.None)
            {
                var sourcePath = await _attachments.ResolveAsync(definition, context.Document, cancellationToken).ConfigureAwait(false);
                var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                fileName = ExportFileNameTemplate.Resolve(
                    definition.FileNamePattern,
                    context.Document,
                    profileName: null,
                    context.AllProfileFields,
                    context.IndexValues,
                    DateTimeOffset.Now) + Path.GetExtension(sourcePath);
                fileContentBase64 = Convert.ToBase64String(bytes);
            }

            var payload = new
            {
                documentId = context.Document.Id,
                fields,
                fileName,
                fileContentBase64
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, definition.RestUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(definition.RestBearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", definition.RestBearerToken);
            foreach (var header in definition.RestCustomHeaders)
            {
                if (!string.IsNullOrWhiteSpace(header.Name))
                    request.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ExportResult(false, $"\"{definition.Name}\": REST endpoint returned {(int)response.StatusCode} {response.ReasonPhrase} — {responseBody}");

            return new ExportResult(true, $"\"{definition.Name}\": posted to {definition.RestUrl}");
        }
        catch (Exception ex)
        {
            return new ExportResult(false, $"\"{definition.Name}\": {ex.Message}");
        }
    }
}
