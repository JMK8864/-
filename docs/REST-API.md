# CJ.SCADA FTMS — REST API 사용 현황

## 1. 구조

```
브라우저(DxfBlazorViewer WASM) ──HTTP──► reverse-proxy(YARP/nginx) ──/api/*──► app-server.ftms :8121
   HttpClient(BaseAddress = 현재 사이트 주소)                         (ASP.NET Core Controllers + Minimal API)
```

- 서버: `servers/ftms/app-server.ftms/Controllers/*.cs` (MVC 컨트롤러), `APIs/EndpointMapper*.cs` (Minimal API)
- JSON: camelCase, 한글 이스케이프 없음 (`Program.cs` AddJsonOptions)
- 개발 시 Swagger: `http://localhost:8121/swagger`
- 서버↔도크 통신은 REST가 아니라 **TCP(6004~6006)**, 실시간 푸시는 **SignalR(/chathub)**

## 2. 서버 엔드포인트 목록

인증 열의 `없음` = `[Authorize]` 미적용 (분석 보고서 보안 S2)

### 인증/사용자
| 메서드 | 경로 | 인증 | 기능 |
|---|---|---|---|
| POST | /api/auth/login | 없음 | `{username,password}` → 본문: Access JWT(15분), 쿠키: `ftms-api-refresh-token`(7일) |
| POST | /api/auth/refresh | 쿠키 | Refresh 쿠키로 새 Access JWT |
| POST | /api/auth/logout | Bearer | 토큰 폐기, 쿠키 삭제 |
| POST | /api/auth/change-password | Bearer | 비밀번호 변경 |
| POST | /api/auth/register | **없음** | 회원가입 (**Admin 권한 자동 부여 — 보안 S1**) |
| GET | /api/users?… | Admin | 사용자 목록 |
| GET | /api/users/{userId} , /{userId}/roles , /me/roles , /roles/all | Admin | 사용자·역할 조회 |
| POST | /api/users/{userId}/assign-roles | Admin | 역할 부여 |

### 운영 데이터
| 메서드 | 경로 | 기능 | 데이터 출처 |
|---|---|---|---|
| GET | /api/Dock/status | 도크/장치 상태(Heartbeat) | 메모리(StateManager) |
| GET | /api/ProcessCount/live/latest-n-minutes?dockIds= | 최근 5분 누적 처리수량 | 메모리 |
| GET | /api/ProcessCount/statics?… | 기간 통계 | DB 함수 `get_dock_process_statistics_lastest` |
| GET | /api/ProcessCount/accumulated/daily , /daily-by-dock | 일 누적 | `process_count_per_day_*` |
| GET | /api/ProcessCount/{dockId} , /daterange , /by-pid/{pid} | 원본 조회 | `process_counts` |
| POST/PUT/DELETE | /api/ProcessCount[/{dockId}/{pid}] | 원본 수정 | `process_counts` |
| GET | /api/EventLog , /unacknowledged , /level/{l} , /device/{id} , /code/{c} , /range , /{id} , /definitions | 알람 조회 | `ftms_event_log` |
| POST | /api/EventLog/search , /search/fast | 알람 검색 | 〃 |
| PUT | /api/EventLog/{id}/acknowledge , /acknowledge/bulk | 알람 확인 처리 | 〃 |
| GET | /api/EventLog/statistics/by-level , /by-device , /timeseries/hourly | 알람 통계 | 〃 |
| DELETE | /api/EventLog/range | 알람 기간 삭제 | 〃 |
| GET/POST | /api/RawData/search/cursor , /search/offset , /download | 원본 스캔 데이터 검색(커서/페이지)·다운로드 | `rawdata_table` |
| GET/POST/PUT/DELETE | /api/ScanData/… (all, range 삭제 포함) | 스캔 데이터 | `scan_data` |

### 설정 저장소 (`/api/setting`, AssetsController)
| 메서드 | 경로 | 기능 |
|---|---|---|
| GET | /api/setting?type=json&category=…&machineId=…&groupId=…&key=…&pageNumber=&pageSize= | 목록 |
| GET | /api/setting/json/{category}/{machineId}/{groupId}/{key} | 단건 JSON |
| POST | /api/setting/json?category=&machineId=&groupId=&key=&userId= (본문: JSON 원문) | 저장 → **메모리 반영 → 다음 Heartbeat로 도크에 전달** |
| GET/POST | /api/setting/text/… , /api/setting/blob/… | 텍스트/바이너리 |
| DELETE | /api/setting/{id} , /json|text|blob/{category}/{machineId}/{groupId}/{key} | 삭제 (메모리 미반영 주의) |

주요 category: `dock.sorting.param`(도크 분류 파라미터), `param.set`(파라미터 세트), 라벨류(LabelsModal)

### 운영/기타
| 메서드 | 경로 | 기능 |
|---|---|---|
| GET | /health | 서버·외부 연결 상태(JSON) |
| GET | /api/status , /api/test01 | 상태(Minimal API) |
| GET | /api/commands | 등록된 IApiCommand 목록 |
| GET | /api/dxf (/sync) | 현장 도면. `Accept: application/x-msgpack`이면 MessagePack+LZ4, 아니면 JSON |
| GET | /api/state , /api/count01 , /api/test02 | IApiCommand 샘플 |
| PUT | /api/RuntimeConfigChange/application/log-level , /clients/by-id/log-level , /clients/by-nickname/log-level | 로그 레벨 런타임 변경 |
| POST | /api/DataUpload/file , /json , /binary , /sampledata1 | 업로드 시험용 |

