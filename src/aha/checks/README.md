# AHA 공통 검사

[개발 안내](../../../dev/aha/README.md) · [계약](../../../dev/aha/contract.md) · [MCP 규약](../../../dev/aha/mcp.md) · [공통 실행 방법](../../../dev/aha/build-and-test.md#회귀-검사-실행)

실행은 저장소 루트의 프로젝트 가상환경에서 한다.

```sh
.venv/bin/python src/aha/checks/run.py
.venv/bin/python check.py --tool aha --build-rhino
```

Rhino 전체 진입점은 [run_rhino.py](run_rhino.py)다. Rhino Python에서 `main("aha")`를 호출하면 공통 호스트 검사만 실행한다. 실제 로드된 AHA·검사 DLL과 현재 빌드의 MVID가 달라지면 재시작을 요구한다. 터미널·Rhino는 같은 등록 검증·선택·결과 집계 규칙을 사용한다.

| 보호하는 계약·실패 조건 | 검사 |
|---|---|
| JSON-RPC 입력·revision·잘못된 타입·중첩/배열 내부 중복 키·손상/과대 요청·Host/Origin | `core/Program.cs` |
| 작업 완료·결과 복사·준비 형상 소유권·문서별 예약·취소/시간 초과/폐기/종료 구분·생성 재시도/중복 방지·종료 기록 수명·동시 등록 한도 | `core/JobChecks.cs` |
| 서버 ON/OFF·같은 포트 적용·반복 교체·충돌 복구·기존 포트 반환 | `lifecycle/` |
| 등록 누락·중복·빈 선택·실패/시간 초과/환경 미준비 집계·동시 실행·이전 보고서 거절·빌드 일치 | `runner/test_runner.py` |
| 공통 버전·툴바 명령·라이선스 포함·패키지 필수/제외 파일·빌드 일치 | `package/check_package.py`, `package/test_package.py` |
| 문서·객체 대상 고정·문서별 잠금·삭제/닫힘·사용자 문서 보존 | `native/RuntimeNativeChecks.cs` |
| 설치본의 MCP 도구 계약·등록 패널·HTTP Info 변경/재조회·충돌·잘못된 업무 값이 있는 대지의 발견과 정정·XY/전체 Topo와 Eum 근거 읽기의 등록·설정 스키마·안전한 입력 거절·HTTP 준비/생성 상태와 실제 객체의 일치 | `native/RuntimeWireChecks.cs` |

일반 검사는 제품 소스를 직접 포함해 컴파일한다. 서버 수명 검사의 Rhino·설정 경계는 메모리 fixture이며 소켓은 임시 loopback 포트다. 호스트 검사는 설치된 AHA를 참조하고 스스로 만든 headless 객체만 수정한다. 개인 KEY·설정과 기존 MCP 서버를 변경하지 않는다.

패키지 생성 후 다음으로 실제 archive를 검사한다.

```sh
.venv/bin/python src/aha/checks/package/check_package.py outputs/packages/aha-0.0.1-rh8_35-mac.yak
```

등록 원본은 각 도구의 `checks/suites.json`이다. `.csproj`와 Python의 `test_*.py`·`check_*.py`, Node의 `test_*`·`check_*`·`*-checks` (`.js`·`.cjs`·`.mjs`)는 검사 진입점 이름으로 사용하며 미등록 파일이 있으면 실행을 거절한다. 보조 파일은 이 이름을 사용하지 않는다. 등록 경로는 소유자의 `checks/` 안에 있어야 하며 종류·계층·인수·시간 제한도 확인한다. 새 실행 검사는 파일과 등록을 함께 추가한다.

`suites.json`은 실행 진입점을 등록한다. 기존 묶음 안의 사례를 추가·이동·삭제할 때는 해당 진입점에서 사례가 실제로 호출되는지 확인하고, 진입점이 바뀔 때 등록을 갱신한다.

실행 로직은 `runner/`, 등록·선택은 `runner/catalogue.py`, 결과 집계는 `runner/outcome.py`를 터미널과 Rhino가 공유한다. 현재 빌드를 확인하는 `native/BuildIdentity.cs`는 도구별 Rhino 빌드에서도 함께 준비한다. `runner/test_runner.py`는 임시 프로세스와 모의 Rhino 경계로 실행기 자체를 검사하며 실제 도구의 호스트 검사를 대신하지 않는다.

실행마다 새 식별자를 기록한다. Rhino의 개별 결과는 해당 실행의 새 디렉터리에만 기록하며 이전 실행의 보고서는 받지 않는다. 선택·준비 실패는 `unavailable`로 기록하며 이전 `passed`를 현재 결과로 남기지 않는다. 동시 요청은 진행 중인 실행의 결과를 덮어쓰지 않고 별도의 실행 불가 결과를 반환한다.

터미널 시간 초과는 실패이며 현재 macOS에서는 자식 프로세스까지 종료한다. Rhino의 시간 초과는 이후 검사를 시작하지 않고 실패로 기록한다. 진행 중인 호스트 작업을 외부에서 강제 취소할 수 없으므로 그 작업의 종료 보고서가 오기 전에는 새 실행을 거절한다. 작업이 종료되지 않는 경우 Rhino를 재시작해야 한다. 시간 초과 후 늦게 도착한 성공 결과가 실패를 통과로 바꾸지는 않는다.

## 실사용·외부 연동 확인

아래 항목은 [검사 유지 원칙](../../../dev/aha/build-and-test.md#검사-유지-원칙)에 따라 별도로 확인한다.

| 재확인할 변경 | 준비·방법 | 기대 결과 |
|---|---|---|
| 패키징·설치 안내 또는 검증 대상 Rhino·OS 변경 | [설치본 확인](../../../dev/aha/build-and-test.md#설치본-확인)을 마친 환경에서 사용자 설치 안내대로 도구모음과 명령으로 패널을 연다. | 개발 폴더에 의존하지 않고 아이콘·패널 자원이 표시되며 안내한 경로로 도구를 열 수 있다. |
| 패널 배치·공통 UI 또는 Rhino·Eto 표시 환경 변경 | 변경한 패널을 실제 Rhino에서 도킹·분리하고 좁은 폭과 일반 사용 폭에서 표시·입력을 확인한다. | 주요 입력·상태·버튼이 잘리거나 가려져 사용을 막지 않고 의미를 구별할 수 있다. |
| 문서 종료 연결 또는 Rhino 호스트 변경 | 별도 시험 창에서 Topo 준비 후 그 창만 닫고 작업 상태와 준비 자원 해제를 확인한다. Headless 문서는 Rhino의 정적 종료 이벤트를 발생시키지 않으므로 자동 서비스 검사는 설치본의 종료 처리기를 직접 실행한다. | 실제 `CloseDocument` 연결이 대상 작업을 취소하고 다른 문서 작업은 보존한다. |
| MCP 전송·등록·연결 안내 또는 호환 확인 대상 클라이언트 변경 | 시험 문서를 열고 실제 로컬 HTTP MCP 클라이언트에 Setting의 주소를 등록해 도구 목록과 `aha_context` 호출 결과를 확인한다. | 클라이언트에서 도구를 발견하고 결과를 읽을 수 있다. 해당 클라이언트·버전에서 확인한 범위이며 다른 AI 제품의 자연어 실행까지 보증하지 않는다. |
