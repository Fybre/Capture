# Phase 4 — Capture planning engine

Completed:

- Added immutable analysis, structured match, decision, diagnostic, source-page, and plan types.
- Added a pure deterministic planner that evaluates batch rules before document rules and never writes persistence state.
- Reuses each supplied page analysis for barcode/text/blank-page decisions.
- Retains rule IDs and captured values independently of consumed separator pages.
- Handles generic batches, repeated document instances, mixed types, ambiguous matches, and cross-input batch continuation.
- The legacy importer remains registered and available; adoption is opt-in through the new workflow components.

Verification: scenario tests cover the Phase 0 boundary combinations and the Student Records vertical slice.
