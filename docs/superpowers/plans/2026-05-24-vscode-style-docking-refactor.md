# VS Code Style Docking Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor Waffle Browse tab docking from hardcoded 3x3 direction zones into a VS Code style model with grid-backed layout state, testable drop-preview hit testing, and a single preview rectangle shown during drag.

**Architecture:** Keep `PanelState` and `TabState` as the file-navigation state. Add a `DockGridState` tree that owns visible panel layout and derives `DockLayoutKind` as a summary, then route all drag/drop through `DockDropHitTester -> DockDropPreview -> DockLayoutService.CommitDrop`. Move WPF drag UI from panel-local 3x3 labels to workspace-level preview rendering so native `IExplorerBrowser` remains passive during drag.

**Tech Stack:** .NET 9, WPF, `System.Text.Json`, existing console-based core test runner.

---

## Source Context

This plan applies the analysis in `docs/vscode-tab-docking-review.md`.

Current hardcoded points to remove or isolate:

- `src/Waffle.Browse.App/Controls/ExplorerPanelControl.xaml.cs`: `DirectionFromPoint`, `SetDockOverlayVisible`, panel-local drag/drop decision.
- `src/Waffle.Browse.App/Controls/ExplorerPanelControl.xaml`: 3x3 `DockOverlay` with `Top`, `Left`, `Move`, `Right`, `Bottom` labels.
- `src/Waffle.Browse.Core/Docking/DockLayoutService.cs`: `DockTab` directly maps direction to `DockLayoutKind` and visible panel slots.
- `src/Waffle.Browse.Core/Docking/DockLayoutKind.cs`: currently behaves as primary layout model; after this refactor it should be a derived summary.

## File Structure

Create these focused core files:

- `src/Waffle.Browse.Core/Docking/DockOrientation.cs`: split axis enum.
- `src/Waffle.Browse.Core/Docking/DockNode.cs`: layout tree nodes, `DockLeaf` and `DockSplit`.
- `src/Waffle.Browse.Core/Docking/DockGridState.cs`: root tree wrapper and helper queries.
- `src/Waffle.Browse.Core/Docking/DockDropOperation.cs`: `None`, `MoveIntoPanel`, `SplitPanel`, `ReorderTab`.
- `src/Waffle.Browse.Core/Docking/DockDropOptions.cs`: edge threshold, preferred orientation, max panels, close-empty policy.
- `src/Waffle.Browse.Core/Docking/DockDropPreview.cs`: hit-test result shared by UI and service commit.
- `src/Waffle.Browse.Core/Docking/DockDragPayload.cs`: core payload for dragged tab.
- `src/Waffle.Browse.Core/Docking/DockRect.cs` and `DockPoint.cs`: UI-independent geometry for tests and hit testing.
- `src/Waffle.Browse.Core/Docking/DockDropHitTester.cs`: VS Code style edge threshold and preview rectangle calculation.
- `src/Waffle.Browse.Core/Docking/DockGridService.cs`: tree operations, split, collapse, summary layout kind, leaf order.

Modify these existing core files:

- `src/Waffle.Browse.Core/Docking/DockLayoutState.cs`: add `DockGridState Grid`, derive `VisiblePanels` from grid leaf order, keep JSON compatibility.
- `src/Waffle.Browse.Core/Docking/DockLayoutService.cs`: replace direction-first `DockTab` logic with preview commit methods while preserving public convenience wrappers during migration.
- `src/Waffle.Browse.Core/Persistence/DockLayoutStore.cs`: normalize old saved layouts that do not contain `Grid`.

Create these app files:

- `src/Waffle.Browse.App/Docking/DockPreviewOverlay.xaml`: workspace-level preview rectangle.
- `src/Waffle.Browse.App/Docking/DockPreviewOverlay.xaml.cs`: display/hide/update preview bounds.
- `src/Waffle.Browse.App/Docking/WorkspaceDockController.cs`: WPF drag session, target panel lookup, hit-test calls, drop commit.
- `src/Waffle.Browse.App/Docking/PanelBoundsSnapshot.cs`: panel id to workspace bounds mapping.

Modify these app files:

- `src/Waffle.Browse.App/MainWindow.xaml`: wrap `WorkspaceGrid` and `DockPreviewOverlay` in a single root grid.
- `src/Waffle.Browse.App/MainWindow.xaml.cs`: delegate drag lifecycle to `WorkspaceDockController`, render panels from `DockGridState`.
- `src/Waffle.Browse.App/Controls/ExplorerPanelControl.xaml`: remove 3x3 `DockOverlay`; expose tab bar and shell host area without deciding dock direction.
- `src/Waffle.Browse.App/Controls/ExplorerPanelControl.xaml.cs`: emit drag payload and pointer updates, no geometry decision.

Modify these tests:

- `tests/Waffle.Browse.Core.Tests/Program.cs`: register new test classes.
- `tests/Waffle.Browse.Core.Tests/Docking/DockGridStateTests.cs`: tree summary and leaf-order tests.
- `tests/Waffle.Browse.Core.Tests/Docking/DockDropHitTesterTests.cs`: VS Code style hit-test tests.
- `tests/Waffle.Browse.Core.Tests/Docking/DockLayoutCommitTests.cs`: commit preview tests.
- `tests/Waffle.Browse.Core.Tests/Persistence/DockLayoutStoreTests.cs`: old-layout migration and grid round-trip tests.

---

### Task 1: Add UI-Independent Drop Hit Testing

**Files:**
- Create: `src/Waffle.Browse.Core/Docking/DockPoint.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockRect.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockDropOperation.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockDropOptions.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockDragPayload.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockDropPreview.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockDropHitTester.cs`
- Create: `tests/Waffle.Browse.Core.Tests/Docking/DockDropHitTesterTests.cs`
- Modify: `tests/Waffle.Browse.Core.Tests/Program.cs`

- [x] **Step 1: Register failing tests**

Modify `tests/Waffle.Browse.Core.Tests/Program.cs` by adding these test entries after existing docking tests:

```csharp
("Hit tester returns center move preview outside edge threshold", DockDropHitTesterTests.CenterAreaReturnsMoveIntoPanel),
("Hit tester returns split preview at each edge", DockDropHitTesterTests.EdgeAreasReturnSplitPreview),
("Hit tester rejects edge split when max panels is reached", DockDropHitTesterTests.EdgeSplitRejectedWhenMaxPanelsReached),
("Hit tester treats edge as center when split drag is disabled", DockDropHitTesterTests.EdgeAreaReturnsMoveWhenSplitIsDisabled),
```

- [x] **Step 2: Write failing hit-test tests**

Create `tests/Waffle.Browse.Core.Tests/Docking/DockDropHitTesterTests.cs`:

