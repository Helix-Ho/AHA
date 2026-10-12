# 토지이음 공개 화면

[토지이음](https://www.eum.go.kr/) · [상세 화면 스크립트](https://www.eum.go.kr/web/js/ar/lu/luLandDet.js) · [산출 도식·면적 계산 스크립트](https://www.eum.go.kr/web/js/ar/lu/luLandDetUse.js)

2026-10-04~05 확인한 공개 화면과 추가 규제 응답 구조를 EumCrawl에 참고했다. 공식 OpenAPI 계약은 아니다. 공개 검사는 [독립 합성 HTML](../../../src/eum-crawl/checks/README.md)을 사용하고, 취득 응답과 출처·해시는 로컬 `.local/test-data/eum-crawl/`에서만 관리한다.

필지 일치 확인, 소재지·지목·면적·용도지역·지구와 건폐율·용적률 등 추가 영역의 취득·파싱을 조사했다. [EumCrawl 개발 문서](../../eum-crawl/README.md)가 현재 호출·파싱 계약을 소유하고, [개발 기록](../../eum-crawl/HISTORY.md)은 최솟값 선택과 한계에 대한 판단을 설명한다.

2026-10-07에는 공개 상세 화면의 추가 응답을 위 계산 스크립트와 대조했다. HTML의 `.coverage_wrap`에는 0% 도식과 기본 조건 문구가 있지만 `fn_calArea()`가 `.anal_c02_left/right .won`과 `.anal_c02_bottom`을 계산 결과로 교체한다. 같은 함수와 `fn_showAreaData()`가 `areaTotal`·`areaTotalM`·`area[지역코드]`·`areaM[지역코드]`의 0㎡도 교체한다. 이 초기값은 규제 본문이 아니므로 원문 읽기에서 미확인으로 구분한다. 표의 실제 조례 수치와 0%는 보존한다. 공개 검사는 독립 합성 구조를 사용하며 외부 JavaScript를 제품에서 실행하거나 계산식을 복제하지 않는다.

보이는 수치·조건을 가져오는 것과 법적으로 적용할 규제를 판정하는 것은 다르다. 고정 HTML 검사의 성공은 현재 공개 화면이나 모든 지역 규제의 완전성을 보증하지 않는다. 화면 구조가 바뀌면 해당 영역의 실제 응답을 재확인한다.

## 자료 이용조건

2026-10-11에 [저작권정책](https://www.eum.go.kr/web/lc/bi/biCopyRight.jsp)과 위 필지 상세 화면의 공공누리 출처표시 자유이용 안내를 확인했다. 공통 정책의 내용 변경 안내와 [공공누리 제1유형](https://www.kogl.or.kr/info/license.do)의 조건은 실제 이용 자료에 붙은 표시를 기준으로 대조한다. 원문 출처와 AHA가 계산한 최솟값을 구분하며, 법령 내용·조회 데이터·웹페이지 HTML 전체가 동일한 이용조건이라고 가정하지 않는다. 공개 HTML fixture도 출처 기록만으로 재배포 허락을 대신하지 않는다.
