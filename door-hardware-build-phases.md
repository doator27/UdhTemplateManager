# Door Hardware Template Builder — Phased Development Plan

Each phase produces a runnable, testable state of the application. No phase requires rework of a previous one — only additions and extensions.

---

## Phase 1 — Project Scaffold & Database Foundation

**Goal:** A compiling solution with a working SQLite database that creates itself on first run.

### Tasks
- Initialize a .NET 8 solution with two projects:
  - `HardwareTemplateBuilder.Core` — class library (models, data access, services)
  - `HardwareTemplateBuilder.App` — Avalonia UI application
- Install dependencies: `Avalonia`, `EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`
- Define all EF Core model classes (one per table):
  - `UserProfile`, `Customer`, `Manufacturer`, `Description`, `DoorMaterial`, `Weight`
  - `ProjectManager`, `HardwareItem`, `IndividualTemplate`
  - `HardwareItemTemplates`, `Job`, `JobHardware`, `JobTemplateSnapshot`
- Configure `AppDbContext` with all DbSets and relationships (foreign keys, constraints)
- Enforce `DoorMaterial` to only allow `"Hollow Metal"` or `"Wood"` at the model/seed level
- Implement `DatabaseInitializer` — creates the SQLite file and applies migrations on first run, storing the `.db` file in `Environment.SpecialFolder.ApplicationData`
- Write a simple console smoke test (or unit test) that: initializes the DB, inserts one record into each table, reads it back, and deletes it

### Deliverable
Solution compiles and runs. SQLite file is created on launch. All tables exist with correct schema and constraints.

---

## Phase 2 — Repository Layer & Core Services

**Goal:** A clean data access layer that all future UI and business logic will consume. No UI yet.

### Tasks
- Implement the **Repository pattern** (Gang of Four) with a generic `IRepository<T>` interface supporting: `GetById`, `GetAll`, `Add`, `Update`, `Delete`
- Create concrete repositories for each entity in `HardwareTemplateBuilder.Core`
- Implement redundancy checks in `Add` methods — return existing record if a duplicate is detected
- Implement `IHardwareItemRepository` with a special `Search(manufacturer, description, modelNumber)` method that returns results sorted by `Frequency` descending
- Implement `FrequencyService` — increments `HardwareItem.Frequency` each time an item is retrieved via lookup
- Implement `WeightParser` — parses the `00.000.000` weight string into comparable segments for sorting
- Implement `PageRangeParser` — parses `PagesToPrint` / `PagesToRotate` strings (e.g. `"1,3-5,8"`) into a sorted list of page numbers
- Write unit tests for `WeightParser`, `PageRangeParser`, and the redundancy checks

### Deliverable
Full repository layer testable without any UI. All data operations go through repositories. Parsers validated by tests.

---

## Phase 3 — Avalonia Shell & Windows 98 Theme

**Goal:** A running Avalonia application with the correct visual style, navigation structure, and placeholder pages.

### Tasks
- Set up the Avalonia app entry point in `HardwareTemplateBuilder.App`
- Create the main `MainWindow` with:
  - A persistent top **menu bar** with dropdowns: `File`, `Jobs`, `Maintenance`, `Admin`
  - A **dashboard panel** as the default view, with large buttons leading to sub-areas
- Apply the Windows 98 / classic Win32 theme globally:
  - Background color: `#C0C0C0`
  - Embossed/beveled button styles via Avalonia `ControlTheme` or `Style`
  - Font: `Tahoma` or `Microsoft Sans Serif` with `Arial` as fallback
  - Classic border and inset styling on panels, listboxes, and comboboxes
- Implement a simple **navigation/view router** — a content area in `MainWindow` that swaps child views in and out
- Create empty placeholder `UserControl` views for each area (CRUD pages, lookup, job builder, maintenance) — just a label identifying the page
- Wire all menu items and dashboard buttons to navigate to their placeholder views

### Deliverable
App launches with correct visual style. Navigation works throughout. All pages are reachable (even if empty).

---

## Phase 4 — Simple Table CRUD Screens

**Goal:** Full Create, Read, Update, Delete functionality for all simple (non-relational) tables.

### Scope
Build CRUD screens for: `Customer`, `Description`, `DoorMaterial`, `Manufacturer`, `ProjectManager`, `UserProfile`, `Weight`

### Tasks (repeat pattern for each table)
- **List view:** A listbox showing all records. A search/filter combobox or text input at the top.
- **Detail/Edit form:** Fields matching the table columns. A Save button (creates or updates). A Delete button with confirmation dialog.
- `DoorMaterial` form: Material field is a combobox locked to `"Hollow Metal"` / `"Wood"`
- `Weight` form: Includes a Description combobox (FK lookup) and a formatted entry field with validation for the `00.000.000` format
- `UserProfile` form: Includes a folder picker for `DefaultTemplateSaveLocation`
- Wire all forms to their respective repositories
- On first launch (no user profiles exist), prompt the user to create a `UserProfile` before proceeding

### Deliverable
All simple tables are fully manageable through the UI. Data persists across restarts.

---

## Phase 5 — HardwareItem & IndividualTemplate CRUD

**Goal:** CRUD for the two most complex records, including their relational linking.

### Tasks

**IndividualTemplate CRUD:**
- Form fields for all columns including FK comboboxes for: `Manufacturer`, `Description`, `DoorMaterial`, `Weight`
- `PagesToPrint` and `PagesToRotate` fields with inline validation using `PageRangeParser`
- `RotationDirection` field as a numeric input (positive/negative degrees)
- `OnlineLink` and `LocalLink` as text fields; `LocalLink` also has a file picker button

**HardwareItem CRUD:**
- Form fields for: `Manufacturer` (combobox), `Description` (combobox), `ModelNumber`, `Remarks`
- `Frequency` displayed as read-only
- **Linked Templates sub-panel:** below the main form, a listbox showing all `IndividualTemplate` records currently linked via `HardwareItemTemplates`
  - A search area (Manufacturer + Description + TemplateNumber comboboxes + listbox) to find templates to link
  - "Link" and "Unlink" buttons to manage `HardwareItemTemplates` records
- Wire all to repositories

### Deliverable
Hardware items and templates can be fully created, edited, and linked together.

---

## Phase 6 — Job Creation & Hardware Linking

**Goal:** Jobs can be created and hardware items can be added to them.

### Tasks
- **Job CRUD screen:**
  - Fields: `JobNumber`, `JobName`, `Customer` (combobox), `ProjectManager` (combobox), `UserProfile` (combobox)
  - **Linked Hardware sub-panel:** listbox showing all `HardwareItem` records currently linked to the job via `JobHardware`
    - Search area using the standard 3-combobox + listbox pattern (Manufacturer, Description, ModelNumber)
    - Results sorted by `Frequency` descending
    - "Add to Job" and "Remove from Job" buttons managing `JobHardware` records
    - Drag-and-drop reordering of the linked hardware listbox to set the final sort order override
- Wire all to repositories; increment `Frequency` on every hardware item lookup

### Deliverable
Jobs can be created and populated with hardware items. Hardware order can be adjusted via drag-and-drop.

---

## Phase 7 — PDF Engine (Core)

**Goal:** A working, tested PDF assembly service — no UI trigger yet.

### Tasks
- Choose and install the PDF library: recommend **PdfSharp** (MIT) for merging/rotation and **QuestPDF** (MIT) for cover sheet generation
- Implement `PageExtractor` — given a PDF file path and a parsed page list, returns a new PDF containing only those pages
- Implement `PageRotator` — applies rotation to specified pages in a PDF
- Implement `PdfMerger` — merges an ordered list of processed PDFs into a single output file
- Implement `PageNumberer` — stamps sequential page numbers (bottom-center) onto pages in a PDF, with a configurable start offset (to skip cover sheet pages)
- Implement `TemplateSorter` using the **Strategy pattern**:
  - Within a manufacturer group: sort by `Weight` ascending
  - Between manufacturer groups: sort by the group's lowest weight; alphabetical by `ManufacturerName` on tie
