using System.Globalization;
using System.Text.Json;
using Capture.App.Converters;
using Capture.App.ViewModels;
using Capture.Core.CaptureProfiles;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Export;

namespace Capture.Tests;

public class FieldConditionTests
{
    private static FieldCondition When(params FieldConditionRule[] rules) => new() { Rules = [.. rules] };

    private static FieldConditionRule Rule(string field, ConditionOperator op, params string[] values) =>
        new() { FieldName = field, Operator = op, Values = [.. values] };

    private static IndexValue Kind(string value) => new()
    {
        FieldName = "Document Kind",
        Kind = FieldKind.Lookup,
        Value = value,
        Confidence = 100,
        LookupOptions =
        [
            new LookupOption { Key = "Invoice", Value = "INV" },
            new LookupOption { Key = "Credit Note", Value = "CRN" },
            new LookupOption { Key = "Statement", Value = "STM" }
        ]
    };

    [Theory]
    [InlineData("INV", false)]
    [InlineData("CRN", false)]
    [InlineData("STM", true)]
    [InlineData("", true)]
    public void Is_one_of_matches_any_listed_value(string kind, bool expectInactive)
    {
        var reference = new IndexValue
        {
            FieldName = "Invoice Number",
            Mandatory = true,
            Condition = When(Rule("Document Kind", ConditionOperator.IsOneOf, "INV", "CRN"))
        };

        FieldConditions.Apply([Kind(kind), reference]);

        Assert.Equal(expectInactive, reference.IsConditionInactive);
        Assert.Equal(!expectInactive, reference.IsMissing);
    }

    [Fact]
    public void A_lookup_rule_also_matches_the_option_label()
    {
        var reason = new IndexValue
        {
            FieldName = "Credit Reason",
            Condition = When(Rule("document kind", ConditionOperator.IsOneOf, " credit note "))
        };

        FieldConditions.Apply([Kind("CRN"), reason]);

        Assert.False(reason.IsConditionInactive);
    }

    [Fact]
    public void Is_not_one_of_filled_in_and_empty()
    {
        var country = new IndexValue { FieldName = "Country", Value = "AU" };
        var amount = new IndexValue { FieldName = "Amount", Value = "" };
        var notListed = new IndexValue { FieldName = "A", Condition = When(Rule("Country", ConditionOperator.IsNotOneOf, "NZ", "US")) };
        var filled = new IndexValue { FieldName = "B", Condition = When(Rule("Amount", ConditionOperator.IsFilledIn)) };
        var empty = new IndexValue { FieldName = "C", Condition = When(Rule("Amount", ConditionOperator.IsEmpty)) };

        FieldConditions.Apply([country, amount, notListed, filled, empty]);

        Assert.False(notListed.IsConditionInactive);
        Assert.True(filled.IsConditionInactive);
        Assert.False(empty.IsConditionInactive);
    }

    [Fact]
    public void All_needs_every_rule_and_any_needs_one()
    {
        IndexValue[] Values(ConditionMatch match, out IndexValue target)
        {
            target = new IndexValue
            {
                FieldName = "Tax",
                Condition = new FieldCondition
                {
                    Match = match,
                    Rules = [Rule("Document Kind", ConditionOperator.IsOneOf, "INV"), Rule("Amount", ConditionOperator.IsFilledIn)]
                }
            };
            return [Kind("INV"), new IndexValue { FieldName = "Amount" }, target];
        }

        FieldConditions.Apply(Values(ConditionMatch.All, out var all));
        FieldConditions.Apply(Values(ConditionMatch.Any, out var any));

        Assert.True(all.IsConditionInactive);
        Assert.False(any.IsConditionInactive);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("Yes")]
    public void Yes_no_rules_accept_any_spelling(string stored)
    {
        var approved = new IndexValue { FieldName = "Approved", Format = FieldFormat.Boolean, Value = stored };
        var approver = new IndexValue { FieldName = "Approver", Condition = When(Rule("Approved", ConditionOperator.IsOneOf, "Yes")) };

        FieldConditions.Apply([approved, approver]);

        Assert.False(approver.IsConditionInactive);
    }

