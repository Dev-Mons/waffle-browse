# Docking Layout State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Waffle Browse WPF foundation through phase 4: shell panel hosting, 1-4 panel presets, tab docking state, and restart-safe layout persistence.

**Architecture:** Put all tab, panel, docking, and persistence behavior in `Waffle.Browse.Core` so it is testable without WPF. Keep WPF in `Waffle.Browse.App`, with a thin shell host and code-behind that renders the current core state.

**Tech Stack:** .NET 9, WPF, Windows Shell `IExplorerBrowser`, `System.Text.Json`, custom console test runner.

---

### Task 1: Create Buildable Project Structure

**Files:**
- Create: `waffle-browse.sln`
- Create: `src/Waffle.Browse.Core/Waffle.Browse.Core.csproj`
- Create: `tests/Waffle.Browse.Core.Tests/Waffle.Browse.Core.Tests.csproj`
- Create: `src/Waffle.Browse.App/Waffle.Browse.App.csproj`

- [x] Create the solution and projects.
- [x] Reference `Waffle.Browse.Core` from both the test project and WPF app.
- [x] Run `dotnet build waffle-browse.slnx` and expect build failures only until the tests reference missing core types.

### Task 2: Red Tests for Docking State

**Files:**
- Create: `tests/Waffle.Browse.Core.Tests/Program.cs`
- Create: `tests/Waffle.Browse.Core.Tests/Docking/DockLayoutServiceTests.cs`
- Create: `tests/Waffle.Browse.Core.Tests/Persistence/DockLayoutStoreTests.cs`

- [x] Write tests proving 1x1, 1x2, 2x1, 2x2, and 3-panel states can be created.
- [x] Write tests proving panel paths and active tabs stay separated.
- [x] Write tests proving hidden panels retain state when panel count is reduced and expanded.
- [x] Write tests proving tab docking refuses a fifth visible panel.
- [x] Write tests proving persisted layout state round-trips and invalid restore paths fall back without throwing.
- [x] Run `dotnet run --project tests/Waffle.Browse.Core.Tests/Waffle.Browse.Core.Tests.csproj` and verify it fails because core types are missing.

### Task 3: Implement Core Models and Services

**Files:**
- Create: `src/Waffle.Browse.Core/Docking/DockDirection.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockLayoutKind.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockLayoutState.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockLayoutService.cs`
- Create: `src/Waffle.Browse.Core/Persistence/DockLayoutStore.cs`

- [x] Implement immutable-enough state classes with clone helpers for JSON.
- [x] Implement preset switching for 1, 2, 3, and 4 panels while preserving hidden panel state.
- [x] Implement tab move and docking operations for center, left, right, top, and bottom.
- [x] Implement save/load with `System.Text.Json`.
- [x] Implement restore normalization that replaces missing, deleted, or inaccessible paths with a fallback path.
- [x] Run the core test project and verify all tests pass.

### Task 4: Implement WPF Shell and Docking UI

**Files:**
- Create/modify: `src/Waffle.Browse.App/App.xaml`
- Create/modify: `src/Waffle.Browse.App/MainWindow.xaml`
- Create/modify: `src/Waffle.Browse.App/MainWindow.xaml.cs`
- Create: `src/Waffle.Browse.App/Shell/ShellExplorerHost.cs`
- Create: `src/Waffle.Browse.App/Controls/ExplorerPanelControl.xaml`
- Create: `src/Waffle.Browse.App/Controls/ExplorerPanelControl.xaml.cs`

- [x] Render 1, 2, 3, and 4 visible panel presets from `DockLayoutState`.
- [x] Host `IExplorerBrowser` inside each panel and navigate by panel active tab path.
- [x] Add panel address bars, back/forward/up app-level navigation, and basic tab add/close controls.
- [x] Add drag/drop from a tab to a target panel; center moves tabs, edges create docked panels while honoring the 4-panel limit.
- [x] Save layout state after user changes and restore it on app startup.
- [x] Run `dotnet build waffle-browse.slnx` and verify the solution builds.

### Task 5: Completion Audit

- [x] Re-read `docs/waffle-browse-plan.md` sections 4.1 through 4.5.
- [x] Map each phase 0-4 requirement to code or verification evidence.
- [x] Run `dotnet run --project tests/Waffle.Browse.Core.Tests/Waffle.Browse.Core.Tests.csproj`.
- [x] Run `dotnet build waffle-browse.slnx`.
- [x] Run a startup smoke test that keeps the WPF app alive for 5 seconds without an immediate shell startup crash.
