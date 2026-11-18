# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Technology Stack

- **Language/Runtime:** C# / .NET 8
- **UI:** Avalonia UI 11.3.9 (cross-platform Linux + Windows)
- **Database:** MySQL via Entity Framework Core 9.0 (Pomelo.EntityFrameworkCore.MySql) — centralized server, all clients connect directly via a shared connection string
- **PDF manipulation:** PdfSharp 6.1 (merging, page extraction, rotation)
- **PDF generation:** QuestPDF 2024 (cover sheet)
- **Tests:** xUnit with EF Core InMemory provider (`UseInMemoryDatabase(...)`)
- **Target platforms:** Linux (dev) and Windows (production)

## Solution Structure

```
HardwareTemplateBuilder.sln
├── HardwareTemplateBuilder.Core/       # Class library: models, EF context, repositories, services
├── HardwareTemplateBuilder.App/        # Avalonia UI application (main product)
├── HardwareTemplateBuilder.Lookup/     # Standalone read-only Avalonia app: search + generate/open a merged template PDF, no write access to the DB
├── HardwareTemplateBuilder.Wpf/        # WPF port of HardwareTemplateBuilder.App with a Windows 95 look (Windows-only at runtime)
├── HardwareTemplateBuilder.Tests/      # xUnit tests
├── HardwareTemplateBuilder.SmokeTest/  # Console smoke tests: full CRUD against all entity types
└── HardwareTemplateBuilder.MigrationTool/  # One-time console tool: copies data from a legacy SQLite DB into MySQL

HardwareTemplateBuilder.Diagnostic/     # Standalone console scratch project (not in the .sln) for probing manufacturer
                                         # websites/sitemaps to find template PDF URLs when configuring TemplateSource data
```

## Commands

```bash
dotnet build                          # Build the solution
dotnet run --project HardwareTemplateBuilder.App  # Run the main app
dotnet run --project HardwareTemplateBuilder.Lookup  # Run the standalone read-only template lookup app
dotnet run --project HardwareTemplateBuilder.Wpf     # Run the Windows 95-style WPF frontend (Windows only; builds anywhere)
dotnet test                           # Run all tests
dotnet test --filter "FullyQualifiedName~PageRangeParser"  # Run a single test class
dotnet ef migrations add <Name> --project HardwareTemplateBuilder.Core  # Add EF migration
dotnet ef database update --project HardwareTemplateBuilder.Core        # Apply migrations
```

## Architecture

### Data Layer (Core/Data/ and Core/Models/)

