# Topo 개발

Topo는 한국의 선택 필지와 주변 지형·도로·건물을 Rhino 모델로 만드는 도구다. VWorld 자료 취득과 형상 계산을 백그라운드에서 끝낸 뒤, 별도 생성 요청으로 전체 모델 또는 Near/Far 모델을 추가한다. Near는 연속 지형 또는 선택적인 Contour 판으로 표현하며, 건물은 공통 지반 통계로 본체와 기초를 배치한다. 형상과 확보한 객체 정보를 제공하며 법규 판정·주소 보강·생성 후 자동 동기화는 맡지 않는다.

평면 정보만 필요한 호출자는 [XY 생성](contract.md#xy-생성)을 사용할 수 있다. 같은 범위의 필지·도로 지목 필지·건물 윤곽을 Z=0 Curve로 제공하며, 전체 모델과 평면 준비·좌표·정보 기록·문서 반영을 공유한다. DEM과 입체 계산은 실행하지 않는다.

패널에서 직접 사용하거나 MCP로 호출해도 업무 검증과 모델 생성은 같은 서비스를 사용한다. `api`와 [shared](../../src/topo/shared/README.md)는 이 도구 내부의 취득·UI와 계산 구분이다. 별도 후행 도구나 중간 Rhino 모델을 요구하지 않는다.

## 읽기 경로

| 문서 | 읽을 때 |
|---|---|
| [계약](contract.md) | 입력·출력·다른 도구와의 연결을 확인하거나 동작을 변경할 때 |
| [설계](architecture.md) | 취득·형상 계산·패널·문서 반영을 구현하거나 오류 원인을 찾을 때 |
| [검사 안내](../../src/topo/checks/README.md) | 보호하는 계약과 실행할 회귀 검사를 찾을 때 |
| [History](HISTORY.md) | 관련 선택의 배경이나 과거 오류를 조사할 때 |
| [사용법](../../docs/topo/README.md) · [데이터와 한계](../../docs/topo/data.md) | 사용자 관점의 동작과 원천 자료의 제약을 확인할 때 |

## 소스 진입점

| 위치 | 책임 |
|---|---|
| [TopoService.cs](../../src/topo/api/rhino/TopoService.cs) | 패널·MCP 공통 준비·명시적 생성 서비스 |
| [Core](../../src/topo/api/rhino/Core/) | VWorld HTTP·완전 수집·건물 분류·DEM |
| [TopoPanel.cs](../../src/topo/api/rhino/TopoPanel.cs) · [TopoMap.html](../../src/topo/api/rhino/TopoMap.html) | Eto 호스트와 지도 입력·표시 |
| [shared/Core](../../src/topo/shared/Core/) | 좌표·대지와 근접 영역·도로 평면·높이장·Contour 단면과 높이 구간 |
| [shared/Rhino](../../src/topo/shared/Rhino/) | 형상 생성·레이어·문서 반영·Info 기록 |
| [PreparedModel.cs](../../src/topo/shared/Rhino/PreparedModel.cs) | 고정 문서 기준과 준비 형상의 소유·해제 |

Info 저장 형식은 [Info 계약](../info/contract.md), KEY 저장은 [Setting 계약](../setting/contract.md), 문서·작업 수명은 [공통 실행 계약](../aha/contract.md), 통신은 [MCP 안내](../aha/mcp.md)가 담당한다. VWorld 필지 요청을 변경하면 이를 KEY 검증에 사용하는 Setting도 함께 확인한다.
