# Waffle Native File Index Architecture

## 목표와 현재 상태

Waffle Browse 검색은 외부 검색 엔진 없이 Waffle이 소유한 로컬 인덱스로 파일 이름과 경로를 검색한다. 2026-07-15 기준으로 단계 1~3과 단계 5의 코드 경계는 완료됐고, 단계 4는 최소 권한 helper, 명시적 UAC 동의 launcher, NativeAOT 배포 계약, idle 종료와 안전한 폴백까지 구현됐다.

현재 실행 경로는 다음과 같다.

```text
SearchResultsView
  -> LatestSearchRequestCoordinator
  -> WaffleFileSearchProvider
      -> FileSearchIndex
      -> FallbackFileIndexSource (볼륨별)
          -> NtfsMftIndexSource
          -> NamedPipeFileIndexSource
          -> RecursiveFileIndexSource
      -> JsonFileIndexStore (format v3)
```

일반 사용자 프로세스에서 NTFS 원시 볼륨 접근이 가능하면 직접 MFT/USN 경로를 사용한다. 권한이 거부되면 현재 사용자 전용 named pipe helper를 시도하고, helper가 없거나 해당 볼륨을 지원하지 않으면 사용자 모드 재귀 열거로 내려간다. 어느 경로에서도 완성되지 않은 볼륨 세대는 게시하지 않는다.

## 단계 1: 검색 수직 단계 — 완료

검색/UI 계약은 다음을 제공한다.

- 파일명 또는 전체 경로의 대소문자 무시 부분 일치와 `*`/`?` 단순 와일드카드
- 전체 인덱스/현재 폴더 범위, 최대 1,000개, 폴더 우선 및 이름·경로·수정일·크기 정렬
- `ReaderWriterLockSlim` 기반 동시 검색/교체 경계
- 초기 구축 중 마지막 정상 세대를 계속 검색하고, 새 세대와 버퍼된 비-native 변경을 원자적으로 교체
- `Empty`, `Loading`, `Ready`, `Rebuilding`, `NeedsRebuild`, `Failed` 상태와 세대·항목 수·완료 시각·볼륨 체크포인트
- 버전 있는 JSON 스냅샷을 임시 파일에 쓴 뒤 원자적으로 교체하고, 손상/형식 불일치는 안전하게 재구축

## 단계 2: NTFS MFT 초기 스냅샷 — 완료

`NtfsMftIndexSource : IFileIndexSnapshotSource`와 Windows accessor가 구현됐다.

1. `\\.\C:` 원시 볼륨 핸들을 열고 volume GUID, 파일시스템, serial number, root FRN을 검증한다.
2. `FSCTL_ENUM_USN_DATA`/`MFT_ENUM_DATA_V0`로 MFT 레코드를 연속 열거한다.
3. 포인터 크기에 의존하지 않는 경계 검사 파서가 `USN_RECORD_V2/V3`, 64/128-bit FRN, Unicode 이름, 속성, USN, reason을 읽는다.
4. 반복형 FRN 그래프 resolver가 레코드 순서와 무관하게 경로를 만들고, 고아·순환·중복·부모 누락을 격리한다.
5. 파일 ID 조회를 2~8개 worker로 제한 병렬화해 크기와 수정 시각을 보강한다. 단일 항목 조회 실패는 경로를 버리지 않고 nullable 메타데이터로 남긴다.
6. hard link는 `FindFirstFileNameW`/`FindNextFileNameW`로 모든 현재 경로를 확장해 같은 FRN의 다중 검색 경로로 보존한다.
7. MFT 스캔 시작 시점의 저널 위치부터 게시 직전까지 변경을 catch-up한 뒤 volume identity를 다시 확인한다.

레코드 길이, 이름 offset/length, 8-byte 정렬, 버퍼 경계, cursor 전진을 매 배치마다 검증한다. 고아/손상 레코드 경고는 개수를 보존하되 메시지를 최대 100개로 제한한다.

## 단계 3: USN Change Journal 증분 처리 — 완료

`FileIndexCheckpoint` format v3는 root path, volume GUID, filesystem, serial number, Journal ID, Next USN을 함께 저장한다. 재시작 및 watcher 이벤트 시 다음 순서를 따른다.

