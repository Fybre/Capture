# Phase 6 — Persisted open batches

Completed:

- Persisted capture-profile identity, input channel, and explicit `Open`/`Closed`/`Exported` batch state in SQLite.
- Added schema migration columns with safe defaults for existing databases.
- Added scoped open-batch lookup and the manual Start new batch operation.
- Materialization resumes an open batch for the same capture profile and channel when no new batch boundary occurs.
- A new boundary closes the prior scoped batch; automated inputs close their batch after the received input set finishes.
- Manual and watch-folder channels cannot accidentally resume each other's batches.

Verification includes recreating the store to demonstrate restart-safe open-batch resolution.
