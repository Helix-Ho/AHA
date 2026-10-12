# AHA 공통 구조

제품 프로젝트는 [`Aha.csproj`](../../src/aha/rhino/Aha.csproj) 하나다. 공통 기반과 각 도구의 소스를 하나의 Rhino 플러그인으로 묶으며, 도구별 책임은 소스·문서·검사 소유권으로 구분한다. 배포를 위해 별도의 업무 구현을 만들지 않는다.

## 실행 경로

```text
Rhino 명령·아이콘 → 등록 패널 ────────────┐
                                         ├→ 도구 서비스 → 계산·외부 취득 → Rhino 반영
HTTP MCP → 프로토콜 → 입력 변환·도구 호출 ┘
                         └→ Jobs: 긴 작업의 상태·결과·취소
```

패널은 사용자 입력과 선택·표시 수명을 담당하고 MCP는 공개 입력을 서비스 입력으로 변환한다. 업무 검증·저장·계산은 도구에 둔다. Topo의 패널 선택값과 MCP PNU 입력처럼 진입 입력은 다를 수 있지만 공통 실행 기능으로 모인다. 이 경계는 [Topo 구조](../topo/architecture.md)를 따른다.

## 구성 요소

| 위치 | 책임 |
|---|---|
| [`Plugin.cs`](../../src/aha/rhino/Plugin.cs) | 시작 시 다섯 패널 등록, `AHA`·도구 명령과 Launcher 제공, 서비스 시작·종료와 문서 닫힘 연결 |
| [`ToolPanels.cs`](../../src/aha/rhino/ToolPanels.cs) | Rhino 등록 패널 열기. 별도 창이나 도킹 위치를 직접 관리하지 않음 |
| [`DocumentAccess.cs`](../../src/aha/rhino/DocumentAccess.cs) | 문서 생존 확인·문서별 변경 잠금·UI 스레드 전달 |
| [`Jobs.cs`](../../src/aha/rhino/Jobs.cs) | 공통 작업의 메모리 상태·준비 결과 소유권·취소 토큰·오류 안내 |
| [`McpControl.cs`](../../src/aha/rhino/McpControl.cs) | 설정에 따른 서버 수명·응답 확인·마지막 호출 상태 |
| [`mcp/`](../../src/aha/mcp/) | HTTP 수신·JSON-RPC·공개 도구 목록과 서비스 연결 |
| [`package/`](../../src/aha/package/) | 버전 적용 manifest·툴바·제품 파일의 Yak 패키징 |

플러그인은 Rhino 시작 시 로드되고, 첫 Idle에서 저장된 MCP 설정을 적용한다. 다섯 패널은 `PanelType.PerDoc` 등록 패널이다. 문서별 패널 인스턴스와 Container 위치·도킹·표시는 Rhino가 소유한다. 각 도구 패널은 숨김·닫힘·폐기 시 자신의 타이머·이벤트·비동기 표시 작업을 정리한다. Topo 준비 작업은 패널과 별개로 유지하며 다시 열면 같은 문서의 상태를 표시한다. 패널을 닫아도 활성화된 MCP 서버는 유지된다.

Launcher의 버전은 제목 행 오른쪽에 표시한다. 하단 로고는 [`LogoView.cs`](../../src/aha/rhino/LogoView.cs)가 남은 공간에 축소해 그리며 스크롤을 추가하지 않는다. 원본은 [`mono SVG`](../../src/aha/assets/logo/AHA_Logo_mono.svg)이고 패널에는 리소스로 포함한다. README는 같은 형상의 [흰색 SVG](../../src/aha/assets/logo/AHA_Logo_mono_white.svg)를 다크 테마에서 사용하며, 원본 로고 변경 시 함께 갱신한다. 패널 등록용 `Icons/aha.ico`와 RUI의 AHA 비트맵도 같은 SVG에서 변환하며 로고 변경 시 함께 갱신한다.

## 문서와 작업의 수명

`DocumentAccess`는 영속적인 모델 관리자가 아니다. 현재 문서의 runtime ID로 문서를 찾고 같은 문서의 AHA 변경을 겹치지 않게 한다. 서비스가 잠금 범위를 소유하고 `Dispose`로 반환한다. `RhinoDispatch`는 UI 스레드 콜백의 값과 예외를 Task 결과로 전달한다.

`Jobs`는 비동기 업무와 준비 결과를 소유한다. Topo 패널과 MCP는 같은 핸들을 사용하고, `PreparedJob`은 요약·명시적 생성·해제만 제공하여 공통 작업 관리가 형상 계산에 의존하지 않게 한다. Topo 서비스는 독립 형상을 백그라운드에서 준비하고, 생성 때만 UI 스레드와 문서 변경 잠금을 사용한다. 프로세스의 형상 계산 슬롯은 Topo가 소유한다. Jobs는 문서별 예약과 원자적 상태 전환을 담당한다.

준비 형상은 생성·폐기까지 유지하고 종료 JSON 기록과 수명을 분리한다. 조회는 JSON 복사본만 반환한다. Rhino 문서 닫힘은 해당 작업을 정리하고 플러그인 종료는 모든 준비 자원을 정리한다. 구체적인 상태·취소·재시도 의미는 [공통 실행 계약](contract.md#긴-작업)에 둔다.

`McpControl`은 프로세스당 서버 하나를 보유한다. 동일한 활성 포트 재적용은 서버를 다시 만들지 않는다. 변경 시 기존 서버를 먼저 종료하고 MCP로 시작한 EumCrawl 조회를 취소한 뒤 새 서버를 시작한다. 문서가 소유한 Topo 작업은 서버 교체·중지와 무관하게 유지한다. 시작 실패 시 다른 포트를 자동으로 고르지 않는다. 설정 주소, 서버 실행 여부, 서버 응답 확인, 실제 도구 호출 기록은 구분한다. 호출 기록의 ‘응답 완료’는 비동기 업무 전체의 완료가 아니므로 업무 결과는 Jobs에서 확인한다.

## 변경 경계

공통 기반은 [Info](../info/contract.md), [Topo](../topo/contract.md), [EumCrawl](../eum-crawl/contract.md), [Setting](../setting/contract.md)의 계약을 연결한다. HTTP·시간·보안 저장소·비동기 응답 완료 경계만 검사에서 필요한 범위로 제어한다. 실제 기본 경로를 유지하며 전역 테스트 모드나 UI/MCP별 업무 구현을 만들지 않는다.

통신과 등록은 [MCP 문서](mcp.md), 빌드·설치·회귀 검사와 빌드 동일성 확인은 [빌드와 검사](build-and-test.md)를 따른다.