1. 저장된 volume GUID·serial number·filesystem과 현재 볼륨이 같은지 확인한다.
2. Journal ID가 같고 저장 USN이 `LowestValidUsn..NextUsn` 안에 있으면 `FSCTL_READ_USN_JOURNAL`로 이어 읽는다.
3. 레코드를 FRN별로 합치고 create/delete/data/basic-info/rename/hard-link 변경 후 현재 파일 ID 상태를 다시 조회한다.
4. 폴더 rename은 하위 경로를 한 번에 이동하고 FRN→경로 multimap도 함께 갱신한다.
5. 변경 배치가 적용된 불변 세대를 게시한 뒤에만 새 체크포인트와 스냅샷을 저장한다.

다음 조건은 해당 볼륨의 전체 MFT 재구축으로 전환한다.

- 저널 미존재·비활성화·삭제 진행 또는 Journal ID 변경
- 저장 USN이 `LowestValidUsn`보다 오래됨
- 저널/MFT 레코드 손상, 역순, cursor 무진행
- volume GUID·serial number·root FRN 변경
- 영속 항목에 FRN이 없거나 중복 경로가 있음

native watcher 이벤트는 재귀 upsert로 FRN을 덮어쓰지 않는다. 250ms 단일 dirty-root worker가 같은 루트 이벤트를 합치고 체크포인트 refresh를 수행한다. 진행 중인 refresh는 후속 이벤트 때문에 취소되지 않으며, 후속 이벤트는 한 번의 추가 pass로 합쳐진다. 변경된 루트만 refresh하고 다른 로컬 볼륨과 네트워크 공유의 마지막 정상 세대는 그대로 병합한다.

## 단계 4: 권한과 프로세스 경계 — helper/게시 완료, installer acceptance 잔여

`Waffle.Browse.Indexer` 최소 helper와 protocol v1이 구현됐다.

- 세션 ID와 정규화한 설치 경로 hash를 포함한 `Waffle.Browse.Indexer.v1.s<session>.<deployment>` named pipe에 network token deny + 현재 사용자 SID allow DACL 적용
- pipe object를 medium integrity로 낮춰 같은 사용자의 비승격 앱이 승격 helper에 연결 가능
- 연결 직후 양쪽 process의 사용자 SID와 terminal session을 상호 검증하고, 앱은 helper token의 elevation도 확인
- 양쪽에서 실제 process image를 조회해 같은 배포 디렉터리의 `Waffle.Browse.App.exe`/`Waffle.Browse.Indexer.exe` sibling인지 검증
- helper 요청은 현재 준비된 fixed local NTFS drive root로 제한해 임의 경로의 승격 읽기를 거부
- 64 KiB 이하 length-prefixed UTF-8 JSON physical frame와 2 MiB 이하 logical message chunking
- entry를 2 MiB 이내 동적 batch로 묶어 항목별 pipe syscall을 방지
- 요청 root 최대 64개, 경로 최대 32,768자, collection count와 protocol version 검증
- Build/Refresh 요청, 전체 baseline/checkpoint/entry streaming, 명시적 remote error taxonomy
- frame idle 10초, 2초 progress heartbeat, 연결/작업 30분 상한과 peer disconnect 시 source 취소
- 검색어 또는 검색 결과 요청이 없는 인덱스 구축 전용 계약
- helper 명령줄 인수 전면 거부: query/path가 command line이나 URL에 노출되지 않음
- malformed/중단 연결은 해당 연결만 폐기하고 다음 연결을 계속 수락
- helper 미가동/권한 거부/지원 불가/작업 실패는 per-root 폴백 또는 마지막 정상 세대 유지로 전환
- helper pipe 연결 timeout/IO 실패 시에만 명시적 사용자 동의 기반 UAC launcher를 시도하며, 연결된 peer 검증 실패를 다른 승격 프로세스로 대체하지 않음
- launcher는 보호된 `Program Files` 설치 위치에서만 같은 디렉터리의 정확한 `Waffle.Browse.Indexer.exe` sibling을 인수 없이 숨김 실행
- 실행 전 `Program Files` root부터 leaf까지와 전체 배포 트리의 모든 디렉터리·파일에서 reparse point를 거부하고, 현재 사용자 token으로 디렉터리 생성·삭제·ACL/owner 변경 및 executable·DLL·설정 파일 쓰기·삭제·ACL/owner 변경 권한을 실제 access check해 하나라도 가능하면 승격하지 않음
- helper는 self-contained NativeAOT로만 게시하고, App 전용 정책이 AMD64 PE32+, COR header 부재, 정확한 `DotNetRuntimeDebugHeader` export, 공식 .NET bundle signature 부재, EOF 또는 Authenticode certificate table 직전의 build stamp, 관리형 DLL/deps/runtimeconfig sidecar 부재를 모두 확인한 뒤에만 승격함. helper가 참조하는 Core에는 표식을 넣지 않으며, 검증 image handle은 `runas` 프로세스 생성 완료까지 쓰기·삭제 공유 없이 유지함
- named-pipe protocol과 Core JSON 저장소는 `System.Text.Json` source-generation metadata를 사용해 NativeAOT/trimming 경고 없이 게시
- UAC 거부 또는 실행·재연결 실패는 재귀 인덱스로 안전하게 폴백하고, launch 직후 상태는 trusted peer 검증이 끝난 뒤에만 해제해 취소·검증 실패 시 승격 프롬프트를 반복하지 않음
- 기본 helper 작업은 세션·설치 경로 identity별 LocalAppData byte-range lock으로 같은 배포의 앱 프로세스 사이에서 직렬화하며, 다른 terminal session/side-by-side 설치와 분리함. contention 후 연결 실패는 새 UAC 대신 안전한 폴백으로 전환하고 프로세스 비정상 종료 시 OS handle 정리로 lock을 해제
- 정상 실행된 helper는 연결 대기 상태가 5분간 지속되면 종료하고, 이후 요청에서는 새 사용자 동의를 받아 재실행 가능
- `build-exe.bat`가 framework-dependent WPF 앱과 self-contained NativeAOT helper를 디버그 심볼 없이 `publish\win-x64`에 함께 publish하고 관리형 helper sidecar가 있으면 실패해 sibling/런타임 계약을 보장

