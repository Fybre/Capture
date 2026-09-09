# Capture Profile Prototype — Phase 1 Progress

## Increment 1: shared rule-set editor

The first Phase 1 increment extracts the rule-list concerns shared by the existing Import and Batch profile designers:

- match mode and minimum-match state;
- ordered strategy rows and allowed strategy types;
- selected-rule state;
- add/remove commands;
- conversion back to the existing `SeparationStrategy` model;
- the shared match-mode and strategy-list UI.

`ImportProfileDesignerViewModel` and `BatchProfileDesignerViewModel` remain adapters. They still own scope-specific sample storage, OCR/barcode evaluation, highlights, and save operations, so stored profile behavior and processing behavior are unchanged. Batch rules continue to exclude Blank Page and Similarity; Import rules continue to expose the full existing strategy list.

This deliberately narrows the component boundary instead of putting batch fields or scope-specific test behavior into a universal rule editor.

## Increment 2: shared sample and zone view

The Batch, Import, and Indexing profile designers now use one `SampleDocumentView` for page navigation, page rendering, OCR overlays, zone drawing/editing, and optional highlight selection and zoom controls. Their existing view-models still own sample persistence and extraction because those operations currently have different profile-specific storage adapters.

## Verification

- Shared editor round-trip and command tests: 2 passed.
- Complete suite after the first two increments: 354 passed, 6 intentionally skipped Phase 0 gaps.
- `Capture.App` build: 0 warnings, 0 errors.

The macOS scanner-helper build now creates its temporary bundle under the system temporary directory before signing. This avoids File Provider metadata racing with `codesign` when the source checkout is inside a synced Documents folder; the produced helper and signing configuration are unchanged.

## Increment 3: field and script collections

`FieldCollectionEditorViewModel` / view now owns ordered field rows, selection, add/remove/reorder, and model round-tripping. `ScriptCollectionEditorViewModel` / view owns scripts, shared source, trigger defaults, and model round-tripping. Both accept the existing `IndexField` and `FieldScript` models, so they edit either batch or indexing scope without branching on scope.

The unified designer consumes these components directly. The existing Batch, Import, and Indexing designers remain available as compatibility adapters; none of their persisted shapes or script execution paths changed.

Phase 1 is complete.

Final prototype verification after all phases: 373 passed, 0 failed, 0 skipped.
