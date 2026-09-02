namespace Capture.App.Services;

public sealed record UpdateCheckResult(bool IsUpdateAvailable, string? LatestVersion, string? ReleaseUrl);

/// <summary>Best-effort, opt-out startup check for a newer Capture release. Never throws — a failed
/// or offline check just reports no update available, so it can be fired-and-forgotten from
/// MainViewModel.InitializeAsync without risking startup.</summary>
public interface IUpdateCheckService
{
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);
}