```csharp
using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.Core.Tests.Docking;

internal static class DockDropHitTesterTests
{
    public static void CenterAreaReturnsMoveIntoPanel()
    {
        var targetPanelId = Guid.NewGuid();
        var payload = new DockDragPayload(Guid.NewGuid(), Guid.NewGuid());
        var preview = new DockDropHitTester().HitTest(
            new DockRect(0, 0, 1000, 800),
            new DockPoint(500, 400),
            targetPanelId,
            payload,
            new DockDropOptions(CurrentVisiblePanelCount: 2));

        TestAssert.Equal(DockDropOperation.MoveIntoPanel, preview.Operation, "Center area should move into target panel");
        TestAssert.Equal(targetPanelId, preview.TargetPanelId, "Preview should target the panel under the pointer");
        TestAssert.Equal(null, preview.SplitDirection, "Move operation should not have a split direction");
        TestAssert.True(preview.Accepted, "Center move should be accepted");
        TestAssert.Equal(new DockRect(0, 0, 1000, 800), preview.PreviewBounds, "Center move should preview the whole panel");
    }

    public static void EdgeAreasReturnSplitPreview()
    {
        var targetPanelId = Guid.NewGuid();
        var payload = new DockDragPayload(Guid.NewGuid(), Guid.NewGuid());
        var hitTester = new DockDropHitTester();
        var options = new DockDropOptions(CurrentVisiblePanelCount: 1);
        var bounds = new DockRect(0, 0, 1000, 800);

        var left = hitTester.HitTest(bounds, new DockPoint(30, 400), targetPanelId, payload, options);
        TestAssert.Equal(DockDropOperation.SplitPanel, left.Operation, "Left edge should split");
        TestAssert.Equal(DockDirection.Left, left.SplitDirection, "Left edge should split left");
        TestAssert.Equal(new DockRect(0, 0, 500, 800), left.PreviewBounds, "Left preview should be left half");

        var right = hitTester.HitTest(bounds, new DockPoint(970, 400), targetPanelId, payload, options);
        TestAssert.Equal(DockDirection.Right, right.SplitDirection, "Right edge should split right");
        TestAssert.Equal(new DockRect(500, 0, 500, 800), right.PreviewBounds, "Right preview should be right half");

        var top = hitTester.HitTest(bounds, new DockPoint(500, 30), targetPanelId, payload, options);
        TestAssert.Equal(DockDirection.Top, top.SplitDirection, "Top edge should split top");
        TestAssert.Equal(new DockRect(0, 0, 1000, 400), top.PreviewBounds, "Top preview should be top half");

        var bottom = hitTester.HitTest(bounds, new DockPoint(500, 770), targetPanelId, payload, options);
        TestAssert.Equal(DockDirection.Bottom, bottom.SplitDirection, "Bottom edge should split bottom");
        TestAssert.Equal(new DockRect(0, 400, 1000, 400), bottom.PreviewBounds, "Bottom preview should be bottom half");
    }

    public static void EdgeSplitRejectedWhenMaxPanelsReached()
    {
        var targetPanelId = Guid.NewGuid();
        var preview = new DockDropHitTester().HitTest(
            new DockRect(0, 0, 1000, 800),
            new DockPoint(20, 400),
            targetPanelId,
            new DockDragPayload(Guid.NewGuid(), Guid.NewGuid()),
            new DockDropOptions(CurrentVisiblePanelCount: 4, MaxVisiblePanels: 4));

        TestAssert.Equal(DockDropOperation.SplitPanel, preview.Operation, "Edge still represents split intent");
        TestAssert.False(preview.Accepted, "Split should be rejected at max panel count");
        TestAssert.Equal("The layout already has four visible panels.", preview.RejectionReason, "Rejection reason should be explicit");
    }

    public static void EdgeAreaReturnsMoveWhenSplitIsDisabled()
    {
        var targetPanelId = Guid.NewGuid();
        var preview = new DockDropHitTester().HitTest(
            new DockRect(0, 0, 1000, 800),
            new DockPoint(20, 400),
            targetPanelId,
            new DockDragPayload(Guid.NewGuid(), Guid.NewGuid()),
            new DockDropOptions(CurrentVisiblePanelCount: 1, SplitOnDragAndDrop: false));

        TestAssert.Equal(DockDropOperation.MoveIntoPanel, preview.Operation, "Disabled split should treat edge as center move");
        TestAssert.Equal(null, preview.SplitDirection, "Disabled split should not carry split direction");
        TestAssert.Equal(new DockRect(0, 0, 1000, 800), preview.PreviewBounds, "Disabled split should preview whole panel");
    }
}
```

- [x] **Step 3: Run tests and verify red**

Run:

```powershell
dotnet run --project tests\Waffle.Browse.Core.Tests\Waffle.Browse.Core.Tests.csproj
```

Expected: build fails with missing `DockDropHitTester`, `DockDropOptions`, `DockDropPreview`, `DockPoint`, `DockRect`, `DockDragPayload`, and `DockDropOperation`.

- [x] **Step 4: Add geometry and preview models**

Create `src/Waffle.Browse.Core/Docking/DockPoint.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public readonly record struct DockPoint(double X, double Y);
```

Create `src/Waffle.Browse.Core/Docking/DockRect.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public readonly record struct DockRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
}
```

Create `src/Waffle.Browse.Core/Docking/DockDropOperation.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public enum DockDropOperation
{
    None,
    MoveIntoPanel,
    SplitPanel,
    ReorderTab
}
```

Create `src/Waffle.Browse.Core/Docking/DockDropOptions.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public sealed record DockDropOptions(
    int CurrentVisiblePanelCount,
    int MaxVisiblePanels = DockLayoutService.MaxPanels,
    double EdgeThresholdRatio = 0.10,
    DockOrientation PreferredOrientation = DockOrientation.Horizontal,
    bool SplitOnDragAndDrop = true);
```

Create `src/Waffle.Browse.Core/Docking/DockDragPayload.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public sealed record DockDragPayload(Guid SourcePanelId, Guid TabId);
```

Create `src/Waffle.Browse.Core/Docking/DockDropPreview.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public sealed record DockDropPreview(
    DockDropOperation Operation,
    Guid TargetPanelId,
    DockDirection? SplitDirection,
    DockRect PreviewBounds,
    bool Accepted,
    string? RejectionReason = null)
{
    public static DockDropPreview None(Guid targetPanelId, DockRect bounds)
    {
        return new DockDropPreview(DockDropOperation.None, targetPanelId, null, bounds, false, "No drop operation is available.");
    }
}
```

- [x] **Step 5: Add `DockOrientation`**

Create `src/Waffle.Browse.Core/Docking/DockOrientation.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public enum DockOrientation
{
    Horizontal,
    Vertical
}
```

- [x] **Step 6: Implement hit tester**

