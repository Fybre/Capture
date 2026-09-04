using Capture.Core.Batches;
using Capture.Core.Paths;
using Capture.Core.Store;
using Capture.Core.Watch;
using Capture.Storage;

namespace Capture.Tests;

public class JsonWatchSettingsStoreTests
{
    [Fact]
    public async Task Roundtrips_watch_settings()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        // Explicit NullOsCredentialStore — this must never exercise the real macOS Keychain / Linux
        // Secret Service, or a test run would write/overwrite entries in the developer's actual store.
        var store = new JsonWatchSettingsStore(paths, new NullOsCredentialStore());
        var profileId = Guid.NewGuid();
        var otherProfileId = Guid.NewGuid();

        await store.SaveAsync(new WatchSettings
        {
            StartView = WorkspaceMode.Table,
            NoBatchProfileBehavior = NoBatchProfileBehavior.AddToOpenBatch,
            AutoDeleteExportedDocuments = true,
            AutoDeleteExportedDocumentsAfterDays = 45,
            RemoveDocumentsAfterExport = true,
            TrashRetentionDays = 60,
            DuplicateImportBehavior = DuplicateImportBehavior.FlagForReview,
            WatchFolders =
            [
                new WatchFolderEntry { Enabled = true, Folder = "/tmp/invoices", ProfileId = profileId, SettleMilliseconds = 1500 },
                new WatchFolderEntry { Enabled = false, Folder = "/tmp/receipts", ProfileId = otherProfileId, SettleMilliseconds = 3000 }
            ],
            AiEndpoint = "https://api.openai.com/v1",
            AiApiKey = "sk-test",
            AiModel = "gpt-4o-mini",
            AiMaxDocumentChars = 50_000,
            AiProvider = AiProvider.Local,
            LocalAiMaxDocumentChars = 9_000,
            ScanDpi = 300,
            ScanGrayscale = true,
            ScanSource = ScanInputSource.Feeder,
            ScanDuplex = true,
            ScanPreferredDeviceId = "scanner-42"
        });

        var loaded = await store.LoadAsync();

