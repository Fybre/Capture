using System.IO.Compression;
using Capture.Core.CaptureProfiles;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Watch;
using Capture.Storage;

namespace Capture.Tests;

public class CaptureBackupServiceTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (var folder in _folders.Where(Directory.Exists))
            Directory.Delete(folder, recursive: true);
    }

    private string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "capture-backup-" + Guid.NewGuid().ToString("N"));
        _folders.Add(folder);
        return folder;
    }

    private sealed class Install
    {
        public Install(string root)
        {
            Paths = new AppPaths(root);
            Settings = new JsonWatchSettingsStore(Paths, new NullOsCredentialStore());
            Profiles = new JsonCaptureProfileStore(Paths);
            RedactionSets = new JsonRedactionEntitySetStore(Paths);
            Backup = new CaptureBackupService(Paths, Settings, Profiles, RedactionSets);
        }

        public AppPaths Paths { get; }
        public JsonWatchSettingsStore Settings { get; }
        public JsonCaptureProfileStore Profiles { get; }
        public JsonRedactionEntitySetStore RedactionSets { get; }
        public CaptureBackupService Backup { get; }
    }

    private static async Task<CaptureProfile> SeedAsync(Install install)
    {
        var typeId = Guid.NewGuid();
        var profile = new CaptureProfile
        {
            Name = "Invoices",
            DocumentTypes =
            [
                new DocumentTypeDefinition
                {
                    Id = typeId,
                    Name = "Invoice",
                    Fields = [new IndexField { Name = "Invoice No" }],
                    Exports = [new ExportDefinition { Name = "Web", Type = ExportType.Rest, RestBearerToken = "rest-secret" }]
                }
            ]
        };
        var sample = Path.Combine(install.Paths.CaptureProfileDirectory(profile.Id), "samples", typeId.ToString("N"), "sample.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(sample)!);
        await File.WriteAllTextAsync(sample, "%PDF sample");
        profile.DocumentTypes[0].SampleFileName = sample;
        await install.Profiles.SaveAsync(profile);

        await install.RedactionSets.SaveAsync(new RedactionEntitySet { Name = "Medical", Entities = ["PERSON", "MEDICAL_LICENSE"] });
        await install.Settings.SaveAsync(new WatchSettings
        {
            HasCompletedFirstRunSetup = true,
            AiApiKey = "sk-secret",
            AiModel = "gpt-test",
            WatchFolders = [new WatchFolderEntry { Folder = "/scans", CaptureProfileId = profile.Id }]
        });
        await File.WriteAllTextAsync(install.Paths.AiFieldCatalogPath, "[{\"id\":\"custom\"}]");
        return profile;
    }

    [Fact]
    public async Task A_backup_restores_everything_into_an_empty_install()
    {
        var source = new Install(TempFolder());
        var profile = await SeedAsync(source);
        var zip = Path.Combine(TempFolder(), "backup.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

        var manifest = await source.Backup.CreateAsync(zip, BackupParts.All, includeCredentials: true, appVersion: "0.8.16");

        Assert.Equal(1, manifest.CaptureProfileCount);
        Assert.Equal(1, manifest.RedactionSetCount);

        var target = new Install(TempFolder());
        var backup = await target.Backup.ReadAsync(zip);
        Assert.Equal("0.8.16", backup.Manifest.AppVersion);
        Assert.False(backup.HasScripts);
        await target.Backup.RestoreAsync(backup);

        var restored = Assert.Single(await target.Profiles.GetAllAsync());
        Assert.Equal(profile.Id, restored.Id);
        var sample = restored.DocumentTypes[0].SampleFileName!;
        Assert.StartsWith(target.Paths.CaptureProfileDirectory(profile.Id), sample);
        Assert.Equal("%PDF sample", await File.ReadAllTextAsync(sample));
        Assert.Equal("rest-secret", restored.DocumentTypes[0].Exports[0].RestBearerToken);

        Assert.Equal("Medical", Assert.Single(await target.RedactionSets.GetAllAsync()).Name);
        var settings = await target.Settings.LoadAsync();
        Assert.Equal("gpt-test", settings.AiModel);
        Assert.Equal("sk-secret", settings.AiApiKey);
        Assert.Equal(profile.Id, Assert.Single(settings.WatchFolders).CaptureProfileId);
        Assert.Equal("[{\"id\":\"custom\"}]", await File.ReadAllTextAsync(target.Paths.AiFieldCatalogPath));
    }

    [Fact]
    public async Task Without_credentials_the_file_holds_no_secrets_and_restoring_keeps_current_ones()
    {
        var source = new Install(TempFolder());
        await SeedAsync(source);
        var zip = Path.Combine(TempFolder(), "backup.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

        await source.Backup.CreateAsync(zip, BackupParts.All, includeCredentials: false, appVersion: null);

        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".json", StringComparison.Ordinal)))
            {
                using var reader = new StreamReader(entry.Open());
                var text = await reader.ReadToEndAsync();
                Assert.DoesNotContain("sk-secret", text);
                Assert.DoesNotContain("rest-secret", text);
            }
        }

        // Restoring over the same install keeps the credentials it already has.
        await source.Backup.RestoreAsync(await source.Backup.ReadAsync(zip));
        Assert.Equal("sk-secret", (await source.Settings.LoadAsync()).AiApiKey);
        var profile = Assert.Single(await source.Profiles.GetAllAsync());
        Assert.Equal("rest-secret", profile.DocumentTypes[0].Exports[0].RestBearerToken);
    }

    [Fact]
    public async Task Restoring_keeps_profiles_that_are_not_in_the_backup()
    {
        var source = new Install(TempFolder());
        await SeedAsync(source);
        var zip = Path.Combine(TempFolder(), "backup.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        await source.Backup.CreateAsync(zip, BackupParts.All, includeCredentials: false, appVersion: null);

        var target = new Install(TempFolder());
        await target.Profiles.SaveAsync(new CaptureProfile { Name = "Local only" });
        await target.Backup.RestoreAsync(await target.Backup.ReadAsync(zip));

        Assert.Equal(["Invoices", "Local only"], (await target.Profiles.GetAllAsync()).Select(profile => profile.Name));
    }

    [Fact]
    public async Task Only_the_chosen_parts_are_backed_up_and_restored()
    {
        var source = new Install(TempFolder());
        await SeedAsync(source);
        var zip = Path.Combine(TempFolder(), "backup.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);

        await source.Backup.CreateAsync(zip, BackupParts.Settings | BackupParts.CaptureProfiles, includeCredentials: false, appVersion: null);
        var backup = await source.Backup.ReadAsync(zip);
        Assert.Equal(BackupParts.Settings | BackupParts.CaptureProfiles, backup.Parts);

        var target = new Install(TempFolder());
        await target.Backup.RestoreAsync(backup, BackupParts.CaptureProfiles);

        Assert.Single(await target.Profiles.GetAllAsync());
        Assert.Empty(await target.RedactionSets.GetAllAsync());
        Assert.NotEqual("gpt-test", (await target.Settings.LoadAsync()).AiModel);
    }

    [Fact]
    public async Task A_settings_file_from_the_old_export_restores_as_settings_only()
    {
        var file = Path.Combine(TempFolder(), "capture-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "{\"startView\": \"preview\", \"aiModel\": \"gpt-old\", \"aiApiKey\": \"(not exported)\", \"watchFolders\": []}");

        var install = new Install(TempFolder());
        await install.Settings.SaveAsync(new WatchSettings { AiApiKey = "sk-current" });
        var backup = await install.Backup.ReadAsync(file);
        Assert.Equal(BackupParts.Settings, backup.Parts);
        Assert.False(backup.Manifest.IncludesCredentials);

        await install.Backup.RestoreAsync(backup);

        var settings = await install.Settings.LoadAsync();
        Assert.Equal("gpt-old", settings.AiModel);
        Assert.Equal("sk-current", settings.AiApiKey);
    }

    [Fact]
    public async Task A_capture_profile_json_is_not_mistaken_for_settings()
    {
        var file = Path.Combine(TempFolder(), "profile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "{\"name\": \"Invoices\", \"documentTypes\": []}");

        var install = new Install(TempFolder());
        await Assert.ThrowsAsync<InvalidDataException>(() => install.Backup.ReadAsync(file));
    }

    [Fact]
    public async Task A_file_that_is_not_a_backup_is_rejected()
    {
        var zip = Path.Combine(TempFolder(), "other.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntry("readme.txt");

        var install = new Install(TempFolder());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => install.Backup.ReadAsync(zip));
        Assert.Equal("That file isn't a Capture backup.", error.Message);
    }
}
