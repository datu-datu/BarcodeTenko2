# Copilot Instructions for BarcodeTenko2

## Build, run, test, lint

### Build / run
- Restore + build:
  - `dotnet restore`
  - `dotnet build .\Tenko.Native.csproj`
- Run app:
  - `dotnet run --project .\Tenko.Native.csproj`
- Publish (single-file, self-contained win-x64 is already configured in `Tenko.Native.csproj`):
  - `dotnet publish .\Tenko.Native.csproj -c Release`

### Test
- There is currently no dedicated test project in this repository (`dotnet test` completes with no test execution).
- If a test project is added later:
  - full suite: `dotnet test`
  - single test: `dotnet test --filter "FullyQualifiedName~Namespace.ClassName.TestMethodName"`

### Lint / format
- No separate linter configuration is present.
- Use .NET formatter when needed:
  - check only: `dotnet format --verify-no-changes`
  - apply fixes: `dotnet format`

## High-level architecture

- This is a .NET 8 WPF desktop app (`net8.0-windows`) using a lightweight MVVM structure:
  - **View**: `MainWindow.xaml`
  - **ViewModel**: `ViewModels/MainViewModel.cs`
  - **Services**: `Services/*` for persistence, settings, notifications, and student lookup

- `MainWindow.xaml.cs` wires dependencies manually (no DI container), sets `DataContext`, and owns UI-only behaviors:
  - Enter-key forwarding for scanner/manual input
  - modal open/close handlers
  - live clock update
  - `data/time.json` file watcher and deadline display
  - red flash storyboard trigger on warning/error notifications

- `MainViewModel` owns domain workflow:
  - validates scanner input (digits only, length 5 or 10)
  - maps input to `Last5` (`ushort`)
  - duplicate prevention by `(Location, Last5)` across `_allHistory`
  - writes both `history.json` and `scans/ids_<location>.bin`
  - supports search/filter, export, per-record delete, location-scoped delete-all, and rename/archive flow

- Persistence is portable and always relative to `AppDomain.CurrentDomain.BaseDirectory`:
  - `data/settings.json` (selected location)
  - `data/locations.json` (location list)
  - `data/history.json` (JSON records)
  - `data/students.enc` (encrypted student lookup table)
  - `data/time.json` (deadline candidates)
  - `scans/ids_<location>.bin` (binary append log)

## Key repo conventions

- **Binary scan format is strict**: append `Last5` as UInt16 little-endian (2 bytes) into `scans/ids_<location>.bin` (README + `ScanFileService`).
- **History is global, UI is location-scoped**: `_allHistory` stores full JSON history; UI `History` is filtered by current `Location` and `SearchText`.
- **Location is mandatory for operations**: scanning/export/delete actions are guarded by `IsLocationSet`, and warning notifications are used when missing.
- **Rename/archive semantics**: renaming an existing bin creates `ids_<location>_<suffix>.bin`; after rename, current-location history is cleared to start a new measurement cycle.
- **Student CSV contract** (`data/students.csv` before encryption): header `student_number,name,code`; lookup key is `student_number` parsed as `ushort`, and displayed fields come from `name` and `code`.
- **Student encryption workflow**: write passphrase in `data/students.passphrase`, then run `.\tools\Encrypt-StudentsCsv.ps1` to generate `data/students.enc` (Windows PowerShell 5.1 / PowerShell 7 compatible); app loads only encrypted file.
- **UI + messaging language**: user-facing strings are Japanese; keep new UI labels/messages consistent with existing Japanese wording.
- **Instruction style inherited from `CLAUDE.md`**: assistant-facing prose in this repo is expected to be concise, direct, and non-polite Japanese (短文・敬語不要・冗長表現を避ける).
