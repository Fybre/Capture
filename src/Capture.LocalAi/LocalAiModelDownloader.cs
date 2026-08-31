using Capture.Core.Paths;

namespace Capture.LocalAi;

public sealed class LocalAiModelDownloader : ILocalAiModelDownloader
{
    // Hugging Face "resolve" URLs redirect (302) to a signed, time-limited CDN download —
    // HttpClient follows redirects by default, so this is used as-is.
    private const string DownloadUrl =
        "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf";

    private readonly IAppPaths _paths;
    private readonly HttpClient _http;

    public LocalAiModelDownloader(IAppPaths paths, HttpClient http)
    {
        _paths = paths;
        _http = http;
    }

    public string ModelFileName => Path.GetFileName(_paths.LocalAiModelPath);

    public async Task DownloadAsync(Action<double> onProgress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.LocalAiModelsDirectory);
        var tempPath = _paths.LocalAiModelPath + ".tmp";

        using (var response = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                readTotal += read;
                if (totalBytes is > 0)
                    onProgress(Math.Clamp((double)readTotal / totalBytes.Value, 0, 1));
            }
        }

        // Only replace a previous file, or land the first one, once the download is fully complete —
        // never leave a partial file sitting at LocalAiModelPath, which is also the IsConfigured check.
        File.Move(tempPath, _paths.LocalAiModelPath, overwrite: true);
        onProgress(1);
    }
}
