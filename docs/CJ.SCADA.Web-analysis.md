# CJ.SCADA.Web 분석 보고서

> 대상: `CJ.SCADA.Web.zip` (`DxfBlazorViewer.sln`)
> 화면 타이틀: **CJ Logistics SCADA / CJ SCADA · FTMS – Megahub Gonjiam Viewer**
> 분석 방식: 정적 코드 리뷰 (빌드·실행은 하지 않음)
> 관련 문서: [CJ.SCADA.Solution-analysis.md](./CJ.SCADA.Solution-analysis.md) (백엔드 포함 전체 솔루션)

---

## 1. 한눈에 보기

| 항목 | 내용 |
|---|---|
| 목적 | 물류센터 상·하차 **도크(Dock) 모니터링 웹 클라이언트**. DXF 도면을 받아 도크/레인을 그리고, 처리량·알람·설정·통계를 보여줌 |
| 형태 | **Blazor WebAssembly (ASP.NET Core Hosted)** – 브라우저에서 C#이 실행되는 SPA |
| 런타임 | .NET 8 (`net8.0`), Visual Studio 17.14 |
| 렌더링 | **SkiaSharp 3.119 (WASM)** 로 Canvas에 도면 직접 렌더링, 차트는 **ECharts**(JS interop) |
| 실시간 | **SignalR**(`/chathub`) – 하트비트·서버 상태·수집 건수 / 나머지 데이터는 **HTTP 폴링(1~2초)** |
| 백엔드 | **이 솔루션에 없음.** 모든 `/api/*`, `/chathub` 는 같은 오리진으로 호출 → 앞단 **Nginx**가 별도 API 서버(app-server, 주석상 `192.168.0.41:8121`)로 라우팅해야 함 |
| 규모 | 프로젝트 4개, 실코드 약 2만 줄 (복사본/구버전 파일 제외 시 약 1.5만 줄) |

---

## 2. 전체 S/W 구조

```
 [운영자 브라우저]
      │ https
      ▼
 ┌─────────────────────────── Nginx (같은 서버) ───────────────────────────┐
 │  /           → 127.0.0.1:5084  (DxfBlazorViewer.Server : 정적 호스팅)    │
 │  /api/*      → API 서버 (app-server, 예: :8121)                         │
 │  /chathub    → API 서버 SignalR (WebSocket Upgrade 필요)                │
 └─────────────────────────────────────────────────────────────────────────┘
      │
      ▼  (브라우저에 다운로드되어 실행)
 ┌─────────────────────── DxfBlazorViewer.Client (WASM) ───────────────────┐
 │ App.razor  ── ChatClientBackgroundService(SignalR) ─ 하트비트/서버상태   │
 │ MainLayout ── LoginManager(외부 패키지) ─ 쿠키 Refresh 로그인/역할 메뉴   │
 │ Pages ─┬ Monitoring ─ <Viewer> ─ DxfServerAdapter ─ /api/dxf (msgpack)  │
 │        │                      └ DxfSkiaRendererFast (SkiaSharp Canvas)  │
 │        ├ Stats(ECharts) ─ <Viewer DocksOnly>  ├ Ppc(KPI 표)             │
 │        ├ Alarm / RawData / Settings           ├ AdminHealth / Users     │
 │ Blazored.Toast (자체 라이브러리, JS 토스트)                              │
 └─────────────────────────────────────────────────────────────────────────┘
```

### 프로젝트 의존 관계

```
DxfBlazorViewer.Server ──► DxfBlazorViewer.Client ──► Blazored.Toast
          │                         │
          └──────────► DxfBlazorViewer.Shared ◄──┘   (내용 비어있음)
```

---

## 3. 프로젝트별 기능

### 3.1 DxfBlazorViewer.Server (`Microsoft.NET.Sdk.Web`)
- **역할: WASM 산출물 정적 호스팅 전용.** `Program.cs` 59줄이 전부이며 컨트롤러가 하나도 없음.
- `UseUrls("http://127.0.0.1:5084")` 하드코딩 → 루프백에서만 리슨, Nginx 뒤에 두는 전제.
- `UseForwardedHeaders`(X-Forwarded-For/Proto), `ResponseCompression`(wasm/js/css), `UseBlazorFrameworkFiles`, `MapFallbackToFile("index.html")`.
- 게시 프로필: **win-x64, Self-contained, SingleFile** → Windows 서버 배포 전제.
- `.config/dotnet-tools.json` 에 `dotnet-ef 9.0.10` 이 있으나 DB 코드 없음(잔재).

