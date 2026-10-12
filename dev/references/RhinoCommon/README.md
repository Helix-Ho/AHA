# RhinoCommon

[공식 API](https://developer.rhino3d.com/api/rhinocommon/) · [개발자 안내](https://developer.rhino3d.com/guides/rhinocommon/)

현재 AHA의 확인 환경인 Rhino 8.35를 기준으로 사용했다. 별도 원본 clone은 없으며 실제 빌드는 설치된 Rhino SDK를 참조한다.

2026-10-11에 확인한 [McNeel SDK/API 라이선스](https://developer.rhino3d.com/license/)는 MIT다. Rhino 제품의 이용허락과는 별개이며, AHA 패키지는 RhinoCommon·Rhino.UI·Eto DLL을 동봉하지 않고 설치된 Rhino에서 참조한다. 이는 [CopyLocal=false 안내](https://developer.rhino3d.com/en/guides/rhinocommon/using-nuget/)에 따른다.

[Info](../../info/README.md)의 ObjectAttributes User Text·Undo·저장, [AHA 공통](../../aha/README.md)의 등록 패널·문서·UI 스레드 접근, [Topo](../../topo/README.md)의 Curve·Mesh·Brep 생성과 유효성 검사에 참고했다. Rhino의 저장·편집 기능 위에 AHA의 의미를 더하고 별도의 모델 원장을 만들지 않는 선택의 기반이다.

## 경계 곡선과 매개변수

2026-10-07에 설치된 Rhino 8 SDK의 XML, [SetEdgeCurve API](https://developer.rhino3d.com/api/rhinocommon/rhino.geometry.brepedge/setedgecurve), openNURBS `8.x`의 [Brep 구현](https://github.com/mcneel/opennurbs/blob/8.x/opennurbs_brep.cpp)·[CurveProxy 구현](https://github.com/mcneel/opennurbs/blob/8.x/opennurbs_curveproxy.cpp)을 확인했다. Topo의 [지형 경계 생성](../../../src/topo/shared/Rhino/TerrainSurfaceGeometry.cs)에서 정확한 UV 경계를 3D 곡선으로 올리고 공유 끝점을 유지하는 데 참고한다.

`SetEdgeCurve`는 새 곡선의 도메인과 정방향으로 proxy를 설정한다. 기존 `edge.DuplicateCurve()`에는 부분 범위·방향이 반영되지만 `edge.EdgeCurve`는 저장 원곡선이므로 둘을 혼동하지 않는다. `trim.IsReversed()`는 edge에 대한 trim 방향이다. SDK의 `Surface.GetSpanVector`는 쌍선형 셀 경계를 나누는 데 사용하며, 직선 UV 구간의 정확한 이차곡선 구성은 한 셀 안에서 적용한다. 원호나 다른 차수의 면까지 같은 구성으로 일반화하지 않는다.

공식 API의 존재만으로 운영체제별 패널 동작이나 수치 형상의 정확성을 보장하지 않는다. 사용 환경과 실제 형상을 Rhino 검사에서 확인한다. 다른 Rhino 버전이나 Windows 지원 완료를 뜻하지 않는다.

## 도구모음과 Container

2026-10-11에 [Rhino UI 구조](https://developer.rhino3d.com/guides/general/rhino-ui-system/)와 [Container 관리](https://docs.mcneel.com/rhino/8mac/help/en-us/toolbarsandmenus/windowlayout.htm)를 확인했다. Rhino 8은 RUI의 도구모음 그룹을 Container로 변환하고 사용자 배치를 별도로 저장한다. AHA는 고정 GUID의 도구모음·그룹 하나를 제공한다. macOS의 `OpenPanel(dockBarId, …)`와 `OpenPanelAsSibling`은 [API 설명](https://developer.rhino3d.com/api/rhinocommon/rhino.ui.panels/openpanelassibling)상 일반 패널 열기로 동작하므로, 특정 Container로 묶는 기능으로 가정하지 않는다.

## 자원 검토의 참고 환경

2026-10-11에 확인한 [Rhino 8 시스템 요구사항](https://www.rhino3d.com/en/8/system-requirements/)은 Windows와 Mac 모두 RAM 8GB 이상을 권장한다. Topo 자원 비용을 검토할 때 참고하며, AHA의 최소 사양이나 특정 입력의 안전성을 보장하는 기준으로 사용하지 않는다.

## Eto 범위 입력

Rhino 8.35에 포함된 Eto 2.11의 `Stepper.Step`·`StepperEventArgs.Direction`을 확인했다. [Stepper](https://github.com/picoe/Eto/blob/develop/src/Eto/Forms/Controls/Stepper.cs)의 방향 이벤트와 TextBox 편집을 분리해 [모델 범위 입력](../../../src/topo/api/rhino/ModelRangeInput.cs)에 사용한다. NumericStepper의 값 차이로 입력 원인을 추정하지 않는다.

## Eto 내장 WebView

2026-10-11에 [Eto WebView API](https://github.com/picoe/Eto/blob/develop/src/Eto/Forms/Controls/WebView.cs)와 [macOS WKWebView 구현](https://github.com/picoe/Eto/blob/develop/src/Eto.Mac/Forms/Controls/WKWebViewHandler.cs)을 확인했다. `BrowserContextMenuEnabled`는 내장 화면의 기본 탐색 메뉴를 끄는 옵션이고 `DocumentLoading`은 주 프레임 탐색을 취소할 수 있다. Rhino 8.35의 Eto 2.11에서 `LoadHtml` 후 Reload가 빈 페이지를 여는 동작을 재현했다. [Topo 지도](../../../src/topo/api/rhino/TopoPanel.cs)에만 이 탐색 경계를 적용하며, KEY 변경에 필요한 호스트 로드는 별도로 유지한다. 다른 운영체제의 실제 WebView 동작까지 확인한 것은 아니다.

## Headless 문서의 검사 경계

2026-10-10에 설치된 Rhino 8.35와 McNeel의 [headless 이벤트 설명](https://discourse.mcneel.com/t/rhino-rhinodoc-import-not-running-headless/158646/3)을 확인했다. `CreateHeadless` 문서의 폐기는 정적 `CloseDocument` 이벤트를 발생시키지 않는다. [Topo 서비스 검사](../../../src/topo/checks/service/TopoServiceChecks.cs)는 종료 처리기를 직접 호출하고 실제 창의 이벤트 연결은 [호스트 확인](../../../src/aha/checks/README.md#실사용외부-연동-확인)으로 구분한다. Undo 뒤 객체 표의 개수는 삭제 기록을 포함하므로 살아 있는 객체 GUID로 보존 여부를 판정하며, headless Undo가 남긴 활성 기록은 검사에서 닫는다.

## 입체 차집합

2026-10-08에 [CreateBooleanDifference API](https://developer.rhino3d.com/api/rhinocommon/rhino.geometry.brep/createbooleandifference)와 McNeel의 [Boolean FAQ](https://wiki.mcneel.com/rhino/booleanfaq)를 확인했다. Topo 굴착을 Rhino 입체 차집합으로 구현할 수 있는지 비교하는 자료다. API는 Brep 집합·허용오차를 받아 결과 배열 또는 실패를 반환하며, 입력 방향과 완전한 교차 경계가 중요하다. 접선·일치면 등에서는 유효한 입력도 연산 실패가 가능하다.

이 자료만으로 AHA의 실제 모델에서 성공이나 성능을 보장하지 않는다. Near 입체와 Far·겹침 도로의 면 표현을 구분하고, 채택 여부는 현재 실패 입력과 경계 교차·표시 Mesh까지 포함한 검증으로 판단한다. 현행 구현은 [Topo 설계](../../topo/architecture.md)에 있다.

## Extrusion과 내부 프로파일

2026-10-09에 설치된 Rhino 8 SDK와 [Extrusion 안내](https://docs.mcneel.com/rhino/8mac/help/en-us/seealso/sak_extrude.htm), [AddInnerProfile API](https://developer.rhino3d.com/api/RhinoCommon/html/M_Rhino_Geometry_Extrusion_AddInnerProfile.htm)를 확인했다. Topo의 Contour 판과 Near 건물을 외곽·구멍·높이 구간으로 직접 출력하는 데 사용한다. 서로 접촉하는 구멍은 유효한 단면들로 나누며, 지원 형식이라는 사실과 실제 폐합·체적·표시 검증을 구분한다. [Contour 출력](../../../src/topo/shared/Rhino/ContourGeometry.cs), [형상 검사](../../../src/topo/checks/native/ContourChecks.cs).

## 준비 형상과 표시망

2026-10-10에 Rhino 8.35에서 독립 형상의 캐시·객체 추가·해제·저장까지 확인했다. `AddBrep`는 면의 표시망을 보존하지만 `AddExtrusion`은 미리 부착한 표시망을 버린다. `Extrusion.SetMesh` 뒤 `CommitChanges`로도 전달되지 않는다. 따라서 [Topo 준비](../../../src/topo/shared/Rhino/PreparedModel.cs)는 Brep 캐시만 미리 채우고 Extrusion의 첫 표시망은 Rhino에 맡긴다. [검사](../../../src/topo/checks/native/PreparedGeometryChecks.cs)는 실제 Extrusion 형식·구멍·해석 체적·저장과 첫 표시 비용을 구분한다.

같은 환경의 기본 Extrusion mesher는 수 µm 간격의 프로파일 점을 float 좌표로 합쳐 면적 0인 표시 면을 만들 수 있었다. 재현 판에서 기본·빠른 설정은 200개 중 5개, 품질 설정은 218개 중 5개였으며 실제 닫힌 Extrusion은 유효했다. `MeshingParameters.DoublePrecision=true`에서는 같은 200개 면이 유효했다. 이것은 원형의 기하 정밀도와 별개인 native 표시망 특성이며, AHA가 사용자 표시 설정을 고정하거나 객체 형식을 바꾸는 근거로 사용하지 않는다.

소유권은 공개 [Extrusion native wrapper](https://github.com/mcneel/rhino3dm/blob/8.x/src/librhino3dm_native/on_beam.cpp)와 [Brep managed wrapper](https://github.com/mcneel/rhino3dm/blob/8.x/src/dotnet/opennurbs/opennurbs_brep.cs)를 함께 확인했다. Extrusion의 `SetMesh`는 복사하고 Brep 면은 소유권을 옮기므로, 임시 managed Mesh를 해제하는 것과 문서 등록 시 캐시가 사라지는 현상을 구분한다.

## 정확 프로파일의 NURBS 표현

2026-10-11에 설치된 Rhino 8.35 SDK의 `NurbsCurvePointList.SetPoint`·`VolumeMassProperties.Compute` 설명과 실제 형상을 확인했다. 가중 제어점의 4개 좌표 인수는 동차좌표이며, 면적 검사의 적분 정확도는 형상 허용오차와 구분한다. [프로파일 출력](../../../src/topo/shared/Rhino/ExactProfileGeometry.cs)은 짧은 직선·원호 구간을 같은 경계의 NURBS로 보존하며, [회귀 검사](../../../src/topo/checks/native/ContourChecks.cs)는 단위·방향·시작점 변환 후 실제 폐합과 체적을 확인한다.
