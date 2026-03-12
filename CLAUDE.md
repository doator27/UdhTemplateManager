# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Status

This project is in the **planning/specification phase**. No source code exists yet. The two specification documents define the full design:

- `door-hardware-template-builder-prompt.md` — complete feature spec, database schema, code quality standards
- `door-hardware-build-phases.md` — 12-phase implementation roadmap with dependency map

## Technology Stack

- **Language/Runtime:** C# / .NET 8
- **UI:** Avalonia UI (cross-platform Linux + Windows)
- **Database:** SQLite via Entity Framework Core
- **PDF manipulation:** PdfSharp (merging, page extraction, rotation)
- **PDF generation:** QuestPDF (cover sheet)
- **Target platforms:** Linux (dev) and Windows (production)

## Planned Solution Structure

```
HardwareTemplateBuilder.sln
├── HardwareTemplateBuilder.Core/   # Class library: models, repositories, services
└── HardwareTemplateBuilder.App/    # Avalonia UI application
```

## Commands (once implemented)

```bash
dotnet build                          # Build the solution
dotnet run --project HardwareTemplateBuilder.App  # Run the app
dotnet test                           # Run all tests
dotnet test --filter "FullyQualifiedName~WeightParser"  # Run a single test class
dotnet ef migrations add <Name> --project HardwareTemplateBuilder.Core  # Add EF migration
dotnet ef database update --project HardwareTemplateBuilder.Core        # Apply migrations
```

## Architecture

### Data Layer (Core project)
- **EF Core models** map to 13 SQLite tables (see spec for full schema)
- **Generic `IRepository<T>`** with `GetById`, `GetAll`, `Add`, `Update`, `Delete`
- **Specialized repositories** per entity; `IHardwareItemRepository` adds `Search(manufacturer, description, modelNumber)` returning results sorted by `Frequency` descending
- **`DatabaseInitializer`** creates the SQLite file in `Environment.SpecialFolder.ApplicationData` on first run
- Redundancy checks in all `Add` methods — return existing record if duplicate detected

### Key Services (Core project)
| Service | Responsibility |
|---|---|
| `FrequencyService` | Increments `HardwareItem.Frequency` on each retrieval |
| `WeightParser` | Parses `00.000.000` weight strings into sortable segments |
| `PageRangeParser` | Parses page specs like `"1,3-5,8"` into page number lists |
| `TemplateSorter` | Strategy pattern — sorts templates by Weight within manufacturer groups, then groups by lowest weight (alpha tiebreak) |
| `FileAcquirer` | Copies local PDF or downloads from `OnlineLink` into job subfolder as `{ManufacturerName}_{TemplateNumber}.pdf` |
| `PageExtractor` | Extracts specified pages from a PDF |
| `PageRotator` | Rotates specified pages by `RotationDirection` degrees |
| `PdfMerger` | Merges ordered PDFs into one |
| `PageNumberer` | Stamps sequential page numbers starting after the cover sheet |
| `CoverSheetBuilder` | QuestPDF-based cover sheet with header block and hardware table |

### PDF Assembly Pipeline (Phases 7–10)
1. `TemplateSorter` → sorted template list
2. `FileAcquirer` → local copies in `{JobNumber}/` subfolder
3. `PageExtractor` → `PageRotator` → `PdfMerger` → merged body PDF
4. `CoverSheetBuilder` → cover sheet PDF
5. Prepend cover sheet + `PageNumberer` (offset past cover pages)
6. Write `JobTemplateSnapshot` records (captures state at generation time, never modified by later refresh)

### UI (App project)
- `MainWindow` with persistent top menu bar (`File`, `Jobs`, `Maintenance`, `Admin`) and dashboard panel
- Content area swaps child `UserControl` views via a navigation/view router
- Visual style: `#C0C0C0` background, beveled buttons, Tahoma/Arial fonts (Windows 98 aesthetic) applied via Avalonia `ControlTheme`/`Style`
- Standard search UX pattern: 3 comboboxes (Manufacturer, Description, ModelNumber) → listbox sorted by `Frequency` desc → default to first match
- On first launch with no `UserProfile` records, prompt user to create one before proceeding

### `JobTemplateSnapshot`
Snapshots are written at PDF generation time and are **never modified** — they preserve the exact pages, rotation, and file path used when the package was generated.

## Code Quality Requirements

Per the project specification:
- All public methods, classes, and non-obvious variables require XML doc comments (`/// <summary>`)
- SOLID principles strictly enforced
- Repository pattern for data access, Factory for PDF builders, Strategy for sorting
- All file paths must use `Path.Combine()` — no hardcoded separators

## Implementation Order

Phases 3–6 (UI) and 7–8 (PDF engine) can be built in parallel after Phase 2. See `door-hardware-build-phases.md` for the full dependency map.
