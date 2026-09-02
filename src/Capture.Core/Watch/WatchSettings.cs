using Capture.Core.Indexing;

namespace Capture.Core.Watch;

public sealed class WatchSettings
{
    public List<WatchFolderEntry> WatchFolders { get; set; } = [];

    // Legacy single-folder fields. Kept only so JsonWatchSettingsStore can migrate
    // settings.json files written before multi-folder support existed — new code
    // should read/write WatchFolders instead.
    public bool Enabled { get; set; }
    public string? Folder { get; set; }
    public Guid? ProfileId { get; set; }
    public int SettleMilliseconds { get; set; } = 2000;

    public string? AiEndpoint { get; set; } = "https://api.openai.com/v1";
    public string? AiApiKey { get; set; }
    public string? AiModel { get; set; } = "gpt-4o-mini";
    public int AiMaxDocumentChars { get; set; } = AiExtractPrompt.MaxDocumentChars;

    /// <summary>Which extractor <c>AiExtractorRouter</c> delegates to — the cloud OpenAI-compatible
    /// endpoint above, or a locally-downloaded model (see Capture.LocalAi).</summary>
    public AiProvider AiProvider { get; set; } = AiProvider.OpenAiCompatible;

    /// <summary>Document-text truncation ceiling for the local extractor — deliberately smaller than
    /// <see cref="AiMaxDocumentChars"/>: long-context prefill on CPU is slow and small local models
    /// are weaker at very long contexts anyway.</summary>
    public int LocalAiMaxDocumentChars { get; set; } = 12_000;
    public WorkspaceMode StartView { get; set; } = WorkspaceMode.Preview;
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>When true, MainViewModel asks IUpdateCheckService to compare the running version
    /// against Fybre/Capture's latest GitHub release once per startup, and toasts if a newer one
    /// exists. Best-effort and silent on failure (offline, rate-limited, etc.) — never blocks or
    /// delays startup. Defaults off — a manual "Check for updates" button in the About dialog is
    /// always available regardless of this setting.</summary>
    public bool CheckForUpdatesOnStartup { get; set; }

    /// <summary>When true, a detailed activity log (imports, exports, watch-folder activity, and
    /// errors) is written to a file for troubleshooting — see <c>IDebugLogService</c>.</summary>
    public bool DebugMode { get; set; }

    public Guid? LastImportProfileId { get; set; }
    public Guid? LastBatchProfileId { get; set; }

    public int ScanDpi { get; set; } = 200;

    // A plain bool rather than referencing Capture.Scanner's ScanColorMode enum — Capture.Core has no
    // project dependencies today and this is the only reason it would need one. MainViewModel (which
    // already references Capture.Scanner) maps this to ScanColorMode when building ScanOptions.
    public bool ScanGrayscale { get; set; }
    public ScanInputSource ScanSource { get; set; } = ScanInputSource.Flatbed;
    public bool ScanDuplex { get; set; }

    /// <summary>The scanner to use, by IScanSource-reported Id — null/empty means "the first available
    /// device", which is also the fallback if this device is no longer present.</summary>
    public string? ScanPreferredDeviceId { get; set; }

    public bool AiConfigured =>
        !string.IsNullOrWhiteSpace(AiEndpoint) && !string.IsNullOrWhiteSpace(AiApiKey);

    public string? ThereforeBaseUrl { get; set; }

    /// <summary>Display/prefill convenience only — sent as-is on every request's TenantName header
    /// (see Capture.Therefore.ThereforeClient), never inferred from ThereforeBaseUrl.</summary>
    public string? ThereforeTenantName { get; set; }
    public ThereforeAuthMethod ThereforeAuthMethod { get; set; } = ThereforeAuthMethod.Basic;
    public string? ThereforeUsername { get; set; }
    public string? ThereforePassword { get; set; }
    public string? ThereforeBearerToken { get; set; }

    public bool ThereforeConfigured =>
        !string.IsNullOrWhiteSpace(ThereforeBaseUrl) &&
        (ThereforeAuthMethod == ThereforeAuthMethod.Bearer
            ? !string.IsNullOrWhiteSpace(ThereforeBearerToken)
            : !string.IsNullOrWhiteSpace(ThereforeUsername) && !string.IsNullOrWhiteSpace(ThereforePassword));
}

public enum ScanInputSource
{
    Flatbed = 0,
    Feeder = 1
}

public enum ThereforeAuthMethod
{
    Basic = 0,
    Bearer = 1
}

public enum AiProvider
{
    OpenAiCompatible = 0,
    Local = 1,

    /// <summary>AI extraction is off — AI-kind fields are left blank rather than silently falling
    /// back to a provider the user never configured. Distinct from an unconfigured cloud/local
    /// provider so the UI can say "disabled" instead of showing an error.</summary>
    None = 2
}
