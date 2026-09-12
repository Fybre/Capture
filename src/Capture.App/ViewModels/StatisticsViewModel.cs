using Capture.Core.CaptureProfiles;
using Capture.Core.Models;
using Capture.Core.Store;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Capture.App.ViewModels;

/// <summary>One labeled count, normalized against the largest value in its own chart, for a simple
/// proportional-bar rendering — no charting library, just a Rectangle whose Width/Height binds to
/// <see cref="Fraction"/> inside a fixed-size track.</summary>
public sealed record StatBar(string Label, int Count, double Fraction)
{
    public string CountDisplay => Count.ToString("N0");
}

/// <summary>Aggregate counts across every document this installation has ever captured — active,
/// trashed, and (via <see cref="CaptureDocument.ExportedUtc"/>) already-exported-and-removed. Computed
/// once when the Statistics window opens; not live-updating, since it's a point-in-time report rather
/// than something a reviewer watches while working.</summary>
public sealed partial class StatisticsViewModel : ViewModelBase
{
    private readonly IDocumentStore _store;
    private readonly ICaptureProfileStore _profiles;

    public StatisticsViewModel(IDocumentStore store, ICaptureProfileStore profiles)
    {
        _store = store;
        _profiles = profiles;
    }

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private int _totalInInbox;
    [ObservableProperty] private int _totalInTrash;
    [ObservableProperty] private int _totalExportedAllTime;
    [ObservableProperty] private IReadOnlyList<StatBar> _statusBreakdown = [];
    [ObservableProperty] private IReadOnlyList<StatBar> _capturedPerDay = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportActivity))]
    private IReadOnlyList<StatBar> _exportedPerDay = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopDocumentTypes))]
    private IReadOnlyList<StatBar> _topDocumentTypes = [];
    public bool HasExportActivity => ExportedPerDay.Any(bar => bar.Count > 0);
    public bool HasTopDocumentTypes => TopDocumentTypes.Count > 0;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var active = await _store.GetAllAsync().ConfigureAwait(true);
            var trashed = await _store.GetTrashedAsync().ConfigureAwait(true);
            var profiles = await _profiles.GetAllAsync().ConfigureAwait(true);
            var all = active.Concat(trashed).ToList();

            TotalInInbox = active.Count;
            TotalInTrash = trashed.Count;
            TotalExportedAllTime = all.Count(document => document.ExportedUtc is not null || document.Status == DocumentStatus.Exported);

            StatusBreakdown = BuildStatusBreakdown(active);
            CapturedPerDay = BuildDailyCounts(all.Select(document => document.CreatedUtc));
            ExportedPerDay = BuildDailyCounts(all.Where(document => document.ExportedUtc is not null).Select(document => document.ExportedUtc!.Value));
            TopDocumentTypes = BuildTopDocumentTypes(all, profiles);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IReadOnlyList<StatBar> BuildStatusBreakdown(IReadOnlyList<CaptureDocument> active)
    {
        var counts = active.GroupBy(document => document.Status)
            .ToDictionary(group => group.Key, group => group.Count());
        // Fixed, meaningful order rather than alphabetical/enum order — a reviewer scans this top to
        // bottom as "how much work is left," so it reads as a pipeline. Only shown when non-zero, except
        // the three statuses the reviewer explicitly cares about (Needs review/Ready/Exported), which
        // always appear so a genuinely empty pipeline still reads as "0 needs review" rather than
        // vanishing entirely.
        var always = new[] { DocumentStatus.NeedsReview, DocumentStatus.Ready, DocumentStatus.Exported };
        var order = new[]
        {
            DocumentStatus.Queued, DocumentStatus.Processing, DocumentStatus.NeedsReview,
            DocumentStatus.Ready, DocumentStatus.Exported, DocumentStatus.Error
        };
        var rows = order
            .Where(status => always.Contains(status) || counts.GetValueOrDefault(status) > 0)
            .Select(status => (Status: status, Count: counts.GetValueOrDefault(status)))
            .ToList();
        var max = Math.Max(1, rows.Count == 0 ? 0 : rows.Max(row => row.Count));
        return rows.Select(row => new StatBar(DisplayName(row.Status), row.Count, row.Count / (double)max)).ToList();
    }

    private static string DisplayName(DocumentStatus status) => status switch
    {
        DocumentStatus.NeedsReview => "Needs review",
        DocumentStatus.Ready => "Ready",
        DocumentStatus.Exported => "Exported",
        DocumentStatus.Error => "Error",
        DocumentStatus.Queued => "Queued",
        DocumentStatus.Processing => "Processing",
        _ => status.ToString()
    };

    private const int DayWindow = 30;

    /// <summary>One bar per calendar day (local time) over the trailing <see cref="DayWindow"/> days,
    /// oldest first, with zero-count days included so a quiet stretch reads as a visible gap rather than
    /// a shorter chart.</summary>
    private static IReadOnlyList<StatBar> BuildDailyCounts(IEnumerable<DateTimeOffset> timestamps)
    {
        var today = DateTimeOffset.Now.Date;
        var counts = timestamps
            .GroupBy(timestamp => timestamp.ToLocalTime().Date)
            .ToDictionary(group => group.Key, group => group.Count());

        var days = Enumerable.Range(0, DayWindow)
            .Select(offset => today.AddDays(-(DayWindow - 1 - offset)))
            .Select(day => (Day: day, Count: counts.GetValueOrDefault(day)))
            .ToList();
        var max = Math.Max(1, days.Count == 0 ? 0 : days.Max(day => day.Count));
        return days.Select(day => new StatBar(day.Day.ToString("MMM d"), day.Count, day.Count / (double)max)).ToList();
    }

    private const int TopDocumentTypeCount = 8;

    private static IReadOnlyList<StatBar> BuildTopDocumentTypes(
        IReadOnlyList<CaptureDocument> all, IReadOnlyList<CaptureProfile> profiles)
    {
        string NameFor(Guid? typeId) => typeId is { } id
            ? profiles.SelectMany(profile => profile.DocumentTypes).FirstOrDefault(type => type.Id == id)?.Name
              ?? "Unknown type"
            : "No profile applied";

        var rows = all.GroupBy(document => NameFor(document.ProfileId))
            .Select(group => (Name: group.Key, Count: group.Count()))
            .OrderByDescending(row => row.Count)
            .Take(TopDocumentTypeCount)
            .ToList();
        var max = Math.Max(1, rows.Count == 0 ? 0 : rows.Max(row => row.Count));
        return rows.Select(row => new StatBar(row.Name, row.Count, row.Count / (double)max)).ToList();
    }
}
