# Info 개발

Info는 Rhino 객체에 저장된 `aha.info`의 조회·입력·수정·삭제와 대지 지정을 담당한다. 법규, 출처의 신뢰도, 형상의 설계 적합성을 판정하지 않는다. 다른 도구도 같은 정보 형식과 쓰기 기능을 사용한다.

정보의 원본은 각 객체의 ObjectAttributes User Text다. 외부 문서, 별도 대지 ID, 그룹 원장, 객체 간 자동 동기화 없이 Rhino의 저장·복사·Undo를 따른다. `kind`는 형상 타입과 별개인 의미상 분류다.

## 읽을 문서

- [계약](contract.md): `aha.info` 형식과 단위, 부분 수정, 충돌·보존·실패 규칙. 이 형식을 생산하거나 소비하는 도구도 따른다.
- [공통 계약](../aha/contract.md): 문서·객체 대상, 실행 스레드와 변경 작업의 경계.
- [MCP](../aha/mcp.md): 외부 호출 형식과 [정보 수정 토큰](../aha/mcp.md#정보-수정-토큰).
- [사용법](../../docs/info/README.md) · [선택적 개발 기록](HISTORY.md).

## 구현과 검사

| 위치 | 책임 |
|---|---|
| [InfoData.cs](../../src/info/shared/InfoData.cs) | JSON 생성·형식 검증·merge patch·변경점 계산 |
| [InfoService.cs](../../src/info/rhino/InfoService.cs) | 문서와 객체 확인, 공통 읽기·쓰기, 충돌 검사와 Undo |
| [InfoPanel.cs](../../src/info/rhino/InfoPanel.cs) | 편집 대상 고정, 필지·수치 입력 확정, 서비스 호출 |

[검사 안내](../../src/info/checks/README.md)에서 형식 검사와 Rhino 객체·패널 검사를 구분한다. 형식 변경은 저장·소비 도구의 계약과 보존 검사를 함께 검토한다.