Create `src/Waffle.Browse.Core/Docking/DockDropHitTester.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public sealed class DockDropHitTester
{
    public DockDropPreview HitTest(
        DockRect targetPanelBounds,
        DockPoint pointer,
        Guid targetPanelId,
        DockDragPayload payload,
        DockDropOptions options)
    {
        if (targetPanelBounds.Width <= 0 || targetPanelBounds.Height <= 0)
        {
            return DockDropPreview.None(targetPanelId, targetPanelBounds);
        }

        if (!options.SplitOnDragAndDrop)
        {
            return MovePreview(targetPanelId, targetPanelBounds);
        }

        var leftDistance = Math.Max(0, pointer.X - targetPanelBounds.Left);
        var rightDistance = Math.Max(0, targetPanelBounds.Right - pointer.X);
        var topDistance = Math.Max(0, pointer.Y - targetPanelBounds.Top);
        var bottomDistance = Math.Max(0, targetPanelBounds.Bottom - pointer.Y);
        var horizontalThreshold = targetPanelBounds.Width * options.EdgeThresholdRatio;
        var verticalThreshold = targetPanelBounds.Height * options.EdgeThresholdRatio;

        var direction = GetSplitDirection(
            leftDistance,
            rightDistance,
            topDistance,
            bottomDistance,
            horizontalThreshold,
            verticalThreshold,
            options.PreferredOrientation);

        if (direction is null)
        {
            return MovePreview(targetPanelId, targetPanelBounds);
        }

        var accepted = options.CurrentVisiblePanelCount < options.MaxVisiblePanels;
        return new DockDropPreview(
            DockDropOperation.SplitPanel,
            targetPanelId,
            direction,
            PreviewForDirection(targetPanelBounds, direction.Value),
            accepted,
            accepted ? null : "The layout already has four visible panels.");
    }

    private static DockDropPreview MovePreview(Guid targetPanelId, DockRect targetPanelBounds)
    {
        return new DockDropPreview(DockDropOperation.MoveIntoPanel, targetPanelId, null, targetPanelBounds, true);
    }

    private static DockDirection? GetSplitDirection(
        double leftDistance,
        double rightDistance,
        double topDistance,
        double bottomDistance,
        double horizontalThreshold,
        double verticalThreshold,
        DockOrientation preferredOrientation)
    {
        var horizontalCandidate = leftDistance <= horizontalThreshold || rightDistance <= horizontalThreshold;
        var verticalCandidate = topDistance <= verticalThreshold || bottomDistance <= verticalThreshold;

        if (!horizontalCandidate && !verticalCandidate)
        {
            return null;
        }

        if (horizontalCandidate && verticalCandidate)
        {
            return preferredOrientation == DockOrientation.Horizontal
                ? (leftDistance <= rightDistance ? DockDirection.Left : DockDirection.Right)
                : (topDistance <= bottomDistance ? DockDirection.Top : DockDirection.Bottom);
        }

        if (horizontalCandidate)
        {
            return leftDistance <= rightDistance ? DockDirection.Left : DockDirection.Right;
        }

        return topDistance <= bottomDistance ? DockDirection.Top : DockDirection.Bottom;
    }

    private static DockRect PreviewForDirection(DockRect bounds, DockDirection direction)
    {
        return direction switch
        {
            DockDirection.Left => new DockRect(bounds.X, bounds.Y, bounds.Width / 2, bounds.Height),
            DockDirection.Right => new DockRect(bounds.X + bounds.Width / 2, bounds.Y, bounds.Width / 2, bounds.Height),
            DockDirection.Top => new DockRect(bounds.X, bounds.Y, bounds.Width, bounds.Height / 2),
            DockDirection.Bottom => new DockRect(bounds.X, bounds.Y + bounds.Height / 2, bounds.Width, bounds.Height / 2),
            _ => bounds
        };
    }
}
```

- [x] **Step 7: Run tests and verify green for hit tester**

Run:

```powershell
dotnet run --project tests\Waffle.Browse.Core.Tests\Waffle.Browse.Core.Tests.csproj
```

Expected: all existing tests plus the new hit-test tests pass.

---

### Task 2: Add Dock Grid Tree While Preserving Existing Behavior

**Files:**
- Create: `src/Waffle.Browse.Core/Docking/DockNode.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockGridState.cs`
- Create: `src/Waffle.Browse.Core/Docking/DockGridService.cs`
- Create: `tests/Waffle.Browse.Core.Tests/Docking/DockGridStateTests.cs`
- Modify: `src/Waffle.Browse.Core/Docking/DockLayoutState.cs`
- Modify: `src/Waffle.Browse.Core/Docking/DockLayoutService.cs`
- Modify: `tests/Waffle.Browse.Core.Tests/Program.cs`

- [x] **Step 1: Register failing grid tests**

Modify `tests/Waffle.Browse.Core.Tests/Program.cs` by adding:

```csharp
("Dock grid derives layout kind from leaf tree", DockGridStateTests.DockGridDerivesLayoutKindFromLeafTree),
("Dock grid preserves leaf order for rendering", DockGridStateTests.DockGridPreservesLeafOrderForRendering),
("Dock grid rejects fifth leaf", DockGridStateTests.DockGridRejectsFifthLeaf),
```

- [x] **Step 2: Write failing grid tests**

Create `tests/Waffle.Browse.Core.Tests/Docking/DockGridStateTests.cs`:

```csharp
using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.Core.Tests.Docking;

internal static class DockGridStateTests
{
    public static void DockGridDerivesLayoutKindFromLeafTree()
    {
        var service = new DockGridService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var fourth = Guid.NewGuid();

        var grid = DockGridState.Single(first);
        TestAssert.Equal(DockLayoutKind.OneByOne, service.GetLayoutKind(grid), "Single leaf should derive 1x1");

        grid = service.Split(grid, first, second, DockDirection.Right);
        TestAssert.Equal(DockLayoutKind.OneByTwo, service.GetLayoutKind(grid), "Right split should derive 1x2");

        grid = service.Split(grid, second, third, DockDirection.Bottom);
        TestAssert.Equal(DockLayoutKind.ThreePanelPrimaryLeft, service.GetLayoutKind(grid), "Third leaf should derive three-panel layout");

        grid = service.Split(grid, third, fourth, DockDirection.Left);
        TestAssert.Equal(DockLayoutKind.TwoByTwo, service.GetLayoutKind(grid), "Fourth leaf should derive 2x2");
    }

    public static void DockGridPreservesLeafOrderForRendering()
    {
        var service = new DockGridService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var grid = DockGridState.Single(first);

        grid = service.Split(grid, first, second, DockDirection.Right);
        grid = service.Split(grid, second, third, DockDirection.Bottom);

        var leaves = service.GetLeafPanelIds(grid).ToList();
        TestAssert.Equal(3, leaves.Count, "Grid should contain three leaves");
        TestAssert.Equal(first, leaves[0], "First leaf should stay first");
        TestAssert.True(leaves.Contains(second), "Second leaf should be present");
        TestAssert.True(leaves.Contains(third), "Third leaf should be present");
    }

    public static void DockGridRejectsFifthLeaf()
    {
        var service = new DockGridService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var fourth = Guid.NewGuid();
        var fifth = Guid.NewGuid();
        var grid = DockGridState.Single(first);

        grid = service.Split(grid, first, second, DockDirection.Right);
        grid = service.Split(grid, second, third, DockDirection.Bottom);
        grid = service.Split(grid, third, fourth, DockDirection.Left);

        TestAssert.Equal(4, service.GetLeafPanelIds(grid).Count, "Setup should have four leaves");

        var threw = false;
        try
        {
            service.Split(grid, fourth, fifth, DockDirection.Right);
        }
        catch (InvalidOperationException ex)
        {
            threw = ex.Message == "The layout already has four visible panels.";
        }

        TestAssert.True(threw, "Fifth split should throw the max panel error");
    }
}
```

- [x] **Step 3: Run tests and verify red**

Run:

```powershell
dotnet run --project tests\Waffle.Browse.Core.Tests\Waffle.Browse.Core.Tests.csproj
```

Expected: missing `DockGridState`, `DockNode`, and `DockGridService` compile errors.

- [x] **Step 4: Add grid node model**

