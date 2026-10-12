# Topo 계약

Topo는 선택 필지와 범위의 자료를 취득해 형상을 준비하고, 명시적 생성 요청으로 지정한 Rhino 문서에 독립된 모델 묶음을 추가한다. 준비만으로 객체·레이어·선택을 바꾸지 않는다. 기존 모델을 수정하거나 후행 도구를 요구하지 않는다. 자료 취득에는 Setting에 저장된 VWorld KEY가 필요하며 이미 준비한 결과의 생성에는 필요하지 않다. 패널은 KEY 미등록 시 지도·검색·새 연산 입력을 잠그며, 등록됐다는 이유만으로 서비스 검증에 성공한 것으로 표시하지 않는다. KEY·SDK 인증값은 지도 HTML, 객체 정보, MCP 결과, 오류 메시지에 포함하지 않는다.

## 호출과 입력

패널은 조회한 필지 도형·원점·범위를 `TopoService.PrepareStart`에 전달한다. MCP는 PNU 목록을 `PrepareByPnusStart` 또는 `PrepareXYByPnusStart`에 전달한다. 두 경로는 같은 작업 관리·입력 검증·계산을 사용하고 `TopoService.Create`로 준비 결과를 반영한다.

| MCP 도구 | 입력과 결과 | 문서 변경 |
|---|---|---|
| `aha_topo_search` | `query`로 주소 후보를 조회한다. 결과는 `Label`, `Pnu`, `Location: { Lon, Lat }`을 가진 객체의 배열이다. 검색은 제한된 후보 목록이며 필지 도형이나 완전 수집 결과가 아니다. | 없음 |
| `aha_topo_parcel` | `pnu` 또는 `longitude`와 `latitude` 쌍 중 하나로 필지 하나를 조회한다. 결과는 `pnu`, `address`, `label`, `polygons`다. 경계에서 여러 필지가 조회되면 선택을 확정하지 않고 실패한다. | 없음 |
| `aha_topo_xy_prepare_start` | `document_id`, 중복 없는 19자리 PNU 문자열 목록 `pnus` 1~100개, 범위·평면 설정을 받아 XY를 준비한다. 작업 핸들을 반환하며 상태는 `aha_job_status`로 확인한다. | 없음 |
| `aha_topo_prepare_start` | 같은 문서·PNU 입력에 범위·형상 설정을 받아 전체 모델을 준비한다. 작업 핸들을 반환한다. | 없음 |
| `aha_topo_create` | `job_id`로 준비 결과의 생성을 요청한다. 같은 작업 ID에서 상태·결과를 조회한다. | 지정 문서에 준비한 전체 또는 XY 모델 추가 |

표의 결과는 MCP 전송 외피 안의 데이터다. 배열인 검색 결과는 `content`의 `text`에 JSON으로 담기며 객체 결과는 `structuredContent`에도 제공된다. 전송·오류 외피는 [AHA 공통](../aha/README.md)이 소유한다.

`polygons`는 **polygon → ring → `{ Lon, Lat }` 점 객체** 순서의 배열이며 각 polygon의 첫 ring이 외곽, 나머지가 구멍이다. GeoJSON의 좌표 숫자 배열이 아니다. 현재 JSON의 필드 대소문자를 임의로 통일하지 않는다. 공개 스키마와 변환 경로는 [MCP 등록 코드](../../src/aha/mcp/McpTools.cs)에 있다.

PNU 기반 실행의 원점은 선택 전체 경계의 경위도 bounding box 중심이다. 모델 가로·세로는 두 생성 경로에서 사용한다. 아래 지형·건물 옵션은 전체 모델의 설정이며 XY 생성은 별도 절에 정의한 평면 설정만 받는다.

모델 가로·세로 `width_m`, `depth_m`는 각각 50~3,000m이며 기본값은 400m다. 이 출력 범위는 선택한 모든 필지와 활성화된 근접 영역을 포함해야 한다. 범위를 벗어난 선택을 잘라서 설계 대지로 만들지 않는다. 생성할 모든 건물의 전체 지반에 필요한 필지·DEM 취득 범위는 내부적으로 확보하며, 출력 범위나 근접 영역을 함께 늘리지 않는다.

