using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Capture.Core.Indexing;
using Capture.Core.Watch;
using Capture.Pdf;

namespace Capture.App.Services;

/// <summary>AI document separation through the cloud OpenAI-compatible endpoint set in Settings, sending
/// page images alongside their text unless that's turned off.</summary>
public sealed class OpenAiPageSeparator : AiPageSeparatorBase
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IWatchSettingsStore _settings;
    private readonly HttpClient _http;

    public OpenAiPageSeparator(IWatchSettingsStore settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
    }

    private WatchSettings Settings => Task.Run(() => _settings.LoadAsync()).GetAwaiter().GetResult();

    public override bool IsConfigured => Settings.AiConfigured;

    protected override bool IncludeImages => Settings.AiSendPageImages;

    protected override async Task<string> CompleteAsync(AiSeparationRequest request, CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var content = request.Parts.Select(part => part.ImagePath is { } path
                ? (object)new { type = "image_url", image_url = new { url = PageImageEncoder.ToJpegDataUrl(path), detail = "auto" } }
                : new { type = "text", text = part.Text ?? string.Empty })
            .ToList();
        return await OpenAiChatClient.CompleteAsync(
            _http,
            settings.AiEndpoint!,
            settings.AiApiKey,
            string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel,
            [
                new { role = "system", content = request.SystemMessage },
                new { role = "user", content }
            ],
            cancellationToken).ConfigureAwait(false);
    }
}