    [Fact]
    public void A_field_testing_an_inactive_field_sees_it_as_empty()
    {
        var kind = Kind("STM");
        var reason = new IndexValue
        {
            FieldName = "Credit Reason",
            Value = "Damaged",
            Condition = When(Rule("Document Kind", ConditionOperator.IsOneOf, "CRN"))
        };
        var approver = new IndexValue
        {
            FieldName = "Reason Approver",
            Condition = When(Rule("Credit Reason", ConditionOperator.IsFilledIn))
        };

        FieldConditions.Apply([approver, reason, kind]);

        Assert.True(reason.IsConditionInactive);
        Assert.True(approver.IsConditionInactive);
        Assert.Equal("Damaged", reason.Value);
    }

    [Fact]
    public void Clear_when_inactive_empties_the_value()
    {
        var reason = new IndexValue
        {
            FieldName = "Credit Reason",
            Value = "Damaged",
            Confidence = 90,
            Condition = new FieldCondition { Rules = [Rule("Document Kind", ConditionOperator.IsOneOf, "CRN")], ClearWhenInactive = true }
        };

        var changed = FieldConditions.Apply([Kind("INV"), reason]);

        Assert.True(changed);
        Assert.Equal(string.Empty, reason.Value);
    }

    [Fact]
    public void A_document_field_can_test_a_batch_field()
    {
        var batch = new IndexValue { FieldName = "Department", Value = "Finance" };
        var costCentre = new IndexValue
        {
            FieldName = "Cost Centre",
            Condition = When(Rule("Department", ConditionOperator.IsOneOf, "finance"))
        };

        FieldConditions.Apply([costCentre], [batch]);

        Assert.False(costCentre.IsConditionInactive);
    }

    [Fact]
    public void Switched_off_or_ruleless_conditions_always_use_the_field()
    {
        var off = new IndexValue
        {
            FieldName = "A",
            IsConditionInactive = true,
            Condition = new FieldCondition { Enabled = false, Rules = [Rule("Missing", ConditionOperator.IsFilledIn)] }
        };
        var none = new IndexValue { FieldName = "B", Condition = new FieldCondition() };

        FieldConditions.Apply([off, none]);

        Assert.False(off.IsConditionInactive);
        Assert.False(none.IsConditionInactive);
    }

    [Fact]
    public void Inactive_fields_do_not_hold_up_ready_status()
    {
        var reason = new IndexValue
        {
            FieldName = "Credit Reason",
            Mandatory = true,
            ValidationError = "Not a date",
            Condition = When(Rule("Document Kind", ConditionOperator.IsOneOf, "CRN"))
        };
        IndexValue[] values = [Kind("INV"), reason];

        FieldConditions.Apply(values);

        Assert.Equal(DocumentStatus.Ready, IndexFormat.StatusFor(values, 80));
    }

    [Fact]
    public void Describe_reads_as_plain_english_with_option_labels()
    {
        var condition = When(
            Rule("Document Kind", ConditionOperator.IsOneOf, "INV", "CRN", "STM"),
            Rule("Country", ConditionOperator.IsNotOneOf, "NZ"),
            Rule("Amount", ConditionOperator.IsFilledIn));

        var text = FieldConditions.Describe(condition, name => name == "Document Kind" ? Kind("").LookupOptions : []);

        Assert.Equal("Document Kind is Invoice, Credit Note, or Statement, Country is not NZ, and Amount is filled in", text);
    }

    [Fact]
    public void Applicator_evaluates_conditions_after_extraction()
    {
        var fields = new List<IndexField>
        {
            new() { Name = "Approved", Kind = FieldKind.Text, Format = FieldFormat.Boolean, DefaultValueTemplate = "0" },
            new()
            {
                Name = "Approver",
                Kind = FieldKind.Text,
                Mandatory = true,
                Condition = When(Rule("Approved", ConditionOperator.IsOneOf, "Yes"))
            }
        };

        var values = new ProfileApplicator().Apply(fields, []);

        var approver = values.Single(value => value.FieldName == "Approver");
        Assert.True(approver.IsConditionInactive);
        Assert.Same(fields[1].Condition, approver.Condition);
    }

