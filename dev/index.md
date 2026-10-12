# AHA 개발 인덱스

AHA는 독립적으로 사용할 수 있는 도구를 조합하는 Rhino 플러그인이다. 아래 소유자 이름은 `src/`, `dev/`, `docs/`에서 동일하다. README는 개발 안내, 계약은 다른 도구와 호출자가 의존하는 동작의 원본이다. 상세 규칙은 연결된 계약에서 관리한다.

| 소유자 | 책임·입력 → 결과 | 개발·계약·사용·구현·검사 |
|---|---|---|
| `aha` | 문서·객체 조회와 도구 호출 → 대상 고정·작업 상태·결과; 패널·MCP·패키지 공통 기반 | [개발](aha/README.md) · [계약](aha/contract.md) · [사용](../docs/aha/README.md) · [소스](../src/aha/) · [검사](../src/aha/checks/README.md) |
| `topo` | 선택 필지·범위 → 평면 XY 컨텍스트; 모델 옵션 추가 → 주변 지형·도로·건물과 객체 정보 | [개발](topo/README.md) · [계약](topo/contract.md) · [사용](../docs/topo/README.md) · [소스](../src/topo/) · [검사](../src/topo/checks/README.md) |
| `info` | 객체·정보·수정 요청 → 검증된 User Text와 Undo | [개발](info/README.md) · [계약](info/contract.md) · [사용](../docs/info/README.md) · [소스](../src/info/) · [검사](../src/info/checks/README.md) |
| `eum-crawl` | PNU → 원문 구조 또는 수치 후보; 대상·선택 항목 → Info 적용 | [개발](eum-crawl/README.md) · [계약](eum-crawl/contract.md) · [사용](../docs/eum-crawl/README.md) · [소스](../src/eum-crawl/) · [검사](../src/eum-crawl/checks/README.md) |
| `setting` | KEY·일반 설정 → 사용자 저장소·상태·MCP 연결 설정 | [개발](setting/README.md) · [계약](setting/contract.md) · [사용](../docs/setting/README.md) · [소스](../src/setting/) · [검사](../src/setting/checks/README.md) |

## 연결과 변경 범위

아래 화살표는 사용하는 쪽에서 제공하는 쪽을 향한다. 서비스 호출과 저장 형식 공유는 서로 다른 의존성이다. 필드·오류 규칙과 외부 응답 검증은 각 사용 도구의 계약에서 확인한다.

| 연결 | 의존하는 내용 |
|---|---|
| AHA 공통 → 각 도구 | 패널·MCP 진입과 업무 서비스 호출. 공개 MCP 기능과 소유자는 [MCP 안내](aha/mcp.md)에서 연결한다. |
| 도구 → AHA 공통 | 문서별 변경 잠금·UI 스레드 실행. 외부 호출의 대상 지정과 긴 작업 수명. |
| Topo → Setting | VWorld KEY 읽기와 인증 상태. |
| Topo → Info | 생성 객체가 사용하는 `aha.info` 저장 형식. CRUD 패널 실행을 요구하지 않는다. |
| EumCrawl → Info | 후보 미리보기의 현재 정보 읽기와 선택 항목의 공통 쓰기. 원문 읽기는 Info에 접근하지 않으며 Topo 실행이나 VWorld KEY도 요구하지 않는다. |
| Setting → AHA 공통 | MCP 서버 시작·정지·상태 조회. 서버 수명은 공통 기반이 소유한다. |
| Setting → Topo | KEY 검증에 사용하는 VWorld 클라이언트. 모델 생성은 실행하지 않는다. |
| Topo → VWorld / EumCrawl → 토지이음 | 지리 자료 취득 / PNU별 공개 조회·파싱. 외부 서비스 의존성이다. |

객체 간 자동 동기화나 별도의 모델 원장은 없다. 연결된 계약을 바꿀 때 관련 소유자까지 범위를 넓혀 검사·문서를 함께 확인한다. 도구 내부 구조만 바꾸면 해당 소유자 안에서 정리한다.
