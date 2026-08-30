using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Export;

namespace Capture.Tests;

public class CsvExportWriterTests
{
    private static (CaptureDocument Document, IReadOnlyList<IndexField> Fields, IReadOnlyList<IndexValue> Values) MakeDocument(
        string originalFileName, string storedPath, string? redactedPath = null)
    {
        var invoiceField = new IndexField { Id = Guid.NewGuid(), Name = "InvoiceNo" };
        var supplierField = new IndexField { Id = Guid.NewGuid(), Name = "Supplier" };
        var hiddenField = new IndexField { Id = Guid.NewGuid(), Name = "Internal", HideFromIndexing = true };

        var document = new CaptureDocument
        {
            OriginalFileName = originalFileName,
            StoredPath = storedPath,
            RedactedPath = redactedPath
        };

        var values = new List<IndexValue>
        {
            new() { FieldId = invoiceField.Id, FieldName = "InvoiceNo", Value = "INV-001" },
            new() { FieldId = supplierField.Id, FieldName = "Supplier", Value = "Acme, Inc." }
        };

        return (document, [invoiceField, supplierField, hiddenField], values);
    }

    private static string CreateTempRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "capture-csv-export-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Empty_field_ids_exports_all_non_hidden_fields()
    {
        var root = CreateTempRoot();
        var (document, fields, values) = MakeDocument("invoice.pdf", Path.Combine(root, "source.pdf"));
        File.WriteAllText(document.StoredPath, "source");

        var definition = new ExportDefinition
        {
            OutputFolder = root,
            OutputMode = ExportOutputMode.OneFilePerDocument,
            FileNamePattern = "{OriginalFileName}"
        };

        var writer = new CsvExportWriter();
        var result = await writer.ExportAsync(definition, new ExportDocumentContext(document, fields, values));

        Assert.True(result.Success, result.Message);
        var path = Path.Combine(root, "invoice.csv");
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal("InvoiceNo,Supplier", lines[0]);
        Assert.Equal("INV-001,\"Acme, Inc.\"", lines[1]);
    }

    [Fact]
    public async Task Append_mode_writes_header_once_and_appends_subsequent_rows()
    {
        var root = CreateTempRoot();
        var definition = new ExportDefinition
        {
            OutputFolder = root,
            OutputMode = ExportOutputMode.AppendToSharedFile,
            SharedFileName = "shared.csv"
        };
        var writer = new CsvExportWriter();

        var (doc1, fields, values1) = MakeDocument("a.pdf", Path.Combine(root, "a-source.pdf"));
        File.WriteAllText(doc1.StoredPath, "a");
        var result1 = await writer.ExportAsync(definition, new ExportDocumentContext(doc1, fields, values1));
        Assert.True(result1.Success, result1.Message);

        var (doc2, _, values2) = MakeDocument("b.pdf", Path.Combine(root, "b-source.pdf"));
        File.WriteAllText(doc2.StoredPath, "b");
        var result2 = await writer.ExportAsync(definition, new ExportDocumentContext(doc2, fields, values2));
        Assert.True(result2.Success, result2.Message);

        var lines = await File.ReadAllLinesAsync(Path.Combine(root, "shared.csv"));
        Assert.Equal(3, lines.Length);
        Assert.Equal("InvoiceNo,Supplier", lines[0]);
    }

