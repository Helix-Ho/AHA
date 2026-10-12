# Setting 계약

설정은 사용자 환경에 저장하며 Rhino 모델과 설치 폴더에 넣지 않는다. KEY와 일반 설정은 별도 저장소를 사용한다. `AhaSettings`가 다른 도구의 공개 접근 경계다.

## VWorld KEY

| API | 입력·결과 |
|---|---|
| `GetVWorldKey()` | 보안 저장소의 KEY 문자열. 미등록이면 빈 문자열 |
| `HasVWorldKey()` | KEY 존재 여부. macOS 존재 확인은 KEY 본문을 가져오지 않는다. |
| `SaveVWorldKey(key)` | 앞뒤 공백을 정리한 비어 있지 않은 문자열을 저장·갱신 |
| `DeleteVWorldKey()` | 저장된 KEY 삭제 |
| `SupportsSecureKeyStorage` | 현재 OS의 보안 저장소 지원 여부 |
| `VWorldKeyState`, `VWorldKeyChanged` | 값·해시를 노출하지 않는 상태와 변경 알림 |

현재 보안 저장소 구현은 macOS의 Security.framework `SecItem` API다. 서비스는 `AHA.VWorld`, 계정은 `api-key`다. KEY를 명령행으로 전달하거나 `.env`·환경변수를 탐색하지 않으며 평문 대체 저장소를 만들지 않는다. 지원하지 않는 OS나 저장소 접근 실패에서는 저장을 성공으로 처리하지 않는다.

KEY는 모델 속성·일반 설정 파일·MCP 결과·복사 주소·로그에 포함하지 않는다. 이를 소비하는 Topo도 같은 보안 경계를 유지한다.

패널을 열면 현재 저장값을 읽어 숨김으로 표시한다. 저장된 KEY는 입력란을 읽기 전용으로 두고 저장 버튼을 끄며 삭제·검증·보기 기능을 제공한다. 패널에서 교체하려면 삭제 후 새 KEY를 저장한다. 이 UI 규칙과 공개 `SaveVWorldKey`가 기존 KEY 갱신도 할 수 있다는 점을 구분한다.

### 상태와 비동기 검증

| 상태 | 의미 |
|---|---|
| `Unknown` | 저장소 존재 상태를 아직 확인하지 않음 |
| `Missing` | 미등록 |
| `Registered` | 등록됨. 이 실행에서 검증 성공을 확인하지 않음 |
| `Validated` | 현재 revision의 검증 요청 성공 |
| `ValidationFailed` | 현재 revision의 검증 실패. 연결·서비스 권한 문제도 포함하므로 KEY 무효를 단정하지 않음 |

상태 tracker는 KEY 값이나 해시 없이 revision으로 오래된 검증 응답을 거절한다. 저장·삭제 성공은 상태를 갱신하며 실패한 쓰기는 revision을 바꾸지 않는다. 미저장 입력을 검증한 결과는 저장된 KEY 상태를 바꾸지 않는다.

KEY 검증은 Topo의 VWorldClient로 서울시청 공개 필지 한 건을 요청한다. 이 요청의 성공은 모든 VWorld API·지역·권한의 성공을 보장하지 않는다.

패널의 저장소 작업은 직렬화하고 화면 수명과 검증 세대를 구분한다. 패널이 숨겨지면 KEY 입력·연결 결과 메시지와 진행 중 검증을 정리한다. 이미 시작된 OS 작업은 끝날 수 있으므로 재열기 시 저장소를 다시 읽고 이전 화면의 응답을 그대로 표시하지 않는다. 상태 공유는 해당 Rhino 프로세스 안에서 이뤄진다.

## 일반 설정

사용자 `ApplicationData/AHA/settings.json`에 다음 두 값을 저장한다.

| 필드 | 타입·기본값 |
|---|---|
| `mcpEnabled` | boolean, `false` |
| `mcpPort` | 정수 1024~65535, `18940` |

`SaveMcpSettings(enabled, port)`는 두 값을 한 번에 저장한다. 파일은 임시 파일을 완성한 뒤 교체하고 macOS에서는 사용자만 읽고 쓸 수 있는 권한을 부여한다. 저장 실패는 오류로 반환하고 기존 파일을 유지한다.

손상되거나 읽을 수 없는 설정을 조회할 때는 원본을 덮어쓰지 않는다. OFF·기본 포트를 제공하고 `GeneralSettingsWarning`으로 문제를 알린다. 사용자가 명시적으로 설정을 저장하면 새 값으로 파일을 교체한다.

일반 설정은 사용자별 공유 파일이다. 여러 Rhino 프로세스의 독립 설정 원장을 만들지 않는다. 실행 중인 서버는 현재 주소를 유지하고 재적용·재시작 시 저장된 설정을 사용한다. 설정 저장과 서버 시작 성공은 서로 다르며 실제 실행 상태를 따로 표시한다.

## MCP 연결 지원

서버 수명·포트 교체·작업 취소는 [AHA 공통 계약](../aha/contract.md)과 [MCP 규약](../aha/mcp.md)이 소유한다. Setting은 McpControl을 통해 프로세스당 서버 하나의 실행 상태·주소·마지막 도구 호출을 표시하고 설정 적용을 요청한다. 포트 교체는 기존 서버를 내린 뒤 새 서버를 시작하며 실패하면 꺼진 상태다. 같은 포트 재적용은 실행 중 작업을 취소하지 않는다.

주소 복사는 실행 중인 서버 주소를 사용하고, 서버가 꺼졌거나 시작에 실패했으면 저장된 설정 주소를 복사하며 꺼진 상태임을 알린다. URL은 사용자정보·query·fragment가 없는 로컬 loopback HTTP 주소여야 한다. 클라이언트 설정 JSON의 입력·생성·병합이나 클라이언트 설정 파일 편집은 제공하지 않는다.

서버 응답 확인은 로컬 통신 확인이며 외부 클라이언트 등록이나 실제 업무 호출 성공과 다르다. Setting에는 KEY·설정을 외부에서 변경하는 MCP 도구를 공개하지 않는다. 특정 AI 서비스 전용 연결이나 로그인도 담당하지 않는다.

## 의존 계약과 검사

KEY 검증만 [Topo](../topo/contract.md)의 VWorld 필지 클라이언트를 사용한다. Topo는 반대로 AhaSettings에서 저장된 KEY를 읽는다. KEY의 보관·표시·revision은 Setting, 필지 요청은 Topo가 소유한다. MCP 실행은 [AHA 공통](../aha/contract.md)에 의존하며 Info와 EumCrawl의 실행에 KEY를 요구하지 않는다.

구현 진입점은 [README](README.md)의 파일 표를 따른다. [검사 안내](../../src/setting/checks/README.md)는 저장소 CRUD·실패, 설정 파일 보존, 복사 주소 검증, 오래된 검증 응답·패널 재열기를 연결한다. OS Keychain 권한과 실제 VWorld 가용성은 이 고정 입력 검사의 범위와 구분한다.