- **AppDbContext** manages 19 tables via EF Core against MySQL (Pomelo provider)
- **DatabaseInitializer** resolves the active MySQL connection string, builds the `AppDbContext`, and applies pending migrations (`Database.Migrate()`) on startup; `EnableRetryOnFailure()` handles transient network blips automatically
- **DatabaseInitializer.CreateContext()** is the standard way to obtain a db context — contexts are created per-operation, not shared
- **DatabaseLocationService** manages the configured MySQL connection string (persisted via a small pointer file at `%AppData%/HardwareTemplateBuilder/db_connection.txt`, falling back to an exe-directory pointer)
- **MachineIdentityService** generates a stable machine UUID stored in `UserProfile.MachineId`, enabling auto-selection of the matching profile on login
- **HardwareTemplateBuilder.MigrationTool** is a one-time console tool for teams migrating from the legacy SQLite architecture: it copies every table from an old `.db` file into the configured MySQL database
- **Generic `IRepository<T>`** with `GetById`, `GetAll`, `Add`, `Update`, `Delete`; `IHardwareItemRepository` adds `Search(manufacturer, description, modelNumber)` returning results sorted by `Frequency` descending
- All `Add()` methods include duplicate detection — return existing record if already present
- `JobTemplateSnapshot` records are written once at PDF generation and **never modified**
- `JobRelease` records model addenda/revisions to a job; `JobHardware` rows can reference a `JobRelease` via nullable FK (SetNull on release delete)
- **Releases:** `JobHardware.ReleaseId == null` is the **base list** (the default); each release has its own separate hardware rows. The job page's release combo sets the *active* release (persisted as `Job.LastActiveReleaseId`); the hardware list, Bulk Add/Import (`BulkAddSession.ReleaseId`; each release has its own Bulk Add draft: `BulkAddDraft.ReleaseId`, plain id, null = base list, passed to every `BulkDraftService`/`BulkAddDraftRepository` call; the bulk-add headers show "24117 Addendum 1" via `BulkAddSession.JobTitle`), Generate and the cover-sheet-only buttons all use only the active release's rows, and Batch Generate uses each job's active release (`JobPackageGenerationService.ActiveReleaseId`). Attachments are per release too (`JobAttachment.ReleaseId`, plain id, null = base list; `JobAttachmentRepository.GetByRelease`): the job page lists the active release's attachments ("Show attachments from every release" lists all, labelled, with "Move to This Release"), files are stored in `{job}/Attachments/` (base list) or `{job}/Attachments/{ReleaseLabel}/` via `JobAttachmentFolders` (also `MoveToRelease`, which moves the file and its mirror). Choosing an empty release asks once whether to import the base list's hardware; No = add hardware the normal way. On output a release never changes the job name: `JobReleaseNaming.CoverJobNumber` prints "{JobNumber} {ReleaseLabel}" (base list = plain job number), and each release gets its own package file (`Templates for job 24117 Addendum 1.pdf`) in the same job folder; regenerating one release doesn't archive the others' packages
- **App configuration is stored in the `AppSetting` table** (key-value), not in appsettings.json. Keys include `TemplateStorageLocation`, `LastRefreshTimestamp`, and SMTP settings seeded by `DatabaseInitializer`
- **Job-scoped templates:** `IndividualTemplate.OriginJobId` (null = globally visible; non-null = hidden from other jobs' searches). `HardwareItemTemplate.JobId` (null = global link included in all packages; non-null = link only active for that specific job's PDF generation). Use `IndividualTemplateRepository.GetVisibleForJob(jobId)` when searching within a job context.
- **Cloning job-scoped templates:** `IndividualTemplateRepository.Add()` deduplicates on `ManufacturerId + TemplateNumber + DoorMaterialId` and ignores `OriginJobId`, so calling it to clone a job-scoped template returns the original unchanged. Instead, query for an existing clone (`OriginJobId == targetJobId`, same mfr+number+material) directly via `ctx.IndividualTemplates.FirstOrDefault(...)`, and if absent insert via `ctx.IndividualTemplates.Add()` bypassing the repository.

### Key Services (Core/Services/)

