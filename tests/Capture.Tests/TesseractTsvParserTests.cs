using Capture.Ocr;

namespace Capture.Tests;

public class TesseractTsvParserTests
{
    [Fact]
    public void Parses_page_size_and_words()
    {
        var tsv = """
            level	page_num	block_num	par_num	line_num	word_num	left	top	width	height	conf	text
            1	1	0	0	0	0	0	0	1240	1754	-1	
            5	1	1	1	1	1	80	40	120	28	96.12	Invoice
            5	1	1	1	1	2	210	40	90	28	88.5	No
            5	1	1	1	1	3	310	40	70	28	-1	
            """;

        var result = TesseractTsvParser.Parse(tsv);

        Assert.Equal(1240, result.Width);
        Assert.Equal(1754, result.Height);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal("Invoice", result.Words[0].Text);
        Assert.Equal(96.12f, result.Words[0].Confidence, 2);
        Assert.Equal(80, result.Words[0].X);
        Assert.Equal("No", result.Words[1].Text);
    }
}
