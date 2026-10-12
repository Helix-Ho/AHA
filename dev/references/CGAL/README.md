# CGAL — 삼각망 단면

삼각망을 평면으로 절단하고 교선을 연결하는 알고리즘 자료다. 검토일은 2026-10-10이며 소스는 **v6.1**로 고정했다. AHA의 의존성이나 채택한 엔진을 뜻하지 않는다.

## 원문과 확인한 내용

- [Polygon_mesh_slicer 설명](https://doc.cgal.org/latest/PMP_Boolean_operations/classCGAL_1_1Polygon__mesh__slicer.html): 변의 AABB 트리를 재사용하며 축에 수직인 평면에는 별도 계산 경로가 있다. 열린 단면도 반환하고, 공면 삼각형 사이의 내부 변은 출력하지 않는다.
- [v6.1 구현](https://github.com/CGAL/cgal/blob/v6.1/Polygon_mesh_processing/include/CGAL/Polygon_mesh_slicer.h): 교점의 연결을 근접 좌표 탐색 대신 원래 정점·변 식별자와 면의 인접 관계로 구성한다. 계산된 그래프를 방향 있는 polyline으로 분해한다.
- 해당 파일의 라이선스 표기는 **GPL-3.0-or-later OR LicenseRef-Commercial**이다. 알고리즘을 검토한 것이며 소스를 복사하거나 제품에 포함하지 않는다.

## AHA에서 참고할 범위

[Topo Contour 계산](../../topo/architecture.md#contour-계산과-출력)의 단면 생성 대안을 검토할 때 참고한다. 여러 레벨에 같은 연결 구조를 재사용하고, 공면·정점 접촉을 원래 인접 관계로 처리하는 방식을 비교할 수 있다. 반환 polyline만으로 판의 외곽·구멍과 점 접촉 영역의 소유권이 모두 결정되는 것은 아니다.

현재 Topo의 셀별 적응 삼각형은 이웃 셀의 변 분할 수가 다를 수 있다. 위 알고리즘을 적용하려면 공유 변을 일치시키거나 원래 셀 변의 교점 소유권을 먼저 정의해야 한다. 이는 AHA 입력 구조에 대한 검토 사항이며 CGAL 자체의 실패를 뜻하지 않는다.