`terrain_level`은 서비스·MCP에서 정수 3~15, 기본 12이며 패널은 12~15를 제공한다. DEM 64타일·높이 표본 100만 개는 공급원 규정이 아닌 AHA의 자원 보호값이다. 설정에서 알 수 있는 요청량을 먼저 검사하고 건물 취득 후 넓어진 전체 지반 범위도 DEM 요청 전에 다시 검사한다. 높은 레벨을 지정했다고 해당 지역의 공급을 보장하지 않는다.

| `options` 항목 | 기본값·유효 조건·효과 |
|---|---|
| `terrain_nurbs`, `buildings_nurbs` | 각각 `false`(Mesh). Near OFF이면 전체, ON이면 Far의 형식이다. |
| `near` | `true`. 근접 지형을 닫힌 입체로, 근접 건물을 Extrusion으로 만든다. `false`는 전체에 선택한 형식을 적용한다. |
| `near_ratio`, `near_depth_m` | 2배·30m. Near ON일 때 배수는 1.5 이상, 깊이는 양수다. |
| `contour`, `contour_interval_m` | `false`·1m. Contour는 Near ON일 때만 유효하며 간격은 양수, `near_depth_m`는 간격의 두 배보다 커야 한다. 주변 지형을 등고판으로 만들고 해당 건물 자리를 판 단위로 굴착한다. |
| `separate_foundations` | `false`. 모든 건물에 적용한다. OFF이면 본체·기초를 하나로, ON이면 평균 지반을 경계로 별도 객체·레이어에 만든다. |
| `parapets`, `parapet_width_m`, `parapet_height_m` | `true`·0.3m·0.3m. Near 건물 지붕의 외곽·중정 가장자리 안쪽에 별도 Extrusion을 만든다. 폭·높이는 양의 유한값이다. Near OFF에서는 적용 상태가 OFF다. 패널 치수 입력은 mm다. |
| `roads` | `true`. OFF이면 도로 영역·높이 보정·도로 형상을 만들지 않지만 필지는 수집한다. |
| `separate_roads` | `true`. OFF이면 연속 땅 위에 같은 높이의 도로 면을 겹친다. |
| `lower_roads`, `road_drop_m` | `true`·0.2m. 도로 표시·분리·내림이 모두 ON일 때만 0 이상의 단차를 적용한다. |
| `unknown_height_m`, `floor_height_m` | 6m·3m, 모두 양수. 높이 추정에 사용한다. |
| `min_building_area_m2` | 10㎡, 0 이상. 원천 건물 전체 외곽에서 구멍을 뺀 면적이 기준 이상이면 크기 필터를 통과한다. |

명시적으로 전달하는 MCP 수치는 비활성 설정이어도 공개 스키마의 유한값·범위를 만족해야 한다. 설정의 적용 여부는 위 표를 따른다. 사용하는 수치는 유한해야 한다. 길이·높이 입력은 m, 면적은 ㎡다. Rhino 문서의 단위가 없거나 사용자 정의 단위라서 표준 변환을 할 수 없으면 생성을 거절한다. 입력·대상 검증은 [TopoService](../../src/topo/api/rhino/TopoService.cs)와 [BuildingRules](../../src/topo/shared/Core/BuildingRules.cs)가 맡는다.

## XY 생성

`TopoService.PrepareXYByPnusStart`는 `aha_topo_xy_prepare_start`의 실행 기능이다. 선택 필지와 지정 범위의 필지·건물을 취득하고 문서 단위를 유지한 Z=0 Curve를 준비한다. DEM·도로 높이 보정·건물 높이 추정·면·입체 생성은 실행하지 않는다. 전체 모델과 PNU 검증·범위 검증·선택 필지 병합·평면 준비·정보 기록·준비 수명·문서 반영을 공유한다. 실제 객체는 `aha_topo_create` 이후에 존재한다.

`width_m`, `depth_m`는 각각 기본 400m, 50~3,000m이며 선택 필지 전체를 포함해야 한다. XY의 `options`는 `roads`(기본 `true`)와 `min_building_area_m2`(기본 10㎡, 0 이상)만 받는다. Near·형상·높이 설정은 받지 않는다.

새 `aha_topo_…` 아래 `XY`를 표시하고 다음 하위 레이어를 순서대로 만든다. 도로 설정이 OFF이면 `RoadBoundary`는 만들지 않지만 도로 지목 필지도 `ParcelBoundary`에는 남는다.

