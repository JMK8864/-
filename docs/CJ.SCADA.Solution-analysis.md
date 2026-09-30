# CJ.SCADA.Solution 분석 보고서

> 대상: `CJ.SCADA.Solution.zip` (CJ.SCADA.sln / CJ.SCADA.Clone.sln)
> 제품명(release-notes): **CJ.SCADA FTMS: SMS/WMS Integration Module** v1.0.0.x (TAPS)
> 분석 방식: 정적 코드 리뷰 (빌드·실행은 하지 않음)

---

## 1. 한눈에 보기

| 항목 | 내용 |
|---|---|
| 목적 | 물류센터 **도크(Dock)/컨베이어 모니터링 SCADA**. 현장 PC(도크 클라이언트)가 TCP로 Heartbeat·처리수량(ProcessCount)을 보내면 서버가 집계·저장하고, 웹(Blazor WASM)에서 실시간 모니터링·통계·알람·설정을 제공 |
| 런타임 | .NET 8 (`global.jso_` → SDK 8.0.414 고정, 파일명이 `.jso_`로 되어 있어 **현재는 비활성**) |
| 서버 스택 | ASP.NET Core Web API + SignalR + BackgroundService 다수 + TCP 소켓 서버(System.IO.Pipelines) |
| 프론트 | Blazor WebAssembly 2종 + SkiaSharp(DXF 도면 렌더링), WPF 클라이언트/에뮬레이터 |
| DB | PostgreSQL(TimescaleDB, Docker) — `authdb`(Identity) / `ftmsdb`(운영 데이터), 일부 SQLite |
| 배포 | Windows Service(Release 빌드 시 `UseWindowsService`) 또는 Linux, 앞단에 YARP 리버스 프록시 |
| 규모 | 약 33개 프로젝트, C#/Razor/XAML 약 9만 줄 (가장 큰 것: DxfBlazorViewer 2.6만, app-server 1.4만) |

---

## 2. 전체 S/W 구조

```
 [현장 도크 PC / PLC 게이트웨이]                   [운영자 브라우저]
   tcpclient.emul.ftms (WPF 에뮬)                          │ http :8000 (예)
   tcplinent.emul.console.ftms (콘솔 에뮬)                  ▼
        │ TCP 6004~6006                        ┌─────────────────────────┐
        │ STX(0x02) JSON ETX(0x03)             │ reverse-proxy.ftms (YARP)│
        ▼                                      └──┬──────────┬───────────┘
 ┌──────────────────────────────────────────┐    │/api,/chathub,/health
 │            app-server.ftms  (:8121)      │◄───┘          │ 그 외 전부
 │ ┌───────────────┐   ┌──────────────────┐ │               ▼
 │ │TcpServerFinal │──►│ITcpMessageHandler│ │   ┌──────────────────────────┐
 │ │Service(Pipes) │   │ Ping/Json/Dock.. │ │   │ web-server.ftms (:8122)   │
 │ └───────────────┘   └───┬──────────────┘ │   │ 정적파일 호스팅(WASM 산출물)│
 │     Channel<string> ────┘   │heartbeat   │   └──────────────────────────┘
 │        ▼                    ▼            │         ▲ 배포되는 SPA
 │ DataCollectorService  LogisticsSystem-   │   wasm-scada.ftms  (구버전 UI)
 │  ├ 파일/DB 배치 적재    StateManager      │   DxfBlazorViewer.Client (현행 UI)
 │  ├ PG 프로시저 집계     └ EventDataStore- │
 │  └ 5분 누적 버퍼          Service(메모리+DB)│
 │ Controllers(REST) · SignalR ChatHub      │
 │ Identity + JWT(Access) + RefreshCookie   │
 └───────────────┬──────────────────────────┘
                 ▼
     PostgreSQL/TimescaleDB : authdb, ftmsdb
     (collected_payload → process_collected_payload_v4() →
      process_counts / rawdata / minute·hour·day 누적 테이블)
```

### 핵심 데이터 흐름

