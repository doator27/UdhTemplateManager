# Hardware Template Builder — V2 Changes Plan

Continues from Phase 12. Each phase is self-contained and leaves the app in a buildable, runnable state. Phases are ordered by dependency — later phases build on earlier ones.

---

## Phase 13 — Weight Schema Consolidation

**Goal:** Weight is no longer a separate selectable entity. It is set once on a Description and then works silently in the background forever. Users never interact with it again after that.

**Motivation:** Items 8 — weights should be tied to the hardware type (Description), assigned at description creation time, invisible thereafter.

### Schema Changes
- Add `WeightValue` string column (`"00.000.000"` format) directly to the `Description` table
- Remove `WeightId` FK column from `IndividualTemplate`
- The `Weight` table and `Weight` entity are removed entirely
- EF migration: before dropping, copy each `IndividualTemplate.Weight.WeightValue` → its `Description.WeightValue` (take the first/lowest weight per description if multiple exist)

### Code Changes
- **`Description` model** — add `WeightValue` string property with `[Required]` and format validation
- **`IndividualTemplate` model** — remove `WeightId`, `Weight` nav property, and `Weights` collection on `Description`
- **`Weight` model, `WeightRepository`, `WeightsView.axaml/.cs`** — deleted
- **`DescriptionsView`** — add `WeightBox` text field with `00.000.000` format hint; validate on save
- **`IndividualTemplatesView`** — remove Weight combobox; weight is now inherited silently from the selected Description
- **`WeightTemplateSortStrategy`** — change sort key from `template.Weight.WeightValue` to `template.Description.WeightValue`; update all `.Include().ThenInclude(Weight)` chains to `.Include(Description)` instead
- **`MainWindow.axaml/.cs`** — remove `_Weights` menu item and `Register("Weights", ...)` route
- **`WeightParser` tests** — update to remove any Weight-entity references (parser itself is unchanged)
- **`AppDbContext`** — remove `DbSet<Weight>`, remove Weight configuration block

### Deliverable
Descriptions have a single weight value set at creation. Templates sort correctly without any Weight screen or Weight selection in the template form.

---

## Phase 14 — App Settings & Shared Template Storage

**Goal:** A global key-value settings table powers a shared template file location. All template downloads go to one shared folder; individual job PDFs still go to each user's own folder.

**Motivation:** Item 2 — everyone should have the same master file of templates.

### Schema Changes
- Add `AppSettings` table: `{ Id, Key (unique), Value }`

### Code Changes
- **`AppSetting` model** and **`AppSettingRepository`** — new, in Core
- **`AppDbContext`** — add `DbSet<AppSetting>`; seed one row: `Key = "TemplateStorageLocation", Value = ""`
- **`AppSettingsView.axaml/.cs`** — new Admin view with a single labeled folder-picker field for Template Storage Location; Save button writes to `AppSettings`
- **`MainWindow.axaml/.cs`** — add `_App Settings` item under Admin menu; register route
- **`TemplateRefreshService`** — accept `templateStorageLocation` as a parameter (instead of reading from user profile); callers pass the value from `AppSettings`
- **`RefreshTemplatesView.axaml.cs`** — read `AppSettings["TemplateStorageLocation"]` on start; fall back to `MyDocuments` if empty; pass it to `TemplateRefreshService`
- **`FileAcquirer`** — no change needed; it already takes a folder path as parameter

### Deliverable
Admin sets one shared folder. All downloaded template files land there. Job packages still save to the user's own folder.

---

## Phase 15 — Startup Profile Selection & Session

**Goal:** Every session is tied to a named user. On launch, a profile picker is shown before the main window is usable. All jobs created in the session automatically record the active user.

**Motivation:** Item 1 — all changes and jobs tracked back to who did them.

### Code Changes
- **`SessionService`** — new singleton in Core (or App): holds `ActiveUserProfile` for the lifetime of the process
- **`ProfilePickerDialog`** — new Avalonia `Window`:
  - Lists all `UserProfile` records in a listbox
  - "Select" button sets `SessionService.ActiveUserProfile` and closes
  - "New Profile" button shows a compact create-profile form inline (name + save location folder picker)
  - Cannot be dismissed without selecting or creating a profile (no close button, `CanClose` override)