    private sealed class CapturingWriter : IExportWriter
    {
        public ExportType Type => ExportType.Csv;
        public IReadOnlyList<IndexValue> Seen { get; private set; } = [];

        public Task<ExportResult> ExportAsync(ExportDefinition definition, ExportDocumentContext context, CancellationToken cancellationToken = default)
        {
            Seen = context.IndexValues;
            return Task.FromResult(new ExportResult(true, null));
        }
    }

    [Fact]
    public async Task Export_leaves_out_inactive_values_without_changing_the_document()
    {
        var writer = new CapturingWriter();
        var profile = new DocumentTypeDefinition { Exports = [new ExportDefinition { Enabled = true, Type = ExportType.Csv }] };
        var reason = new IndexValue { FieldName = "Credit Reason", Value = "Damaged", IsConditionInactive = true };

        await new ProfileExportRunner([writer]).RunAsync(profile, new CaptureDocument { OriginalFileName = "a.pdf", StoredPath = "/tmp/a.pdf" }, [reason]);

        Assert.Equal(string.Empty, writer.Seen.Single().Value);
        Assert.Equal("Damaged", reason.Value);
    }

    [Fact]
    public void Condition_survives_a_json_round_trip()
    {
        var field = new IndexField
        {
            Name = "Approver",
            Condition = new FieldCondition
            {
                Match = ConditionMatch.Any,
                WhenInactive = InactiveFieldBehavior.Hide,
                ClearWhenInactive = true,
                Rules = [Rule("Approved", ConditionOperator.IsOneOf, "Yes")]
            }
        };

        var copy = JsonSerializer.Deserialize<IndexField>(JsonSerializer.Serialize(field))!;

        Assert.Equal(ConditionMatch.Any, copy.Condition!.Match);
        Assert.Equal(InactiveFieldBehavior.Hide, copy.Condition.WhenInactive);
        Assert.True(copy.Condition.ClearWhenInactive);
        Assert.Equal(["Yes"], copy.Condition.Rules.Single().Values);
    }

    // ── Review panel and Table mode ────────────────────────────────────────────────────────────────

    private static (DocumentRow Row, IndexValue Kind, IndexValue Reason) ReviewDocument(InactiveFieldBehavior behavior)
    {
        var kind = Kind("INV");
        var reason = new IndexValue
        {
            FieldName = "Credit Reason",
            Value = "Damaged",
            Mandatory = true,
            Condition = new FieldCondition { Rules = [Rule("Document Kind", ConditionOperator.IsOneOf, "CRN")], WhenInactive = behavior }
        };
        var row = new DocumentRow(new CaptureDocument { OriginalFileName = "a.pdf", StoredPath = "/tmp/a.pdf" });
        row.SetDocumentIndexes([kind, reason]);
        return (row, kind, reason);
    }

    [Fact]
    public void Review_row_is_disabled_with_a_reason_while_rules_do_not_match()
    {
        var (row, kind, reason) = ReviewDocument(InactiveFieldBehavior.Disable);
        var review = new IndexValueRow(reason, 80, null) { OptionsFor = name => name == "Document Kind" ? kind.LookupOptions : [] };

        Assert.True(review.IsShown);
        Assert.True(review.IsConditionDisabled);
        Assert.False(review.IsEditable);
        Assert.False(review.HasFlag);
        Assert.Equal("Used when Document Kind is Credit Note", review.ConditionHint);
        Assert.Equal(DocumentStatus.Ready, row.Document.Status);

        kind.Value = "CRN";
        row.RecalcStatus();
        review.RefreshCondition();

        Assert.True(review.IsEditable);
        Assert.False(review.IsConditionDisabled);
    }

    [Fact]
    public void Hidden_fields_vanish_from_review_and_table_cells()
    {
        var (row, _, reason) = ReviewDocument(InactiveFieldBehavior.Hide);
        var review = new IndexValueRow(reason, 80, null);

        Assert.False(review.IsShown);
        Assert.Equal(string.Empty, IndexCellTextConverter.Instance.Convert(
            row, typeof(string), new IndexCellBinding("Credit Reason", false), CultureInfo.InvariantCulture));
        Assert.DoesNotContain("Credit Reason", row.IndexesSummary);
    }

