using Capture.Core.Import;
using Capture.Core.Models;

namespace Capture.Tests;

public sealed class DuplicateSourceIdentityTests
{
    [Fact]
    public void Split_documents_from_one_source_are_not_duplicate_imports()
    {
        var sourceImportId = Guid.NewGuid();
        var first = Document("HASH", sourceImportId);
        var second = Document("HASH", sourceImportId);

        Assert.False(DuplicateSourceIdentity.AreDuplicateImports(first, second));
    }

    [Fact]
    public void Same_content_imported_again_is_a_duplicate_import()
    {
        var first = Document("HASH", Guid.NewGuid());
        var second = Document("HASH", Guid.NewGuid());

        Assert.True(DuplicateSourceIdentity.AreDuplicateImports(first, second));
    }

    [Fact]
    public void Different_content_is_not_a_duplicate_import()
    {
        var first = Document("FIRST", Guid.NewGuid());
        var second = Document("SECOND", Guid.NewGuid());

        Assert.False(DuplicateSourceIdentity.AreDuplicateImports(first, second));
    }

    private static CaptureDocument Document(string hash, Guid sourceImportId) => new()
    {
        OriginalFileName = "sample.pdf",
        StoredPath = "/tmp/sample.pdf",
        ContentHash = hash,
        SourceImportId = sourceImportId
    };
}
