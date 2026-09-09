using Capture.Core.Redaction;

namespace Capture.Tests;

public class BuiltInRedactionSetsTests
{
    [Fact]
    public void Core_has_no_country_specific_entities()
    {
        var core = BuiltInRedactionSets.All.Single(set => set.Id == BuiltInRedactionSets.CoreId);

        Assert.DoesNotContain(core.Entities, entity => entity.StartsWith("AU_") || entity.StartsWith("US_"));
    }

    [Fact]
    public void CoreAu_extends_core_with_only_australian_entities()
    {
        var core = BuiltInRedactionSets.All.Single(set => set.Id == BuiltInRedactionSets.CoreId);
        var coreAu = BuiltInRedactionSets.All.Single(set => set.Id == BuiltInRedactionSets.CoreAuId);

        Assert.All(core.Entities, entity => Assert.Contains(entity, coreAu.Entities));
        var added = coreAu.Entities.Except(core.Entities).ToList();
        Assert.Equal(["AU_TFN", "AU_MEDICARE", "AU_ABN", "AU_ACN"], added);
    }

    [Fact]
    public void CoreUs_extends_core_with_only_us_entities()
    {
        var core = BuiltInRedactionSets.All.Single(set => set.Id == BuiltInRedactionSets.CoreId);
        var coreUs = BuiltInRedactionSets.All.Single(set => set.Id == BuiltInRedactionSets.CoreUsId);

        Assert.All(core.Entities, entity => Assert.Contains(entity, coreUs.Entities));
        var added = coreUs.Entities.Except(core.Entities).ToList();
        Assert.Equal(["US_SSN", "US_DRIVER_LICENSE", "US_BANK_NUMBER", "US_PASSPORT", "MEDICAL_LICENSE"], added);
    }

    [Fact]
    public void Every_entity_across_every_built_in_set_has_a_friendly_name_mapping()
    {
        foreach (var set in BuiltInRedactionSets.All)
        foreach (var entity in set.Entities)
            Assert.Contains(entity, PresidioEntityNames.StandardEntityTypes);
    }
}