| 레이어 | 의미 |
|---|---|
| `XY::TargetBoundary` | 인접 선택 필지를 합친 대지 외곽과 구멍. 떨어진 대지는 독립 경계다. 외곽만 `aha.info.site=true`이며 확보한 원필지별 상세 정보를 기록한다. |
| `XY::ParcelBoundary` | 지정 범위로 잘린 원천 필지 경계와 기본 정보 |
| `XY::RoadBoundary` | 도로 지목 필지의 원천 경계. 실제 포장 경계·차선을 뜻하지 않는다. |
| `XY::BuildingFootprints` | 유효한 원천 건물 윤곽과 기본 정보. 범위와 면적으로 교차하는 건물을 포함하고 지상 건물에 최소 면적을 적용한다. 확인된 지하 시설은 크기 필터 없이 분류한 참조선으로 남긴다. |

선택 대지는 범위 포함을 검사한 뒤 합집합 폐곡선을 보존한다. 주변 원천 선은 모델 범위에서 자르되 범위 경계에 가짜 닫힘 선분을 추가하지 않는다. 100mm 모델 정리는 적용하지 않는다. 전체 Topo의 XY도 같은 평면 기준을 사용하므로 입체 생성 실패가 유효한 원본 윤곽을 없애지 않는다. XY 전용은 지형 높이나 생성한 건물 높이를 정보에 넣지 않는다. 계산 면적을 원천 토지의 `area_m2`로 대체하지 않는다.

완료 결과 `XYResult`는 다음 항목을 제공한다.

| 항목 | 의미 |
|---|---|
| `ParentLayerId`, `LayerName`, `ObjectIds`, `CreatedCount` | 새 레이어와 실제 생성 Curve 식별자·개수 |
| `SelectedParcels`, `SiteComponents` | 선택 원필지 수와 병합한 독립 대지 수 |
| `Coordinates` | `Origin: {Lon,Lat}`, `SourceCrs`(EPSG:4326), `XAxis`(East), `YAxis`(North), `DocumentUnits`, `DocumentUnitsPerMetre`, `ElevationMetres`(0), 지역 근사 설명 `Method` |
| `Bounds` | 조회 범위의 `West`, `South`, `East`, `North` 경위도 |
| `ObjectCounts` | 역할별 실제 Curve 수. `TargetBoundary`, `ParcelBoundary`, `RoadBoundary`, `BuildingFootprint` 중 생성한 역할을 포함한다. |
| `AppliedSettings` | 실제 가로·세로 m, 도로 참조선 여부, 최소 건물 면적 ㎡ |
| `InvalidBuildingIds`, `Warnings` | 유효하지 않은 평면 도형 식별자와 처리 안내 |

좌표 원점은 전체 생성과 같은 선택 범위 중심이다. 좌표 설명은 기존 모델과의 자동 정렬·연결·갱신 기능을 뜻하지 않는다. 문서 잠금·안전 지점 취소·한 번의 Undo·반영 실패 복구는 전체 생성과 같은 공통 경로다. 구현은 [XYModel](../../src/topo/shared/Rhino/XYModel.cs)과 [공통 평면 참조선](../../src/topo/shared/Rhino/PlanarReferences.cs), 회귀 검사는 [XY 형상](../../src/topo/checks/native/XYChecks.cs)·[XY 서비스](../../src/topo/checks/service/XYServiceChecks.cs)에 있다.

## 준비 수명과 명시적 생성

문서당 진행 또는 준비 중인 Topo 작업은 하나다. 기존 결과를 생성하거나 명시적으로 폐기한 뒤 새 연산을 시작한다. 상태는 `running → ready → creating → completed`이며 실패·취소는 `failed`·`cancelled`다. 전체 모델·XY 준비에는 자동 시간 제한을 두지 않으며 명시적 취소·대상 종료로 중단한다. 개별 통신 제한·DEM 취득 한도와 수치 표현 범위는 유지한다. 준비 형상은 메모리에만 보관하고 자동 만료·별도 저장·Rhino 재시작 복원을 제공하지 않는다.

