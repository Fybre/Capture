using System.Text.Json;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Watch;
using Capture.Export;
using Capture.Therefore;

namespace Capture.Tests;

public class ThereforeExportWriterTests
{
    private sealed class FakeWatchSettingsStore : IWatchSettingsStore
    {
        public Task<WatchSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WatchSettings
            {
                ThereforeBaseUrl = "https://example.thereforeonline.com",
                ThereforeUsername = "user",
                ThereforePassword = "pass"
            });

        public Task SaveAsync(WatchSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NullAttachmentProcessor : IExportAttachmentProcessor
    {
        public Task<string> ResolveAsync(ExportDefinition definition, CaptureDocument document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not needed — tests use ExportFileMode.None.");

        public Task CleanupAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Reproduces the exact race condition confirmed live against a real tenant: a Therefore workflow
    // triggered by the save moves the document to a different category before CreateDocument's own
    // internal follow-up can read the index data back, so Therefore's error names a DocNo that was, in
    // fact, genuinely saved.
    private sealed class RaceConditionThereforeClient(bool documentActuallyExists) : IThereforeClient
    {
        public Task<bool> TestConnectionAsync(ThereforeConnectionSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ThereforeTreeNode>> GetCategoriesTreeAsync(ThereforeConnectionSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ThereforeCategoryInfo> GetCategoryInfoAsync(ThereforeConnectionSettings settings, int categoryNo, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ThereforeCreateDocumentResult> CreateDocumentAsync(ThereforeConnectionSettings settings, ThereforeCreateDocumentRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "CreateDocument failed (500): {\"WSError\":{\"ErrorMessage\":\"The server reported an error.\\n" +
                "Additional information:\\r\\n\\tFailed to load index data for DocNo: 27911. Possible reason: " +
                "Passed in ctgry definition does not match actual saved category. Passed in CtgryNo: 360\\r\\n\\r\\n\"}}");

        public Task<bool> DocumentExistsAsync(ThereforeConnectionSettings settings, int docNo, CancellationToken cancellationToken = default)
        {
            Assert.Equal(27911, docNo);
            return Task.FromResult(documentActuallyExists);
        }
    }

    private static ExportDocumentContext Context() => new(
        new CaptureDocument { OriginalFileName = "doc.pdf", StoredPath = "doc.pdf" },
        ProfileFields: [],
        IndexValues: []);

    [Fact]
    public async Task A_failed_CreateDocument_naming_a_DocNo_that_actually_exists_is_reported_as_success()
    {
        var writer = new ThereforeExportWriter(new RaceConditionThereforeClient(documentActuallyExists: true), new FakeWatchSettingsStore(), new NullAttachmentProcessor());
        var definition = new ExportDefinition { Name = "Therefore", ThereforeCategoryNo = 360 };

        var result = await writer.ExportAsync(definition, Context());

        Assert.True(result.Success);
        Assert.Contains("27911", result.Message);
    }

    [Fact]
    public async Task A_failed_CreateDocument_naming_a_DocNo_that_does_not_exist_is_reported_as_failure()
    {
        var writer = new ThereforeExportWriter(new RaceConditionThereforeClient(documentActuallyExists: false), new FakeWatchSettingsStore(), new NullAttachmentProcessor());
        var definition = new ExportDefinition { Name = "Therefore", ThereforeCategoryNo = 360 };

        var result = await writer.ExportAsync(definition, Context());

        Assert.False(result.Success);
        Assert.Contains("Failed to load index data", result.Message);
    }

    [Fact]
    public void Redacted_source_resolution_fails_closed_until_redaction_is_applied()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-therefore-redaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var redactedPath = Path.Combine(root, "redacted.pdf");
        File.WriteAllText(redactedPath, "redacted");
        var document = new CaptureDocument
        {
            OriginalFileName = "source.pdf",
            StoredPath = Path.Combine(root, "source.pdf"),
            RedactedPath = redactedPath
        };
        var definition = new ExportDefinition { FileMode = ExportFileMode.Redacted };

        Assert.Throws<InvalidOperationException>(() => ExportSourceFile.Resolve(definition, document));

        document.RedactionStatus = RedactionStatus.Applied;
        Assert.Equal(redactedPath, ExportSourceFile.Resolve(definition, document));
    }

    [Fact]
    public void Redacted_or_original_source_resolution_uses_the_best_available_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-therefore-redaction-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalPath = Path.Combine(root, "source.pdf");
        var redactedPath = Path.Combine(root, "redacted.pdf");
        File.WriteAllText(originalPath, "original");
        File.WriteAllText(redactedPath, "redacted");
        var document = new CaptureDocument
        {
            OriginalFileName = "source.pdf",
            StoredPath = originalPath,
            RedactedPath = redactedPath
        };
        var definition = new ExportDefinition { FileMode = ExportFileMode.RedactedOrOriginal };

        Assert.Equal(originalPath, ExportSourceFile.Resolve(definition, document));

        document.RedactionStatus = RedactionStatus.Applied;
        Assert.Equal(redactedPath, ExportSourceFile.Resolve(definition, document));

        File.Delete(redactedPath);
        Assert.Equal(originalPath, ExportSourceFile.Resolve(definition, document));
    }

    private static ThereforeFieldMapping Mapping(ThereforeFieldType type) => new()
    {
        FieldNo = 42,
        Caption = "Test Field",
        IndexDataFieldName = "Test_Field",
        FieldType = (int)type,
        Mandatory = false
    };

    private static JsonElement Serialize(object item)
    {
        var json = JsonSerializer.Serialize(item);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Int_field_with_a_parseable_value_produces_IntIndexData()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Int), "123"));

        var data = element.GetProperty("IntIndexData");
        Assert.Equal("Test_Field", data.GetProperty("FieldName").GetString());
        Assert.Equal(123, data.GetProperty("DataValue").GetInt64());
    }

    [Fact]
    public void Int_field_with_an_unparseable_value_falls_back_to_string()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Int), "not-a-number"));

        var data = element.GetProperty("StringIndexData");
        Assert.Equal("not-a-number", data.GetProperty("DataValue").GetString());
    }

    [Fact]
    public void Money_field_with_a_parseable_value_produces_MoneyIndexData()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Money), "19.99"));

        var data = element.GetProperty("MoneyIndexData");
        Assert.Equal(19.99m, data.GetProperty("DataValue").GetDecimal());
    }

    [Fact]
    public void Money_field_with_an_unparseable_value_falls_back_to_string()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Money), "lots"));

        Assert.Equal("lots", element.GetProperty("StringIndexData").GetProperty("DataValue").GetString());
    }

    [Fact]
    public void Date_field_with_a_parseable_value_produces_DateIndexData()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Date), "2026-01-15"));

        Assert.True(element.TryGetProperty("DateIndexData", out _));
    }

    [Fact]
    public void Date_field_with_an_unparseable_value_falls_back_to_string()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Date), "whenever"));

        Assert.Equal("whenever", element.GetProperty("StringIndexData").GetProperty("DataValue").GetString());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("Yes", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    public void Logical_field_parses_truthy_values(string value, bool expected)
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.Logical), value));

        Assert.Equal(expected, element.GetProperty("LogicalIndexData").GetProperty("DataValue").GetBoolean());
    }

    [Fact]
    public void String_field_always_produces_StringIndexData()
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(ThereforeFieldType.String), "hello"));

        Assert.Equal("hello", element.GetProperty("StringIndexData").GetProperty("DataValue").GetString());
    }

    [Theory]
    [InlineData(ThereforeFieldType.Table)]
    [InlineData(ThereforeFieldType.Custom)]
    public void Table_and_custom_fields_fall_back_to_string(ThereforeFieldType type)
    {
        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(Mapping(type), "raw-value"));

        Assert.Equal("raw-value", element.GetProperty("StringIndexData").GetProperty("DataValue").GetString());
    }

    [Fact]
    public void Falls_back_to_caption_when_IndexDataFieldName_is_empty()
    {
        var mapping = new ThereforeFieldMapping
        {
            FieldNo = 1, Caption = "Fallback Caption", IndexDataFieldName = "", FieldType = (int)ThereforeFieldType.String
        };

        var element = Serialize(ThereforeExportWriter.BuildIndexDataItem(mapping, "value"));

        Assert.Equal("Fallback Caption", element.GetProperty("StringIndexData").GetProperty("FieldName").GetString());
    }

    [Fact]
    public void Index_mapping_still_resolves_the_current_document_value()
    {
        var fieldId = Guid.NewGuid();
        var mapping = Mapping(ThereforeFieldType.String);
        mapping.IndexFieldId = fieldId;

        Assert.True(ThereforeExportWriter.IsConfigured(mapping));
        Assert.Equal("INV-42", ThereforeExportWriter.ResolveValue(
            mapping, new Dictionary<Guid, string> { [fieldId] = "INV-42" }));
    }

    [Fact]
    public void Unmapped_index_source_is_not_written()
    {
        Assert.False(ThereforeExportWriter.IsConfigured(Mapping(ThereforeFieldType.String)));
    }
}