- Implement `FileAcquirer` — given an `IndividualTemplate`, either copies the local file or downloads from `OnlineLink` into the job's subfolder using the `{ManufacturerName}_{TemplateNumber}.pdf` naming convention
- Write unit/integration tests for each service using sample PDFs

### Deliverable
Full PDF pipeline testable in isolation. Given a list of templates, produces a correctly assembled and numbered PDF.

---

## Phase 8 — Cover Sheet Generator

**Goal:** A professional cover sheet PDF is generated and prepended to every package.

### Tasks
- Implement `CoverSheetBuilder` using **QuestPDF**:
  - Header block: Title (`"Unified Door and Hardware Templates"`), Job Number, Job Name, Customer, Project Manager, Date Created, Page X of Y
  - Body table with one row per hardware item: Manufacturer, Hardware Type, Hardware Description, Template Number(s), Page Number(s), Remarks
  - Handle multi-page cover sheets gracefully (the table may overflow to a second page)
- Integrate `CoverSheetBuilder` into the full PDF assembly pipeline — cover sheet is always the first page(s)
- Ensure page numbering starts after all cover sheet pages

### Deliverable
Every assembled PDF begins with a fully populated, professionally formatted cover sheet.

---

## Phase 9 — Individual Template Lookup Feature

**Goal:** Users can look up a hardware item and instantly receive a merged PDF of its templates.

### Tasks
- Build the **Individual Template Lookup** view:
  - Three comboboxes: Manufacturer, Description, ModelNumber (each filters the next)
  - Listbox showing matching `HardwareItem` records, sorted by `Frequency` descending
  - Default selection: first item in listbox if user hasn't clicked
  - "Generate PDF" button
- On "Generate PDF":
  1. Retrieve all linked `IndividualTemplate` records via `HardwareItemTemplates`
  2. Sort by `Weight` ascending
  3. Acquire files (local copy or download)
  4. Run through `PageExtractor` → `PageRotator` → `PdfMerger` → `PageNumberer`
  5. Save to the active user's `DefaultTemplateSaveLocation`
  6. Open the file with the OS default PDF viewer
  7. Increment `Frequency` on the selected `HardwareItem`
- Show a progress indicator during generation

### Deliverable
Users can search for a hardware item and receive a correctly assembled, numbered PDF in one click.

---

## Phase 10 — Full Job PDF Package Generation

**Goal:** A complete job PDF package is assembled from all hardware items on a job, with cover sheet and snapshots.

### Tasks
- Add a "Generate Package" button to the Job screen
- On trigger:
  1. Retrieve all `HardwareItem` records linked via `JobHardware`
  2. For each, retrieve linked `IndividualTemplate` records via `HardwareItemTemplates`
  3. Apply `TemplateSorter` (manufacturer groups, weight sort, alphabetical tiebreak)
  4. Run `FileAcquirer` for all templates — copy/download to `{JobNumber}/` subfolder
  5. Run `PageExtractor` → `PageRotator` → `PdfMerger`
  6. Run `CoverSheetBuilder` with full job metadata and hardware table
  7. Prepend cover sheet; run `PageNumberer` starting after cover sheet
  8. Save final PDF to job folder
  9. Write one `JobTemplateSnapshot` record per template used, capturing file path, pages, rotation, and date
  10. Open the completed package
- Show a progress bar with per-template status during generation
- Increment `Frequency` on all hardware items used

### Deliverable
Full job PDF packages are generated correctly, sorted, numbered, and snapshotted.

---

## Phase 11 — Maintenance: Refresh Templates

**Goal:** All templates with online links can be refreshed in bulk; failures are reported clearly.

### Tasks
- Add a "Refresh Templates" option under the `Maintenance` menu
- On trigger:
  1. Query all `IndividualTemplate` records with a non-null `OnlineLink`
  2. For each, attempt download to `DefaultTemplateSaveLocation` using `{ManufacturerName}_{TemplateNumber}.pdf` naming
  3. On success: update `LocalLink` in the database
  4. On failure (HTTP error, timeout, empty response): record the failure
  5. After all attempts complete, display a results window listing every failed template with its ID, name, and the error encountered
- `JobTemplateSnapshot` records are never modified by this process
- Run downloads asynchronously with a progress bar; allow cancellation

### Deliverable
Templates stay current with one click. Failures are surfaced clearly for manual follow-up.

---

## Phase 12 — Polish, Validation & Edge Cases

**Goal:** Production-ready hardening across the full application.

### Tasks
- Add input validation to all forms (required fields, format checks, FK existence)
- Add confirmation dialogs for all destructive actions (Delete, overwrite existing PDF, etc.)
- Handle missing/moved local files gracefully — warn the user and offer to re-download or re-link
- Handle jobs with zero hardware items — block PDF generation with a clear message
- Handle templates with no local or online link — warn during job generation; list them before proceeding
- Add an "About" dialog with version info
- Ensure all file paths use `Path.Combine()` — audit the entire codebase
- Final XML doc comment audit — every public method, class, and non-obvious variable must be documented
- Review all code against SOLID and DRY — refactor any violations found during integration
- Cross-platform smoke test: run and generate a full PDF package on both Linux and Windows

### Deliverable
Application is stable, validated, and ready for real-world use.

---

---

## Phase 13 — Template-Add Panel Refinements & Door Material Values

**Goal:** Streamline the add-template workflow inside `HardwareItemDetailView` and correct the `DoorMaterial` value set.

### Tasks

**Add-Template Panel (HardwareItemDetailView):**
- Remove the Manufacturer combobox from the add-template search panel entirely — it is always the same manufacturer as the parent hardware item
- Pre-filter the template search results server-side so only templates whose `ManufacturerId` matches the hardware item's `ManufacturerId` are shown
- Pre-filter the Description combobox to show only descriptions that are descendants (children, grandchildren, etc.) of the hardware item's own `DescriptionId`:
  - Implement (or reuse) a BFS/DFS `GetDescendantIds(int rootId, IEnumerable<Description> allDescs)` helper that returns the root plus all transitive children
  - Load that filtered set into the Description combo when the detail view opens
- Filter the template search results further so only templates whose `DescriptionId` is in the descendant set are displayed
- Existing linked templates list: apply the same manufacturer + descendant-description filter so unrelated templates are never shown as candidates

