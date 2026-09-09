# Capture Profile Architecture Prototype Plan

## Purpose

This copy of the application is an architecture prototype. It exists to test a unified capture-profile model without changing the original project.

The prototype should prove that one understandable configuration can describe:

- how pages are grouped into batches;
- how batch-level index values are captured;
- how pages are separated into document instances;
- how each document is classified as a document type;
- how document-level index values are extracted;
- whether batch or document separator pages are retained;
- which scripts, validation, redaction, and exports apply at each scope.

The prototype is successful when a user can configure and test a complete workflow from one designer and process a mixed input stream into the expected batch/document hierarchy.

## Explicit non-goals

- Do not redesign or sandbox the scripting runtime.
- Do not replace the existing Roslyn scripting system.
- Do not introduce distributed processing, a message bus, event sourcing, or a plug-in framework.
- Do not generalise every pipeline operation before a real use case needs it.
- Do not remove the existing profile files until compatibility and migration have been demonstrated.
- Do not change export, redaction, OCR, barcode, or Therefore implementations merely to fit a new abstraction.

The existing scripting trust and security profile is accepted. Preserve the current `AllowFieldScripts` control, timeouts, compilation cache, globals, and execution behaviour. Only extend the script context when batch/document scope requires it.

## Core design decision

Use one user-facing **Capture Profile** as the aggregate root:

```text
Capture Profile
├── Batch definition
│   ├── start rules
│   ├── batch fields
│   ├── scripts
│   ├── separator-page handling
│   └── open/close policy
│
└── Document types
    ├── Invoice
    │   ├── recognition rules
    │   ├── start rules
    │   ├── fields
    │   ├── scripts
    │   ├── separator-page handling
    │   ├── redaction
    │   └── exports
    ├── Statement
    └── Unclassified fallback
```

Batch and document definitions share editor components and rule primitives, but remain distinct domain scopes. Do not make a batch inherit from an indexing profile.

## Terminology

| Term | Meaning |
| --- | --- |
| Capture profile | The complete configuration for one capture workflow. |
| Batch | A business grouping such as a matter, student record, or submission. |
| Batch definition | Rules, fields, and scripts governing batch creation. |
| Document type | A reusable definition such as Invoice, Statement, or Transcript. |
| Recognition rule | Determines what type a page/document appears to be. |
| Start rule | Determines whether a page starts a new batch or document instance. |
| Trigger page | The page that caused a start rule to match. |
| Page disposition | Whether the trigger page is retained or consumed. |
| Generic batch | A real batch with no captured business indexes or specific batch match. |
| Capture plan | An in-memory description of batches/documents/pages produced before persistence. |

## Behavioural rules to establish first

These rules remove ambiguity before implementation begins:

1. If no batch exists when the first retained document page arrives, create a generic batch.
2. A batch start match closes the current document, closes the current batch, and opens a new batch.
3. A document start match closes the current document and opens a new document of the matched type.
4. Batch boundary evaluation happens before document boundary evaluation for the same page.
5. A page may trigger both a batch and a document without either result being lost.
6. Captured values survive even when the trigger page is consumed.
7. Consumed separator pages belong to neither the previous nor the next document.
8. An unrecognised retained page is appended to the current document when one exists.
9. If no document exists, an unrecognised retained page starts an Unclassified document.
10. A document type match and a new document instance are separate decisions.
11. Repeated first-page matches for the same type create repeated document instances.
12. Open strategy-controlled batches can continue across input files and watch-folder processing cycles.
13. End of an input file closes a document only when the capture profile says a file is a document boundary.
14. Manual imports reuse their current batch unless a batch rule matches, the user starts a new batch, or the profile starts a new batch for each file. Automated inputs close their batch when the received input set finishes.

## Minimal target domain model

Names can change during implementation. Keep the initial model small.