1. **Heartbeat**: 도크 → `{"type":"heartbeat", ...DeviceStatuses}` → `JsonMessagedHandler` → `LogisticsSystemStateManager.GetResponseHeartbeatMessageAsync`
   - 장치 상태 문자열 정규화(Sanitize) 후 `EventDataStoreService`에 저장(메모리 + 10초 주기 DB flush)
   - 응답에 해당 도크의 **현재 분류(Sorting) 파라미터**를 실어서 돌려줌 → 서버가 설정을 "푸시"하는 구조
   - `DeviceStatusManager`가 도크 온라인/오프라인 상태를 관리
2. **ProcessCount(처리수량)**: 도크 → `type:process_count` → `DataCollectorChannel`(Unbounded) → `DataCollectorService`
   - (a) 10개의 회전 큐에 쌓았다가 1초/2만건마다 CSV 텍스트로 `collected_payload` 테이블에 적재
   - (b) 별도 루프가 PG 함수 `process_collected_payload_v4`를 호출해 원본/분/시/일 단위 집계
   - (c) 최근 N분(기본 5분, DEBUG 1분) 슬라이딩 누적값을 메모리에 유지 → UI 실시간 표시
   - 처리 결과는 SignalR `CollectedCountReceived`로 브라우저에 푸시
3. **설정 변경**: 웹 → `/api/setting` (AssetsController) → DB 저장 + `AssetItemUpdateChannel` → StateManager가 메모리 반영 → 다음 Heartbeat 응답으로 도크에 전달
4. **UI**: Blazor WASM이 REST(`/api/...`)로 조회, SignalR(`/chathub`)로 서버 상태·하트비트·집계 수신, DXF 도면(`/api/dxf`, MessagePack+LZ4)을 SkiaSharp로 렌더링

---

## 3. 프로젝트별 기능

### 3.1 서버 (servers/ftms) — 본 제품의 핵심

| 프로젝트 | 종류 | 기능 / 특징 |
|---|---|---|
| **app-server.ftms** | ASP.NET Core Web (net8.0) | 모든 백엔드 로직. TCP 서버, REST 컨트롤러(Auth, Users, ProcessCount, EventLog, RawData, ScanData, Dock, Settings, RuntimeConfigChange, DataUpload), Minimal API(`/api/status`, `/api/dxf`, `/api/commands` 등 `IApiCommand` 자동등록), SignalR Hub, HealthCheck(`/health`), BackgroundService 8종. 기동 시 `EnsureCreated` + 기본 Role/계정 시드 + `.default.settings/*.json·tsv`를 DB에 반영 후 타임스탬프 폴더로 이동 |
| **app-shared.ftms** | Class Library | 서버·클라이언트 공용 모델/DTO(ProcessCount, HeartBeat, DockStatus, EventLog, Setting…), TCP 프레이밍 `ProtocolParser`(Line / Frame 모드), 권한 모델 `Account`(7개 Role × Permission 플래그), JSON 헬퍼 |
| **reverse-proxy.ftms** | YARP | `/health`, `/chathub/**`, `/api/**` → 8121, 나머지 → 8122. 업로드 한도 500MB |
| **web-server.ftms** | ASP.NET Core | WASM 정적 파일 호스팅(+ `.wasm/.dll/.dat` MIME·1년 캐시), SPA fallback |
| **DxfBlazorViewer.Client** | Blazor WASM | **현행 운영 UI**. 대시보드(`/dashboard`), 모니터링, DXF 도면 뷰어(Skia), 통계, 알람, RawData 검색/다운로드, 설정, 사용자 권한관리, 관리자 Health. 공용 어셈블리를 **ProjectReference가 아닌 NuGet 패키지(1.0.0.1)**로 참조 |
| **wasm-scada.ftms** | Blazor WASM | 구버전/실험 UI(Operation, SortingRuleEditor, EventLogView, D3/Three.js 샘플, 가상키보드 등). ProjectReference 기반 |
| **tcpclient.emul.ftms** | WPF | 도크 클라이언트 에뮬레이터(다중 서버 연결, 재연결·KeepAlive, 모니터링 통계). `appsettings - <IP>.json`로 현장별 설정 |
| **tcplinent.emul.console.ftms** | Worker | 위 에뮬레이터의 콘솔(헤드리스) 버전 |
| tcpclient-linux.emul.ftms_______ | Web | 폐기(이름에 `_______`), 솔루션 미포함 |

