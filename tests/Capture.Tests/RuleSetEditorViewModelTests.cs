using Capture.App.ViewModels;
using Capture.Core.Import;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class RuleSetEditorViewModelTests
{
    [Fact]
    public void Round_trip_preserves_existing_rule_configuration()
    {
        var id = Guid.NewGuid();
        var source = new SeparationStrategy
        {
            Id = id,
            Type = SeparationStrategyType.Barcode,
            Name = "Student header",
            PageCount = 3,
            BlankInkPercent = 4,
            Zone = new ZoneRect { PageNumber = 2, X = 0.1f, Y = 0.2f, Width = 0.3f, Height = 0.4f },
            ZonePageNumber = 2,
            BarcodeFormat = "CODE_128",
            BarcodeValuePattern = "^STUDENT:",
            TextPattern = "student"
        };
        var editor = new RuleSetEditorViewModel(
            [source],
            SeparationMatchMode.AtLeast,
            2,
            Enum.GetValues<SeparationStrategyType>(),
            "Start when");

        var result = Assert.Single(editor.ToModels());

        Assert.Equal(id, result.Id);
        Assert.Equal(source.Type, result.Type);
        Assert.Equal(source.Name, result.Name);
        Assert.Equal(source.PageCount, result.PageCount);
        Assert.Equal(source.Zone, result.Zone);
        Assert.Equal(source.BarcodeFormat, result.BarcodeFormat);
        Assert.Equal(source.BarcodeValuePattern, result.BarcodeValuePattern);
        Assert.Equal(SeparationMatchMode.AtLeast, editor.MatchMode);
        Assert.Equal(2, editor.MatchMinimum);
        Assert.True(editor.IsAtLeast);
    }

    [Fact]
    public void Add_and_remove_commands_manage_selection_and_use_the_scope_default()
    {
        var allowed = new[] { SeparationStrategyType.Barcode, SeparationStrategyType.Regex };
        var editor = new RuleSetEditorViewModel(
            [],
            SeparationMatchMode.Any,
            0,
            allowed,
            "Recognise when",
            defaultStrategyType: SeparationStrategyType.Regex);

        editor.AddStrategyCommand.Execute(null);

        var added = Assert.Single(editor.Strategies);
        Assert.Same(added, editor.SelectedStrategy);
        Assert.Equal(SeparationStrategyType.Regex, added.Type);
        Assert.Equal(1, editor.MatchMinimum);
        Assert.Equal(allowed, editor.StrategyTypeOptions);

        editor.RemoveStrategyCommand.Execute(added);

        Assert.Empty(editor.Strategies);
        Assert.Null(editor.SelectedStrategy);
    }

    [Fact]
    public void Editor_uses_plain_language_and_reports_selected_rule_type_changes()
    {
        var editor = new RuleSetEditorViewModel(
            [new SeparationStrategy { Type = SeparationStrategyType.Regex }],
            SeparationMatchMode.Any,
            1,
            Enum.GetValues<SeparationStrategyType>(),
            "Recognise when");
        var changes = 0;
        editor.SelectionChanged = () => changes++;

        editor.SelectedStrategy = editor.Strategies[0];
        editor.SelectedStrategy.Type = SeparationStrategyType.OcrZone;

        Assert.Equal(2, changes);
        Assert.Contains(editor.MatchModeChoices, choice => choice.Label == "Match any rule");
        Assert.Contains(editor.StrategyTypeChoices, choice => choice.Label == "Text in an area");
        Assert.Equal("Text in an area", editor.SelectedStrategy.DisplayLabel);
    }

    [Fact]
    public void Optional_none_mode_hides_and_deselects_rules_without_deleting_them()
    {
        var editor = new RuleSetEditorViewModel(
            [new SeparationStrategy { Type = SeparationStrategyType.Barcode }],
            SeparationMatchMode.Any,
            1,
            Enum.GetValues<SeparationStrategyType>(),
            "Starts a new batch",
            allowNone: true);
        editor.SelectedStrategy = editor.Strategies[0];

        editor.MatchMode = SeparationMatchMode.None;

        Assert.True(editor.IsNone);
        Assert.False(editor.RulesEnabled);
        Assert.Null(editor.SelectedStrategy);
        Assert.Single(editor.Strategies);
        Assert.Equal("None — keep using the current batch", editor.MatchModeChoices[0].Label);
    }

    [Fact]
    public void Barcode_rule_explicitly_switches_between_entire_page_and_a_drawn_area()
    {
        var originalZone = new ZoneRect { PageNumber = 2, X = .1f, Y = .2f, Width = .3f, Height = .4f };
        var row = new SeparationStrategyRow(new SeparationStrategy
        {
            Type = SeparationStrategyType.Barcode,
            Zone = originalZone
        });

        Assert.Equal(BarcodeScanArea.SelectedArea, row.BarcodeScanArea);
        Assert.Contains("page 2", row.BarcodeAreaStatus);

        row.BarcodeScanArea = BarcodeScanArea.EntirePage;

        Assert.Null(row.Zone);
        Assert.Contains("whole page", row.BarcodeAreaStatus);

        row.BarcodeScanArea = BarcodeScanArea.SelectedArea;
        Assert.Contains("Draw", row.BarcodeAreaStatus);
        var replacement = new ZoneRect { PageNumber = 1, X = .2f, Y = .2f, Width = .2f, Height = .2f };
        row.SetZone(replacement);
        Assert.Equal(replacement, row.Zone);
        Assert.Equal(BarcodeScanArea.SelectedArea, row.BarcodeScanArea);
    }
}
