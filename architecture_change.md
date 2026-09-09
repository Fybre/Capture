# Architecture Change Plan

## Purpose

This document assesses the current Capture application architecture and defines an incremental path from the concern-organized `MainViewModel` partial class to independently testable application workflows and presentation components.

The recent partial-class split should be retained. It is a useful, low-risk staging point, but it is not yet a true component decomposition: all partial files still compile into one class and can access all shared private state.

## Verified baseline

Assessment baseline:

- Commit: `6f21bb0` (`Split MainViewModel into partial-class files by concern`)
- Release build: succeeds with zero compiler warnings
- Automated tests: 312 passed, zero failed
- Worktree at assessment time: clean except for the pre-existing untracked `.claude/` directory

The architectural change was behavior-preserving. The concern split did not change XAML bindings, generated commands, observable properties, or service registration behavior.

## Current architecture

```text
Avalonia views
    |
    v
MainViewModel partial class
    |-- Documents
    |-- Import / Scanning / Watch
    |-- Pages / Preview
    |-- Review / Profiles
    |-- Redaction
    `-- Export
    |
    v
Core interfaces and workflow logic
    |
    v
Storage / PDF / OCR / Scanner / Scripting / Therefore adapters
```

### Project-level strengths

The solution-level dependency direction is sound:

- `Capture.Core` has no project or package dependencies.
- Storage, PDF, OCR, Local AI, and Scripting depend inward on Core.
- `Capture.Therefore` remains independent of Core.
- `Capture.Export` adapts Therefore functionality into the Core-facing export model.
- `Capture.App` acts as the composition root and connects concrete implementations.
- Most infrastructure implementations are accessed through interfaces.

These boundaries should be preserved.

### Benefits of the partial-class split

The following files now provide useful feature locality:

- `MainViewModel.cs`: dependencies, construction, shared shell state, initialization, and navigation
- `MainViewModel.Documents.cs`: document and batch actions
- `MainViewModel.Import.cs`: file import and indexing workflow
- `MainViewModel.Pages.cs`: page navigation, thumbnails, page operations, and preview loading
- `MainViewModel.Profiles.cs`: profile dialogs, loading, and selection persistence
- `MainViewModel.Review.cs`: index review, edit persistence, and button scripts
- `MainViewModel.Redaction.cs`: redaction selection, editing, detection, and application
- `MainViewModel.Scanning.cs`: scanner workflow and cancellation
- `MainViewModel.Export.cs`: export commands and result handling
- `MainViewModel.Watch.cs`: watch-folder queue, settings application, and update notification

This improves discoverability, reduces editing conflicts, and provides a safe staging point for later extraction.

## Current architectural limitations

### 1. The application shell remains one large object

The partial files total approximately 3,021 lines and compile into the same `MainViewModel`. Every concern can still access every private field and method.

The constructor currently receives approximately 31 dependencies. This indicates that the class simultaneously acts as:

- application workflow coordinator;
- presentation model;
- global state container;
- collection manager;
- navigation controller;
- status and notification presenter;
- background-operation coordinator.

Splitting source files improves navigation but does not reduce runtime coupling or constructor complexity.

### 2. Shared mutable state couples otherwise separate concerns

The following state is read or mutated across multiple partial files:

- `IsBusy`
- `StatusText`
- `SelectedDocument`
- `Documents`
- `SelectedDocuments`
- profile collections and selections
- preview/page state
- review and redaction collections

`IsBusy` is particularly fragile because unrelated operations assign the same Boolean. If concurrent background work is introduced, one operation may set it to `false` while another is still active.

### 3. Cross-concern calls are unrestricted

Examples of the remaining coupling include:

- Watch processing directly calls the private import workflow.
- Import directly refreshes document groups and loads preview state.
- Document actions directly trigger preview and review refreshes.
- Review persistence updates batch peers and document statuses.
- Multiple concerns write directly to `StatusText` and display toasts.

There is no enforceable boundary between partial files because they are members of the same class.

### 4. Application orchestration is not independently tested

The existing test projects do not reference `Capture.App`. Core and infrastructure coverage is strong, but the following behaviors lack direct application-layer tests:

- selection and command-state transitions;
- import-to-index-to-redaction workflow transitions;
- watch processing while a manual operation is active;
- collection updates after delete, merge, export, or cleanup;
- preview cancellation and bitmap lifetime;
- status and error-summary presentation;
- interactions between batch-level and document-level review edits.

Partial files cannot be tested independently because they are not separate types.

### 5. Main view-model lifetime is inconsistent with its subscriptions

`MainViewModel` is registered as transient, but it subscribes to events raised by singleton services:

- `_watch.FilesReady`
- `_presidioLauncher.StatusChanged`

It does not unsubscribe or implement `IDisposable`. The current single-window lifecycle masks this issue. If a main view model is recreated later, the previous instance can remain reachable, receive events, and potentially duplicate processing.

### 6. Dependency visibility inside partial files is noisy

Each partial file retains almost the entire original using list, including namespaces unrelated to that concern. This adds hundreds of non-functional lines and obscures the dependencies actually required by each area.

Trimming imports will make coupling more visible and help identify extraction boundaries.

## Target architecture

The desired structure is a thin Avalonia shell over UI-independent application workflows:

```text
MainWindow
    |
    v