### 3.2 공용 어셈블리 (Assembies — 폴더명 오타 그대로)

| 프로젝트 | TFM | 기능 |
|---|---|---|
| Assembly.Iocp | netstandard2.0 | SocketAsyncEventArgs 기반 IOCP TCP 서버/클라이언트, 자동 재연결 정책(고정횟수/지수백오프), 바이트 버퍼 파서 |
| Assembly.Data.Collector | net8.0 | 수집 데이터 저장소. `CollectedPayloadRepository`(스키마·PL/pgSQL 함수 생성 SQL 내장, 재시도/Dead-letter), `ProcessCountRepository`(COPY 기반 Bulk Upsert, 집계 호출), SQLite 파일 테이블 |
| EventDataStore | net8.0 | `event_data_store`(JSONB) 엔티티/DbContext/인터페이스 — 키-값 형태 최신 상태 저장 |
| Assem.DxfSceneParser | netstandard2.0 | IxMilia.Dxf로 DXF → `DxfSceneDto`(MessagePack) 변환 |
| Assembly.JwtTokenGenerator | netstandard2.0 | Access/Refresh JWT 생성·디코딩(HS256) |
| Assembly.LoginManager | net8.0 | WASM용 로그인 상태 관리(Real/Mock AuthService, 토큰 만료·재발급) |
| Assembly.ChatHub(.Shared) / SR.ChatClientManager | ns2.0 / net8.0 | SignalR Hub(`ChatHub`), 이벤트명 상수, 세션 관리, 클라이언트 래퍼 |
| Blazored.Toast | Razor Lib | 토스트 UI(외부 라이브러리를 사내화), FontAwesome 포함 |
| HttpClientFactory | ns2.0 | WPF용 HttpClient 래퍼 |
| YiApp.MvvmCore | ns2.1 | Prism 스타일 MVVM(BindableBase, DelegateCommand…) |
| MemoryStoreTemplate | multi | IMemoryCache 기반 KeyValue 스토어 템플릿 |

### 3.3 클라이언트 / 기타

