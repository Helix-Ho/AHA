# EumCrawl 개발

EumCrawl은 PNU로 토지이음을 조회하고, 규칙으로 읽은 후보를 사용자에게 제시한 뒤 선택한 항목만 객체에 적용하는 실험 도구다. 초기 계획에서 필지 정보와 규제 후보를 확인하고 원문 검토로 이어가는 데 사용한다. 법규를 확정하지 않으며, 조회와 저장을 분리한다. Topo 실행 여부와 관계없이 사용할 수 있다.

MCP의 원문 읽기는 표·조건·문단을 수치 선택 없이 제공한다. 후보 미리보기의 건폐율·용적률은 해당 조례 항목에서 읽은 백분율의 최솟값을 사용한다. 원문 조건과 이 방식의 한계를 결과·패널·MCP 설명에 유지한다. 정보 저장은 Info의 형식과 공통 쓰기 기능을 사용한다.

## 읽을 문서

- [계약](contract.md): 원문 읽기·조회 입력, 후보 상태·최솟값 정책, 미리보기 수명과 선택 적용.
- [구조](architecture.md): 토지이음 요청·파싱, 비동기 응답, 패널과 서비스의 연결을 변경할 때 읽는다.
- [Info 계약](../info/contract.md): 저장 형식과 부분 수정·보존.
- [공통 계약](../aha/contract.md) · [MCP 규약](../aha/mcp.md): Rhino 대상·실행과 외부 호출.
- [사용법과 한계](../../docs/eum-crawl/README.md) · [선택적 개발 기록](HISTORY.md).

## 구현과 검사

`src/eum-crawl/shared/`의 [EumClient](../../src/eum-crawl/shared/EumClient.cs)·[EumParser](../../src/eum-crawl/shared/EumParser.cs)·[EumEvidence](../../src/eum-crawl/shared/EumEvidence.cs)·[EumApply](../../src/eum-crawl/shared/EumApply.cs)가 공통 취득·후보 파싱·원문 구조·선택 필드 매핑을 나눈다. [EumService](../../src/eum-crawl/rhino/EumService.cs)는 문서·미리보기·저장을 연결하고, [EumPanel](../../src/eum-crawl/rhino/EumPanel.cs)은 비교 화면과 조회 상태를 관리한다.

[검사 안내](../../src/eum-crawl/checks/README.md)에서 고정 HTTP 자료·파서·적용·Rhino 수명 검사를 찾는다. 실제 서비스의 결과를 추정하는 검사 대신 실제 파서와 제어된 응답·시간을 사용한다.