| Service | Responsibility |
|---|---|
| `FrequencyService` | Increments `HardwareItem.Frequency` on each retrieval |
| `PageRangeParser` | Parses page specs like `"1,3-5,8"` into page number lists |
| `DescriptionPathService` | Builds hierarchical display paths for `Description` tree nodes |
| `TemplateRefreshService` | Re-downloads or re-copies template files on demand |
| `TemplateFixListService` | The "Templates to Fix" list: `TemplateRefreshService.RefreshAsync` calls `Record()` with its failures (replacing the previous list, stored as JSON in `AppSetting` key `TemplateFixList`). `GetOutstanding()` returns the hardware items still linked to a failed template and prunes any that are fixed (new template linked since the refresh, broken one unlinked, or the template's online/local link repaired or the template deleted). Shown by `TemplateFixListView` (Maintenance → Templates to Fix, and the button on `RefreshTemplatesView`) in both apps: one card per item with "Add/Edit Template" → `HardwareItemDetail:{id}:TemplateFixList`, plus Dismiss |
| `Import/HardwarePdfImportService` | "Parse PDF into Bulk Add" (job page → select a PDF attachment). `HardwarePdfTextReader` reads text with PdfPig and rebuilds column layout (`LayoutTextBuilder`); image-only pages go to an `IPdfOcrEngine` (Windows OCR, `Shared/WindowsPdfOcrEngine.cs`, linked into both apps; the App targets `net8.0-windows10.0.19041.0` only when built on Windows, Wpf always). `HardwarePdfParser` understands hardware schedules/spec sets ("Heading #… (Set #…)" + item lines, incl. OCR'd scans) component-costing reports (manufacturer codes like AC01/S701 are named from the report's "Project Cost Summary" by matching each manufacturer's net total to the code's summed extended costs, `MatchCostSummaryNames`), and door machining sheets (one door type per page: "Qty  Hardware Details" left-column list with the manufacturer name first, "Prep:" notes, quantities × the page's "N Opening (hand): mark" count; text after a wide gap is drawing junk and is cut; compliance codes like BAA move to the end of the callout); otherwise `HardwareImportMatcher.FindKnownCallouts` picks out known model numbers/labels. `HardwareImportMatcher` groups lines into distinct callouts (handing/finish ignored) and resolves manufacturer (learned alias → PDF code list → common codes → name in callout) and item (learned mapping ≥ threshold → exact model number → exact past label; else suggestions for the wizard). Labels follow how jobs are entered: the callout (sizes stripped), a matching past label, or blank when it's just the model number. `HardwareImportLearningService` stores wizard answers in `PdfImportItemMappings`/`PdfImportManufacturerAliases` with confirmation counts; App Setting `PdfImportAutoMatchThreshold` (default 5, App Settings page) makes them automatic. `HardwareImportSession` is the shared non-UI side of `PdfImportWizardWindow` (both apps): before any review it marks callouts already on the job's active release or in its Bulk Add draft (`MarkAlreadyAdded`: same item+label, a label equal to the callout, or an unlabelled line with that model number) and leaves them out; `HardwareImportSimilarity.FindClusters` groups look-alike callouts (same model word, or a long shared start) into `SimilarCluster`s, each sorted on its own wizard page before the item steps: tick callouts and say they're the same item (`AddItem`, combined via `MergedInto`/`MergedGroups`), each its own item (`AddEach`) or left out (`Leave` → `LeftOutAsSimilar`, not learned), with Undo/Start Over; Next is blocked until `IsDone` (at most one unsorted callout left, which stays its own item). An item covering several labels (combined callouts, or labels used with the chosen item before) shows a tick box + editable label per label (`HardwareImportGroup.LabelChoices`, `BuildLabelChoices`); each ticked label is its own Bulk Add line (cover-sheet line). `HardwareImportLabelLearning` (AppSetting `PdfImportLabelLearning`) learns from label edits: words the user keeps removing are dropped, text they keep adding before/after an item's label is added (both after the import threshold); exact label per callout is still learned via mappings. Assemblies (`HardwareImportAssemblies`, `AssemblyWindow` in both apps, "Make Assembly..." on an item step): the callout becomes one line whose label is the description chosen from the Description tree (`DescriptionPickerWindow`; `AssemblyDescriptionId`, also pre-fills a new item's description) + every callout joined with " | "; parts come from the same heading/set (`SetTokens`, or every heading) and are left out (`PartOfAssembly`, not `InPlay`, steps skip them, not learned); assemblies are remembered as JSON in AppSetting `PdfImportAssemblies` and suggested next time (`SuggestedAssemblyParts`, user confirms). Finish merges into the Bulk Add draft (still skipping lines already there) and opens `BulkJobHub` |
| `TemplateFinder/TemplateFinderService` | "Find Template Online" (no paid services, no API keys): broken links (old link over https/http, Internet Archive copy), patterns from this manufacturer's past `OnlineLink`s (template number swapped in the file name, e.g. NGP `{n}_cutsheet.pdf`), and a crawl of the manufacturer's site(s) (hosts from past links minus shared CDNs, else `www.{name}.com`) via robots.txt sitemaps and product pages, obeying robots.txt (`RobotsRules`). Every link is downloaded and checked by `TemplateLinkChecker` (real PDF, page count, mentions a search term, looks like a template). Sites behind bot protection (Allegion, Hager: Cloudflare "Just a moment") are reported in `BlockedSites`; the window offers Google searches in the browser (`GoogleSearchUrl`/`GoogleSiteSearchUrl`) and a paste-a-link check. UI: `TemplateFinderWindow` (both apps, preview via `TemplatePreviewPanel.ShowPdfAsync`) through `Helpers/TemplateFinderFlow`: hardware item page and bulk-add template step ("Find Online..." fills the template form for the user to save), Refresh Templates ("Find New Link for Selected...") and Templates to Fix ("Find New Link...") which confirm, then `TemplateLinkReplacer` saves the link and re-downloads |
| `Catalogs/ProductCatalogService` + `Catalogs/CatalogLookupService` | Product catalogs (Maintenance → Product Catalogs, `ProductCatalogsView` in both apps): `ProductCatalog` rows (plain `ManufacturerId`, local file and/or online link) whose text `IndexAsync` reads (layout text via `HardwarePdfTextReader`, OCR for scans) into `ProductCatalogPage` rows (`Text` + `SearchKey` = `ImportText.Key`, searched with LIKE); online-only catalogs are downloaded to `{TemplateStorageLocation}/Product Catalogs/{Manufacturer}/`. "Search Website" uses `TemplateFinderService.FindCatalogLinksAsync` (same site-map crawl as the template finder, `CatalogUrlScore`). `CatalogLookupService.Lookup(mfrId, callout)` finds pages listing the model (`CatalogPageAnalyzer`: model keys, option codes, page score) and reads template numbers ("Template No. …" and known template numbers on the page, linked to templates/items already in the DB), option meanings ("EDA  Extra Duty Arm"), the product description and a suggested `Description` category. Used by `CatalogLookupWindow` (both apps, page preview via `PagePdfAsync`): hardware item page "Look Up in Catalog..." (links a known template or fills Template #/description), Find Template Online (catalog panel; the numbers go into `TemplateSearchRequest.CatalogTemplateNumbers` → search terms + your existing templates with those numbers), and Parse PDF into Bulk Add (`CatalogImportHints`: `HardwareImportGroup.CatalogInfo` shown in the wizard, items using the catalog's template suggested, new-item description pre-picked; never automatic) |
| `TemplatePageInfo` | Add-template defaults used by all three add-template forms (hardware item page, bulk-add template step, Individual Templates) via each app's `Helpers/TemplatePagesHelper`: door material defaults to "Both", description to the hardware item's own; when the Online Link/Local File changes the PDF's pages are counted (`CountPagesAsync`, local file first, else download) and a blank (or still auto-filled) Pages To Print becomes all pages (`AllPages` → "1-N"); Num Pages always follows the Pages To Print range; a blank Pages To Print at save means all pages (counted then) |
| `Platform/AppShortcuts` (Windows) | Taskbar pinning: both apps run with **no** AppUserModelID. At startup `Program.CreateStartMenuShortcut` calls `StripOldAppIds` (removes the old "HardwareTemplateBuilder.App"/".Wpf" IDs, or any ID on a shortcut pointing at this exe, from Start Menu, Desktop and taskbar pinned/implicit shortcuts: a leftover ID makes the taskbar menu show only "Close window"), then writes the plain Start Menu shortcut with `CreateShortcut` (IShellLink COM, no WScript) |
| `MissingTemplateAlertService` | Checks for hardware items missing template files at startup (best-effort, background) |
| `JobStorageLocationResolver` | Decides where a job's folder lives: owner's `UserProfile.CustomJobSaveLocation` → App Settings `TemplateStorageLocation` → My Documents. Mirror copy to the shared location only when it is actually configured. Use `ResolveForJob`/`ResolveForProfile`; never re-implement the fallback inline |
| `JobFolderMigrationService` | Moves a user's job folders from this computer's local fixed drives (My Documents, a local shared location, folders their attachments point at) into their job save location. Preview → confirm → Apply; never overwrites (conflicts kept as `name (moved from local drive).ext`), re-points `JobAttachment.StoredPath`, leaves `JobTemplateSnapshot` untouched. `Preview(userId, fromFolder)` searches only that folder (any drive, incl. shares). Admin → User Profiles in both apps: changing a user's job save location asks to move their jobs from the folder they were going to (old location, else shared/My Documents); the "Move Existing Jobs Here..." button does the local-drive search |

### PDF Assembly Pipeline (Core/Services/Pdf/)

`PdfAssemblyService.AssembleAsync` accepts an `AssemblyRequest` and executes:

1. `TemplateSorter` (uses `ITemplateSortStrategy` — default: `WeightTemplateSortStrategy`) → sorted template list
2. `FileAcquirer` → local copies in `{JobNumber}/` subfolder as `{ManufacturerName}_{TemplateNumber}.pdf`
3. `PageExtractor` → `PageRotator` → `PdfMerger` → merged body PDF
4. `CoverSheetBuilder` (QuestPDF) → cover sheet PDF with header block and hardware table
5. Prepend cover sheet + `PageNumberer` stamps sequential numbers offset past cover pages

It does **not** write `JobTemplateSnapshot` records itself — that, along with the rest of the per-job generation workflow, is the caller's job:

- **`JobPackageGenerationService`** wraps `AssembleAsync` with everything needed to generate one job's package end to end: loads job/hardware data, backs up any previous output into `{JobNumber}/Old versions/{timestamp}/`, pre-downloads templates missing a local copy, calls `AssembleAsync`, then writes the `JobTemplateSnapshot` rows, increments `HardwareItem.Frequency`, and appends a `job_history.txt` entry. This is the single shared pipeline behind both the "Generate Package" button (`JobDetailView`) and batch generation.
- **`BatchJobPackageGenerationService`** loops `JobPackageGenerationService` over many jobs (used by `BatchGenerateView`, reachable from Jobs → Batch Generate). A failing job is recorded and skipped — not fatal to the run — while a cancellation request aborts the whole batch immediately, matching the collect-and-continue / cancel-aborts pattern already used by `TemplateRefreshService.RefreshAsync`.

**Categorized failure reports:** `AssembleAsync` collects every problem into one report grouped by what it's attributed to — a hardware item with no linked templates, a specific template that couldn't be acquired/extracted/rotated (labeled with the owning hardware item's model number via `BuildTemplateToItemsMap`), or something else (a merge/cover-sheet/page-numbering failure). Still exactly one `InvalidOperationException` per call, just with a clearer, categorized message.

#### QuestPDF constraints (CoverSheetBuilder)

`page.Header()` in QuestPDF is rendered as a **completely fresh, independent context on every page** — no state carries across page renders. This means `ShowOnce()`, `SkipOnce()`, and `IDynamicComponent` (including reading `context.PageNumber`) all behave as if every page is page 1 when placed inside `page.Header()`. The only reliable way to show something once on the first page is to put it in `page.Content()` wrapped in `ShowOnce()`, which works because content is a single continuous flow across pages.

- `DynamicContext` is in `QuestPDF.Elements` (not `QuestPDF.Infrastructure`/`QuestPDF.Fluent`) — add `using QuestPDF.Elements;` if implementing `IDynamicComponent`
- `IDynamicComponent` (non-generic) is in `QuestPDF.Infrastructure`; `IDynamicComponent<TState>` extends it with a `State` property
- Cover sheet row ordering is by **minimum body page number** (not description path), so the Page # column always ascends in document order
- `CoverSheetOrder` reproduces that order without building a PDF (item key = its earliest template by `WeightTemplateSortStrategy` path + template number, ties by label; items with no templates yet fall back to their own description path; nothing to place by → last). Used by the "Sort Like Cover Sheet" button on `BulkHardwareEntryView` in both apps. If `BuildCoverSheetData`'s ordering changes, update `CoverSheetOrder` to match

**Duplicated pipeline logic:** `TemplateLookupView` (in both `HardwareTemplateBuilder.App` and `HardwareTemplateBuilder.Wpf`) and `HardwareTemplateBuilder.Lookup` all bypass `PdfAssemblyService` and manually re-implement steps 1–3 (`TemplateSorter` → `FileAcquirer` → `PageExtractor`/`PageRotator`/`PdfMerger`) to produce a quick merged PDF without a cover sheet or `JobTemplateSnapshot` write. Changes to those four classes' behavior/signatures must be mirrored in all three call sites, not just `PdfAssemblyService`.

### UI (App/)

- **Architecture is code-behind, not MVVM** — views directly instantiate repositories and call services; there are no ViewModels or data-binding commands
- `MainWindow` hosts a persistent menu bar (File, Jobs, Maintenance, Admin) and a content area
- `ViewRouter` swaps child `UserControl` views for navigation — 28 views total; **a new instance is created on every navigate** (no view caching)
- `SessionService` (static) holds the active `UserProfile` for the session lifetime; `BulkAddSession` (static) holds transient bulk-entry state across the multi-step bulk-add flow
- **Bulk-add flow** is a multi-step wizard entirely within the content area: `BulkManufacturerSelectionView` → `BulkJobHubView` → `BulkManufacturerSessionView` → `BulkHardwareEntryView` → `TemplateResolutionWizardView` (persists `JobHardware` on Finish). State is shared via `BulkAddSession` and `BulkSessionDraft` static classes. On the hub's "Continue" (and on Import Hardware), `BulkHardwareSaver.SaveRowsWithTemplates` saves every row that already has templates straight onto the job (active release) and only rows with no linked/staged templates (`BulkHardwareSaver.NeedsTemplates`) go to `TemplateResolutionWizardView`; if none do, it goes straight back to `JobDetail:{id}`. The wizard's Finish saves its rows with the same `BulkHardwareSaver.Save`. `JobDetailView` also has "Remove All Hardware" (clears the active release's list after a confirm).
- **`BatchGenerateView`** (Jobs → Batch Generate) lets the user select several jobs and generate all their packages in one run via `BatchJobPackageGenerationService`. Selection is a session-only `HashSet<int>` of job IDs (not persisted), sorted oldest/never-generated first; a checkbox toggle brings inactive (complete) jobs into the list. A creator filter combo (mirrors `JobsView`'s `CreatorFilterCombo`, defaulting to `SessionService.ActiveUserProfile`) scopes the list to one user's jobs. It's the first view with a per-row checkbox inside a `ListBox` `ItemTemplate` — built with `supportsRecycling: false` since recycled containers can desync their checked state from the underlying row on scroll.
- **Import hardware flow** feeds into the same `TemplateResolutionWizardView`. `JobDetailView` shows an "Import Hardware" button that opens `ImportJobPickerDialog` (searchable job list), then populates `BulkAddSession.PendingRows` from the source job's base `JobHardware` rows (ordered by `Id`) and navigates to `TemplateResolutionWizard:{jobId}`. Job-scoped templates from the source job appear in `row.PendingTemplates` and are cloned at Finish time.
- On first launch with no `UserProfile` records, `ProfilePickerDialog` prompts creation before proceeding; `DatabaseSetupDialog` handles unreachable DB paths at startup
- **Visual style:** a dark theme (`RequestedThemeVariant="Dark"` in `App.axaml`, chosen for long-session eye comfort — page `#1A1D23`, card surfaces `#23262D`, body text `#E8EAED`, light-blue accent `#4FA3D1`) with rounded corners and a `Segoe UI`/`Inter` font stack, layered on Avalonia's built-in `FluentTheme` rather than a hand-rolled `ControlTemplate`. `App.axaml` overrides `SystemAccentColor` (+ its `Light1-3`/`Dark1-3` variants, deliberately *lighter* than a light-theme accent would be so it stays visible against the dark background) and FluentTheme's own `DynamicResource` brush keys (`ButtonBackgroundPointerOver`, `TextControlBorderBrushFocused`, `ControlCornerRadius`, etc.) so hover/pressed/focus states re-skin correctly without touching any control template. `Styles/AppTheme.axaml` sets base-state Background/BorderBrush/Foreground/CornerRadius/typography per control type plus the `Border.panel`/`Button.dashboard`/`Button.dashboard-primary` classes — `Button.dashboard-primary` intentionally fills with the *darker* `Dark1` accent tint (`#3D82AA`), not the base accent, so its white text keeps good contrast.
  - **`Helpers/AppColors.cs`** holds the status-text brushes (`Danger`/`Success`/`Muted`/`Primary`/`Info`/`Warning`) that XAML Styles can't reach — code-behind sets `StatusLabel.Foreground` etc. directly in ~8 views (`JobDetailView`, `JobsView`, `DeduplicateHardwareView`, `BulkHardwareEntryView`, `BulkJobHubView`, `BulkManufacturerSessionView`, `TemplateResolutionWizardView`, `AppSettingsView`). These used to be light-theme-appropriate named brushes (`Brushes.DarkRed`, `Brushes.Gray`, etc.) baked directly into C# — if you see a new one, add it to `AppColors` and reference that instead of a raw `Brushes.*`/hex value, or it'll silently look wrong (usually low-contrast/invisible) against the dark background.
  - Predates a 2026 redesign from an earlier Windows-98-style beveled light theme — if you see `#C0C0C0`/`#808080`/Tahoma references anywhere, that's leftover from the old look and should be updated to match. `HardwareTemplateBuilder.Lookup` (the standalone viewer) has its own separate, still-unmigrated Win98-style theme file — out of scope for the main app's redesign.
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

Batch operations (PDF assembly, template refresh) collect all per-item failures into a list and throw a single `InvalidOperationException` with a consolidated report — not fail-fast. `PdfAssemblyService.AssembleAsync`'s report further groups failures by category (hardware item / template / other) — see PDF Assembly Pipeline above. Batch **job** generation (`BatchJobPackageGenerationService`) applies this same collect-and-continue idea one level up: a failing job is skipped and recorded, not fatal to the run, while cancellation aborts the whole batch rather than skipping just the current job. Background startup tasks (missing-template alert, auto-refresh) swallow all exceptions to avoid disrupting the UI.

### WPF frontend (Wpf/)

`HardwareTemplateBuilder.Wpf` is a feature-for-feature port of `HardwareTemplateBuilder.App` to WPF
(`net8.0-windows`, `EnableWindowsTargeting=true` so the solution still builds on Linux/CI). It shares
`HardwareTemplateBuilder.Core` and mirrors the App's structure file-for-file: same view names, same
code-behind (no MVVM), same `ViewRouter` route strings and `"ViewName:{id}"` parameterized routes, same
`SessionService`/`BulkAddSession`/`BulkDraftService` state. **When you change behavior in an App view,
make the same change in the matching Wpf view** (and vice versa).

- **Look:** Windows 95, copied from SpecialJobLookup's WPF theme so the two apps match. `Themes/Win95.xaml`
  is that theme (bevelled 3D edges built from nested 1px borders, gray `#C0C0C0` face, navy `#000080`
  selection/caption, MS Sans Serif); `Themes/Win95Extras.xaml` merges it and adds menus, radio buttons,
  tree, progress bar, the `Etched` frame and dashboard button styles. App.xaml merges only Extras.
  Page titles are navy `CaptionBar` borders; the App's `Border.panel` frames are `GroupBox` (when they
  had a bold title) or `ContentControl Style="{StaticResource Etched}"`. Lists draw their own sunken
  frame. There is no Light/Dark setting in this frontend.
- **Title bar:** the `Win95Window` style (Win95Extras.xaml, used by `MainWindow` and every `Win95Window`
  dialog) replaces the Windows 10/11 title bar via `WindowChrome` with a drawn Win95 caption (navy, gray
  when inactive; icon = system menu; bevelled min/max-restore/close) inside a raised 3D frame. Caption
  buttons need `WindowChrome.IsHitTestVisibleInChrome="True"`. `Ui/Win95Chrome.Register()` (called in
  `App.OnStartup`) binds the `SystemCommands` for all windows, turns off Windows 11 rounded corners and
  pads the template root (`PART_Win95Root`) when maximized. Dialogs not in the taskbar get no minimize
  button; `NoResize` windows get only Close. Use `DialogHelper` (incl. `ShowErrorAsync`) for pop-ups, not `MessageBox.Show` (native, keeps the OS title bar; only the crash handler's fallback uses it).
- **Shell:** `MainWindow` has the same menu bar as the App (File, Jobs, Template Lookup, Maintenance,
  Admin), Win95-styled; menu items with a `Tag` navigate to that route via `Nav_Click`, the rest run a
  command. Status bar = thin sunken fields (status, user, clock). No access-key underscores in labels.
- **Avalonia shims in `Ui/`** keep ported code close to the original: `ui:GridEx.Columns/Rows`
  (compact `"Auto,*"` grid specs), `ui:Stack` (StackPanel with `Spacing`), `ui:Watermark.Text`
  (placeholder for TextBox/ComboBox, drawn by the theme templates), `Win95Window` (dialog base with
  `CloseWith(result)` / `ShowDialogAsync<T>(owner)` in place of Avalonia's `Close(result)` /
  `ShowDialog<T>`), `ItemFactory.SetItemFactory<T>` (code-built list rows, for Avalonia's
  `FuncDataTemplate`), `FilePickers` (Win32 dialogs returning paths), and `UiExtensions`
  (`SetVisible`/`IsShown` for `IsVisible`, `OwnerWindow()` for `TopLevel.GetTopLevel`,
  `WhenChanged` for `PropertyChanged` checks, `UiExtensions.Post` for `Dispatcher.UIThread.Post`).
- `Helpers/AppColors` has the same member names as the App's but Win95-palette colors (dark red,
  dark green, navy). Use it for code-behind status colors, same rule as in the App.
- Gotchas: WPF `SelectionChanged`/`TextChanged` bubble, so a selector inside another selector's item
  will fire the outer handler; `CheckBox.Click` (used for Avalonia's `IsCheckedChanged`) only fires on
  user clicks, not programmatic `IsChecked` changes; `TreeView.SelectedItem` is read-only, so
  `DescriptionsView` binds `IsSelected`/`IsExpanded` on its node objects instead.

### Description Hierarchy

`Description` is a self-referential tree used for categorizing hardware items. `DescriptionPathService` resolves full paths. The hierarchy is also how `WeightTemplateSortStrategy` groups templates for sorting.

### App Startup Sequence

`App.OnFrameworkInitializationCompleted()` defers startup to `mainWindow.Opened`. Phases: database location resolution → migration + schema patches → machine ID lookup → profile selection → background refresh check.

## Releases

See `RELEASING.md`. Releases are produced by the GitHub Actions workflow in `.github/workflows/release.yml` — triggered by a `v*` tag push or manual dispatch. It runs on `windows-latest` and attaches two self-contained single-file Windows x64 builds: the Avalonia app as `HardwareTemplateBuilder-{VERSION}-win-x64.zip` and the WPF app as `HardwareTemplateBuilder95-{VERSION}-win-x64.zip`. Visual Studio publish profiles (`Properties/PublishProfiles/*.pubxml`) must name `net8.0-windows10.0.19041.0` for both apps (the App only uses that TFM when built on Windows); a profile with the old `net8.0`/`net8.0-windows` makes VS publish fail with "unable to determine the cause".

`Program.cs` (in both App and Wpf) recreates a plain Start Menu shortcut (`Hardware Template Builder.lnk` / `Hardware Template Builder 95.lnk` under `%APPDATA%\Microsoft\Windows\Start Menu\Programs`) on every launch so the app appears in Start search, using `Environment.ProcessPath` (not `Process.MainModule`) for the launcher path. **Do not set an AppUserModelID** (no `SetCurrentProcessExplicitAppUserModelID`, nothing on the shortcut or window): without one, Windows identifies the app by its exe path like any ordinary program and the taskbar right-click menu shows the app name and "Pin to taskbar". With an explicit ID that Windows couldn't match to a shortcut, the menu showed only "Close window" (several earlier fixes, including the scripts/docs in `HardwareTemplateBuilder.App/` such as `FixTaskbarPinning.ps1`, `VerifyAppId.ps1` and `PINNING_QUICK_START.md`, describe that old ID-based approach and are obsolete).

The `HardwareTemplateBuilder.SmokeTest` console project runs full CRUD against all entity types to validate schema and relationships — run it manually against a real database when testing migrations or repository changes.

## Code Quality Requirements

- All public methods, classes, and non-obvious variables require XML doc comments (`/// <summary>`)
- SOLID principles: Repository for data access, Strategy for sorting (`ITemplateSortStrategy`), Factory via `ViewRouter`
- All file paths must use `Path.Combine()` — no hardcoded separators
- Nullable reference types enabled across all projects

- `HardwareTemplateBuilder.Tests/MigrationModelTests` fails if the EF model and the migrations snapshot differ (the app runs `Migrate()` at startup and EF Core 9 refuses pending model changes). Add a migration whenever you change the model.
