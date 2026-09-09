using System.Globalization;
using Capture.Core.Lattice;

namespace Capture.Ocr;

public static class TesseractTsvParser
{
    public static OcrResult Parse(string tsv)
    {
        var words = new List<OcrWord>();
        var width = 0;
        var height = 0;

        using var reader = new StringReader(tsv);
        var header = reader.ReadLine();
        if (header is null)
            return new OcrResult { Width = 0, Height = 0, Words = words };

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split('\t');
            if (parts.Length < 12)
                continue;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
                continue;

            if (!float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var left) ||
                !float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var top) ||
                !float.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxWidth) ||
                !float.TryParse(parts[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxHeight))
                continue;

            if (level == 1)
            {
                width = Math.Max(width, (int)Math.Ceiling(left + boxWidth));
                height = Math.Max(height, (int)Math.Ceiling(top + boxHeight));
                continue;
            }

            if (level != 5)
                continue;

            if (!float.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) || confidence < 0)
                continue;

            var text = string.Join('\t', parts.Skip(11)).Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            words.Add(new OcrWord
            {
                Text = text,
                Confidence = confidence,
                X = left,
                Y = top,
                Width = boxWidth,
                Height = boxHeight
            });
        }

        return new OcrResult
        {
            Width = width,
            Height = height,
            Words = words
        };
    }
}
