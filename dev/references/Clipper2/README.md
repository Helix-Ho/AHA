# Clipper2

[원본 저장소](https://github.com/AngusJohnson/Clipper2) · [공식 문서](https://www.angusj.com/clipper2/Docs/Overview.htm)

제품이 사용하는 패키지 버전은 **2.0.0**이며 원본은 [Aha.csproj](../../../src/aha/rhino/Aha.csproj)에서 관리한다. 별도 소스 clone은 없다.

[Topo](../../topo/README.md)의 필지 합집합·도로/땅 분할·모델용 평면 정리에 사용한다. 라이브러리 연산 성공만으로 얇은 면·공유 경계·원본 보존이 충족되는 것은 아니므로 물리 단위와 모델링 허용 기준은 Topo가 소유한다. 관련 동작은 [Topo 검사](../../../src/topo/checks/README.md)에서 확인한다.

[Offset 구현](https://github.com/AngusJohnson/Clipper2/blob/main/CSharp/Clipper2Lib/Clipper.Offset.cs)의 유한 정밀도 반올림과 축소·확대 동작을 참고했다. Offset 결과를 반복 적용하면 정상 모서리도 달라질 수 있으므로, Topo는 폭 판정용 후보와 실제 출력 경계를 분리한다. 재현은 `ContourPrecisionChecks`에 두며 제품 패키지 버전은 위 참조를 따른다.

라이선스는 [Boost Software License 1.0](https://github.com/AngusJohnson/Clipper2/blob/main/LICENSE)이다. 2026-10-11에 사용 중인 NuGet 2.0.0의 라이선스와 제품 패키지의 [`THIRD_PARTY_NOTICES.txt`](../../../src/topo/api/rhino/THIRD_PARTY_NOTICES.txt)를 대조했다. 패키지를 바꾸면 라이선스·고지와 평면 연산 결과를 함께 확인한다.
