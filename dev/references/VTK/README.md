# VTK — FlyingEdges2D

VTK의 구조화 격자 등고선 추출기다. Topo에서 공유 변의 교차점을 반복 계산하지 않고 경계만 생성하는 방법을 검토할 때 참고한다. VTK 전체나 이 필터를 제품 의존성으로 채택한 자료는 아니다.

- 검토: 2026-10-10, [공식 배포 v9.7.1](https://vtk.org/download/), [버전 고정 구현](https://github.com/Kitware/VTK/blob/v9.7.1/Filters/Core/vtkFlyingEdges2D.cxx).
- 설명: [FlyingEdges2D API](https://vtk.org/doc/nightly/html/classvtkFlyingEdges2D.html). 이 API 페이지는 nightly이므로 구현 판단은 위 버전 고정 소스를 기준으로 한다.
- 라이선스: 검토 파일의 SPDX는 BSD-3-Clause이며 [VTK Copyright](https://github.com/Kitware/VTK/blob/v9.7.1/Copyright.txt)를 따른다. 로컬 원본이나 실행 의존성은 두지 않는다.

## 참고할 구조

변의 분류·출력 개수를 먼저 계산하고 prefix sum으로 버퍼를 할당한다. 공유 변은 한 번만 교차 계산하며, 행에서 등고선이 없는 구간을 제외한다. 동일 좌표를 따로 생성한 뒤 위치로 병합하는 과정을 피한다. 이 원리는 특정 출력 프로그램에 의존하지 않는다.

## 직접 대체 시 확인할 차이

- 결과는 선분이며, 판에 필요한 채워진 영역·외곽·구멍 소유 관계를 직접 제공하지 않는다. 영역 경계를 따라 닫는 처리가 별도로 필요하다.
- v9.7.1의 `EdgeCases`는 고정 16개 분류표를 사용한다. 안장 경우 6·9도 고정 연결이며 bilinear asymptotic decider를 실행하지 않는다.
- 변 교차점은 선형 보간한다. `ProcessXEdge`는 `z < level`을 below로 분류하여 같은 높이는 above로 둔다. 셀 내부 직선 연결이 정확한 bilinear 등고 곡선이라는 뜻은 아니다.
- 검토 구현의 `NewPoints`와 출력 배열은 `float`다. 입력 높이가 double이어도 AHA의 좌표 정밀도가 자동 유지된다고 가정할 수 없다.
- 공식 설명은 길이 0 선분이 생길 수 있다고 명시한다. 공유 변 ID가 있어도 같은 레벨의 여러 변 교차점이 한 정점에 모이는 퇴화 사례는 별도로 다뤄야 한다.

Topo에는 라이브러리 전체 도입보다 공유 변 ID·선할당·활성 구간 순회 원리를 독립적인 계산에 적용하는 후보로 적합하다. 높이장의 해석과 동등성, 영역 폐합, 전체 처리 시간은 별도 검증 대상이다. 현재 계약은 [Topo](../../topo/contract.md)에 있다.