### 3.2 DxfBlazorViewer.Client (`Microsoft.NET.Sdk.BlazorWebAssembly`) – 핵심
| 라우트 | 파일 | 기능 |
|---|---|---|
| `/` | `Index.razor` | 로그인 / 회원가입(`/api/auth/register`) / 비밀번호 변경 |
| `/monitor` | `Monitoring.razor` | 메인 화면. 좌측 도크 목록·현황(활성/비활성, 최근 5분 소·중/대형/이형 비율), 중앙 `Viewer`, 우측 도크 상세(개요·설정 파라미터) 패널. 1초 자동 갱신 |
| `/viewer` (컴포넌트) | `Viewer.razor(.cs)` 3,900줄 | DXF 씬 로드 → Skia 렌더, 줌/팬/Fit, 도크 하이라이트, **판정 데이터로 레인 색 채움**, **Dock diagram(도식) 모드**(PLC/리드시스템/엔코더/컨베이어 상태 타일), 최근 알람 5건 패널, 라벨 편집 모달 |
| `/stats` | `Stats.razor(.cs)` | 실적 대시보드. 일별/도크별 누적(`/api/ProcessCount/accumulated/*`) → ECharts 추이·파이·주간비교·히트맵 |
| `/dashboard` | `Ppc.razor` | "통계" – 엑셀 PPC 시트의 설비 성능 KPI 표(합계 + 도크별 열), 엑셀/CSV 내보내기 |
| `/settings` | `Settings.razor(.cs)` | 도크 분류계획: 파라미터 세트(Volume/Saturation 기준) 작성·저장(`/api/setting`), 도크(B/C/D/E × 01~18) 일괄 적용 |
| `/alarms` | `Alarm.razor(.cs)` | EventLog 검색(`/api/EventLog/search/fast`), 단건/일괄 확인(acknowledge), CSV 내보내기 |
| `/raw-data` | `RawData.razor(.cs)` | 원시 데이터 조회: Offset 페이징 / Cursor 무한스크롤 |
| `/admin/health` | `AdminHealth.razor` | 서버 Health 스냅샷, 접속 클라이언트 목록, **원격 로그레벨 변경**(`/api/RuntimeConfigChange/...`) |
| `/users/role-management` | `UserRoleManagement.razor` | 사용자 검색, 역할 조회/부여(`/api/users/...`) |

주요 공용 클래스(`Client/Shared`)
- **`DxfServerAdapter`** – `/api/dxf` 호출. `Accept: application/x-msgpack, application/json` 협상, MessagePack(LZ4) 우선 + JSON 폴백 + 첫 바이트 스니핑, ETag/304 캐시. DTO(`DxfSceneDto`, `BlockDto`, `InsertDto`, `PathDto`, `TextDto`)는 서버의 DXF 전처리기와 **Key 순서가 계약**.
- **`DxfSkiaRendererFast`** (3,100줄) – 씬 DTO를 IxMilia.Dxf 객체(`DxfInsert`)로 재구성 후 캐시, 블록 화이트리스트(`LANE_HEAD/LARGE/IRREGULAR/MEDIUM`), **기하학적 클러스터링으로 도크 번호 부여**(`AssignDockIndices`), 가로 레인↔도크 연결 추출, 라벨 충돌 회피 배치, 색상 계열(Red/Blue/Green)별 비율→채도 정책(`JudgePolicies`).
- **`DockLabelUtil`** – `DOCKn` ↔ `B01` 라벨 변환. 기본은 하드코딩 산식(1..72 → B..E × 18), 서버/로컬의 `dock.id.map` 으로 오버라이드(정적 전역 상태).
- `ProcessCount*`, `ApiProcessCountDto`, `DockStats` – 처리량 DTO·집계.
- `LabelsModal.razor` – 레인/도크 라벨 편집, `/api/setting` 의 `dock.label` 등 카테고리로 저장.
- `ChatClientBackgroundService` – SignalR 연결(5초 재시도), `Heartbeat / ServerStatusReceived / CollectedCountReceived` 이벤트 → `App.razor` 상단 바(하트비트 램프, 서버 시각·버전, 최근 N분 Loaded/Deduped/Inserted/Updated/Error).
- `wwwroot/js` – `wheel-blocker`, `dpr-watcher`, `resize-observer`(Viewer), `echartsInterop`(Stats), `ppcSticky/ppcExport`(Ppc), `alarm-export`, `chatHubInterop`(unload 시 허브 해제).

