# Setting History

필요한 과거 판단을 찾아 읽는 기록이다. 현재 책임은 [개발 README](README.md), 동작의 약속은 [계약](contract.md), 기록 방법은 [History 기록 기준](../README.md#history-기록-기준)을 따른다. 아래 2026-10-05는 이전 개발 과정을 정리한 날짜이며 실제 결정일을 뜻하지 않는다.

## 2026-10-05 · Topo의 KEY 입력을 공통 설정으로 이동

Topo의 KEY 입력과 설치 경로 주변 `.env` 탐색은 설치·재설치 및 여러 패널의 상태 관리를 복잡하게 만들었다. Setting을 단일 설정 진입점으로 정하고 일반 설정과 인증정보를 분리했다. 현재 macOS에서는 KEY를 Keychain에 저장하며 모델이나 설치 폴더와 함께 보관하지 않는다.

저장된 KEY를 숨긴 상태로 다시 불러오되 변경은 삭제 후 신규 저장으로 단순화했다. 보안 저장소를 사용할 수 없을 때 평문 저장으로 우회하는 방식은 채택하지 않았다. 관련 경계는 [CredentialAccess](../../src/setting/rhino/CredentialAccess.cs), [MacKeychain](../../src/setting/rhino/MacKeychain.cs), [SettingsStore](../../src/setting/rhino/SettingsStore.cs)에 있다. 다른 OS를 지원할 때는 그 환경의 보안 저장소 구현을 별도로 검증할 필요가 있다.

## 2026-10-05 · RhinoAI 전용 연결에서 일반 MCP 연결 지원으로 변경

RhinoAI는 조사와 연결 검토에 사용한 클라이언트지만 AHA의 필수 의존성으로 삼지 않기로 했다. Setting은 로컬 MCP 주소와 복사 가능한 일반 연결 설정을 제공하고, 기존 설정에서는 AHA 항목만 갱신하도록 정리했다. 클라이언트의 설정 파일을 대신 편집하거나 다른 서버 설정을 덮어쓰지 않는다.

서버 실행·응답 확인·실제 외부 호출을 구분한 이유는 서버가 켜진 사실만으로 특정 클라이언트에서 AHA를 활용할 수 있다고 단정할 수 없기 때문이다. 포트와 서버 수명은 Setting에서 별도로 구현하지 않고 [AHA 공통 실행 구조](../aha/architecture.md)가 맡는다.

2026-10-09에는 패널을 KEY와 MCP의 설정·상태 확인에 집중하도록 JSON 입력·병합·전체 복사 도우미와 전용 검사를 제거했다. 주소 복사·응답 확인은 유지하며 현재 동작은 [Setting 계약](contract.md#mcp-연결-지원)을 따른다.

## 2026-10-05 · 저장 상태와 비동기 KEY 검증 응답의 수명 분리

검증 요청 이후 KEY가 삭제·교체되거나 패널이 닫힐 수 있으므로, 마지막으로 도착한 응답을 그대로 표시하면 현재 저장 상태를 잘못 나타낼 수 있었다. KEY 변경 revision과 패널 작업 수명을 확인해 오래된 검증 결과를 폐기하도록 했다. 저장·삭제가 실패했을 때 성공 상태를 발표하지 않는 것도 같은 이유다.

검증 실패에는 연결이나 서비스 권한 문제도 포함될 수 있으므로 KEY 자체가 무효라고 단정하지 않도록 했다. [VWorldKeyState](../../src/setting/rhino/VWorldKeyState.cs), [SettingNativeChecks](../../src/setting/checks/native/SettingNativeChecks.cs), [저장·상태 검사](../../src/setting/checks/Program.cs)에 변경·실패·재열기 경계를 재현했다. 개인 Keychain에 의존하지 않고 확인할 수 있도록 검사에서는 저장소와 검증 완료 시점을 제어한다.