## 3. 호출하는 곳 (클라이언트)

### DxfBlazorViewer.Client (현행 웹 UI)
| 화면/파일 | 호출 API | 방식 |
|---|---|---|
| `Index.razor` (로그인 후 첫 화면) | /api/auth/change-password, /api/auth/register | Bearer 헤더 수동 첨부 |
| `Viewer.razor.cs` (`/viewer`) | /api/dxf, /api/Dock/status, /api/ProcessCount/live/latest-n-minutes, /api/EventLog/search/fast, /api/setting | **PeriodicTimer 폴링**(1초 등) |
| `Ppc.razor` (`/dashboard`) | /api/ProcessCount/statics | 주기 조회 |
| `Monitoring.razor` (`/monitor`) | /api/ProcessCount/live/latest-n-minutes, /api/setting(dock.sorting.param) | 주기 조회 |
| `Stats.razor.cs` (`/stats`) | /api/ProcessCount/accumulated/daily, /daily-by-dock | 조회 시 |
| `Alarm.razor.cs` (`/alarms`) | /api/EventLog/search/fast, /{id}/acknowledge, /acknowledge/bulk, /api/dxf, /api/setting | 폴링 + 버튼 |
| `RawData.razor.cs` (`/raw-data`) | /api/RawData/search/cursor, /search/offset, /download | 검색·다운로드 |
| `Settings.razor.cs` (`/settings`) | GET/POST /api/setting(/json) | 저장 시 POST |
| `LabelsModal.razor` | POST /api/setting/json (레인/도크 라벨 3종) | 저장 시 |
| `AdminHealth.razor` (`/admin/health`) | /health, PUT /api/RuntimeConfigChange/clients/by-nickname/log-level | Bearer 헤더 수동 첨부 |
| `UserRoleManagement.razor` | /api/users…, /roles/all, /assign-roles | Bearer 헤더 수동 첨부 |
| `DxfServerAdapter.cs` | /api/dxf (MessagePack 우선, ETag 캐시), /api/dxf/local | ⚠ `/api/dxf/local`은 서버에 없음(404) |
| `Assembly.LoginManager/RealAuthService` | /api/auth/login, /refresh, /logout | 쿠키 포함 요청 |

공통 방식
- `Program.cs`: `AddHttpClient("DxfBlazorViewer.ServerAPI", BaseAddress = 현재 사이트)` + `IncludeCredentialsHandler`(모든 요청에 브라우저 쿠키 포함)
- 각 페이지는 `@inject HttpClient`(또는 ApiClient)로 `GetFromJsonAsync` / `PostAsJsonAsync` / `PostAsync(StringContent)` 사용
- **Bearer 토큰은 일부 페이지(Index, AdminHealth, UserRoleManagement)만 수동으로 붙임** → 나머지 데이터 API는 토큰 없이 호출(서버도 인증 미요구)

### 기타 클라이언트
| 클라이언트 | 호출 | 비고 |
|---|---|---|
| wasm-scada.ftms (구 UI) | 유사한 /api/* + /api/auth/* | 구버전 |
| CJ.FTMS.Viewer (WPF) | `http://127.0.0.1:5999/api/status`, `/health` (하드코딩) | ⚠ 포트 5999는 현재 서버(8121)와 불일치 → 동작 안 함 |
| 도크 PC / 에뮬레이터 | REST 사용 안 함 (TCP) | |

## 4. 호출 예시

```http
POST /api/auth/login
Content-Type: application/json

{"username":"admin","password":"admin"}
→ 200 "eyJhbGciOi..."            (Access JWT, 문자열)
   Set-Cookie: ftms-api-refresh-token=...; HttpOnly
```

```http
GET /api/ProcessCount/live/latest-n-minutes?dockIds=B22&dockIds=B23
→ 200 [ ProcessCountL 배열 (dockId, vehicleNumber, createdAt, scanner, dimensioner, judge, sorting …, camelCase) ]
```

```http
POST /api/setting/json?category=dock.sorting.param&machineId=B22&groupId=global&key=curr.param&userId=admin
Content-Type: application/json

{"dockId":"B22","paramName":"SET-A","updatedAt":"2026-10-12T00:00:00Z"}   (DockSortingActiveParameter, 예시)
→ DB 저장 + 메모리 반영 → B22의 다음 Heartbeat 응답 SetParameter 로 전달
```

```http
PUT /api/EventLog/acknowledge/bulk
Content-Type: application/json
{"eventIds":[101,102,103],"eventMemo":"현장 확인 완료","acknowledgedBy":"admin"}
```

## 5. 주의

- **보안 수정 시 연쇄 영향**: 컨트롤러에 `[Authorize]`를 추가하면 Viewer/Monitoring/Alarm 등 토큰을 붙이지 않는 화면이 모두 401 → `LoginManager.AccessToken`을 자동으로 붙이는 `DelegatingHandler`를 Program.cs의 HttpClient에 함께 추가해야 함
- Viewer는 여러 API를 1초 단위로 폴링 → 브라우저 수 × 폴링 수만큼 서버 부하. 실시간성이 필요한 것은 SignalR 푸시로 전환 검토
- `/api/setting` DELETE는 메모리에 반영되지 않음(서버 재시작 전까지 이전 설정이 도크로 전송됨)
