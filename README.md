<picture>
  <source media="(prefers-color-scheme: dark)" srcset="src/aha/assets/logo/AHA_Logo_mono_white.svg">
  <img src="src/aha/assets/logo/AHA_Logo_mono.svg" alt="AHA" width="144">
</picture>

# AHA — AI × Architecture Helix

AHA는 Rhino에서 사용하는 건축 설계 도구 모음이다. 하나의 플러그인으로 설치하고 각 도구를 직접 사용하거나 로컬 MCP 클라이언트에서 호출할 수 있다. 현재 버전은 **[0.0.1](CHANGELOG.md#001)**이다.

## 시작하기

[설치·패널·MCP 사용 안내](docs/aha/README.md)를 읽고 `AHA` 명령으로 시작한다. 현재 검증 환경은 macOS Rhino 8.35다. Topo는 Setting에 본인의 VWorld KEY를 저장한 후 사용한다. Info와 PNU 기반 EumCrawl에는 KEY나 AI 연결이 필요하지 않다.

| 구성 | 역할 |
|---|---|
| [AHA](docs/aha/README.md) | 플러그인 설치·공통 패널·명령·MCP 사용 |
| [Topo](docs/topo/README.md) | VWorld 자료로 한국의 지형·필지·도로·건물과 선택적 근접 설계 영역 생성 |
| [Info](docs/info/README.md) | 객체에 저장된 정보·원필지 목록·대지 지정·수치 편집 |
| [EumCrawl](docs/eum-crawl/README.md) | PNU로 토지이음 조회·기존 정보와 비교·선택 반영 · 실험 기능 |
| [Setting](docs/setting/README.md) | VWorld KEY와 선택적 로컬 MCP 연결 관리 |

[도구 사용 안내](docs/index.md) · [버전 기록](CHANGELOG.md) · [소스 개발 안내](dev/README.md)

## 라이선스

AHA는 [MIT License](LICENSE)로 제공한다. 외부 라이브러리에는 [각 라이선스](src/topo/api/rhino/THIRD_PARTY_NOTICES.txt)가 적용되며, VWorld·토지이음 자료의 이용조건은 [Topo](docs/topo/data.md)·[EumCrawl](docs/eum-crawl/README.md) 안내에서 확인한다.