```csharp
public sealed class CaptureProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "New capture profile";
    public BatchDefinition Batch { get; set; } = new();
    public List<DocumentTypeDefinition> DocumentTypes { get; set; } = [];
    public Guid? DefaultDocumentTypeId { get; set; }
    public bool AutoExportReadyDocuments { get; set; }
}

public sealed class BatchDefinition
{
    public RuleSet StartRules { get; set; } = new() { MatchMode = SeparationMatchMode.None };
    public List<IndexField> Fields { get; set; } = [];
    public List<FieldScript> Scripts { get; set; } = [];
    public string SharedScriptSource { get; set; } = "";
    public PageDisposition TriggerPageDisposition { get; set; }
    public bool StartNewBatchForEachFile { get; set; }
}

public sealed class DocumentTypeDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "New document type";
    public RuleSet RecognitionRules { get; set; } = new();
    public RuleSet StartRules { get; set; } = new();
    public List<IndexField> Fields { get; set; } = [];
    public List<FieldScript> Scripts { get; set; } = [];
    public string SharedScriptSource { get; set; } = "";
    public PageDisposition TriggerPageDisposition { get; set; }
    public RedactionSettings Redaction { get; set; } = new();
    public List<ExportDefinition> Exports { get; set; } = [];
}

public sealed class RuleSet
{
    public List<SeparationStrategy> Rules { get; set; } = [];
    public SeparationMatchMode MatchMode { get; set; } = SeparationMatchMode.Any;
    public int MatchMinimum { get; set; } = 1;
}

public enum PageDisposition
{
    IncludeInNewDocument,
    Consume
}
```

Continue using `SeparationStrategy` initially. Replacing it with a polymorphic hierarchy is unnecessary for this prototype.

## Structured matching result

Do not pass boundary information as only a Boolean and one ambient string. Introduce a structured result that retains all contributing rule values:

```csharp
public sealed record RuleMatch(
    Guid RuleId,
    string? CapturedValue,
    double? Confidence);

public sealed record BoundaryDecision(
    BoundaryScope Scope,
    Guid? DocumentTypeId,
    IReadOnlyList<RuleMatch> Matches,
    PageDisposition PageDisposition);
```

Index fields should be able to refer to a captured rule by ID. This permits a barcode or regex value to populate an index even when its separator page is consumed.

## Proposed processing flow

Build a single ordered planning engine that analyses each page once:

```text
Acquire/rasterise input
        │
        ▼
Build reusable page analysis
(OCR lattice, text, barcodes)
        │
        ▼
Evaluate batch start rules
        │
        ├── on match: close current document/batch
        │             open new batch
        │             capture batch values
        │
        ▼
Evaluate document type and start rules
        │
        ├── on match: close current document
        │             open typed document
        │             capture boundary values
        │
        ▼
Apply page disposition
        │
        ▼
Append retained page to current document
        │
        ▼
At input end, finish current document
and close/retain batch according to policy
```

The planning engine should return a `CapturePlan`. A separate materialiser persists it.

```csharp
public sealed record CapturePlan(
    IReadOnlyList<PlannedBatch> Batches,
    IReadOnlyList<ProcessingDiagnostic> Diagnostics);

public sealed record PlannedBatch(
    BatchDefinition? Definition,
    IReadOnlyList<IndexValue> CapturedValues,
    IReadOnlyList<PlannedDocument> Documents);

public sealed record PlannedDocument(
    DocumentTypeDefinition? Type,
    IReadOnlyList<int> SourcePages,
    IReadOnlyList<RuleMatch> BoundaryMatches);
```

This is a planning object, not a permanent event model. Keep it specific to capture processing.

## Unified designer

Create one `CaptureProfileDesignerView` with a navigation tree on the left and an editor workspace on the right.

Suggested navigation:

```text
Overview
Batch
Document Types
  Invoice
  Statement
  Supporting Document
Test Capture
```

Shared components:

- `SampleDocumentViewModel` / view
- `RuleSetEditorViewModel` / view
- `FieldCollectionEditorViewModel` / view
- `ScriptCollectionEditorViewModel` / view
- zone drawing and live extraction results

Scope-specific components:

- Batch: continuation, generic batch, header disposition, batch fields.
- Document type: recognition, instance start, fallback, redaction, exports.

Each document type can use its own sample. The batch definition can use a different batch-header sample. A single designer does not imply a single shared sample file.

## Scripting approach

Retain the current scripting implementation and security assumptions.

