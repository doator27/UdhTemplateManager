# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Technology Stack

- **Language/Runtime:** C# / .NET 8
- **UI:** Avalonia UI 11.3.9 (cross-platform Linux + Windows)
- **Database:** SQLite via Entity Framework Core 8.0
- **PDF manipulation:** PdfSharp 6.1 (merging, page extraction, rotation)
- **PDF generation:** QuestPDF 2024 (cover sheet)
- **Tests:** xUnit with in-memory SQLite (`UseSqlite("Data Source=:memory:")` + `EnsureCreated()`)
- **Target platforms:** Linux (dev) and Windows (production)

## Solution Structure

```
HardwareTemplateBuilder.sln
├── HardwareTemplateBuilder.Core/   # Class library: models, EF context, repositories, services
├── HardwareTemplateBuilder.App/    # Avalonia UI application
├── HardwareTemplateBuilder.Tests/  # xUnit tests
└── HardwareTemplateBuilder.SmokeTest/  # Console smoke tests
```

## Commands

```bash
dotnet build                          # Build the solution
dotnet run --project HardwareTemplateBuilder.App  # Run the app
dotnet test                           # Run all tests
dotnet test --filter "FullyQualifiedName~PageRangeParser"  # Run a single test class
dotnet ef migrations add <Name> --project HardwareTemplateBuilder.Core  # Add EF migration
dotnet ef database update --project HardwareTemplateBuilder.Core        # Apply migrations
```

## Architecture

### Data Layer (Core/Data/ and Core/Models/)

- **AppDbContext** manages 16 SQLite tables via EF Core
- **DatabaseInitializer** handles creation, migration, and post-migration schema patches (`EnsureSchemaPatches()`) on startup; patches are idempotent and used for retroactive schema fixes
- **DatabaseInitializer.CreateContext()** is the standard way to obtain a db context — contexts are created per-operation, not shared
- **DatabaseLocationService** manages the configurable database file path (default: `%AppData%/HardwareTemplateBuilder/hardware_templates.db`)
- **MachineIdentityService** generates a stable machine UUID stored in `UserProfile.MachineId`, enabling auto-selection of the matching profile on login
- **SqlitePragmaInterceptor** (EF Core `DbConnectionInterceptor`) applies `PRAGMA busy_timeout=5000` on every connection open; WAL mode is set once at startup via `DatabaseInitializer` and persists in the DB file
- **RetryHelper** provides `ExecuteWithRetry()` for write operations that may hit `SQLITE_BUSY`/`SQLITE_LOCKED` — exponential back-off, 3 retries by default
- **Generic `IRepository<T>`** with `GetById`, `GetAll`, `Add`, `Update`, `Delete`; `IHardwareItemRepository` adds `Search(manufacturer, description, modelNumber)` returning results sorted by `Frequency` descending
- All `Add()` methods include duplicate detection — return existing record if already present
- `JobTemplateSnapshot` records are written once at PDF generation and **never modified**
- `JobRelease` records model addenda/revisions to a job; `JobHardware` rows can reference a `JobRelease` via nullable FK (SetNull on release delete)
- **App configuration is stored in the `AppSetting` table** (key-value), not in appsettings.json. Keys include `TemplateStorageLocation`, `LastRefreshTimestamp`, and SMTP settings seeded by `DatabaseInitializer`
- **Job-scoped templates:** `IndividualTemplate.OriginJobId` (null = globally visible; non-null = hidden from other jobs' searches). `HardwareItemTemplate.JobId` (null = global link included in all packages; non-null = link only active for that specific job's PDF generation). Use `IndividualTemplateRepository.GetVisibleForJob(jobId)` when searching within a job context.

### Key Services (Core/Services/)

| Service | Responsibility |
|---|---|
| `FrequencyService` | Increments `HardwareItem.Frequency` on each retrieval |
| `PageRangeParser` | Parses page specs like `"1,3-5,8"` into page number lists |
| `DescriptionPathService` | Builds hierarchical display paths for `Description` tree nodes |
| `TemplateRefreshService` | Re-downloads or re-copies template files on demand |
| `MissingTemplateAlertService` | Checks for hardware items missing template files at startup (best-effort, background) |

### PDF Assembly Pipeline (Core/Services/Pdf/)

Orchestrated by `PdfAssemblyService`, which accepts an `AssemblyRequest` and executes:

