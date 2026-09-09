using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

/// <summary>One labeled cluster of checkboxes (e.g. "Financial") in the Settings redaction-set editor —
/// wraps a Capture.Core.Redaction.EntityGroup with per-entity selection state, plus "All"/"None" bulk
/// shortcuts that toggle every checkbox in the group at once without hiding them.</summary>
public sealed partial class RedactionEntityGroupRow : ObservableObject
{
    public RedactionEntityGroupRow(string name, IEnumerable<string> entityTypes, IReadOnlySet<string> selected)
    {
        Name = name;
        foreach (var type in entityTypes)
            Entities.Add(new RedactionEntityRow(type, selected.Contains(type)));
    }

    public string Name { get; }

    public ObservableCollection<RedactionEntityRow> Entities { get; } = [];

    public IEnumerable<string> SelectedEntities => Entities.Where(entity => entity.IsSelected).Select(entity => entity.Name);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var entity in Entities)
            entity.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var entity in Entities)
            entity.IsSelected = false;
    }
}