Create `src/Waffle.Browse.Core/Docking/DockNode.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public abstract record DockNode;

public sealed record DockLeaf(Guid PanelId) : DockNode;

public sealed record DockSplit(
    DockOrientation Orientation,
    DockNode First,
    DockNode Second,
    double Ratio = 0.5) : DockNode;
```

Create `src/Waffle.Browse.Core/Docking/DockGridState.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public sealed record DockGridState
{
    public DockNode Root { get; init; } = new DockLeaf(Guid.Empty);

    public static DockGridState Single(Guid panelId)
    {
        return new DockGridState { Root = new DockLeaf(panelId) };
    }
}
```

- [x] **Step 5: Add grid service**

Create `src/Waffle.Browse.Core/Docking/DockGridService.cs`:

```csharp
namespace Waffle.Browse.Core.Docking;

public sealed class DockGridService
{
    public IReadOnlyList<Guid> GetLeafPanelIds(DockGridState grid)
    {
        var leaves = new List<Guid>();
        CollectLeaves(grid.Root, leaves);
        return leaves;
    }

    public DockGridState Split(DockGridState grid, Guid targetPanelId, Guid newPanelId, DockDirection direction)
    {
        if (GetLeafPanelIds(grid).Count >= DockLayoutService.MaxPanels)
        {
            throw new InvalidOperationException("The layout already has four visible panels.");
        }

        return grid with
        {
            Root = SplitNode(grid.Root, targetPanelId, newPanelId, direction)
        };
    }

    public DockGridState RemoveLeaf(DockGridState grid, Guid panelId)
    {
        var reduced = RemoveNode(grid.Root, panelId);
        return grid with { Root = reduced ?? new DockLeaf(panelId) };
    }

    public DockLayoutKind GetLayoutKind(DockGridState grid)
    {
        var leaves = GetLeafPanelIds(grid);
        return leaves.Count switch
        {
            <= 1 => DockLayoutKind.OneByOne,
            2 => GetTwoLeafKind(grid.Root),
            3 => DockLayoutKind.ThreePanelPrimaryLeft,
            _ => DockLayoutKind.TwoByTwo
        };
    }

    private static DockLayoutKind GetTwoLeafKind(DockNode root)
    {
        return root is DockSplit { Orientation: DockOrientation.Vertical }
            ? DockLayoutKind.TwoByOne
            : DockLayoutKind.OneByTwo;
    }

    private static DockNode SplitNode(DockNode node, Guid targetPanelId, Guid newPanelId, DockDirection direction)
    {
        if (node is DockLeaf leaf && leaf.PanelId == targetPanelId)
        {
            var newLeaf = new DockLeaf(newPanelId);
            var targetLeaf = new DockLeaf(targetPanelId);
            var orientation = direction is DockDirection.Left or DockDirection.Right
                ? DockOrientation.Horizontal
                : DockOrientation.Vertical;

            return direction is DockDirection.Left or DockDirection.Top
                ? new DockSplit(orientation, newLeaf, targetLeaf)
                : new DockSplit(orientation, targetLeaf, newLeaf);
        }

        if (node is DockSplit split)
        {
            return split with
            {
                First = SplitNode(split.First, targetPanelId, newPanelId, direction),
                Second = SplitNode(split.Second, targetPanelId, newPanelId, direction)
            };
        }

        return node;
    }

    private static DockNode? RemoveNode(DockNode node, Guid panelId)
    {
        if (node is DockLeaf leaf)
        {
            return leaf.PanelId == panelId ? null : leaf;
        }

        if (node is not DockSplit split)
        {
            return node;
        }

        var first = RemoveNode(split.First, panelId);
        var second = RemoveNode(split.Second, panelId);

        return (first, second) switch
        {
            (null, null) => null,
            (not null, null) => first,
            (null, not null) => second,
            _ => split with { First = first!, Second = second! }
        };
    }

    private static void CollectLeaves(DockNode node, List<Guid> leaves)
    {
        switch (node)
        {
            case DockLeaf leaf:
                leaves.Add(leaf.PanelId);
                break;
            case DockSplit split:
                CollectLeaves(split.First, leaves);
                CollectLeaves(split.Second, leaves);
                break;
        }
    }
}
```

- [x] **Step 6: Add grid to layout state**

Modify `src/Waffle.Browse.Core/Docking/DockLayoutState.cs` to include `Grid` and visible panel ordering:

```csharp
using System.Text.Json.Serialization;

namespace Waffle.Browse.Core.Docking;

public sealed record DockLayoutState
{
    public DockLayoutKind LayoutKind { get; init; } = DockLayoutKind.OneByOne;

    public Guid? ActivePanelId { get; init; }

    public DockGridState? Grid { get; init; }

    public List<PanelState> Panels { get; init; } = [];

    [JsonIgnore]
    public List<PanelState> VisiblePanels
    {
        get
        {
            var visibleById = Panels.Where(panel => panel.IsVisible).ToDictionary(panel => panel.Id);
            if (Grid is null)
            {
                return Panels.Where(panel => panel.IsVisible).ToList();
            }

            var ordered = new DockGridService()
                .GetLeafPanelIds(Grid)
                .Where(visibleById.ContainsKey)
                .Select(id => visibleById[id])
                .ToList();

            return ordered.Count > 0 ? ordered : Panels.Where(panel => panel.IsVisible).ToList();
        }
    }

    public PanelState FindPanel(Guid panelId)
    {
        return Panels.First(panel => panel.Id == panelId);
    }
}
```

- [x] **Step 7: Initialize grid in default layouts**

Modify `DockLayoutService.CreateDefault` so the returned state includes:

```csharp
Grid = DockGridState.Single(panels[0].Id),
```

Inside `SetLayout`, build a grid from the visible panels for preset buttons by adding a private method:

```csharp
private static DockGridState CreatePresetGrid(IReadOnlyList<PanelState> visiblePanels, DockLayoutKind layoutKind)
{
    var gridService = new DockGridService();
    var grid = DockGridState.Single(visiblePanels[0].Id);

    if (visiblePanels.Count >= 2)
    {
        var direction = layoutKind == DockLayoutKind.TwoByOne ? DockDirection.Bottom : DockDirection.Right;
        grid = gridService.Split(grid, visiblePanels[0].Id, visiblePanels[1].Id, direction);
    }

    if (visiblePanels.Count >= 3)
    {
        grid = gridService.Split(grid, visiblePanels[1].Id, visiblePanels[2].Id, DockDirection.Bottom);
    }

    if (visiblePanels.Count >= 4)
    {
        grid = gridService.Split(grid, visiblePanels[2].Id, visiblePanels[3].Id, DockDirection.Right);
    }

    return grid;
}
```

Then assign:

```csharp
var visiblePanels = panels.Where(panel => panel.IsVisible).ToList();
var grid = CreatePresetGrid(visiblePanels, layoutKind);
return state with
{
    LayoutKind = layoutKind,
    ActivePanelId = activePanelId,
    Grid = grid,
    Panels = panels
};
```

- [x] **Step 8: Run tests and verify green**

Run:

```powershell
dotnet run --project tests\Waffle.Browse.Core.Tests\Waffle.Browse.Core.Tests.csproj
```

Expected: all tests pass, including existing visible panel and docking tests.

---

### Task 3: Commit Dock Drops From Preview Transactions

