namespace Capture.App.ViewModels;

/// <summary>A standalone, non-selectable row inserted immediately before the first document of each
/// batch, in both Table mode's per-profile grids and the Preview mode Inbox rail. Deliberately its own
/// type rather than a "divider mode" of <see cref="DocumentRow"/> — every interaction handler in
/// MainWindow.axaml.cs already pattern-matches on <c>is DocumentRow</c>/<c>as DocumentRow</c>, so an
/// instance of this type is automatically treated as "not a document" (no selection, no drag source)
/// everywhere, with no extra guarding needed. See MainViewModel.ApplyBatchDividers/BuildDisplayRows for
/// how these get interleaved into each grid's display-only row list.</summary>
public sealed record BatchDividerRow(Guid BatchId, string Label, bool Accent);
