namespace Capture.Core.Paths;

public interface IAppPaths
{
    string Root { get; }
    string DatabasePath { get; }
    string WorkDirectory { get; }
    string DocumentDirectory(Guid documentId);
    string DocumentOriginalPath(Guid documentId, string originalFileName);
    string DocumentPagesDirectory(Guid documentId);
    string DocumentLatticeDirectory(Guid documentId);
    string DocumentLatticePath(Guid documentId, int pageNumber);
    string DocumentOcrDirectory(Guid documentId);
    string DocumentIndexesPath(Guid documentId);
    string DocumentRedactionCandidatesPath(Guid documentId);
    string DocumentRedactedPath(Guid documentId);
    string BatchIndexesPath(Guid batchId);
    string CaptureProfilesDirectory { get; }
    string CaptureProfileDirectory(Guid captureProfileId);
    string CaptureProfileJsonPath(Guid captureProfileId);
    string RedactionSetsDirectory { get; }
    string RedactionSetDirectory(Guid redactionSetId);
    string RedactionSetJsonPath(Guid redactionSetId);
    string SettingsPath { get; }
    string AiFieldCatalogPath { get; }
    string DebugLogPath { get; }
    string LocalAiModelsDirectory { get; }
    string LocalAiModelPath { get; }
    void EnsureCreated();
}

public sealed class AppPaths : IAppPaths
{
    public const string ApplicationDataDirectoryName = "CaptureV2";

    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(DefaultBaseDirectory, ApplicationDataDirectoryName);
        CaptureProfilesDirectory = Path.Combine(Root, "capture-profiles");
        RedactionSetsDirectory = Path.Combine(Root, "redaction-sets");
        WorkDirectory = Path.Combine(Root, "work");
        DatabasePath = Path.Combine(Root, "capture.db");
        SettingsPath = Path.Combine(Root, "settings.json");
        AiFieldCatalogPath = Path.Combine(Root, "ai-field-catalog.json");
        DebugLogPath = Path.Combine(Root, "logs", "activity.log");
        LocalAiModelsDirectory = Path.Combine(Root, "models");
        LocalAiModelPath = Path.Combine(LocalAiModelsDirectory, "llama-3.2-3b-instruct-q4_k_m.gguf");
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string SettingsPath { get; }
    public string AiFieldCatalogPath { get; }
    public string DebugLogPath { get; }
    public string LocalAiModelsDirectory { get; }
    public string LocalAiModelPath { get; }
    public string CaptureProfilesDirectory { get; }
    public string RedactionSetsDirectory { get; }
    public string WorkDirectory { get; }

    public string DocumentDirectory(Guid documentId) =>
        Path.Combine(WorkDirectory, documentId.ToString("N"));

    public string DocumentOriginalPath(Guid documentId, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".bin";
        return Path.Combine(DocumentDirectory(documentId), "original" + extension);
    }

    public string DocumentPagesDirectory(Guid documentId) =>
        Path.Combine(DocumentDirectory(documentId), "pages");

    public string DocumentLatticeDirectory(Guid documentId) =>
        Path.Combine(DocumentDirectory(documentId), "lattice");

    public string DocumentLatticePath(Guid documentId, int pageNumber) =>
        Path.Combine(DocumentLatticeDirectory(documentId), $"{pageNumber:D4}.json");

    public string DocumentOcrDirectory(Guid documentId) =>
        Path.Combine(DocumentDirectory(documentId), "ocr");

    public string DocumentIndexesPath(Guid documentId) =>
        Path.Combine(DocumentDirectory(documentId), "indexes.json");

    public string DocumentRedactionCandidatesPath(Guid documentId) =>
        Path.Combine(DocumentDirectory(documentId), "redactions.json");

    public string DocumentRedactedPath(Guid documentId) =>
        Path.Combine(DocumentDirectory(documentId), "redacted.pdf");

    public string BatchIndexesPath(Guid batchId) =>
        Path.Combine(WorkDirectory, "batches", batchId.ToString("N"), "indexes.json");

    public string CaptureProfileDirectory(Guid captureProfileId) =>
        Path.Combine(CaptureProfilesDirectory, captureProfileId.ToString("N"));

    public string CaptureProfileJsonPath(Guid captureProfileId) =>
        Path.Combine(CaptureProfileDirectory(captureProfileId), "capture-profile.json");

    public string RedactionSetDirectory(Guid redactionSetId) =>
        Path.Combine(RedactionSetsDirectory, redactionSetId.ToString("N"));

    public string RedactionSetJsonPath(Guid redactionSetId) =>
        Path.Combine(RedactionSetDirectory(redactionSetId), "redaction-set.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CaptureProfilesDirectory);
        Directory.CreateDirectory(RedactionSetsDirectory);
        Directory.CreateDirectory(WorkDirectory);
        Directory.CreateDirectory(LocalAiModelsDirectory);
    }

    private static string DefaultBaseDirectory =>
        OperatingSystem.IsMacOS()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