외부(사내) NuGet 패키지 – **소스 없음, 사내 피드 필요**
- `Assembly.LoginManager` (`LoginManager`, `IAuthService`, `RealAuthService`, `AuthServiceOptions`)
- `Assembly.JwtTokenGenerator`, `Assembly.ChatHub.Shared` (SignalR Client 를 전이 참조로 가져오는 것으로 보임)

### 3.3 Blazored.Toast (Razor Class Library)
- 이름은 공개 패키지 `Blazored.Toast` 와 같지만 **자체 제작 라이브러리**(Authors: hkhk01). `ToastService` → JS `window.showToast(message, type)`.
- FontAwesome 7 웹폰트·CSS를 `_content/Blazored.Toast/...` 로 제공. `ExampleJsInterop`, `Component1` 은 템플릿 잔재.

### 3.4 DxfBlazorViewer.Shared
- 템플릿 그대로 비어 있음(주석 한 줄). 실제 공용 DTO는 모두 Client 안에 있음.

---

## 4. 특징 (설계상 눈여겨볼 점)

1. **서버는 껍데기, 로직은 전부 브라우저(WASM)** – DXF 기하 처리·도크 인덱싱·라벨 배치까지 클라이언트 CPU에서 수행. 서버 부하는 적지만 저사양 PC/태블릿에서는 초기 로딩과 렌더 비용이 큼.
2. **`WasmBuildNative=true`** – SkiaSharp WASM 네이티브 링크 때문에 필요. 빌드 PC에 `wasm-tools` 워크로드(Emscripten) 설치가 필수이며 Release 빌드가 수 분 단위로 느림.
3. **전송 최적화** – DXF 씬을 서버에서 전처리해 MessagePack+LZ4로 받고, ETag 캐시를 사용.
4. **도크 식별 2단계** – 렌더러가 도면 좌표로 `DOCK1..N` 을 매기고, `dock.id.map` 설정으로 실제 라벨(B01…)에 매핑. 설정 저장소는 `/api/setting` (type/category/machineId/groupId/key 구조) + `localStorage` 이중화.
5. **실시간은 하이브리드** – 상태 표시줄만 SignalR, 화면 데이터는 1~2초 HTTP 폴링.
6. **권한은 UI 레벨 메뉴 제어** – `LoginManager.UserRoles` 문자열(Admin/Manager/Operator/Engineer) 기준으로 메뉴 표시.
7. 화면 자체 디자인 시스템(`scada.css` 2,200줄, CJ OnlyOne 폰트), 다크 테마.

---

## 5. 주의할 점 / 리스크

### 5.1 빌드·배포 (가장 먼저 부딪히는 문제)
| # | 내용 | 위치 |
|---|---|---|
| B1 | **사내 NuGet 3종이 없으면 빌드 불가** (`Assembly.LoginManager/JwtTokenGenerator/ChatHub.Shared`). `nuget.config` 도 zip에 없음 → 사내 피드 주소 문서화 필요 | `Client.csproj` |
| B2 | **한글 파일명 `복사본`** 이 csproj의 `Compile Remove`/`Content Remove` 로 제외되는 구조. zip 해제 시 인코딩이 깨지면(이번 zip도 `#Ubcf5#Uc0ac#Ubcf8` 로 깨짐) 제외 규칙이 안 맞아 **`Viewer` 클래스 중복, `/viewer` 라우트 중복, `DxfSkiaRendererFast` 중복 → 컴파일 에러**. 복사본/`_v1~_v3`/`test.cs` 는 저장소에서 삭제 권장 | `Pages/Viewer - 복사본.razor`, `Shared/DxfSkiaRendererFast - 복사본.cs` 등 |
| B3 | `wasm-tools` 워크로드 필수(`WasmBuildNative`) | `Client.csproj` |
| B4 | **Nginx 라우팅 의존**: `/api/*`, `/chathub` 가 API 서버로 가지 않고 이 Server로 오면 `MapFallbackToFile` 이 **index.html을 200으로 반환** → 클라이언트에선 "JSON 파싱 오류"로만 보임. `/chathub` 는 WebSocket Upgrade 헤더 설정 필요 | `Server/Program.cs` |
| B5 | `UseUrls("http://127.0.0.1:5084")` 하드코딩 → launchSettings의 `0.0.0.0`, `https://localhost:7225` 무시. Nginx가 다른 서버면 접속 불가이며, 그 경우 `ForwardedHeaders` 의 `KnownProxies` 도 설정해야 함 | `Server/Program.cs` |
| B6 | 패키지 버전 불일치(WebAssembly 8.0.22 vs DevServer/Server 8.0.21), `System.Text.Json 9.0.9`·`System.IO.Hashing` 을 net8에 추가, **`netDxf` 는 제외된 `test.cs` 에서만 사용(불필요한 용량)**. `DxfSkiaRenderer.cs`(구버전) 도 미사용 | `Client.csproj` |
| B7 | `index.html` 이 **존재하지 않는 `js/rawDataScroll.js`** 를 로드 → 404, RawData 무한스크롤 바인딩 실패("스크롤 감시 바인딩 실패" 표시) | `wwwroot/index.html`, `RawData.razor.cs:141` |