        Assert.Equal(WorkspaceMode.Table, loaded.StartView);
        Assert.Equal(NoBatchProfileBehavior.AddToOpenBatch, loaded.NoBatchProfileBehavior);
        Assert.True(loaded.AutoDeleteExportedDocuments);
        Assert.Equal(45, loaded.AutoDeleteExportedDocumentsAfterDays);
        Assert.True(loaded.RemoveDocumentsAfterExport);
        Assert.Equal(60, loaded.TrashRetentionDays);
        Assert.Equal(DuplicateImportBehavior.FlagForReview, loaded.DuplicateImportBehavior);
        Assert.Equal(2, loaded.WatchFolders.Count);
        Assert.True(loaded.WatchFolders[0].Enabled);
        Assert.Equal("/tmp/invoices", loaded.WatchFolders[0].Folder);
        Assert.Equal(profileId, loaded.WatchFolders[0].ProfileId);
        Assert.Equal(1500, loaded.WatchFolders[0].SettleMilliseconds);
        Assert.False(loaded.WatchFolders[1].Enabled);
        Assert.Equal("/tmp/receipts", loaded.WatchFolders[1].Folder);
        Assert.Equal(otherProfileId, loaded.WatchFolders[1].ProfileId);
        Assert.Equal("https://api.openai.com/v1", loaded.AiEndpoint);
        Assert.Equal("sk-test", loaded.AiApiKey);
        Assert.Equal("gpt-4o-mini", loaded.AiModel);
        Assert.Equal(50_000, loaded.AiMaxDocumentChars);
        Assert.Equal(AiProvider.Local, loaded.AiProvider);
        Assert.Equal(9_000, loaded.LocalAiMaxDocumentChars);
        Assert.True(loaded.AiConfigured);
        Assert.Equal(300, loaded.ScanDpi);
        Assert.True(loaded.ScanGrayscale);
        Assert.Equal(ScanInputSource.Feeder, loaded.ScanSource);
        Assert.True(loaded.ScanDuplex);
        Assert.Equal("scanner-42", loaded.ScanPreferredDeviceId);
        Assert.Equal(Path.Combine(paths.Root, "settings.json"), paths.SettingsPath);
    }

    [Fact]
    public async Task Migrates_a_pre_multi_folder_settings_file()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var profileId = Guid.NewGuid();
        await File.WriteAllTextAsync(paths.SettingsPath, $$"""
            {
              "enabled": true,
              "folder": "/tmp/inbox",
              "profileId": "{{profileId}}",
              "settleMilliseconds": 1500
            }
            """);
        // Explicit NullOsCredentialStore — this must never exercise the real macOS Keychain / Linux
        // Secret Service, or a test run would write/overwrite entries in the developer's actual store.
        var store = new JsonWatchSettingsStore(paths, new NullOsCredentialStore());

        var loaded = await store.LoadAsync();

        var entry = Assert.Single(loaded.WatchFolders);
        Assert.True(entry.Enabled);
        Assert.Equal("/tmp/inbox", entry.Folder);
        Assert.Equal(profileId, entry.ProfileId);
        Assert.Equal(1500, entry.SettleMilliseconds);
    }

    [Fact]
    public async Task Hands_the_ai_api_key_to_the_credential_store_and_reads_it_back()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        var credentialStore = new FakeCredentialStore();
        var store = new JsonWatchSettingsStore(paths, credentialStore);

        await store.SaveAsync(new WatchSettings { AiApiKey = "sk-real-secret" });

        // The plaintext key never lands in settings.json — only a sentinel does.
        var raw = await File.ReadAllTextAsync(paths.SettingsPath);
        Assert.DoesNotContain("sk-real-secret", raw);
        // On Windows, JsonWatchSettingsStore.Protect() takes the DPAPI branch instead of the
        // IOsCredentialStore, so the fake store is legitimately never invoked there.
        if (!OperatingSystem.IsWindows())
            Assert.Equal("sk-real-secret", credentialStore.TryRead("AiApiKey"));

        var loaded = await store.LoadAsync();
        Assert.Equal("sk-real-secret", loaded.AiApiKey);
    }

    [Fact]
    public async Task Stores_the_therefore_password_and_bearer_token_independently_of_the_ai_key()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        var credentialStore = new FakeCredentialStore();
        var store = new JsonWatchSettingsStore(paths, credentialStore);

        await store.SaveAsync(new WatchSettings
        {
            AiApiKey = "sk-ai-secret",
            ThereforeBaseUrl = "https://sampletenant.thereforeonline.com",
            ThereforeTenantName = "sampletenant",
            ThereforeAuthMethod = ThereforeAuthMethod.Basic,
            ThereforeUsername = "sample.user",
            ThereforePassword = "th-password-secret",
            ThereforeBearerToken = "th-bearer-secret"
        });

        var raw = await File.ReadAllTextAsync(paths.SettingsPath);
        Assert.DoesNotContain("sk-ai-secret", raw);
        Assert.DoesNotContain("th-password-secret", raw);
        Assert.DoesNotContain("th-bearer-secret", raw);

        // Each secret lands under its own account name — no collision between the three.
        // On Windows, Protect() takes the DPAPI branch instead of the IOsCredentialStore, so the
        // fake store is legitimately never invoked there.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("sk-ai-secret", credentialStore.TryRead("AiApiKey"));
            Assert.Equal("th-password-secret", credentialStore.TryRead("ThereforePassword"));
            Assert.Equal("th-bearer-secret", credentialStore.TryRead("ThereforeBearerToken"));
        }

        var loaded = await store.LoadAsync();
        Assert.Equal("sk-ai-secret", loaded.AiApiKey);
        Assert.Equal("th-password-secret", loaded.ThereforePassword);
        Assert.Equal("th-bearer-secret", loaded.ThereforeBearerToken);
        Assert.Equal("https://sampletenant.thereforeonline.com", loaded.ThereforeBaseUrl);
        Assert.Equal("sampletenant", loaded.ThereforeTenantName);
        Assert.Equal("sample.user", loaded.ThereforeUsername);
        Assert.True(loaded.ThereforeConfigured);
    }

    [Fact]
    public async Task Falls_back_to_plaintext_when_the_credential_store_is_unavailable()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        var store = new JsonWatchSettingsStore(paths, new NullOsCredentialStore());

        await store.SaveAsync(new WatchSettings { AiApiKey = "sk-real-secret" });
        var loaded = await store.LoadAsync();

        Assert.Equal("sk-real-secret", loaded.AiApiKey);
    }

    private sealed class FakeCredentialStore : IOsCredentialStore
    {
        private readonly Dictionary<string, string> _stored = new();

        public bool TryStore(string account, string value)
        {
            _stored[account] = value;
            return true;
        }

        public string? TryRead(string account) => _stored.GetValueOrDefault(account);
    }
}
