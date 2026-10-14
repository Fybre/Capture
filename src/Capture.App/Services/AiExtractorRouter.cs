using Capture.Core.Indexing;
using Capture.Core.Profiles;
using Capture.Core.Watch;
using Capture.LocalAi;

namespace Capture.App.Services;

/// <summary>The <see cref="IAiExtractor"/> actually registered for the app — delegates to the cloud
/// <see cref="OpenAiExtractor"/>, the offline <see cref="LocalLlmExtractor"/>, or a no-op extractor
/// based on <c>WatchSettings.AiProvider</c>, loaded fresh on every call (same "read settings live,
/// don't cache" idiom <see cref="OpenAiExtractor.IsConfigured"/> already uses). Everything downstream
/// (<c>ProfileApplicator</c>) keeps depending on the plain interface and needs no changes.</summary>
public sealed class AiExtractorRouter : IAiExtractor
{
    private readonly IWatchSettingsStore _settings;
    private readonly OpenAiExtractor _cloud;
    private readonly LocalLlmExtractor _local;
    private readonly NoneAiExtractor _none = new();

    public AiExtractorRouter(IWatchSettingsStore settings, OpenAiExtractor cloud, LocalLlmExtractor local)
    {
        _settings = settings;
        _cloud = cloud;
        _local = local;
    }

    public bool IsConfigured => CurrentSync().IsConfigured;

    public async Task<IReadOnlyDictionary<Guid, AiExtractedValue>> ExtractAsync(
        string documentText,
        IReadOnlyList<IndexField> fields,
        CancellationToken cancellationToken = default)
    {
        var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        return await current.ExtractAsync(documentText, fields, cancellationToken).ConfigureAwait(false);
    }

    private IAiExtractor CurrentSync() =>
        Select(Task.Run(() => _settings.LoadAsync()).GetAwaiter().GetResult().AiProvider);

    private async Task<IAiExtractor> CurrentAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return Select(settings.AiProvider);
    }

    private IAiExtractor Select(AiProvider provider) => provider switch
    {
        AiProvider.Local => _local,
        AiProvider.None => _none,
        _ => _cloud
    };

    /// <summary>AI extraction disabled — always reports unconfigured and never returns any values,
    /// so AI-kind fields are simply left blank instead of silently falling back to a provider the
    /// user never asked for.</summary>
    private sealed class NoneAiExtractor : IAiExtractor
    {
        public bool IsConfigured => false;

        public Task<IReadOnlyDictionary<Guid, AiExtractedValue>> ExtractAsync(
            string documentText,
            IReadOnlyList<IndexField> fields,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, AiExtractedValue>>(new Dictionary<Guid, AiExtractedValue>());
    }
}

/// <summary>The <see cref="IAiPageSeparator"/> registered for the app: the cloud or local separator by
/// <c>WatchSettings.AiProvider</c>, read on every call like <see cref="AiExtractorRouter"/>. With AI
/// turned off it reports unconfigured, so captures fall back to rules and flag their documents.</summary>
public sealed class AiPageSeparatorRouter : IAiPageSeparator
{
    private readonly IWatchSettingsStore _settings;
    private readonly OpenAiPageSeparator _cloud;
    private readonly LocalLlmPageSeparator _local;

    public AiPageSeparatorRouter(IWatchSettingsStore settings, OpenAiPageSeparator cloud, LocalLlmPageSeparator local)
    {
        _settings = settings;
        _cloud = cloud;
        _local = local;
    }

    public bool IsConfigured => Current() is { IsConfigured: true };

    public Task<IReadOnlyList<AiPageVerdict>> AnalyzeAsync(
        IReadOnlyList<AiSeparationPage> pages,
        IReadOnlyList<AiSeparationType>? types,
        string? guidance = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) =>
        Current() is { } separator
            ? separator.AnalyzeAsync(pages, types, guidance, progress, cancellationToken)
            : Task.FromResult<IReadOnlyList<AiPageVerdict>>([]);

    private IAiPageSeparator? Current() =>
        Task.Run(() => _settings.LoadAsync()).GetAwaiter().GetResult().AiProvider switch
        {
            AiProvider.Local => _local,
            AiProvider.None => null,
            _ => _cloud
        };
}
