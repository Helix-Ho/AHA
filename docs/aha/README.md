# AHA 설치와 공통 사용

AHA는 Topo·Info·EumCrawl·Setting을 하나로 설치하는 Rhino 플러그인이다. 현재 버전은 **[0.0.1](../../CHANGELOG.md#001)**이며 검증 환경은 **macOS Rhino 8.35**다.

## 설치와 시작

설치 파일은 `aha-0.0.1-rh8_35-mac.yak` 하나다. 사용자에게 .NET SDK나 Python 설치를 요구하지 않는다. 받은 Yak 파일을 Rhino에 끌어 놓거나 `-PackageManager`의 `Install`에서 파일을 지정한다. 로드된 AHA를 교체했다면 Rhino를 재시작한다.

`AHA` 명령에서 도구를 연다. 직접 명령은 `AHATopo`, `AHAInfo`, `AHAEumCrawl`, `AHASetting`이다. 패키지의 AHA 도구모음에는 각 도구의 아이콘이 있으며 Rhino의 `Window → Toolbars`에서 표시할 수 있다. 패널 위치·크기·도킹 상태는 Rhino Container가 관리한다.

Topo를 사용하려면 [Setting](../setting/README.md)에서 본인의 VWorld KEY를 저장한다. Info와 PNU 기반 EumCrawl에는 KEY나 AI 연결이 필요하지 않다. 도구의 정보는 객체의 `aha.info`에 저장되어 3dm과 함께 보존된다. 별도의 사이트 설명 파일이나 연결 원장은 만들지 않는다.

## MCP 사용

MCP는 기본 OFF다. 필요한 경우 Setting에서 켜고 표시된 주소를 로컬 HTTP MCP 클라이언트에 등록한다. [연결 방법](../setting/README.md#mcp-연결)을 따른다. AHA 클라우드 서버나 특정 AI 서비스 계정은 요구하지 않는다. 사용하는 클라이언트의 AI 연결은 해당 클라이언트에서 관리한다.

클라이언트는 열려 있는 문서와 선택 객체를 조회하고, Info 읽기·편집, Topo 검색·XY 평면 생성·전체 모델 생성, EumCrawl 원문 읽기·수치 후보 조회·적용을 호출할 수 있다. Topo는 연산 후 준비 완료 상태에서 명시적으로 생성한다. 패널과 MCP 어디서 시작했든 같은 작업을 생성하거나 폐기할 수 있고, 패널을 닫거나 MCP를 꺼도 준비 결과는 유지된다. 긴 작업은 진행·결과를 조회하거나 취소한다. 패널과 MCP는 같은 업무 기능과 검증을 재사용하며 도구를 독립적으로 선택할 수 있다. 초기 계획에서는 PNU로 EumCrawl의 규제 후보를 확인하고 반환된 토지이용계획 링크에서 원문을 열람할 수 있다.

## 현재 한계

Topo는 초기 대지 검토용 맥락 모델이다. 원천 데이터 정확도·완전성과 지형 공급 경로의 제약은 [Topo 데이터와 한계](../topo/data.md)를 따른다. EumCrawl의 수치 후보는 해당 원문 백분율의 최솟값이며 확정된 법규 수치가 아니다. 별도 원문 읽기 기능은 수치를 선택하지 않는다. Windows 및 특정 AI 제품의 자연어 실행 호환성을 검증 완료로 제공하지 않는다.

[도구 목록과 연결](../index.md)
