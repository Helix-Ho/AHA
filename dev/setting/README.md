# Setting 개발

Setting은 사용자별 VWorld KEY와 일반 설정의 진입점이며 로컬 MCP의 상태·주소 복사·응답 확인을 제공한다. 특정 AI 클라이언트에 종속되지 않는다. 인증정보는 모델·설치 폴더와 분리하고, 서버의 실행 상태와 저장된 설정을 구분한다.

## 읽을 문서

- [계약](contract.md): KEY·일반 설정·비동기 상태·보안·MCP 연결 지원.
- [공통 계약](../aha/contract.md) · [MCP 규약](../aha/mcp.md): 서버 수명과 외부 통신은 AHA 공통 기반이 소유한다.
- [사용법](../../docs/setting/README.md) · [선택적 개발 기록](HISTORY.md).

## 구현과 검사

| 위치 | 책임 |
|---|---|
| [AhaSettings.cs](../../src/setting/rhino/AhaSettings.cs) | 공개 설정 진입점 |
| [CredentialAccess.cs](../../src/setting/rhino/CredentialAccess.cs) · [MacKeychain.cs](../../src/setting/rhino/MacKeychain.cs) | KEY 저장소 경계와 macOS 구현 |
| [VWorldKeyState.cs](../../src/setting/rhino/VWorldKeyState.cs) · [SettingsStore.cs](../../src/setting/rhino/SettingsStore.cs) | KEY 상태·revision과 일반 설정 파일 |
| [McpAddress.cs](../../src/setting/rhino/McpAddress.cs) | 복사할 로컬 MCP 주소 검증 |
| [SettingPanel.cs](../../src/setting/rhino/SettingPanel.cs) | 마스킹·저장·검증·서버 상태·주소 복사·응답 확인 |

[검사 안내](../../src/setting/checks/README.md)는 실제 KEY 대신 메모리 저장소와 임시 설정 파일을 사용한다. 소켓·서버·포트 수명 검사는 [AHA 공통](../../src/aha/checks/README.md)에 둔다.
