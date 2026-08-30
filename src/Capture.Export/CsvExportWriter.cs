using System.Collections.Concurrent;
using System.Text;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Export;

public sealed class CsvExportWriter : IExportWriter
{
    // Keyed by output file path so two documents exporting to the same shared CSV concurrently
    // append serially instead of interleaving or truncating each other's rows.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    public ExportType Type => ExportType.Csv;

    public async Task<ExportResult> ExportAsync(
        ExportDefinition definition, ExportDocumentContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(definition.OutputFolder);

            var fields = ResolveFields(definition, context.ProfileFields);
            var valuesByField = context.IndexValues.ToDictionary(value => value.FieldId, value => value.Value);
            var fieldValues = fields.Select(field => valuesByField.GetValueOrDefault(field.Id, string.Empty)).ToList();
            var baseName = ExportFileNameTemplate.Resolve(
                definition.FileNamePattern,
                context.Document,
                profileName: null,
                context.ProfileFields,
                context.IndexValues,
                DateTimeOffset.Now);

            string? copiedFilePath = null;
            if (definition.FileMode != ExportFileMode.None)
            {
                // Redaction may not have been applied yet — fall back to the original rather than
                // exporting nothing at all, mirroring RedactionDetectionStep's graceful-degrade pattern.
                var sourcePath = definition.FileMode == ExportFileMode.Redacted
                    ? context.Document.RedactedPath ?? context.Document.StoredPath
                    : context.Document.StoredPath;
                var destination = EnsureUniquePath(
                    Path.Combine(definition.OutputFolder, baseName + Path.GetExtension(sourcePath)));
                File.Copy(sourcePath, destination, overwrite: false);
                copiedFilePath = destination;
            }

            var header = fields.Select(field => field.Name).ToList();
            var row = new List<string>(fieldValues);
            if (definition.FileMode != ExportFileMode.None)
            {
                header.Add("File");
                row.Add(copiedFilePath ?? string.Empty);
            }

            if (definition.OutputMode == ExportOutputMode.AppendToSharedFile)
            {
                var path = Path.Combine(definition.OutputFolder, definition.SharedFileName);
                await AppendRowAsync(path, header, row, definition.IncludeHeader, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var path = EnsureUniquePath(Path.Combine(definition.OutputFolder, baseName + ".csv"));
                await WriteNewFileAsync(path, header, row, definition.IncludeHeader, cancellationToken).ConfigureAwait(false);
            }

            return new ExportResult(true, null);
        }
        catch (Exception ex)
        {
            return new ExportResult(false, $"\"{definition.Name}\": {ex.Message}");
        }
    }

    private static IReadOnlyList<IndexField> ResolveFields(ExportDefinition definition, IReadOnlyList<IndexField> profileFields)
    {
        if (definition.FieldIds.Count == 0)
            return profileFields.Where(field => !field.HideFromIndexing).ToList();

        var byId = profileFields.ToDictionary(field => field.Id);
        return definition.FieldIds
            .Select(id => byId.GetValueOrDefault(id))
            .Where(field => field is not null)
            .Cast<IndexField>()
            .ToList();
    }

    // Avoids one document's export silently overwriting a previous document's file when two
    // documents happen to resolve to the same pattern-derived name.
    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var counter = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{name} ({counter}){extension}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static Task WriteNewFileAsync(
        string path, IReadOnlyList<string> header, IReadOnlyList<string> row, bool includeHeader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        if (includeHeader)
            builder.Append(ToCsvLine(header));
        builder.Append(ToCsvLine(row));
        return File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    private static async Task AppendRowAsync(
        string path, IReadOnlyList<string> header, IReadOnlyList<string> row, bool includeHeader, CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var writeHeader = includeHeader && !File.Exists(path);
            var builder = new StringBuilder();
            if (writeHeader)
                builder.Append(ToCsvLine(header));
            builder.Append(ToCsvLine(row));
            await File.AppendAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string ToCsvLine(IReadOnlyList<string> fields) =>
        string.Join(',', fields.Select(EscapeCsvField)) + "\r\n";

    private static string EscapeCsvField(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
