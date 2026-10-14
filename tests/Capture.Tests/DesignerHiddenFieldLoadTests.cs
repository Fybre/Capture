using Capture.App.ViewModels;
using Capture.Core.CaptureProfiles;
using Capture.Core.Profiles;

namespace Capture.Tests;

/// <summary>Building a field list containing a hidden field used to report a field change before the
/// designer was ready: with a hidden batch field the designer wouldn't open at all, and opening a
/// document type with a hidden field copied the previously open type's scripts and exports into it.</summary>
public sealed class DesignerHiddenFieldLoadTests
{
    [Fact]
    public void A_profile_with_a_hidden_batch_field_opens()
    {
        var profile = new CaptureProfile();
        profile.Batch.Fields.Add(new IndexField { Name = "Shown" });
        profile.Batch.Fields.Add(new IndexField { Name = "Internal", HideFromIndexing = true });

        var designer = new CaptureProfileDesignerViewModel(profile, new NullStore());

        Assert.Equal(["Shown", "Internal"], designer.BatchFields.Fields.Select(field => field.Name));
        Assert.False(designer.HasUnsavedChanges());
    }

    [Fact]
    public void Opening_a_document_type_with_a_hidden_field_keeps_its_own_scripts_and_exports()
    {
        var first = new DocumentTypeDefinition { Name = "Invoice" };
        first.Fields.Add(new IndexField { Name = "Invoice No" });
        first.Scripts.Add(new FieldScript { Name = "Invoice script", Source = "Log.Info(\"a\");" });
        first.SharedScriptSource = "// invoice shared";
        first.Exports.Add(new ExportDefinition { Name = "Invoice export" });

        var second = new DocumentTypeDefinition { Name = "Statement" };
        second.Fields.Add(new IndexField { Name = "Account" });
        second.Fields.Add(new IndexField { Name = "Internal", HideFromIndexing = true });
        second.Scripts.Add(new FieldScript { Name = "Statement script", Source = "Log.Info(\"b\");" });
        second.SharedScriptSource = "// statement shared";
        second.Exports.Add(new ExportDefinition { Name = "Statement export" });

        var profile = new CaptureProfile();
        profile.DocumentTypes.Add(first);
        profile.DocumentTypes.Add(second);
        var designer = new CaptureProfileDesignerViewModel(profile, new NullStore());

        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == first);
        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == second);

        Assert.Equal(["Statement script"], designer.DocumentScripts!.ToModels().Select(script => script.Name));
        Assert.Equal("// statement shared", designer.DocumentScripts.SharedSource);
        Assert.Equal(["Statement export"], designer.DocumentExports.Select(row => row.Definition.Name));
        designer.HasUnsavedChanges(); // writes the open type back into the profile
        Assert.Equal(["Statement script"], second.Scripts.Select(script => script.Name));
        Assert.Equal(["Statement export"], second.Exports.Select(export => export.Name));
        Assert.Equal(["Invoice script"], first.Scripts.Select(script => script.Name));
    }

    [Fact]
    public void A_document_type_that_cannot_be_loaded_is_marked_and_left_as_saved()
    {
        var good = new DocumentTypeDefinition { Name = "Invoice" };
        var broken = new DocumentTypeDefinition { Name = "Statement" };
        broken.Fields.Add(new IndexField { Name = "Account" });
        broken.Scripts.Add(new FieldScript { Name = "Statement script" });
        broken.Exports = null!; // e.g. "exports": null in a hand-edited file
        var profile = new CaptureProfile();
        profile.DocumentTypes.Add(good);
        profile.DocumentTypes.Add(broken);
        var designer = new CaptureProfileDesignerViewModel(profile, new NullStore());

        var brokenNode = designer.Navigation.Single(node => node.DocumentType == broken);
        designer.SelectedNode = brokenNode;

        Assert.True(brokenNode.HasLoadError);
        Assert.True(designer.HasSectionLoadError);
        Assert.Contains("Statement", designer.SectionLoadError);
        Assert.Null(designer.DocumentFields);

        designer.SelectedNode = designer.Navigation.Single(node => node.DocumentType == good);
        designer.HasUnsavedChanges();
        Assert.False(designer.HasSectionLoadError);
        Assert.True(designer.Navigation.Single(node => node.DocumentType == broken).HasLoadError);
        Assert.Equal(["Account"], broken.Fields.Select(field => field.Name));
        Assert.Equal(["Statement script"], broken.Scripts.Select(script => script.Name));
    }

    [Fact]
    public void Batch_settings_that_cannot_be_loaded_are_marked_and_not_overwritten()
    {
        var profile = new CaptureProfile();
        profile.Batch.Fields.Add(new IndexField { Name = "Batch No" });
        profile.Batch.StartRules = null!;

        var designer = new CaptureProfileDesignerViewModel(profile, new NullStore());
        designer.HasUnsavedChanges();

        Assert.True(designer.Navigation.Single(node => node.Section == CaptureDesignerSection.Batch).HasLoadError);
        Assert.Equal(["Batch No"], profile.Batch.Fields.Select(field => field.Name));
    }

    private sealed class NullStore : ICaptureProfileStore
    {
        public Task<IReadOnlyList<CaptureProfile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CaptureProfile>>([]);
        public Task<CaptureProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<CaptureProfile?>(null);
        public Task SaveAsync(CaptureProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
