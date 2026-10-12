# AHA 공통 실행 계약

공통 기반은 호출 대상을 식별하고 도구의 실행·결과·수명을 관리한다. 도구별 업무 입력·출력은 각 [도구 계약](../index.md)에, 전송 형식은 [MCP](mcp.md)에 둔다.

## 문서와 객체 조회

| 기능 | 입력 | 결과·보존 규칙 |
|---|---|---|
| `aha_context` | 없음 | 열린 문서별 `document_id`, `display_name`, `path`, `active`, `units`, `selected_objects`, `topo_job`를 반환한다. `topo_job`는 해당 문서의 실행·준비·생성 중 Topo 작업 상태이며 없으면 `null`이다. 문서나 선택을 바꾸지 않는다. |
| `aha_objects` | `document_id`; 선택적으로 `site_only=false`, `offset=0` | 해당 문서의 `total`, `offset`, 최대 500개의 `objects`를 반환한다. 숨김·잠금 객체를 포함하고 삭제 객체는 제외한다. 선택은 바꾸지 않는다. |

객체 요약은 `object_id`, `name`, `type`, `layer`, `has_info`다. `type`은 Rhino 객체 유형이며 Info의 `kind`와 별개다. `has_info`는 속성의 존재 여부이고 내용의 유효성 보장은 아니다. `site_only`는 Info 읽기 결과의 `site=true`로 필터링한다. 형식을 읽을 수 없는 정보는 제외하지만, 수정이 필요한 업무 수치만으로 대지 객체를 숨기지는 않는다. 정보 읽기와 쓰기의 검증 차이는 [Info 계약](../info/contract.md)을 따른다.

`document_id`는 현재 Rhino 프로세스에서 열린 문서의 runtime serial number다. 문서 이름·파일 경로·목록 순번은 식별자를 대체하지 않는다. 객체는 해당 문서 안의 Rhino GUID로 지정한다. 문서 재열기·Rhino 재시작 후에는 문서 목록을 다시 조회한다. 대상이 모호하면 호출자가 문서 표시 정보와 선택 객체를 확인해야 하며, 실행 중 새로 활성화된 문서를 대신 사용하지 않는다.

`offset`은 0 이상의 정수다. 페이지 조회는 저장된 스냅샷이 아니므로 조회 사이 객체가 추가·삭제되면 목록도 달라질 수 있다. 닫힌 문서나 삭제된 객체는 실행 시 거절하며 다른 대상을 자동으로 찾지 않는다.

## 변경과 보존

- 같은 문서에는 동시에 하나의 AHA 변경 작업만 허용한다. 중복 요청은 대기열에 넣지 않고 거절한다. 다른 문서의 작업은 별도로 허용한다.
- Topo는 문서당 실행·준비 작업 하나를 예약하되 변경 잠금은 명시적 생성 구간에서만 유지한다. 준비 중 사용자 편집과 Info 변경을 허용한다. Info 및 EumCrawl 적용도 쓰기 구간만 보호한다.
- 잠금은 AHA 변경끼리의 중복을 막는다. Rhino 사용자 편집이나 다른 플러그인의 변경까지 차단하지는 않는다. 정보 편집 충돌은 [Info](../info/contract.md), 조회 후 적용 충돌은 [EumCrawl](../eum-crawl/contract.md)의 계약으로 확인한다.
- Rhino 객체 접근과 문서 반영은 Rhino UI 스레드에서 수행한다. 외부 응답 뒤에는 원래 문서의 생존을 다시 확인한다. 실패·취소 시 변경 잠금을 해제해 다시 실행할 수 있게 한다.
- 각 쓰기 서비스가 Undo와 실패 복구를 소유한다. Topo의 생성 객체·레이어 복구, Info의 속성 보존 등은 해당 도구 계약을 따른다. 공통 기반은 모든 도구에 단일한 형상 복구 방법을 강제하지 않는다.

## 긴 작업

Topo의 패널과 MCP는 같은 작업 핸들을 사용한다. EumCrawl의 MCP 조회도 이 작업 관리 기능을 사용하며, MCP 프로토콜의 별도 Tasks 기능은 아니다.

| 기능 | 입력 | 결과 |
|---|---|---|
| 준비·조회 시작 | 도구별 입력 | `job_id`를 포함한 작업 상태. 시작은 업무 성공이 아니다. |
| `aha_topo_create` | `job_id` | 준비된 Topo의 생성 시작. 상태와 결과는 같은 작업 ID로 조회한다. |
| `aha_job_status` | `job_id` | 현재 상태·입력·준비 요약·결과·오류의 사본 |
| `aha_job_cancel` | `job_id` | 실행 중 취소 요청 또는 준비 결과 폐기 뒤 상태 |