### 5.2 보안
| # | 내용 |
|---|---|
| S1 | **페이지 단위 권한 체크 없음.** `Settings`(Admin 전용 메뉴), `Alarm`, `RawData`, `Monitoring`, `Stats`, `Ppc` 는 메뉴만 숨길 뿐 URL 직접 입력 시 그대로 열림. `AdminHealth`, `UserRoleManagement` 만 화면 내 Admin 체크. WASM은 클라이언트 코드가 노출되므로 **최종 권한 검증은 반드시 API 서버에서** 해야 함 |
| S2 | Bearer 토큰은 `AdminHealth`/`UserRoleManagement` 에서만 첨부. 나머지(설정 저장 `POST /api/setting/json`, 알람 확인 `PUT /api/EventLog/.../acknowledge` 등)는 쿠키(`credentials: include`)에만 의존 → 서버가 쿠키 인증을 강제하지 않으면 **비인증 쓰기 가능**. 쿠키 기반이면 CSRF 대책(SameSite 등) 확인 필요 |
| S3 | SignalR 허브 연결에 인증 토큰 없음(`WithUrl(url)`만) |
| S4 | 토스트가 `innerHTML` 로 메시지를 삽입 → `ex.Message` 등 서버 유래 문자열이 들어가면 XSS 여지. `textContent` 사용 권장 (`Blazored.Toast/wwwroot/toast.js`) |
| S5 | `Ppc.razor` 의 `MarkupString` 은 숫자 서식만 넣고 있어 현재는 안전하나, 문자열 값을 넣도록 바뀌면 위험 |

