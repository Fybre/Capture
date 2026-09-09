# Phase 5 — Plan materialization and indexing

Completed:

- Added `CapturePlanMaterializer` as the single application service coordinating batches, documents, page files, lattices, and index values.
- Carries the planned document type (and compatible legacy profile ID) onto each persisted document.
- Runs batch extraction/scripts once per new batch and document extraction/scripts once per document through the existing `IProfileApplicator`.
- Reports document-level progress without exposing persistence steps to the UI.
- Defines rollback: newly-created documents are purged in reverse order and then empty batches are removed if any materialization step fails.
- Preserves the existing scripting runner, `AllowFieldScripts` gate, timeouts, cache, globals, and in-process trust model by reusing `IProfileApplicator` unchanged.
- Added `CaptureWorkflowService` as one end-to-end application operation: rasterise, build each page lattice once, analyse, plan, and materialise. The legacy importer remains available for the comparison feature flag/rollout period.
- Extended script globals additively with `Context.Batch`, `Context.Document`, and `Context.Trigger.Matches`; the familiar `Fields`, document/batch number, timeout, compilation cache, availability gate, and trusted in-process execution remain unchanged.
