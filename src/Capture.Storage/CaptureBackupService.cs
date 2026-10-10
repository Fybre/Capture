using System.IO.Compression;
using System.Text.Json;
using Capture.Core.CaptureProfiles;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Watch;

namespace Capture.Storage;

/// <summary>The parts of Capture's configuration a backup can hold.</summary>
[Flags]
public enum BackupParts
{
    None = 0,
    Settings = 1,
    CaptureProfiles = 2,
    RedactionSets = 4,
    AiFieldCatalog = 8,
    All = Settings | CaptureProfiles | RedactionSets | AiFieldCatalog
}

/// <summary>What's available to back up right now.</summary>
public sealed record BackupInventory(int CaptureProfileCount, int RedactionSetCount, bool HasAiFieldCatalog);

/// <summary>Describes a backup file: what it holds, read before restoring so the person can confirm.</summary>
public sealed record CaptureBackupManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string? AppVersion { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IncludesCredentials { get; init; }
    public int CaptureProfileCount { get; init; }
    public int RedactionSetCount { get; init; }
    public bool HasSettings { get; init; } = true;
    public bool HasAiFieldCatalog { get; init; }
}

/// <summary>A backup read from disk and ready to restore.</summary>
public sealed class CaptureBackup
{
    internal CaptureBackup(string path, CaptureBackupManifest manifest, WatchSettings? settings,
        IReadOnlyList<CaptureProfile> profiles, IReadOnlyList<RedactionEntitySet> redactionSets, bool isArchive = true)
    {
        Path = path;
        IsArchive = isArchive;
        Manifest = manifest;
        Settings = settings;
        CaptureProfiles = profiles;
        RedactionSets = redactionSets;
    }

    public string Path { get; }

    /// <summary>False for a plain settings .json saved by older versions' Export settings, which holds
    /// settings only.</summary>
    public bool IsArchive { get; }

    public CaptureBackupManifest Manifest { get; }
    public WatchSettings? Settings { get; }
    public IReadOnlyList<CaptureProfile> CaptureProfiles { get; }
    public IReadOnlyList<RedactionEntitySet> RedactionSets { get; }

    /// <summary>The parts this backup actually holds.</summary>
    public BackupParts Parts =>
        (Settings is not null ? BackupParts.Settings : BackupParts.None)
        | (CaptureProfiles.Count > 0 ? BackupParts.CaptureProfiles : BackupParts.None)
        | (RedactionSets.Count > 0 ? BackupParts.RedactionSets : BackupParts.None)
        | (Manifest.HasAiFieldCatalog ? BackupParts.AiFieldCatalog : BackupParts.None);

    /// <summary>True when any profile carries C# scripts, which run fully trusted once restored.</summary>
    public bool HasScripts => CaptureProfiles.Any(CaptureBackupService.HasScripts);
}