**Files:**
- Modify: `src/Waffle.Browse.Core/Docking/DockLayoutService.cs`
- Create: `tests/Waffle.Browse.Core.Tests/Docking/DockLayoutCommitTests.cs`
- Modify: `tests/Waffle.Browse.Core.Tests/Program.cs`

- [x] **Step 1: Register failing commit tests**

Modify `tests/Waffle.Browse.Core.Tests/Program.cs` by adding:

```csharp
("Committing split preview updates grid and panel state", DockLayoutCommitTests.CommittingSplitPreviewUpdatesGridAndPanelState),
("Committing move preview collapses empty source panel through grid", DockLayoutCommitTests.CommittingMovePreviewCollapsesEmptySourcePanelThroughGrid),
("Rejected preview does not change layout state", DockLayoutCommitTests.RejectedPreviewDoesNotChangeLayoutState),
```

- [x] **Step 2: Write failing commit tests**

Create `tests/Waffle.Browse.Core.Tests/Docking/DockLayoutCommitTests.cs`:

```csharp
using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.Core.Tests.Docking;

internal static class DockLayoutCommitTests
{
    public static void CommittingSplitPreviewUpdatesGridAndPanelState()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var sourcePanel = state.VisiblePanels[0];
        state = service.AddTab(state, sourcePanel.Id, @"C:\Split");
        var tabId = state.FindPanel(sourcePanel.Id).ActiveTab!.Id;

        var preview = new DockDropPreview(
            DockDropOperation.SplitPanel,
            sourcePanel.Id,
            DockDirection.Right,
            new DockRect(500, 0, 500, 800),
            true);

        var result = service.CommitDrop(state, new DockDragPayload(sourcePanel.Id, tabId), preview);

        TestAssert.True(result.Accepted, "Split preview should commit");
        TestAssert.Equal(DockLayoutKind.OneByTwo, result.State.LayoutKind, "Right split should derive 1x2");
        TestAssert.Equal(2, result.State.VisiblePanels.Count, "Split should create second visible panel");
        TestAssert.NotNull(result.State.Grid, "Committed layout should have grid state");
    }

    public static void CommittingMovePreviewCollapsesEmptySourcePanelThroughGrid()
    {
        var service = new DockLayoutService();
        var state = service.CreateDefault(@"C:\");
        var first = state.VisiblePanels[0];
        state = service.AddTab(state, first.Id, @"C:\Second");

        var split = service.CommitDrop(
            state,
            new DockDragPayload(first.Id, state.FindPanel(first.Id).ActiveTab!.Id),
            new DockDropPreview(DockDropOperation.SplitPanel, first.Id, DockDirection.Right, new DockRect(500, 0, 500, 800), true));

        state = split.State;
        var target = state.VisiblePanels[0];
        var source = state.VisiblePanels[1];
        var sourceTabId = state.FindPanel(source.Id).ActiveTab!.Id;

        var move = service.CommitDrop(
            state,
            new DockDragPayload(source.Id, sourceTabId),
            new DockDropPreview(DockDropOperation.MoveIntoPanel, target.Id, null, new DockRect(0, 0, 1000, 800), true));

        TestAssert.True(move.Accepted, "Move preview should commit");
        TestAssert.Equal(DockLayoutKind.OneByOne, move.State.LayoutKind, "Moving last source tab should collapse grid to 1x1");
        TestAssert.Equal(1, move.State.VisiblePanels.Count, "Only target panel should remain visible");
        TestAssert.False(move.State.FindPanel(source.Id).IsVisible, "Source panel should be hidden");
    }

    public static void RejectedPreviewDoesNotChangeLayoutState()
    {
        var service = new DockLayoutService();
        var state = service.SetVisiblePanelCount(service.CreateDefault(@"C:\"), 4);
        var source = state.VisiblePanels[0];
        var tabId = state.FindPanel(source.Id).ActiveTab!.Id;

        var result = service.CommitDrop(
            state,
            new DockDragPayload(source.Id, tabId),
            new DockDropPreview(DockDropOperation.SplitPanel, source.Id, DockDirection.Right, new DockRect(500, 0, 500, 800), false, "The layout already has four visible panels."));

        TestAssert.False(result.Accepted, "Rejected preview should not commit");
        TestAssert.Equal(4, result.State.VisiblePanels.Count, "Rejected preview should preserve visible panel count");
        TestAssert.Equal(state.LayoutKind, result.State.LayoutKind, "Rejected preview should preserve layout kind");
    }
}
```

- [x] **Step 3: Run tests and verify red**

Run:

```powershell
dotnet run --project tests\Waffle.Browse.Core.Tests\Waffle.Browse.Core.Tests.csproj
```

Expected: compile fails because `DockLayoutService.CommitDrop` is missing.

- [x] **Step 4: Implement `CommitDrop`**

Add this public method to `src/Waffle.Browse.Core/Docking/DockLayoutService.cs`:

```csharp
public DockOperationResult CommitDrop(DockLayoutState state, DockDragPayload payload, DockDropPreview preview)
{
    if (!preview.Accepted)
    {
        return new DockOperationResult(false, state, preview.RejectionReason);
    }

    return preview.Operation switch
    {
        DockDropOperation.MoveIntoPanel => MoveTabToPanel(state, payload.SourcePanelId, payload.TabId, preview.TargetPanelId),
        DockDropOperation.SplitPanel when preview.SplitDirection is { } direction => SplitPanelWithTab(state, payload.SourcePanelId, payload.TabId, preview.TargetPanelId, direction),
        DockDropOperation.ReorderTab => new DockOperationResult(false, state, "Tab reorder commit requires a target index."),
        _ => new DockOperationResult(false, state, "No drop operation is available.")
    };
}
```

- [x] **Step 5: Replace split path with grid-backed method**

In `DockLayoutService`, add:

```csharp
private static DockOperationResult SplitPanelWithTab(
    DockLayoutState state,
    Guid sourcePanelId,
    Guid tabId,
    Guid targetPanelId,
    DockDirection direction)
{
    if (state.VisiblePanels.Count >= MaxPanels)
    {
        return new DockOperationResult(false, state, "The layout already has four visible panels.");
    }

    var sourcePanel = state.FindPanel(sourcePanelId);
    var movingTab = sourcePanel.Tabs.FirstOrDefault(tab => tab.Id == tabId);
    if (movingTab is null)
    {
        return new DockOperationResult(false, state, "The source tab does not exist.");
    }

    var destinationPanel = state.Panels.FirstOrDefault(panel => !panel.IsVisible);
    if (destinationPanel is null)
    {
        return new DockOperationResult(false, state, "No hidden panel is available.");
    }

    var withoutTab = RemoveTabFromPanel(state, sourcePanelId, tabId, keepReplacementTab: true);
    var updatedDestination = destinationPanel with
    {
        IsVisible = true,
        Tabs = [movingTab],
        ActiveTabId = movingTab.Id
    };
    var withDestination = ReplacePanel(withoutTab, updatedDestination);
    var gridService = new DockGridService();
    var currentGrid = withDestination.Grid ?? DockGridState.Single(withDestination.VisiblePanels[0].Id);
    var grid = gridService.Split(currentGrid, targetPanelId, updatedDestination.Id, direction);
    var layoutKind = gridService.GetLayoutKind(grid);

    return new DockOperationResult(true, withDestination with
    {
        Grid = grid,
        LayoutKind = layoutKind,
        ActivePanelId = updatedDestination.Id
    });
}
```

