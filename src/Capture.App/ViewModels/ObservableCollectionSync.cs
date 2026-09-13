using System.Collections.ObjectModel;

namespace Capture.App.ViewModels;

/// <summary>Updates an <see cref="ObservableCollection{T}"/> to match a desired sequence using per-index
/// Replace/Add/Remove notifications instead of Clear()+Add(). This matters for any collection bound to a
/// control's own selection (e.g. a ComboBox's ItemsSource driving its SelectedValue): Clear() raises a
/// single Reset notification while the collection is momentarily empty, and a control that reacts to
/// Reset by clearing its own selection can end up displaying nothing even though the desired sequence,
/// once fully applied, still contains a matching item — the picker then needs some later, separate
/// re-sync to recover, which is exactly the kind of timing race that's hard to get right. Never touching
/// an index unless its value actually changed, and never leaving the collection momentarily empty when
/// the desired sequence isn't, avoids the race at its source instead of reacting to it after the fact.</summary>
public static class ObservableCollectionSync
{
    public static void SyncFrom<T>(this ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count)
            {
                if (!EqualityComparer<T>.Default.Equals(target[i], desired[i]))
                    target[i] = desired[i];
            }
            else
            {
                target.Add(desired[i]);
            }
        }
        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }
}
