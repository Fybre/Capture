using Capture.App.Controls;

namespace Capture.Tests;

public sealed class PagePreviewTests
{
    [Fact]
    public void PreviewAlwaysClipsZoomedContentToItsBounds()
    {
        var preview = new PagePreview();

        Assert.True(preview.ClipToBounds);
    }

    [Fact]
    public void Existing_highlight_editing_can_be_enabled_without_enabling_drawing()
    {
        var preview = new PagePreview { AllowHighlightEdit = true };

        Assert.True(preview.AllowHighlightEdit);
        Assert.False(preview.AllowDraw);
    }
}
