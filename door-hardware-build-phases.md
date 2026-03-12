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
```

> Phases 3–6 (UI) and Phases 7–8 (PDF engine) can be developed in parallel once Phase 2 is complete.
