# Waffle Safe File Index Architecture

## 목표

Waffle Browse는 외부 검색 엔진 없이 파일명과 경로를 빠르게 검색한다. 인덱싱 성능은 일반 사용자 권한 안에서만 개선하며, 관리자 권한이나 운영체제 보안 경계를 검색 성능의 수단으로 사용하지 않는다.

## 실행 구조

```text
SearchResultsView
  -> LatestSearchRequestCoordinator
  -> WaffleFileSearchProvider
      -> FileSearchIndex
      -> RecursiveFileIndexSource
      -> JsonFileIndexStore (format v4)
      -> FileSystemWatcher
```

- `RecursiveFileIndexSource`는 .NET의 문서화된 관리형 파일 열거 API만 사용한다.
- 앱 시작 시에는 색인 루트가 없으며 파일 열거를 시작하지 않는다.
- 검색 입력란에 포커스하면 활성 탭의 현재 폴더 하나를 색인 루트로 설정한다.
- 다른 탭에서 검색을 준비하면 이전 루트의 색인과 변경 감시를 새 활성 폴더로 교체한다.
- 현재 사용자에게 접근 권한이 없는 경로는 건너뛴다.
- reparse point는 결과에는 포함할 수 있지만 그 대상으로 재귀 진입하지 않는다.
- 검색은 현재 활성 폴더에 대해 완성된 메모리 인덱스를 사용하고, 완성된 스냅샷을 JSON으로 원자적으로 저장한다.
- 이전 권한 상승 구현이 생성한 v3 캐시는 로드하지 않고 v4 안전 인덱스를 새로 만든다.
- 실행 중 변경은 `FileSystemWatcher`로 반영하며, 버퍼 유실 시 현재 활성 색인 루트만 다시 색인한다.

## 금지된 구현

다음 기능은 이 프로젝트의 파일 검색 구현에 포함하지 않는다.

- 관리자 권한 요청 또는 UAC `runas`
- 별도 상승 helper, 서비스 또는 드라이버
- raw volume, MFT 또는 USN Change Journal 직접 접근
- `DeviceIoControl`을 통한 파일시스템 제어
- ACL, 소유권, 권한, 보안 정책 또는 보안 제품 설정 변경
- 접근 거부 항목을 우회하거나 보호된 메타데이터를 일반 프로세스에 전달하는 기능

## 안전한 성능 개선 방향

허용되는 최적화는 다음과 같다.

- 열거 과정의 불필요한 중간 목록과 중복 파일 상태 조회 제거
- 메모리 인덱스의 효율적인 자료구조 및 검색 알고리즘
- JSON 스냅샷의 빠른 로드와 원자적 교체
- 파일 변경 이벤트의 debounce, batching 및 루트 단위 재색인
- 사용자 설정에 따른 색인 범위 축소
- 취소 가능한 백그라운드 처리와 제한된 병렬 처리

성능 개선안이 권한 상승이나 보호된 시스템 상태 접근을 필요로 한다면 해당 방법은 구현하지 않고, 현재 사용자 권한 안에서 가능한 대안을 선택한다.
