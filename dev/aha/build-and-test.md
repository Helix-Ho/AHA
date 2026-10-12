# AHA 빌드와 검사

제품과 검사 빌드는 같은 소스를 사용한다. 배포 패키지는 실행에 필요한 파일만 포함하며, 개발 문서·검사 코드·실행 산출물의 역할을 구분한다.

## 환경과 빌드

현재 검증 환경은 macOS, Rhino 8.35, .NET 8이다. Node.js 18 이상은 지도 검사에 사용한다. Python은 프로젝트 가상환경을 사용하며 검사 실행기는 표준 라이브러리만 필요하다. 첫 .NET 패키지 복원에는 NuGet 접속이 필요할 수 있다. 핵심 검사 실행 중 외부 서비스나 AI는 필요하지 않다.

저장소 루트에서 실행한다. 기존 `.venv`가 있으면 생성 단계를 생략한다.

```sh
python3 -m venv .venv
.venv/bin/python check.py --build-rhino
bash src/aha/package/build.sh
```

.NET은 `AHA_DOTNET`, `.tools/dotnet/dotnet`, 시스템 `dotnet` 순으로 찾는다. 패키지 스크립트의 Yak 기본 경로는 Rhino 8 앱 내부이며 `AHA_YAK`으로 지정할 수 있다. Rhino SDK 참조가 다른 경우 .NET 빌드의 `RhinoSystemDir` 속성을 지정한다. 검사 실행기의 캐시는 `.tools/`에 둔다. `.venv/`, `.tools/`, `bin/`, `obj/`, `outputs/`는 소스가 아니다.

제품 프로젝트는 [`Aha.csproj`](../../src/aha/rhino/Aha.csproj) 하나다. Release 빌드의 출력은 `src/aha/rhino/bin/Release/net8.0/`, Yak는 `outputs/packages/`에 생성된다. 패키지에는 검사 DLL·고정 입력·소스·개발 문서·비밀정보를 넣지 않는다. `package/build.sh`가 임시 디렉터리에 제품 파일과 버전 적용 manifest를 모아 Yak를 만들고 임시 디렉터리를 정리한다.

AHA의 [MIT License](../../LICENSE)와 외부 라이브러리 고지는 빌드 출력과 Yak에 함께 포함한다.

포함 자원·툴바 명령을 바꾸면 [패키지 생성](../../src/aha/package/build.sh), [패키지 검사 정책](../../src/aha/checks/package/check_package.py), [회귀 입력](../../src/aha/checks/package/test_package.py)을 함께 확인한다.

버전 원본은 [`Directory.Build.props`](../../Directory.Build.props)다. 초기 개발 버전은 `0.0.N`, 첫 공개 배포는 `0.1.0`으로 관리하며 검사 실행만으로 올리지 않는다. 현재는 **0.0.1**, Info 형식은 별도로 **1**이다. MCP 프로토콜 버전도 제품 버전과 별개다.

버전별 사용자용 설명은 [`CHANGELOG.md`](../../CHANGELOG.md)에 주요 기능·변경, 실제 검증 환경과 한계를 짧게 기록한다. 버전을 정할 때 갱신하며 커밋 목록이나 개발 History를 옮겨 적지 않는다. 공개일은 실제 공개할 때 추가하고, GitHub Release 설명은 해당 항목을 사용한다.

## 설치본 확인

