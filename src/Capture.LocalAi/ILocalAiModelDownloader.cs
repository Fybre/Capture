namespace Capture.LocalAi;

public interface ILocalAiModelDownloader
{
    /// <summary>The exact file this downloads — shown in Settings so the user knows what they're
    /// fetching before starting.</summary>
    string ModelFileName { get; }

    /// <summary>Downloads the model to its final <c>IAppPaths.LocalAiModelPath</c>, reporting 0–1
    /// progress as bytes arrive. Streams to a temporary file first and only moves it into place on
    /// a fully-successful download, so a cancelled/failed attempt never leaves a partial file at the
    /// path <see cref="LocalLlmExtractor.IsConfigured"/> checks for.</summary>
    Task DownloadAsync(Action<double> onProgress, CancellationToken cancellationToken = default);
}
