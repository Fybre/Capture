# Capture V2 feature-parity audit

Reference: `/Users/craig/Documents/source/Grok-Workspace` at commit `60b4e68`.

This audit compares user-visible behaviour, commands, persisted settings, and runtime services. The
three legacy profile formats and their migration UI are intentionally excluded because this version
was explicitly changed to support Capture Profiles only.

## Restored and verified

- Unified capture profile model, planner, materializer, open-batch persistence, and rollback tests.
- Batch/document rule types: page count, blank page, barcode, whole-page regex, and OCR zone.
- Batch/document fields and page scopes.
- Profile scripts: inline source, shared source, pop-out editor, triggers, timeout, and explicit test.
- Redaction: Sensitive fields, optional Presidio detection, entity sets, both confidence thresholds.
- Full CSV export configuration: folder, output mode, names, selected fields, header, and attachment.
- Full Therefore export configuration: category selection, field mapping, and attachment.
- Copying a document type now regenerates all nested IDs and remaps its internal references.
- Field-level Script, Button, and Post-process source has syntax-highlighted pop-outs and explicit tests.
- AI fields can explicitly extract all AI values from the complete selected sample.
- Drawing Barcode or OCR-zone rule areas detects the sample content and seeds the rule configuration.
- Key/value and Regex fields can suggest key/value patterns from an area selected on the sample.
- Rule sets can test every rule and report the final All/Any/At-least-N outcome.
- `Test Capture` uses a preview-only planning path and does not persist or export documents.
- The non-functional visual-similarity placeholder and its unused embedding settings were hard-removed.
- Saving a valid profile now provides visible success feedback.
- Selected fields refresh their sample extraction when selected, redrawn, or when extraction settings change.

## Confirmed gaps to restore

### Behaviour requiring regression decisions/tests

- Export configuration needs a native UI interaction pass; the build and model regression test pass,
  but the native accessibility bridge does not expose the directly-run development executable.

## Audit rule

A feature is not marked complete merely because its model still exists. It must be reachable in the
unified designer, round-trip through storage, execute through the production service, have regression
coverage proportional to its risk, and pass a native UI check when layout or interaction is involved.
