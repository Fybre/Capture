using Avalonia.Controls;
using Capture.App.ViewModels;
using Capture.App.Views;
using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Paths;
using Capture.Core.Store;
using Capture.Core.Redaction;
using Capture.Core.Scripting;

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
    IHelpWindowService help) : ICaptureProfileDialogService
{
    public async Task ShowAsync(object owner)
    {
        if (owner is not Window window) return;
        var dialog = new CaptureProfilesWindow();
        var viewModel = new CaptureProfilesViewModel(store);
        viewModel.EditRequested = profile => ShowDesignerAsync(dialog, profile);
        viewModel.DeleteConfirmed = profile => confirm.ConfirmAsync(
            dialog, "Delete Capture Profile", $"Delete ‘{profile.Name}’? This cannot be undone.", "Delete");
        dialog.DataContext = viewModel;
        await viewModel.InitializeAsync();
        toasts.AttachHost(dialog);
        try { await dialog.ShowDialog(window); }
        finally { toasts.DetachHost(dialog); }
    }

    private async Task ShowDesignerAsync(Window owner, CaptureProfile profile)
    {
        var designer = new CaptureProfileDesignerWindow();
        dialogs.Host = designer;
        var viewModel = new CaptureProfileDesignerViewModel(
            profile, store, workflow, dialogs,
            paths, pdfs, images, latticeBuilder, barcodes, blanks, applicator,
            scriptEditor, designer, scriptRunner, redactionEntitySets, piiDetector,
            thereforeCategoryPicker, ai, help);
        designer.DataContext = viewModel;
        await viewModel.InitializeAsync();
        toasts.AttachHost(designer);
        try { await designer.ShowDialog(owner); }
        finally { toasts.DetachHost(designer); dialogs.Host = owner; }
    }
}