- **`App.axaml.cs`** — on startup, show `ProfilePickerDialog` before (or immediately after) creating `MainWindow`; block main window display until a profile is chosen
- **`JobsView` (later replaced in Phase 18)** — remove `UserProfile` combobox from the job form; on Save, use `SessionService.ActiveUserProfile.Id` instead
- **`MainWindow`** — display the active user's name in the status bar ("Ready — logged in as: John")
- **`NavigateToFirstLaunch()`** in `MainWindow` — remove; the picker dialog handles the zero-profiles case itself

### Deliverable
App always knows who is working. Jobs are attributed automatically. No one can use the app without identifying themselves.

---

## Phase 16 — Dashboard Navigation & UI Accessibility

**Goal:** All major areas are reachable from a visual dashboard, not just the top menu bar. Individual Template Lookup is prominently accessible.

**Motivation:** Items 3 and 4.

### Code Changes
- **`DashboardView.axaml/.cs`** — replace the current placeholder with a grid of large labeled navigation buttons, one per major area:
  - Row 1: `Jobs`, `Template Lookup`
  - Row 2: `Hardware Items`, `Templates`
  - Row 3: `Customers`, `Manufacturers`, `Descriptions`, `Project Managers`
  - Row 4: `User Profiles`, `Door Materials`, `App Settings`, `Refresh Templates`
  - Each button fires `NavigationRequested` (same event the dashboard already exposes to `MainWindow`)
- **`MainWindow.axaml`** — add `_Template Lookup` as a top-level menu item in the menu bar (between Jobs and Maintenance) so it is one click away from anywhere

### Deliverable
Full app is navigable from the dashboard. Template Lookup is a first-class menu item.

---

## Phase 17 — Door Material "Any" Filter

**Goal:** Door Material combos in search/filter contexts include an "(Any)" option, consistent with how Manufacturer and Description filters already work.

**Motivation:** Item 5.

### Code Changes
- **`IndividualTemplatesView.axaml.cs`** — wherever the DoorMaterial combobox is used as a search filter, prepend a sentinel `DoorMaterial { Id = 0, MaterialName = "(Any)" }`; treat `Id == 0` as no filter (already the pattern used for Manufacturer/Description)
- **`HardwareItemsView.axaml.cs`** — same treatment if DoorMaterial filtering exists there
- No model or schema changes

### Deliverable
Door Material filter works the same way as every other filter combo in the app.

---

## Phase 18 — Page Number Styling

**Goal:** Page numbers are large, bold, red, and in the bottom-right corner of each page.

**Motivation:** Item 7.

### Code Changes
- **`PageNumberer.cs`** — update `StampPageNumbers`:
  - Font size: `25`, style: `Bold`
  - Color: `XBrushes.Red` (PDFsharp built-in)
  - Position: bottom-right — `x = page.Width - rightMargin - textWidth`, `y = page.Height - bottomMargin`
  - `ResolveFont()` updated to request `Bold` style via `XFontStyleEx.Bold`
- **`PdfServiceTests.cs`** — existing page-count tests are unaffected; no new tests needed (visual output only)

### Deliverable
All generated PDFs have large red bold page numbers in the bottom-right corner.

---

## Phase 19 — Two-Step Job Flow

**Goal:** Job creation and hardware management are split into two separate views. Step 1 creates the job record. Step 2 opens the job and manages its hardware and PDF generation.

**Motivation:** Item 10.

### Code Changes
- **`JobListView.axaml/.cs`** — new view (replaces the current `JobsView` list-side):
  - Left panel: filter textbox + job listbox (same as current)
  - Right panel: job metadata form (JobNumber, JobName, Customer, ProjectManager — no UserProfile combobox; filled from `SessionService`)
  - Save, New, Delete buttons
  - **"Open Job" button** — navigates to `JobDetailView`, passing the selected job ID via a constructor parameter or a navigation parameter
- **`JobDetailView.axaml/.cs`** — new view (replaces the current `JobsView` right-side hardware panel):
  - Header: displays Job Number and Job Name (read-only)
  - **"← Back to Jobs"** button at the top left — navigates back to `JobListView`
  - Hardware search panel (Manufacturer, Description, Model combos + results list)
  - Linked hardware list with drag-and-drop reorder (same as current)
  - Add / Remove hardware buttons
  - Generate Package button + progress bar
  - Custom hardware section (Phase 20)
- **`ViewRouter`** — support passing a parameter to a registered view factory (or use a simple lambda capture when registering `JobDetailView`)
- **`MainWindow`** — update registration: register both `JobList` and `JobDetail` routes; `Jobs` menu item navigates to `JobList`
- **Old `JobsView`** — deleted

