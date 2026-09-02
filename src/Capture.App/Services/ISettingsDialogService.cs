namespace Capture.App.Services;

/// <summary>What changed while the Settings dialog was open, so the caller knows what (if anything)
/// to refresh — Saved covers WatchSettings (theme, watch folders, AI/Therefore config, ...), while
/// DocumentsChanged covers the separate "Clean up old documents" action, which can delete documents
/// regardless of whether Save was ever clicked.</summary>
public readonly record struct SettingsDialogResult(bool Saved, bool DocumentsChanged);

public interface ISettingsDialogService
{
    Task<SettingsDialogResult> ShowAsync(object owner);
}
