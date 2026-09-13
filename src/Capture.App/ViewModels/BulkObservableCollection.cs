using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Capture.App.ViewModels;

/// <summary>An <see cref="ObservableCollection{T}"/> with a real bulk-add: <see cref="AddRange"/> appends
/// every item directly to the underlying list and raises one <see cref="NotifyCollectionChangedAction.Reset"/>
/// notification, instead of the N individual Add notifications a plain <c>foreach (var item in items) Add(item)</c>
/// would raise. A full Inbox reload adding hundreds/thousands of rows one at a time meant the bound
/// DataGrid processed that many incremental collection-changed events for what is, semantically, one
/// bulk replacement — a single Reset is the same signal a DataGrid already handles as "reload everything,"
/// just delivered once instead of once per row.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        var any = false;
        foreach (var item in items)
        {
            Items.Add(item);
            any = true;
        }

        if (!any)
            return;

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
