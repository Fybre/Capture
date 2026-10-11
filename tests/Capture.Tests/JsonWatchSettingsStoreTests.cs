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
            InboxOrder = InboxOrder.OldestFirst,
            AutoDeleteExportedDocuments = true,
            AutoDeleteExportedDocumentsAfterDays = 45,
            RemoveDocumentsAfterExport = true,
            TrashRetentionDays = 60,
            DuplicateImportBehavior = DuplicateImportBehavior.FlagForReview,
            WatchFolders =
            [
                new WatchFolderEntry { Enabled = true, Folder = "/tmp/invoices", CaptureProfileId = profileId, SettleMilliseconds = 1500 },
                new WatchFolderEntry { Enabled = false, Folder = "/tmp/receipts", CaptureProfileId = otherProfileId, SettleMilliseconds = 3000 }
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
        Assert.Equal(InboxOrder.OldestFirst, loaded.InboxOrder);
        Assert.True(loaded.AutoDeleteExportedDocuments);
        Assert.Equal(45, loaded.AutoDeleteExportedDocumentsAfterDays);
        Assert.True(loaded.RemoveDocumentsAfterExport);
        Assert.Equal(60, loaded.TrashRetentionDays);
        Assert.Equal(DuplicateImportBehavior.FlagForReview, loaded.DuplicateImportBehavior);
        Assert.Equal(2, loaded.WatchFolders.Count);
        Assert.True(loaded.WatchFolders[0].Enabled);
        Assert.Equal("/tmp/invoices", loaded.WatchFolders[0].Folder);
        Assert.Equal(profileId, loaded.WatchFolders[0].CaptureProfileId);
        Assert.Equal(1500, loaded.WatchFolders[0].SettleMilliseconds);
        Assert.False(loaded.WatchFolders[1].Enabled);
        Assert.Equal("/tmp/receipts", loaded.WatchFolders[1].Folder);
        Assert.Equal(otherProfileId, loaded.WatchFolders[1].CaptureProfileId);
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
    public async Task Each_connection_keeps_its_secrets_out_of_the_settings_file()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        var credentialStore = new FakeCredentialStore();
        var store = new JsonWatchSettingsStore(paths, credentialStore);
        var therefore = new ConnectionDefinition
        {
            Name = "Therefore",
            Type = ConnectionType.Therefore,
            TenantName = "sampletenant",
            AuthMethod = ConnectionAuthMethod.Basic,
            Username = "sample.user",
            Password = "th-password-secret"
        };
        var xero = new ConnectionDefinition
        {
            Name = "Xero",
            Type = ConnectionType.Rest,
            BaseUrl = "https://api.xero.com",
            AuthMethod = ConnectionAuthMethod.Bearer,
            Token = "xero-token-secret"
        };

        await store.SaveAsync(new WatchSettings { AiApiKey = "sk-ai-secret", Connections = [therefore, xero] });

        var raw = await File.ReadAllTextAsync(paths.SettingsPath);
        Assert.DoesNotContain("sk-ai-secret", raw);
        Assert.DoesNotContain("th-password-secret", raw);
        Assert.DoesNotContain("xero-token-secret", raw);

        // On Windows, Protect() takes the DPAPI branch instead of the IOsCredentialStore.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("th-password-secret", credentialStore.TryRead($"Connection.{therefore.Id:N}.Password"));
            Assert.Equal("xero-token-secret", credentialStore.TryRead($"Connection.{xero.Id:N}.Token"));
        }

        // A fresh store reads the file, not its cache.
        var loaded = await new JsonWatchSettingsStore(paths, credentialStore).LoadAsync();
        Assert.Equal("sk-ai-secret", loaded.AiApiKey);
        Assert.Equal(["Therefore", "Xero"], loaded.Connections.Select(connection => connection.Name));
        Assert.Equal("th-password-secret", loaded.Connections[0].Password);
        Assert.Equal("xero-token-secret", loaded.Connections[1].Token);
        Assert.All(loaded.Connections, connection => Assert.True(connection.IsConfigured));
    }

    [Fact]
    public async Task An_older_settings_file_becomes_a_therefore_connection()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsPath, """
            {
              "thereforeBaseUrl": "https://sampletenant.thereforeonline.com",
              "thereforeTenantName": "sampletenant",
              "thereforeAuthMethod": "Basic",
              "thereforeUsername": "sample.user",
              "thereforePassword": "th-password-secret"
            }
            """);
        var store = new JsonWatchSettingsStore(paths, new FakeCredentialStore());

        var loaded = await store.LoadAsync();

        var connection = Assert.Single(loaded.Connections);
        Assert.Equal("Therefore", connection.Name);
        Assert.Equal(ConnectionType.Therefore, connection.Type);
        Assert.Equal("sampletenant", connection.TenantName);
        Assert.Equal("sample.user", connection.Username);
        Assert.Equal("th-password-secret", connection.Password);
        Assert.Null(loaded.ThereforeBaseUrl);
        Assert.Same(connection, loaded.FindConnection(null, ConnectionType.Therefore));

        // Saving writes only the new form.
        await store.SaveAsync(loaded);
        var raw = await File.ReadAllTextAsync(paths.SettingsPath);
        Assert.DoesNotContain("thereforeBaseUrl\": \"https", raw);
        Assert.Single((await new JsonWatchSettingsStore(paths, new FakeCredentialStore()).LoadAsync()).Connections);
    }

    [Fact]
    public async Task LastCaptureProfileId_roundtrips_the_None_sentinels_own_fixed_id_like_any_real_profile()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        var store = new JsonWatchSettingsStore(paths, new NullOsCredentialStore());

        // A fresh install has never saved a preference.
        var fresh = await store.LoadAsync();
        Assert.Null(fresh.LastCaptureProfileId);

        // "None" is just BuiltInCaptureProfiles.UnsortedId — a real, permanent Guid, not a null/flag
        // pair — so persisting it round-trips exactly like any other profile's Id would.
        var noneId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await store.SaveAsync(new WatchSettings { LastCaptureProfileId = noneId });

        var afterExplicitNone = await store.LoadAsync();
        Assert.Equal(noneId, afterExplicitNone.LastCaptureProfileId);
    }

    [Fact]
    public async Task LoadAsync_caches_after_the_first_call_and_does_not_hit_the_credential_store_again()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        var credentialStore = new FakeCredentialStore();
        var store = new JsonWatchSettingsStore(paths, credentialStore);
        await store.SaveAsync(new WatchSettings { AiApiKey = "sk-real-secret" });
        // SaveAsync already primed the cache from the settings it was given — reset the read count so
        // this test is purely about whether a *second* LoadAsync call re-reads the credential store.
        credentialStore.ReadCount = 0;

        var first = await store.LoadAsync();
        var second = await store.LoadAsync();

        Assert.Equal("sk-real-secret", first.AiApiKey);
        Assert.Equal("sk-real-secret", second.AiApiKey);
        Assert.Equal(0, credentialStore.ReadCount);
        // Each call must still get its own independent object — mutating one must never affect the
        // other or the store's internal cache.
        Assert.NotSame(first, second);
        second.AiApiKey = "mutated";
        Assert.Equal("sk-real-secret", (await store.LoadAsync()).AiApiKey);
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

        public int ReadCount { get; set; }

        public bool TryStore(string account, string value)
        {
            _stored[account] = value;
            return true;
        }

        public string? TryRead(string account)
        {
            ReadCount++;
            return _stored.GetValueOrDefault(account);
        }
    }
}
