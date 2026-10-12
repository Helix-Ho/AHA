# Info 계약

Info는 ObjectAttributes User Text의 `aha.info` 키에 JSON 객체 하나를 저장한다. 이 문서는 그 저장 형식과 `InfoService`의 업무 계약을 정의한다. MCP의 전달 형식·오류 포장·수정 토큰은 [공통 MCP 규약](../aha/mcp.md)을 따른다.

## 저장 형식

최소 정보는 다음과 같다.

```json
{
  "version": 1,
  "kind": "object",
  "site": false,
  "data": {}
}
```

| 필수 루트 필드 | 의미와 검증 |
|---|---|
| `version` | 양의 JSON 정수. 현재 쓰기 지원 버전은 `1`이며 AHA 제품 버전과 독립적이다. |
| `kind` | 비어 있지 않은 문자열. 허용 목록으로 제한하지 않는 의미상 분류다. |
| `site` | boolean. `true`로 저장하려면 객체가 유효한 닫힌 Curve 또는 닫힌 Brep·Extrusion 입체여야 한다. Curve의 평면 조건을 추가로 검사하지 않으며 Mesh는 대지 지정 대상이 아니다. |
| `data` | 아래의 기본 필드와 확장 항목을 담는 JSON 객체. |

`kind`를 수정해도 Rhino의 Curve·Mesh·Brep 형상 타입은 바뀌지 않는다. 건물 입체와 XY 경계선은 모두 `building`일 수 있다. Info가 없는 객체에 패널이 표시하는 기본값 `object`는 저장 전에는 기록되지 않는다.

| `data`의 선택 필드 | 값과 단위 |
|---|---|
| `bcr_percent`, `far_percent` | 건폐율·용적률, JSON 숫자, % |
| `setback_m`, `max_height_m` | 이격거리·최고높이, JSON 숫자, m |
| `max_floors` | 층수, 정수인 JSON 숫자 |
| `notes` | 참고 메모 문자열 |
| `parcels` | 주소·PNU를 짝지어 담는 원필지 객체 목록 |

선택 필드는 생략과 `null`을 허용한다. 0은 입력값이며 미확인이나 제한 없음으로 해석하지 않는다. 기본 수치는 0 이상의 유한한 숫자여야 하고 숫자 문자열은 받지 않는다. 표현 범위보다 작은 비영 숫자를 0으로 바꾸거나, 반올림으로 소수 층수를 정수로 인정하지 않는다. 법규상 상한을 공통 형식에 추가하지 않는다.

### 원필지

`data.parcels`의 각 항목은 객체이며 항목 자체가 `null`일 수는 없다. 여러 필지를 합친 경계도 원필지마다 한 항목씩 기록한다.

| 항목의 선택 필드 | 값과 의미 |
|---|---|
| `pnu` | ASCII 숫자 19자리 문자열. Info는 이 자리수와 문자 형식을 검사한다. |
| `address` | 해당 PNU와 짝지어진 주소 문자열 |
| `land_category` | 지목 문자열 |
| `area_m2` | 0 이상의 유한한 JSON 숫자, ㎡. 자료에 기록된 면적이며 Rhino 형상 면적과 별개다. |
| `land_use_zones` | 용도지역·지구 객체. `national_plan`, `other_laws`에 각 그룹의 원문 문자열을 담는다. |

각 필드와 `land_use_zones`의 알려진 하위 필드는 생략·`null`을 허용한다. 지역·지구 원문을 공통 저장 계층에서 개별 지구 목록으로 분해하지 않는다. 빈 주소 등 문자열의 의미를 Info가 판정하지 않는다.

알려지지 않은 루트·data·원필지·지역지구 필드는 확장 정보다. 부분 수정에서 건드리지 않은 항목은 보존한다. `data.sources`도 특정 형식을 강제하지 않는 확장 항목이다. 과거 필드 별칭의 자동 변환은 하지 않는다. `AHA.Role` 같은 형상 처리용 User Text는 `aha.info`와 별개다.

## 입력과 결과

다음 서비스는 Rhino UI 스레드에서 호출한다.

| 기능 | 입력 | 결과 |
|---|---|---|
| `InfoService.Read` | Rhino 문서, 객체 GUID | Info JSON 또는 미기록 시 `null`. 객체는 바꾸지 않는다. |
| `InfoService.Update` | 문서, GUID, JSON merge patch, 선택적 `expectedJson` | 검증·저장한 전체 Info JSON |
| `InfoService.Delete` | 문서, GUID, 선택적 `expectedJson` | `aha.info`만 삭제. 형상과 다른 User Text는 유지한다. |

