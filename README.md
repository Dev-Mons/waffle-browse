# Waffle Browse

Windows Explorer의 파일 목록 렌더링을 재사용하는 **멀티 패널 WPF 파일 탐색기 래퍼**입니다.
`IExplorerBrowser`를 네이티브로 호스팅해 탐색기 UX의 핵심(아이콘/컨텍스트 메뉴/파일 보기 동작)을 그대로 활용하면서,  
탭 기반 워크플로우와 패널 도킹 기능으로 작업 효율을 높이는 데 초점을 둡니다.

## 주요 기능

- **Windows Shell 통합**
  - `IExplorerBrowser`를 `HwndHost`에 호스팅하여 네이티브 탐색기 뷰를 사용
  - 폴더 이동, 탐색기 상호작용, 컨텍스트 메뉴 흐름을 앱 레벨로 통합
- **멀티 패널 + 탭**
  - 1/2/3/4 패널 레이아웃(예: 2열, 2행, 2x2) 전환
  - 패널별 탭, 주소 표시줄, Back / Forward / Up 이동
  - 탭 드래그를 통한 패널 이동 및 도킹(분할/이동)
- **빠른 검색**
  - Waffle 자체 로컬 인덱스로 입력 후 즉시 파일/폴더 검색
  - NTFS는 MFT 초기 스냅샷과 USN Change Journal 체크포인트로 증분 갱신
  - hard link와 64/128-bit 파일 ID를 보존하고, 비 NTFS는 재귀 인덱스로 볼륨별 폴백
  - 전체 인덱스 또는 현재 폴더와 하위 폴더 범위 선택
  - 검색 탭의 가상화 결과 목록을 1초 간격으로 갱신
  - 이름/경로 부분 일치 및 와일드카드, 이름/경로/수정일/크기 정렬 지원
  - 검색어를 지우면 검색 시작 패널의 원래 경로로 복귀
- **네비게이션/입력 동기화**
  - 마우스 버튼(뒤로/앞으로), Backspace, Alt+←/→ 등 탐색 동작 지원
  - Shell 메시지/포커스 키보드 이벤트를 WPF 키 처리와 연동
- **테마**
  - 라이트/다크 토글
  - 앱 리소스 + Shell 창 테마 동기화 적용
- **상태 영속화**
  - 마지막 레이아웃(`layout.json`) 자동 복원
  - UI 설정(`settings.json`) 저장 및 로드
- **테스트 기반 안정성**
  - 도킹, 탭 이동, 탐색 히스토리, 쉘 검색 타깃 구성, 테마/포커스 동작을 위한 Core/App 테스트 프로젝트 구성

## 기술 스택

- **.NET 9 / WPF (net9.0-windows)**
- **C# 13**
- **Windows Shell COM interop**
- Clean architecture-like layering:
  - `src/Waffle.Browse.Core` (도메인/상태/테스트)
  - `src/Waffle.Browse.App` (WPF UI + Shell 호스트 + 사용자 입력 처리)
  - `src/Waffle.Browse.Indexer` (권한 분리용 current-user named-pipe helper)

## 빌드 및 실행

검색은 Waffle 자체 인덱스를 사용합니다. 첫 실행의 전체 드라이브 인덱스 구축은 백그라운드에서 진행되며, 이후 생성·삭제·이름 변경을 증분 반영합니다.

```bash
git clone https://github.com/Dev-Mons/waffle-browse.git
cd waffle-browse
dotnet build
```

```bash
dotnet run --project src/Waffle.Browse.App/Waffle.Browse.App.csproj
```

일반 사용자 토큰에서 원시 NTFS 접근이 거부되고 helper pipe가 없으면, 보호된 `Program Files` 설치 위치의 앱은 같은 디렉터리에 있는 정확한 sibling `Waffle.Browse.Indexer.exe`를 인수 없이 UAC로 실행합니다. 실행 전에는 `Program Files` root부터 전체 배포 트리까지 reparse point가 없는지, 현재 사용자에게 디렉터리 교체나 executable·DLL·설정 파일의 쓰기·삭제·ACL 변경 권한이 없는지를 실제 Windows access check로 확인합니다. 또한 AMD64 PE32+, COR header 부재, NativeAOT runtime export, .NET bundle signature 부재, 정확한 build stamp와 관리형 sidecar 부재를 모두 확인하므로 표식만 복사한 관리형 apphost도 승격하지 않습니다. 검증한 image handle은 프로세스 생성이 끝날 때까지 쓰기·삭제 공유 없이 유지합니다. 사용자가 UAC를 거부하거나 실행·연결에 실패하면 재귀 인덱스로 안전하게 폴백하고, 같은 앱 세션에서는 승격 요청을 반복하지 않습니다. 여러 앱 프로세스의 helper 작업도 세션·설치 경로별 파일 lock으로 직렬화해 중복 UAC를 막고, 다른 RDP/콘솔 세션이나 side-by-side 설치와는 분리합니다. 정상 실행된 helper는 연결을 기다리는 상태가 5분 동안 이어지면 종료되며, 이후 native 접근이 다시 필요하면 앱이 새 UAC 동의를 받아 재실행할 수 있습니다.

`build-exe.bat`는 framework-dependent WPF 앱과 self-contained NativeAOT helper를 디버그 심볼 없이 `publish\win-x64`에 sibling으로 함께 publish합니다. helper용 JSON 경로는 source generation을 사용하며 게시 시 trimming/AOT 경고 없이 생성됩니다.

```powershell
.\build-exe.bat
& ".\publish\win-x64\Waffle.Browse.App.exe"
```

portable publish 디렉터리는 일반 사용자가 수정할 수 있으므로 그 위치에서는 자동 승격이 의도적으로 비활성화됩니다. native helper 자동 실행은 installer가 전체 배포 트리를 보호된 `Program Files` 위치에 설치한 제품 배포에서만 사용합니다. installer의 ACL 강제, NativeAOT 표식 기록 이후의 코드 서명, 업데이트·제거 수명주기는 아직 남은 작업입니다.

명시적으로 인덱싱할 네트워크 공유는 `%LocalAppData%\Waffle Browse\settings.json`의 `IndexedNetworkRoots` 배열에 UNC 경로로 추가합니다. 대형 인덱스 저장소와 protocol v2를 포함한 후속 항목은 [native file index 계획](docs/waffle-native-file-index-plan.md)에 정리되어 있습니다.

## 현재 한계 / 향후 계획

- 현재는 Windows 플랫폼 전용 앱입니다.
- 현재 저장/검색 UI는 핵심 워크플로우(탐색/패널/탭/테마/검색)에 집중되어 있으며,
  북마크·작업공간 같은 고급 기능은 별도 단계로 확장될 예정입니다.

## 영어 요약 (English)

**Waffle Browse** is a Windows-first WPF file-explorer wrapper built on top of the native Windows Explorer view.

- Hosts `IExplorerBrowser` directly in WPF, so the native shell experience stays familiar.
- Supports 1~4 panel layouts with tab-based navigation.
- Provides live file and folder search through Waffle's own local index.
- Includes native-style keyboard navigation, focus handling, and dark/light theme switching.
- Persists layout and UI settings locally.
- Covers the core feature set with dedicated test projects for reliability.
