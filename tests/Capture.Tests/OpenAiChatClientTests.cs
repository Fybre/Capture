using System.Net;
using System.Text;
using Capture.App.Services;

namespace Capture.Tests;

public class OpenAiChatClientTests
{
    [Fact]
    public async Task A_model_that_refuses_temperature_is_retried_without_it_and_remembered()
    {
        var handler = new ReasoningModel();
        using var http = new HttpClient(handler);
        object[] messages = [new { role = "user", content = "hi" }];

        var first = await OpenAiChatClient.CompleteAsync(http, "https://res.openai.azure.com/openai/v1", "key", "gpt-5.1-test", messages, default);
        var second = await OpenAiChatClient.CompleteAsync(http, "https://res.openai.azure.com/openai/v1", "key", "gpt-5.1-test", messages, default);

        Assert.Equal("{\"ok\":true}", first);
        Assert.Equal("{\"ok\":true}", second);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains("\"temperature\"", handler.Bodies[0]);
        Assert.DoesNotContain("temperature", handler.Bodies[1]);
        Assert.DoesNotContain("temperature", handler.Bodies[2]);
        Assert.All(handler.Bodies, body => Assert.DoesNotContain("max_tokens", body));
    }

    [Fact]
    public async Task Other_errors_are_reported_with_the_services_message()
    {
        using var http = new HttpClient(new Fixed(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"DeploymentNotFound\"}}"));
        var reply = await OpenAiChatClient.SendAsync(http, "https://api.example.com/v1", "key", "m", [new { role = "user", content = "hi" }], default);

        Assert.Equal(404, reply.Status);
        Assert.Contains("DeploymentNotFound", reply.Error);
    }

    private sealed class ReasoningModel : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return body.Contains("\"temperature\"")
                ? Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unsupported value: 'temperature' does not support 0 with this model.\"}}")
                : Json(HttpStatusCode.OK, "{\"model\":\"gpt-5.1\",\"choices\":[{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}");
        }
    }

    private sealed class Fixed(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Json(status, body));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
