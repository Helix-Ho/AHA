# Info 검사

[개발 안내](../../../dev/info/README.md) · [계약](../../../dev/info/contract.md) · [공통 실행 방법](../../../dev/aha/build-and-test.md#회귀-검사-실행)

```sh
.venv/bin/python src/info/checks/run.py
.venv/bin/python check.py --tool info --build-rhino
```

Rhino에서 [run_rhino.py](run_rhino.py)를 실행한다. 등록은 [suites.json](suites.json)에서 관리한다.

| 보호하는 계약·실패 조건 | 검사 |
|---|---|
| version 1·필수 필드·수치·null/0·확장 정보·merge patch·다필지 보존 | `Program.cs` |
| 폐곡선·닫힌 Brep/Extrusion 대지 지정·열린 형상/Mesh 제외·형상 변경 후 조회/해제·잠금/참조/삭제·무변경 저장·충돌 | `native/InfoNativeChecks.cs`, `native/InfoPanelChecks.cs` |
| 편집 중 선택 변경·저장 직전 필지 입력·외부 변경·수치 입력 정밀도와 미입력 보존·고급 JSON의 기존 필드 정정 | `native/InfoPanelChecks.cs` |
| Undo/Redo·대지 해제·삭제·독립 복사·Import·3dm 재열기 | `native/InfoNativeChecks.cs` |

일반 검사는 InfoData 원본을 컴파일한다. 호스트 검사는 설치된 AHA의 서비스·패널을 호출한다. 임시 headless 문서와 객체만 만들며 성공한 저장 파일은 지운다. 현재 형식의 확장 속성은 유지하되 과거 별칭의 변환을 요구하는 검사는 없다.