상태 객체는 `job_id`, `document_id`, `operation`, `state`, `progress`, `input`, `summary`, `result`, `error`를 포함한다. `operation`은 `topo`, `topo_xy`, `eum-crawl`이며 `progress`는 표시 문구다. Topo의 `input`은 고정된 준비 입력, `summary`는 준비 요약이다. 실제 객체 GUID는 생성 완료 후 `result`에서만 제공한다. EumCrawl은 준비·생성 상태를 사용하지 않고 조회 완료 결과를 반환한다.

| `state` | 의미 |
|---|---|
| `running` | 취득·계산 대기·형상 준비 또는 EumCrawl 조회 중 |
| `ready` | 검증된 Topo 형상을 보유하며 Rhino 객체는 아직 없음 |
| `creating` | Rhino 모델 반영 또는 실패 복구 중. 중간 취소하지 않음 |
| `completed` | 실제 생성 또는 조회 완료. 결과의 미확인 항목은 도구 계약에 따름 |
| `failed` | 실행 실패. `error`에 안내를 제공함 |
| `cancelled` | 취소·시간 초과 또는 준비 결과 폐기 완료 |

Topo는 문서당 종료되지 않은 작업 하나만 허용한다. 새 준비 요청이 기존 작업을 덮어쓰지 않으며, 거절 안내에 기존 ID를 포함한다. 생성 요청은 `ready`에서만 수락한다. 같은 작업의 중복 요청은 한 번만 반영하고 `completed` 재요청은 기존 결과를 반환한다. 이후 Rhino Undo는 이 작업을 다시 생성 가능한 상태로 바꾸지 않는다.

Topo는 시작 시 원래 문서·단위·허용오차·입력을 고정한다. 활성 문서 전환·일반 편집·저장은 무효화 사유가 아니다. 생성 전 대상 생존·단위·허용오차를 다시 검사한다. Rhino 명령이나 같은 문서의 AHA 변경이 진행 중이면 `ready`를 유지하고 `progress`에 재시도 이유를 알린다. 문서 닫힘·단위 변경 같은 실제 무효화는 실패·취소로 처리한다.

Topo 전체 모델·XY 준비에는 자동 기한을 두지 않는다. 일반 조회 작업은 10분 뒤 취소를 요청하며 개별 HTTP 응답 제한도 유지한다. native 계산의 취소는 반환 후 안전한 지점에서 처리한다. 사용자 취소·준비 결과 폐기·대상 종료·시간 제한 초과는 기존 `progress`에 실제 사유를 구분해 제공한다. 취소 뒤 늦게 반환한 준비 형상은 폐기한다. `ready`는 자동 만료되지 않으며 KEY 없이 생성할 수 있다. 패널 닫기·탭 전환·MCP 중지는 Topo 작업을 취소하지 않는다. 명시적 취소·폐기, 대상 문서 닫힘, 플러그인 종료는 준비 자원을 해제한다. 생성 구간에 들어갔다면 반영 또는 복구를 끝낸다.

작업과 형상은 메모리에만 있고 Rhino 재시작 후 복원하지 않는다. 실행·준비 작업은 최대 128개다. 종료 JSON 기록은 형상과 별도로 최대 128개·종료 후 1시간까지 보관하고 작업 상태가 바뀌거나 새 작업을 등록할 때 정리한다. 생성·실패·폐기된 형상은 해제한다. MCP 중지·포트 교체는 MCP로 시작한 EumCrawl 조회를 취소하지만 Topo는 유지한다.

## 연결과 검증

| 의존하는 계약 | 공통 기반의 사용 |
|---|---|
| [Info](../info/contract.md) | 객체 정보 읽기·대지 필터·revision을 확인한 수정·삭제 |
| [Topo](../topo/contract.md) | 외부 검색·필지 취득, XY·전체 모델의 백그라운드 준비와 지정 문서의 명시적 생성 |
| [EumCrawl](../eum-crawl/contract.md) | 비동기 원문 읽기·후보 미리보기, 지정 객체에 선택 항목 적용 |
| [Setting](../setting/contract.md) | 저장된 MCP 활성 여부·포트로 서버 시작·중지. KEY를 MCP 결과에 포함하지 않는다. |

구현은 [`DocumentAccess.cs`](../../src/aha/rhino/DocumentAccess.cs), [`Jobs.cs`](../../src/aha/rhino/Jobs.cs), [`McpTools.cs`](../../src/aha/mcp/McpTools.cs)가 담당한다. 작업 상태·취소는 [`JobChecks.cs`](../../src/aha/checks/core/JobChecks.cs), 문서 수명·잠금은 [`RuntimeNativeChecks.cs`](../../src/aha/checks/native/RuntimeNativeChecks.cs), 설치본의 조회·변경 연결은 [`RuntimeWireChecks.cs`](../../src/aha/checks/native/RuntimeWireChecks.cs)로 검증한다.