Prototype changes are limited to context clarity:

```csharp
Context.Batch.Number
Context.Batch.Fields
Context.Document.Number
Context.Document.Type
Context.Document.Fields
Context.Document.Text
Context.Trigger.Matches
```

Preserve the familiar `Fields` accessor as an alias for the current scope so existing scripts remain readable and migration remains possible.

Suggested triggers:

- `AfterBatchFieldsPopulated`
- `AfterDocumentFieldsPopulated`
- existing `BeforeExport`
- existing `AfterExport`

Do not add process isolation, a permission DSL, or a new sandbox. Any future hardening should be a separate project driven by a changed security requirement.

## Implementation phases

### Phase 0 — Freeze and characterise the baseline

Goal: make architectural changes measurable.

Tasks:

- Run and record the complete test suite.
- Add end-to-end fixtures representing a legal matter, student record, and mixed invoice/statement input.
- Add regression coverage for currently problematic boundary combinations.
- Document expected pages, batches, document types, captured values, and consumed pages for every fixture.

Required tests:

- document separator value survives separator deletion;
- one page can start both a batch and a document;
- multiple invoices in one PDF become separate Invoice instances;
- an Invoice followed by a Statement changes type;
- content before the first batch header enters a generic batch;
- strategy batches continue across two input files;
- a watch-folder batch continues across two queue processing cycles.

Exit criterion: current failures are documented as expected gaps rather than hidden by unit-only coverage.

### Phase 1 — Extract reusable designer components

Goal: reduce UI duplication without changing stored profile behaviour.

Tasks:

- Extract the sample viewer and zone editor from the existing designers.
- Extract the strategy/rule-set editor.
- Extract the field collection editor.
- Extract the scripts and shared-source editor.
- Keep adapters so the existing Batch, Import, and Indexing designers still run.

Exit criterion: existing designer behaviour is unchanged, and shared components can edit both batch and indexing data.

Stop/go checkpoint: if shared components require excessive conditional logic, narrow their responsibility rather than creating one universal editor view-model.

### Phase 2 — Add the Capture Profile aggregate

Goal: represent a complete workflow without removing existing profile models.

Tasks:

- Add `CaptureProfile` and a JSON store.
- Add nested batch and document-type definitions.
- Add references/adapters to existing `BatchProfile`, `ImportProfile`, and `IndexingProfile` where useful.
- Support creating a Capture Profile from existing profiles.
- Preserve original profile IDs during the prototype so results can be compared.

Exit criterion: a complete workflow can round-trip through storage and can reference or copy existing configurations.

### Phase 3 — Build the unified designer shell

Goal: configure the new aggregate in one place.

Tasks:

- Add the navigation tree and scope editor.
- Implement the Batch section.
- Implement add/copy/remove/reorder for Document Types.
- Implement Recognition and Start Rules as separate sections.
- Reuse existing field, script, redaction, and export editors.
- Add validation for missing fallback behaviour and ambiguous document rules.

Exit criterion: the student-record and accounts-payable examples can be configured without opening another profile window.

### Phase 4 — Introduce the capture planning engine

Goal: replace independent batch/document separator passes with one deterministic state machine.

Tasks:

- Cache per-page analysis so OCR/barcode work is not duplicated.
- Evaluate batch boundaries before document boundaries.
- retain structured matches after consuming pages;
- produce `CapturePlan` without writing document rows or final files;
- produce diagnostics for ambiguous matches and unclassified pages;
- keep the existing importer available behind a feature flag during comparison.

Exit criterion: fixture capture plans exactly match expected batch, document, type, page, and captured-value assignments.

### Phase 5 — Materialise and index the plan

Goal: persist a validated plan through an application service rather than the main view-model.

Tasks:

- Add a `CaptureWorkflowService` or `CapturePlanMaterializer`.
- Create batches, documents, page files, lattices, and index values as one coordinated operation.
- Carry the selected document type/profile through to each materialised document.
- Run batch field scripts once per new batch.
- Run document extraction and scripts once per document.
- Return progress and results to the UI without exposing persistence steps.
- Define rollback behaviour for a partially materialised input.

