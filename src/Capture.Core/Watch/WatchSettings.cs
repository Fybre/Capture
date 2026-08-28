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
    public WorkspaceMode StartView { get; set; } = WorkspaceMode.Preview;
    public AppTheme Theme { get; set; } = AppTheme.System;

    public Guid? LastImportProfileId { get; set; }
    public Guid? LastBatchProfileId { get; set; }

    public bool AiConfigured =>
        !string.IsNullOrWhiteSpace(AiEndpoint) && !string.IsNullOrWhiteSpace(AiApiKey);
}