### 5.3 기능·안정성
| # | 내용 | 위치 |
|---|---|---|
| F1 | `DxfServerAdapter.FetchLocalAsync` 가 **`http://localhost:5146` 하드코딩**(현재는 제외된 `Viewer_v1` 에서만 사용) | `DxfServerAdapter.cs:249` |
| F2 | **폴링 부하**: `/monitor` 한 화면이 알람 1초 + 판정 2초 + (도식 모드) Dock status 1초 + 좌측 현황 1초 → 클라이언트 1대당 초당 3~4회 요청. 상황실 PC 수만큼 배수로 증가. SignalR 푸시 전환 또는 주기 완화 검토 | `Viewer.razor.cs:41,93,409`, `Monitoring.razor:543` |
| F3 | `Stats` 페이지가 도크 목록만 얻으려고 숨겨진 `<Viewer DocksOnly>` 로 **DXF 전체를 다시 다운로드·파싱** | `Stats.razor:17` |
| F4 | `ChatClientBackgroundService` – `WithAutomaticReconnect` 대신 수동 루프. `DisconnectOnUnload` 의 `StopAsync` 가 `Closed` 이벤트 → 재연결 시도를 유발, 이미 `DisposeAsync` 된 연결로 `StartAsync` 재시도 루프에 빠질 수 있음. 허브 미가용 시 무한 재시도(5초) | `ChatClientBackgroundService.cs` |
| F5 | `Viewer` 는 `IAsyncDisposable` 만 구현 → 별도 `Dispose()`(`_animCts` 취소)는 Blazor가 호출하지 않음 | `Viewer.razor.cs:2418` |
| F6 | WASM은 단일 스레드(`WasmEnableThreads=false`) → `Task.Run` 폴링 루프도 UI 스레드에서 실행. 렌더(대형 DXF)와 폴링이 겹치면 프레임 드랍 | `Viewer.razor.cs:468,501` |
| F7 | 예외 무시(`catch { }`) 45곳, `async void` 이벤트 핸들러 3곳 → 장애 원인 추적이 어려움. 최소한 콘솔 로그 권장 | 전반 |
| F8 | **도크 번호가 도면 좌표 클러스터링 결과**에 의존 → DXF 수정(블록 이동/추가) 시 `DOCKn` 번호가 바뀌고, 저장된 `dock.id.map`·라벨 설정과 어긋날 수 있음. 도면 변경 시 매핑 재검증 필요 | `DxfSkiaRendererFast.AssignDockIndices` |
| F9 | 도크 구성 하드코딩: `Settings` 는 B/C/D/E × 01~18(72개) 고정, `DockLabelUtil` 기본 산식도 72개 고정. 센터/라인 증설 시 코드 수정 필요 | `Settings.razor.cs BuildDefaultGroups`, `DockLabelUtil.FromIndex_Legacy` |
| F10 | `DockLabelUtil` 의 매핑이 **static 전역 상태** → 컴포넌트 간 공유(의도)이지만 씬 전환 시 초기화 순서에 민감(`LoadDockIdMapAsync` 가 먼저 Clear) | `DockLabelUtil.cs` |
| F11 | 설정·라벨·열 수 등 다수가 `localStorage` 에도 저장(20곳) → PC마다 표시가 달라질 수 있음. 서버 값이 우선인지 정책 정리 필요 | Viewer/Settings/LabelsModal |
| F12 | `Ppc.razor` 주석에 "데모 랜덤 데이터" 안내가 남아 있음 – 실제 API(`/api/ProcessCount/statics`) 연동 여부 확인 필요 | `Ppc.razor` 상단 |
| F13 | 시간대: API는 UTC, 표시는 브라우저 로컬 시간 변환. 클라이언트 PC 시간대/시계가 틀리면 "최근 30초 이내 = 활성" 판정(`IsActiveDock`)이 틀어짐 | `Monitoring.razor:391` |

### 5.4 유지보수
- `Viewer.razor.cs` 3,700줄 + 렌더러 3,100줄에 렌더링·폴링·알람·라벨 저장·도식 렌더러(내부 클래스)가 섞여 있음 → 최소 `AlarmPanel`, `DockStatusDiagramRenderer`, 라벨 저장소 서비스 정도로 분리 권장.
- API 경로 문자열이 페이지마다 상수로 흩어져 있고, 주석 처리된 `http://192.168.0.41:8121` 절대주소가 다수 남아 있음 → `appsettings`(wwwroot) 또는 단일 `ApiRoutes` 클래스로 일원화 권장.
- HttpClient 이름이 `"HttpsApi"` 와 `"DxfBlazorViewer.ServerAPI"` 두 개인데 BaseAddress·핸들러가 동일 → 하나로 통합 가능.
- `DxfBlazorViewer.Shared` 를 실제로 사용하지 않음 – 서버 DXF 전처리기와 공유해야 할 `DxfSceneDto`(MessagePack Key 계약)를 여기로 옮기는 것이 바람직.
- `.csproj.user`, `.pubxml.user`, `slnLaunch.user`, `refer/fontawesome-free-7.0.1-web.zip` 등은 저장소에서 제외(.gitignore) 권장.

---

## 6. 빌드/실행 체크리스트

1. .NET 8 SDK + `dotnet workload install wasm-tools`
2. 사내 NuGet 피드 등록(Assembly.* 3종)
3. 복사본/구버전 파일 삭제(또는 파일명 인코딩 확인)
4. 로컬 실행: Server 프로젝트 시작 → `http://127.0.0.1:5084` (API 서버가 없으면 로그인·데이터 모두 실패)
5. 운영: `dotnet publish` (win-x64 self-contained) → Windows 서비스/작업 등록 → Nginx 설정
   - `/` → `127.0.0.1:5084`
   - `/api/` → API 서버
   - `/chathub` → API 서버 (`proxy_http_version 1.1; Upgrade/Connection` 헤더)
   - `.wasm` MIME, gzip/brotli, `_framework/` 캐시 정책(배포 시 캐시 무효화) 확인
