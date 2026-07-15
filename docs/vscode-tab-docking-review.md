# VS Code 스타일 탭 드래그 도킹 리뷰

작성일: 2026-05-24

## 목적

현재 Waffle Browse의 탭 드래그 도킹은 `Top`, `Left`, `Move`, `Right`, `Bottom` 같은 3x3 고정 구역을 화면에 직접 표시하고, 마우스 좌표를 즉시 `DockDirection`으로 바꾸는 방식이다. 이 방식은 초기 PoC로는 빠르지만, VS Code 같은 앱의 도킹 경험과 비교하면 다음 문제가 있다.

- 레이아웃 모델이 UI 표시 방식에 묶여 있다.
- 드래그 중 프리뷰가 실제 결과 레이아웃과 정밀하게 대응하지 않는다.
- 1x1, 1x2, 2x1, 2x2가 상태 전이 결과라기보다 enum 분기처럼 취급된다.
- `IExplorerBrowser` 같은 native HWND 위에서 드롭을 받기 위해 오버레이가 필요하지만, 오버레이 자체가 도킹 모델을 대신하고 있다.

이 문서는 VS Code의 공식 문서와 공개 소스 기준으로 탭 드래그 도킹을 어떻게 나누어 처리하는지 정리하고, Waffle Browse에 적용할 구조를 제안한다.

## 참고한 1차 자료

- VS Code 공식 문서, Custom Layout: editor groups, grid layout, floating window 동작  
  <https://code.visualstudio.com/docs/configure/custom-layout>
- VS Code 공식 문서, User Interface: tab ordering, grid editor layout, drag-and-drop split  
  <https://code.visualstudio.com/docs/getstarted/userinterface>
- VS Code Theme Color reference: editor group/tab drag feedback color  
  <https://code.visualstudio.com/api/references/theme-color>
- VS Code source, editor drop target  
  <https://github.com/microsoft/vscode/blob/main/src/vs/workbench/browser/parts/editor/editorDropTarget.ts>
- VS Code source, editor groups service contract  
  <https://github.com/microsoft/vscode/blob/main/src/vs/workbench/services/editor/common/editorGroupsService.ts>
- VS Code source, serializable grid  
  <https://github.com/microsoft/vscode/blob/main/src/vs/base/browser/ui/grid/grid.ts>
- VS Code source, editor part/grid persistence  
  <https://github.com/microsoft/vscode/blob/main/src/vs/workbench/browser/parts/editor/editorPart.ts>
- VS Code source, drop overlay CSS  
  <https://github.com/microsoft/vscode/blob/main/src/vs/workbench/browser/parts/editor/media/editordroptarget.css>

## VS Code의 사용자 관점 동작

### 1. 기본 단위는 "탭"과 "에디터 그룹"이다

VS Code 공식 Theme Color 문서는 Editor Group을 editor의 컨테이너, Tab을 editor의 컨테이너로 설명한다. 즉 사용자는 탭을 보지만, 레이아웃은 탭 하나가 아니라 "editor group"이라는 패널 단위로 계산된다.

사용자 경험은 다음처럼 나뉜다.

- 같은 그룹 안에서 탭을 드래그하면 탭 순서가 바뀐다.
- 다른 그룹 중앙으로 탭을 드래그하면 해당 그룹으로 탭이 이동하거나 복사된다.
- 그룹의 좌/우/상/하 가장자리로 탭을 드래그하면 새 그룹이 생기고 기존 그룹이 분할된다.
- 마지막 탭이 빠져나간 그룹은 설정에 따라 닫힌다. 기본값은 빈 그룹 닫기다.
- 그룹 사이 sash를 잡고 크기를 조절할 수 있다.

### 2. 1x1, 1x2, 2x1, 2x2는 고정 버튼이 아니라 grid 상태의 결과다

공식 문서는 grid layout에서 여러 row/column editor group을 만들 수 있고, View > Editor Layout 메뉴에 Two Columns, Three Columns, Grid (2x2) 같은 프리셋이 있다고 설명한다. 그러나 드래그 동작 자체는 "2x2 enum으로 바꾼다"가 아니라, 현재 target group 주변에 새 group을 추가하거나 기존 group을 병합/이동하는 식으로 처리된다.

