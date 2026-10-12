# AHA MCP

MCP는 로컬 Rhino의 도구 서비스를 공개하는 호출 경로다. 업무 의미는 소유자의 계약에 두고, 이 문서는 통신·도구 발견·입력 변환·결과 전달을 관리한다. 특정 AI 제품을 요구하지 않으며 클라이언트의 로그인·모델·추론 실행은 AHA가 관리하지 않는다.

## HTTP와 초기화

현재 구현은 MCP Streamable HTTP의 상태 없는 요청·응답 경로다. Setting에 저장된 포트로 `http://127.0.0.1:<port>/mcp/`를 열고 JSON 응답을 반환한다. GET/SSE·세션 ID·서버 알림·범용 코드 실행 기능은 제공하지 않는다. 활성 여부와 포트의 저장·복사 계약은 [Setting](../setting/contract.md)을 따른다.

- loopback 접속만 받고 URL authority와 경로가 설정 주소와 일치해야 한다. `Origin`이 있으면 같은 scheme·authority여야 한다. Origin 없는 로컬 클라이언트는 허용한다. 별도의 인증 헤더나 KEY를 받지 않는다.
- `POST`, `Content-Type: application/json`을 받는다. 본문은 최대 1 MiB, 본문 읽기는 15초로 제한한다. 중첩 객체·배열 내부까지 동일한 JSON 속성 이름의 중복을 거절한다.
- `MCP-Protocol-Version` 헤더가 있으면 지원 버전이어야 한다. 지원 버전은 `2025-11-25`, `2025-06-18`, `2025-03-26`이다. `initialize`는 요청한 지원 버전을 반환하며 그 외에는 첫 번째 지원 버전을 제안한다.
- 초기화 응답은 서버 이름 `aha`, 제품 버전, `tools.listChanged=false`와 대상 확인·작업 결과 조회 지침을 포함한다. 제품 버전·Info 형식 버전·MCP 프로토콜 버전은 별개다.

서버는 초기화 완료 여부를 세션으로 저장하지 않는다. 클라이언트는 MCP의 초기화·버전 협상 절차를 따르되 AHA가 세션 상태를 보관한다고 가정하지 않는다.

## 발견과 호출

`tools/list`는 [`McpTools.List()`](../../src/aha/mcp/McpTools.cs)의 도구 이름·설명·`inputSchema`·annotations를 반환한다. `tools/call`은 `name`과 객체형 `arguments`를 받아 등록된 서비스만 호출한다. `ping`은 빈 객체, resources·resource templates·prompts 목록은 빈 배열을 반환한다. 유효한 알림처럼 `id`가 없는 요청에는 JSON-RPC 본문 없이 HTTP 202를 반환한다. 배치 요청은 받지 않는다.