    [Fact]
    public async Task One_file_per_document_produces_distinct_files()
    {
        var root = CreateTempRoot();
        var definition = new ExportDefinition
        {
            OutputFolder = root,
            OutputMode = ExportOutputMode.OneFilePerDocument,
            FileNamePattern = "{OriginalFileName}"
        };
        var writer = new CsvExportWriter();

        var (doc1, fields, values1) = MakeDocument("first.pdf", Path.Combine(root, "first-source.pdf"));
        File.WriteAllText(doc1.StoredPath, "1");
        var (doc2, _, values2) = MakeDocument("second.pdf", Path.Combine(root, "second-source.pdf"));
        File.WriteAllText(doc2.StoredPath, "2");

        await writer.ExportAsync(definition, new ExportDocumentContext(doc1, fields, values1));
        await writer.ExportAsync(definition, new ExportDocumentContext(doc2, fields, values2));

        Assert.True(File.Exists(Path.Combine(root, "first.csv")));
        Assert.True(File.Exists(Path.Combine(root, "second.csv")));
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(root, "first.csv"))).Length);
    }

    [Fact]
    public async Task Explicit_field_ids_select_a_subset_in_order()
    {
        var root = CreateTempRoot();
        var (document, fields, values) = MakeDocument("invoice.pdf", Path.Combine(root, "source.pdf"));
        File.WriteAllText(document.StoredPath, "source");

        var definition = new ExportDefinition
        {
            OutputFolder = root,
            OutputMode = ExportOutputMode.OneFilePerDocument,
            FileNamePattern = "{OriginalFileName}",
            FieldIds = [fields[1].Id] // Supplier only
        };

        var writer = new CsvExportWriter();
        await writer.ExportAsync(definition, new ExportDocumentContext(document, fields, values));

        var lines = await File.ReadAllLinesAsync(Path.Combine(root, "invoice.csv"));
        Assert.Equal("Supplier", lines[0]);
        Assert.Equal("\"Acme, Inc.\"", lines[1]);
    }

    [Fact]
    public async Task Filename_pattern_can_reference_a_field_excluded_from_csv_columns()
    {
        var root = CreateTempRoot();
        var (document, fields, values) = MakeDocument("invoice.pdf", Path.Combine(root, "source.pdf"));
        File.WriteAllText(document.StoredPath, "source");

        var definition = new ExportDefinition
        {
            OutputFolder = root,
            OutputMode = ExportOutputMode.OneFilePerDocument,
            FileNamePattern = "{Supplier}",
            FieldIds = [fields[0].Id]
        };

        var result = await new CsvExportWriter().ExportAsync(
            definition,
            new ExportDocumentContext(document, fields, values));

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(root, "Acme, Inc.csv")));
        Assert.Equal("InvoiceNo", (await File.ReadAllLinesAsync(Path.Combine(root, "Acme, Inc.csv")))[0]);
    }

    [Fact]
    public void Filename_macro_resolves_date_time_and_sanitizes_portably()
    {
        var (document, fields, values) = MakeDocument("invoice.pdf", "/tmp/source.pdf");
        values[1].Value = "Acme/Aux: West?";

        var resolved = ExportFileNameTemplate.Resolve(
            "{Date|yyyyMMdd}_{Time|HH-mm-ss}_{Supplier}",
            document,
            "Invoices",
            fields,
            values,
            new DateTimeOffset(2026, 8, 30, 21, 5, 6, TimeSpan.FromHours(10)));

        Assert.Equal("20260830_21-05-06_Acme_Aux_ West_", resolved);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("a<b>c", "a_b_c")]
    public void Filename_sanitizer_handles_portable_edge_cases(string input, string expected)
    {
        Assert.Equal(expected, ExportFileNameTemplate.Sanitize(input, "fallback"));
    }

    [Fact]
    public async Task FileMode_original_copies_stored_path_and_adds_file_column()
    {
        var root = CreateTempRoot();
        var (document, fields, values) = MakeDocument("invoice.pdf", Path.Combine(root, "source.pdf"));
        File.WriteAllText(document.StoredPath, "original content");

        var definition = new ExportDefinition
        {
            OutputFolder = root,
            OutputMode = ExportOutputMode.OneFilePerDocument,
            FileNamePattern = "{OriginalFileName}",
            FileMode = ExportFileMode.Original
        };

        var writer = new CsvExportWriter();
        var result = await writer.ExportAsync(definition, new ExportDocumentContext(document, fields, values));

        Assert.True(result.Success, result.Message);
        var copied = Path.Combine(root, "invoice.pdf");
        Assert.True(File.Exists(copied));
        Assert.Equal("original content", await File.ReadAllTextAsync(copied));
        var lines = await File.ReadAllLinesAsync(Path.Combine(root, "invoice.csv"));
        Assert.Equal("InvoiceNo,Supplier,File", lines[0]);
        Assert.Contains(copied, lines[1]);
    }

    [Fact]
    public async Task FileMode_redacted_falls_back_to_original_when_not_redacted_yet()
    {
        var root = CreateTempRoot();
        var (document, fields, values) = MakeDocument("invoice.pdf", Path.Combine(root, "source.pdf"), redactedPath: null);
        File.WriteAllText(document.StoredPath, "original content");

        var definition = new ExportDefinition
        {
            OutputFolder = root,
            OutputMode = ExportOutputMode.OneFilePerDocument,
            FileNamePattern = "{OriginalFileName}",
            FileMode = ExportFileMode.Redacted
        };

        var writer = new CsvExportWriter();
        var result = await writer.ExportAsync(definition, new ExportDocumentContext(document, fields, values));

        Assert.True(result.Success, result.Message);
        Assert.Equal("original content", await File.ReadAllTextAsync(Path.Combine(root, "invoice.pdf")));
    }

    [Fact]
    public async Task Colliding_target_path_gets_a_numeric_suffix_instead_of_overwriting()
    {
        var root = CreateTempRoot();
        var definition = new ExportDefinition
        {
            OutputFolder = root,
            OutputMode = ExportOutputMode.OneFilePerDocument,
            FileNamePattern = "same-name"
        };
        var writer = new CsvExportWriter();

        var (doc1, fields, values1) = MakeDocument("a.pdf", Path.Combine(root, "a-source.pdf"));
        File.WriteAllText(doc1.StoredPath, "1");
        var (doc2, _, values2) = MakeDocument("b.pdf", Path.Combine(root, "b-source.pdf"));
        File.WriteAllText(doc2.StoredPath, "2");

        await writer.ExportAsync(definition, new ExportDocumentContext(doc1, fields, values1));
        await writer.ExportAsync(definition, new ExportDocumentContext(doc2, fields, values2));

        Assert.True(File.Exists(Path.Combine(root, "same-name.csv")));
        Assert.True(File.Exists(Path.Combine(root, "same-name (2).csv")));
    }
}
