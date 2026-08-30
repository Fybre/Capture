using Capture.Core.Paths;
using Capture.Core.Redaction;
using Capture.Storage;

namespace Capture.Tests;

public class JsonRedactionCandidateStoreTests
{
    [Fact]
    public async Task Roundtrips_candidates_including_decision_and_score()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-redaction-candidates-" + Guid.NewGuid().ToString("N")));
        var store = new JsonRedactionCandidateStore(paths);
        var documentId = Guid.NewGuid();

        var candidates = new List<RedactionCandidate>
        {
            new()
            {
                Source = RedactionSource.Presidio,
                Label = "PERSON",
                PreviewText = "Jane Doe",
                PageNumber = 1,
                X = 0.1f,
                Y = 0.2f,
                Width = 0.3f,
                Height = 0.04f,
                Score = 0.87f,
                Decision = RedactionDecision.Rejected
            },
            new()
            {
                Source = RedactionSource.SensitiveField,
                Label = "SSN",
                PageNumber = 2,
                X = 0.05f,
                Y = 0.5f,
                Width = 0.2f,
                Height = 0.03f,
                Score = 1f
            }
        };

        await store.SaveAsync(documentId, candidates);
        var loaded = await store.GetAsync(documentId);

        Assert.Equal(2, loaded.Count);
        var first = loaded.Single(item => item.Label == "PERSON");
        Assert.Equal(RedactionSource.Presidio, first.Source);
        Assert.Equal("Jane Doe", first.PreviewText);
        Assert.Equal(1, first.PageNumber);
        Assert.Equal(0.87f, first.Score);
        Assert.Equal(RedactionDecision.Rejected, first.Decision);

        var second = loaded.Single(item => item.Label == "SSN");
        Assert.Equal(RedactionSource.SensitiveField, second.Source);
        Assert.Equal(RedactionDecision.Confirmed, second.Decision);
    }

    [Fact]
    public async Task GetAsync_returns_empty_for_a_document_with_no_saved_candidates()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-redaction-candidates-" + Guid.NewGuid().ToString("N")));
        var store = new JsonRedactionCandidateStore(paths);

        var loaded = await store.GetAsync(Guid.NewGuid());

        Assert.Empty(loaded);
    }
}