VS Code 소스의 `SerializableGrid`는 leaf와 branch를 가진 grid tree를 직렬화한다. `addView`, `removeView`, `moveView`, `serialize`, `deserialize`가 핵심이다. 따라서 레이아웃은 다음 개념에 가깝다.

```text
Grid
└─ Branch(orientation)
   ├─ Leaf(editor group)
   └─ Branch(opposite orientation)
      ├─ Leaf(editor group)
      └─ Leaf(editor group)
```

이 구조에서는 1x1, 1x2, 2x1, 2x2가 별도 구현이 아니라 tree의 leaf 개수와 split orientation의 표현이다.

## VS Code 내부 처리 흐름

### 1. 드래그 감지는 editor area 레벨에서 시작한다

VS Code의 `EditorDropTarget`은 전체 editor container에 `DRAG_ENTER`, `DRAG_LEAVE`, `DRAG_END` listener를 건다. 드래그가 들어오면 transfer data가 VS Code 내부 editor/group인지, 외부 file/resource인지 검증한다.

핵심 포인트:

- drop target은 개별 탭 컨트롤에만 붙지 않는다.
- 현재 마우스가 올라간 editor group을 찾아 그 group 위에 overlay를 만든다.
- overlay는 transient UI다. 실제 상태 변경은 drop 시점의 operation으로 결정된다.

Waffle Browse 적용점: `ExplorerPanelControl`이 직접 모든 판단을 하는 대신, `WorkspaceDockController` 같은 상위 컨트롤이 드래그 세션과 target panel 탐지를 관리해야 한다.

### 2. DropOverlay는 "도킹 상태"가 아니라 "현재 가능한 drop operation 프리뷰"다

VS Code의 `DropOverlay`는 드래그 중에만 생성된다. overlay는 target editor group 내부에 붙고, tab header가 있는 경우 editor content 영역 아래에 표시된다. 즉 탭 영역을 가리는 3x3 메뉴가 아니라, 실제 split 결과가 생길 content region에 반투명 preview rectangle을 그린다.

드래그 중 overlay는 다음 중 하나만 표현한다.

| 마우스 위치 | 의미 | 시각 표현 |
| --- | --- | --- |
| 중앙 | 기존 그룹에 merge/move | target group 전체를 반투명 highlight |
| 왼쪽 edge | 왼쪽에 새 group split | target group 왼쪽 절반 highlight |
| 오른쪽 edge | 오른쪽에 새 group split | target group 오른쪽 절반 highlight |
| 위쪽 edge | 위쪽에 새 group split | target group 위쪽 절반 highlight |
| 아래쪽 edge | 아래쪽에 새 group split | target group 아래쪽 절반 highlight |

중요한 점은 "Left", "Right" 같은 텍스트 라벨을 계속 보여주지 않는다는 것이다. 사용자는 반투명 면적이 움직이는 것을 보고 drop 결과를 예측한다.

### 3. hit-test는 단순 3x3 격자가 아니다

VS Code의 `positionOverlay`는 다음 정책을 쓴다.

- editor tab을 드래그할 때 edge threshold는 폭/높이의 약 10%다.
- editor group 전체를 드래그할 때는 preferred split 방향의 hit zone이 더 넓다.
- `openSideBySideDirection` 설정이 `right`이면 좌/우 split을 더 쉽게 선택하게 만든다.
- `openSideBySideDirection` 설정이 `down`이면 상/하 split을 더 쉽게 선택하게 만든다.
- 중앙 non-edge 영역은 split이 아니라 merge/drop into existing group이다.
- split preview는 항상 target group의 절반 영역으로 그린다.

이 차이가 크다. 현재 Waffle Browse는 중앙 1/3을 center로 두고, 나머지는 가장 가까운 edge를 고르는 방식이다. VS Code는 "edge activation threshold"와 "preferred orientation"을 분리한다.

권장 hit-test 모델:

```csharp
public sealed record DockDropPreview(
    DockDropOperation Operation,
    Guid TargetPanelId,
    DockDirection? SplitDirection,
    Rect PreviewBounds,
    bool Accepted,
    string? RejectionReason);

public enum DockDropOperation
{
    None,
    MoveIntoPanel,
    SplitPanel,
    ReorderTab
}
```

