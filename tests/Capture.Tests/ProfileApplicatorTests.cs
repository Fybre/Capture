using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;
using Capture.Core.CaptureProfiles;

namespace Capture.Tests;

public class ProfileApplicatorTests
{
    [Fact]
    public void Boundary_rule_values_overlay_extraction_before_defaults()
    {
        var field = new IndexField { Name = "StudentNumber", Kind = FieldKind.Text, BoundaryRuleId = Guid.NewGuid() };
        var captured = new IndexValue { FieldId = field.Id, FieldName = field.Name, Value = "1001" };
        var values = new ProfileApplicator().Apply([field], [], existingValues: [captured]);
        Assert.Equal("1001", Assert.Single(values).Value);
    }

    [Fact]
    public void Applies_zonal_and_key_value_fields()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words =
            [
                Word("Invoice", 0.10f, 0.10f),
                Word("No", 0.22f, 0.10f),
                Word("00001521", 0.40f, 0.10f),
                Word("Total", 0.10f, 0.50f)
            ]
        };

        var profile = new DocumentTypeDefinition
        {
            AutoReadyThreshold = 80,
            Fields =
            [
                new IndexField
                {
                    Name = "InvoiceNo",
                    Kind = FieldKind.KeyValue,
                    KeyPattern = @"Invoice\s*No",
                    ValuePattern = @"\d+",
                    Mandatory = true,
                    PageScope = PageScope.First
                },
                new IndexField
                {
                    Name = "TotalLabel",
                    Kind = FieldKind.Zonal,
                    PageNumber = 1,
                    Zone = new ZoneRect { PageNumber = 1, X = 0.05f, Y = 0.45f, Width = 0.2f, Height = 0.1f }
                }
            ]
        };

        var values = new ProfileApplicator().Apply(profile, [lattice]);
        Assert.Equal(2, values.Count);
        Assert.Equal("00001521", values[0].Value);
        Assert.Equal("Total", values[1].Value);
        Assert.Equal(DocumentStatus.Ready, IndexFormat.StatusFor(values, 80));
    }

    [Fact]
    public void BatchSeparatorValue_field_mirrors_the_ambient_batch_trigger_value()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields = [new IndexField { Name = "BatchBarcode", Kind = FieldKind.BatchSeparatorValue }]
        };

        var withValue = new ProfileApplicator().Apply(profile, [], batchSeparatorValue: "BATCH-42");
        var value = Assert.Single(withValue);
        Assert.Equal("BATCH-42", value.Value);
        Assert.Equal(100, value.Confidence);

        var withoutValue = new ProfileApplicator().Apply(profile, [], batchSeparatorValue: null);
        Assert.Equal(string.Empty, Assert.Single(withoutValue).Value);
    }

    [Fact]
    public void Manual_fields_are_empty_and_carry_lookup_configuration_to_review()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField { Name = "Comment", Kind = FieldKind.Text, Format = FieldFormat.Integer },
                new IndexField
                {
                    Name = "Decision",
                    Kind = FieldKind.Lookup,
                    LookupOptions = [new LookupOption { Key = "Approved", Value = "A" }],
                    LookupDefaultValue = "A"
                }
            ]
        };

        var values = new ProfileApplicator().Apply(profile, []);

        Assert.Equal(FieldKind.Text, values[0].Kind);
        Assert.Equal(FieldFormat.Integer, values[0].Format);
        Assert.Equal(string.Empty, values[0].Value);
        Assert.Equal(FieldKind.Lookup, values[1].Kind);
        Assert.Equal("A", values[1].Value);
        Assert.Equal(100, values[1].Confidence);
        var option = Assert.Single(values[1].LookupOptions);
        Assert.Equal("Approved", option.Key);
        Assert.Equal("A", option.Value);

        // Applicator results are snapshots, not aliases into the editable profile.
        profile.Fields[1].LookupOptions[0].Value = "CHANGED";
        Assert.Equal("A", option.Value);
    }

    [Fact]
    public void Text_default_value_template_resolves_against_another_fields_extracted_value()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words = [Word("Invoice", 0.10f, 0.10f), Word("No", 0.22f, 0.10f), Word("12345", 0.40f, 0.10f)]
        };
        var profile = new DocumentTypeDefinition
        {
            Name = "Invoice",
            Fields =
            [
                new IndexField
                {
                    Name = "InvoiceNo",
                    Kind = FieldKind.KeyValue,
                    KeyPattern = @"Invoice\s*No",
                    ValuePattern = @"\d+",
                    PageScope = PageScope.First
                },
                new IndexField
                {
                    Name = "Combined",
                    Kind = FieldKind.Text,
                    DefaultValueTemplate = "{Doc#|000}-{InvoiceNo}"
                }
            ]
        };

        var context = new DefaultValueContext { DocumentNumber = 7 };
        var values = new ProfileApplicator().Apply(profile, [lattice], context);

        var combined = values.Single(v => v.FieldName == "Combined");
        Assert.Equal("007-12345", combined.Value);
        Assert.Equal(100, combined.Confidence);
    }

    [Fact]
    public void Text_default_can_reference_another_computed_field()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField { Name = "A", Kind = FieldKind.Text, DefaultValueTemplate = "AAA" },
                new IndexField { Name = "B", Kind = FieldKind.Text, DefaultValueTemplate = "{A}" }
            ]
        };

        var values = new ProfileApplicator().Apply(profile, []);

        Assert.Equal("AAA", values.Single(v => v.FieldName == "A").Value);
        Assert.Equal("AAA", values.Single(v => v.FieldName == "B").Value);
    }

    [Fact]
    public void Circular_field_value_sources_are_reported_without_looping()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField { Name = "A", Kind = FieldKind.Text, DefaultValueTemplate = "{B}" },
                new IndexField { Name = "B", Kind = FieldKind.Text, DefaultValueTemplate = "{A}" }
            ]
        };

        var values = new ProfileApplicator().Apply(profile, []);

        Assert.All(values, value => Assert.Equal("Circular value source", value.ValidationError));
    }

    [Fact]
    public void Empty_extracted_field_can_fall_back_to_a_batch_field_and_page_macros()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField
                {
                    Name = "Routing",
                    Kind = FieldKind.Regex,
                    ValuePattern = "does-not-match",
                    DefaultValueTemplate = "{Student number}-p{Page#}-of-{PageCount}"
                }
            ]
        };
        var context = new DefaultValueContext
        {
            BatchValues = [new IndexValue { FieldName = "Student number", Value = "X00007" }]
        };
        var lattices = new[]
        {
            new PageLattice { PageNumber = 1 },
            new PageLattice { PageNumber = 2 },
            new PageLattice { PageNumber = 3 }
        };

        var value = Assert.Single(new ProfileApplicator().Apply(profile, lattices, context));

        Assert.Equal("X00007-p1-of-3", value.Value);
        Assert.Equal(100, value.Confidence);
    }

    [Fact]
    public void Text_default_that_fails_the_fields_format_is_stored_and_flagged()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields = [new IndexField { Name = "Amount", Kind = FieldKind.Text, Format = FieldFormat.Integer, DefaultValueTemplate = "{Date}" }]
        };

        var values = new ProfileApplicator().Apply(profile, []);

        var value = Assert.Single(values);
        Assert.NotEmpty(value.Value);
        Assert.NotNull(value.ValidationError);
        Assert.Equal(DocumentStatus.NeedsReview, IndexFormat.StatusFor(values, 80));
    }

    [Fact]
    public void Re_applying_does_not_overwrite_a_manually_edited_default()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields = [new IndexField { Name = "Combined", Kind = FieldKind.Text, DefaultValueTemplate = "{Doc#}" }]
        };

        var first = new ProfileApplicator().Apply(profile, [], new DefaultValueContext { DocumentNumber = 1 });
        var value = Assert.Single(first);
        Assert.Equal("1", value.Value);

        value.Value = "hand-typed";
        value.IsManual = true;

        var second = new ProfileApplicator().Apply(
            profile, [], new DefaultValueContext { DocumentNumber = 2 }, existingValues: first);

        Assert.Equal("hand-typed", Assert.Single(second).Value);
    }

    [Fact]
    public void Re_applying_preserves_manually_entered_text_without_a_default()
    {
        var field = new IndexField { Name = "Comment", Kind = FieldKind.Text };
        var profile = new DocumentTypeDefinition { Fields = [field] };
        var existing = new[]
        {
            new IndexValue
            {
                FieldId = field.Id,
                FieldName = field.Name,
                Kind = FieldKind.Text,
                Value = "keep this",
                Confidence = 100,
                IsManual = true
            }
        };

        var reapplied = new ProfileApplicator().Apply(profile, [], existingValues: existing);

        var value = Assert.Single(reapplied);
        Assert.Equal("keep this", value.Value);
        Assert.True(value.IsManual);
    }

    [Fact]
    public void Invalid_default_template_format_sends_a_read_only_field_to_review()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField
                {
                    Name = "Computed",
                    Kind = FieldKind.Text,
                    DefaultValueTemplate = "{Doc#|Z}",
                    IsReadOnly = true
                }
            ]
        };

        var values = new ProfileApplicator().Apply(profile, []);

        var value = Assert.Single(values);
        Assert.Equal("Invalid default value format", value.ValidationError);
        Assert.Equal(DocumentStatus.NeedsReview, IndexFormat.StatusFor(values, 80));
    }

    [Fact]
    public void Mandatory_read_only_field_does_not_block_ready()
    {
        var values = new[]
        {
            new IndexValue { FieldName = "Computed", Mandatory = true, IsReadOnly = true },
            new IndexValue { FieldName = "Invoice", Value = "1", Confidence = 99 }
        };
        Assert.Equal(DocumentStatus.Ready, IndexFormat.StatusFor(values, 80));
    }

    [Fact]
    public void Lookup_ignores_a_default_that_is_not_a_configured_option()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField
                {
                    Kind = FieldKind.Lookup,
                    LookupOptions = [new LookupOption { Key = "Approved", Value = "A" }],
                    LookupDefaultValue = "REMOVED"
                }
            ]
        };

        var value = Assert.Single(new ProfileApplicator().Apply(profile, []));

        Assert.Equal(string.Empty, value.Value);
        Assert.Equal(0, value.Confidence);
    }

    [Fact]
    public void Lookup_key_template_matches_another_fields_value_case_insensitively()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words = [Word("acme", 0.10f, 0.10f), Word("inc", 0.20f, 0.10f)]
        };
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField
                {
                    Name = "Company Name",
                    Kind = FieldKind.Zonal,
                    Zone = new ZoneRect { PageNumber = 1, X = 0.05f, Y = 0.05f, Width = 0.5f, Height = 0.1f }
                },
                new IndexField
                {
                    Name = "Customer Id",
                    Kind = FieldKind.Lookup,
                    LookupOptions = [new LookupOption { Key = "ACME INC", Value = "customer01" }],
                    LookupKeyTemplate = "{Company Name}"
                }
            ]
        };

        var values = new ProfileApplicator().Apply(profile, [lattice]);

        var customerId = values.Single(v => v.FieldName == "Customer Id");
        Assert.Equal("customer01", customerId.Value);
        Assert.Equal(100, customerId.Confidence);
    }

    [Fact]
    public void Lookup_key_template_falls_back_to_the_fixed_default_when_nothing_matches()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField { Name = "Company Name", Kind = FieldKind.Text, DefaultValueTemplate = "Unknown Corp" },
                new IndexField
                {
                    Name = "Customer Id",
                    Kind = FieldKind.Lookup,
                    LookupOptions = [new LookupOption { Key = "ACME INC", Value = "customer01" }],
                    LookupDefaultValue = "customer01",
                    LookupKeyTemplate = "{Company Name}"
                }
            ]
        };

        var values = new ProfileApplicator().Apply(profile, []);

        // "Company Name" itself has a computed default, so it's excluded from the token dictionary —
        // {Company Name} resolves blank, no option matches, and the fixed LookupDefaultValue stands.
        var customerId = values.Single(v => v.FieldName == "Customer Id");
        Assert.Equal("customer01", customerId.Value);
    }

    [Fact]
    public void Lookup_key_template_does_not_override_a_manually_chosen_value_on_reapply()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField
                {
                    Id = Guid.NewGuid(),
                    Name = "Company Name",
                    Kind = FieldKind.Zonal,
                    Zone = new ZoneRect { PageNumber = 1, X = 0.05f, Y = 0.05f, Width = 0.5f, Height = 0.1f }
                },
                new IndexField
                {
                    Id = Guid.NewGuid(),
                    Name = "Customer Id",
                    Kind = FieldKind.Lookup,
                    LookupOptions =
                    [
                        new LookupOption { Key = "ACME INC", Value = "customer01" },
                        new LookupOption { Key = "OTHER CO", Value = "customer99" }
                    ],
                    LookupKeyTemplate = "{Company Name}"
                }
            ]
        };
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words = [Word("acme", 0.10f, 0.10f), Word("inc", 0.20f, 0.10f)]
        };

        var existing = new[]
        {
            new IndexValue { FieldId = profile.Fields[1].Id, FieldName = "Customer Id", Value = "customer99", IsManual = true, Confidence = 100 }
        };
        var values = new ProfileApplicator().Apply(profile, [lattice], existingValues: existing);

        var customerId = values.Single(v => v.FieldName == "Customer Id");
        Assert.Equal("customer99", customerId.Value);
        Assert.True(customerId.IsManual);
    }

    [Theory]
    [InlineData(PageScope.First, 99, "PAGE-ONE", 1)]
    [InlineData(PageScope.Number, 2, "PAGE-TWO", 2)]
    public void Zonal_field_honors_first_and_specific_page_scope(
        PageScope scope,
        int configuredPage,
        string expected,
        int expectedPage)
    {
        var field = new IndexField
        {
            Kind = FieldKind.Zonal,
            PageScope = scope,
            PageNumber = configuredPage,
            Zone = new ZoneRect { PageNumber = 1, X = 0.05f, Y = 0.05f, Width = 0.5f, Height = 0.15f }
        };
        var lattices = new[]
        {
            new PageLattice { PageNumber = 1, Words = [Word("PAGE-ONE", 0.1f, 0.1f)] },
            new PageLattice { PageNumber = 2, Words = [Word("PAGE-TWO", 0.1f, 0.1f)] }
        };

        var value = Assert.Single(new ProfileApplicator().Apply([field], lattices));

        Assert.Equal(expected, value.Value);
        Assert.Equal(expectedPage, value.PageNumber);
        Assert.Equal(expectedPage, value.Bounds?.PageNumber);
    }

    [Fact]
    public void Zonal_field_with_any_page_scope_uses_the_first_page_with_a_value()
    {
        var field = new IndexField
        {
            Kind = FieldKind.Zonal,
            PageScope = PageScope.Any,
            Zone = new ZoneRect { PageNumber = 1, X = 0.05f, Y = 0.05f, Width = 0.5f, Height = 0.15f }
        };
        var lattices = new[]
        {
            new PageLattice { PageNumber = 1, Words = [Word("OUTSIDE", 0.8f, 0.8f)] },
            new PageLattice { PageNumber = 2, Words = [Word("FOUND", 0.1f, 0.1f)] }
        };

        var value = Assert.Single(new ProfileApplicator().Apply([field], lattices));

        Assert.Equal("FOUND", value.Value);
        Assert.Equal(2, value.PageNumber);
    }

    [Fact]
    public void Barcode_field_with_number_scope_only_reads_its_configured_page()
    {
        var field = new IndexField { Kind = FieldKind.Barcode, PageScope = PageScope.Number, PageNumber = 2 };
        var profile = new DocumentTypeDefinition { Fields = [field] };
        var pages = Pages(("a.png", "1"), ("b.png", "2"));
        var decoder = new MapDecoder { ["a.png"] = "WRONG", ["b.png"] = "RIGHT" };

        var value = Assert.Single(new ProfileApplicator(decoder).Apply(profile, [], pages: pages));

        Assert.Equal("RIGHT", value.Value);
        Assert.Equal(2, value.PageNumber);
    }

    [Fact]
    public void Barcode_field_with_first_scope_always_reads_page_one()
    {
        var field = new IndexField { Kind = FieldKind.Barcode, PageScope = PageScope.First, PageNumber = 3 };
        var profile = new DocumentTypeDefinition { Fields = [field] };
        var pages = Pages(("a.png", "1"), ("b.png", "2"));
        var decoder = new MapDecoder { ["a.png"] = "PAGE-ONE", ["b.png"] = "PAGE-TWO" };

        var value = Assert.Single(new ProfileApplicator(decoder).Apply(profile, [], pages: pages));

        Assert.Equal("PAGE-ONE", value.Value);
    }

    [Fact]
    public void Barcode_field_with_any_scope_scans_every_page_for_a_match()
    {
        var field = new IndexField { Kind = FieldKind.Barcode, PageScope = PageScope.Any };
        var profile = new DocumentTypeDefinition { Fields = [field] };
        var pages = Pages(("a.png", "1"), ("b.png", "2"), ("c.png", "3"));
        var decoder = new MapDecoder { ["c.png"] = "FOUND-ON-THREE" };

        var value = Assert.Single(new ProfileApplicator(decoder).Apply(profile, [], pages: pages));

        Assert.Equal("FOUND-ON-THREE", value.Value);
        Assert.Equal(3, value.PageNumber);
    }

    private static List<DocumentPage> Pages(params (string ImagePath, string Number)[] pages) =>
        pages.Select(item => new DocumentPage
        {
            DocumentId = Guid.NewGuid(),
            PageNumber = int.Parse(item.Number),
            SourcePageNumber = int.Parse(item.Number),
            ImagePath = item.ImagePath
        }).ToList();

    private sealed class MapDecoder : Dictionary<string, string>, IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) =>
            TryGetValue(imagePath, out var text) ? new BarcodeReadResult(text, "CODE_128", 99) : null;
    }

    [Fact]
    public void Mandatory_missing_needs_review()
    {
        var profile = new DocumentTypeDefinition
        {
            Fields =
            [
                new IndexField
                {
                    Name = "InvoiceNo",
                    Kind = FieldKind.KeyValue,
                    KeyPattern = "MissingKey",
                    ValuePattern = @"\d+",
                    Mandatory = true,
                    PageScope = PageScope.First
                }
            ]
        };

        var lattice = new PageLattice { PageNumber = 1, Words = [Word("Hello", 0.1f, 0.1f)] };
        var values = new ProfileApplicator().Apply(profile, [lattice]);
        Assert.True(values[0].IsMissing);
        Assert.Equal(DocumentStatus.NeedsReview, IndexFormat.StatusFor(values, 80));
    }

    [Fact]
    public void Hidden_separator_does_not_block_ready()
    {
        var values = new[]
        {
            new IndexValue { FieldName = "Sep", Mandatory = true, HideFromIndexing = true },
            new IndexValue { FieldName = "Invoice", Value = "1", Confidence = 99 }
        };
        Assert.Equal(DocumentStatus.Ready, IndexFormat.StatusFor(values, 80));
    }

    [Fact]
    public async Task Ai_field_locates_and_highlights_its_answer_when_the_text_appears_on_a_page()
    {
        var field = new IndexField { Name = "Supplier", Kind = FieldKind.Ai };
        var documentType = new DocumentTypeDefinition { Fields = [field] };
        var lattices = new[]
        {
            new PageLattice { PageNumber = 1, Words = [Word("Nothing", 0.1f, 0.1f)] },
            new PageLattice { PageNumber = 2, Words = [Word("Acme", 0.2f, 0.3f), Word("Corp", 0.31f, 0.3f)] }
        };
        var ai = new FakeAiExtractor { [field.Id] = new AiExtractedValue("Acme Corp", 88) };

        var values = await new ProfileApplicator(ai: ai).ApplyAsync(documentType, lattices);

        var value = Assert.Single(values);
        Assert.Equal("Acme Corp", value.Value);
        Assert.Equal(88, value.Confidence);
        Assert.Equal(2, value.PageNumber);
        Assert.NotNull(value.Bounds);
        Assert.Equal(2, value.Bounds!.PageNumber);
    }

    [Fact]
    public async Task Ai_field_locates_a_money_answer_despite_the_source_texts_currency_formatting()
    {
        var field = new IndexField { Name = "InvoiceTotal", Kind = FieldKind.Ai };
        var documentType = new DocumentTypeDefinition { Fields = [field] };
        var lattices = new[]
        {
            new PageLattice { PageNumber = 1, Words = [Word("$1,249.60", 0.3f, 0.5f)] }
        };
        // The model reports the bare number; the source token still has the "$" and thousands comma.
        var ai = new FakeAiExtractor { [field.Id] = new AiExtractedValue("1249.60", 92) };

        var values = await new ProfileApplicator(ai: ai).ApplyAsync(documentType, lattices);

        var value = Assert.Single(values);
        Assert.Equal("1249.60", value.Value);
        Assert.NotNull(value.Bounds);
        Assert.Equal(1, value.Bounds!.PageNumber);
    }

    [Fact]
    public async Task Ai_field_has_no_highlight_when_its_answer_is_not_verbatim_on_any_page()
    {
        var field = new IndexField { Name = "Total", Kind = FieldKind.Ai, PageNumber = 1 };
        var documentType = new DocumentTypeDefinition { Fields = [field] };
        var lattices = new[]
        {
            new PageLattice { PageNumber = 1, Words = [Word("Subtotal", 0.1f, 0.1f), Word("45.00", 0.3f, 0.1f)] }
        };
        // The model summed/derived this value — it never appears verbatim in the OCR text.
        var ai = new FakeAiExtractor { [field.Id] = new AiExtractedValue("49.50", 90) };

        var values = await new ProfileApplicator(ai: ai).ApplyAsync(documentType, lattices);

        var value = Assert.Single(values);
        Assert.Equal("49.50", value.Value);
        Assert.Null(value.Bounds);
        Assert.Equal(field.PageNumber, value.PageNumber);
    }

    private sealed class FakeAiExtractor : IAiExtractor
    {
        private readonly Dictionary<Guid, AiExtractedValue> _values = [];
        public bool IsConfigured => true;
        public AiExtractedValue this[Guid fieldId] { set => _values[fieldId] = value; }

        public Task<IReadOnlyDictionary<Guid, AiExtractedValue>> ExtractAsync(
            string documentText, IReadOnlyList<IndexField> fields, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, AiExtractedValue>>(_values);
    }

    private static LatticeWord Word(string text, float x, float y) => new()
    {
        Text = text,
        Confidence = 95,
        X = x,
        Y = y,
        Width = 0.10f,
        Height = 0.04f
    };
}

public class IndexFormatTests
{
    [Fact]
    public void Validates_integer_and_money()
    {
        Assert.Null(IndexFormat.Validate("12", FieldFormat.Integer, "en-AU"));
        Assert.NotNull(IndexFormat.Validate("12.3", FieldFormat.Integer, "en-AU"));
        Assert.Null(IndexFormat.Validate("1,234.50", FieldFormat.Money, "en-AU"));
        Assert.Null(IndexFormat.Validate("yes", FieldFormat.Boolean, null));
        Assert.NotNull(IndexFormat.Validate("maybe", FieldFormat.Boolean, null));
    }
}
