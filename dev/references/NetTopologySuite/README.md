# NetTopologySuite

[원본 저장소](https://github.com/NetTopologySuite/NetTopologySuite)

제품이 사용하는 패키지 버전은 **2.6.0**이며 원본은 [Aha.csproj](../../../src/aha/rhino/Aha.csproj)에서 관리한다. 별도 소스 clone은 없다.

[Topo](../../topo/README.md)의 평면 기하와 삼각분할에 사용한다. 이 라이브러리는 Rhino의 NURBS·Mesh 생성 자체를 맡지 않으며, 공유 경계와 고도·법선·입체 유효성은 Topo의 책임이다. 수치적으로 애매한 입력과 형상 보존은 [Topo 검사](../../../src/topo/checks/README.md)에서 별도로 확인한다.

프로젝트 라이선스는 BSD-3-Clause다. 2026-10-11에 [2.6.0 공식 License.md](https://raw.githubusercontent.com/NetTopologySuite/NetTopologySuite/v2.6.0/License.md)와 제품 패키지의 [`THIRD_PARTY_NOTICES.txt`](../../../src/topo/api/rhino/THIRD_PARTY_NOTICES.txt)를 대조했다. 해당 파일의 JTS 및 포함 코드 고지도 함께 보존한다. 패키지 변경은 라이선스·고지와 일반·퇴화·경계 입력의 영향을 함께 확인한다.

## 평면 합집합의 입력 조건

2026-10-10에 기반 프로젝트 JTS 1.20.0의 [OverlayNG](https://locationtech.github.io/jts/javadoc/org/locationtech/jts/operation/overlayng/OverlayNG.html)와 [CoverageUnion](https://locationtech.github.io/jts/javadoc/org/locationtech/jts/operation/overlayng/CoverageUnion.html)을 확인했다. 일반 평면 교차와 이미 연결 관계가 맞는 영역 집합의 합집합은 다른 문제다. CoverageUnion은 겹치지 않는 다각형들이 동일한 변을 공유하거나 끝점에서만 만나야 하며, 입력 유효성을 대신 검사하지 않는다. 이 조건이 없는 입력은 실패하거나 잘못된 결과가 나올 수 있다.

Topo의 [등고 단면 계산](../../../src/topo/shared/Core/ContourPaths.cs)에서 반복 합집합을 줄이는 방법을 검토할 때 참고한다. 셀별 적응 분할의 긴 변·짧은 변을 일치시키기 전에는 이 최적화의 전제를 만족한다고 가정하지 않는다. JTS 문서는 알고리즘 조건의 근거이며 AHA가 해당 API를 채택했다는 뜻은 아니다.
