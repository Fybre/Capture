using Capture.Therefore;

namespace Capture.App.Services;

public sealed record ThereforeCategorySelection(
    int CategoryNo,
    string CategoryName,
    IReadOnlyList<ThereforeCategoryField> Fields);

public interface IThereforeCategoryPickerDialogService
{
    Task<ThereforeCategorySelection?> ShowAsync(object owner);
}
