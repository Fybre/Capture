using Capture.App.Services;
using Capture.App.ViewModels;
using Capture.Core.Batches;
using Capture.Core.Diagnostics;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Paths;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Scripting;
using Capture.Core.Store;
using Capture.Core.Watch;
using Capture.Export;
using Capture.LocalAi;
using Capture.Ocr;
using Capture.Pdf;
using Capture.Scanner;
using Capture.Scripting;
using Capture.Storage;
using Capture.Therefore;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App.Hosting;

public static class ServiceConfiguration
{
    public static IServiceCollection AddCapture(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<IDebugLogService, DebugLogService>();
        services.AddSingleton<IDocumentStore, SqliteDocumentStore>();
        services.AddSingleton<ILatticeStore, JsonLatticeStore>();
        services.AddSingleton<IPdfRasterizer, PdfiumRasterizer>();
        services.AddSingleton<IImagePageImporter, SkiaImagePageImporter>();
        services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<IPdfSubsetWriter, PdfPigSubsetWriter>();
        services.AddSingleton<IMergedDocumentWriter, PdfPigMergedDocumentWriter>();
        services.AddSingleton<IOcrEngine, TesseractCliOcrEngine>();
        services.AddSingleton<ILatticeBuilder, LatticeBuilder>();
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(90) });
        services.AddSingleton<OpenAiExtractor>();
        services.AddSingleton<LocalLlmExtractor>();
        services.AddSingleton<IAiExtractor, AiExtractorRouter>();
        services.AddSingleton<ILocalAiModelDownloader, LocalAiModelDownloader>();
        services.AddSingleton<IBarcodeDecoder, ZxingBarcodeDecoder>();
        services.AddSingleton<IFieldScriptRunner, RoslynFieldScriptRunner>();
        services.AddSingleton<IBlankPageDetector, InkCoverageBlankPageDetector>();
        services.AddSingleton<IPreIndexStep, ClassicSeparatorStep>();
        services.AddSingleton<PresidioSidecarLauncher>();
        services.AddSingleton<IPiiDetector, PresidioAnalyzerClient>();
        services.AddSingleton<IRedactionCandidateStore, JsonRedactionCandidateStore>();
        services.AddSingleton<IRedactionEntitySetStore, JsonRedactionEntitySetStore>();
        services.AddSingleton<IRedactedDocumentWriter, SkiaPdfRedactor>();
        services.AddSingleton<RedactionApplier>();
        // Registered as its own concrete type (not just via IPostIndexStep) so MainViewModel can call
        // its DetectAsync directly for a manual "redact this document now" action, independent of the
        // profile-driven post-index pipeline — both resolve to the same singleton instance.
        services.AddSingleton<RedactionDetectionStep>();
        services.AddSingleton<IPostIndexStep>(sp => sp.GetRequiredService<RedactionDetectionStep>());
        services.AddSingleton<IDocumentImporter, DocumentImporter>();
        services.AddSingleton<IPageManagementService, PageManagementService>();
        services.AddSingleton<IIndexValueStore, JsonIndexValueStore>();
        services.AddSingleton<IProfileApplicator, ProfileApplicator>();
        services.AddSingleton<IProfileStore, JsonProfileStore>();
        services.AddSingleton<IBatchProfileStore, JsonBatchProfileStore>();
        services.AddSingleton<IImportProfileStore, JsonImportProfileStore>();
        services.AddSingleton<IProfileSampleService, ProfileSampleService>();
        services.AddSingleton<IWatchSettingsStore, JsonWatchSettingsStore>();
        services.AddSingleton<IAiFieldCatalogStore, JsonAiFieldCatalogStore>();
        services.AddSingleton<IWatchFolderService, WatchFolderService>();
        services.AddSingleton<IScanSource>(_ =>
            OperatingSystem.IsMacOS() ? new MacScanSource()
            : OperatingSystem.IsWindows() ? new WiaScanSource()
            : new UnavailableScanSource());
        services.AddSingleton<IExportWriter, CsvExportWriter>();
        services.AddSingleton<IExportWriter, ThereforeExportWriter>();
        services.AddSingleton<ProfileExportRunner>();
        services.AddSingleton<IThereforeClient, ThereforeClient>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IProfileDialogService, ProfileDialogService>();
        services.AddSingleton<IBatchProfileDialogService, BatchProfileDialogService>();
        services.AddSingleton<ISettingsDialogService, SettingsDialogService>();
        services.AddSingleton<IHelpWindowService, HelpWindowService>();
        services.AddSingleton<IAboutDialogService, AboutDialogService>();
        services.AddSingleton<IConfirmDialogService, ConfirmDialogService>();
        services.AddSingleton<IScriptEditorDialogService, ScriptEditorDialogService>();
        services.AddSingleton<IThereforeCategoryPickerDialogService, ThereforeCategoryPickerDialogService>();
        services.AddSingleton<IToastService, ToastService>();
        services.AddSingleton<IUpdateCheckService, GitHubUpdateCheckService>();
        // Singleton, not transient: App.axaml.cs only ever creates one (the single main window, for the
        // whole process lifetime), and it subscribes to events on singleton services (IWatchFolderService,
        // PresidioSidecarLauncher) at construction with no IDisposable/unsubscribe. A transient
        // registration papers over that — today's single-instance reality means it never bites — but
        // registering it as what it actually is closes the landmine outright instead of relying on nothing
        // ever creating a second instance.
        services.AddSingleton<MainViewModel>();
        services.AddTransient<ProfilesViewModel>();
        services.AddTransient<BatchProfilesViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ThereforeCategoryPickerViewModel>();
        return services;
    }
}