미기록 객체의 Update는 위 최소 정보에서 시작한다. patch에 필수 루트 필드를 모두 보낼 필요는 없지만 수정 결과에는 모두 존재해야 한다. 읽기는 루트 형식을 확인하되 알려진 업무 필드의 검증을 유예한다. 따라서 현재 버전의 잘못된 수치를 읽어 정정할 수 있다. 손상된 JSON이나 잘못된 필수 루트 형식을 정상 정보로 간주하지 않는다. 다른 양의 버전은 조회할 수 있으나 수정·삭제하지 않는다.

### MCP 연결

| 공개 기능 | 입력과 서비스 연결 | 도구 결과 |
|---|---|---|
| `aha_info_read` | `document_id`, `object_id` → `Read` | 대상 식별자, `info`, `revision` |
| `aha_info_update` | 위 식별자와 `patch`, `revision` → 원본 토큰 확인 후 `Update` | 대상 식별자, 저장한 `info`, 새 `revision` |
| `aha_info_delete` | 위 식별자와 `revision` → 원본 토큰 확인 후 `Delete` | 대상 식별자, `deleted: true`, 빈 `revision` |

표의 입력은 모두 필수다. `patch`는 저장 JSON의 루트에 적용하는 merge patch이며, 전송 계층이 필수 루트 필드를 별도로 요구하지는 않는다. 미기록 상태에서 시작하는 규칙도 패널·서비스와 같다. `document_id`와 `object_id`의 수명은 [공통 계약](../aha/contract.md), 토큰과 응답 포장은 [MCP 규약](../aha/mcp.md#정보-수정-토큰)을 따른다.

### 부분 수정과 보존

RFC 7396 merge patch를 사용한다. 객체는 재귀 병합하고 배열은 전체 교체하며, patch의 `null`은 속성을 제거한다. 저장된 선택 필드의 `null`이 유효하다는 규칙과 patch에서 `null`이 삭제를 뜻한다는 규칙은 구분한다.

다필지 수정자가 `parcels` 배열을 교체한다면 수정하지 않은 행과 그 확장 정보까지 보존해야 한다. Info 패널과 EumCrawl의 적용기는 이 보존을 수행한다. 저장은 수정 결과 전체를 검증하므로 잘못된 기존 값을 둔 채 다른 항목만 저장할 수는 없다.

`site: false`는 대지 지정을 해제하며 data는 보존한다. 형상이 별도로 편집되어 열리더라도 읽기는 저장된 site 값을 유지하므로 기존 정보를 조회하고 지정을 해제할 수 있다. `site: true` 쓰기는 현재 형상을 다시 검증한다. 정보 삭제는 대지 해제와 다르다. 무변경 저장은 불필요한 Undo를 만들지 않는다. 변경은 Rhino Undo/Redo를 지원하며 복사·Import·3dm 저장은 객체 속성으로 정보를 운반한다. 복사본과 원본은 독립적이며 자동 동기화하지 않는다.

### 대상과 충돌

문서·객체의 존재, 읽기 전용 문서, 잠긴 객체·상위 레이어, 참조 객체·레이어, 블록 정의 내부 객체를 확인한다. 쓰기 불가능한 대상에는 변경하지 않는다. 문서별 변경 잠금은 [공통 계약](../aha/contract.md)을 따른다.

서비스의 `expectedJson`은 편집 시작 시점의 원본 문자열이다. `null`이면 비교를 생략하며 미기록 상태와 비교할 때는 빈 문자열을 사용한다. 원본이 달라졌으면 쓰기를 거절한다. MCP의 `revision`은 별도 토큰이며 이 문자열 인자와 같지 않다. [정보 수정 토큰](../aha/mcp.md#정보-수정-토큰)의 규칙으로 변환해 호출한다.

패널은 편집 중 대상을 고정하고 저장 직전에 현재 필지 입력을 확정한다. 선택 변경이나 외부 수정이 다른 객체의 덮어쓰기로 이어지지 않아야 한다. 기본 수치·필지 면적·추가 숫자는 입력한 JSON 숫자 토큰으로 검증하며 먼저 부동소수점으로 반올림하지 않는다. 편집하지 않은 기본 수치와 명시적 `null`도 보존한다.

## 의존 계약과 검사

Info는 외부 API·VWorld KEY·Topo·EumCrawl에 의존하지 않는다. Topo는 [Topo 계약](../topo/contract.md)에 따라 확보한 정보를 이 형식으로 기록한다. EumCrawl은 [조회·선택 적용 계약](../eum-crawl/contract.md)에 따라 InfoService를 호출한다. 정보 생산자의 취득·추정 정책을 Info에 복제하지 않는다.

형식·merge patch는 [InfoData.cs](../../src/info/shared/InfoData.cs), Rhino 쓰기는 [InfoService.cs](../../src/info/rhino/InfoService.cs), 편집 상태는 [InfoPanel.cs](../../src/info/rhino/InfoPanel.cs)가 담당한다. [검사 안내](../../src/info/checks/README.md)는 형식·다필지·충돌·수치 입력·Undo·복사·저장 보존에 대응하는 검사를 연결한다.
