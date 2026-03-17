# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Technology Stack

- **Language/Runtime:** C# / .NET 8
- **UI:** Avalonia UI 11.3.9 (cross-platform Linux + Windows)
- **Database:** SQLite via Entity Framework Core 8.0
- **PDF manipulation:** PdfSharp 6.1 (merging, page extraction, rotation)
- **PDF generation:** QuestPDF 2024 (cover sheet)
- **Tests:** xUnit with EF Core InMemory/SQLite providers
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

- **AppDbContext** manages 13 SQLite tables via EF Core
- **DatabaseInitializer** handles creation, migration, and post-migration schema patches on startup
- **DatabaseLocationService** manages the configurable database file path (default: `%AppData%/HardwareTemplateBuilder/hardware_templates.db`)
- **Generic `IRepository<T>`** with `GetById`, `GetAll`, `Add`, `Update`, `Delete`; `IHardwareItemRepository` adds `Search(manufacturer, description, modelNumber)` returning results sorted by `Frequency` descending
- All `Add()` methods include duplicate detection — return existing record if already present
- `JobTemplateSnapshot` records are written once at PDF generation and **never modified**

### Key Services (Core/Services/)

| Service | Responsibility |
|---|---|
| `FrequencyService` | Increments `HardwareItem.Frequency` on each retrieval |
| `PageRangeParser` | Parses page specs like `"1,3-5,8"` into page number lists |
| `DescriptionPathService` | Builds hierarchical display paths for `Description` tree nodes |
| `TemplateRefreshService` | Re-downloads or re-copies template files on demand |

### PDF Assembly Pipeline (Core/Services/Pdf/)

Orchestrated by `PdfAssemblyService`, which accepts an `AssemblyRequest` and executes:

1. `TemplateSorter` (uses `ITemplateSortStrategy` — default: `WeightTemplateSortStrategy`) → sorted template list
2. `FileAcquirer` → local copies in `{JobNumber}/` subfolder as `{ManufacturerName}_{TemplateNumber}.pdf`
3. `PageExtractor` → `PageRotator` → `PdfMerger` → merged body PDF
4. `CoverSheetBuilder` (QuestPDF) → cover sheet PDF with header block and hardware table
5. Prepend cover sheet + `PageNumberer` stamps sequential numbers offset past cover pages
6. Write `JobTemplateSnapshot` records

### UI (App/)

- `MainWindow` hosts a persistent menu bar (File, Jobs, Maintenance, Admin) and a content area
- `ViewRouter` swaps child `UserControl` views for navigation — 17 views total
- `SessionService` (static) holds the active `UserProfile` for the session lifetime
- On first launch with no `UserProfile` records, `ProfilePickerDialog` prompts creation before proceeding
- Visual style: `#C0C0C0` background, beveled buttons, Tahoma/Arial fonts (Windows 98 aesthetic) via Avalonia `ControlTheme`/`Style`
- Standard search pattern throughout: Manufacturer/Description/ModelNumber comboboxes → listbox sorted by `Frequency` desc

### Description Hierarchy

`Description` is a self-referential tree used for categorizing hardware items. `DescriptionPathService` resolves full paths. The hierarchy is also how `WeightTemplateSortStrategy` groups templates for sorting.

## Code Quality Requirements

- All public methods, classes, and non-obvious variables require XML doc comments (`/// <summary>`)
- SOLID principles: Repository for data access, Strategy for sorting (`ITemplateSortStrategy`), Factory via `ViewRouter`
- All file paths must use `Path.Combine()` — no hardcoded separators
- Nullable reference types enabled across all projects