- 연산 시작 시 문서 식별자·단위·허용오차와 입력을 고정한다. 취득·형상 계산 중 다른 객체를 편집하거나 다른 문서에서 작업할 수 있다. 무거운 형상 계산은 프로세스당 하나씩 실행한다.
- 패널 닫기·탭 전환·MCP 중지는 작업을 취소하지 않는다. 패널 재표시와 `aha_context`로 같은 작업을 찾는다. `aha_job_cancel`은 연산 취소 또는 준비 결과 폐기다.
- `ready`의 `summary`에는 `mode`, `applied_settings`, `bounds`, `coordinates`, `prepared_object_count`, `object_counts`, `selected_parcels`, `site_components`, `warnings`가 담긴다. 전체 모델에는 `terrain_level`도 있다. 실제 객체·레이어 GUID와 생성 개수는 `completed.result`에만 존재한다. 적용 설정 등 중첩 모델 값은 기존 PascalCase를 유지한다.
- 생성 전에 원래 문서의 생존·단위·허용오차를 확인한다. 활성 문서 전환·일반 편집·저장은 준비 결과를 무효화하지 않는다. 다른 Rhino 명령 또는 AHA 변경 작업이 진행 중이면 `ready`를 유지하고 생성 재시도를 안내한다.
- 동일 작업의 중복 생성은 한 번만 반영한다. 완료 후 재요청하면 기존 결과를 반환하고 Undo 후에도 다시 생성하지 않는다. 생성·폐기·실패·문서 종료 시 준비 형상을 해제한다.

