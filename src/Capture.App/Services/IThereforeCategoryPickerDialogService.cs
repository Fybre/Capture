using Capture.Therefore;

namespace Capture.App.Services;

public sealed record ThereforeCategorySelection(
    int CategoryNo,
    string CategoryName,
    IReadOnlyList<ThereforeCategoryField> Fields);

public interface IThereforeCategoryPickerDialogService
{
    /// <param name="wasIncluded">Given a loaded category's number and one of its fields, whether the export
    /// already had that field — so re-picking (or repointing to another server) ticks the same fields
    /// rather than all of them. Null ticks every field, as for a new export.</param>
    Task<ThereforeCategorySelection?> ShowAsync(object owner, Guid? connectionId = null,
        Func<int, ThereforeCategoryField, bool>? wasIncluded = null);
}