UI는 이 결과의 `PreviewBounds`만 그린다. 레이아웃 상태 변경은 `CommitDrop(preview)`에서만 수행한다.

### 4. drop 시점에는 operation을 commit한다

VS Code `handleDrop`은 drag data 유형에 따라 다르게 처리한다.

- editor group drag:
  - edge drop이면 `moveGroup` 또는 `copyGroup`
  - center drop이면 `mergeGroup`
- editor tab drag:
  - edge drop이면 target group을 만들고 editor를 move/copy
  - center drop이면 기존 target group으로 editor를 move/copy
  - source group의 마지막 editor를 edge split하는 경우, 빈 그룹을 만들지 않도록 group move 최적화를 한다.
- 외부 파일/resource drop:
  - resource drop handler가 target group을 보장하고 파일을 연다.

Waffle Browse에서는 파일 탐색 탭만 다루므로 VS Code 전체 복잡도를 그대로 가져올 필요는 없다. 다만 다음 분리는 가져와야 한다.

- `DragPayload`: 무엇을 끌고 있는가
- `DropPreview`: 현재 좌표가 어떤 operation을 의미하는가
- `DockLayoutTransaction`: drop을 실제 상태 변경으로 적용
- `DockGridState`: 적용 후 레이아웃 구조

## 드래그 중 화면이 어떻게 보이는가

### 1. 반투명 overlay가 target group 위에 뜬다

VS Code CSS는 editor drop overlay container와 내부 indicator를 따로 둔다. indicator는 absolute 영역으로 움직이며, theme color `editorGroup.dropBackground`를 사용한다. Theme Color reference는 이 색을 "dragging editors around" 시 background로 설명한다.

Waffle Browse에서는 현재 `DockOverlay`가 3x3 전체 grid를 표시한다. VS Code식으로 바꾸려면 다음이 낫다.

- 하나의 반투명 rectangle만 표시한다.
- rectangle 위치/크기는 `DockDropPreview.PreviewBounds`에서 온다.
- 중앙 drop이면 target panel content 전체.
- edge drop이면 target panel content의 절반.
- max 4 panel 초과 등 불가 상태면 rectangle을 붉은 border 또는 forbidden cursor로 표시한다.

### 2. overlay 이동은 짧고 부드럽다

VS Code CSS는 overlay의 `top`, `left`, `width`, `height`, `opacity`에 짧은 transition을 준다. 즉 드래그 중 영역이 갑자기 텍스트 메뉴처럼 바뀌는 게 아니라, preview 면적이 따라 움직인다.

Waffle Browse에서는 WPF `AdornerLayer`나 workspace-level overlay canvas를 쓰면 비슷하게 만들 수 있다.

권장 WPF 구조:

```text
MainWindow
└─ WorkspaceRootGrid
   ├─ PanelGrid
   │  ├─ ExplorerPanelControl
   │  └─ ExplorerPanelControl
   └─ DockPreviewOverlayCanvas
      └─ DockPreviewRectangle
```

`IExplorerBrowser`는 native HWND라 일반 WPF element 위아래 문제가 생길 수 있다. 그래서 panel 내부에 overlay를 박아두기보다 workspace 최상단에 별도 overlay layer를 두는 편이 안전하다. 필요하면 투명 child window나 `Popup`도 후보가 된다.

### 3. tab reorder와 panel split feedback은 다르다

VS Code Theme Color reference에는 `tab.dragAndDropBorder`가 따로 있다. 이는 탭 사이에 삽입될 수 있음을 나타내는 border다. 반면 editor group split은 `editorGroup.dropBackground`가 담당한다.

Waffle Browse도 두 가지 feedback을 나눠야 한다.

- 탭 바 내부 reorder: 탭 사이 insertion line
- 패널 본문/edge split: panel content 위 preview rectangle

둘을 같은 3x3 overlay로 처리하면 UX가 거칠어진다.

## 현재 Waffle Browse 구현과의 차이