MainViewModel (shell)
    |-- InboxViewModel
    |-- PreviewViewModel
    |-- ReviewViewModel
    |-- RedactionViewModel
    `-- OperationViewModel
              |
              v
Application workflow services
    |-- ImportSessionCoordinator
    |-- DocumentWorkflowService
    |-- WatchQueueProcessor
    |-- ReviewPersistenceService
    |-- ProfileExportRunner
    |-- RedactionApplier
    `-- PageManagementService
              |
              v
Core ports and domain logic
              |
              v
Infrastructure adapters
```

The existing `ProfileExportRunner`, `RedactionApplier`, `PageManagementService`, and `DocumentImporter` already move in this direction and should be reused rather than duplicated.

## Incremental change plan

### Phase 0: Preserve and document the current split

Goal: establish a stable baseline before extracting behavior.

Tasks:

1. Retain the concern-based partial files.
2. Remove unused using directives from each partial file.
3. Document which file owns each command and observable property.
4. Add a rule that new infrastructure dependencies should not be injected directly into `MainViewModel` without first considering an application service.
5. Decide the intended main-window lifecycle:
   - register `MainViewModel` as a singleton if it is application-lifetime; or
   - implement `IDisposable` and unsubscribe from singleton events.

Acceptance criteria:

- Build and tests remain green.
- Each partial file contains only relevant imports.
- Main view-model lifetime is explicit and safe.
- No behavior changes are introduced.

### Phase 1: Introduce structured operation state

Goal: remove reliance on a shared Boolean and ad hoc status mutation.

Introduce an operation tracker with either reference counting or named operations:

```csharp
public interface IOperationTracker
{
    bool IsBusy { get; }
    IDisposable Begin(string operationName, string? initialStatus = null);
}
```

Alternatively expose independent workflow state:

```csharp
public sealed class ApplicationOperationState
{
    public bool IsImporting { get; set; }
    public bool IsExporting { get; set; }
    public bool IsScanning { get; set; }
    public bool IsRedacting { get; set; }
    public bool IsManagingPages { get; set; }
    public bool IsBusy => IsImporting || IsExporting || IsScanning || IsRedacting || IsManagingPages;
}
```

Tasks:

1. Replace direct `IsBusy = true/false` pairs with scoped operation ownership.
2. Give cancellable workflows their own `CancellationTokenSource`.
3. Centralize status/toast presentation in the shell.
4. Ensure a completed operation cannot clear another operation's busy state.

Acceptance criteria:

- Concurrent operations cannot incorrectly clear busy state.
- Commands continue to derive their enabled state from one observable aggregate.
- Cancellation ownership is explicit.

### Phase 2: Extract `DocumentWorkflowService`

Goal: move profile application and status calculation out of the UI layer.

Responsibilities:

- load document pages and lattices;
- apply document-level fields;
- apply batch-level fields;
- preserve manually edited values;
- calculate document status;
- persist index values and document changes;
- run post-index steps;
- return warnings and outcomes rather than writing UI status.

Possible contract:

```csharp
public interface IDocumentWorkflowService
{
    Task<DocumentWorkflowResult> ApplyProfileAsync(
        CaptureDocument document,
        IndexingProfile profile,
        IReadOnlyDictionary<Guid, string>? separatorValues = null,
        string? batchSeparatorValue = null,
        CancellationToken cancellationToken = default);
}

