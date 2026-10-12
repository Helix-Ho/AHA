# EumCrawl 검사

[개발 안내](../../../dev/eum-crawl/README.md) · [계약](../../../dev/eum-crawl/contract.md) · [공통 실행 방법](../../../dev/aha/build-and-test.md#회귀-검사-실행)

```sh
.venv/bin/python src/eum-crawl/checks/run.py
.venv/bin/python src/eum-crawl/checks/run.py --include-local-fixtures
.venv/bin/python check.py --tool eum-crawl --build-rhino
```

Rhino에서 [run_rhino.py](run_rhino.py)를 실행한다. 등록은 [suites.json](suites.json)에서 관리한다.

| 보호하는 계약·실패 조건 | 검사 |
|---|---|
| 원문 표·조건·헤더·병합 셀·중첩 표·링크 보존, 계산 전 UI 초기값과 실제 0% 구분, 수치 선택 없음, 부분 취득·한도 실패 | `EvidenceChecks.cs` |
| 원문 읽기의 객체 불변·문서 닫힘 후 응답 거절 | `native/EumNativeChecks.cs` |
| 항목별 백분율 최솟값·조건/중첩/범위/0·누락/숨김 숫자·한계 안내 | `Program.cs` |
| PNU 일치·CP949·부분 실패·응답 크기/수신 제한·취소·미리보기의 원문 열람 URL 보존 | `Program.cs`, `FixtureHandler.cs` |
| 선택 필드·원필지 슬롯·주소 별도 반영·확장/미선택 값 보존·건축선/공지 원문의 메모 보존 | `Program.cs`, `native/EumNativeChecks.cs` |
| 대상·원본 충돌·정확한 만료 경계·변조·재사용·동시 캐시 한도 | `native/EumNativeChecks.cs` |
| 조회 중 선택/입력 변경·숨김·다중 선택 후 지연 응답 | `native/EumPanelChecks.cs` |
| 일반 객체·닫힌 대지 입체의 Info 선택 적용·Undo와 기존 문서 보존 | `native/EumNativeChecks.cs` |

기본 검사는 [합성 HTML](fixtures/)을 실제 EumClient·파서에 공급한다. 원본 응답 HTML·출처·해시는 로컬 `.local/test-data/eum-crawl/`에만 보관하며 `--include-local-fixtures`로 동일한 검사 논리에 입력한다. 자료 누락·해시 불일치는 실행 불가다. Rhino 검사도 고정 HTTP 응답과 제어된 시간을 사용하며 실제 사이트·사용자 객체를 요구하지 않는다. 제품 EumWorkflow를 호출하므로 테스트 전용 파서나 적용 규칙을 복제하지 않는다.

| 보호할 실패 조건 | 공개 합성 검사 | 로컬 원본 검사 |
|---|---|---|
| 표·병합 셀·숨김 값·계산 전 0%와 실제 0% 구분 | `EvidenceChecks`와 `fixtures/*-synthetic.html` | 원본 5개 HTML과 로컬 `facts.json` |
| 조건부 비율의 최솟값·법령일·부분 실패 | `Program.cs`의 합성 수치·실패 입력 | 같은 파서·기대값으로 원본 비율과 날짜 재확인 |
| 조회 결과의 대상 고정·선택 적용·Undo | 합성 응답의 `Program.cs`, `EumNativeChecks` | 같은 적용 경로에 원본 응답을 공급한 Rhino 검사 |

원본 HTML의 특정 문구·숫자와 현재 공급원 응답은 합성 검사만으로 보장하지 않는다.

## 실사용·외부 연동 확인

아래 항목은 [검사 유지 원칙](../../../dev/aha/build-and-test.md#검사-유지-원칙)에 따라 별도로 확인한다. 패널 도킹·표시는 [공통 확인](../../aha/checks/README.md#실사용외부-연동-확인)을 따른다.

| 재확인할 변경 | 준비·방법 | 기대 결과 |
|---|---|---|
| 토지이음 요청·파서 변경 또는 원문 구조 변경 의심 | 대표 PNU의 현재 원문을 브라우저로 열고, 같은 대상의 원문 읽기·후보 미리보기와 항목명·표 조건·수치·링크를 대조한다. | 계약상 수집 범위의 원문과 후보 근거가 대응하고, 스크립트 계산·누락·조건부 항목은 확정값으로 보이지 않는다. 화면 전체 재현이나 법규 적용성은 판정하지 않는다. |
