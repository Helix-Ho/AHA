# ContourPy

구조화된 2차원 격자에서 등고선과 채워진 등고 영역을 계산하는 C++/Python 라이브러리다. Topo의 판 단면을 내부 삼각형들의 합집합 없이 직접 추출하는 알고리즘을 검토할 때 참고한다. 제품 의존성으로 채택한 자료는 아니다.

- 검토: 2026-10-10, [v1.4.0](https://github.com/contourpy/contourpy/releases/tag/v1.4.0).
- 원본: [저장소](https://github.com/contourpy/contourpy), [버전 고정 소스](https://github.com/contourpy/contourpy/blob/v1.4.0/src/base_impl.h).
- 라이선스: [BSD-3-Clause](https://github.com/contourpy/contourpy/blob/v1.4.0/LICENSE). 로컬 원본이나 실행 의존성은 두지 않는다.

## 참고할 구조

`serial`·`threaded`는 격자의 마스크·경계 정보를 캐시하고, 첫 순회에서 점·루프 수와 구멍 관계를 계산한 뒤 버퍼를 할당한다. 두 번째 순회에서 경계를 추적해 결과를 채운다. 내부 삼각형을 Polygon으로 만든 뒤 일반 Boolean으로 모두 합치는 과정은 필요하지 않다. [알고리즘 설명](https://contourpy.readthedocs.io/en/v1.4.0/description.html)

`filled(h, +inf)`는 높이 h 위 영역을 반환한다. `OuterOffset`은 각 외곽과 그 구멍을 묶어 반환하므로 판 단면으로 변환하기 쉽다. `multi_filled(levels)`는 인접 레벨 사이의 띠들을 반환하며, 각 층을 받치는 누적 단면과는 다르다. [호출 규약](https://contourpy.readthedocs.io/en/v1.4.0/api/contourpy/ContourGenerator.html), [출력 형식](https://contourpy.readthedocs.io/en/v1.4.0/user_guide/calculate/fill_type.html)

## AHA와 다른 의미

- 영역 분류는 `lower < z <= upper`다. `base_impl.h`의 `z_to_zlevel`과 문서 모두 같은 높이를 below로 분류한다. 따라서 모든 높이가 h인 평지는 `filled(h, +inf)`에서 비지만, 현재 Topo의 `z >= h` 단면에는 포함된다. 판 레벨 계약을 확인하지 않고 교체할 수 없다.
- 기본 보간은 변 위의 선형 보간이며 셀 내부 교차점은 직선으로 잇는다. 안장 셀은 네 모서리의 평균 높이로 연결 방향을 정한다. 정확한 bilinear 등고선이나 bilinear 안장 판정과 동일하지 않다. [보간 설명](https://contourpy.readthedocs.io/en/v1.4.0/user_guide/calculate/z_interp.html)
- `quad_as_tri=True`는 평균 높이의 중앙점을 넣은 네 삼각형을 사용한다. 현재 Topo의 높이오차를 제한한 적응 삼각 근사를 대체하면 표면 해석도 달라진다. [삼각형 모드](https://contourpy.readthedocs.io/en/v1.4.0/user_guide/calculate/quad_as_tri.html)
- 정확히 같은 등고 높이에서는 중복 점과 면적 0 Polygon이 나올 수 있다. 외곽·구멍 형식을 제공한다고 모델링용 유효성이 자동 보장되는 것은 아니다. [공식 한계](https://github.com/contourpy/contourpy/blob/v1.4.0/docs/user_guide/calculate/limitations.md)

bilinear 해석을 유지하는 별도 등고 추출기를 검토할 때는 [Nielson·Hamann, The Asymptotic Decider (1991)](https://graphics.stanford.edu/courses/cs164-10-spring/Handouts/paper_p83-nielson.pdf)의 안장 연결 판정을 구분해 참고한다. 삼각형의 선형 보간을 선택하면 셀 안장 모호성은 없지만, 어떤 삼각분할을 선택했는지가 표면을 정의한다.

도로·대지·기초 구멍의 평면 연산, 정확한 Near 원호, 100mm 상세 정리와 Extrusion 출력은 이 라이브러리의 책임이 아니다. 비교 시 계산 시간뿐 아니라 평탄 레벨·안장·구멍·점 접촉·영역 경계와 높이 오차를 확인한다. 현재 계약은 [Topo](../../topo/contract.md)에 있다.