    // ── Designer ──────────────────────────────────────────────────────────────────────────────────

    private static FieldCollectionEditorViewModel Designer() => new(
    [
        new IndexField
        {
            Name = "Document Kind",
            Kind = FieldKind.Lookup,
            LookupOptions = [new LookupOption { Key = "Invoice", Value = "INV" }, new LookupOption { Key = "Credit Note", Value = "CRN" }]
        },
        new IndexField { Name = "Approved", Kind = FieldKind.Text, Format = FieldFormat.Boolean },
        new IndexField { Name = "Credit Reason", Kind = FieldKind.Text }
    ]);

    [Fact]
    public void Designer_rule_offers_lookup_options_and_writes_the_condition()
    {
        var editor = Designer();
        var reason = editor.Fields.Single(field => field.Name == "Credit Reason");
        editor.SelectedField = reason;

        reason.AddConditionRuleCommand.Execute(null);
        var rule = reason.ConditionRules.Single();
        Assert.Equal(["Document Kind", "Approved"], rule.FieldChoices);
        Assert.Equal(["Invoice", "Credit Note"], rule.ValueChoices.Select(choice => choice.Label));

        rule.ValueChoices.Single(choice => choice.Label == "Credit Note").IsSelected = true;
        reason.SelectedInactiveBehavior = FieldRow.InactiveBehaviorChoices.Single(choice => choice.Value == InactiveFieldBehavior.Hide);

        var condition = reason.Field.Condition!;
        Assert.True(condition.HasRules);
        Assert.Equal(InactiveFieldBehavior.Hide, condition.WhenInactive);
        Assert.Equal(["CRN"], condition.Rules.Single().Values);
        Assert.Equal("Used when Document Kind is Credit Note.", reason.ConditionSummary);
    }

    [Fact]
    public void Designer_rule_for_a_yes_no_field_offers_yes_and_no_and_free_text_otherwise()
    {
        var editor = Designer();
        var reason = editor.Fields.Single(field => field.Name == "Credit Reason");
        reason.AddConditionRuleCommand.Execute(null);
        var rule = reason.ConditionRules.Single();

        rule.FieldName = "Approved";
        Assert.Equal(["Yes", "No"], rule.ValueChoices.Select(choice => choice.Label));

        var kind = editor.Fields.Single(field => field.Name == "Approved");
        kind.Format = FieldFormat.String;
        rule.RefreshChoices();
        Assert.True(rule.IsFreeText);
        rule.ValuesText = "A\n  B  \n\n";
        Assert.Equal(["A", "B"], reason.Field.Condition!.Rules.Single().Values);
    }

    [Fact]
    public void Turning_the_condition_off_keeps_its_rules()
    {
        var editor = Designer();
        var reason = editor.Fields.Single(field => field.Name == "Credit Reason");
        reason.AddConditionRuleCommand.Execute(null);

        reason.IsConditional = false;

        Assert.False(reason.Field.Condition!.Enabled);
        Assert.Single(reason.Field.Condition.Rules);
        Assert.Equal("This field is always used.", reason.ConditionSummary);
    }

    [Fact]
    public void Renaming_a_field_updates_rules_that_test_it()
    {
        var editor = Designer();
        var reason = editor.Fields.Single(field => field.Name == "Credit Reason");
        reason.AddConditionRuleCommand.Execute(null);
        var renamed = new List<(string, string)>();
        editor.FieldRenamed = (oldName, newName) => renamed.Add((oldName, newName));

        editor.Fields.Single(field => field.Name == "Document Kind").Name = "Kind";

        Assert.Equal("Kind", reason.Field.Condition!.Rules.Single().FieldName);
        Assert.Equal("Kind", reason.ConditionRules.Single().FieldName);
        Assert.Contains(("Document Kind", "Kind"), renamed);
    }
}
