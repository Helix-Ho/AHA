# 삼각망 슬라이싱

높이별 단면 생성과 연결에 관한 논문·구현 자료다. 검토일은 2026-10-10이다. Topo의 [Contour 계산](../../topo/architecture.md#contour-계산과-출력) 대안을 판단할 때 읽으며, 현재 알고리즘의 계약은 [Topo 계약](../../topo/contract.md#contour와-대지-본체)이 소유한다.

## Minetto 외, 2017

- **An Optimal Algorithm for 3D Triangle Mesh Slicing**, *Computer-Aided Design* 92, 1–10. [DOI](https://doi.org/10.1016/j.cad.2017.07.001) · [저자 페이지](https://pessoal.dainf.ct.utfpr.edu.br/rminetto/projects/slicing/) · [저자 제공 PDF](https://www.inf.ufpr.br/murilo/public/CAD-slicing.pdf) · [관련 저장소](https://github.com/rminetto/slicing)
- 삼각형의 높이 범위로 활성 목록을 관리한다. 등간격 절단면의 교선 생성은 삼각형 수 n, 레벨 수 k, 실제 교차 선분 수 m에 대해 `O(n+k+m)`이다. 전체 Topo의 평면 처리·정리·Rhino 출력 비용까지 포함한 복잡도가 아니다.
- 논문은 정점 좌표를 짝수 ε, 절단 높이를 홀수 ε 배수로 반올림해 일치 상황을 피한다. 폐곡선 구성의 단순 해시 절차는 정상적인 닫힌 manifold와 각 교점의 두 이웃을 전제로 한다.

**AHA 적용 시 판단:** 활성 레벨 관리는 참고할 수 있지만 높이 이동은 그대로 적용하지 않는다. 정확한 `k×간격`·평지 레벨을 유지하면서 정점·변·공면 일치를 명시적으로 처리해야 한다. 점 접촉의 여러 영역을 하나의 고리로 임의 연결하지 않는다. 논문·소스의 재배포나 코드 복제는 수행하지 않았다.

## PrusaSlicer 2.9.6

- [고정 버전 TriangleMeshSlicer.cpp](https://github.com/prusa3d/PrusaSlicer/blob/version_2.9.6/src/libslic3r/TriangleMeshSlicer.cpp). 파일은 **AGPLv3 or higher**를 명시한다. 알고리즘 참고이며 제품에 코드를 복사하지 않는다.
- `IntersectionReference`와 `chain_lines_by_triangle_connectivity`는 교점에 원래 정점·변 ID를 보존하고 ID로 선분을 잇는다. 같은 변의 보간 방향도 통일한다.
- `slice_facet`는 수평 삼각형과 평면 위 변·정점을 구별한다. 삼각형의 아래쪽 변과 위쪽 변의 소유를 구분해 중복 단면을 제어한다. 레벨 순회에서는 삼각형의 최소·최대 높이를 이진 탐색해 만날 레벨만 찾는다.

**AHA 적용 시 판단:** ID 기반 연결과 레벨 범위 제한을 참고한다. 인쇄용 상하 경계 소유 규칙을 그대로 복제하지 않고 AHA의 `높이 이상 영역`과 평지 보존 규칙에 맞춰 검사해야 한다. 손상 STL 복구나 근접점 연결 경로를 정상 높이장 처리의 전제로 삼지 않는다.

## Agarwal 외 — 지형 등고선의 연결·포함 관계

- **I/O-Efficient Algorithms for Computing Contour Maps on Terrains**. [저자 제공 PDF](https://www.cs.toronto.edu/~sadri/publications/contours.pdf).
- 정렬된 폐곡선과 그 포함 관계를 함께 다루며, 임계 높이에서 등고선이 점으로 접촉하거나 사라지는 현상을 설명한다. 설명에서는 서로 다른 정점 높이를 가정하므로 AHA의 평지·동일 높이 입력을 그대로 해결하는 근거는 아니다.

**AHA 적용 시 판단:** 고리 연결과 외곽·구멍 판정을 별개로 보지 않는 이론적 참고다. 메모리를 넘는 대규모 지형의 I/O 최적화가 목적이므로 현재 AHA에 전체 contour-tree·외부 메모리 체계를 도입할 근거로 사용하지 않는다.
