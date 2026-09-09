using System.Text.Json;
using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Profiles;
using Capture.Storage;

namespace Capture.Tests;

public class CaptureJsonOptionsTests
{
    [Fact]
    public void Roundtrips_capture_profile()
    {
        var typeId = Guid.NewGuid();
        var profile = new CaptureProfile
        {
            Name = "Accounts payable",
            Enabled = false,
            AutoExportReadyDocuments = true,
            DefaultDocumentTypeId = typeId,
            Batch = new BatchDefinition
            {
                StartNewBatchForEachFile = true,
                Fields = [new IndexField { Name = "Batch number", Kind = FieldKind.BatchSeparatorValue }]
            },
            DocumentTypes =
            [
                new DocumentTypeDefinition
                {
                    Id = typeId,
                    Name = "Invoice",
                    Redaction = new RedactionSettings { Enabled = true },
                    StartRules = new RuleSet
                    {
                        Rules = [new SeparationStrategy { Type = SeparationStrategyType.Barcode, BarcodeValuePattern = "^INV-" }]
                    },
                    Fields = [new IndexField { Name = "Total", Kind = FieldKind.Zonal, Format = FieldFormat.Money }],
                    Exports = [new ExportDefinition { Name = "CSV", Type = ExportType.Csv, Enabled = true }]
                }
            ]
        };

        var json = JsonSerializer.Serialize(profile, CaptureJsonOptions.Default);
        var roundtripped = JsonSerializer.Deserialize<CaptureProfile>(json, CaptureJsonOptions.Default);

        Assert.NotNull(roundtripped);
        Assert.Equal("Accounts payable", roundtripped!.Name);
        Assert.False(roundtripped.Enabled);
        Assert.Equal(typeId, roundtripped.DefaultDocumentTypeId);
        Assert.True(roundtripped.AutoExportReadyDocuments);
        Assert.True(roundtripped.Batch.StartNewBatchForEachFile);
        Assert.Equal(SeparationMatchMode.None, roundtripped.Batch.StartRules.MatchMode);
        var documentType = Assert.Single(roundtripped.DocumentTypes);
        Assert.True(documentType.Redaction.Enabled);
        Assert.Single(documentType.StartRules.Rules);
        Assert.Single(documentType.Fields);
        Assert.Single(documentType.Exports);
    }
}
