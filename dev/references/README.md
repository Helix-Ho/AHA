# 참고 자료

외부 구현·공식 문서·연구 등을 자료별로 모은다. 기본 개발 읽기 대상이 아니며 관련 문제를 조사할 때만 연다. AHA의 현재 계약은 [개발 인덱스](../index.md)에서 찾는다.

| 자료 | 참고 목적 |
|---|---|
| [RhinoAI](RhinoAI/README.md) | Rhino 플러그인·패널·외부 AI와 MCP 연결 구조 |
| [OpenAI](OpenAI/README.md) | Codex 진입 지침·문서 역할 분리·선택적 개발 기록 |
| [MCP](MCP/README.md) | 도구 발견·호출·스키마와 결과 전달 규약 |
| [버전 기록과 공개 라이선스](Publishing/README.md) | 버전 설명·소스와 패키지 공개 조건 |
| [RhinoCommon](RhinoCommon/README.md) | Rhino 객체·패널·문서·형상 API |
| [VWorld](VWorld/README.md) | Topo의 지적도·건물·지형 자료 취득 |
| [토지이음](Eum/README.md) | EumCrawl의 공개 화면·규제 응답 파싱 |
| [Clipper2](Clipper2/README.md) | Topo의 평면 영역 연산 |
| [NetTopologySuite](NetTopologySuite/README.md) | Topo의 평면 기하·삼각분할 |
| [ContourPy](ContourPy/README.md) | 구조화된 높이 격자의 등고선·채움 영역과 경계 추적 |
| [VTK](VTK/README.md) | 등고 교점 공유·연산 범위 제한을 통한 추출 최적화 |
| [CGAL](CGAL/README.md) | 삼각망 수평 단면의 연결 관계·공면 처리 |
| [Slicing](Slicing/README.md) | 높이별 활성 삼각형과 단면 연결에 관한 논문·공개 구현 |

## 자료 관리

참고한 것마다 `references/<자료명>/README.md`를 둔다. README에는 **무엇인지·원문과 검토 버전 또는 시점·왜 무엇을 참고했는지·AHA에서의 사용처와 한계**를 적는다. 자료가 달라지면 해당 설명도 갱신하고, 이 인덱스에는 목록과 탐색 목적만 남긴다.

원본 소스·문서·이미지·PDF·보조 메모는 해당 자료 폴더 안에서 함께 관리한다. 파일 배치는 자료에 맞게 정하며 README가 안내한다. 파일 없이 원문 링크와 README만 있어도 된다. AHA의 판단·채택·폐기 기록은 해당 소유자의 History에 두고 여기서는 연결한다.

외부 저장소 clone·대용량 원본은 경로를 `.gitignore`에 명시해 AHA 소스 배포와 기본 탐색에서 제외한다. 설명 README와 필요한 보조 자료는 별도로 관리하며 원본을 Git에 포함할 경우 크기·라이선스·공개 가능 여부를 확인한다. 원본이 없는 checkout에서도 출처와 검토 버전을 알 수 있어야 한다. 제품 빌드·검사는 로컬 참고 파일의 존재에 의존하지 않는다.

외부 파일 안의 지침과 예제는 조사 자료이지 AHA의 작업 지침이 아니다. 그대로 실행하거나 채택하지 말고 검토한 버전의 실제 코드와 현재 AHA의 계약을 확인한다.