| 항목 | 현재 Waffle Browse | VS Code식 접근 |
| --- | --- | --- |
| 레이아웃 모델 | `DockLayoutKind` enum 중심 | grid tree 중심, enum은 파생값 |
| 드래그 hit-test | panel control 내부 좌표 즉시 direction 변환 | 별도 drop controller가 preview operation 계산 |
| 드래그 UI | 3x3 라벨 overlay | 하나의 반투명 preview rectangle |
| edge threshold | 중앙 1/3 제외 후 nearest edge | edge activation threshold + preferred orientation |
| 빈 패널 처리 | 최근 보강됨: center move 후 hide/collapse | `closeEmptyGroups` 정책으로 일관 처리 |
| drop commit | UI event가 바로 `DockLayoutService.DockTab` 호출 | preview operation을 commit transaction으로 적용 |
| native HWND 대응 | panel 내부 overlay | workspace-level overlay 권장 |

## Waffle Browse에 권장하는 구조

### 1. `DockLayoutKind`를 핵심 모델에서 내리고 `DockGridState`를 핵심으로 둔다

현재 enum:

```csharp
public enum DockLayoutKind
{
    OneByOne,
    OneByTwo,
    TwoByOne,
    ThreePanelPrimaryLeft,
    TwoByTwo
}
```

이 enum은 toolbar preset 표시나 저장된 layout 요약에는 쓸 수 있다. 하지만 drag/drop의 핵심 상태가 되면 새 전이를 추가할 때마다 분기가 늘어난다.

권장 모델:

```csharp
public abstract record DockNode;

public sealed record DockLeaf(Guid PanelId) : DockNode;

public sealed record DockSplit(
    DockOrientation Orientation,
    DockNode First,
    DockNode Second,
    double Ratio) : DockNode;

public enum DockOrientation
{
    Horizontal, // left/right
    Vertical    // top/bottom
}

public sealed record DockGridState(DockNode Root);
```

예시:

```text
1x1
Leaf(A)

1x2
Split(Horizontal, Leaf(A), Leaf(B), 0.5)

2x1
Split(Vertical, Leaf(A), Leaf(B), 0.5)

2x2
Split(Vertical,
  Split(Horizontal, Leaf(A), Leaf(B), 0.5),
  Split(Horizontal, Leaf(C), Leaf(D), 0.5),
  0.5)
```

이렇게 하면 1x1~2x2 제한은 "leaf 최대 4개"로 강제할 수 있다.

### 2. 도킹 hit-test를 서비스로 뺀다

현재 `ExplorerPanelControl.DirectionFromPoint`가 UI 내부에 박혀 있다. 이를 다음처럼 분리한다.

```csharp
public sealed class DockDropHitTester
{
    public DockDropPreview HitTest(
        Rect targetPanelBounds,
        Point pointer,
        DockDragPayload payload,
        DockDropOptions options);
}
```

기본 옵션:

```csharp
public sealed record DockDropOptions(
    double EdgeThresholdRatio = 0.10,
    double GroupEdgeThresholdRatio = 0.30,
    DockOrientation PreferredOrientation = DockOrientation.Horizontal,
    bool SplitOnDragAndDrop = true,
    int MaxVisiblePanels = 4);
```

이렇게 하면 테스트에서 좌표별 preview 결과를 검증할 수 있다.

### 3. 드래그 흐름을 세 단계로 나눈다

```text
BeginDrag
  - payload 생성
  - overlay layer 표시
  - source tab/group capture

UpdateDrag
  - 현재 포인터 좌표로 target panel 찾기
  - DockDropHitTester.HitTest 호출
  - preview rectangle만 업데이트
  - accepted/rejected 상태 표시

CommitDrop
  - preview operation을 DockLayoutService에 전달
  - MoveIntoPanel / SplitPanel / ReorderTab 수행
  - overlay 제거
```

이 분리를 하면 "드래그 중 보이는 것"과 "실제 레이아웃 변경"이 같은 계산 결과를 공유한다.

### 4. overlay는 라벨이 아니라 면적으로 보여준다

권장 시각 규칙:

