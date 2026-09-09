using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Storage;

namespace Capture.Tests;

public class JsonCaptureProfileStoreTests
{
    [Fact]
    public async Task Complete_workflow_roundtrips()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-profile-" + Guid.NewGuid().ToString("N")));
        var store = new JsonCaptureProfileStore(paths);
        var ruleId = Guid.NewGuid();
        var profile = new CaptureProfile
        {
            Name = "Student Records",
            Batch = new BatchDefinition
            {
                TriggerPageDisposition = PageDisposition.Consume,
                StartRules = new RuleSet { Rules = [new SeparationStrategy { Id = ruleId, Type = SeparationStrategyType.Barcode, BarcodeValuePattern = "^STUDENT:" }] },
                Fields = [new IndexField { Name = "StudentNumber", BoundaryRuleId = ruleId }]
            },
            DocumentTypes = [new DocumentTypeDefinition
            {
                Name = "Transcript",
                IdentificationStartsNewDocument = true,
                StartRules = new RuleSet { Rules = [new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "TRANSCRIPT" }] },
                Exports = [new ExportDefinition
                {
                    Type = ExportType.Therefore,
                    ThereforeCategoryNo = 8,
                    ThereforeFieldMappings = [new ThereforeFieldMapping
                    {
                        FieldNo = 10,
                        Caption = "Source",
                        ValueSource = ThereforeMappingValueSource.Constant,
                        ConstantValue = "Capture"
                    }]
                }]
            }]
        };

        await store.SaveAsync(profile);
        var loaded = await store.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Student Records", loaded!.Name);
        Assert.Equal(PageDisposition.Consume, loaded.Batch.TriggerPageDisposition);
        Assert.Equal(ruleId, Assert.Single(loaded.Batch.Fields).BoundaryRuleId);
        var loadedType = Assert.Single(loaded.DocumentTypes);
        Assert.True(loadedType.IdentificationStartsNewDocument);
        Assert.Equal("Transcript", loadedType.Name);
        var loadedMapping = Assert.Single(Assert.Single(loadedType.Exports).ThereforeFieldMappings);
        Assert.Equal(ThereforeMappingValueSource.Constant, loadedMapping.ValueSource);
        Assert.Equal("Capture", loadedMapping.ConstantValue);
    }

}