| 공개 이름 | 업무 계약 |
|---|---|
| `aha_context` | [공통 문서·선택 조회](contract.md#문서와-객체-조회) |
| `aha_objects` | [공통 객체 조회](contract.md#문서와-객체-조회) |
| `aha_info_read` | [Info](../info/contract.md) |
| `aha_info_update` | [Info](../info/contract.md) |
| `aha_info_delete` | [Info](../info/contract.md) |
| `aha_topo_search` | [Topo](../topo/contract.md) |
| `aha_topo_parcel` | [Topo](../topo/contract.md) |
| `aha_topo_xy_prepare_start` | [Topo XY 생성](../topo/contract.md#xy-생성) |
| `aha_topo_prepare_start` | [Topo 준비](../topo/contract.md) |
| `aha_topo_create` | [Topo 명시적 생성](../topo/contract.md) |
| `aha_eum_read` | [EumCrawl 원문 구조 읽기](../eum-crawl/contract.md#원문-구조-읽기) |
| `aha_eum_preview` | [EumCrawl](../eum-crawl/contract.md) |
| `aha_eum_apply` | [EumCrawl](../eum-crawl/contract.md) |
| `aha_job_status` | [공통 작업 상태](contract.md#긴-작업) |
| `aha_job_cancel` | [공통 작업 취소](contract.md#긴-작업) |

Setting의 설정 변경·KEY 관리 기능은 MCP에 공개하지 않는다. 기능을 추가·변경할 때 이름·설명·스키마·실제 분기와 소유자의 계약을 함께 갱신한다. 이 표는 소유권 연결을 담당하며 도구별 입력 필드를 복사하지 않는다.

등록 스키마는 최상위·옵션 객체의 허용 속성을 명시한다. 실행 시 공통 검증이 필수값·알려진 속성·자료형·중첩 옵션과 등록된 수치 범위·배열 길이/중복·열거값을 검사한다. PNU 형식·지리 범위·문서 상태 같은 업무 조건은 서비스가 검증한다. `default`는 생략 시의 동작을 설명하며 실제 기본값은 호출 변환과 서비스에서도 일치해야 한다. 범용 JSON Schema 검증기는 아니므로 새로운 종류의 제약은 실행 검사도 함께 추가한다.

Topo 준비는 작업 상태를 만들므로 `readOnlyHint=false`, `destructiveHint=false`, `idempotentHint=false`다. `aha_topo_create`는 기존 객체를 파괴하지 않고 같은 작업의 중복 생성을 막으므로 `idempotentHint=true`다. 생성에는 외부 취득이 없어 `openWorldHint=false`를 사용한다. 원문 조회는 읽기 전용이고, Info 변경·삭제와 미리보기 적용은 기존 정보의 변경이다. annotations는 권한 통제나 AI의 선택 강제를 대신하지 않는다.

## 결과와 오류

성공한 `tools/call`은 `isError=false`와 `content`의 text 항목을 반환한다. text에는 도구 결과를 JSON으로 직렬화한 문자열이 들어 있다. 결과가 JSON 객체이면 같은 값을 `structuredContent`에도 넣고, 배열·`null`이면 text에만 넣는다. 현재 `outputSchema`는 제공하지 않는다.

공통 래퍼와 직접 만든 JSON 필드는 `document_id`처럼 snake_case를 사용하지만 전체 응답에 일괄적인 이름 변환을 적용하지 않는다. Topo 검색 DTO 배열·좌표 DTO·모델 결과 DTO는 선언된 PascalCase 속성 이름으로 직렬화된다. 클라이언트는 전체 결과의 이름을 추정하지 않고 [Topo 계약](../topo/contract.md)의 실제 결과 형식을 따른다.

| 오류 계층 | 반환과 해석 |
|---|---|
| HTTP | 접속 범위·Origin 부적합 403, POST 외 405, 지원하지 않는 버전 헤더 400, Content-Type 부적합 415, 과대 본문 413, 본문 읽기 취소·시간 초과 408. 도구가 실행됐다고 해석하지 않는다. |
| JSON-RPC | 손상 JSON `-32700`, 잘못된 요청·중복 속성 `-32600`, 없는 메서드 `-32601`, 잘못된 메서드 매개변수 `-32602`. 응답의 `error`에 담긴다. |
| 도구 실행 | 잘못된 도구 이름·업무 입력·충돌·서비스 오류는 `result.isError=true`와 text 안내로 반환한다. HTTP·JSON-RPC 응답 성공이 업무 성공은 아니다. |
| 비동기 업무 | 시작 호출 뒤의 준비·생성·실패·취소는 [작업 상태](contract.md#긴-작업)에 담긴다. `aha_job_status` 호출 자체의 성공과 업무 상태를 구분한다. |

예상된 입력·상태 오류는 안내 메시지를 전달한다. 그 외 예외는 유형만 포함한 일반 오류로 바꾸며 스택 추적이나 KEY를 반환하지 않는다. 표시 문구를 기계적인 오류 코드로 해석하지 않는다.

MCP 클라이언트가 발견하는 설명은 Markdown 문서가 아니라 `tools/list` 응답이다. 단위·미입력·수정 범위·EumCrawl 미리보기의 최솟값 정책처럼 도구 선택과 실행에 필요한 제약은 소유자 계약과 실제 도구 설명 양쪽에 일치해야 한다. 전체 계약을 설명 문자열에 복제하지는 않는다.

설명은 필요한 기능을 선택하도록 연결한다. 주소 검색·필지 조회는 XY 평면 생성, 전체 주변 모델 생성, PNU 기반 EumCrawl 원문 읽기·수치 후보 조회를 구분해 안내한다. 초기 계획의 규제 후보는 EumCrawl로 확인하고 완료 결과의 토지이용계획 링크를 사용자에게 제공할 수 있다. 도구를 특정 순서로 강제 실행하거나 Topo가 EumCrawl을 자동 호출하지는 않는다. AI의 설계 판단·형상 생성은 외부 호출자의 책임이다.

## 정보 수정 토큰

`aha_info_read`와 `aha_info_update`는 Info와 함께 `revision`을 반환한다. `aha_info_update`·`aha_info_delete` 호출자는 가장 최근에 읽은 토큰을 그대로 전달해야 한다. 토큰은 저장된 `aha.info` 원문에 대응하는 불투명 문자열이며 전체 JSON으로 대체하거나 클라이언트가 다시 계산하지 않는다. 정보가 없으면 빈 문자열이고, 삭제 결과도 빈 문자열이다.

수정 직전 같은 UI 스레드 호출 안에서 현재 원문과 토큰을 대조한 뒤 서비스의 원문 스냅샷 검사로 연결한다. 원문이 달라졌으면 쓰기를 거절한다. 이때 다시 읽고 수정 내용을 재검토해야 하며 이전 값으로 덮어쓰지 않는다. 토큰은 모델의 Info 필드에 저장하지 않는다. [Info 계약](../info/contract.md)은 정보의 형식·병합·보존을 담당하고, EumCrawl의 조회 후 적용은 [별도의 미리보기 계약](../eum-crawl/contract.md)을 따른다.

## 구현과 검사

[`McpHttpServer.cs`](../../src/aha/mcp/McpHttpServer.cs)가 접속·본문을 검증하고, [`McpProtocol.cs`](../../src/aha/mcp/McpProtocol.cs)가 JSON-RPC와 결과 포장을 담당한다. [`McpTools.cs`](../../src/aha/mcp/McpTools.cs)는 입력을 도구 서비스로 연결한다. [`InfoRevision.cs`](../../src/aha/mcp/InfoRevision.cs)는 정보 수정 토큰을 실제 저장값과 대조하며 토큰을 모델에 저장하지 않는다.

통신 경계는 [`core/Program.cs`](../../src/aha/checks/core/Program.cs), 서버 교체·포트 반환은 [`lifecycle/`](../../src/aha/checks/lifecycle/), 설치된 서비스와 실제 HTTP 호출은 [`RuntimeWireChecks.cs`](../../src/aha/checks/native/RuntimeWireChecks.cs)로 검사한다. 공식 프로토콜 자료는 [MCP 참고 자료](../references/MCP/README.md)에 있다.