**Template file source — local path support:**
- When creating or editing an `IndividualTemplate`, the user may supply either (or both) an `OnlineLink` URL and a `LocalLink` file path — these are already separate columns in the schema
- Ensure the `IndividualTemplate` form (in `HardwareItemDetailView`'s add-template panel and in the standalone `IndividualTemplatesView`) shows both fields clearly:
  - `OnlineLink` — text input for a URL
  - `LocalLink` — text input with an adjacent "Browse…" file-picker button that opens a native open-file dialog filtered to `*.pdf`
- `FileAcquirer` priority logic: if `LocalLink` is set and the file exists on disk, use it directly (no download); fall back to `OnlineLink` if the local file is missing or the path is empty
- During template refresh (Phase 11 / Phase 16), only re-download templates that have an `OnlineLink`; templates with only a `LocalLink` are skipped (the file is managed by the user)
- Validation: at least one of `OnlineLink` or `LocalLink` must be non-empty before the template can be saved

**Door Material values:**
- Change the allowed `DoorMaterial` values from `"Hollow Metal"` / `"Wood"` to: **`"Metal"`**, **`"Wood"`**, **`"Both"`**
- Update the `DoorMaterial` seed data / constraint in Phase 1's `DatabaseInitializer` (and any EF migration) to use the new values
- Update the `DoorMaterial` CRUD form combobox (Phase 4) to offer `Metal`, `Wood`, `Both`
- Update any existing database records (migration script) that contain `"Hollow Metal"` → `"Metal"`

### Deliverable
The add-template panel no longer shows irrelevant manufacturers or unrelated descriptions. `DoorMaterial` reflects the correct three-value set throughout the application and database.

---

## Phase 14 — Job Backup Folder Restructuring

**Goal:** Previous versions of a job package are stored in a clearly organized sub-folder rather than cluttering the root save directory.

### Tasks
- When generating a new PDF package for a job that already has an existing output file, move the existing file into:
  ```
  {OutputDirectory}/{JobNumber}/Old versions/{original_creation_datetime}/
  ```
  where `original_creation_datetime` is formatted as `yyyy-MM-dd HH-mm-ss` (filesystem-safe)
- The `original_creation_datetime` should come from the file's last-write timestamp (or the `DateCreated` field on the previous `JobTemplateSnapshot` batch if available)
- Create the `Old versions` sub-folder and datetime-named folder automatically; never overwrite files within an existing datetime folder
- Update `PdfAssemblyService` (and the job generation trigger in `JobDetailView`) to perform the backup move before writing the new output file
- Ensure the backup step is skipped cleanly if no previous file exists (first-time generation)
- Add a "Browse Old Versions" button to the Job screen that opens the `Old versions` folder in the OS file explorer (if it exists)

### Deliverable
Every re-generation automatically archives the previous package in a dated sub-folder. Old versions are easy to find without polluting the main save location.

---

## Phase 15 — Job-Centric Add-Anything Workflow

**Goal:** All entity creation (hardware items, templates, manufacturers, descriptions, customers, project managers) is accessible directly from within the Job screen without leaving the job context.

### Tasks
- Add a toolbar or action panel to `JobDetailView` (or its linked hardware sub-panel) with quick-access buttons:
  - **New Hardware Item** — opens `HardwareItemsView` in a modal/side-panel pre-filtered to the job's context; on save, optionally adds the new item to the job automatically
  - **New Template** — opens `IndividualTemplatesView` in a modal; on save, optionally links the new template to the currently selected hardware item
  - **New Manufacturer** — inline dialog (single text field) or navigates to `ManufacturersView`; newly created manufacturer is immediately available in all FK combos
  - **New Description** — opens `DescriptionsView` (or a simplified add-description dialog); new description is immediately available in all FK combos
  - **New Customer** — inline add panel (already implemented as of Phase 12+); ensure it is consistent here
  - **New Project Manager** — inline add panel (already implemented); ensure it is consistent here
- All modals/dialogs must refresh their parent FK comboboxes upon close so newly created records are immediately selectable without a full view reload
- Navigation away from the job to perform these actions must preserve the current job state (unsaved changes prompt or auto-save draft)

### Deliverable
A user can build an entire job — including creating every referenced entity from scratch — without ever leaving the job screen.

---

## Phase 16 — Refresh Templates 403 Fix

**Goal:** The Maintenance "Refresh Templates" feature successfully downloads templates from servers that block bare HTTP requests, matching the fix already applied to job package generation.

### Tasks
- In `FileAcquirer.DownloadAsync` (or the equivalent download path used by the refresh workflow), ensure every HTTP request sends a realistic browser `User-Agent` header:
  ```
  User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36
  ```
- Use `HttpRequestMessage` with `request.Headers.TryAddWithoutValidation("User-Agent", ...)` rather than setting it on the `HttpClient` default headers (avoids InvalidOperationException on shared clients)
- Verify this same header is present in all code paths that download a template file:
  1. Job package generation (`PdfAssemblyService` → `FileAcquirer`)
  2. Individual template lookup generation (`TemplateLookupView` → `FileAcquirer`)
  3. Maintenance refresh (`MaintenanceView` → `FileAcquirer` or equivalent)
- Add the fix to the shared `FileAcquirer` so all three paths benefit from a single change
- Regression test: confirm that a template with a known `OnlineLink` downloads without error via the refresh path

### Deliverable
Maintenance refresh no longer returns 403 errors. All three download paths (job package, individual lookup, maintenance refresh) share a single download implementation with the correct User-Agent.

---

## Phase 17 — Machine-Bound User Profile & Auto-Select

**Goal:** A user only picks their profile once per machine; subsequent launches auto-select the correct profile. Switching profiles remains possible via Settings.

### Tasks

**Machine identity:**
- On first launch, generate a stable machine identifier (e.g. hash of hostname + username, or a GUID written to a local config file in `ApplicationData`) and store it in the `UserProfile` table as a new `MachineId` column (nullable, added via EF migration)
- When a `UserProfile` is selected/created, write the current machine's identifier into that profile's `MachineId` field

**Auto-select on launch:**
- On every subsequent launch, query for a `UserProfile` whose `MachineId` matches the current machine
- If exactly one match is found: auto-select it silently and proceed directly to the Dashboard — no profile picker is shown
- If no match is found (first run on this machine, or the profile was deleted): show the profile picker listing all existing profiles plus a "New Profile" option

**Profile picker UX:**
- List all existing `UserProfile` records with name + machine association shown (e.g. "Alice (this machine)", "Bob (WORKSTATION-2)")
- Allow creating a new profile inline
- On selecting an existing profile from another machine, update its `MachineId` to the current machine (re-binds the profile)

**Settings override:**
- Add a "Change User Profile" entry under the `File` menu (or a dedicated `Settings` view)
- Triggering it shows the profile picker, allowing the user to switch to a different profile or create a new one
- After switching, the new profile's `MachineId` is updated to the current machine

### Deliverable
Single-machine users never see the profile picker after initial setup. Multi-machine teams can easily re-bind profiles. Profile switching is always accessible but never in the way.

---

---

## Phase 18 — Shared Database Location Setup

**Goal:** On first run on any machine, the app asks the user where the SQLite database lives (or where to create it). This single database is shared across all users on the team via a network path; each machine stores only a small local pointer file pointing at it.

### Background & rationale
The SQLite database contains all hardware items, templates, jobs, descriptions, and user profiles — it is the single source of truth for the whole team. Rather than each machine maintaining its own isolated database, the file should live on a shared network location (e.g. a mapped drive, NAS, or UNC path) so all team members see the same data. The per-machine pointer file is stored in `AppData/HardwareTemplateBuilder/db_location.txt` and is never committed to source control or distributed with the installer.

### Tasks

**Local pointer file (`db_location.txt`):**
- Create `DatabaseLocationService` (alongside `MachineIdentityService`) that:
  - Reads `AppData/HardwareTemplateBuilder/db_location.txt` for the database path
  - Returns `null` if the file does not exist or the stored path is blank
  - Writes a validated path to the file when the user completes setup
- Update `DatabaseInitializer.GetDatabasePath()` to call `DatabaseLocationService.GetConfiguredPath()` and fall back to the existing `AppData` default only for legacy / single-user scenarios (i.e., if a database already exists at the old location, use it and write its path to the pointer file automatically so existing installs are not disrupted)

**`DatabaseSetupDialog` (new `Window`):**
- Shown on startup before `DatabaseInitializer.Initialize()` when `db_location.txt` does not exist and no database is found at the legacy default path
- Two options presented to the user:

  | Option | Description |
  |--------|-------------|
  | **Create new database** | Folder picker selects the target folder; the app creates `hardware_templates.db` there and runs all migrations |
  | **Connect to existing database** | File picker (filter: `*.db`) selects an existing database file; the app validates it (attempts to open and query the schema version) before accepting |

- On confirmation, the chosen path is written to `db_location.txt`
- The dialog cannot be dismissed without making a valid selection (same pattern as `ProfilePickerDialog`)
- Display the resolved path in a read-only text field so the user can verify it before confirming
- If validation fails (file not found, schema mismatch, corrupt file), show an inline error and keep the dialog open

**`App.axaml.cs` startup sequence:**
```
1. DatabaseLocationService.GetConfiguredPath() → path?
2. If null AND no database at legacy path → show DatabaseSetupDialog
3. If null AND database EXISTS at legacy path → migrate silently (write legacy path to db_location.txt)
4. DatabaseInitializer.Initialize() (migrate / create schema at the configured path)
5. MachineIdentityService.GetMachineId()
6. Auto-select profile or show ProfilePickerDialog
7. Show MainWindow / Dashboard
```

**Relocate database (Settings):**
- Add a "Database Location…" entry to the `File` menu (or under `Admin`)
- Opens a simplified version of `DatabaseSetupDialog` that only shows the "Connect to existing" option (moving the database file itself is the user's responsibility — the app just re-points to the new location)
- After re-pointing, the app re-runs `DatabaseInitializer.Initialize()` against the new path and refreshes the active session

**Error handling:**
- If `db_location.txt` points to a path that no longer exists (e.g. network share unavailable), show a clear "Cannot reach database" dialog on startup with options: Retry, Choose different location, or Exit
- Never silently fall back to the local `AppData` path if a configured path exists but is unreachable — this would result in the user working against an empty local database and not realising it

### Deliverable
On first run, every team member is guided to connect to (or create) the shared database in one step. Subsequent launches go straight to profile selection with no friction. The database file location is visible and changeable from the File menu.

---

## Phase 19 — Organized Local Template Storage

**Goal:** Template PDF files stored on disk are organized into a folder hierarchy that mirrors the manufacturer and description tree, making the storage folder human-navigable without the app.

### Background & rationale
Currently all downloaded template files land flat in the configured `TemplateStorageLocation`. With hundreds of templates from dozens of manufacturers this becomes unmanageable. Organizing by manufacturer and then by description hierarchy makes it easy for users to find a specific PDF by hand, and also makes the folder structure self-documenting.

### Target folder structure
```
{TemplateStorageLocation}/
  {ManufacturerName}/
    {RootDescription}/
      {ChildDescription}/
        {GrandchildDescription}/
          {TemplateNumber}.pdf
    (templates with no description go directly under {ManufacturerName}/)
```

Example:
```
Templates/
  Allegion/
    Hinges/
      Butt Hinges/
        FBB179 4.5x4.5.pdf
    Closers/
      Surface Mounted/
        LCN 4040XP.pdf
  Von Duprin/
    Exit Devices/
      Rim/
        99 Series.pdf
```

### Tasks

**`DescriptionPathService` (new, Core):**
- `GetFolderPath(int? descriptionId, IReadOnlyList<Description> allDescriptions) → string`
  - Walks the `ParentId` chain from the given description up to the root, collecting names
  - Returns the names joined as a relative path (e.g. `"Hinges/Butt Hinges"`) using `Path.Combine`
  - Returns an empty string if `descriptionId` is null (template goes directly under manufacturer folder)
  - Sanitizes each segment with `SanitizeFolderName()` (replaces characters illegal on Windows/Linux: `\ / : * ? " < > |` with `_`)

**Update `TemplateRefreshService`:**
- After downloading a template PDF, derive its save path as:
  `Path.Combine(saveLocation, manufacturerName, descriptionPath, templateNumber + ".pdf")`
- Create all intermediate directories with `Directory.CreateDirectory`
- Store the resulting absolute path in `IndividualTemplate.LocalLink` (updates existing record if already set)
- Load all `Description` records once before the refresh loop to avoid repeated DB queries

**Update `FileAcquirer`:**
- When looking for a local copy of a template, check `IndividualTemplate.LocalLink` first (already present)
- If `LocalLink` is set and the file exists at that path, use it directly — no download needed
- If `LocalLink` is null or the file is missing, fall back to downloading (existing behaviour)
- After a successful download/copy, derive the organized path using `DescriptionPathService` and save it to `LocalLink`

**Migration:**
- No schema change required — `LocalLink` already exists on `IndividualTemplate`
- Existing `LocalLink` values pointing to old flat paths remain valid; the app uses them as-is until the user triggers a refresh, at which point paths are updated to the new structure

### Deliverable
After a template refresh, all downloaded PDFs are stored in `{Manufacturer}/{Description hierarchy}/` subfolders. `FileAcquirer` finds templates via `LocalLink` without re-downloading. The storage folder is navigable by hand.

---

## Dependency Map

```
Phase 1 (DB Schema)
  └── Phase 2 (Repositories)
        ├── Phase 3 (UI Shell)
        │     └── Phase 4 (Simple CRUD)
        │           └── Phase 5 (HardwareItem + Template CRUD)
        │                 └── Phase 6 (Job + JobHardware)
        └── Phase 7 (PDF Engine)
              └── Phase 8 (Cover Sheet)
                    ├── Phase 9 (Individual Lookup)  ← needs Phase 5 + 6
                    └── Phase 10 (Job Package)       ← needs Phase 5 + 6
                          └── Phase 11 (Refresh)
                                └── Phase 12 (Polish)
                                      ├── Phase 13 (Template-Add Refinements + Door Material)
                                      │     └── Phase 15 (Job-Centric Workflow)
                                      ├── Phase 14 (Job Backup Folder)
                                      │     └── Phase 15 (Job-Centric Workflow)
                                      ├── Phase 16 (Refresh 403 Fix)  ← also needs Phase 11
                                      ├── Phase 17 (Machine-Bound User Profile)
                                      ├── Phase 18 (Shared Database Location)  ← must precede Phase 17
                                      └── Phase 19 (Organized Template Storage) ← needs Phase 11 + 16
```

> Phases 13–18 are independent post-polish features and can be developed in parallel once Phase 12 is complete.
> Phase 18 should be implemented before Phase 17 in practice — database location must be resolved before the profile picker can query it.
> Phase 16 can be targeted earlier (immediately after Phase 11) if the 403 error is blocking active use.
> Phase 19 depends on Phase 11 (refresh service) and benefits from Phase 16 (403 fix) being in place first.

---

## Phase 20 — Job Lifecycle: Active / Complete Status

**Goal:** Jobs have an explicit active/complete state. Generating a PDF package prompts the user to mark the job finished. Completed jobs are hidden from the default job list but remain fully searchable and can be reactivated at any time.

### Schema Changes
- Add `IsComplete bool NOT NULL DEFAULT 0` column to the `Job` table via EF migration.
- No other schema changes.

### Code Changes

**`Job` model:**
- Add `public bool IsComplete { get; set; }` property.

**`JobsView.axaml/.cs`:**
- Default query: `WHERE IsComplete = 0` (active only).
- Add a "Show Completed" checkbox / toggle above the job list; when checked, the query returns all jobs (or completed-only — user preference).
- Completed jobs displayed with a visual indicator (greyed text, or `[COMPLETE]` prefix on the label).

**`JobDetailView.axaml/.cs`:**
- Add a **"Mark Complete"** button to the toolbar (shown only when `IsComplete == false`).
- Add a **"Reactivate Job"** button (shown only when `IsComplete == true`).
- Both buttons toggle the flag and immediately reload the view.
- After `OnGeneratePackageAsync` succeeds: show a `ConfirmAsync` dialog — _"PDF generated. Is this job finished? Mark it as complete?"_ — and set `IsComplete` if the user confirms.

### Deliverable
Jobs move cleanly through active → complete. The job list stays uncluttered. Completed jobs are never lost and can always be reopened.

---

## Phase 21 — Collapsible Linked Hardware Panel

**Goal:** The linked hardware list in `JobDetailView` can be collapsed/expanded with one click, matching the existing collapsible pattern used by the Quick-Create panels.

### Code Changes

**`JobDetailView.axaml`:**
- Wrap the linked hardware `ListBox` and its associated controls (Remove button, custom-desc input, etc.) in a `Border` with `Name="LinkedHardwarePanel"` and `IsVisible="True"`.
- Add a **"▼ Linked Hardware (n)"** toggle button directly above that border; the header updates the count after every add/remove.

**`JobDetailView.axaml.cs`:**
- Wire the toggle button with the same `TogglePanel` helper already used by `NewHardwarePanel`, `NewDescriptionPanel`, and `NewTemplatePanel`.
- Update the toggle button label to reflect current item count and collapsed/expanded state (e.g. `"▼ Linked Hardware (4)"` / `"► Linked Hardware (4)"`).

### Deliverable
Users with long hardware lists can collapse the linked section to reclaim vertical space without losing any functionality.

---

## Phase 22 — Editable and Sortable Job Hardware

**Goal:** Any hardware row already linked to a job can have its Custom Label and Remarks edited in-place. The linked list can be re-sorted by Manufacturer or Description without disrupting the final drag-drop order.

### Code Changes

**`JobDetailView.axaml`:**
- Add **"Sort by Manufacturer"** and **"Sort by Description"** buttons to the linked hardware toolbar.
- Add an **"Edit Selected"** button (or double-click handler) on the linked hardware list.

**`JobDetailView.axaml.cs`:**
- `SortLinkedHardware(field)`: re-orders `_linkedHardware` in-place by `HardwareItem.Manufacturer.ManufacturerName` or `HardwareItem.Description.DescriptionText`; preserves the observable collection so drag-drop still works afterward.
- `EditLinkedHardware()`: opens a small inline edit panel (or a dialog) showing the selected `JobHardware`'s current `CustomDescription` and `HardwareItem.Remarks`; Save writes both fields back to the DB and refreshes the list.
- The edit panel reuses the existing Win98-style `Border` / `TextBox` pattern; no new dependencies.

### Deliverable
Hardware on a job is not permanent once added. Users can correct labels and remarks, and re-sort the list at any time without losing drag-drop control.

---

## Phase 23 — Job Releases (Multiple Revisions)

**Goal:** A single job can have multiple named releases, each with its own hardware list. Releasing a job does not delete or overwrite the base list. Any release can be used to generate its own PDF package.

### Schema Changes
- New table `JobRelease`: `Id (PK), JobId (FK → Jobs, CASCADE), ReleaseNumber (int), ReleaseLabel (string), Notes (string?), CreatedAt (datetime)`.
- Add nullable `ReleaseId (int? FK → JobRelease)` column to `JobHardware`.
  - `NULL` = base/main release (existing rows are unaffected; no data migration needed).
  - Non-null = hardware belonging to a specific named release.
- Unique constraint on `(JobId, ReleaseNumber)`.

### Code Changes

**New models:** `JobRelease`, update `JobHardware` with `ReleaseId` + nav property.

**`JobReleaseRepository`:** standard CRUD + `GetByJob(jobId)`.

**`JobDetailView.axaml/.cs`:**
- Add a **Release selector** ComboBox at the top of the hardware panel (items: `"Base"` + all `JobRelease` records for the job); defaults to `"Base"` (null `ReleaseId`).
- Add a **"+ New Release"** button that opens a dialog for `ReleaseLabel` and `Notes`, with an option to copy hardware from the current selection.
- All add/remove/sort operations apply only to the currently selected release.
- `LoadLinkedHardware()` filters `JobHardware` by the active `ReleaseId` (null = base).
- `OnGeneratePackageAsync()` passes only the hardware rows for the active release to the assembly service.
- The cover sheet title shows the release label (if not base) alongside the job number.

**`AssemblyRequest`:** add `string? ReleaseLabel` for cover sheet use.

### Deliverable
A job can be revised multiple times without losing earlier hardware lists. Each release generates its own independent PDF package, and all versions coexist under the same job number.

---

## Phase 24 — Missing Template Email Alerts

**Goal:** If a job's hardware items still have no linked templates one week after the job was created, the app automatically sends an email notification listing the gaps.

### Schema Changes
- Add `AppSetting` keys (seeded empty, configured via `AppSettingsView`):
  - `SmtpHost`, `SmtpPort` (default `"587"`), `SmtpUsername`, `SmtpPassword`, `SmtpFromAddress`, `NotifyEmailAddress`.
- Add `MissingTemplateNotifiedAt (datetime?)` nullable column to `Job` — set when the notification email is sent; never reset, so the email fires at most once per job.

### Code Changes

**`AppSettingsView.axaml/.cs`:**
- Add a new "Email Notifications" section with labeled text inputs for all six SMTP keys plus a **"Send Test Email"** button.
- Inputs are password-masked for `SmtpPassword`.

**`MissingTemplateAlertService` (new, Core):**
- `GetJobsMissingTemplates(ctx, olderThanDays: 7) → IReadOnlyList<JobAlertInfo>`: queries jobs where `CreatedAt < now - 7d AND MissingTemplateNotifiedAt IS NULL` and have at least one `JobHardware` row whose `HardwareItem` has zero `HardwareItemTemplate` records.
- `SendAlertAsync(jobs, smtpSettings)`: builds a plain-text email listing each job (number, name, items with no templates) and sends it via `System.Net.Mail.SmtpClient`.
- Marks `MissingTemplateNotifiedAt = UtcNow` on each alerted job.

**`App.axaml.cs` / `MainWindow` startup:**
- After profile selection, run `MissingTemplateAlertService.GetJobsMissingTemplates()` in the background.
- If results found AND all SMTP settings are configured: call `SendAlertAsync` silently; update the status bar with a brief "Alert sent for N job(s)" message on success.
- If SMTP is not configured: show an in-app status bar note only — no crash, no blocking dialog.

### Deliverable
Jobs with incomplete hardware setups are never silently forgotten. One week after creation, a single notification email lists every hardware item still missing a template. SMTP is configured once by the admin and runs automatically thereafter.

---

## Phase 25 — Persistent Bulk-Add Session

**Goal:** Work in the Bulk Hardware Entry view survives navigation away. Returning to the bulk entry view for the same job restores the previous manufacturer groups and rows exactly as left.

### Schema Changes
- New table `BulkAddDraft`: `Id (PK), JobId (int, unique), DraftJson (text), SavedAt (datetime)`.
  - One row per job (upsert on save; delete on Finish or explicit Cancel).

### Code Changes

**`BulkAddDraft` model + `BulkAddDraftRepository`:** standard; `GetByJob(jobId)`, `Save(jobId, json)`, `Delete(jobId)`.

**Draft serialization:**
- `BulkAddDraftDto`: a JSON-serializable mirror of the manufacturer-group + row structure (manufacturer ID, description ID, model number, custom label, remarks — no matched-item references, those are re-resolved on restore).
- Use `System.Text.Json` for serialize/deserialize.

**`BulkHardwareEntryView.axaml.cs`:**
- On `Initialize()`: check `BulkAddDraftRepository.GetByJob(_jobId)`.
  - If a draft exists: call `RestoreDraft(draft)`, which re-creates all `MfrGroup` panels and `AddRowToGroup` calls using the saved data, then re-runs `SyncModelState` on each row to recompute match status.
  - Show a subtle `StatusLabel` note: _"Draft restored — {n} row(s) from previous session."_
- On `BackButton.Click` / any navigation away (override `OnDetachedFromVisualTree` or wire `BackButton.Click` before calling `NavigationRequested`): call `SaveDraft()` which serializes current groups/rows to JSON and upserts to `BulkAddDraft`.
- On `OnContinue()` (Finish): call `BulkAddDraftRepository.Delete(_jobId)` after populating `BulkAddSession.PendingRows`.
- Add a **"Clear Draft"** button (small, bottom-left) to discard saved state and start fresh.

### Deliverable
Bulk entry is no longer all-or-nothing. Users can leave mid-entry and return days later to exactly where they left off. The draft is silently saved on every navigation away and deleted on successful completion.

---

## Phase 26 — Jobs as Default Home Page

**Goal:** Opening the app lands directly on the Jobs list. The dashboard remains accessible but is no longer the entry point.

### Code Changes

**`MainWindow.axaml.cs`:**
- Change the startup navigation from `NavigateTo("Dashboard")` → `NavigateTo("Jobs")` in the constructor.

**`JobsView.axaml`:**
- Add a **"⊞ Dashboard"** button to the toolbar (alongside the existing navigation buttons) that calls `NavigationRequested?.Invoke("Dashboard")`.

**`MainWindow.axaml`:**
- No menu changes required — the Dashboard is already reachable via the existing button grid on the DashboardView itself.

### Deliverable
Power users open the app and are immediately in the job list. The dashboard is one button press away for users who prefer the visual navigation grid.

---

## Updated Dependency Map (Phases 20–26)

```
Phase 12 (Polish) — baseline
  ├── Phase 20 (Job Complete Status)    ← needs Job model + JobDetailView
  │     └── Phase 23 (Job Releases)    ← extends job lifecycle concept
  ├── Phase 21 (Collapsible Hardware)  ← UI-only, independent
  ├── Phase 22 (Editable Hardware)     ← UI + minor DB, independent
  ├── Phase 23 (Job Releases)          ← new table, JobHardware FK; Phase 20 recommended first
  ├── Phase 24 (Email Alerts)          ← needs AppSettings (Phase 14/v2); independent otherwise
  ├── Phase 25 (Persistent Bulk-Add)   ← new table; independent of other new phases
  └── Phase 26 (Jobs Home Page)        ← 2-line change; do last (or first — no risk either way)
```

> Phases 21, 22, 25, and 26 are self-contained and can be done in any order or in parallel.
> Phase 20 should precede Phase 23 since the release UI builds on the job status concept.
> Phase 24 depends on the AppSettings infrastructure introduced in v2-changes-plan Phase 14.

---

## Phase 30 — Concurrent Multi-User Database Access

**Goal:** Multiple users on separate machines can open the app against the same shared SQLite database simultaneously without conflicts, deadlocks, or silent data loss.

### Problem

SQLite's default journal mode (`DELETE`) serializes all writes through a single exclusive lock. With multiple concurrent users this produces `SQLITE_BUSY` / `SQLITE_LOCKED` errors that EF Core surfaces as unhandled exceptions. Read operations also block during a write. WAL (Write-Ahead Logging) mode fixes this by allowing concurrent reads alongside one writer and queuing additional writers with a configurable timeout.

### Schema / Connection Changes

**`AppDbContext` (or `DatabaseInitializer.CreateContext()`):**
- Set the SQLite connection string to include `Journal Mode=WAL;Busy Timeout=5000;` (5-second retry window before throwing a lock error).
- After the first `EnsureCreated`/migration run, execute `PRAGMA journal_mode=WAL;` once via raw SQL so existing databases are upgraded automatically. The pragma is idempotent and survives app restarts.

**`DatabaseInitializer.EnsureSchemaPatches()`:**
- Add a one-time patch that issues `PRAGMA journal_mode=WAL;` if the current mode is not already `wal`. Log the result (no-op if already set).

### Code Changes

**`DatabaseInitializer.cs`:**
- In `InitializeAsync()`, after migrations are applied, call the WAL patch unconditionally — it is safe to repeat.

**`RetryHelper` (new, `Core/Data/`):**
- Static helper: `ExecuteWithRetry(Func<T>, maxRetries: 3, delayMs: 200)` — catches `Microsoft.Data.Sqlite.SqliteException` with `SqliteErrorCode.Busy` or `SqliteErrorCode.Locked` and retries with exponential back-off before re-throwing.
- Used by any repository `Add`/`Update`/`Delete` call that writes; reads do not need it under WAL.

**`JobsView.axaml.cs` and other list views:**
- Subscribe to the parent `Window.Activated` event in `Initialize()`.
- On `Activated`, call a lightweight `RefreshIfStale()` method: re-query the DB only if the view has been inactive for ≥ 30 seconds (`_lastRefreshed` timestamp field). This keeps the jobs list current for a user who leaves the app in the background while a colleague adds a job.

### Deliverable
Two app instances pointing at the same `.db` file can run concurrently. Writes queue cleanly under the 5-second busy timeout. List views auto-refresh when the window regains focus after 30 seconds so each user sees the latest data without a manual reload.

---

## Phase 31 — Bulk Add: Manufacturer-Scoped Description Suggestions

**Goal:** In the per-manufacturer hardware entry cards inside `BulkHardwareEntryView`, the Description field becomes an editable combo box that pre-populates with descriptions already used by items belonging to that manufacturer, while still accepting any free-text value.

### Problem

Currently the Description field in each bulk-add row is a plain `TextBox`. Users must remember or retype descriptions they have already used for the same manufacturer, leading to inconsistencies. A filtered suggestion list reduces typos and speeds entry.

### Code Changes

**`BulkHardwareEntryView.axaml`:**
- Replace the `TextBox` used for the description cell in each row template with an Avalonia `AutoCompleteBox`.
- Set `FilterMode="Contains"` and `MinimumPrefixLength="0"` so the full list appears on focus/empty.
- Bind `ItemsSource` to a per-manufacturer description list resolved at row-creation time.
- Keep `IsTextCompletionEnabled="False"` so the user can type a value not in the list without it being rejected.

**`BulkHardwareEntryView.axaml.cs`:**
- Add a private method `GetDescriptionsForManufacturer(int manufacturerId) → IList<string>`: queries `HardwareItemRepository.GetAll()` filtering by `ManufacturerId`, projects `Description.DescriptionText`, deduplicates, and sorts alphabetically.
- Call this method when a manufacturer card is created and pass the result as the `ItemsSource` for every `AutoCompleteBox` in that card.
- The existing `SyncModelState` row logic is unchanged — it already reads the `AutoCompleteBox.Text` property the same way it read `TextBox.Text`.

**Draft restore (`BulkHardwareEntryView.cs` — `RestoreDraft`):**
- After recreating each row, re-set `AutoCompleteBox.Text` from the saved draft value. The list will populate from the manufacturer query as usual; the saved text just pre-fills the field.

### Deliverable
When entering hardware for a manufacturer, users see a dropdown of description strings already associated with that manufacturer's items. Selecting one fills the field; typing anything else is accepted as-is. Bulk entry of repeat hardware is faster and more consistent.

---

## Phase 32 — Page Numbering Fix for Non-Standard PDFs

**Goal:** Page number stamps that silently fail or are hidden on certain PDFs (e.g., those with full-bleed images, unusual content stream structures, or non-standard page boxes) are applied reliably and visibly across all templates in a package.

### Problem

`PageNumberer` draws a stamp via `PdfSharp.Drawing.XGraphics` and appends it to the page's content stream. For PDFs where existing content streams paint over the full page (common in scanned or fully-imaged spec sheets like those from AA Americas), the stamp is drawn behind the raster content and is invisible. Additionally, if a page defines a `CropBox` smaller than its `MediaBox`, coordinates calculated from `MediaBox` dimensions may place the stamp outside the visible area.

### Code Changes

**`PageNumberer.cs` (Core/Services/Pdf/):**
- When creating the `XGraphics` context for each page, call `page.MediaBox` **and** check for a `CropBox`; use the `CropBox` dimensions when present as the authoritative visible rectangle.
- Draw a small filled white rectangle (e.g., 36 × 14 pt) immediately before the page number text so the stamp has an opaque background regardless of what is beneath it.
- Change the content stream insertion strategy from the default (which may prepend or append depending on PdfSharp version) to explicitly **append** by accessing `page.Contents.Append()` (a new `PdfContentStream`) so the stamp is the last thing drawn and therefore on top of all existing content.
- Wrap the per-page stamping block in a `try/catch (Exception ex)` that logs the page index and continues rather than aborting the entire numbering pass — a single unreadable page should not prevent the rest of the package from getting numbers.

**`PdfAssemblyService.cs`:**
- After calling `PageNumberer`, inspect its result for any skipped pages and include a count in the assembly summary (e.g., append `"(N page(s) could not be numbered)"` to the output message if non-zero).

**Tests (`HardwareTemplateBuilder.Tests/`):**
- Add a test fixture that creates a minimal PDF whose single page has a full-page `XObject` image as its only content stream entry, then asserts the stamp text is present in the merged output.
- Add a test verifying the stamper does not throw when given a PDF with a `CropBox` smaller than `MediaBox`.

### Deliverable
Page numbers appear on top of content for all standard and fully-imaged PDFs in the package. The rare PDF that cannot be stamped is skipped gracefully and reported in the assembly summary rather than silently missing its number.

---

## Phase 33 — Copy Existing Job on Creation

**Goal:** When creating a new job, the user may optionally clone an existing job so the new job starts with the same base hardware list. Hardware items can then be added, removed, or edited before the job is used.

### Code Changes

**`JobCreationView.axaml`:**
- Add a **"Copy hardware from existing job"** `CheckBox` below the standard new-job fields.
- When checked, show a `ComboBox` (or searchable list) populated with all existing jobs (`JobNumber — JobName`), sorted by most recent first.
- The combo is hidden and its value cleared when the checkbox is unchecked.

**`JobCreationView.axaml.cs`:**
- On `SaveButton.Click`, after inserting the new `Job` record, check if the copy checkbox is checked and a source job is selected.
- If so, call `CopyHardwareFromJob(sourceJobId, newJobId)`:
  - Load all `JobHardware` rows for the source job where `ReleaseId IS NULL` (base release only).
  - Insert a duplicate row for each into the new job with the same `HardwareItemId`, `CustomDescription`, `SortOrder`, and `Remarks`; set `ReleaseId = null` and `JobId = newJobId`.
  - Use `DatabaseInitializer.CreateContext()` per the standard context pattern; no new repository needed — use `JobHardwareRepository` directly.
- Navigate to `"JobDetail:{newJobId}"` as usual after saving.

**`JobDetailView.axaml.cs`:**
- No changes required — the copied `JobHardware` rows are loaded by the existing `LoadLinkedHardware()` query.

### Deliverable
Creating a follow-on job for a repeat customer or similar project no longer requires re-entering every hardware item. The copied job is fully independent — changes to one do not affect the other.

---

## Phase 34 — Job Creator Tracking and Sort

**Goal:** Each job records which user profile created it. The Jobs list can be sorted by creator, making it easy to see one's own jobs or audit another user's workload in a shared-database environment.

### Schema Changes

- Add nullable column `CreatedByProfileId (int? FK → UserProfile, SetNull on delete)` to `Job`.
- EF Core migration; existing rows get `NULL` (no data migration needed — unknown creator is acceptable for historical records).

### Code Changes

**`Job.cs` (model):**
- Add `CreatedByProfileId (int?)` and navigation property `CreatedBy (UserProfile?)`.

**`AppDbContext.cs`:**
- Configure the FK with `OnDelete(DeleteBehavior.SetNull)`.

**`JobRepository.cs` (or `JobCreationView.axaml.cs`):**
- Wherever a new `Job` is inserted, set `CreatedByProfileId = SessionService.ActiveUserProfile?.Id` before calling `Add()`.

**`JobsView.axaml`:**
- Add a **"Sort by Creator"** button to the toolbar alongside the existing sort options.
- Optionally add a **"Creator"** filter `ComboBox` populated with all distinct `CreatedBy` profile names from the current job list (plus an `"(Any)"` entry); filters the displayed rows client-side.

**`JobsView.axaml.cs`:**
- `SortByCreator()`: re-orders `_jobs` by `CreatedBy?.UserName ?? ""` ascending.
- `FilterByCreator(profileId)`: filters the display list to jobs where `CreatedByProfileId == profileId` (or shows all when `"(Any)"` is selected).
- Eager-load `CreatedBy` in the initial `JobRepository.GetAll()` query using `.Include(j => j.CreatedBy)`.

### Deliverable
Every new job is stamped with the creating user's profile. The Jobs list can be sorted or filtered by creator, which is especially useful in the shared-database multi-user environment introduced in Phase 30.

---

## Dependency Map (Phases 30–34)

```
Phase 26 (Jobs Home Page) — baseline for this group
  ├── Phase 30 (Multi-User DB Access)    ← foundational; recommended before Phase 34
  ├── Phase 31 (Description Combobox)   ← UI-only within BulkHardwareEntryView; independent
  ├── Phase 32 (Page Numbering Fix)      ← service-only; independent
  ├── Phase 33 (Copy Job on Creation)   ← extends JobCreationView; independent
  └── Phase 34 (Job Creator Tracking)   ← Phase 30 recommended first (shared DB gives it value)
```

> Phases 31, 32, and 33 are fully self-contained and can be done in any order.
> Phase 30 should precede Phase 34 — creator tracking is most useful once multiple users share the same database.
> Phase 32 has no schema changes and does not affect any other phase.
> Phase 23 is the most complex of the group — plan a dedicated session for schema migration + UI work.

---

## Phase 35 — Job-Specific Templates and Quick-Create Removal

**Goal:** The template-staging step in the bulk add wizard gains a "Job-specific" checkbox. When checked, the template is hidden from every other job's template search and is linked only to the current job's PDF assembly. Simultaneously, the unused Quick Create panel (inline hardware/description/template creation in `JobDetailView`) is removed to reduce UI surface area.

---

### Background and design rationale

Currently every `IndividualTemplate` and every `HardwareItemTemplate` link is global — a template staged for one job becomes visible and linked to that hardware item for all future jobs. The new behaviour introduces two scoping controls:

| Column | Table | Meaning |
|---|---|---|
| `OriginJobId INT? FK→Jobs` | `IndividualTemplate` | Which job originally created this template. `NULL` = globally visible. Non-null = hidden from all other jobs' searches. |
| `JobId INT? FK→Jobs` | `HardwareItemTemplate` | Which job this hardware-template link belongs to. `NULL` = applies to all jobs. Non-null = included in PDF assembly for that job only. |

**Deduplication behaviour:** `IndividualTemplateRepository.FindDuplicate` already matches on (TemplateNumber, ManufacturerId, DescriptionId, DoorMaterialId). If an existing record is found — whether global or job-specific for a different job — it is reused as-is. Only the `HardwareItemTemplate` link that is created in `OnFinish()` is scoped to the current job (via `JobId`). The `OriginJobId` on the returned duplicate template is never mutated.

**Carrying the job-specific intent through staging:** Because `FindDuplicate` may return a template whose `OriginJobId` belongs to a different job, the "is this template job-specific?" intent must live on the pending row, not solely on the template record. `BulkHardwareRow.PendingTemplates` changes from `List<IndividualTemplate>` to `List<BulkPendingTemplate>` (a new lightweight wrapper defined in `BulkAddSession.cs`).

---

### Schema changes

**`IndividualTemplate` — add column `OriginJobId`:**
- Nullable `int` FK referencing `Jobs.Id`, `ON DELETE SET NULL`.
- `NULL` = globally available template; non-null = job-scoped.

**`HardwareItemTemplate` — add column `JobId`:**
- Nullable `int` FK referencing `Jobs.Id`, `ON DELETE SET NULL`.
- `NULL` = global hardware-template link; non-null = applies to one job only.

Apply both changes as a single EF Core migration (`AddJobScopedTemplates`). Add idempotent `EnsureSchemaPatches()` blocks for both columns so existing databases are patched on first launch.

---

### Model and session changes

**`BulkPendingTemplate` (new, `BulkAddSession.cs`):**
```csharp
public class BulkPendingTemplate
{
    public IndividualTemplate Template { get; set; } = null!;
    /// <summary>When true, the HardwareItemTemplate link is scoped to the current job.</summary>
    public bool IsJobSpecific { get; set; }
}
```

**`BulkHardwareRow`:**
- Change `PendingTemplates` from `List<IndividualTemplate>` to `List<BulkPendingTemplate>`.
- Update all callers (`AddPendingTemplate`, `RemovePendingTemplate`, `RefreshPendingList`, `StageSearchResult`, `OnFinish`) to use the wrapper.

**`IndividualTemplate.cs`:**
- Add `public int? OriginJobId { get; set; }` and navigation property `public Job? OriginJob { get; set; }`.

**`HardwareItemTemplate.cs`:**
- Add `public int? JobId { get; set; }` and navigation property `public Job? Job { get; set; }`.

**`AppDbContext.cs`:**
- Configure both new FKs with `OnDelete(DeleteBehavior.SetNull)`.

---

### Repository changes

**`HardwareItemTemplateRepository`:**
- Add `GetByHardwareItem(int hardwareItemId, int? jobId)` overload: returns templates where `JobId IS NULL OR JobId = jobId`.
- The existing parameterless `GetByHardwareItemId` (used by non-job contexts) is unchanged.

**`IndividualTemplateRepository`:**
- `FindDuplicate`: no change — key match already ignores `OriginJobId`.
- Add `GetVisibleForJob(int jobId)`: returns templates where `OriginJobId IS NULL OR OriginJobId = jobId`. Used by the wizard's search panel.

---

### Wizard changes (`TemplateResolutionWizardView.axaml/.cs`)

**AXAML:**
- Add a `CheckBox` labelled "Job-specific (hide from other jobs)" to the add-template form, placed below the template number field. Name it `JobSpecificCheck`.

**`AddPendingTemplate()`:**
- After `templateRepo.Add(candidate)`, construct a `BulkPendingTemplate { Template = saved, IsJobSpecific = JobSpecificCheck.IsChecked == true }`.
- If `IsJobSpecific` and `saved.OriginJobId == null` (i.e. the template was newly created, not a dedup hit against an existing global template): set `saved.OriginJobId = _jobId` and call `ctx.SaveChanges()` to persist the origin marker.
- Append the `BulkPendingTemplate` to `row.PendingTemplates`.

**`RefreshPendingList(BulkHardwareRow row)`:**
- Bind `row.PendingTemplates` as before; update `DisplayMemberBinding` to show `Template.TemplateNumber` (via `BulkPendingTemplate.Template`).
- Optionally suffix job-specific entries with `" [job]"` in the display.

**`RemovePendingTemplate()`:**
- Unwrap `PendingTemplatesList.SelectedItem as BulkPendingTemplate` instead of `IndividualTemplate`.

**`StageSearchResult()` / `RunTemplateSearch()`:**
- Filter the template search query: `WHERE OriginJobId IS NULL OR OriginJobId = _jobId`.
- Use `IndividualTemplateRepository.GetVisibleForJob(_jobId)` as the source.

**`OnFinish()` — Case B template linking:**
- For each `BulkPendingTemplate pending` in `row.PendingTemplates`:
  ```csharp
  hitRepo.Add(new HardwareItemTemplate
  {
      HardwareItemId       = hwItemId,
      IndividualTemplateId = pending.Template.Id,
      JobId                = pending.IsJobSpecific ? _jobId : (int?)null
  });
  ```

---

### PDF assembly changes (`JobDetailView.axaml.cs`)

When building the `AssemblyRequest.Hardware` list, replace the current `ctx.HardwareItemTemplates.Where(hit => hit.HardwareItemId == item.Id)` query with a job-aware query:
```csharp
.Where(hit => hit.HardwareItemId == item.Id
           && (hit.JobId == null || hit.JobId == _jobId))
```
This ensures job-specific templates from other jobs are excluded while global and current-job-specific templates are both included.

---

### Quick Create removal (`JobDetailView.axaml/.cs`)

**`JobDetailView.axaml` — remove entirely:**
- The `ToggleQuickCreateButton` / `QuickCreateBody` collapsible section header
- `NewHardwarePanel` (manufacturer + description + model + notes form)
- `NewDescriptionPanel` (parent description + name form)
- `NewTemplatePanel` (hardware link + number + description + material + links form)

**`JobDetailView.axaml.cs` — remove entirely:**
- `WireToggle(ToggleQuickCreateButton, QuickCreateBody)` call in `Initialize()`
- `ToggleNewHardwareButton.Click`, `ToggleNewDescriptionButton.Click`, `ToggleNewTemplateButton.Click` handlers
- `CreateHardwareButton.Click`, `CreateDescriptionButton.Click`, `CreateTemplateButton.Click` handlers
- `InitNewHardwarePanel()`, `InitNewDescriptionPanel()`, `InitNewTemplatePanel()`, `RefreshNewTemplateHardwareCombo()` methods
- `CreateHardwareItem()`, `CreateDescription()`, `CreateTemplateAsync()` methods

The "Linked Hardware Items" search-and-add section (searching existing items by manufacturer/description/model and adding them to the job) is **not** removed — it is separate from Quick Create and remains in use.

---

### Deliverable

- The bulk add wizard shows a "Job-specific" checkbox when staging a new template. Checked templates are stored with `OriginJobId` set and linked with a scoped `HardwareItemTemplate.JobId`; unchecked templates behave exactly as before.
- PDF assembly for a job includes both global templates and job-specific templates for that job, and excludes job-specific templates belonging to other jobs.
- Template search in the wizard hides templates originating from other jobs.
- The Quick Create panel is gone from `JobDetailView`, eliminating dead UI surface.

---

## Dependency Map (Phases 30–35)

```
Phase 26 (Jobs Home Page) — baseline for this group
  ├── Phase 30 (Multi-User DB Access)    ← foundational; recommended before Phase 34
  ├── Phase 31 (Description Combobox)   ← UI-only within BulkHardwareEntryView; independent
  ├── Phase 32 (Page Numbering Fix)      ← service-only; independent
  ├── Phase 33 (Copy Job on Creation)   ← extends JobCreationView; independent
  ├── Phase 34 (Job Creator Tracking)   ← Phase 30 recommended first (shared DB gives it value)
  └── Phase 35 (Job-Specific Templates) ← extends Phases 25–26 bulk flow; independent of 30–34
```

> Phase 35 touches the bulk add wizard (Phase 25), the PDF assembly pipeline (Phase 8/10), and JobDetailView.
> The Quick Create removal in Phase 35 is purely subtractive — no risk of regression to the linked-hardware search section.
> Phase 35 has no dependency on Phases 30–34 and can be developed independently.
