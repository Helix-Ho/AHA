# Topo 검사

[개발 안내](../../../dev/topo/README.md) · [계약](../../../dev/topo/contract.md) · [공통 실행 방법](../../../dev/aha/build-and-test.md#회귀-검사-실행)

API 취득·형상 계산·지도·설치본 서비스 검사를 Topo 도구 수준에서 관리한다.

```sh
.venv/bin/python src/topo/checks/run.py
.venv/bin/python src/topo/checks/run.py --include-local-fixtures
.venv/bin/python check.py --tool topo --build-rhino
```

Rhino에서 [run_rhino.py](run_rhino.py)를 실행한다. 형상 검사와 설치본 서비스 검사가 모두 끝난 뒤 전체 보고서의 `passed`를 확인한다. 등록 원본은 [suites.json](suites.json)이다.

| 보호하는 계약·실패 조건 | 검사 |
|---|---|
| 좌표축·원점·유한 수치·범위·PNU·완전 수집·공급원 페이지/묶음 끝까지 수집·분리 불가능한 불완전 응답·DEM 누락/손상 | `core/DataChecks.cs`, `core/TerrainChecks.cs`, `core/BuildingSourceChecks.cs`, `core/BuildingClassificationChecks.cs`, `service/TopoServiceChecks.cs` |
| 모든 생성 건물의 전체 지반 취득·고정 건물 목록·재조회 없음·확장 취득 실패·원래 격자와 출력 경계 보존 | `core/TerrainCoverageChecks.cs`, `service/TerrainCoverageServiceChecks.cs`, `native/CoverageGeometryChecks.cs` |
| 재배분된 출력 도로와 건물 지반 통계 일치·출력 밖 취득 도로 보존 | `native/CoverageGeometryChecks.cs` |
| 도로 지목·100mm 평면 정리·출력 경계의 미세 띠·원본 보존·횡단 평탄화 | `core/RoadGeometryChecks.cs`, `core/ModelPlanChecks.cs`, `core/RoadGradingChecks.cs` |
| Near/Far 작은 역할 조각의 같은 구역 재배분·전체 그래프 교차 후 미세 조각·정확 Near 경계·50mm 수직 단차 | `core/NearRoadChecks.cs`, `core/PlanarPartitionChecks.cs`, `native/NearRoadCleanupChecks.cs` |
| 1.5R/2R·인접/분리 대지·원호·공통접선·경계 접촉의 strict Near 분류 | `core/EditRegionChecks.cs`, `core/HullChecks.cs`, `native/HullGeometryChecks.cs`, `native/BuildingClassificationChecks.cs` |
| 건물 높이·크기·지하 분류·다중 외곽/중정·정보 밀도 | `core/Building*Checks.cs`, `core/ObjectInfoChecks.cs`, `native/Building*Checks.cs` (`BuildingContactChecks`는 `Helpers`에서 실행) |
| 파라펫의 해석적 면적·체적·안쪽 폭·중정/다중 외곽/접촉/좁은 지붕·실제 Extrusion 폐합·m/mm·Near 적용 범위·OFF/ON 기존 형상 보존 | `core/ParapetProfileChecks.cs`, `native/ParapetGeometryChecks.cs` (`Parapets` 및 전체 모델 공통 검사) |
| 전체 지반 최저점·면적 가중평균·도로 후보 인덱스 동등성·분할 면적 보존·이동/회전·최저점−1m·겹친 Near 건물의 공통 바닥 | `core/BuildingPlacementChecks.cs`, `native/BuildingMeshChecks.cs`, `native/ContourModelChecks.cs` |
| 공유 위상·서로 다른 셀 세분 수·분리 Near 밖 고도 제외/공유 세분점 보존·동일 고도/평탄부·음의 고도·해석적 단면 면적·상하 포함·준비/추출 취소·순차 단면 처리 | `core/ContourSectionChecks.cs`, `core/ContourChecks.cs`, `core/ContourPrecisionChecks.cs` (`core/Program.cs`에서 실행) |
| 정확 Near 유한 직선/원호·공유/근접 교점 구분·정수 격자 판정·구멍 소유권·단면 면적·불변 도로 색인 재사용·방향/이동/회전 | `core/ExactNearRegionsChecks.cs`, `core/ExactContourProfileChecks.cs`, `native/ContourChecks.cs` |
| 도로 보정의 실제 잔차·조건이 나쁜 양의 정부호 입력·독립 직접해·영향 밖의 정확한 0 보정·±3m 제약 | `core/RoadSolverChecks.cs` (`RoadGradingChecks`에서 실행) |
| Contour 공통 레벨·작은 돌출 생략·상하 지지 관계·도로 내림 0/간격 미만/동일/초과·한 판 굴착·잔여부 생략 후 기초/구멍 일치·대지 제외·하부 깊이 | `core/ContourChecks.cs`, `native/ContourChecks.cs`, `native/ContourModelChecks.cs` |
| Contour 고정 정밀도 노딩·NonZero 합집합·구멍 소유권·반복 정규화 보존·원본/표시 노출영역 일치·정확 경계 구간과 일치 접선의 소유·분리된 Near 사이의 Far 표고 제외 | `core/ContourPrecisionChecks.cs` |
| 반복 정리의 정상 경계 보존·대지 제외 후 좁은 연결부와 낮은 상세판의 생략 전파·지지판 피복·정확 지반 조회·노출면의 무관/관련 판 변경과 접촉/미세 겹침·원호 공유 끝점·생략 공간 색인·기초 안정화·A→B→A 격리 | `core/ContourPrecisionChecks.cs`, `core/ContourChecks.cs`, `native/ContourChecks.cs` |
| 실제 Extrusion 형식·일정 단면·양의 두께·구멍/접촉·체적·정확 원호·미세 직선·원호 구간의 방향/시작점/m·mm 출력·도로 겹침면·기초 분리·지역 재생의 모든 건물/기초 폐합과 실제 변 연결 | `native/ContourChecks.cs`, `native/ContourModelChecks.cs`, `native/SurfaceCheckRunner.cs` |
| 곡면 Site 입체·기단 접촉·지지판의 Site 구멍·Site 정보·투영 대지경계 생략·정확 NURBS 외벽·Far 높이/도로 단차·기단 높이·캡 부재 | `native/ContourModelChecks.cs`, `native/ContourWallChecks.cs` |
| Contour의 Near 연속면 생략·전체 평면 피복·Far Mesh/NURBS 면과 도로 옆면 보존 | `native/FarSurfaceChecks.cs` (`Contour`에서 실행) |
| 여러 땅·도로·구멍을 가진 셀의 피복·비정사각 격자·투영 대지경계의 도로 단차/폐합·XY 보존·Site 셀 창의 독립 체적/중정·방향/이동/회전/m·mm | `native/FarSurfaceChecks.cs` (`Contour`에서 실행) |
| 지도 선택·범위·드래그·휠·타일 메시지·우클릭/가운데 버튼의 선택·이동 방지 | `map/topo-map-checks.cjs` |
| 실제 WebView의 페이지별 메시지·지연 타일 격리, 종료 상태 재적용의 입력 오류 보존, Reload 차단·지도/선택 표시 보존·기본 브라우저 메뉴 제거·KEY 상태 변경 시 정상 HTML 재로드 | `service/MapNavigationChecks.cs` (`TopoPanelChecks`에서 실행) |
| 입력 확정·Contour/기초 설정 전달·KEY/지도 세대·늦은 응답·공통 작업 복원·패널 종료 후 준비 유지·KEY 없는 생성 버튼의 실제 반영·준비/완료 개수와 안내 원문/순서/중복/빈 항목/이전 결과 제거 | `service/TopoPanelChecks.cs` |
| 파라펫 기본값·mm→m·입력 활성/잠금/요약·MCP 옵션 변환과 실제 생성·기존 건물/지형 집계 보존 | `service/TopoPanelChecks.cs`, `service/TopoServiceChecks.cs`, AHA의 `RuntimeWireChecks.cs` |
| 등고판 교차 후 겹침 도로 미세 조각 재배분·변경 면적·얇은 프로파일 trim·표시망의 가까운 정점 보존·m/mm·이동·방향 반전 | `core/ContourChecks.cs`, `native/ContourOverlayChecks.cs` (`Helpers`에서 실행) |
| 고정 응답 백그라운드 준비·명시적 생성·준비 중 문서 보존·중복 실행·취소·닫힌 문서·실패 복구 | `service/TopoServiceChecks.cs`, `native/TransactionChecks.cs` |
| 문서 표시망 설정의 복사·누락된 Brep 표시망 준비·기존 지형 표시망 보존·준비 해제 후 캐시 유지·Extrusion 원형/구멍/체적·Rhino 첫 표시망과 저장 | `native/PreparedGeometryChecks.cs` (`Transaction`에서 실행) |
| XY 전용·대지와 구멍·원본 절단·평면 건물 필터·단위/정보 보존·준비 메타데이터와 생성 식별자의 분리·DEM 미취득 | `native/XYChecks.cs`, `service/XYServiceChecks.cs` |
| m/mm·Mesh/NURBS·분리/겹침·수직 단차·Near/Contour·기초 분리 조합 | `native/SurfaceCheckRunner.cs`, `native/ModelGeometryChecks.cs`, `native/ContourModelChecks.cs` |
| 분할 선분 소유권·유한 교점·격자/역할 경계 구분·교차 후 작은 영역·샘플 블록 병합 | `core/PlanBoundarySourcesChecks.cs`, `core/ContourChecks.cs`, `core/PlanarPartitionChecks.cs`, `native/ModelPlanNativeChecks.cs` |
| 폐합·체적·법선·공통 바닥·연속 상면 높이·경계 오차·표시 Mesh. 원본 NURBS와 표시망의 모서리 접선은 다를 수 있으므로 각각의 공유변 방향·부호 있는 체적으로 검증 | `native/Near*Checks.cs`, `native/SurfaceHelperChecks.cs`, `native/ModelGeometryChecks.cs`, `native/SolidDisplayChecks.cs` |
| 원본 XY·대지경계·레이어 순서/표시·Info·Undo·저장 | `native/SourceReferenceChecks.cs`, `native/TopoWorkflowChecks.cs`, `native/ContourModelChecks.cs`, `native/ModelPersistenceChecks.cs` |
| 작은 NURBS 구간 면적·같은 원 위 점 판정·중복 지지면 구간의 높이 보존·빌린 지지면과 출력 형상의 변환/해제 분리·3D 경계 오차·이동/단위/방향 회귀 | `native/AreaIntegralChecks.cs`, `native/InCircleChecks.cs`, `native/SurfaceHelperChecks.cs` |

`core/`와 `native/`는 제품 원본을 참조해 재컴파일한다. `service/`는 설치된 AHA를 호출하며 취득 경계에 고정 응답을 넣는다. 기대값은 평지·평면 경사·쌍선형 적분·단면 면적×두께 등의 해석값과 실제 객체를 비교한다. 제품 계산을 복사해 기대값으로 삼지 않는다. UI 외관·자연스러움은 자동 판정하지 않는다.

연속 지형의 상면 높이 판정은 `native/TerrainElevationExpectation.cs`에 둔다. Contour는 `native/ContourModelChecks.cs`가 실제 Extrusion 프로파일·높이·체적·판 쌓임과 기초·Site 관계를 검사한다. 판이 실패해도 독립적인 Site·건물·Far 검사는 계속하며, 판에 의존하는 쌓임 검사는 실행 불가로 구분한다. 손상된 판의 음성 대조로 이 집계를 확인한다. 도로 단차와 계단화 전 건물 지붕 기준을 구분하며 허용오차를 늘려 차이를 숨기지 않는다.

닫힌 입체의 폐합과 의도적으로 열린 Far 면·`Connections` 외벽을 구분한다. 실제 Mesh 객체·AHA가 구성한 Brep 표시 Mesh와 Rhino가 만드는 Extrusion Render Mesh도 별도로 판정한다. 표시 이상이 있으면 실제 형상의 열린 경계·누락·중복 면부터 확인하며, native Render Mesh의 경고로 형상 검사를 완화하지 않는다. 책임 범위는 [형상 계약](../../../dev/topo/contract.md#형상-검증과-화면-표시)에 둔다.

100mm 폭·0.01㎡ 기준은 연결된 모델 평면과 작은 Contour 돌출에 적용한다. 원천 평면 정리의 50mm 변경 띠와 이후 독립 미세 조각·Near 분할 후 역할 재배분은 서로 다른 계약이다. 선택 대지·정확 Near·XY를 보존하고 각 실행에서 모델 역할·높이장이 일치하는지 확인한다. 연결 입체에 필요한 작은 구성면을 독립된 작은 판과 혼동하지 않는다.

서비스의 문서 무변경 검사는 객체 식별자·기하 CRC·속성·선택·레이어를 비교한다. 저장 보존은 `ModelPersistenceChecks`가 객체 GUID·레이어 GUID/부모/표시·이름/정보·같은 Rhino API의 형상과 물리 단위 bbox를 대조한다. 레이어 번호는 해당 문서에서 레이어를 찾는 데만 사용하며 CRC를 저장 동등성의 단독 기준으로 삼지 않는다. 레이어 번호 재배치의 양성 대조와 실제 레이어/정보/형상 변경·누락의 음성 대조를 함께 유지한다. 준비와 실제 생성을 나누어 확인하며 준비만으로 객체가 생기지 않아야 한다. Undo 뒤 객체 표의 `Count`는 삭제 기록을 포함하므로 살아 있는 GUID 집합과 생성 레이어 제거를 검사한다. Undo 번호는 문서 사이에서도 할당되므로 번호 차이 대신 한 번의 Undo가 실제 모델을 제거하는지 확인한다. Headless Undo가 남긴 활성 기록은 검사에서 닫는다. Rhino 8의 headless 문서는 `CloseDocument` 이벤트를 발생시키지 않으므로 문서 폐기 후 설치본의 종료 처리기를 호출한다. 실제 창의 이벤트 연결은 [공통 확인](../../aha/checks/README.md#실사용외부-연동-확인)으로 구분한다. 공통 작업 상태·중복 생성·취소·MCP 종료 수명은 [AHA 검사](../../aha/checks/README.md)와 함께 보호한다.

기본 형상 묶음은 공개 합성 입력과 설정 조합을 사용한다. 로컬 원본 범위를 선택하면 지역 입력도 함께 재생한다. 저장/재열기는 대표 m/mm·Near·겹침·다중 대지와 Contour 사례로 확인한다. 감천의 동일 입력은 연속 Near와 Contour의 생성 시간·객체 수·기하 메모리·3dm 크기를 함께 기록한다. 측정값에 임의 성능 합격선을 두지 않는다.

## 고정 입력과 집중 실행

[fixtures/](fixtures/)에는 독립 합성 좌표·높이와 해석 가능한 기대값을 둔다. 원본 지역 압축 JSON, 축약 재현, 검사 코드에서 분리한 원본 좌표·기대값, 출처와 해시는 Git 제외 영역 `.local/test-data/topo/`에 보존한다. 원본 수치가 필요한 검사는 실행 시 로컬 JSON을 읽는다. 로컬 범위를 요청했는데 자료가 없거나 해시가 다르면 실행 불가다. 기본 검사와 원본 검사는 제품 계산을 공유하며 입력·기대값을 구분한다. 생성된 3dm·KEY·개인 경로·과거 로그는 검사 입력이 아니다.

| 보호할 실패 조건 | 공개 합성 검사 | 로컬 원본 검사 |
|---|---|---|
| 공유 교점·유한 직선/원호·구멍 소유권 | `ExactNearRegionsChecks`, `PlanBoundarySourcesChecks`, `polygon-regressions.json` | `RecordedExactNearRegionsChecks`, `near-finite-regions.json`, 지역 경계 입력 |
| 불균일 분할·동일 고도·작은 조각과 판 지지 | `ContourSectionChecks`, `ContourPrecisionChecks`, `conforming-regressions.json` | 축약 원본 JSON과 지역 Contour 재생 |
| 원천 건물·도로와 기초/지붕·정보의 연결 | 합성 `DataChecks`, `Building*Checks`, `ContourModelChecks` | 9개 지역 압축 응답의 전체 모델 재생 |
| 미세 좌표·표고에 따른 형상 퇴화 | 합성 `witnesses.json`과 형상 검사 | 원본 `witnesses.json`, `RecordedContourGeometryChecks`, 지역 재생 |

합성 검사는 해당 실패 조건을 독립 입력으로 판정한다. 원본의 개별 좌표·표고와 지역 전체 생성은 로컬 검사만 보호한다.

`SurfaceCheckRunner.Start("All")`은 합성 검사만 실행한다. 아래 지역 선택은 `--include-local-fixtures`로 등록되는 `Recorded` 범위에서 실행한다. 선택 실행은 같은 판정을 사용한다.

| 선택 | 입력·확인 범위 |
|---|---|
| `Contour` | 합성 경사·도로·구멍·점 접촉·m/mm의 직접 Extrusion, 정확 NURBS 외벽, Near 생략 전후 Far 면·도로 옆면 보존 |
| `Parapets` | 독립 면적·체적 기대값, 중정·좁은 지붕·분리 단면, Near/Contour/기초 설정 조합과 OFF/ON 기존 형상·정보 보존 |
| `Recorded` (로컬) | 아래 `BoundaryModes`·지역 `Replay`, 원본 수치가 필요한 유한 경계·프로파일 회귀를 함께 실행 |
| `BoundaryModes` | `JongnoBoundary`의 8개 대지·Near 경계 교차 건물·원본 68×93 격자. Contour ON/OFF·m/mm, 교차 건물의 Far 보존과 상면·입체·표시망 |
| `JongnoJunction-near`, `JongnoJunction-contour` | 1,000m·Near 3R의 종로 공유 정점 입력. 원본 DEM·도로로 상면/외벽 대응·폐합·표시망·원본 보존 검사 |
| `SeongsuContact-near`, `SeongsuContact-contour` | 다중 대지·원천 건물 접촉 입력의 연속/Contour 모델 |
| `Bulgwang-near`, `Bulgwang-contour` | 불광 1,000m·Near 3R 고정 입력의 연속/Contour 모델·기초·실제 입체·저장 보존 |
| `Coverage`, `NearRoadCleanup`, `BuildingClassification` | 전체 지반 확보, 파생 작은 역할 조각, 경계 접촉 건물의 분류 |
| `Matrix` | Near·지형 형식·도로 분리/겹침·m/mm 조합 |
| `Replay` (로컬) | 감천·정릉·도동·서울 광역·군포 다필지의 Near ON/OFF와 감천·정릉의 Contour |

fixture 재생은 현재 API나 전국 공급 가능성 검사가 아니다. [데이터 제약](../../../docs/topo/data.md)을 유지하며 허용오차는 물리 단위와 표현 계약에 근거한다.

## 실사용·외부 연동 확인

아래 항목은 [검사 유지 원칙](../../../dev/aha/build-and-test.md#검사-유지-원칙)에 따라 별도로 확인한다. 패널 도킹·표시는 [공통 확인](../../aha/checks/README.md#실사용외부-연동-확인)을 따른다.

| 재확인할 변경 | 준비·방법 | 기대 결과 |
|---|---|---|
| VWorld 요청·인증·응답 해석 또는 공급원 변경 | 검증용 KEY와 시험 문서에서 해당 지도·검색·필지·XY/전체 생성 경로를 대표 범위로 실행하고 현재 응답과 비교한다. | 지원 응답은 계약대로 쓰고 인증·자료 누락은 실패나 경고로 드러난다. 표본 성공을 전국·모든 레벨에 확대하지 않는다. |
| 지형·도로·건물 표현 또는 표시 변경 | 경사·단차·근접 건물이 있는 고정 입력으로 만들고 Rhino에서 회전·확대해 확인한다. Contour·기초 분리의 객체 유형과 레이어도 확인한다. 표시 이상은 실제 형상과 Render Mesh를 나누어 원인을 확인한다. | 실제 형상에 의도하지 않은 틈·돌출·중복 면이 없고 판·대지·도로·기초 관계가 계약에 맞는다. 자연스러움은 [원천과 모델링 한계](../../../docs/topo/data.md) 안에서 판단한다. |

범위 입력은 직접 편집과 100m 방향 조절·잘못된 값 거절을 구분한다. 패널 검사는 접힌 고급 설정에서도 지형 요청 한도와 독립 입력 오류를 판정하고, 지도 검사는 작업 잠금 중 초기화와 복귀 갱신·Reload 차단을 확인한다. 실제 입체 검사는 양의 체적뿐 아니라 예상 체적과 다른 유효 solid도 거절한다.
