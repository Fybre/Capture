using System.Diagnostics;
using System.Text;
using Capture.Core.Indexing;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Watch;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Capture.LocalAi;

/// <summary>Local, offline AI field extractor — an alternative to the cloud OpenAI-compatible
/// extractor for users who need documents to never leave the machine. Reuses
/// <see cref="AiExtractPrompt"/> for prompt construction/response parsing unchanged (it's already
/// provider-agnostic), and adds grammar-constrained decoding (<see cref="LocalExtractGrammar"/>) —
/// the local-llm-spike project found this was the decisive fix for reliable JSON output, which the
/// cloud extractor doesn't need since it relies on the remote model's own JSON mode instead.
///
/// API usage here mirrors the spike's proven-working harness (local-llm-spike/Spike/Program.cs)
/// closely: a fresh LLamaContext/InteractiveExecutor/ChatSession per call so no KV cache/history
/// carries over between documents, temperature 0, and the dynamic grammar as a hard constraint.
/// </summary>
public sealed class LocalLlmExtractor : IAiExtractor, IDisposable
{
    private const int ContextSize = 4096;
    private const int MinMaxTokens = 220;
    private const int MaxTokensPerField = 70;

    private readonly IWatchSettingsStore _settings;
    private readonly IAppPaths _paths;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private LLamaWeights? _weights;
    private ModelParams? _modelParams;

    // llama.cpp logs verbosely (tensor loading, KV cache sizing, per-layer device assignment, etc.)
    // straight to the console by default, which is fine for the spike harness this was lifted from
    // but not for a console-run app — it drowns out everything else and appears regardless of
    // DebugMode. Redirecting it into Trace instead routes it through the same DebugLogService sink
    // as the rest of the app's diagnostics: silent unless DebugMode is on, captured to a file when it
    // is. Must run before the native library first loads (NativeLibraryConfig rejects changes once
    // LibraryHasLoaded is true), so it's a static constructor on the one class that loads it.
    static LocalLlmExtractor()
    {
        NativeLibraryConfig.All.WithLogCallback((level, message) =>
            Trace.WriteLine($"[llama.cpp {level}] {message.TrimEnd()}"));
    }

    public LocalLlmExtractor(IWatchSettingsStore settings, IAppPaths paths)
    {
        _settings = settings;
        _paths = paths;
    }

    public bool IsConfigured => File.Exists(_paths.LocalAiModelPath);

    public async Task<IReadOnlyDictionary<Guid, AiExtractedValue>> ExtractAsync(
        string documentText,
        IReadOnlyList<IndexField> fields,
        CancellationToken cancellationToken = default)
    {
        if (fields.Count == 0 || !IsConfigured)
            return new Dictionary<Guid, AiExtractedValue>();

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var maxChars = settings.LocalAiMaxDocumentChars > 0 ? settings.LocalAiMaxDocumentChars : 12_000;

        var systemPrompt = AiExtractPrompt.SystemMessage();
        var userPrompt = AiExtractPrompt.UserMessage(documentText, fields, maxChars);
        var grammar = LocalExtractGrammar.Build(fields);

        var weights = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var raw = await RunInferenceAsync(weights, systemPrompt, userPrompt, grammar, fields.Count, cancellationToken)
            .ConfigureAwait(false);
        return AiExtractPrompt.Parse(raw, fields);
    }

    private async Task<LLamaWeights> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_weights is not null)
            return _weights;

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_weights is not null)
                return _weights;

            _modelParams = new ModelParams(_paths.LocalAiModelPath)
            {
                ContextSize = ContextSize,
                GpuLayerCount = 99
            };
            // Loading a ~2GB model is CPU/IO-bound, not cancellable mid-load in LLamaSharp's sync
            // API — run it on a background thread so the caller's cancellation token still applies
            // to everything around it.
            _weights = await Task.Run(() => LLamaWeights.LoadFromFile(_modelParams), cancellationToken)
                .ConfigureAwait(false);
            return _weights;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<string> RunInferenceAsync(
        LLamaWeights weights,
        string systemPrompt,
        string userPrompt,
        string grammar,
        int fieldCount,
        CancellationToken cancellationToken)
    {
        using var context = weights.CreateContext(_modelParams!);
        var executor = new InteractiveExecutor(context);
        var chatHistory = new ChatHistory();
        chatHistory.AddMessage(AuthorRole.System, systemPrompt);
        var session = new ChatSession(executor, chatHistory);
        session.WithHistoryTransform(new LLama.Transformers.PromptTemplateTransformer(weights, withAssistant: true));
        session.WithOutputTransform(new LLamaTransforms.KeywordTextOutputStreamTransform(
            ["User:", "�"], redundancyLength: 5));

        var inferenceParams = new InferenceParams
        {
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0f, Grammar = new Grammar(grammar, "root") },
            MaxTokens = Math.Max(MinMaxTokens, fieldCount * MaxTokensPerField),
            AntiPrompts = ["User:"]
        };

        var response = new StringBuilder();
        await foreach (var text in session.ChatAsync(new ChatHistory.Message(AuthorRole.User, userPrompt), inferenceParams)
            .WithCancellation(cancellationToken))
        {
            response.Append(text);
        }

        return response.ToString().Trim();
    }

    public void Dispose()
    {
        _weights?.Dispose();
        _loadGate.Dispose();
    }
}