공통 상태·작업 기록·종료 정책의 원본은 [AHA 공통 계약](../aha/contract.md)이며 형상 소유권은 [설계](architecture.md#실행과-취득)와 [PreparedModel](../../src/topo/shared/Rhino/PreparedModel.cs)에 있다.

## 전체 모델 결과와 문서 변경

`aha_topo_prepare_start`는 작업 핸들을 반환한다. `aha_topo_create`로 생성한 뒤 `state=completed`일 때 `result`가 생성 결과다. 작업 외피는 `job_id`, `document_id`, `state`, `progress`, `input`, `summary`, `result`, `error` 등의 소문자 이름을 쓰며 `ModelResult`는 **`ParentLayerId`, `ObjectIds`, `CreatedCount`, `LayerName`, `AppliedSettings`, `Warnings` 등 PascalCase**를 쓴다. 전송과 작업 수명은 [AHA 공통 계약](../aha/contract.md)이 소유한다.

- `AppliedSettings`는 `ModelOptions`의 PascalCase 이름으로 실제 적용 설정을 제공한다. 비활성 도로 단차는 0, 비활성 Near 수치와 Contour 간격은 기본값으로 정규화한다.
- `Origin`, `Bounds`, `DocumentUnits`, `DocumentUnitsPerMetre`, `TerrainLevel`은 좌표와 취득 조건이다.
- `ObjectIds`는 생성한 모든 객체 GUID다. `SiteObjectIds`는 Contour의 대지 본체 GUID이며 다른 모드에서는 비어 있다. 별도 대지 원장이나 객체 간 자동 연결을 뜻하지 않는다.
- `ContourPlateCount`는 출력한 Extrusion 판 개수이며 기단·지지판을 포함한다. `ContourOmittedAreaSquareMetres`는 생략한 상세 평면의 합집합 면적으로 층별 면적 합이 아니다. `PartitionReassignedAreaSquareMetres`는 공유 평면 분할 후 역할 재배분 면적이다. 원천 평면 정리 후 잔여 조각의 재배분은 `PlanCleanup` 각 항목의 `ReassignedAreaSquareMetres`에 별도로 집계한다. 다른 집계·오차 항목은 [ModelResult](../../src/topo/shared/Rhino/RhinoModel.cs)를 따른다. `SurfaceField`·`RoadGrading` 계산 객체는 직렬화하지 않는다.

매 실행은 새 `aha_topo_…` 레이어에 독립 모델을 추가하며 한 번의 Rhino Undo로 되돌린다. 실행 중 활성 문서가 바뀌어도 원래 지정한 문서에만 반영한다.

| 모드 | 상위 레이어 내부 순서 |
|---|---|
| Near OFF | `TargetBoundary`, `Surfaces`, `Buildings`, `XY` |
| Near ON·Contour OFF | `TargetBoundary`, `Near`, `Far`, `XY` |
| Contour ON | `Site`, `Near`, `Far`, `XY` |

Near/Far 내부는 `Surfaces`, `Buildings` 순이다. Surfaces 아래에서 땅·도로·Contour 기단·연결면을 구분하며 기초 분리 시 Buildings 아래 `Foundations`를 만든다. 상위 레이어만 펼치고 하위는 접는다. 전체 모델의 `XY`는 기본 숨김이다.

Contour OFF의 `TargetBoundary`에는 하위 레이어 없이 보정 지형에 투영한 대지경계를 직접 담는다. Contour ON은 투영 대지경계와 이 레이어를 만들지 않고 `Site`의 대지 본체로 구분한다. 모든 모드의 원본 XY 대지경계는 `XY::TargetBoundary`에 유지한다.

## 형상·좌표·고도

- 입력은 WGS84 경도·위도다. 내부는 지역 원점의 동·북 방향 m 좌표이며 Rhino 객체를 만들 때 문서 단위로 변환한다. 정식 측량 좌표계 변환은 아니다.
- DEM 표고에서 대지 중심 높이를 빼지 않는다. 공급원의 수직 기준은 미확인이다. 도로 보정은 DEM 노드의 변화를 최대 ±3m로 제한하며 도로 단차는 별도로 적용한다.
- Near는 독립 대지의 면적 중심에서 가장 먼 외곽까지의 거리 R에 지정 배수를 곱한 원이다. 겹친 원 묶음은 바깥 공통접선·원호로 감싸고 떨어진 묶음은 독립 영역으로 유지한다.
- 연속 지형 NURBS는 U/V 차수 1의 쌍선형 높이장이다. 셀 사이 C0 연속을 유지하며 C1은 보장하지 않는다. 표시 Mesh의 높이 오차 50mm·공유 경계 오차 1mm는 표현 기준이지 측량 정확도가 아니다.
- 모델 평면은 최소 폭 100mm·작은 영역 0.01㎡ 기준으로 정리한다. 일반 원천 평면 정리는 경계 주변 50mm 변경 띠 안에서 수행한다. 이 정리 이후 남은 독립 미세 조각도 같은 출력 구역의 이웃 역할로 재배분하며 50mm 한도를 다시 적용하지 않는다. Near/Far 분할로 생긴 작은 땅·도로 조각은 **같은 구역에서 가장 긴 경계를 공유하는 이웃 역할로 재배분**하며 이때 원천의 50mm 한도로 거절하지 않는다. Near·선택 대지경계와 원본 XY는 움직이지 않는다. 수직 도로 단차에는 100mm 기준을 적용하지 않는다.
- 땅·도로는 같은 보정 높이장을 쓴다. 도로는 도로 지목 필지이며 비도로·미확인 필지와의 중첩은 제외한다. 실제 포장·차선·교량·터널을 추정하지 않는다.

### 건물과 기초

생성 대상은 원천 전체 윤곽의 면적 중심이 출력 범위 안에 있는 유효한 지상 건물이다. 최소 면적 필터는 구멍을 뺀 원천 면적에 적용한다. 생성 대상을 한 번 확정한 뒤 **모든 건물의 전체 지반**을 확보하며 넓어진 지반 범위로 건물을 다시 조회하지 않는다. 입체는 전체 윤곽을 유지하므로 일부가 사각 출력 범위를 넘을 수 있다. 필지 안에 맞추기 위해 이동·축소하지 않는다.

원천 윤곽과 100mm 정리 윤곽이 모두 Near 내부에 있고 경계와 접촉하지 않아야 Near 건물이다. 접촉·교차 건물은 전체를 Far로 분류한다. 중정은 평면 정리로 메우지 않는다.

| 기준 | 동작 |
|---|---|
| 높이 H | 양수 실높이 → 양수 지상층수×층당 높이 → 기본 높이. 현재 공급원에는 실높이가 없어 뒤의 두 경로를 사용한다. |
| 지붕 | 계단화·굴착 전 연속 보정 지형의 수평 면적 가중평균 + H. 도로 분리 단차를 반영하며 Contour의 선택 대지 안에는 도로 단차를 적용하지 않는다. |
| 일반 기초 바닥 | 건물 아래 전체 출력 지반의 최저점−1m. Near OFF·Far도 같은 전체 지반 통계를 사용한다. |
| 본체·기초 구분 | 평균 지반 높이. 합침은 기초 바닥부터 지붕까지 직접 생성하고, 분리는 같은 윤곽의 본체·기초를 각각 만든다. |
| 표현 | Near는 실제 Rhino Extrusion. Far·Near OFF는 선택한 Mesh 또는 편집 가능한 형식을 직접 생성한다. 떨어진 외곽은 외곽별 객체로 만든다. |

다중 외곽·구멍을 포함한 전체 모델 윤곽으로 통계를 계산한다. 급경사에서 지붕 일부가 지형에 묻혀도 임의로 높이지 않는다. 양의 면적으로 겹친 유효 건물은 삭제하지 않는다. Near의 겹침 연결 묶음은 가장 낮은 기초 바닥을 공유하고 각 건물의 지붕·객체는 유지한다.

Contour OFF에서는 땅·건물이 겹친다. 독립 굴착 옵션은 없다. 기초는 모델 표현이며 실제 구조 기초·지하층·토공 설계가 아니다.

파라펫은 확정된 모델용 지붕 영역에서 안쪽 offset 영역을 뺀 띠를 기존 지붕 높이부터 돌출한다. 외곽을 넓히거나 중정 안으로 들어가지 않는다. 좁은 부분에서 띠가 만나면 합치고 내부가 사라지면 해당 지붕 전체를 돌출한다. 출력 프로파일 분해로 생긴 이음선에는 파라펫을 만들지 않으며 100mm 지형 상세 정리를 다시 적용하지 않는다. Near 건물에만 적용하고 Contour·기초 분리 설정과 무관하다. Far·Near OFF·XY 전용에는 생성하지 않는다.

파라펫은 `Near::Buildings::Parapets`의 별도 닫힌 Extrusion이다. 기존 건물 수·지형 입체 수와 높이 H는 바꾸지 않고 준비·생성 객체 수에 포함한다. 원천에서 취득한 실제 파라펫이 아닌 지정 치수의 모델 표현이며 객체 간 자동 동기화는 없다.

### Contour와 대지 본체

Contour ON의 주변 Near 지형은 기존 모델 Z의 일정 간격 Δ에 따라 만든 **수평 일정 단면의 닫힌 Extrusion 판**이다. 외곽·구멍·하단·상단을 평면 계산으로 정하고 직접 출력한다. 점 접촉 등으로 하나의 유효 단면이 되지 않으면 같은 위치를 유지한 여러 Extrusion으로 나눈다.

- 작은 등고 돌출·섬은 100mm·0.01㎡ 기준으로 생략해 아래 레벨이 드러나게 한다. 대지 제외와 도로·기초·Near 절단 뒤 생긴 좁은 연결부·독립 작은 판에도 같은 기준을 적용한다. 아래에서 제거한 영역은 위 판에도 반영해 떠 있는 판을 막는다. 아래판과 입체 BooleanUnion해 돌출부를 남기지 않는다. 건물 기초 구멍과 중정은 보호한다.
- 도로 분리 ON이면 땅·도로는 다른 판과 레이어다. 각 위치의 **노출 도로 상판만** 단차만큼 얇게 하며 아래 판을 통째로 내리지 않는다. 단차가 Δ 이상이면 위 판을 제거하고 다음 판을 남은 높이만큼 얇게 한다. 0두께 객체는 만들지 않는다.
- 도로 분리 OFF이면 연속 땅판 위에 도로 면을 겹치고 내림은 적용하지 않는다. 이 도로 면은 Extrusion이 아니다. 등고판과 교차한 뒤 생긴 미세 도로·땅 조각에도 같은 100mm·0.01㎡ 역할 재배분을 적용하되 판·기초 구멍·대지·Near 경계는 유지한다. 이 변경 면적은 `ContourOverlayReassignedAreaSquareMetres`에 기록한다.
- Near 외곽에는 계단화 전 연속 지형에서 기단 윗면 `L−2Δ`까지 내려오는 캡 없는 수직 NURBS 외벽을 둔다. 상단은 Far 쪽 도로 역할·내림값을 따른다. 원호·공통접선 위치를 유지하고 도로 단차에서 면을 나눈다. 판 옆면과의 겹침은 허용하며 수평 보정은 하지 않는다. 외벽은 `Near::Surfaces::Connections`에 두고 판·입체 수에 포함하지 않는다. 겹침 도로는 굴착·작은 조각 생략 후 남은 상면을 따른다.

Contour 굴착은 선택 대지에 접촉·교차하지 않는 Near 건물에만 적용한다. 건물 아래 굴착 전 노출판들의 바닥 중 가장 낮은 등고 레벨을 기초 바닥으로 정한다. 평지에서도 한 판을 파며, 도로 내림으로 0.8m가 된 상판은 그 판의 바닥까지 판다. 바닥 위 판에서 건물 윤곽을 완전히 빼고 그 아래 판은 남긴다. 판 내부에 부분 깊이 홈을 만들지 않는다. 작은 잔여부 생략으로 지반이 더 낮아지면 기초·겹침 묶음·굴착 구멍을 함께 낮춰 실제 판과 맞춘다. 이미 파인 바닥에서 추가 1m를 빼지는 않는다. Far나 대지에 걸친 건물은 굴착하지 않으며 실제 계단 지형보다 기초가 충분히 아래에 있도록 한다.

선택 필지를 합친 독립 대지마다 **계단화하지 않은 곡면 상면의 닫힌 NURBS 입체**를 `Site`에 만든다. 대지 안은 도로 구분·단차 없이 연속 지형으로 유지하고 원본 도로는 XY에 남긴다. 대지 본체는 자체 외벽을 가지며 주변 판과 맞추는 접속벽을 중복 생성하지 않는다.

도로 내림·굴착·선택 대지의 최저 높이를 포함한 최저 상세 등고 레벨을 L이라 할 때 하부는 다음과 같다. 깊이 D는 2Δ보다 커야 한다.

| 부분 | 높이 구간 |
|---|---|
| 지지판 2장 | `[L−Δ, L]`, `[L−2Δ, L−Δ]` |
| 두꺼운 기단 | `[L−D, L−2Δ]` |
| 대지 본체 바닥 | 기단 윗면 `L−2Δ` |

상세판·지지판에서는 대지 영역을 빼고 기단은 대지 아래까지 받친다. Contour OFF의 Near 땅·분리 도로는 상면 전체 최저점에서 D만큼 내린 공통 바닥을 가진 닫힌 NURBS 입체다. Far·Near OFF의 지형은 면이며, 겹침 도로는 모든 모드에서 면이다.

### 형상 검증과 화면 표시

Topo는 생성한 실제 Curve·Mesh·Brep·Extrusion의 유효성, 경계와 의도한 연결을 책임진다. 닫힌 입체의 열린 변(Naked edges), 누락된 면, 의도하지 않은 중복 면은 수정해야 할 생성 결함이다. Far 지형·겹침 도로·캡 없는 `Connections` 외벽의 열린 경계와 계약에 명시한 판·기초·외벽의 접촉 또는 겹침은 닫힌 입체의 결함과 구분한다.

닫힌 Extrusion은 자체 유효성 플래그뿐 아니라 실제 프로파일의 양의 면적·구멍 소유권, Brep 변 연결·다양체·양의 체적을 준비 단계에서 확인한다. 생성 단계에서는 형상을 다시 계산하지 않는다.

실제 Mesh 객체와 AHA가 구성한 Brep 표시 Mesh도 Topo의 검증 대상이다. Rhino가 Extrusion 등 원형에서 생성하는 화면용 **Render Mesh**와 사용자 표시 설정은 Rhino가 관리한다. 화면 이상만으로 실제 형상 결함을 단정하거나, Render Mesh 문제라는 이유로 실제 열린 경계·중복 면을 허용하지 않는다. Topo는 표시 설정을 강제하거나 별도 Render Mesh 제공자를 설치하지 않는다.

## 객체 정보와 후속 도구

저장 형식·CRUD·확장 필드 규칙은 [Info 계약](../info/contract.md)이 소유한다. Topo는 생성할 때 `aha.info`를 기록하고 이후 동기화하지 않는다. 별도 주소 조회로 보강하지 않고 이미 확보한 응답만 사용한다.

| 대상 | 생성 시 기록 |
|---|---|
| 투영 대지경계 / Contour 대지 본체 | 원필지별 PNU·지번주소·지번 표기·지목 코드의 상세 정보. 외곽 Curve 또는 대지 본체에 `site=true`. 구멍 Curve는 `false`. |
| XY 대지경계 | 같은 원필지 정보. 전체 모델은 `site=false`, XY 전용은 외곽만 `site=true`. |
| 주변 필지·도로 필지 XY | 기본 PNU·지번 표기·지목 코드. Near와 겹치면 확보한 지번주소를 더한다. |
| 생성 건물 본체·기초·XY 윤곽 | 같은 JSON. 기본 관리번호·PNU·지상층수·높이 H·높이 근거·분류 상태. Near는 확보한 명칭·주소·지하층수·분류 코드를 더한다. |
| 파라펫 | `kind=parapet`, `site=false`. 기술 속성에 `AHA.Role=Parapet`, 원건물 `AHA.FeatureId`, 폭·높이 `AHA.ParapetWidthMetres`·`AHA.ParapetHeightMetres`, 하단·상단 `AHA.GroundMetres`·`AHA.TopMetres`를 기록한다. 이름은 `Parapet_<건물 식별자>`다. |
| 땅·도로 판/면/입체 | 종류(`kind`)만 기록하며 병합 전 원필지를 집계하지 않는다. |

대상 대지는 Near OFF여도 상세 정보다. 그 밖에는 Near OFF이면 기본 정보다. 입체가 없는 유효한 XY 참조선에는 생성하지 않은 모델 높이를 넣지 않는다. PNU·주소를 추정하거나 건물관리번호에서 PNU를 잘라 만들지 않는다. 건물 도로명주소는 필지 지번주소와 구분한다. 생성 필드는 [TopoObjectInfo](../../src/topo/shared/Rhino/TopoObjectInfo.cs)를 따른다.

`aha.info.data.height_m`·`AHA.HeightMetres`는 H다. 실제 객체 바닥은 `AHA.GroundMetres`, 건물 기초 바닥은 `AHA.FoundationBottomMetres`, 평균·지붕·입체 높이는 `AHA.ReferenceMeanElevationMetres`·`AHA.RoofElevationMetres`·`AHA.ModelHeightMetres`로 구분한다. 역할·구역 기술 태그는 업무 정보와 별개다. `AHA.FeatureId`는 요청마다 바뀔 수 있는 원천 도형 ID이며 영구 식별자가 아니다.

## 실패·취소·보존

잘못된 입력, 불완전 페이지·건수·식별자 불일치, 누락 DEM, 인증·통신 실패, 닫힌 문서는 실패다. 건물 전체 지반에도 같은 DEM 64타일 한도·자료 검증을 적용한다. 정상 수집·수렴·형상 정제를 누적 요청·반복·객체 개수만으로 중단하지 않는다. 숫자·좌표 정밀도·배열 표현 범위와 실제 형상 검증은 유지하며 간격이나 형식을 자동 변경하지 않는다. 고도를 0으로 채우거나 다른 레벨·지역별 예외로 대체하지 않는다.

유효하지 않은 원천 건물 윤곽이나 모델 최소 상세보다 작은 윤곽은 식별자·이유를 `InvalidBuildingIds`·`Warnings`에 남긴다. 유효한 원본 XY는 보존한다. 정상적인 접촉·교차·작은 조각의 처리 실패는 구현에서 해결하며 사용자 입력 제약이나 원천 불량으로 돌리지 않는다. 내부 검증을 유지하고 유효한 생성 건물을 조용히 삭제하거나 잘못된 입체를 성공으로 반환하지 않는다. 입체 출력 실패 시 전체 반영을 중단하는 것은 잘못된 결과를 막기 위한 보호이며, 정상 입력의 실패를 지원 한계로 인정한다는 뜻이 아니다.

패널의 기본 설정은 모델 범위→근접 영역→파라펫→Contour 순서다. 형식·지형 표본 간격·도로·건물 옵션은 기본 접힌 고급 설정에 둔다. 접기·펼치기는 값을 바꾸지 않는다. 범위의 직접 입력은 50~3,000m와 소수 한 자리를 유지하며 버튼·↑/↓만 방향별 다음 100m 배수로 이동한다. 입력·확인 가능한 지형 요청량을 함께 판정해 부적합하면 연산을 막는다. 서비스는 취득 건물의 전체 지반 범위도 DEM 취득 전에 다시 검사한다.

형상 계산은 고정된 입력과 독립 형상으로 백그라운드에서 실행하며 문서 변경 잠금은 생성 구간에서만 사용한다. 취득·계산 슬롯 대기·평면 준비·도로 솔버·등고 위상과 레벨 순회·기초 수렴의 안전 지점에서 취소한다. native 함수 실행 중 즉시 중단을 보장하지 않으며 늦게 반환한 취소 결과는 폐기한다. 반영 진입 후에는 중간 취소하지 않고 한 번의 Undo 반영 또는 실패 복구를 끝낸다. 이미 반영한 결과는 완료로 유지하고 Undo로 되돌린다. 진행의 ‘Rhino 모델 추가’는 객체·레이어 반영이며 외부 문서 생성이 아니다.

검사와 고정 입력은 [Topo 검사 안내](../../src/topo/checks/README.md)에 연결되어 있다.
