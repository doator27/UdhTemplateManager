# Door Hardware Template Builder — Project Prompt

## Overview

Build a cross-platform desktop application in **C# / .NET 8** that runs on both **Linux** (development) and **Windows** (production). The application manages a database of door hardware items and their associated installation templates, and assembles those templates into professional PDF packages for construction jobs.

The UI framework should be **Avalonia UI** to support both Linux and Windows. The visual style should closely mimic the **Windows 98 / classic Win32 aesthetic**: gray (#C0C0C0) backgrounds, embossed buttons, Tahoma or Microsoft Sans Serif fonts, and classic window chrome with title bars. Navigation should include both a persistent top menu bar (with dropdowns for File, Jobs, Maintenance, Admin, etc.) and a main dashboard with buttons linking to sub-menus and key functions.

---

## 1. Database Design

Use **SQLite** via Entity Framework Core. Create the database on first run if it does not already exist. Enforce redundancy checks before inserting any new record — only add if the record does not already exist.

---

### Tables

#### `UserProfile`
| Column | Type | Notes |
|--------|------|-------|
| Id | INT PK | |
| UserName | TEXT | |
| DefaultTemplateSaveLocation | TEXT | Path where downloaded templates are saved |

Jobs and other user-created records should be linked to the creating `UserProfile`.

---

#### `Customer`
| Column | Type |
|--------|------|
| Id | INT PK |
| CustomerName | TEXT |

---

#### `Manufacturer`
| Column | Type |
|--------|------|
| Id | INT PK |
| ManufacturerName | TEXT |

---

#### `Description`
| Column | Type |
|--------|------|
| Id | INT PK |
| Description | TEXT |

---

#### `DoorMaterial`
| Column | Type | Notes |
|--------|------|-------|
| Id | INT PK | |
| Material | TEXT | Constrained to: `"Hollow Metal"` or `"Wood"` only |

---

#### `Weight`
| Column | Type | Notes |
|--------|------|-------|
| Id | INT PK | |
| Weight | TEXT | Format: `00.000.000` — see Weight Format section below |
| DescriptionId | INT FK → Description | |

**Weight Format:**
The weight string uses a three-part dot-delimited format: `{Function}.{Type}.{SubType}`

- First segment (Function): e.g. `01` = Hangs, `02` = Locks, `03` = Misc, `04` = Protects, `05` = Controls
- Second segment (Type): e.g. `001` = Mortise Lockset
- Third segment (SubType): e.g. `020` = specific function within that type

This format is used to sort templates within a PDF package.

---

#### `ProjectManager`
| Column | Type |
|--------|------|
| Id | INT PK |
| ProjectManager | TEXT |

---

#### `HardwareItem`
| Column | Type | Notes |
|--------|------|-------|
| Id | INT PK | |
| ManufacturerId | INT FK → Manufacturer | |
| DescriptionId | INT FK → Description | |
| ModelNumber | TEXT | |
| Remarks | TEXT | Optional |
| Frequency | INT | Incremented each time this item is referenced in any lookup or job; used to weight search results |

---

#### `IndividualTemplate`
| Column | Type | Notes |
|--------|------|-------|
| Id | INT PK | |
| ManufacturerId | INT FK → Manufacturer | |
| DescriptionId | INT FK → Description | |
| TemplateNumber | TEXT | |
| NumPages | INT | Total page count of source PDF |
| PagesToPrint | TEXT | Pages to include. Supports: single (`1`), range (`2-7`), non-consecutive (`3,6,8`), or combinations (`1,3-5,8`) |
| PagesToRotate | TEXT | Same format as PagesToPrint. Pages that need rotation. Null if none. |
| RotationDirection | INT | Degrees of rotation. Positive = clockwise, Negative = counterclockwise. Applied to all pages in PagesToRotate. |
| WeightId | INT FK → Weight | Used for sorting |
| DoorMaterialId | INT FK → DoorMaterial | |
| OnlineLink | TEXT | URL to source file |
| LocalLink | TEXT | Path to locally cached file |

---

#### `HardwareItemTemplates` *(Junction Table)*
| Column | Type | Notes |
|--------|------|-------|
| Id | INT PK | |
| HardwareItemId | INT FK → HardwareItem | |
| IndividualTemplateId | INT FK → IndividualTemplate | |

---

#### `Job`
| Column | Type | Notes |
|--------|------|-------|
| Id | INT PK | |
| JobNumber | TEXT | |
| JobName | TEXT | |
| CustomerId | INT FK → Customer | |
| ProjectManagerId | INT FK → ProjectManager | |
| UserProfileId | INT FK → UserProfile | |

---

#### `JobHardware` *(Junction Table)*
| Column | Type | Notes |
|--------|------|-------|
| Id | INT PK | |
| JobId | INT FK → Job | |
| HardwareItemId | INT FK → HardwareItem | |

---

#### `JobTemplateSnapshot`
When a PDF package is generated for a job, a snapshot is taken of each template at that moment in time. This ensures that future references to the job reflect the templates as they existed at the time of generation, even if the master `IndividualTemplate` record is later updated.

| Column | Type | Notes |
|--------|------|-------|
| Id | INT PK | |
| JobId | INT FK → Job | |
| IndividualTemplateId | INT FK → IndividualTemplate | Original template reference |
| SnapshotLocalLink | TEXT | Path to the file as used for this job's package |
| SnapshotDate | DATETIME | When the snapshot was created |
| PagesToPrint | TEXT | Copied from IndividualTemplate at time of snapshot |
| PagesToRotate | TEXT | Copied from IndividualTemplate at time of snapshot |
| RotationDirection | INT | Copied from IndividualTemplate at time of snapshot |

---

## 2. Application Features

### 2a. Individual Template Lookup

- Search by Manufacturer, Description, and Model Number using comboboxes populated from the database.
- Show matching HardwareItems in a listbox. If no item is selected, default to the first closest match.
- Search results should be sorted by `Frequency` descending (most commonly used first).
- On confirmation, gather all linked `IndividualTemplate` records via `HardwareItemTemplates`, sorted by `Weight` ascending.
- Generate and return a single merged PDF with all relevant templates.
- Increment the `Frequency` field on the `HardwareItem` record each time it is retrieved.

---

### 2b. Full Job Template Package

**Job Creation:**
- Create a new job with: Job Number, Job Name, Customer, Project Manager, and User Profile.
- Add hardware items to the job by searching Manufacturer + Description + Model Number using comboboxes and a listbox (same search UX as above).
- Hardware items are linked to the job via the `JobHardware` junction table.

**PDF Package Generation:**
1. Retrieve all `HardwareItem` records linked to the job via `JobHardware`.
2. For each hardware item, retrieve all linked `IndividualTemplate` records via `HardwareItemTemplates`.
3. **Sorting logic:**
   - Within each manufacturer group: sort templates by `Weight` ascending.
   - Sort manufacturer groups against each other by the lowest `Weight` of the first item in the group.
   - If two manufacturer groups have identical leading weights, sort alphabetically by `ManufacturerName`.
4. Download or copy all source PDFs to a job-specific subfolder within the user's `DefaultTemplateSaveLocation`. Use the naming convention: `{ManufacturerName}_{TemplateNumber}.pdf`.
5. Extract only the pages specified in `PagesToPrint`. Apply rotations from `PagesToRotate` / `RotationDirection`.
6. Merge all processed pages into a single PDF.
7. Prepend a **cover sheet** (see Cover Sheet spec below).
8. Add **sequential page numbers** to all pages after the cover sheet.
9. Save a `JobTemplateSnapshot` record for each template used, capturing the file path, pages, and rotation at time of generation.

---

### 2c. Cover Sheet

The cover sheet is prepended to every generated PDF package. It should be professionally formatted and contain:

**Header block:**
- Title: `"Unified Door and Hardware Templates"`
- Job Number
- Job Name
- Customer Name
- Project Manager
- Date Created
- Page X of Y (for the cover sheet itself, in case it spans multiple pages)

**Body table** (one row per hardware item on the job):

| Manufacturer | Hardware Type | Hardware Description | Template Number(s) | Page Number(s) | Remarks |
|---|---|---|---|---|---|

---

### 2d. Maintenance — Refresh Templates

Accessible from the Maintenance menu. This function:

1. Iterates all `IndividualTemplate` records that have a non-null `OnlineLink`.
2. Attempts to download the file to the user's `DefaultTemplateSaveLocation` using the naming convention `{ManufacturerName}_{TemplateNumber}.pdf`.
3. On success: updates `LocalLink` in the `IndividualTemplate` record.
4. On failure (broken link, no response, HTTP error): adds the template to a results report.
5. After completion, displays a summary list of all templates where the download failed or the link was missing, so the user can manually update them.

> **Note:** Existing `JobTemplateSnapshot` records are not modified by this process — they always reflect the state at time of job generation.

---

## 3. CRUD Management

The following tables require full Create, Read, Update, and Delete screens:

- `Customer`
- `Description`
- `DoorMaterial`
- `HardwareItem` *(includes sub-UI to search and link `IndividualTemplate` records via `HardwareItemTemplates`)*
- `IndividualTemplate`
- `Job` *(includes sub-UI to search and link `HardwareItem` records via `JobHardware`)*
- `Manufacturer`
- `ProjectManager`
- `UserProfile`
- `Weight`

`HardwareItemTemplates` and `JobHardware` are managed as part of their parent record screens — no standalone CRUD pages needed.

---

## 4. UI / UX Guidelines

- **Framework:** Avalonia UI (cross-platform Linux + Windows)
- **Visual style:** Windows 98 / classic Win32 — gray backgrounds (#C0C0C0), embossed/beveled buttons, Tahoma or Microsoft Sans Serif fonts, classic title bar styling
- **Navigation:** Always-visible top menu bar with dropdowns (e.g. File, Jobs, Maintenance, Admin) plus a main dashboard with buttons that lead to sub-menus and feature areas
- **Search fields:** Comboboxes for filterable fields (Manufacturer, Description, etc.), results shown in a listbox. If the user makes no selection, default to the first match in the list.
- **Drag-and-drop ordering:** Where applicable (e.g. ordering hardware items in a job), support drag-and-drop reordering of listbox items.

---

## 5. PDF Library

Use a **free** PDF manipulation library compatible with .NET 8 and cross-platform Linux/Windows. Recommended options (select the most capable):

- **PdfSharp / MigraDoc** (MIT license) — good for merging, page manipulation, and document generation
- **iText7** (AGPL license) — more powerful; free for open-source use
- **QuestPDF** (MIT license) — excellent for generating cover sheets and structured layouts

A combination approach is acceptable (e.g. QuestPDF for cover sheet generation, PdfSharp for merging and page manipulation).

---

## 6. Code Quality Standards

- All methods, functions, classes, and non-obvious variables must have **XML doc comments** (`/// <summary>`) at the declaration.
- Follow **SOLID principles** strictly throughout.
- Follow **DRY** — no duplicated logic.
- Apply **Gang of Four design patterns** wherever they naturally reduce complexity (e.g. Repository pattern for data access, Factory for PDF builders, Strategy for sorting).
- All file paths must use `Path.Combine()` — no hardcoded separators.
- The SQLite database file must be stored in a user-writable app data directory (e.g. via `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)`).
- Create the database schema automatically on first run if the file does not exist.