/// <summary>Backs up and restores Capture's whole configuration as one .zip: settings, capture profiles
/// (with their designer sample files), custom redaction sets, and the AI field catalog. Documents are
/// not included. Laid out the same way as the data folder, so the file can also be inspected by hand.</summary>
public sealed class CaptureBackupService(
    IAppPaths paths,
    IWatchSettingsStore settingsStore,
    ICaptureProfileStore profileStore,
    IRedactionEntitySetStore redactionSetStore)
{
    private const string ManifestEntry = "manifest.json";
    private const string SettingsEntry = "settings.json";
    private const string AiCatalogEntry = "ai-field-catalog.json";
    private const string ProfilesFolder = "capture-profiles/";
    private const string RedactionSetsFolder = "redaction-sets/";
    private const string ProfileFileName = "capture-profile.json";
    private const string SamplesFolder = "samples";

    public async Task<BackupInventory> GetInventoryAsync(CancellationToken cancellationToken = default) => new(
        (await profileStore.GetAllAsync(cancellationToken).ConfigureAwait(false)).Count,
        (await redactionSetStore.GetAllAsync(cancellationToken).ConfigureAwait(false)).Count,
        File.Exists(paths.AiFieldCatalogPath));

    /// <summary>Writes the chosen <paramref name="parts"/> to <paramref name="zipPath"/>. Without
    /// <paramref name="includeCredentials"/>, the AI API key, Therefore password/token and REST export
    /// tokens are replaced with a placeholder; restoring such a backup keeps whatever credentials are
    /// already configured.</summary>
    public async Task<CaptureBackupManifest> CreateAsync(string zipPath, BackupParts parts, bool includeCredentials, string? appVersion, CancellationToken cancellationToken = default)
    {
        var settings = parts.HasFlag(BackupParts.Settings) ? await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false) : null;
        var profiles = parts.HasFlag(BackupParts.CaptureProfiles) ? await profileStore.GetAllAsync(cancellationToken).ConfigureAwait(false) : [];
        var redactionSets = parts.HasFlag(BackupParts.RedactionSets) ? await redactionSetStore.GetAllAsync(cancellationToken).ConfigureAwait(false) : [];
        var includeCatalog = parts.HasFlag(BackupParts.AiFieldCatalog) && File.Exists(paths.AiFieldCatalogPath);

        if (!includeCredentials && settings is not null)
        {
            settings.AiApiKey = CredentialRedaction.Redact(settings.AiApiKey);
            settings.ThereforePassword = CredentialRedaction.Redact(settings.ThereforePassword);
            settings.ThereforeBearerToken = CredentialRedaction.Redact(settings.ThereforeBearerToken);
        }
        if (!includeCredentials)
        {
            foreach (var export in profiles.SelectMany(AllExports))
                export.RestBearerToken = CredentialRedaction.Redact(export.RestBearerToken);
        }

        var manifest = new CaptureBackupManifest
        {
            AppVersion = appVersion,
            IncludesCredentials = includeCredentials,
            CaptureProfileCount = profiles.Count,
            RedactionSetCount = redactionSets.Count,
            HasSettings = settings is not null,
            HasAiFieldCatalog = includeCatalog
        };

        // Write to a temporary file first so a failure never leaves a half-written backup behind.
        var temporary = zipPath + ".partial";
        try
        {
            await using (var file = File.Create(temporary))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                await WriteJsonAsync(archive, ManifestEntry, manifest, cancellationToken).ConfigureAwait(false);
                if (settings is not null)
                    await WriteJsonAsync(archive, SettingsEntry, settings, cancellationToken).ConfigureAwait(false);
                foreach (var profile in profiles)
                {
                    var folder = $"{ProfilesFolder}{profile.Id:N}/";
                    await WriteJsonAsync(archive, folder + ProfileFileName, profile, cancellationToken).ConfigureAwait(false);
                    var samples = Path.Combine(paths.CaptureProfileDirectory(profile.Id), SamplesFolder);
                    if (Directory.Exists(samples))
                    {
                        foreach (var sample in Directory.EnumerateFiles(samples, "*", SearchOption.AllDirectories))
                        {
                            var relative = Path.GetRelativePath(paths.CaptureProfileDirectory(profile.Id), sample).Replace('\\', '/');
                            archive.CreateEntryFromFile(sample, folder + relative, CompressionLevel.Optimal);
                        }
                    }
                }
                foreach (var set in redactionSets)
                    await WriteJsonAsync(archive, $"{RedactionSetsFolder}{set.Id:N}.json", set, cancellationToken).ConfigureAwait(false);
                if (includeCatalog)
                    archive.CreateEntryFromFile(paths.AiFieldCatalogPath, AiCatalogEntry, CompressionLevel.Optimal);
            }

            File.Move(temporary, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return manifest;
    }

    /// <summary>Reads a backup without changing anything: a .zip from <see cref="CreateAsync"/>, or a
    /// settings .json saved by older versions' Export settings. Throws <see cref="InvalidDataException"/>
    /// when the file isn't a Capture backup, or was made by a newer version of Capture.</summary>
    public async Task<CaptureBackup> ReadAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        if (string.Equals(System.IO.Path.GetExtension(zipPath), ".json", StringComparison.OrdinalIgnoreCase))
            return await ReadSettingsFileAsync(zipPath, cancellationToken).ConfigureAwait(false);

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("That file isn't a Capture backup.");
        }
        using var _ = archive;
        var manifest = await ReadJsonAsync<CaptureBackupManifest>(archive, ManifestEntry, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("That file isn't a Capture backup.");
        if (manifest.FormatVersion > CaptureBackupManifest.CurrentFormatVersion)
            throw new InvalidDataException("That backup was made by a newer version of Capture. Update Capture, then restore it.");

        var settings = await ReadJsonAsync<WatchSettings>(archive, SettingsEntry, cancellationToken).ConfigureAwait(false);
        var profiles = new List<CaptureProfile>();
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.FullName.StartsWith(ProfilesFolder, StringComparison.Ordinal)
                     && entry.FullName.EndsWith("/" + ProfileFileName, StringComparison.Ordinal)))
        {
            if (await ReadJsonAsync<CaptureProfile>(archive, entry.FullName, cancellationToken).ConfigureAwait(false) is { } profile)
                profiles.Add(profile);
        }
        var redactionSets = new List<RedactionEntitySet>();
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.FullName.StartsWith(RedactionSetsFolder, StringComparison.Ordinal)
                     && entry.FullName.EndsWith(".json", StringComparison.Ordinal)))
        {
            if (await ReadJsonAsync<RedactionEntitySet>(archive, entry.FullName, cancellationToken).ConfigureAwait(false) is { } set)
                redactionSets.Add(set);
        }

        return new CaptureBackup(zipPath, manifest with { HasAiFieldCatalog = archive.GetEntry(AiCatalogEntry) is not null }, settings,
            profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), redactionSets);
    }

    private static async Task<CaptureBackup> ReadSettingsFileAsync(string path, CancellationToken cancellationToken)
    {
        WatchSettings? settings;
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            // A capture profile export is JSON too; only accept something shaped like settings.
            var looksLikeSettings = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals("watchFolders", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("startView", StringComparison.OrdinalIgnoreCase));
            settings = looksLikeSettings ? JsonSerializer.Deserialize<WatchSettings>(text, CaptureJsonOptions.Default) : null;
        }
        catch (JsonException)
        {
            settings = null;
        }

        if (settings is null)
            throw new InvalidDataException("That file isn't a Capture backup.");

        var manifest = new CaptureBackupManifest
        {
            CreatedUtc = File.GetLastWriteTimeUtc(path),
            IncludesCredentials = !string.IsNullOrEmpty(settings.AiApiKey) && settings.AiApiKey != CredentialRedaction.Placeholder,
            HasSettings = true
        };
        return new CaptureBackup(path, manifest, settings, [], [], isArchive: false);
    }

    /// <summary>Restores <paramref name="backup"/>: settings are replaced, and capture profiles and redaction
    /// sets are added or, where one with the same identity already exists, replaced. Profiles and sets that
    /// aren't in the backup are left alone. Credentials missing from the backup keep their current values.</summary>
    public async Task RestoreAsync(CaptureBackup backup, BackupParts parts = BackupParts.All, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        using var archive = backup.IsArchive ? ZipFile.OpenRead(backup.Path) : null;

        foreach (var profile in parts.HasFlag(BackupParts.CaptureProfiles) && archive is not null ? backup.CaptureProfiles : [])
        {
            var existing = await profileStore.GetAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            var existingTokens = existing is null
                ? new Dictionary<Guid, string?>()
                : AllExports(existing).GroupBy(export => export.Id).ToDictionary(group => group.Key, group => group.First().RestBearerToken);
            foreach (var export in AllExports(profile))
            {
                if (export.RestBearerToken == CredentialRedaction.Placeholder)
                    export.RestBearerToken = existingTokens.GetValueOrDefault(export.Id);
            }

            // Replace the profile's sample files with the backup's, and point the profile at their new home.
            var directory = paths.CaptureProfileDirectory(profile.Id);
            var samples = Path.Combine(directory, SamplesFolder);
            if (Directory.Exists(samples))
                Directory.Delete(samples, recursive: true);
            var folder = $"{ProfilesFolder}{profile.Id:N}/";
            foreach (var entry in archive.Entries.Where(entry =>
                         entry.FullName.StartsWith(folder + SamplesFolder + "/", StringComparison.Ordinal) && entry.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(directory, entry.FullName[folder.Length..]));
                if (!target.StartsWith(Path.GetFullPath(directory), StringComparison.Ordinal))
                    continue; // never write outside the profile's folder
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            profile.Batch.SampleFileName = RelocateSample(profile.Batch.SampleFileName, directory);
            foreach (var type in profile.DocumentTypes)
                type.SampleFileName = RelocateSample(type.SampleFileName, directory);

            await profileStore.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        foreach (var set in parts.HasFlag(BackupParts.RedactionSets) ? backup.RedactionSets : [])
            await redactionSetStore.SaveAsync(set, cancellationToken).ConfigureAwait(false);

        if (parts.HasFlag(BackupParts.AiFieldCatalog) && archive?.GetEntry(AiCatalogEntry) is { } catalog)
            catalog.ExtractToFile(paths.AiFieldCatalogPath, overwrite: true);

        if (parts.HasFlag(BackupParts.Settings) && backup.Settings is { } settings)
        {
            var current = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            settings.AiApiKey = CredentialRedaction.PreserveIfRedacted(settings.AiApiKey, current.AiApiKey ?? string.Empty);
            settings.ThereforePassword = CredentialRedaction.PreserveIfRedacted(settings.ThereforePassword, current.ThereforePassword ?? string.Empty);
            settings.ThereforeBearerToken = CredentialRedaction.PreserveIfRedacted(settings.ThereforeBearerToken, current.ThereforeBearerToken ?? string.Empty);
            // Restoring is a deliberate setup step; never send someone back through first-run setup.
            settings.HasCompletedFirstRunSetup = true;
            await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool HasScripts(CaptureProfile profile)
    {
        static bool FieldScripts(IEnumerable<IndexField> fields) => fields.Any(field =>
            !string.IsNullOrWhiteSpace(field.ScriptExpression)
            || !string.IsNullOrWhiteSpace(field.ButtonScriptSource)
            || !string.IsNullOrWhiteSpace(field.PostProcessScript));

        return profile.Batch.Scripts.Any(script => !string.IsNullOrWhiteSpace(script.Source))
            || FieldScripts(profile.Batch.Fields)
            || profile.DocumentTypes.Any(type =>
                type.Scripts.Any(script => !string.IsNullOrWhiteSpace(script.Source)) || FieldScripts(type.Fields));
    }

    private static IEnumerable<ExportDefinition> AllExports(CaptureProfile profile) =>
        profile.DocumentTypes.SelectMany(type => type.Exports);

    /// <summary>Sample paths are absolute, so a restored profile (on another machine, or in the Store
    /// version) has to be pointed at where its samples were just extracted.</summary>
    private static string? RelocateSample(string? original, string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(original))
            return original;
        var parts = original.Replace('\\', '/').Split('/');
        var index = Array.LastIndexOf(parts, SamplesFolder);
        if (index < 0)
            return original;
        var relocated = Path.Combine([profileDirectory, .. parts[index..]]);
        return File.Exists(relocated) ? relocated : original;
    }

    private static async Task WriteJsonAsync<T>(ZipArchive archive, string name, T value, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, CaptureJsonOptions.Default, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T?> ReadJsonAsync<T>(ZipArchive archive, string name, CancellationToken cancellationToken) where T : class
    {
        if (archive.GetEntry(name) is not { } entry)
            return null;
        try
        {
            await using var stream = entry.Open();
            return await JsonSerializer.DeserializeAsync<T>(stream, CaptureJsonOptions.Default, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The backup's {name} couldn't be read: {ex.Message}", ex);
        }
    }
}
