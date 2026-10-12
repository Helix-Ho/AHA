# EumCrawl 구조

업무상 입력·결과·보존 기준은 [계약](contract.md)에 둔다. 이 문서는 외부 HTTP에서 Info 저장까지의 처리 경계와 비동기 수명을 설명한다.

## 취득과 파싱

[EumClient](../../src/eum-crawl/shared/EumClient.cs)는 고정된 토지이음 HTTPS 경로에 순서대로 요청한다.

1. `luLandDet.jsp?pnu=...`에서 PNU·소재지·필지 항목과 추가 요청에 필요한 지역지구 코드를 확인한다.
2. `luLandDetUseGYAjax.jsp`에서 건폐율·용적률 항목을 받는다.
3. `luLandDetUseCAjax.jsp`에서 높이·층수 항목을 받는다.
4. `luLandDetUseKDAjax.jsp`에 K, K2 구분을 보내 건축선과 대지 안의 공지를 받는다.

브라우저나 JavaScript 실행을 사용하지 않는다. 임의 URL·리다이렉트·로그인에 의존하지 않는다. 상세 페이지의 PNU가 요청과 다르거나 필수 식별 내용이 없으면 다음 단계로 진행하지 않는다. 규제 요청에 전달하는 코드의 문자와 길이도 검사한다.

응답마다 본문 수신을 포함해 30초와 2 MiB 한도를 적용한다. EUC-KR/CP949를 처리하며 한글 인코딩 정보가 없는 공식 응답도 CP949로 읽는다. 규제 응답의 예상 표식과 텍스트 길이도 검사한다. 개별 규제 취득의 HTTP·내용·시간 초과 실패는 경고로 남기지만 사용자 취소는 상위로 전달한다.

[EumParser](../../src/eum-crawl/shared/EumParser.cs)는 HTML 위치와 항목 표식을 기준으로 읽는다. 스크립트·스타일·주석·태그를 제거하고 HTML 문자를 복원한다. 브라우저의 CSS 표시 상태를 계산하는 파서는 아니다. `gun_basic_`·`yong_basic_` 입력이 있는 조례 셀을 찾되 숨김 입력의 value를 백분율로 사용하지 않는다. 그 셀의 본문에서만 후보를 읽는다. 필지 항목의 중복 팝업 내용도 분리한다.

수치 선택 정책은 [계약의 수치 규칙](contract.md#수치와-필지-정보)에 둔다. 출처 URL·PNU·기준일·조회시각·원문·항목별 발췌는 조회 결과에 남기며 저장 여부는 적용 계층이 결정한다.

## 원문 읽기와 후보 파싱의 분리

EumClient의 같은 취득 경로에서 상세 페이지 식별과 규제 응답 한도·오류를 확인한다. `LookupAsync`는 모든 항목을 후보 파서로 보내고, `ReadEvidenceAsync`는 요청한 항목만 [EumEvidence](../../src/eum-crawl/shared/EumEvidence.cs)로 보낸다. 읽기 경로가 미리보기 JSON을 역변환하거나 최솟값 파서의 출력에 의존하지 않는다.

EumEvidence는 제한된 HTML 토큰 트리를 만들어 표의 셀 병합·헤더·문단·조건·안전한 링크를 유지한다. 허용된 원문 구조만 JSON으로 내보내고 HTML·스크립트를 실행하지 않는다. 일반 브라우저 DOM/CSS 엔진을 구현하지 않으며, 구조를 해석할 수 없거나 한도를 넘으면 항목 실패로 드러낸다. 문자열을 자르는 응답 축약은 하지 않는다.

토지이음이 브라우저에서 계산하는 `coverage_wrap` 도식과 `areaTotal`·`areaTotalM`·`area[지역코드]` 셀은 미확인 표시로 치환한다. 숫자 0을 제거하는 규칙이 아니라 [공식 화면 스크립트의 역할](../references/Eum/README.md)로 구분하므로 규제 본문의 0%에는 영향을 주지 않는다.

`EumService.ReadAsync`는 요청 전후에 원래 문서의 생존을 확인한다. 객체·Info·미리보기 캐시·Undo에 접근하지 않는다. 문서와 후보를 결합하는 아래 흐름은 Preview/Apply 전용이다.

## 조회와 저장의 경계

[EumService](../../src/eum-crawl/rhino/EumService.cs)의 `EumWorkflow`가 다음 순서를 소유한다.

1. Rhino UI 스레드에서 문서·객체·Info·원필지 위치를 확인하고 원본 JSON과 data를 복사한다.
2. UI 스레드 밖에서 취소 가능한 조회를 실행한다.
3. 문서가 여전히 열려 있는지 확인하고 [EumApply](../../src/eum-crawl/shared/EumApply.cs)로 비교 결과를 만든다.
4. 객체에 묶인 결과는 별도 복사본으로 캐시에 보관한다. 반환 JSON의 변경은 캐시를 바꾸지 않는다.
5. 적용 요청은 캐시의 후보와 현재 data를 대조하고 선택 필드명으로 patch를 만든다. InfoService가 원본 JSON·편집 가능 여부를 다시 확인해 쓰고 Undo를 구성한다. 성공한 결과만 소비한다.

조회 함수와 시계는 workflow의 외부 입력이다. 제품은 실제 EumClient와 UTC 시간을 사용하고, 검사는 고정 HTTP 응답과 제어된 시간을 사용한다. 파서·필드 매핑·충돌 규칙을 검사 안에 복제하지 않는다.

## 패널과 MCP

[EumPanel](../../src/eum-crawl/rhino/EumPanel.cs)은 선택이 없으면 조회 전용, 하나면 기존값·신규 후보 비교, 둘 이상이면 비활성 상태로 동작한다. 400ms 간격으로 문서·선택·원본 정보의 변화를 확인한다. 입력이나 대상이 바뀌면 이전 조회를 취소하고 미리보기를 버린다.

비동기 요청에는 세대 번호를 붙인다. 취소 요청 뒤 도착한 응답도 현재 세대와 다르면 표시하지 않는다. 패널이 숨겨지면 진행 중 조회를 취소한다. 검색 성공 전에는 열람할 결과 링크가 없으며 성공한 조회의 PNU로 열람 버튼을 활성화한다.

패널 하단의 국토교통부·토지이음 출처 링크는 조회 상태와 관계없이 홈페이지를 연다. 검색한 필지를 여는 열람 버튼과 구분한다.

MCP도 EumService를 사용하되 긴 조회의 진행·취소는 AHA Jobs가 관리한다. 패널의 화면 세대와 MCP 작업 상태를 하나의 전역 UI 상태로 묶지 않는다. 문서 식별·스레드·호출 결과는 [공통 계약](../aha/contract.md)과 [MCP 규약](../aha/mcp.md)을 따른다.

## 검사 연결

[검사 안내](../../src/eum-crawl/checks/README.md)에서 취득·파싱은 `Program.cs`와 `FixtureHandler.cs`, 캐시·적용·Undo는 `native/EumNativeChecks.cs`, 지연 응답·선택·숨김은 `native/EumPanelChecks.cs`로 연결한다. 공개 합성 입력과 로컬 원본 입력은 같은 호출·판정 경로를 사용한다.
