# Capture Profile Prototype — Phase 0 Baseline

Recorded 2026-09-05 on macOS arm64 with .NET SDK 8.0.130.

## Baseline test run

Command:

```text
dotnet test Capture.sln --no-restore --verbosity minimal
```

Result before Phase 0 fixture coverage was added:

| Test assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `Capture.Tests` | 328 | 0 | 0 |
| `Capture.Scripting.Tests` | 15 | 0 | 0 |
| **Total** | **343** | **0** | **0** |

The source revision could not be recorded because this workspace copy contains no `.git` metadata.

After adding the Phase 0 suite, the same command reports 351 passing tests and 6 intentionally skipped acceptance tests (336 passed / 6 skipped in `Capture.Tests`, plus 15 passed in `Capture.Scripting.Tests`).

## End-to-end fixture contracts

The JSON files under `tests/Capture.Tests/Fixtures/CaptureProfiles` are engine-independent contracts for later planning-engine tests. Every input page is identified as `file#page` and must occur exactly once in either a planned document or `consumedPages`. The contract tests enforce that partition now, before a `CapturePlan` implementation exists.

| Fixture | Expected result |
| --- | --- |
| `student-records.json` | Two student batches. Student 1001 contains Enrolment Form pages 2–3 and Transcript pages 4–5; Student 1002 contains Transcript page 2. Both barcode headers are consumed while their `StudentNumber` values survive. |
| `legal-matter.json` | Pre-header content becomes an Unclassified document in a generic batch. The next retained page starts both Matter 4711 and a Correspondence document. A Witness Statement in a second file and later watch cycle remains in Matter 4711. |
| `mixed-accounts-payable.json` | One generic batch contains two distinct Invoice instances followed by a Statement. Invoice separator pages are consumed, while `INV-100` and `INV-101` remain as boundary values. |

## Characterised behavior and expected gaps

| Required Phase 0 behavior | Baseline status | Evidence / target phase |
| --- | --- | --- |
| Document separator value survives separator deletion | **Gap**: `PageSeparator` drops separator values when it consumes the trigger page. | Passing baseline characterization plus skipped acceptance test; structured matches in Phase 4. |
| One page can start both a batch and a document | **Partial**: independent passes both match the page, but there is no ordered, typed, structured decision. | Passing independent-pass characterization; combined planning assertion is pending Phase 4. |
| Multiple invoices in one PDF become separate Invoice instances | **Gap**: repeated page splits are possible, but multiple document type definitions do not exist. | Fixture and skipped acceptance test; Phase 4. |
| Invoice followed by Statement changes type | **Gap**: one indexing profile is selected for all current splits. | Fixture and skipped acceptance test; Phase 4/5. |
| Content before the first batch header enters a generic batch | **Partial**: allocation creates a real batch when none exists, but it is not explicit in a capture plan. | Fixture and skipped capture-plan acceptance test; Phase 4/5. |
| Strategy batch continues across two input files | **Supported within one processing session**: one allocator retains its current batch across file calls. | Passing characterization test. |
| Watch-folder batch closes after each received input set | **Supported**: automated execution starts a scoped batch and closes it after the complete received set is processed. | Runtime execution options plus persisted open-batch coverage. |

Skipped acceptance tests are intentional Phase 0 gap markers. They should be enabled, not rewritten to weaker assertions, when the phase named in each skip reason supplies the required architecture.

## Final prototype verification

Phases 4–6 supplied the missing planner and persisted-open-batch architecture. The skipped placeholders were removed only after executable scenario tests covered their contracts. The final suite has 373 passing tests, zero failures, and zero skips; all three JSON fixtures are now executed directly against `CapturePlanner` with exact batch, field, document type, page, boundary-value, and consumed-page assertions.

## Scripting security invariant

Phase 0 changes test assets and documentation only. Production scripting code, `WatchSettings.AllowFieldScripts`, Roslyn execution, timeouts, compilation caching, globals, and trusted in-process behavior are unchanged.