- 기본 색: `editorGroup.dropBackground`와 유사한 반투명 accent.
- 허용 상태: 파란색/브랜드 accent의 18~25% alpha.
- 불가 상태: 붉은 border + 낮은 alpha.
- split preview: target panel 절반.
- merge preview: target panel content 전체.
- tab reorder: 탭 사이 2px insertion line.
- 텍스트 라벨은 기본적으로 숨긴다. 접근성/툴팁 수준에서만 제공한다.

### 5. Waffle Browse 특수성: Shell View는 옮기지 말고 panel state를 재배치한다

VS Code는 editor pane이 웹/DOM 기반이라 grid view가 직접 view를 이동한다. Waffle Browse는 `IExplorerBrowser` native HWND가 들어간다. 따라서 드래그 중에 실제 Shell host를 이동하지 말고, drop commit 후 새 레이아웃을 렌더링하는 것이 안정적이다.

권장:

- drag 중: overlay만 움직인다.
- drop 시: `DockGridState`를 갱신한다.
- render 시: visible leaf 순서대로 `ExplorerPanelControl`을 재생성하거나 재배치한다.
- Shell View 수명 정책은 별도 관리한다.

## 단계별 리팩터링 제안

### 1단계: 현재 기능 유지하면서 hit-test만 분리

- `ExplorerPanelControl.DirectionFromPoint` 제거.
- `DockDropHitTester` 추가.
- 좌표별 unit test 작성:
  - center -> move into panel
  - left/right/top/bottom edge -> split direction
  - max panel 초과 -> rejected
  - split disabled + edge -> center move

### 2단계: overlay를 preview rectangle로 변경

- XAML의 3x3 `DockOverlay` 라벨 제거.
- `Rectangle` 하나를 가진 overlay layer 추가.
- `DockDropPreview.PreviewBounds`에 맞춰 위치/크기 갱신.
- 드래그 중 전체 panel overlay를 켜는 방식 대신, 현재 target에만 preview를 표시한다.

### 3단계: `DockGridState` 도입

- 기존 `PanelState`와 `TabState`는 유지.
- `DockLayoutState`에 `DockGridState Grid` 추가.
- `DockLayoutKind`는 `Grid`에서 계산하는 read-only summary로 전환.
- 저장 포맷에는 grid tree와 panel states를 같이 저장한다.

### 4단계: 도킹 transaction으로 상태 변경

- `DockLayoutService.DockTab`을 다음처럼 나눈다.
  - `MoveTabToPanel`
  - `SplitPanelWithTab`
  - `ReorderTab`
  - `CollapseEmptyPanels`
- drop UI는 직접 direction을 넘기지 않고 `DockDropPreview`를 commit한다.

### 5단계: VS Code식 옵션 추가

초기값:

```json
{
  "dock.closeEmptyPanels": true,
  "dock.splitOnDragAndDrop": true,
  "dock.openSideBySideDirection": "right",
  "dock.maxVisiblePanels": 4
}
```

VS Code처럼 Alt/Shift modifier로 split 동작을 반전하는 것은 MVP 이후에 붙여도 된다. 먼저 model과 preview 일치를 잡는 것이 중요하다.

## 결론

VS Code식 도킹의 핵심은 "방향을 하드코딩한 3x3 UI"가 아니다. 핵심은 다음 세 가지다.

1. 레이아웃은 editor group leaf를 가진 grid tree로 관리한다.
2. 드래그 중에는 hit-test 결과인 preview operation만 보여준다.
3. drop 시점에 preview operation을 transaction으로 commit한다.

Waffle Browse는 Windows Shell View를 호스팅하므로 VS Code처럼 모든 view를 자유롭게 DOM grid에서 이동할 수는 없다. 대신 Shell host는 panel leaf의 content로 보고, 도킹 레이아웃은 별도 `DockGridState`로 관리하면 같은 UX 원칙을 안정적으로 가져올 수 있다.

## 적용 상태

- `DockDropHitTester`로 드래그 좌표 판정을 UI에서 분리했다.
- `DockGridState`로 visible panel layout을 tree 형태로 저장한다.
- WPF 드래그 중에는 3x3 텍스트 overlay 대신 workspace-level preview rectangle을 표시한다.
- drop 시점에는 `DockDropPreview`를 `DockLayoutService.CommitDrop`으로 commit한다.
