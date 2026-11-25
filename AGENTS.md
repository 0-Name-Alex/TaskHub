# TaskHub — AI Agent Guide

Local-first Windows task manager, reminder engine, and desktop automation hub targeting **.NET 10 / C# 14 / WPF**.

## 1. Architecture & Strict Layer Boundaries
Read [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) before major changes. Core invariants:
- **`TaskHub.Core`** (`net10.0`) must have **zero** WPF, ASP.NET Core, EF Core, or Windows-specific dependencies so domain logic, priority-queue scheduling, recurrence (`Ical.Net`), and rules remain platform-independent and fast to test.
- **`TaskHub.Abstractions`** (`net10.0`) holds shared contracts, DTOs, event envelopes, and `ITaskHubPlugin`. Keep external SDK deps minimal.
- **`TaskHub.Storage`** (`net10.0`) owns EF Core SQLite (`WAL` mode), repositories, migrations, and backups. All state mutations must flow through application services in Core so persistence and `IEventBus` stay consistent.
- **`TaskHub.Platform.Windows`** (`net10.0-windows`) isolates Win32 interop (`CsWin32` via `NativeMethods.txt` + P/Invoke in `Memory/MemoryOptimizer.cs`).
- **`TaskHub.Api`** hosts Kestrel on loopback `127.0.0.1` (REST + `/v1/events` WebSocket for `TaskHubGreeter` & `TaskHub.Cli`).
- **`TaskHub.App`** hosts the .NET Generic Host, WPF UI (`MainWindow`, `LauncherWindow`, `TrayIconService`), and single-instance lock.

## 2. Memory Governor & Performance Rules
- **Workstation GC is mandatory** in [`src/TaskHub.App/TaskHub.App.csproj`](src/TaskHub.App/TaskHub.App.csproj) (`<ServerGarbageCollection>false</ServerGarbageCollection>`, `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>`). Do not remove these—ASP.NET Core defaults to Server GC which inflates idle RAM to ~200+ MB.
- [`MemoryOptimizer.TrimWorkingSet()`](src/TaskHub.Platform.Windows/Memory/MemoryOptimizer.cs) runs compacting GC + `SetProcessWorkingSetSize(-1, -1)` + `EmptyWorkingSet` 3 seconds post-startup, on minimize/close to tray, and on tray **"Trim memory"**. Keep idle RAM ~15–28 MB.

## 3. Build, Test & Database Migrations
- **Full build + auto-migration check + tests**: `.\build.ps1` (or `build.cmd`).
- **Tests**: `dotnet test TaskHub.slnx -c Release` (471 tests across `TaskHub.Core.Tests`, `TaskHub.Storage.Tests`, `TaskHub.Api.Tests`).
- **Publish single-file**: `.\build.ps1 -Publish` -> outputs `TaskHub.exe` and `th.exe` to `artifacts/TaskHub/`.
- **EF Core model changes**: `build.ps1` checks `dotnet ef migrations has-pending-model-changes` and auto-generates a migration using `src/TaskHub.Storage/next-migration.txt` (or timestamp).
- **Static analysis**: `Directory.Build.props` enables .NET analyzers; resolve any new `CA*` warnings rather than suppressing them globally.

## 4. Git & CI
- Author: `0-Name-Alex <0-Name-Alex@users.noreply.github.com>`, Conventional Commits, branch `main`.
- CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) builds and tests `TaskHub.slnx` in Release on `windows-latest`, and publishes `TaskHub-win-x64.zip` on `v*` tags.