앱의 기본 순서는 `직접 NTFS -> helper -> recursive`다. 명시적 사용자 동의 기반 launcher와 게시 경로는 구현됐지만, portable publish 디렉터리는 일반 사용자가 수정할 수 있으므로 그 위치에서는 자동 승격을 시도하지 않는다. 제품 배포에는 전체 load closure를 `Program Files` 아래에 설치하고 일반 사용자의 binary 교체를 막는 installer와 ACL 강제, AOT 표식 기록 후 코드 서명, 안전한 업데이트·제거 수명주기가 여전히 필요하다.

현재 위협 모델에서 UAC에 동의한 사용자의 SID와 terminal session은 helper가 살아 있는 동안 하나의 trust domain으로 취급한다. 같은 사용자가 자신의 managed App 프로세스를 계측할 수 있다는 점은 수용하며, helper capability는 준비된 fixed local NTFS volume의 파일명·경로·기본 메타데이터 읽기와 인덱스 계산으로 제한하고 파일 내용 읽기나 쓰기 API는 제공하지 않는다. 따라서 metadata가 원래 ACL로 열 수 없는 항목 이름을 포함할 수 있다는 점도 사용자 동의 범위에 속한다. runtime access check는 현재 token에 대한 defense-in-depth일 뿐 machine-wide DACL과 서명을 대신하지 않으며, installer는 다른 비관리자 계정도 배포 트리를 수정하지 못하게 하고 update 전에 helper를 종료한 뒤 원자적으로 교체해야 한다.

protocol v1의 refresh는 변경된 볼륨 하나로 범위를 자르고 entry를 batch 전송하지만, helper가 stateless이므로 그 볼륨의 baseline과 결과는 아직 전체 snapshot이다. 이는 정확성과 장애 시 폴백을 우선한 현재 코드 경계이며 1천만 항목의 최종 증분 전송 설계는 아니다. 저장소 벤치마크와 함께 helper-owned volume cache, checkpoint-only 요청, journal delta 응답, helper 재시작/cache miss 시 전체 재구축을 포함한 protocol v2를 검증해야 한다.

## 단계 5: 비 NTFS와 네트워크 — 완료