| 프로젝트 | 기능 |
|---|---|
| **CJ.FTMS.Viewer** (WPF, net8.0-windows10.0.19041) | 데스크톱 뷰어: 탭(System Monitoring / 인식상태 / Log), netDxf+SkiaSharp 도면, `Test/DongleLicense*`(USB/COM 동글 라이선스 시험) |
| pacmgr / pacmgr.svc (CJ.SCADA.Clone.sln에만 포함) | 별도 "파라미터 관리" 서브시스템(API·Blazor Server·WASM·WPF). SQLite + Identity + JWT. FTMS와는 독립적이며 초기 버전 성격 |
| Test/*, Test2/* | `.sln`에는 등록되어 있으나 **zip에 폴더가 없음** → 솔루션 로드 시 "unloaded" |

---

## 4. 기술적 특징 (잘 된 점)

- **System.IO.Pipelines 기반 TCP 수신** + 구분자 프레이밍(`STX..ETX` Frame / 종단자 Line 두 모드) — 부분 패킷·연속 패킷 처리가 올바름
- **메시지 핸들러 플러그인 구조**: `ITcpMessageHandler` 구현체를 리플렉션으로 자동 DI 등록, `Priority`로 정렬
- **Minimal API 커맨드 패턴**: `IApiCommand` 구현만 추가하면 `/api/{name}`, `/api/{name}/sync` 자동 매핑
- **고속 수집 경로**: 회전 큐(10개) → 배치 → PostgreSQL `COPY`/PLpgSQL 집계, TimescaleDB 분/시/일 누적
- **DXF 도면 전송 최적화**: MessagePack + LZ4 압축, Accept 헤더로 JSON/MsgPack 선택
- **권한 모델**: Role 7단계 × Permission 비트플래그 → Policy 자동 생성, 역할 위임 규칙(`AssignableRoles`)
- **운영 편의**: Serilog 파일 롤링(14일), 런타임 로그레벨 변경 API(`LoggingLevelSwitch`, 클라이언트별 TCP 로그레벨), `/health`에 외부서버 연결 상태 포함
- `.editorconfig`, `Directory.Build.props`(버전 1.0.0.5 일괄 관리)

---

## 5. 주의할 점 (중요도 순)

### 5.1 🔴 보안 — 운영 전 반드시 조치

| # | 위치 | 문제 | 영향 |
|---|---|---|---|
| S1 | `app-server.ftms/Controllers/AuthController.cs:82,103` | `POST /api/auth/register`가 **익명 허용**이고, 가입자에게 **`Admin` 역할을 자동 부여** | 네트워크에 접근 가능한 누구나 관리자 계정 생성 → 사용자·설정·데이터 전체 장악 |
| S2 | `Controllers/*` (Users 제외 전부) | `ProcessCount`, `EventLog`, `RawData`, `ScanData`, `Setting`, `RuntimeConfigChange`, `DataUpload`, `Dock` 컨트롤러에 **`[Authorize]`가 없음**. `ScanData`에는 `DELETE /all`, `/range`까지 존재 | 인증 없이 데이터 조회·삭제·설정 변경·도크 파라미터 변경 가능. 정의해 둔 Policy(`CanAdjustParams` 등)가 실제로는 거의 적용되지 않음 |
| S3 | `Data/Seeders/IdentityDataSeeder.cs:14` | 모든 Role에 대해 **ID=비밀번호=역할명 소문자**(`admin/admin`, `manager/manager` …) 계정 자동 생성, 비밀번호 정책 최소 4자 | 기본 계정으로 즉시 로그인 가능 |
| S4 | `appsettings.json` (app-server, pacmgr.server 동일 값) | JWT `SecretKey`, DB 비밀번호(`tapsuser/taps12`), PFX 비밀번호(주석), `certs/*.pfx` 파일이 소스에 포함 | 비밀키 유출 시 임의 토큰 위조 가능. 두 시스템이 같은 키를 공유 |
| S5 | `Program.Extensions.cs:271` | CORS `SetIsOriginAllowed(_ => true)` + `AllowCredentials()` | 임의 사이트에서 사용자 쿠키(refresh token)로 API 호출 가능 → S1/S2와 결합 시 위험 증가 |
| S6 | `AuthController.cs:183` 등 | Access/Refresh 토큰을 **Console에 평문 출력**, Refresh 토큰 DB 평문 저장, 쿠키 `Secure=false` | 로그/콘솔 접근자에게 세션 탈취 |
| S7 | `ChatHub` / TCP 서버 | SignalR Hub에 인증 없음(JWT 쿼리스트링 처리 주석처리됨), TCP 6004~6006은 무인증·평문, 임의 클라이언트가 Heartbeat 위조 가능 | 현장망 분리 전제 — 망 분리가 깨지면 상태 위조/설정 수신 가능 |
| S8 | `ForwardedHeaders` | 운영에서도 `KnownNetworks/Proxies.Clear()` | `X-Forwarded-For` 위조로 IP 기반 refresh 토큰 바인딩 무력화 |
| S9 | Login | `CheckPasswordAsync`만 사용 → 계정 잠금(`AuthSettings.MaxLoginAttempts`) 미적용 | 무차별 대입 가능 |

### 5.2 🟠 안정성 / 데이터 정합성 버그

| # | 위치 | 문제 |
|---|---|---|
| B1 | `Services/EventDataStoreService.cs:72-73, 153` | `new Timer(async _ => ...)`는 **async void**. `DeleteOldEventsAsync`가 예외를 `throw;`로 재던지므로 **DB 일시 장애 시 처리되지 않은 예외로 서버 프로세스가 종료**될 수 있음 |
| B2 | `EventDataStoreService.cs:289` | Flush 후 `buf.Value.Clear()` — (a) DB Insert 실패해도 비움(내부에서 예외를 삼킴), (b) 세마포어 대기 실패로 skip돼도 비움, (c) Insert 도중 들어온 신규 값도 함께 삭제 → **Heartbeat/파라미터 상태 DB 영속화 누락** |
| B3 | `Managers/LogisticsSystemStateManager.cs:855` | 도크 전용 파라미터가 없으면 공유 `"Default"` 객체를 꺼내 **그 객체의 `DockId`를 직접 수정**. 여러 도크 세션이 동시에 처리되므로 응답에 다른 도크 ID가 섞일 수 있고, 메모리 원본도 오염됨 → 복사본을 만들어야 함 |
| B4 | `TcpMessageHandlers/DockStatusMessageHandler.cs:41` | `GetAllStatusAsync()`를 **await 없이** 직렬화 → `Task` 객체가 JSON으로 나감(`/api/dock/status` TCP 명령이 의미 없는 응답) |
| B5 | `JsonMessageHandler.cs:119` | `channelWriter.WriteAsync(message)` 미await(ValueTask 버림). 현재 Unbounded라 동작은 하지만 Bounded로 바꾸는 순간 유실 |
| B6 | `Services/DataCollectorService.cs:392` | `CallAggregationDbProcedureContinuouslyAsync` 루프에 try/catch 없음 → **DB 오류 1회로 집계 루프 영구 정지**(로그도 채널 루프 종료 시점까지 안 남음) |
| B7 | `DataCollectorService.cs:112` | 생성자에서 `InitializeAsync()`(스키마/함수 생성) fire-and-forget → 초기화 전에 적재가 시작될 수 있음, 실패도 관찰 안 됨 |
| B8 | `DataCollectorService.StopAsync` | `Flush()`가 `Task.Run`만 걸고 반환 → 서비스 종료 시 마지막 배치 **유실 가능** |
| B9 | `LogisticsSystemStateManager.cs:454` | "누적 카운트를 초기화합니다" 로그와 달리 실제로는 같은 값을 다시 저장(초기화 안 됨) |
| B10 | `EventDataStoreService.GetAsync` | 내부 `ConcurrentDictionary`와 그 안의 `List<ProcessCountL>` 등 **가변 객체를 그대로 반환** → API 직렬화 중 백그라운드가 리스트 수정 시 `Collection was modified` 가능. 또한 키가 `typeof(T).Name`이라 `List<A>`와 `List<B>`가 모두 `"List`1"`로 충돌 |
| B11 | `TcpServerServiceBase` / `ProtocolParser` | 메시지 최대 길이 제한 없음. 구분자 없는 데이터가 오면 Pipe가 64KB에서 멈춰 타임아웃까지 세션이 매달림(타임아웃 10초 설정 시 자동 정리됨) |
| B12 | `LogisticsSystemStateManager.StartAsync` | DB 연결 실패 시 `throw` → 호스트 전체 기동 실패. DB가 늦게 뜨는 Docker 환경에서 재시도 로직 없음 |
| B13 | `AuthController.Refresh` | `lock` 안에서 `GetAwaiter().GetResult()`(sync-over-async) → 동시 요청 시 스레드풀 고갈 위험. 또한 인스턴스마다 다른 `_db`를 쓰므로 lock의 의미가 약함 |
| B14 | `JsonMessagedHandler.CanHandle` | `type` 값 비교가 대소문자 구분(키는 무시) — `"Heartbeat"`로 보내면 "Unknown command" 응답 |

### 5.3 🟡 빌드 / 구성 / 배포

- **DxfBlazorViewer.Client**가 `Assembly.ChatHub.Shared`, `Assembly.JwtTokenGenerator`, `Assembly.LoginManager` **1.0.0.1을 NuGet으로 참조**하지만 zip에 nuget.config·로컬 피드가 없음(`.packages`엔 YiApp.*만 있음) → 다른 PC에서 **복원 실패**. 반대로 wasm-scada는 ProjectReference → 두 UI가 서로 다른 버전의 공용 코드를 쓸 수 있음. `Directory.Build.props`의 버전(1.0.0.5)과도 불일치
- 패키지 버전 **와일드카드(`8.*`, `3.*`, `0.*`)** 다수 → 빌드 시점마다 다른 버전, 재현 불가. `IxMilia.Dxf 0.*`는 pre-1.0이라 깨질 수 있음. Central Package Management(`Directory.Packages.props`) 권장
- `global.jso_` 이름 때문에 SDK 고정이 꺼져 있음(의도 확인 필요)
- `.sln`에 `Test\...` 프로젝트가 있으나 폴더 누락, `CJ.SCADA.Clone.sln`은 존재하지 않는 `bridge-server.ftms`, `scada-server.ftms`, `ftms.shared` 등을 참조
- `EnsureCreated()` 사용 + Migrations 미사용 → **스키마 변경 시 기존 DB에 반영 안 됨**. 추가로 `Database/*.sql`, `CollectedPayloadRepository` 내장 SQL, EF 모델 세 곳에 스키마가 분산
- 기동할 때마다 `.default.settings` 파일을 DB에 넣고 **파일을 이동**시킴 → 재배포 시 설정 파일이 사라진 것처럼 보임
- 포트: TCP 6004/6005/6006, API 8121, Web 8122, Proxy(개발) 8000 — `appsettings.json`의 `TcpServer`, `TcpClientFinal`, `ExternalServers`는 현재 코드에서 사실상 미사용(혼동 주의)
- Release 빌드에서만 Windows Service로 동작(`#if !DEBUG`), DEBUG에서는 누적 윈도우가 5분 → **1분으로 바뀜** — 디버그/운영 결과 차이 주의
- zip의 한글 파일명이 `#Ubcf5#Uc0ac#Ubcf8`처럼 깨져 있음(압축 도구 인코딩 문제). 이 상태로 풀면 `csproj`의 `Compile/Content Remove "…복사본…"`이 적용되지 않아 `Viewer - 복사본.razor`가 빌드에 포함되고 `@page "/viewer"` **라우트 중복**이 발생함
- 저장소에 **런타임 산출물 포함**: `pacmgr.db3(-shm/-wal)`, `Database/auth.db`, `.dxf/*.bak`, `*.7z`, `*.zip`, `*.csproj.user`, `*.pubxml.user` → `.gitignore` 정리 필요

### 5.4 🟢 유지보수성

- 사용하지 않는 코드가 매우 많음: `#if false` 블록, `_v1/_v2/_v3`, `- Copy`, `- old`, `.NA`, `TcpClient-NA`, `AssetsController/**`(Compile Remove) 등 — 실제 빌드 대상 파악이 어려움. Git 이력에 맡기고 삭제 권장
- 거대 파일: `Viewer.razor.cs` 3,667줄, `DxfSkiaRendererFast.cs` 3,100줄, `LogisticsSystemStateManager` 1,024줄 — 책임 분리 필요
- `catch (Exception) { }` 빈 catch가 광범위 → 장애 원인 추적 어려움(B2, B5와 직결)
- `Console.WriteLine`과 Serilog 혼용, 이름 오타(`Assembies`, `Ploicy`, `tcplinent`, `JsonMessagedHandler`, `DockerStatusLogicService`(Dock) 등)
- `LogisticsSystemStateManager`는 이름은 Manager지만 BackgroundService + 싱글턴 이중 등록(코드 주석에도 언급)
- 자동화 테스트 프로젝트 없음(`Test.EventDataStore`는 zip에 없음)

---

## 6. 권장 조치 우선순위

1. **즉시(보안)**: `register` 엔드포인트 제거 또는 `[Authorize(Policy=CanManageUsers)]` + 기본 Role을 Viewer로 / 모든 컨트롤러에 `[Authorize]` 기본 적용(`AuthorizeFilter` 전역 등록 후 필요한 곳만 `[AllowAnonymous]`) / 시드 계정 제거 또는 최초 로그인 시 비밀번호 변경 강제 / Secret·DB 비밀번호를 환경변수·User Secrets로 이동하고 **키 교체** / CORS 화이트리스트 / 토큰 콘솔 출력 제거
2. **단기(안정성)**: B1(타이머 async void) · B2(Flush 유실) · B3(Default 객체 오염) · B6(집계 루프 정지) 수정 — 모두 수 줄 수준의 수정으로 해결 가능
3. **중기(빌드)**: nuget.config + 로컬 피드 또는 ProjectReference로 통일, 패키지 버전 고정(CPM), `global.json` 활성화, 누락 프로젝트를 sln에서 제거, EF Migrations 도입
4. **장기(구조)**: 죽은 코드 정리, 대형 파일 분리, 빈 catch 제거·로깅 일원화, 핵심 파이프라인(ProtocolParser, 누적 집계, Heartbeat 응답) 단위 테스트 추가
