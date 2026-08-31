namespace Capture.Core.Paths;

public interface IAppPaths
{
    string Root { get; }
    string DatabasePath { get; }
    string ProfilesDirectory { get; }
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
    string ProfileDirectory(Guid profileId);
    string ProfileJsonPath(Guid profileId);
    string ProfilePagesDirectory(Guid profileId);
    string ProfileLatticeDirectory(Guid profileId);
    string ProfileLatticePath(Guid profileId, int pageNumber);
    string ProfileSamplePath(Guid profileId, string originalFileName);
    string BatchProfilesDirectory { get; }
    string BatchProfileDirectory(Guid batchProfileId);
    string BatchProfileJsonPath(Guid batchProfileId);
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
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(DefaultBaseDirectory, "Capture");
        ProfilesDirectory = Path.Combine(Root, "profiles");
        BatchProfilesDirectory = Path.Combine(Root, "batch-profiles");
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
    public string ProfilesDirectory { get; }
    public string BatchProfilesDirectory { get; }
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

    public string ProfileDirectory(Guid profileId) =>
        Path.Combine(ProfilesDirectory, profileId.ToString("N"));

    public string ProfileJsonPath(Guid profileId) =>
        Path.Combine(ProfileDirectory(profileId), "profile.json");

    public string ProfilePagesDirectory(Guid profileId) =>
        Path.Combine(ProfileDirectory(profileId), "pages");

    public string ProfileLatticeDirectory(Guid profileId) =>
        Path.Combine(ProfileDirectory(profileId), "lattice");

    public string ProfileLatticePath(Guid profileId, int pageNumber) =>
        Path.Combine(ProfileLatticeDirectory(profileId), $"{pageNumber:D4}.json");

    public string ProfileSamplePath(Guid profileId, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".bin";
        return Path.Combine(ProfileDirectory(profileId), "sample" + extension);
    }

    public string BatchProfileDirectory(Guid batchProfileId) =>
        Path.Combine(BatchProfilesDirectory, batchProfileId.ToString("N"));

    public string BatchProfileJsonPath(Guid batchProfileId) =>
        Path.Combine(BatchProfileDirectory(batchProfileId), "batch-profile.json");

    public string RedactionSetDirectory(Guid redactionSetId) =>
        Path.Combine(RedactionSetsDirectory, redactionSetId.ToString("N"));

    public string RedactionSetJsonPath(Guid redactionSetId) =>
        Path.Combine(RedactionSetDirectory(redactionSetId), "redaction-set.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(BatchProfilesDirectory);
        Directory.CreateDirectory(RedactionSetsDirectory);
        Directory.CreateDirectory(WorkDirectory);
        Directory.CreateDirectory(LocalAiModelsDirectory);
    }

    private static string DefaultBaseDirectory =>
        OperatingSystem.IsMacOS()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