- 앱은 준비된 모든 fixed local drive를 root로 등록한다.
- NTFS가 아니거나 native API를 지원하지 않는 볼륨은 `RecursiveFileIndexSource`로 볼륨 단위 폴백한다.
- 한 볼륨의 detach/IO/저널 실패는 다른 볼륨 세대를 폐기하지 않고 해당 root의 마지막 정상 세대를 유지한다.
- 네트워크 공유는 `settings.json`의 `IndexedNetworkRoots`에 명시적으로 추가한 UNC root만 인덱싱한다.
- watcher 이벤트를 반영하고, 연결 끊김과 이벤트 신뢰도 보완을 위해 최초 1분 후부터 5분마다 해당 UNC root만 재검증한다.
- watcher 재연결과 overflow 재구축은 root 단위 상태를 보존한다.

## 영속 저장소 후속 결정 — 잔여

현재 스냅샷은 `search-index-v3.json`이다. format v3는 volume serial과 64/128-bit file ID 표현 폭을 보존하지만, JSON은 수백만 항목의 최종 저장 설계가 아니다.

다음 후보를 같은 데이터셋으로 벤치마크한 뒤 하나를 선택한다.

- Waffle 소유의 버전 있는 이진 스냅샷 + 메모리 매핑
- Windows inbox API를 이용한 ESE
- SQLite의 네이티브 배포 크기, 라이선스 고지, 업데이트 전략 포함 비교

선택 기준은 1천만 항목 초기 로드 시간, 스냅샷 크기, 이름 조회 지연, 배치 갱신 비용, 충돌 후 복구, 쓰기 증폭, 배포 크기다. format v3 JSON에서 선택 포맷으로의 migration/rollback도 함께 정의해야 한다.

## 자동 검증 현황

2026-07-15 현재:

- Core 테스트 118개 통과
- App 테스트 64개 통과
- 전체 solution build 경고 0, 오류 0

추가된 native 회귀 범위는 다음을 포함한다.

- V2/V3 및 64/128-bit FRN, Unicode, malformed buffer
- parent-before-child 비의존 경로 해석, 고아·순환·deep graph·경고 제한
- MFT metadata/checkpoint, 취소, cursor 무진행, 스캔 중 journal catch-up
- persisted checkpoint replay, invalid journal full rebuild, hard-link 유지
- per-root fallback/last-good, native watcher checkpoint refresh, 변경 root만 refresh
- JSON의 128-bit 폭 보존
- pipe frame round-trip/oversize/version mismatch/dynamic batching/idle timeout/heartbeat/helper 부재/실제 Build·Refresh 왕복
- UAC launcher의 정확한 sibling·무인수 실행 계약, writable ACL 거부, 성공 후 재연결과 idle 종료 후 재실행, 프로세스 내·프로세스 간 단일 helper 사용, 동시 UAC 거부 coalescing, launch 후 취소와 untrusted peer 검증 실패의 replacement prompt 억제
- NativeAOT PE/export/stamp 경계와 관리형 bundle/sidecar 거부, helper 참조 Core에서 App 전용 표식 제외, 검증 image launch lease의 쓰기·삭제 차단, AOT 0-warning publish와 관리형 single-file 음성 산출물 검사

## 제품화 전 남은 수동 검증

코드 작업과 별도로 실제 관리자 경계 및 볼륨 동작은 격리된 VHDX에서 수동 acceptance가 필요하다.

1. 임시 NTFS VHDX에서 대량 파일, Unicode, deep tree, hard link를 만든 뒤 MFT 결과를 실제 파일시스템과 대조한다.
2. USN create/delete/rename/저널 wrap/저널 삭제 진행/볼륨 detach·reattach를 재현해 checkpoint fallback을 확인한다.
3. 일반 사용자 직접 접근 거부와 UAC 승격 helper 성공·거부·실패, 세션 내 프롬프트 억제, 5분 idle 종료와 재실행, helper 미가동, malformed client를 각각 확인한다.
4. helper installer의 설치·ACL 강제·AOT 표식 이후 서명·업데이트·제거와 pipe ACL 정책을 보호된 `Program Files` 배포에서 검증한다.
5. 1천만 항목 저장소 및 helper protocol v2 벤치마크 후 JSON 대체 여부, delta 전송, migration을 확정한다.
