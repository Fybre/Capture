using Capture.App.Services;
using Capture.App.ViewModels;
using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Paths;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
using Capture.Core.Store;
using Capture.Core.Watch;
using Capture.Export;
using Capture.Ocr;
using Capture.Pdf;
using Capture.Scanner;
using Capture.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Capture.App.Hosting;

public static class ServiceConfiguration
{
    public static IServiceCollection AddCapture(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<IDocumentStore, SqliteDocumentStore>();
        services.AddSingleton<ILatticeStore, JsonLatticeStore>();
        services.AddSingleton<IPdfRasterizer, PdfiumRasterizer>();
        services.AddSingleton<IImagePageImporter, SkiaImagePageImporter>();
        services.AddSingleton<IPdfTextExtractor, PdfPigTextExtractor>();
        services.AddSingleton<IPdfSubsetWriter, PdfPigSubsetWriter>();
        services.AddSingleton<IOcrEngine, TesseractCliOcrEngine>();
        services.AddSingleton<ILatticeBuilder, LatticeBuilder>();
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(90) });
        services.AddSingleton<IAiExtractor, OpenAiExtractor>();
        services.AddSingleton<IBarcodeDecoder, ZxingBarcodeDecoder>();
        services.AddSingleton<IBlankPageDetector, InkCoverageBlankPageDetector>();
        services.AddSingleton<IPreIndexStep, ClassicSeparatorStep>();
        // IPostIndexStep has no implementations registered yet — DI resolves IEnumerable<IPostIndexStep>
        // to an empty collection, so the pipeline hook in MainViewModel is a no-op until Phase 4 adds one.
        services.AddSingleton<IDocumentImporter, DocumentImporter>();
        services.AddSingleton<IIndexValueStore, JsonIndexValueStore>();
        services.AddSingleton<IProfileApplicator, ProfileApplicator>();
        services.AddSingleton<IProfileStore, JsonProfileStore>();
        services.AddSingleton<IBatchProfileStore, JsonBatchProfileStore>();
        services.AddSingleton<IProfileSampleService, ProfileSampleService>();
        services.AddSingleton<IWatchSettingsStore, JsonWatchSettingsStore>();
        services.AddSingleton<IAiFieldCatalogStore, JsonAiFieldCatalogStore>();
        services.AddSingleton<IWatchFolderService, WatchFolderService>();
        services.AddSingleton<IScanSource, UnavailableScanSource>();
        services.AddSingleton<IExportAdapter, ThereforeExportAdapter>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IProfileDialogService, ProfileDialogService>();
        services.AddSingleton<IBatchProfileDialogService, BatchProfileDialogService>();
        services.AddSingleton<ISettingsDialogService, SettingsDialogService>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<ProfilesViewModel>();
        services.AddTransient<BatchProfilesViewModel>();
        services.AddTransient<SettingsViewModel>();
        return services;
    }
}
