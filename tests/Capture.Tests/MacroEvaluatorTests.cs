using Capture.Core.Indexing;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class MacroEvaluatorTests
{
    [Fact]
    public void Concatenates_literal_counters_date_and_field()
    {
        var segments = new[]
        {
            new MacroSegment { Kind = MacroSegmentKind.DateTime, Text = "yyyyMMdd" },
            new MacroSegment { Kind = MacroSegmentKind.Literal, Text = "-" },
            new MacroSegment { Kind = MacroSegmentKind.BatchCounter, CounterWidth = 3 },
            new MacroSegment { Kind = MacroSegmentKind.Literal, Text = "-" },
            new MacroSegment { Kind = MacroSegmentKind.DocumentCounter, CounterWidth = 3 },
            new MacroSegment { Kind = MacroSegmentKind.Literal, Text = "-" },
            new MacroSegment { Kind = MacroSegmentKind.Field, Text = "InvoiceNo" }
        };

        var value = MacroEvaluator.Evaluate(segments, new MacroContext
        {
            BatchNumber = 7,
            DocumentNumber = 2,
            Timestamp = new DateTimeOffset(2026, 8, 26, 15, 0, 0, TimeSpan.Zero),
            Fields = new Dictionary<string, string> { ["InvoiceNo"] = "00001521" }
        });

        Assert.Equal("20260826-007-002-00001521", value);
    }

    [Fact]
    public void Resolves_profile_name()
    {
        var segments = new[]
        {
            new MacroSegment { Kind = MacroSegmentKind.ProfileName },
            new MacroSegment { Kind = MacroSegmentKind.Literal, Text = "-" },
            new MacroSegment { Kind = MacroSegmentKind.DocumentCounter, CounterWidth = 3 }
        };

        var value = MacroEvaluator.Evaluate(segments, new MacroContext
        {
            DocumentNumber = 4,
            ProfileName = "AP Invoices"
        });

        Assert.Equal("AP Invoices-004", value);
    }

    [Fact]
    public void Profile_name_is_empty_when_not_supplied()
    {
        var value = MacroEvaluator.Evaluate(
            [new MacroSegment { Kind = MacroSegmentKind.ProfileName }],
            new MacroContext());

        Assert.Equal(string.Empty, value);
    }
}