1. `TemplateSorter` (uses `ITemplateSortStrategy` — default: `WeightTemplateSortStrategy`) → sorted template list
2. `FileAcquirer` → local copies in `{JobNumber}/` subfolder as `{ManufacturerName}_{TemplateNumber}.pdf`
3. `PageExtractor` → `PageRotator` → `PdfMerger` → merged body PDF
4. `CoverSheetBuilder` (QuestPDF) → cover sheet PDF with header block and hardware table
5. Prepend cover sheet + `PageNumberer` stamps sequential numbers offset past cover pages
6. Write `JobTemplateSnapshot` records

### UI (App/)

- **Architecture is code-behind, not MVVM** — views directly instantiate repositories and call services; there are no ViewModels or data-binding commands
- `MainWindow` hosts a persistent menu bar (File, Jobs, Maintenance, Admin) and a content area
- `ViewRouter` swaps child `UserControl` views for navigation — 25 views total; **a new instance is created on every navigate** (no view caching)
- `SessionService` (static) holds the active `UserProfile` for the session lifetime; `BulkAddSession` (static) holds transient bulk-entry state across the multi-step bulk-add flow
- **Bulk-add flow** is a multi-step wizard entirely within the content area: `BulkManufacturerSelectionView` → `BulkJobHubView` → `BulkManufacturerSessionView` → `BulkHardwareEntryView` → `TemplateResolutionWizardView` (persists `JobHardware` on Finish). State is shared via `BulkAddSession` and `BulkSessionDraft` static classes.
- On first launch with no `UserProfile` records, `ProfilePickerDialog` prompts creation before proceeding; `DatabaseSetupDialog` handles unreachable DB paths at startup
- Visual style: `#C0C0C0` background, beveled buttons, Tahoma/Arial fonts (Windows 98 aesthetic) via Avalonia `ControlTheme`/`Style`
- Standard search pattern throughout: Manufacturer/Description/ModelNumber comboboxes → listbox sorted by `Frequency` desc
- `DescriptionPickerWindow` is a modal window (not a UserControl) used wherever a `Description` tree selection is needed

#### View Initialization Pattern

All views follow this convention:
```csharp
public MyView() {
    InitializeComponent();
    Loaded += (_, _) => Initialize();
}
private void Initialize() { /* repo creation, data loading */ }
```
Heavy initialization is deferred to `Loaded` so XAML controls are fully constructed first.

#### Navigation

`ViewRouter` uses string-based routing. Parameterized routes follow the `"ViewName:{id}"` pattern (e.g., `"JobDetail:42"`), parsed in `MainWindow.NavigateTo()`. Views raise `NavigationRequested` (`Action<string>`) to trigger navigation from the parent.

#### Dialogs

Modal windows (`Window` subclasses) are shown with `ShowDialog<T>(parentWindow)` and return typed results (null = canceled). `DialogHelper` creates simple OK/Cancel dialogs programmatically without XAML.

#### Long-Running Operations

Views that launch background work store a `CancellationTokenSource?` field and toggle button states (`StartButton.IsEnabled`, `CancelButton.IsEnabled`). Background tasks use `IProgress<T>` + `Dispatcher.UIThread.Post()` to marshal UI updates. `OperationCanceledException` is caught separately from unexpected exceptions.

#### Error Handling

Batch operations (PDF assembly, template refresh) collect all per-item failures into a list and throw a single `InvalidOperationException` with a consolidated report — not fail-fast. Background startup tasks (missing-template alert, auto-refresh) swallow all exceptions to avoid disrupting the UI.

### Description Hierarchy

`Description` is a self-referential tree used for categorizing hardware items. `DescriptionPathService` resolves full paths. The hierarchy is also how `WeightTemplateSortStrategy` groups templates for sorting.

### App Startup Sequence

`App.OnFrameworkInitializationCompleted()` defers startup to `mainWindow.Opened`. Phases: database location resolution → migration + schema patches → machine ID lookup → profile selection → background refresh check.

## Releases

See `RELEASING.md`. Releases are produced by the GitHub Actions workflow in `.github/workflows/release.yml` — triggered by a `v*` tag push or manual dispatch. The artifact is a self-contained single-file Windows x64 executable zipped as `HardwareTemplateBuilder-{VERSION}-win-x64.zip`.

The `HardwareTemplateBuilder.SmokeTest` console project runs full CRUD against all entity types to validate schema and relationships — run it manually against a real database when testing migrations or repository changes.

## Code Quality Requirements

- All public methods, classes, and non-obvious variables require XML doc comments (`/// <summary>`)
- SOLID principles: Repository for data access, Strategy for sorting (`ITemplateSortStrategy`), Factory via `ViewRouter`
- All file paths must use `Path.Combine()` — no hardcoded separators
- Nullable reference types enabled across all projects
