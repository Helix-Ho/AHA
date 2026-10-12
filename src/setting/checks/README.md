# Setting 검사

[개발 안내](../../../dev/setting/README.md) · [계약](../../../dev/setting/contract.md) · [공통 실행 방법](../../../dev/aha/build-and-test.md#회귀-검사-실행)

```sh
.venv/bin/python src/setting/checks/run.py
.venv/bin/python check.py --tool setting --build-rhino
```

Rhino에서 [run_rhino.py](run_rhino.py)를 실행한다. 등록은 [suites.json](suites.json)에서 관리한다.

| 보호하는 계약·실패 조건 | 검사 |
|---|---|
| MCP 복사 주소의 로컬 HTTP 제한·인증정보 포함 주소 거절 | `Program.cs` |
| KEY CRUD·저장소 실패·revision·미등록/검증/교체 상태 | `Program.cs` |
| 일반 설정 기본값·포트 검증·파일 권한·저장 실패 시 기존 값 보존 | `Program.cs` |
| 마스킹 상태·진행 중 KEY 변경·늦은 검증 응답·숨김/재열기 | `native/SettingNativeChecks.cs` |

실제 MacKeychain을 호출하지 않는 메모리 KEY 저장소와 임시 설정 파일을 쓴다. 호스트 검사는 설치된 SettingPanel에 저장소·검증 완료 시점을 주입한다. 소켓과 서버 수명은 [공통 검사](../../aha/checks/README.md)가 소유한다. OS Keychain 권한·실제 VWorld 가용성·도킹 외관은 이 자동 검사의 범위가 아니다.

## 실사용·외부 연동 확인

아래 항목은 [검사 유지 원칙](../../../dev/aha/build-and-test.md#검사-유지-원칙)에 따라 별도로 확인한다. 패널 표시·외부 MCP 클라이언트 연결은 [공통 확인](../../aha/checks/README.md#실사용외부-연동-확인), 현재 VWorld 응답은 [Topo 확인](../../topo/checks/README.md#실사용외부-연동-확인)을 따른다.

| 재확인할 변경 | 준비·방법 | 기대 결과 |
|---|---|---|
| MacKeychain 연동·권한 처리 또는 검증 대상 macOS 변경 | 격리된 시험용 macOS 계정에서 실제 KEY 대신 시험 문자열을 저장하고 Rhino 재시작 후 읽기·삭제를 확인한다. 접근 거절을 재현할 수 있는 환경에서는 거절 상태도 확인한다. | 실제 OS 저장소에서 재시작 후 값이 유지되고 삭제가 반영된다. 접근 실패를 미등록·저장 성공으로 처리하거나 평문 저장으로 우회하지 않는다. |
