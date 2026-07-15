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
  - Everything 인덱스를 사용해 입력 후 즉시 파일/폴더 검색
  - 전체 인덱스 또는 현재 폴더와 하위 폴더 범위 선택
  - 검색 탭의 가상화 결과 목록을 1초 간격으로 갱신
  - Everything 검색 문법과 이름/경로/수정일/크기 정렬 지원
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

## 빌드 및 실행

검색 기능을 사용하려면 [Everything](https://www.voidtools.com/ko-kr/) 1.4 이상이 설치되어 백그라운드에서 실행 중이어야 합니다. 앱 배포물에는 공식 x64 SDK DLL만 포함되며 Everything 실행 엔진은 포함하지 않습니다.

```bash
git clone https://github.com/Dev-Mons/waffle-browse.git
cd waffle-browse
dotnet build
```

```bash
dotnet run --project src/Waffle.Browse.App/Waffle.Browse.App.csproj
```

## 현재 한계 / 향후 계획

- 현재는 Windows 플랫폼 전용 앱입니다.
- 현재 저장/검색 UI는 핵심 워크플로우(탐색/패널/탭/테마/검색)에 집중되어 있으며,
  북마크·작업공간 같은 고급 기능은 별도 단계로 확장될 예정입니다.

## 영어 요약 (English)

**Waffle Browse** is a Windows-first WPF file-explorer wrapper built on top of the native Windows Explorer view.

- Hosts `IExplorerBrowser` directly in WPF, so the native shell experience stays familiar.
- Supports 1~4 panel layouts with tab-based navigation.
- Provides live, indexed file and folder search through the Everything SDK.
- Includes native-style keyboard navigation, focus handling, and dark/light theme switching.
- Persists layout and UI settings locally.
- Covers the core feature set with dedicated test projects for reliability.
