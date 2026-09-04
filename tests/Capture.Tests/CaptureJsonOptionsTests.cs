using System.Text.Json;
using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Profiles;
using Capture.Core.Watch;
using Capture.Storage;

namespace Capture.Tests;

// Profile/settings export-import (ProfilesViewModel.ExportProfileAsync/ImportProfileAsync,
// BatchProfilesViewModel's equivalents, SettingsViewModel.ExportSettingsAsync/ImportSettingsAsync) all
// serialize through CaptureJsonOptions.Default rather than a dedicated store — this pins that the shape
// round-trips faithfully for each exportable model, independently of any on-disk store.
public class CaptureJsonOptionsTests
{
    [Fact]
    public void Roundtrips_indexing_profile_including_enums_and_nested_fields()
    {
        var profile = new IndexingProfile
        {
            Name = "Invoice",
            Redaction = new RedactionSettings { Enabled = true, Entities = ["PERSON", "EMAIL_ADDRESS"] },
            Fields =
            [
                new IndexField
                {
                    Name = "Total",
                    Kind = FieldKind.Zonal,
                    Format = FieldFormat.Money,
                    Sensitive = true,
                    Zone = new ZoneRect { PageNumber = 1, X = 0.1f, Y = 0.2f, Width = 0.3f, Height = 0.05f }
                }
            ],
            Exports =
            [
                new ExportDefinition
                {
                    Name = "Accounts CSV",
                    Type = ExportType.Csv,
                    OutputFolder = "/tmp/exports",
                    OutputMode = ExportOutputMode.AppendToSharedFile,
                    SharedFileName = "invoices.csv",
                    FileMode = ExportFileMode.Redacted,
                    FieldIds = [Guid.NewGuid()]
                }
            ]
        };

        var json = JsonSerializer.Serialize(profile, CaptureJsonOptions.Default);
        var roundtripped = JsonSerializer.Deserialize<IndexingProfile>(json, CaptureJsonOptions.Default);

        Assert.NotNull(roundtripped);
        Assert.Equal("Invoice", roundtripped!.Name);
        Assert.True(roundtripped.Redaction.Enabled);
        Assert.Equal(["PERSON", "EMAIL_ADDRESS"], roundtripped.Redaction.Entities);
        var field = Assert.Single(roundtripped.Fields);
        Assert.Equal(FieldKind.Zonal, field.Kind);
        Assert.Equal(FieldFormat.Money, field.Format);
        Assert.True(field.Sensitive);
        Assert.NotNull(field.Zone);

        var export = Assert.Single(roundtripped.Exports);
        Assert.Equal("Accounts CSV", export.Name);
        Assert.Equal(ExportType.Csv, export.Type);
        Assert.Equal(ExportOutputMode.AppendToSharedFile, export.OutputMode);
        Assert.Equal("invoices.csv", export.SharedFileName);
        Assert.Equal(ExportFileMode.Redacted, export.FileMode);
        Assert.Single(export.FieldIds);
    }

    [Fact]
    public void Roundtrips_a_list_of_indexing_profiles_as_produced_by_export_all()
    {
        var profiles = new List<IndexingProfile>
        {
            new() { Name = "Invoice" },
            new() { Name = "Receipt" }
        };

        var json = JsonSerializer.Serialize(profiles, CaptureJsonOptions.Default);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        var roundtripped = document.RootElement.EnumerateArray()
            .Select(element => element.Deserialize<IndexingProfile>(CaptureJsonOptions.Default))
            .ToList();

        Assert.Equal(2, roundtripped.Count);
        Assert.Equal(["Invoice", "Receipt"], roundtripped.Select(p => p!.Name));
    }

    [Fact]
    public void Roundtrips_batch_profile_including_trigger_enum()
    {
        var profile = new BatchProfile
        {
            Name = "Barcode batches",
            Trigger = BatchTrigger.Barcode,
            BarcodeFormat = "CODE_128",
            DiscardSeparatorPage = true
        };

        var json = JsonSerializer.Serialize(profile, CaptureJsonOptions.Default);
        var roundtripped = JsonSerializer.Deserialize<BatchProfile>(json, CaptureJsonOptions.Default);

        Assert.NotNull(roundtripped);
        Assert.Equal("Barcode batches", roundtripped!.Name);
        Assert.Equal(BatchTrigger.Barcode, roundtripped.Trigger);
        Assert.Equal("CODE_128", roundtripped.BarcodeFormat);
        Assert.True(roundtripped.DiscardSeparatorPage);
    }

    [Fact]
    public void Roundtrips_import_profile_including_strategy_list_and_match_mode()
    {
        var profile = new ImportProfile
        {
            Name = "Barcode splits",
            MatchMode = SeparationMatchMode.All,
            IndexingProfileIds = [Guid.NewGuid()],
            BatchProfileId = Guid.NewGuid(),
            Strategies =
            [
                new SeparationStrategy
                {
                    Type = SeparationStrategyType.Barcode,
                    BarcodeFormat = "CODE_128",
                    BarcodeValuePattern = "^DOC-",
                    Zone = new ZoneRect { PageNumber = 1, X = 0.1f, Y = 0.2f, Width = 0.3f, Height = 0.05f },
                    ZonePageNumber = 1,
                    DiscardSeparatorPage = true
                }
            ]
        };

        var json = JsonSerializer.Serialize(profile, CaptureJsonOptions.Default);
        var roundtripped = JsonSerializer.Deserialize<ImportProfile>(json, CaptureJsonOptions.Default);

        Assert.NotNull(roundtripped);
        Assert.Equal("Barcode splits", roundtripped!.Name);
        Assert.Equal(SeparationMatchMode.All, roundtripped.MatchMode);
        Assert.Single(roundtripped.IndexingProfileIds);
        Assert.Equal(profile.BatchProfileId, roundtripped.BatchProfileId);

        var strategy = Assert.Single(roundtripped.Strategies);
        Assert.Equal(SeparationStrategyType.Barcode, strategy.Type);
        Assert.Equal("CODE_128", strategy.BarcodeFormat);
        Assert.Equal("^DOC-", strategy.BarcodeValuePattern);
        Assert.NotNull(strategy.Zone);
        Assert.True(strategy.DiscardSeparatorPage);
    }

    [Fact]
    public void Roundtrips_watch_settings_including_watch_folders_and_scan_options()
    {
        var settings = new WatchSettings
        {
            Theme = AppTheme.Dark,
            AiEndpoint = "https://api.openai.com/v1",
            AiApiKey = "sk-test",
            ScanDpi = 300,
            ScanSource = ScanInputSource.Feeder,
            ScanDuplex = true,
            WatchFolders =
            [
                new WatchFolderEntry { Enabled = true, Folder = "/tmp/watch", SettleMilliseconds = 1500 }
            ]
        };

        var json = JsonSerializer.Serialize(settings, CaptureJsonOptions.Default);
        var roundtripped = JsonSerializer.Deserialize<WatchSettings>(json, CaptureJsonOptions.Default);

        Assert.NotNull(roundtripped);
        Assert.Equal(AppTheme.Dark, roundtripped!.Theme);
        Assert.Equal("sk-test", roundtripped.AiApiKey);
        Assert.Equal(300, roundtripped.ScanDpi);
        Assert.Equal(ScanInputSource.Feeder, roundtripped.ScanSource);
        Assert.True(roundtripped.ScanDuplex);
        var folder = Assert.Single(roundtripped.WatchFolders);
        Assert.Equal("/tmp/watch", folder.Folder);
        Assert.Equal(1500, folder.SettleMilliseconds);
    }
}