- [x] **Step 6: Make old `DockTab` call preview commit**

Replace the body of `DockTab` with:

```csharp
public DockOperationResult DockTab(DockLayoutState state, Guid sourcePanelId, Guid tabId, Guid targetPanelId, DockDirection direction)
{
    var operation = direction == DockDirection.Center
        ? DockDropOperation.MoveIntoPanel
        : DockDropOperation.SplitPanel;
    var preview = new DockDropPreview(
        operation,
        targetPanelId,
        direction == DockDirection.Center ? null : direction,
        new DockRect(0, 0, 1, 1),
        direction == DockDirection.Center || state.VisiblePanels.Count < MaxPanels,
        state.VisiblePanels.Count >= MaxPanels && direction != DockDirection.Center
            ? "The layout already has four visible panels."
            : null);

    return CommitDrop(state, new DockDragPayload(sourcePanelId, tabId), preview);
}
```

- [x] **Step 7: Update `MoveTabToPanel` to update grid**

After hiding an empty source panel, remove the hidden panel from grid:

```csharp
var gridService = new DockGridService();
var grid = withoutTab.Grid;
if (grid is not null && !withoutTab.FindPanel(sourcePanelId).IsVisible)
{
    grid = gridService.RemoveLeaf(grid, sourcePanelId);
}
var withMovedTab = ReplacePanel(withoutTab, updatedTarget);
var stateWithGrid = withMovedTab with
{
    Grid = grid,
    ActivePanelId = targetPanelId
};
return new DockOperationResult(true, NormalizeVisibleLayout(stateWithGrid));
```

Update `NormalizeVisibleLayout` so it derives layout kind from grid when grid exists:

```csharp
private static DockLayoutState NormalizeVisibleLayout(DockLayoutState state)
{
    var gridService = new DockGridService();
    var layoutKind = state.Grid is not null
        ? gridService.GetLayoutKind(state.Grid)
        : state.VisiblePanels.Count switch
        {
            <= 1 => DockLayoutKind.OneByOne,
            2 when state.LayoutKind == DockLayoutKind.TwoByOne => DockLayoutKind.TwoByOne,
            2 => DockLayoutKind.OneByTwo,
            3 => DockLayoutKind.ThreePanelPrimaryLeft,
            _ => DockLayoutKind.TwoByTwo
        };
    var activePanelId = state.VisiblePanels.Any(panel => panel.Id == state.ActivePanelId)
        ? state.ActivePanelId
        : state.VisiblePanels.FirstOrDefault()?.Id;

    return state with
    {
        LayoutKind = layoutKind,
        ActivePanelId = activePanelId
    };
}
```

- [x] **Step 8: Run tests and verify green**

Run:

```powershell
dotnet run --project tests\Waffle.Browse.Core.Tests\Waffle.Browse.Core.Tests.csproj
```

Expected: all tests pass, including old `DockTab` tests and new `CommitDrop` tests.

---

### Task 4: Persist and Migrate Grid State

**Files:**
- Modify: `src/Waffle.Browse.Core/Persistence/DockLayoutStore.cs`
- Modify: `tests/Waffle.Browse.Core.Tests/Persistence/DockLayoutStoreTests.cs`

- [x] **Step 1: Add failing migration test**

Append this test method to `DockLayoutStoreTests`:

```csharp
public static void RestoreCreatesGridWhenSavedStateHasOnlyVisiblePanels()
{
    var service = new DockLayoutService();
    var legacy = service.SetVisiblePanelCount(service.CreateDefault(@"C:\Valid"), 2) with
    {
        Grid = null
    };

    var normalized = DockLayoutStore.NormalizeForRestore(legacy, @"C:\Fallback", path => true);

    TestAssert.NotNull(normalized.Grid, "Restore should create grid for legacy layout state");
    TestAssert.Equal(2, normalized.VisiblePanels.Count, "Visible panel count should survive migration");
    TestAssert.Equal(DockLayoutKind.OneByTwo, normalized.LayoutKind, "Migrated two-panel layout should preserve layout kind");
}
```

Register it in `Program.cs`:

```csharp
("Restore creates grid for legacy visible-panel state", DockLayoutStoreTests.RestoreCreatesGridWhenSavedStateHasOnlyVisiblePanels),
```

- [x] **Step 2: Run tests and verify red if migration is missing**

Run:

```powershell
dotnet run --project tests\Waffle.Browse.Core.Tests\Waffle.Browse.Core.Tests.csproj
```

Expected: the new migration test fails because `Grid` remains null after restore.

- [x] **Step 3: Normalize grid during restore**

In `DockLayoutStore.NormalizeForRestore`, after path normalization and before return, ensure grid:

```csharp
var normalized = new DockLayoutService().Normalize(state with { Panels = panels }, fallbackPath);
return normalized.Grid is null
    ? normalized with { Grid = CreateGridFromVisiblePanels(normalized) }
    : normalized;
```

Add private helper in `DockLayoutStore`:

```csharp
private static DockGridState CreateGridFromVisiblePanels(DockLayoutState state)
{
    var visiblePanels = state.Panels.Where(panel => panel.IsVisible).ToList();
    if (visiblePanels.Count == 0)
    {
        var firstPanel = state.Panels.First();
        return DockGridState.Single(firstPanel.Id);
    }

    var service = new DockGridService();
    var grid = DockGridState.Single(visiblePanels[0].Id);

    if (visiblePanels.Count >= 2)
    {
        grid = service.Split(grid, visiblePanels[0].Id, visiblePanels[1].Id, state.LayoutKind == DockLayoutKind.TwoByOne ? DockDirection.Bottom : DockDirection.Right);
    }

    if (visiblePanels.Count >= 3)
    {
        grid = service.Split(grid, visiblePanels[1].Id, visiblePanels[2].Id, DockDirection.Bottom);
    }

    if (visiblePanels.Count >= 4)
    {
        grid = service.Split(grid, visiblePanels[2].Id, visiblePanels[3].Id, DockDirection.Right);
    }

    return grid;
}
```

- [x] **Step 4: Run tests and verify green**

Run:

```powershell
dotnet run --project tests\Waffle.Browse.Core.Tests\Waffle.Browse.Core.Tests.csproj
```

Expected: all persistence and docking tests pass.

---

### Task 5: Replace Panel-Local 3x3 Overlay With Workspace Preview Rectangle

**Files:**
- Create: `src/Waffle.Browse.App/Docking/DockPreviewOverlay.xaml`
- Create: `src/Waffle.Browse.App/Docking/DockPreviewOverlay.xaml.cs`
- Modify: `src/Waffle.Browse.App/MainWindow.xaml`
- Modify: `src/Waffle.Browse.App/MainWindow.xaml.cs`
- Modify: `src/Waffle.Browse.App/Controls/ExplorerPanelControl.xaml`
- Modify: `src/Waffle.Browse.App/Controls/ExplorerPanelControl.xaml.cs`

- [x] **Step 1: Create preview overlay control**

Create `src/Waffle.Browse.App/Docking/DockPreviewOverlay.xaml`:

