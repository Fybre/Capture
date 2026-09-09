# Phase 2 — Capture Profile aggregate

Completed:

- Added the `CaptureProfile` aggregate with distinct batch and document-type definitions.
- Added rule sets, page disposition, per-file batch grouping, and boundary-rule field references.
- Added `ICaptureProfileStore` and JSON persistence under the version-specific `CaptureV2/capture-profiles` data folder.
- Added an isolated legacy compatibility mapper that copies existing Batch, Import, and Indexing profiles while retaining their IDs.
- Kept all legacy profile models and stores intact for side-by-side comparison.

Verification: aggregate round-trip and compatibility-ID tests are included in `Capture.Tests`.
