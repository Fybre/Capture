using System.Collections.ObjectModel;
using Capture.App.Services;
using Capture.Core.Watch;
using Capture.Therefore;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public sealed partial class ThereforeTreeNodeRow : ObservableObject
{
    public ThereforeTreeNodeRow(ThereforeTreeNode node)
    {
        Node = node;
        Children = node.Children.Select(child => new ThereforeTreeNodeRow(child)).ToList();
    }

    public ThereforeTreeNode Node { get; }

    public string Name => Node.Name;

    public bool IsCategory => Node.IsCategory;

    public IReadOnlyList<ThereforeTreeNodeRow> Children { get; }
}

public sealed partial class ThereforeCategoryFieldRow : ObservableObject
{
    public ThereforeCategoryFieldRow(ThereforeCategoryField field, bool isIncluded)
    {
        Field = field;
        _isIncluded = isIncluded;
    }

    public ThereforeCategoryField Field { get; }

    public string Caption => Field.Caption;

    public string TypeDisplay => Field.FieldType.ToString();

    public bool Mandatory => Field.Mandatory;

    [ObservableProperty]
    private bool _isIncluded;
}

public partial class ThereforeCategoryPickerViewModel : ViewModelBase
{
    private readonly IWatchSettingsStore _watchSettings;
    private readonly IThereforeClient _client;
    private ThereforeConnectionSettings? _connection;

    public ThereforeCategoryPickerViewModel(IWatchSettingsStore watchSettings, IThereforeClient client)
    {
        _watchSettings = watchSettings;
        _client = client;
    }

    public Action? Close { get; set; }

    public ThereforeCategorySelection? Result { get; private set; }

    public ObservableCollection<ThereforeTreeNodeRow> Tree { get; } = [];

    public ObservableCollection<ThereforeCategoryFieldRow> Fields { get; } = [];

    public bool HasFields => Fields.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseCategoryCommand))]
    private ThereforeTreeNodeRow? _selectedNode;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    public bool HasStatusText => !string.IsNullOrEmpty(StatusText);

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatusText));

    [ObservableProperty]
    private bool _isConfigured;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseCategoryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private int? _selectedCategoryNo;

    [ObservableProperty]
    private string? _selectedCategoryName;

    public async Task InitializeAsync()
    {
        var settings = await _watchSettings.LoadAsync().ConfigureAwait(true);
        IsConfigured = settings.ThereforeConfigured;
        if (!IsConfigured)
        {
            StatusText = "Therefore isn't configured yet — set it up in Settings first.";
            StatusIsError = true;
            return;
        }

        _connection = new ThereforeConnectionSettings
        {
            BaseUrl = settings.ThereforeBaseUrl ?? string.Empty,
            TenantName = settings.ThereforeTenantName,
            AuthMethod = settings.ThereforeAuthMethod == Core.Watch.ThereforeAuthMethod.Bearer
                ? global::Capture.Therefore.ThereforeAuthMethod.Bearer
                : global::Capture.Therefore.ThereforeAuthMethod.Basic,
            Username = settings.ThereforeUsername,
            Password = settings.ThereforePassword,
            BearerToken = settings.ThereforeBearerToken
        };

        IsBusy = true;
        try
        {
            var tree = await _client.GetCategoriesTreeAsync(_connection).ConfigureAwait(true);
            Tree.Clear();
            foreach (var node in tree)
                Tree.Add(new ThereforeTreeNodeRow(node));
            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load categories: {ex.Message}";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseCategory))]
    private async Task UseCategoryAsync()
    {
        if (SelectedNode is not { IsCategory: true } node || _connection is null)
            return;

        IsBusy = true;
        try
        {
            var info = await _client.GetCategoryInfoAsync(_connection, node.Node.ItemNo).ConfigureAwait(true);
            SelectedCategoryNo = info.CategoryNo;
            SelectedCategoryName = info.Name;

            // Label (on-screen caption for its neighbor) and the two counter types (server-generated
            // sequence fields) are confirmed non-mappable — excluded outright, not just hidden.
            var selectable = info.Fields
                .Where(field => field.FieldType is not (ThereforeFieldType.Label or ThereforeFieldType.NumericCounter or ThereforeFieldType.TextCounter))
                .ToList();

            Fields.Clear();
            foreach (var field in selectable)
                Fields.Add(new ThereforeCategoryFieldRow(field, isIncluded: !LooksLikeKeywordTextShadow(field, selectable)));

            OnPropertyChanged(nameof(HasFields));
            StatusText = $"Loaded {Fields.Count} field(s) from \"{info.Name}\"";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load category fields: {ex.Message}";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUseCategory() => SelectedNode is { IsCategory: true } && !IsBusy;

    // Best-effort only, not a confirmed API contract (see the "Therefore export support" plan notes):
    // a non-keyword field whose caption ends in "Text"/"Label" and whose base caption matches a
    // keyword field in the same category is probably that keyword's display-text shadow.
    private static bool LooksLikeKeywordTextShadow(ThereforeCategoryField field, IReadOnlyList<ThereforeCategoryField> allFields)
    {
        if (field.IsSingleKeyword || field.IsMultipleKeyword)
            return false;

        var caption = field.Caption.Trim();
        string? baseCaption = null;
        foreach (var suffix in new[] { " Text", "_Text", " Label", "_Label" })
        {
            if (caption.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                baseCaption = caption[..^suffix.Length].Trim();
                break;
            }
        }

        if (baseCaption is null)
            return false;

        return allFields.Any(other =>
            (other.IsSingleKeyword || other.IsMultipleKeyword)
            && string.Equals(other.Caption.Trim(), baseCaption, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        if (SelectedCategoryNo is { } categoryNo && SelectedCategoryName is not null)
        {
            Result = new ThereforeCategorySelection(
                categoryNo,
                SelectedCategoryName,
                Fields.Where(row => row.IsIncluded).Select(row => row.Field).ToList());
        }

        Close?.Invoke();
    }

    private bool CanOk() => SelectedCategoryNo is not null;

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        Close?.Invoke();
    }
}