public sealed record DocumentWorkflowResult(
    CaptureDocument Document,
    IReadOnlyList<IndexValue> DocumentValues,
    IReadOnlyList<IndexValue> BatchValues,
    IReadOnlyList<string> Warnings);
```

Move the current extraction, batch-field, macro-context, status, and post-index orchestration from `MainViewModel.Import.cs` into this service.

Acceptance criteria:

- The service has no Avalonia dependency.
- Profile application can be tested without creating `MainViewModel`.
- `MainViewModel` only maps results into rows and UI messages.

### Phase 3: Extract `ImportSessionCoordinator`

Goal: separate import-session orchestration from UI collection mutation.

Responsibilities:

- accept files or scanned pages;
- resolve batch behavior;
- invoke `IDocumentImporter`;
- allocate batches;
- invoke `IDocumentWorkflowService`;
- aggregate successes and failures;
- return a complete session result.

Suggested result types:

```csharp
public sealed record ImportFailure(string SourcePath, string Message);

public sealed record ImportSessionResult(
    IReadOnlyList<CaptureDocument> Imported,
    IReadOnlyList<ImportFailure> Failures,
    CaptureBatch? LastBatch);
```

The coordinator should not manipulate `ObservableCollection`, select rows, show toasts, or set `StatusText`.

Acceptance criteria:

- Manual import, folder import, scan import, and watch import share the same coordinator.
- Mixed-success imports produce deterministic structured results.
- Batch allocation and failure behavior are covered by tests.

### Phase 4: Extract `WatchQueueProcessor`

Goal: remove the watch queue and retry orchestration from the view model.

Responsibilities:

- subscribe to `IWatchFolderService`;
- queue and deduplicate paths;
- group work by watch-folder entry;
- wait while interactive work owns exclusive processing capacity;
- invoke `ImportSessionCoordinator`;
- move files to processed/error destinations;
- report retry exhaustion;
- expose progress and completion events or an asynchronous result stream.

Prefer `System.Threading.Channels.Channel<T>` over a manually managed `Queue<T>` and Boolean processing flag.

Possible interface:

```csharp
public interface IWatchQueueProcessor : IAsyncDisposable
{
    event EventHandler<WatchProgressEventArgs>? ProgressChanged;
    event EventHandler<ImportSessionResult>? SessionCompleted;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task ApplySettingsAsync(IReadOnlyList<WatchFolderEntry> entries, CancellationToken cancellationToken = default);
}
```

Acceptance criteria:

- Watch processing works without an Avalonia dispatcher.
- Retry and quarantine behavior is independently tested.
- Subscriptions are disposed deterministically.
- The view model only consumes progress/results on the UI thread.

### Phase 5: Extract `ReviewPersistenceService`

Goal: isolate review-edit persistence and batch propagation.

Responsibilities:

- debounce or serialize repeated edits;
- persist document-level values;
- persist batch-level values;
- propagate batch changes to related documents;
- recalculate statuses;
- return the IDs of affected documents.

Suggested contract:

```csharp
public interface IReviewPersistenceService
{
    Task<ReviewPersistResult> SaveAsync(
        CaptureDocument document,
        IReadOnlyList<IndexValue> documentValues,
        IReadOnlyList<IndexValue> batchValues,
        CancellationToken cancellationToken = default);
}
```

Acceptance criteria:

- Review persistence has no dependency on `ObservableCollection` or toast services.
- Rapid edits cannot produce out-of-order final state.
- Batch edits update all affected rows through an explicit result.

### Phase 6: Extract `PreviewViewModel`

Goal: create the first independently testable child presentation component.

`MainViewModel.Pages.cs` is the most cohesive candidate for extraction. `PreviewViewModel` should own:

- current page and page count;
- page thumbnails and selection;
- page image and bitmap disposal;
- current lattice and OCR overlay;
- page-load generation/cancellation;
- page navigation commands;
- page split, delete, and reorder commands where appropriate.

Document collection changes should be returned as events or results rather than performed by reaching into the parent's collections.

Acceptance criteria:

- Rapid document/page changes cannot display stale content.
- Bitmap disposal is unit/integration tested.
- Preview loading can be tested using fake page and lattice stores.
- The shell binds to `Preview.*` or exposes narrow forwarding properties during migration.

### Phase 7: Extract remaining child view models

Once the workflow services exist, extract presentation components in this order:

1. `RedactionViewModel`
2. `ReviewViewModel`
3. `InboxViewModel`
4. `OperationViewModel`

Keep `MainViewModel` as a shell responsible for:

- composing child view models;
- top-level navigation;
- active selection coordination;
- modal-dialog ownership;
- global notifications;
- application initialization and shutdown.

Acceptance criteria:

- Each child view model has a focused constructor.
- Each child can be tested independently.
- Cross-component communication uses explicit events, messages, or result types.
- Child view models do not reach into each other's private state.

## Boundary rules for future changes

Apply these rules immediately, even before all extraction phases are complete:

1. `MainViewModel.Watch.cs` may call an import coordinator, but should not depend directly on private implementation details in `MainViewModel.Import.cs`.
2. Import code should select a resulting document; preview code should react to selection rather than being called to load directly.
3. Export and redaction workflows should return outcomes and must not set UI text or show toasts.
4. Only the shell/presentation layer should own `StatusText` and toast wording.
5. Application services must not depend on Avalonia types.
6. Infrastructure projects must continue to depend inward on Core, never on App.
7. New workflows should return structured results instead of communicating through several mutated collections and counters.
8. New dependencies should be injected into the smallest owning component, not automatically into `MainViewModel`.
9. Background subscriptions must have explicit lifetime ownership and disposal.
10. Avoid moving domain or workflow logic into code-behind.

## Testing strategy

Create an application-layer test project, for example:

```text
tests/Capture.App.Tests/
```

Recommended coverage:

- import session with complete success;
- import session with mixed success;
- batch allocation across several source files;
- watch queue deduplication and retry exhaustion;
- simultaneous/manual work and queued watch imports;
- profile application with document and batch fields;
- rapid review edits and final persistence state;
- selection changes during preview loading;
- page-load cancellation and stale-result rejection;
- document removal after export;
- redaction detection/application transitions;
- event unsubscription and object disposal.

Prefer testing application services directly. View-model tests should focus on observable state, command availability, and translation of service outcomes into UI state.

## Migration principles

- Keep every phase buildable and releasable.
- Move behavior before redesigning it.
- Add characterization tests before extracting complex workflows.
- Avoid changing XAML bindings and workflow behavior in the same commit.
- Introduce interfaces only where they define a real boundary or test seam.
- Do not duplicate existing services merely to achieve a preferred diagram.
- Preserve the current inward dependency direction.
- Measure constructor size and cross-component dependencies after each phase.

## Definition of done

The architecture change is complete when:

- `MainViewModel` is a small application shell rather than the workflow implementation;
- no single view model coordinates import, indexing, watch processing, review, redaction, and export;
- application workflows can run and be tested without Avalonia;
- child view models have focused responsibilities and constructors;
- singleton event subscriptions are disposed safely;
- operation state supports concurrent/background workflows correctly;
- workflow failures and summaries use structured result types;
- the full build, existing tests, and new application-layer tests remain green;
- the project dependency direction remains Core-inward and App-outward.

## Recommended immediate next step

Do not immediately replace the partial classes with many new view models. First extract `DocumentWorkflowService`, because it removes the most reusable non-UI behavior from the import, review, and document actions and creates a natural seam for `ImportSessionCoordinator` afterward.

The partial-class split is therefore considered successful as an organizational refactor and should be treated as the starting point—not the final state—of the architectural decomposition.