```xml
<UserControl x:Class="Waffle.Browse.App.Docking.DockPreviewOverlay"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             IsHitTestVisible="False"
             Visibility="Collapsed">
    <Canvas x:Name="RootCanvas">
        <Border x:Name="PreviewBorder"
                Background="#332F6FED"
                BorderBrush="#992F6FED"
                BorderThickness="2"
                CornerRadius="2" />
    </Canvas>
</UserControl>
```

Create `src/Waffle.Browse.App/Docking/DockPreviewOverlay.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using Waffle.Browse.Core.Docking;

namespace Waffle.Browse.App.Docking;

public partial class DockPreviewOverlay : UserControl
{
    public DockPreviewOverlay()
    {
        InitializeComponent();
    }

    public void ShowPreview(DockDropPreview preview)
    {
        Visibility = Visibility.Visible;
        PreviewBorder.BorderBrush = preview.Accepted
            ? System.Windows.Media.Brushes.DodgerBlue
            : System.Windows.Media.Brushes.IndianRed;
        Canvas.SetLeft(PreviewBorder, preview.PreviewBounds.X);
        Canvas.SetTop(PreviewBorder, preview.PreviewBounds.Y);
        PreviewBorder.Width = preview.PreviewBounds.Width;
        PreviewBorder.Height = preview.PreviewBounds.Height;
    }

    public void HidePreview()
    {
        Visibility = Visibility.Collapsed;
    }
}
```

- [x] **Step 2: Wrap workspace with overlay layer**

Modify `src/Waffle.Browse.App/MainWindow.xaml` by replacing:

```xml
<Grid x:Name="WorkspaceGrid"
      Margin="6" />
```

with:

```xml
<Grid x:Name="WorkspaceRoot"
      Margin="6"
      xmlns:docking="clr-namespace:Waffle.Browse.App.Docking">
    <Grid x:Name="WorkspaceGrid" />
    <docking:DockPreviewOverlay x:Name="DockPreviewOverlay" />
</Grid>
```

- [x] **Step 3: Remove 3x3 overlay markup**

In `src/Waffle.Browse.App/Controls/ExplorerPanelControl.xaml`, delete the entire grid whose name is `DockOverlay` and leave only:

```xml
<Grid Grid.Row="2"
      Background="#101820"
      x:Name="ShellHostArea"
      AllowDrop="True"
      DragEnter="OnPanelDragEnter"
      DragOver="OnPanelDragOver"
      Drop="OnPanelDrop">
    <ContentControl x:Name="ShellHostContainer" />
</Grid>
```

- [x] **Step 4: Replace control events with payload and pointer events**

In `ExplorerPanelControl.xaml.cs`, replace `SetDockOverlayVisible`, `DirectionFromPoint`, and `TabDockRequested` usage with these events:

```csharp
public event EventHandler<TabDragStartedEventArgs>? TabDragStarted;
public event EventHandler<TabDragPointerEventArgs>? TabDragOverPanel;
public event EventHandler<TabDragPointerEventArgs>? TabDroppedOnPanel;
public event EventHandler? TabDragCompleted;
```

Add event args:

```csharp
public sealed record TabDragStartedEventArgs(DockDragPayload Payload);

public sealed record TabDragPointerEventArgs(
    DockDragPayload Payload,
    Guid TargetPanelId,
    Point PointerInPanel,
    FrameworkElement TargetElement);
```

In `OnTabPreviewMouseMove`, create core payload and start drag:

```csharp
var payload = new DockDragPayload(Panel.Id, tab.Id);
var data = new DataObject();
data.SetData(TabDragPayload.Format, new TabDragPayload(payload.SourcePanelId, payload.TabId));
TabDragStarted?.Invoke(this, new TabDragStartedEventArgs(payload));
```

In `OnPanelDragOver`, emit pointer updates:

```csharp
if (e.Data.GetData(TabDragPayload.Format) is TabDragPayload payload)
{
    TabDragOverPanel?.Invoke(this, new TabDragPointerEventArgs(
        new DockDragPayload(payload.SourcePanelId, payload.TabId),
        Panel.Id,
        e.GetPosition(ShellHostArea),
        ShellHostArea));
    e.Effects = DragDropEffects.Move;
    e.Handled = true;
}
```

In `OnPanelDrop`, emit drop event:

```csharp
if (e.Data.GetData(TabDragPayload.Format) is TabDragPayload payload)
{
    TabDroppedOnPanel?.Invoke(this, new TabDragPointerEventArgs(
        new DockDragPayload(payload.SourcePanelId, payload.TabId),
        Panel.Id,
        e.GetPosition(ShellHostArea),
        ShellHostArea));
    e.Handled = true;
}
```

- [x] **Step 5: Add preview calculation in MainWindow**

In `MainWindow.xaml.cs`, add fields:

```csharp
private readonly DockDropHitTester dropHitTester = new();
private DockDragPayload? activeDragPayload;
private DockDropPreview? activeDropPreview;
```

Wire new events when creating each `ExplorerPanelControl`:

```csharp
control.TabDragStarted += OnTabDragStarted;
control.TabDragOverPanel += OnTabDragOverPanel;
control.TabDroppedOnPanel += OnTabDroppedOnPanel;
control.TabDragCompleted += OnTabDragCompleted;
```

Add handlers:

```csharp
private void OnTabDragStarted(object? sender, TabDragStartedEventArgs e)
{
    activeDragPayload = e.Payload;
}

private void OnTabDragOverPanel(object? sender, TabDragPointerEventArgs e)
{
    activeDropPreview = CreateDropPreview(e);
    DockPreviewOverlay.ShowPreview(activeDropPreview);
}

private void OnTabDroppedOnPanel(object? sender, TabDragPointerEventArgs e)
{
    activeDropPreview = CreateDropPreview(e);
    var result = layoutService.CommitDrop(layoutState, e.Payload, activeDropPreview);
    DockPreviewOverlay.HidePreview();
    activeDragPayload = null;
    activeDropPreview = null;

    if (!result.Accepted)
    {
        SetStatus(result.Reason ?? "Docking was not accepted.");
        return;
    }

    ApplyLayout(result.State, "Docked tab.");
}

private void OnTabDragCompleted(object? sender, EventArgs e)
{
    activeDragPayload = null;
    activeDropPreview = null;
    DockPreviewOverlay.HidePreview();
}

private DockDropPreview CreateDropPreview(TabDragPointerEventArgs e)
{
    var topLeft = e.TargetElement.TranslatePoint(new Point(0, 0), WorkspaceRoot);
    var pointer = e.TargetElement.TranslatePoint(e.PointerInPanel, WorkspaceRoot);
    var bounds = new DockRect(topLeft.X, topLeft.Y, e.TargetElement.ActualWidth, e.TargetElement.ActualHeight);
    return dropHitTester.HitTest(
        bounds,
        new DockPoint(pointer.X, pointer.Y),
        e.TargetPanelId,
        e.Payload,
        new DockDropOptions(CurrentVisiblePanelCount: layoutState.VisiblePanels.Count));
}
```

- [x] **Step 6: Build to verify XAML and event wiring**

Run:

```powershell
dotnet build waffle-browse.slnx
```

Expected: build succeeds with 0 errors. Fix namespace or event signature mistakes before continuing.

---

### Task 6: Render From DockGridState Instead of Fixed Preset Indexes