개발 중에도 생성한 Yak를 개발 폴더 밖의 Rhino 패키지 위치에 설치해 확인한다. 설치 조작은 [사용자 설치 안내](../../docs/aha/README.md#설치와-시작)를 따른다. 제품 또는 검사 DLL을 다시 빌드했다면 Rhino를 재시작한다.

```sh
.venv/bin/python src/aha/checks/package/check_package.py outputs/packages/aha-0.0.1-rh8_35-mac.yak
```

패키지 검사는 필수 자원·제외 파일·버전을 확인하고 archive 안의 제품 파일을 현재 빌드와 대조한다. 설치 후에는 실제 설치 경로·설치 파일의 해시를 빌드 및 archive와 별도로 대조한다. Rhino 실행기는 로드된 제품과 검사 DLL의 MVID를 현재 Release 빌드와 확인한다. 같은 버전 번호만으로 동일한 빌드라고 판단하지 않는다.

## 회귀 검사 실행

```sh
# 전체 일반 검사
.venv/bin/python check.py
# 도구별 일반 검사: aha, setting, info, eum-crawl, topo
.venv/bin/python check.py --tool topo
# 로컬에 보존한 원본 취득 자료까지 포함
.venv/bin/python check.py --tool topo --include-local-fixtures
# 같은 등록 목록을 사용하는 도구별 진입점
.venv/bin/python src/topo/checks/run.py
# 등록 목록
.venv/bin/python check.py --list --layer all
# 전체 Rhino 검사 DLL 빌드 (검사 실행은 아님)
.venv/bin/python check.py --build-rhino
```

기본 검사 입력은 저장소의 합성 자료다. 실제 취득 원본과 그 출처·해시는 Git에서 제외된 `.local/test-data/<도구>/`에 보관한다. `--include-local-fixtures`는 공개 검사에 원본 검사를 더하며, 자료가 없거나 해시가 다르면 실행 불가(2)다. Rhino 공통 진입점은 `main(tool="topo", include_local_fixtures=True)`처럼 같은 범위를 선택한다. 선택하지 않은 원본 검사는 통과로 집계하지 않는다. 원본 자료를 API에서 자동 재취득하지 않으며 검사 DLL·Yak에 복사하지 않는다.

Rhino에 현재 AHA 패키지를 설치·로드한 뒤 `RunPythonScript`로 [`src/aha/checks/run_rhino.py`](../../src/aha/checks/run_rhino.py)를 선택한다. 도구별 실행은 `src/<도구>/checks/run_rhino.py`를 선택한다. `aha`의 공통 검사만 실행하려면 Rhino Python에서 공통 진입점의 `main("aha")`를 호출한다. 파일 경로는 해당 checkout의 위치를 사용한다.

일반 결과는 `outputs/checks/offline.json`, Rhino 결과는 `outputs/checks/rhino/report.json`이다. 비동기 검사가 모두 끝난 뒤 **status가 passed**인지 확인한다. 시작 메시지나 DLL 빌드는 통과가 아니다. 성공 0, 실패 1, 실행 환경 미준비 2이며 실패가 환경 문제보다 우선한다. Rhino에서도 같은 집계 규칙과 `exit_code`를 보고한다. 터미널에서 Rhino 계층 실행을 요청하면 실제 호스트 실행 안내와 실행 불가를 반환한다.

공통·Info·Setting·EumCrawl·Topo 서비스 검사는 설치된 AHA를 참조한다. Topo의 세부 형상 검사는 제품 소스를 직접 포함해 재컴파일하며 계산 코드를 복제 구현하지 않는다. 일반 검사는 제품 소스와 필요한 호스트 경계 fixture를 사용한다.

검사는 임시 문서·객체·파일·로컬 포트와 고정 응답을 사용한다. 개인 KEY·설정을 쓰거나 외부 데이터를 취득하지 않는다. Rhino에서 사용자의 문서를 동시에 편집하면 보존 검사가 실패할 수 있다. Rhino 8의 headless Undo는 활성 문서의 수정 표시를 바꿀 수 있어 내용·선택·레이어 보존과 수정 표시를 구분한다. 성공한 임시 3dm은 삭제하고 실패 자료만 원인 조사에 남긴다. `outputs/`는 다음 검사의 입력으로 쓰지 않는다.

## 검사 유지 원칙

| 계층 | 판정 대상 |
|---|---|
| 일반 검사 | 계산·파싱·정보 계약·설정·지도 상태·로컬 MCP. 실제 외부 API·KEY·AI에 의존하지 않는다. |
| Rhino 검사 | 실제 기하·문서·Undo·저장·패널 상태·설치본 서비스. 사용자 클릭 시나리오를 요구하지 않는다. |
| 실사용·외부 연동 확인 | 고정 입력으로 보장하지 못하는 공급원·실행 환경·정성적 품질. 관련 변경 때 확인하며 핵심 검사와 구분한다. |

개발 변경 전에 소유자의 `checks/README.md`에서 **보호하는 계약·실패 조건과 기존 검사**를 확인한다. 필요한 입력·판정을 추가·수정하고, 중복되거나 폐기된 동작만 보호하는 검사는 병합·삭제한다. 보호가 필요한 조건을 잃지 않도록 대응표와 호출 연결도 갱신한다. 구현 문자열이나 내부 객체 동일성 대신 입력·출력·부작용을 판정한다.

각 도구의 `checks/suites.json`은 전체·도구별 실행기가 공유하는 실행 진입점 목록이다. 진입점 추가·이동·삭제 시 등록도 갱신한다. 오류 수정은 최소 재현 입력과 자동 판정으로 남기며, 자동화가 부적합한 항목은 아래 확인 절차로 관리한다.

마지막 관련 변경 이후의 코드로 영향받는 검사를 실행한다. 공통 상태·실행 흐름 변경이면 전체로 넓히며, 이후 변경이 없고 관련 검사가 통과했다면 불필요하게 반복하지 않는다. 문서만 바꾸면 링크·명령·구현 설명을 확인한다. 실행기의 빈 선택·등록 누락·중복·시간 초과·이전 보고서 거절은 [공통 검사](../../src/aha/checks/README.md)가 보호한다.

자동화하기 어려운 중요한 확인은 소유자의 `checks/README.md`에 **재확인할 변경·준비와 절차·기대 결과·자동화 한계**를 짧게 남긴다. 항목이 많아질 때만 같은 폴더에서 문서를 나눈다. 가능한 판정은 코드로 옮기고 중복 절차를 정리한다. 실행 결과는 안내에 누적하지 않으며, 미실행·실행 불가는 통과로 보고하지 않는다.
