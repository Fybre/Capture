using Capture.Core.Paths;
using Capture.Core.Watch;
using Capture.Storage;

namespace Capture.Tests;

public class JsonWatchSettingsStoreTests
{
    [Fact]
    public async Task Roundtrips_watch_settings()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-watch-settings-" + Guid.NewGuid().ToString("N")));
        var store = new JsonWatchSettingsStore(paths);
        var profileId = Guid.NewGuid();
        var otherProfileId = Guid.NewGuid();

        await store.SaveAsync(new WatchSettings
        {
            StartView = WorkspaceMode.Table,
            WatchFolders =
            [
                new WatchFolderEntry { Enabled = true, Folder = "/tmp/invoices", ProfileId = profileId, SettleMilliseconds = 1500 },
                new WatchFolderEntry { Enabled = false, Folder = "/tmp/receipts", ProfileId = otherProfileId, SettleMilliseconds = 3000 }
            ],
            AiEndpoint = "https://api.openai.com/v1",
            AiApiKey = "sk-test",
            AiModel = "gpt-4o-mini",
            AiMaxDocumentChars = 50_000
        });

        var loaded = await store.LoadAsync();

        Assert.Equal(WorkspaceMode.Table, loaded.StartView);
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
        Assert.True(loaded.AiConfigured);
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
        var store = new JsonWatchSettingsStore(paths);

        var loaded = await store.LoadAsync();

        var entry = Assert.Single(loaded.WatchFolders);
        Assert.True(entry.Enabled);
        Assert.Equal("/tmp/inbox", entry.Folder);
        Assert.Equal(profileId, entry.ProfileId);
        Assert.Equal(1500, entry.SettleMilliseconds);
    }
}
