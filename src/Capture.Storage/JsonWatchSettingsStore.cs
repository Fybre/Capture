using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capture.Core.Paths;
using Capture.Core.Watch;

namespace Capture.Storage;

public sealed class JsonWatchSettingsStore : IWatchSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Capture.WatchSettings.AiApiKey");

    private readonly IAppPaths _paths;

    public JsonWatchSettingsStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public async Task<WatchSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        if (!File.Exists(_paths.SettingsPath))
            return new WatchSettings();

        await using var stream = File.OpenRead(_paths.SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<WatchSettings>(stream, LatticeJson.Options, cancellationToken)
            .ConfigureAwait(false);
        settings ??= new WatchSettings();
        settings.AiApiKey = Unprotect(settings.AiApiKey);
        MigrateLegacyWatchFolder(settings);
        return settings;
    }

    // Settings files written before multi-folder support only had a single Enabled/Folder/
    // ProfileId/SettleMilliseconds set. Turn that into one WatchFolderEntry so existing
    // configurations keep working after upgrading.
    private static void MigrateLegacyWatchFolder(WatchSettings settings)
    {
        if (settings.WatchFolders.Count > 0 || string.IsNullOrWhiteSpace(settings.Folder))
            return;

        settings.WatchFolders.Add(new WatchFolderEntry
        {
            Enabled = settings.Enabled,
            Folder = settings.Folder,
            ProfileId = settings.ProfileId,
            SettleMilliseconds = settings.SettleMilliseconds > 0 ? settings.SettleMilliseconds : 2000
        });
    }

    public async Task SaveAsync(WatchSettings settings, CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        var toSave = new WatchSettings
        {
            WatchFolders = settings.WatchFolders,
            StartView = settings.StartView,
            Theme = settings.Theme,
            AiEndpoint = settings.AiEndpoint,
            AiApiKey = Protect(settings.AiApiKey),
            AiModel = settings.AiModel,
            AiMaxDocumentChars = settings.AiMaxDocumentChars
        };
        await using var stream = File.Create(_paths.SettingsPath);
        await JsonSerializer.SerializeAsync(stream, toSave, LatticeJson.Options, cancellationToken)
            .ConfigureAwait(false);
    }

    // DPAPI is only available on Windows; on other platforms the key is stored as-is.
    private static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText) || !OperatingSystem.IsWindows())
            return plainText;

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? storedValue)
    {
        if (string.IsNullOrEmpty(storedValue) || !OperatingSystem.IsWindows())
            return storedValue;

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(storedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            // Pre-existing plaintext value from before encryption was introduced.
            return storedValue;
        }
        catch (CryptographicException)
        {
            // Pre-existing plaintext value from before encryption was introduced.
            return storedValue;
        }
    }
}