**Files:**
- Modify: `src/Waffle.Browse.App/MainWindow.xaml.cs`
- Modify: `src/Waffle.Browse.Core/Docking/DockGridService.cs`

- [x] **Step 1: Add grid placement projection**

Add this method to `DockGridService`:

```csharp
public IReadOnlyDictionary<Guid, DockRect> GetNormalizedLeafBounds(DockGridState grid)
{
    var bounds = new Dictionary<Guid, DockRect>();
    AssignBounds(grid.Root, new DockRect(0, 0, 1, 1), bounds);
    return bounds;
}

private static void AssignBounds(DockNode node, DockRect rect, Dictionary<Guid, DockRect> bounds)
{
    switch (node)
    {
        case DockLeaf leaf:
            bounds[leaf.PanelId] = rect;
            break;
        case DockSplit { Orientation: DockOrientation.Horizontal } split:
            AssignBounds(split.First, new DockRect(rect.X, rect.Y, rect.Width * split.Ratio, rect.Height), bounds);
            AssignBounds(split.Second, new DockRect(rect.X + rect.Width * split.Ratio, rect.Y, rect.Width * (1 - split.Ratio), rect.Height), bounds);
            break;
        case DockSplit { Orientation: DockOrientation.Vertical } split:
            AssignBounds(split.First, new DockRect(rect.X, rect.Y, rect.Width, rect.Height * split.Ratio), bounds);
            AssignBounds(split.Second, new DockRect(rect.X, rect.Y + rect.Height * split.Ratio, rect.Width, rect.Height * (1 - split.Ratio)), bounds);
            break;
    }
}
```

- [x] **Step 2: Replace fixed row/column placement in MainWindow**

In `MainWindow.xaml.cs`, keep `CreateRows`, `CreateColumns`, and `GetPlacement` temporarily for preset fallback, but change `RenderLayout` to use normalized grid bounds when `layoutState.Grid` exists:

```csharp
private void RenderLayout()
{
    WorkspaceGrid.Children.Clear();
    WorkspaceGrid.RowDefinitions.Clear();
    WorkspaceGrid.ColumnDefinitions.Clear();

    WorkspaceGrid.RowDefinitions.Add(new RowDefinition());
    WorkspaceGrid.ColumnDefinitions.Add(new ColumnDefinition());

    if (layoutState.Grid is null)
    {
        RenderPresetLayout();
        return;
    }

    var canvas = new Canvas();
    WorkspaceGrid.Children.Add(canvas);
    WorkspaceGrid.SizeChanged += (_, _) => ArrangeGridPanels(canvas);
    ArrangeGridPanels(canvas);
}

private void ArrangeGridPanels(Canvas canvas)
{
    if (layoutState.Grid is null)
    {
        return;
    }

    canvas.Children.Clear();
    var bounds = new DockGridService().GetNormalizedLeafBounds(layoutState.Grid);
    foreach (var panel in layoutState.VisiblePanels)
    {
        if (!bounds.TryGetValue(panel.Id, out var normalized))
        {
            continue;
        }

        var control = CreatePanelControl(panel);
        Canvas.SetLeft(control, normalized.X * WorkspaceGrid.ActualWidth);
        Canvas.SetTop(control, normalized.Y * WorkspaceGrid.ActualHeight);
        control.Width = normalized.Width * WorkspaceGrid.ActualWidth;
        control.Height = normalized.Height * WorkspaceGrid.ActualHeight;
        canvas.Children.Add(control);
    }
}
```

Add a `CreatePanelControl` helper containing the existing event wiring:

```csharp
private ExplorerPanelControl CreatePanelControl(PanelState panel)
{
    var control = new ExplorerPanelControl(panel, layoutState.ActivePanelId == panel.Id);
    control.NavigationRequested += OnPanelNavigationRequested;
    control.PathSubmitted += OnPanelPathSubmitted;
    control.ShellPathChanged += OnPanelShellPathChanged;
    control.TabAddRequested += OnTabAddRequested;
    control.TabCloseRequested += OnTabCloseRequested;
    control.TabSelected += OnTabSelected;
    control.TabDragStarted += OnTabDragStarted;
    control.TabDragOverPanel += OnTabDragOverPanel;
    control.TabDroppedOnPanel += OnTabDroppedOnPanel;
    control.TabDragCompleted += OnTabDragCompleted;
    return control;
}
```

If Canvas sizing causes unacceptable stretch or overlap in smoke testing, replace the Canvas projection with a recursive WPF `Grid` builder in the same task. Do not keep both renderers active.

- [x] **Step 3: Build and smoke test**

Run:

```powershell
dotnet build waffle-browse.slnx
```

Expected: build succeeds.

Run:

```powershell
$out = Join-Path $env:TEMP ('waffle-browse-' + [guid]::NewGuid().ToString('N') + '.out.log')
$err = Join-Path $env:TEMP ('waffle-browse-' + [guid]::NewGuid().ToString('N') + '.err.log')
$p = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--project','src\Waffle.Browse.App\Waffle.Browse.App.csproj') -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
Start-Sleep -Seconds 5
if ($p.HasExited) { Get-Content $err; exit 1 }
$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 1
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
```

Expected: app stays alive for 5 seconds and stderr is empty.

---

### Task 7: Final Verification and Documentation Update

**Files:**
- Modify: `docs/vscode-tab-docking-review.md`
- Modify: `docs/superpowers/plans/2026-05-24-vscode-style-docking-refactor.md`

- [x] **Step 1: Run full core test suite**

Run:

```powershell
dotnet run --project tests\Waffle.Browse.Core.Tests\Waffle.Browse.Core.Tests.csproj
```

Expected: every registered test prints `PASS`, and the final line reports all tests passed.

- [x] **Step 2: Run build**

Run:

```powershell
dotnet build waffle-browse.slnx
```

Expected: build succeeds with 0 warnings and 0 errors.

- [x] **Step 3: Run WPF startup smoke test**

Run the smoke script from Task 6 Step 3.

Expected: app stays alive for 5 seconds without immediate startup crash.

- [x] **Step 4: Update review document with implementation status**

Append this section to `docs/vscode-tab-docking-review.md`:

```markdown
## 적용 상태

- `DockDropHitTester`로 드래그 좌표 판정을 UI에서 분리했다.
- `DockGridState`로 visible panel layout을 tree 형태로 저장한다.
- WPF 드래그 중에는 3x3 텍스트 overlay 대신 workspace-level preview rectangle을 표시한다.
- drop 시점에는 `DockDropPreview`를 `DockLayoutService.CommitDrop`으로 commit한다.
```

- [x] **Step 5: Mark this plan checklist**

Mark completed steps in this plan file with `[x]` after each task is implemented and verified.

---

## Self-Review

Spec coverage:

- VS Code style grid tree layout: covered by Tasks 2, 3, and 6.
- Hit-test separation from UI: covered by Task 1.
- Preview rectangle during drag: covered by Task 5.
- Drop transaction commit: covered by Task 3.
- Persistence migration: covered by Task 4.
- Waffle Browse Shell View stability: covered by Task 5 and Task 6 smoke tests.

Execution note:

- The current workspace is not a Git repository. Commit steps are intentionally omitted. If this folder is later initialized as Git, commit after each task with focused messages.
