using System.Text.Json;
using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Store;
using Capture.Core.Redaction;
using Capture.Core.Scripting;
using Capture.Core.Watch;
using Capture.Storage;

namespace Capture.App.Services;

public sealed class CaptureProfileDialogService(
    ICaptureProfileStore store,
    CaptureWorkflowService workflow,
    IFileDialogService dialogs,
    IToastService toasts,
    IConfirmDialogService confirm,
    IAppPaths paths,
    IPdfRasterizer pdfs,
    IImagePageImporter images,
    ILatticeBuilder latticeBuilder,
    IBarcodeDecoder barcodes,
    IBlankPageDetector blanks,
    IProfileApplicator applicator,
    IScriptEditorDialogService scriptEditor,
    IFieldScriptRunner scriptRunner,
    IRedactionEntitySetStore redactionEntitySets,
    IPiiDetector piiDetector,
    IThereforeCategoryPickerDialogService thereforeCategoryPicker,
    IAiExtractor ai,
    IHelpWindowService help,
    IDocumentStore documents,
    IWatchSettingsStore watchStore) : ICaptureProfileDialogService
{
    public async Task ShowAsync(object owner)
    {
        if (owner is not Window window) return;
        var dialog = new CaptureProfilesWindow();
        var viewModel = new CaptureProfilesViewModel(store);
        viewModel.EditRequested = profile => ShowDesignerAsync(dialog, profile);
        viewModel.DeleteConfirmed = profile => ConfirmDeleteAsync(dialog, profile);
        viewModel.ExportRequested = profile => ExportProfileAsync(profile);
        viewModel.ExportAllRequested = ExportAllProfilesAsync;
        viewModel.ImportRequested = () => ImportProfileAsync(dialog);
        dialog.DataContext = viewModel;
        await viewModel.InitializeAsync();
        toasts.AttachHost(dialog);
        dialogs.Host = dialog;
        try { await dialog.ShowDialog(window); }
        finally { toasts.DetachHost(dialog); dialogs.Host = window; }
    }

    private async Task ExportProfileAsync(CaptureProfile profile)
    {
        var path = await dialogs.PickSaveJsonFileAsync("Export capture profile", SanitizeFileName(profile.Name) + ".json").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        await WriteProfileJsonAsync(path, profile).ConfigureAwait(true);
    }

    private async Task ExportAllProfilesAsync()
    {
        var all = await store.GetAllAsync().ConfigureAwait(true);
        if (all.Count == 0)
        {
            toasts.ShowInfo("There are no capture profiles to export.");
            return;
        }

        var folder = await dialogs.PickFolderAsync("Export all capture profiles to folder").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(folder)) return;

        var exported = 0;
        foreach (var profile in all)
        {
            var path = Path.Combine(folder, SanitizeFileName(profile.Name) + ".json");
            if (await WriteProfileJsonAsync(path, profile, announce: false).ConfigureAwait(true))
                exported++;
        }
        toasts.ShowSuccess($"Exported {exported} of {all.Count} capture profile{(all.Count == 1 ? string.Empty : "s")} to {folder}");
    }

    private async Task<bool> WriteProfileJsonAsync(string path, CaptureProfile profile, bool announce = true)
    {
        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, profile, CaptureJsonOptions.Default).ConfigureAwait(true);
            if (announce) toasts.ShowSuccess($"Exported '{profile.Name}' to {path}");
            return true;
        }
        catch (Exception ex)
        {
            toasts.ShowError($"Export of '{profile.Name}' failed: {ex.Message}");
            return false;
        }
    }

    private async Task<CaptureProfile?> ImportProfileAsync(Window owner)
    {
        var path = await dialogs.PickJsonFileAsync("Import capture profile").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return null;

        CaptureProfile? profile;
        try
        {
            await using var stream = File.OpenRead(path);
            profile = await JsonSerializer.DeserializeAsync<CaptureProfile>(stream, CaptureJsonOptions.Default).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            toasts.ShowError($"Import failed: {ex.Message}");
            return null;
        }
        if (profile is null)
        {
            toasts.ShowError("That file doesn't contain a valid capture profile.");
            return null;
        }

        if (HasExecutableScripts(profile))
        {
            var proceed = await confirm.ConfirmAsync(
                owner,
                "Imported profile contains scripts",
                "This capture profile includes C# scripts (field scripts, button scripts, or script expressions), " +
                "which run as fully trusted, in-process code with no sandboxing. Only import it if you trust where it came from.",
                confirmText: "Import anyway",
                cancelText: "Cancel").ConfigureAwait(true);
            if (!proceed) return null;
        }

        CaptureProfileImportIds.Reassign(profile);
        profile.Enabled = false;
        profile.CreatedUtc = DateTimeOffset.UtcNow;
        profile.ModifiedUtc = DateTimeOffset.UtcNow;

        var existingNames = (await store.GetAllAsync().ConfigureAwait(true))
            .Select(existing => existing.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        profile.Name = UniqueName(profile.Name, existingNames);

        await store.SaveAsync(profile).ConfigureAwait(true);
        toasts.ShowSuccess($"Imported '{profile.Name}' as a disabled draft — review its settings before enabling it.");
        return profile;
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "capture-profile" : sanitized;
    }

    private static string UniqueName(string baseName, IReadOnlySet<string> existingNames)
    {
        if (!existingNames.Contains(baseName)) return baseName;
        var candidate = $"{baseName} (imported)";
        var suffix = 2;
        while (existingNames.Contains(candidate))
            candidate = $"{baseName} (imported {suffix++})";
        return candidate;
    }

    // Scripts run fully trusted, in-process C# (see capture_profile_prototype_plan.md's scripting
    // non-goals) — an imported profile carrying any must be flagged before it's silently added.
    private static bool HasExecutableScripts(CaptureProfile profile)
    {
        bool HasFieldScripts(IEnumerable<IndexField> fields) => fields.Any(field =>
            !string.IsNullOrWhiteSpace(field.ScriptExpression)
            || !string.IsNullOrWhiteSpace(field.ButtonScriptSource)
            || !string.IsNullOrWhiteSpace(field.PostProcessScript));

        if (profile.Batch.Scripts.Any(script => !string.IsNullOrWhiteSpace(script.Source)) || HasFieldScripts(profile.Batch.Fields))
            return true;
        return profile.DocumentTypes.Any(type =>
            type.Scripts.Any(script => !string.IsNullOrWhiteSpace(script.Source)) || HasFieldScripts(type.Fields));
    }

    // Deleting a profile is irreversible (unlike disabling it), so warn about anything that will be
    // orphaned by the deletion: documents already captured under one of its document types, and watch
    // folders that route imports to it.
    private async Task<bool> ConfirmDeleteAsync(Window dialog, CaptureProfile profile)
    {
        var typeIds = profile.DocumentTypes.Select(type => type.Id).ToHashSet();
        var documentCount = typeIds.Count == 0
            ? 0
            : (await documents.GetAllAsync().ConfigureAwait(true)).Count(document => document.ProfileId is { } id && typeIds.Contains(id));
        var watchFolderCount = (await watchStore.LoadAsync().ConfigureAwait(true)).WatchFolders
            .Count(entry => entry.CaptureProfileId == profile.Id);

        var impact = new List<string>();
        if (documentCount > 0)
            impact.Add($"{documentCount} already-captured document{(documentCount == 1 ? string.Empty : "s")} will no longer resolve their field/export settings");
        if (watchFolderCount > 0)
            impact.Add($"{watchFolderCount} watch folder{(watchFolderCount == 1 ? string.Empty : "s")} pointing to it will stop importing");

        var message = impact.Count == 0
            ? $"Delete '{profile.Name}'? This cannot be undone."
            : $"Delete '{profile.Name}'? This cannot be undone, and {string.Join(", and ", impact)}. " +
              "Consider disabling the profile instead if you need to keep it around for those.";
        return await confirm.ConfirmAsync(dialog, "Delete Capture Profile", message, "Delete").ConfigureAwait(true);
    }

    private async Task ShowDesignerAsync(Window owner, CaptureProfile profile)
    {
        var designer = new CaptureProfileDesignerWindow { ConfirmService = confirm };
        dialogs.Host = designer;
        var viewModel = new CaptureProfileDesignerViewModel(
            profile, store, workflow, dialogs,
            paths, pdfs, images, latticeBuilder, barcodes, blanks, applicator,
            scriptEditor, designer, scriptRunner, redactionEntitySets, piiDetector,
            thereforeCategoryPicker, ai, help, confirm);
        designer.DataContext = viewModel;
        await viewModel.InitializeAsync();
        toasts.AttachHost(designer);
        try { await designer.ShowDialog(owner); }
        finally { toasts.DetachHost(designer); dialogs.Host = owner; }
    }
}
