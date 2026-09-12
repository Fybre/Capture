namespace Capture.App.Services;

/// <summary>Opens the read-only Statistics window — throughput and status counts across every document
/// this installation has ever captured, not scoped to the current Inbox filter.</summary>
public interface IStatisticsWindowService
{
    Task ShowAsync(object owner);
}
