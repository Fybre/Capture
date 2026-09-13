using System.Net;
using System.Text.Json;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Export;

namespace Capture.Tests;

public class RestExportWriterTests
{
    [Fact]
    public async Task Posts_mapped_fields_bearer_token_and_custom_headers()
    {
        var fieldId = Guid.NewGuid();
        var document = new CaptureDocument { OriginalFileName = "invoice.pdf", StoredPath = "irrelevant.pdf" };
        var context = new ExportDocumentContext(
            document,
            [new IndexField { Id = fieldId, Name = "InvoiceNo" }],
            [new IndexValue { FieldId = fieldId, Value = "INV-42" }]);
        var definition = new ExportDefinition
        {
            RestUrl = "https://example.test/ingest",
            RestBearerToken = "secret-token",
            RestCustomHeaders = [new RestCustomHeader { Name = "X-Source", Value = "capture" }],
            RestFieldMappings = [new RestFieldMapping { JsonKey = "invoiceNumber", IndexFieldId = fieldId }],
            FileMode = ExportFileMode.None
        };

        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var writer = new RestExportWriter(new HttpClient(handler), new PassthroughExportAttachmentProcessor());

        var result = await writer.ExportAsync(definition, context);

        Assert.True(result.Success);
        Assert.Equal("https://example.test/ingest", captured!.RequestUri!.ToString());
        Assert.Equal("secret-token", captured.Headers.Authorization!.Parameter);
        Assert.Equal("capture", captured.Headers.GetValues("X-Source").Single());

        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal("INV-42", json.RootElement.GetProperty("fields").GetProperty("invoiceNumber").GetString());
        Assert.Equal(document.Id.ToString(), json.RootElement.GetProperty("documentId").GetString());
    }

    [Fact]
    public async Task Attaches_the_resolved_file_as_base64_when_a_file_mode_is_set()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var storedPath = Path.Combine(root, "source.pdf");
            File.WriteAllBytes(storedPath, [1, 2, 3, 4]);
            var document = new CaptureDocument { OriginalFileName = "source.pdf", StoredPath = storedPath };
            var context = new ExportDocumentContext(document, [], []);
            var definition = new ExportDefinition
            {
                RestUrl = "https://example.test/ingest",
                FileMode = ExportFileMode.Original
            };

            string? capturedBody = null;
            var handler = new StubHandler(async request =>
            {
                capturedBody = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            var writer = new RestExportWriter(new HttpClient(handler), new PassthroughExportAttachmentProcessor());

            var result = await writer.ExportAsync(definition, context);

            Assert.True(result.Success);
            using var json = JsonDocument.Parse(capturedBody!);
            var base64 = json.RootElement.GetProperty("fileContentBase64").GetString();
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, Convert.FromBase64String(base64!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Reports_failure_when_no_url_is_configured()
    {
        var document = new CaptureDocument { OriginalFileName = "invoice.pdf", StoredPath = "irrelevant.pdf" };
        var context = new ExportDocumentContext(document, [], []);
        var definition = new ExportDefinition { RestUrl = "" };
        var writer = new RestExportWriter(new HttpClient(new StubHandler(_ => throw new InvalidOperationException("should not be called"))), new PassthroughExportAttachmentProcessor());

        var result = await writer.ExportAsync(definition, context);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Reports_failure_with_the_response_body_on_a_non_success_status_code()
    {
        var document = new CaptureDocument { OriginalFileName = "invoice.pdf", StoredPath = "irrelevant.pdf" };
        var context = new ExportDocumentContext(document, [], []);
        var definition = new ExportDefinition { RestUrl = "https://example.test/ingest" };
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("field mapping invalid")
        }));
        var writer = new RestExportWriter(new HttpClient(handler), new PassthroughExportAttachmentProcessor());

        var result = await writer.ExportAsync(definition, context);

        Assert.False(result.Success);
        Assert.Contains("field mapping invalid", result.Message);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _respond(request);
    }
}
