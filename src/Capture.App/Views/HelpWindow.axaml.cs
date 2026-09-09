using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Capture.App.Views;

public partial class HelpWindow : Window
{
    // Built once on first use (help content never changes at runtime) — maps every tab to a lowercased
    // blob of its header plus every TextBlock it contains, so a search only needs a substring check
    // per tab rather than re-walking the visual tree on every keystroke.
    private Dictionary<TabItem, string>? _searchIndex;

    public HelpWindow()
    {
        InitializeComponent();
    }

    public void SelectScriptingTab() => Tabs.SelectedItem = ScriptingTab;

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchIndex ??= BuildSearchIndex();
        var query = SearchBox.Text?.Trim();

        if (string.IsNullOrEmpty(query))
        {
            foreach (var tab in _searchIndex.Keys)
                tab.IsVisible = true;
            SearchStatus.IsVisible = false;
            return;
        }

        var lowered = query.ToLowerInvariant();
        var matchCount = 0;
        TabItem? firstMatch = null;
        foreach (var (tab, text) in _searchIndex)
        {
            var matches = text.Contains(lowered, StringComparison.Ordinal);
            tab.IsVisible = matches;
            if (!matches)
                continue;
            matchCount++;
            firstMatch ??= tab;
        }

        SearchStatus.IsVisible = true;
        SearchStatus.Text = matchCount == 0
            ? "No topics match"
            : $"{matchCount} topic{(matchCount == 1 ? string.Empty : "s")} match";

        // Keep the current tab selected if it's still a match — jumping away mid-read is more
        // disruptive than leaving an unrelated tab temporarily hidden from the rail.
        if (Tabs.SelectedItem is not TabItem { IsVisible: true } && firstMatch is not null)
            Tabs.SelectedItem = firstMatch;
    }

    private Dictionary<TabItem, string> BuildSearchIndex()
    {
        var index = new Dictionary<TabItem, string>();
        foreach (var item in Tabs.Items)
        {
            if (item is not TabItem tab)
                continue;

            var header = tab.Header?.ToString() ?? string.Empty;
            // Walk tab.Content directly, not the TabItem itself — TabControl renders the selected tab's
            // Content through its own shared ContentPresenter, so it never appears under the TabItem's
            // own visual tree. tab.Content is still a fully-built object graph regardless of whether
            // it's currently on screen, since its children were added (and VisualParent-linked) at parse
            // time, not lazily when displayed.
            var body = (tab.Content as Avalonia.Visual)?.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(block => block.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)) ?? [];
            index[tab] = string.Join(' ', new[] { header }.Concat(body!)).ToLowerInvariant();
        }
        return index;
    }
}