### Deliverable
Clean two-screen job workflow. Job metadata and hardware management are clearly separated.

---

## Phase 20 — Custom Hardware Descriptions on Jobs

**Goal:** When adding hardware to a job, the user can enter a free-text custom label for that line item. The same hardware item can be added multiple times under different custom labels. Templates are only included once in the PDF body — all custom entries pointing to the same hardware item share the same page numbers on the cover sheet.

**Motivation:** Item 6.

### Schema Changes
- Add `CustomDescription` string column (nullable) to `JobHardware` junction table
- `JobHardware` now allows duplicate `HardwareItemId` for the same `JobId` as long as `CustomDescription` differs (unique constraint updated)

### Code Changes
- **`JobHardware` model** — add `CustomDescription` string? property
- **`JobHardwareRepository`** — update duplicate check to allow same item with different `CustomDescription`
- **`JobDetailView`** — add a `CustomDescriptionBox` text input above the "Add to Job" button:
  - If left blank, the hardware item's own `ModelNumber` is used as the display label (current behavior)
  - If filled, that text becomes the cover sheet label for this instance
  - The same hardware item can be added again with a different custom label
- **`PdfAssemblyService.AssembleAsync`** — template deduplication:
  - Collect all unique template IDs across all `HardwareWithTemplates` entries
  - Each unique template is acquired and processed **once**
  - `itemBodyPages` maps each `JobHardware` row (by its position index, not just hardware item ID) to the page range of its hardware item's templates
  - Cover sheet rows use `CustomDescription ?? item.ModelNumber` as the Hardware Description column
- **`AssemblyRequest` / `HardwareWithTemplates`** — add `CustomLabel` string? property to `HardwareWithTemplates` so the assembly service knows what label to use per row

### Deliverable
Users can add "Door 101A" and "Door 101B" as separate cover sheet rows using the same hardware item. The template pages appear once in the PDF body; both rows cite the same page numbers.

---

## Phase 21 — Auto-Refresh on Startup & Per-Template Refresh

**Goal:** Templates stay current automatically. The refresh runs silently on startup if it hasn't run in a week. Adding a new template with an online link triggers an immediate download for that one template.

**Motivation:** Item 9.

### Schema / Settings Changes
- Add `AppSetting` key `"LastRefreshTimestamp"` (ISO-8601 UTC string); empty = never refreshed

### Code Changes
- **`App.axaml.cs`** (or `MainWindow` constructor, after profile is selected):
  - Read `AppSettings["LastRefreshTimestamp"]`
  - If empty or more than 7 days ago: fire `TemplateRefreshService.RefreshAsync` in the background (non-blocking); do not show the Refresh Templates view — just a subtle status bar message "Refreshing templates in background..."
  - On completion: update `LastRefreshTimestamp`; show a brief status bar note if any failures occurred
- **`RefreshTemplatesView.axaml.cs`** — on manual "Start Refresh": update `LastRefreshTimestamp` on success
- **`IndividualTemplatesView.axaml.cs`** — after saving a **new** template (not an update):
  - If `OnlineLink` is non-empty, immediately call `FileAcquirer.AcquireAsync` for that single template in the background
  - Update `LocalLink` on success; show a brief inline status ("Downloaded template file")
  - Failure is non-fatal — show a warning but do not block the save

### Deliverable
Templates are always fresh without manual intervention. New templates self-download. The 7-day check runs silently and doesn't interrupt the user.

---

## Dependency Map

```
Phase 13 (Weight → Description)          ← do first; touches many views
  └── unblocks all template-related phases

Phase 14 (App Settings + Shared Storage)
  └── Phase 21 (Auto-Refresh timing)

Phase 15 (Session / Profile Picker)
  └── Phase 19 (Two-Step Job Flow)       ← job form uses session user
        └── Phase 20 (Custom Hardware)

Phase 16 (Dashboard Navigation)          ← independent
Phase 17 (Door Material Any)             ← independent
Phase 18 (Page Number Styling)           ← independent

Phase 21 (Auto-Refresh)
  └── requires Phase 14 (AppSettings) + Phase 15 (startup hook)
```

### Recommended execution order
13 → 14 → 15 → 16 → 17 → 18 → 19 → 20 → 21

Phases 16, 17, and 18 are small enough to be done in any order or combined into a single session.