Exit criterion: `MainViewModel` invokes one workflow operation and does not allocate batches or apply fields itself.

### Phase 6 — Persist open batch state

Goal: make “until another batch separator is found” work across files and restarts.

Tasks:

- Add `BatchProfileId`/capture-profile identity to persisted batches.
- Add explicit `Open`, `Closed`, and optionally `Exported` states.
- Resolve the current open batch by input channel and capture profile.
- Close it only on a batch match, explicit user action, or configured end policy.
- support an explicit “Close current batch” action.

Exit criterion: watch-folder and manual-import tests continue the correct batch across independent operations.

### Phase 7 — Compare, migrate, and simplify

Goal: decide whether the prototype should replace the old workflow.

Tasks:

- Run old and new engines against the same fixtures.
- Add an import/migration preview for existing profiles.
- Identify configurations that cannot be mapped automatically.
- Measure configuration steps and processing time.
- Remove old designers only after a deliberate adoption decision.

Exit criterion: there is an evidence-backed decision to adopt, revise, or abandon the prototype.

## First vertical slice

Do not begin by implementing every rule type and every document feature. Use this narrow scenario:

```text
Capture profile: Student Records

Batch start:
    barcode `STUDENT:<number>`
    consume header page
    capture StudentNumber

Document types:
    Enrolment Form
        recognise/start when OCR contains `ENROLMENT FORM`

    Transcript
        recognise/start when OCR contains `ACADEMIC TRANSCRIPT`

Fallback:
    Unclassified Document
```

Acceptance example:

```text
Page 1  STUDENT:1001                consumed batch header
Page 2  ENROLMENT FORM              Student 1001 / Enrolment Form
Page 3  enrolment continuation      Student 1001 / Enrolment Form
Page 4  ACADEMIC TRANSCRIPT          Student 1001 / Transcript
Page 5  transcript continuation     Student 1001 / Transcript
Page 6  STUDENT:1002                consumed batch header
Page 7  ACADEMIC TRANSCRIPT          Student 1002 / Transcript
```

Expected result:

```text
Batch StudentNumber=1001
├── Enrolment Form: pages 2-3
└── Transcript: pages 4-5

Batch StudentNumber=1002
└── Transcript: page 7
```

Once this works through configuration, preview, processing, review, scripting, and persistence, extend it to repeated invoices and statement classification.

## Test strategy

Prefer scenario tests around the planning engine. Unit-test individual rules, but consider the page-to-plan result the main behavioural contract.

Each scenario should assert:

- batch count and order;
- batch field values;
- document count and order;
- assigned document type;
- source pages belonging to each document;
- consumed pages;
- captured rule values;
- classification ambiguity diagnostics;
- script-produced field values;
- behaviour at file and processing-session boundaries.

Designer tests should focus on model round-tripping and commands. Avoid duplicating every engine scenario at the UI level.

## Architecture constraints

1. `Capture.Core` owns definitions, decisions, plans, and processing rules.
2. `Capture.Storage` owns persistence implementations.
3. `Capture.App` owns UI state and invokes application services.
4. UI view-models do not decide batch membership.
5. UI view-models do not select the indexing profile for an already-classified document.
6. Boundary values remain available independently of trigger-page retention.
7. The same page analysis is reused by batch rules, document rules, and extraction where possible.
8. Existing script execution remains trusted and in-process.
9. Compatibility code is isolated and removable.
10. A generic batch is a persisted domain entity, not a null or view-only grouping.

## Prototype success criteria

Adopt the design only if all of the following are true:

- One designer configures batch grouping and multiple document types coherently.
- Mixed document inputs receive the correct document types and fields.
- Repeated documents of the same type become separate instances.
- Batch and document separator pages can be consumed without losing values.
- Batch membership remains correct across multiple files and watch cycles.
- Existing scripts run with no mandatory rewrite.
- The new pipeline is easier to test than the current view-model orchestration.
- Common use cases do not require understanding separate Import, Batch, and Indexing profile screens.

## Recommended starting point

Start with Phase 0 and the student-record fixture, then extract only the shared rule editor and sample/zone editor needed for the first unified-designer screen. Do not begin with storage migration or deletion of the old designers.
