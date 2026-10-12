# AHA 사용 안내

[설치와 공통 사용](aha/README.md)부터 시작한다. 모든 도구는 한 AHA 플러그인에 포함되며 `AHA` 패널이나 직접 명령으로 열 수 있다.

| 도구 | 입력 → 결과 | 명령 |
|---|---|---|
| [Topo](topo/README.md) | VWorld KEY·선택 필지·모델 설정 → 지형·도로·건물·객체 정보; MCP로 같은 범위의 XY 평면 정보만 생성도 가능 | `AHATopo` |
| [Info](info/README.md) | 선택 객체·입력값 → 객체의 `aha.info` | `AHAInfo` |
| [EumCrawl](eum-crawl/README.md) | PNU → 수치 후보·토지이음 링크, 선택 시 객체에 반영; MCP로 수치 선택 없는 원문 구조 읽기도 가능 | `AHAEumCrawl` |
| [Setting](setting/README.md) | KEY·MCP 사용 여부와 포트 → 사용자 설정·연결 정보 | `AHASetting` |

Topo가 기록한 정보를 Info에서 읽거나 수정할 수 있다. EumCrawl은 객체에 저장된 PNU 또는 직접 입력한 PNU로 조회한다. Info·EumCrawl은 Topo 실행 없이도 사용할 수 있고, 조회만으로 객체 정보가 바뀌지는 않는다. KEY는 Setting에서 관리하며 모델에 저장하지 않는다.
