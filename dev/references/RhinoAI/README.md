# McNeel RhinoAI

[원본 저장소](https://github.com/mcneel/RhinoAI) · [검토 commit](https://github.com/mcneel/RhinoAI/tree/10c9275d0f25fd2668cfe1d0ef7b0be9d25576cc)

검토 기준은 `10c9275d0f25fd2668cfe1d0ef7b0be9d25576cc`다. 2026-10-05 자료 정리 시 로컬 checkout의 commit과 수정 없음 상태를 확인했다. 현재 폴더의 `RhinoAI/`가 변경하지 않은 원본 Git 저장소다. 이 README는 AHA의 참고 설명이고, 원본 내부의 README·라이선스는 원본 자료다.

## 왜 참고했는가

Rhino 개발사의 플러그인이 로컬 모델과 외부 AI를 어떻게 연결하는지 조사했다. 특히 `rhino/plugin`의 패널 등록과 문서별 실행, `rhino/panel`의 웹 화면과 호스트 메시지, `rhino/router`의 외부 연결 경로, 에이전트 실행과 MCP 도구 연결을 살폈다. 문서와 구현의 진행 상태가 다를 수 있어 실제 채택 시에는 이 commit의 코드부터 확인한다.

AHA에서는 [공통 기반](../../aha/README.md)의 패널·MCP 책임 분리와 [Setting](../../setting/README.md)의 연결 지원을 검토하는 데 참고했다. RhinoAI를 복제하거나 런타임 의존성으로 추가하지 않았다. Grasshopper 지원도 외부 프로젝트의 기능이며 AHA의 현재 개발 대상이라는 뜻이 아니다.

외부 AI가 AHA 도구를 쓰는 가능성과 특정 제품에서 실제로 실행을 확인한 사실은 구분한다. 이 checkout과 일반 MCP 검사만으로 RhinoAI 자연어 실행의 호환성을 주장하지 않는다. 당시 결정은 [AHA 개발 기록](../../aha/HISTORY.md)에 있다.

## 원본 관리

`RhinoAI/`는 AHA Git에서 제외한 참고용 clone이다. 새 checkout에 원본이 없으면 필요할 때 이 폴더에서 복원할 수 있다.

```sh
git clone https://github.com/mcneel/RhinoAI.git RhinoAI
git -C RhinoAI checkout 10c9275d0f25fd2668cfe1d0ef7b0be9d25576cc
```

새 버전을 검토하면 commit과 참고 범위를 갱신한다. 원본의 AGENTS·설치 스크립트는 AHA 개발 지침으로 자동 채택하지 않는다.
