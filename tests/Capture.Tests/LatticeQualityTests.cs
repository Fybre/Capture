using Capture.Core.Lattice;

namespace Capture.Tests;

public class LatticeQualityTests
{
    [Fact]
    public void Real_invoice_text_is_accepted()
    {
        var words = "INVOICE & TAX INVOICE Southern Cross Aluminium Pty Limited"
            .Split(' ')
            .Select(text => new LatticeWord { Text = text, Confidence = 100, Width = 0.1f, Height = 0.02f })
            .ToList();

        Assert.True(LatticeQuality.LooksLikeRealText(words));
    }

    [Fact]
    public void Empty_or_short_text_is_rejected()
    {
        Assert.False(LatticeQuality.LooksLikeRealText([]));
        Assert.False(LatticeQuality.LooksLikeRealText([new LatticeWord { Text = "ab", Confidence = 100 }]));
    }

    [Fact]
    public void High_bit_garbage_is_rejected()
    {
        var words = Enumerable.Range(0, 20)
            .Select(i => new LatticeWord { Text = new string((char)(0xE000 + i), 4), Confidence = 100 })
            .ToList();

        Assert.False(LatticeQuality.LooksLikeRealText(words));
    }
}
