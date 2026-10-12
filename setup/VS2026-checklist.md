# VS2022로 개발된 CJ.SCADA 솔루션을 VS2026에서 빌드·실행하기 — 체크리스트

## 0. 결론
VS2026은 .sln 을 변환 없이 열 수 있지만, **VS2026에 기본으로 없는 .NET 8 구성요소**와
**원본 PC에만 있던 것(로컬 패키지·버전·시작 설정)**을 채워야 합니다.

## 1. 설치 (한 번만)

| # | 항목 | 방법 | 이유 |
|---|---|---|---|
| 1 | VS2026 워크로드 | 설치 관리자 → 수정 → **ASP.NET 및 웹 개발**, **.NET 데스크톱 개발** | 서버/Blazor, WPF |
| 2 | VS2026 개별 구성요소 | **.NET WebAssembly 빌드 도구** (+ 보이면 .NET 8.0 런타임) | Blazor WASM(SkiaSharp) 네이티브 빌드 |
| 3 | **.NET 8 SDK 8.0.4xx** | https://dotnet.microsoft.com/download/dotnet/8.0 → SDK x64 설치 | VS2026은 .NET 10 SDK만 포함. .NET 8 런타임도 함께 설치됨 |
| 4 | wasm 워크로드 | 솔루션 폴더에서 관리자 PowerShell: `dotnet workload install wasm-tools` | 없으면 "wasm-tools-net8 워크로드 필요" 오류 |
| 5 | PostgreSQL 17 + TimescaleDB | 설치 후 `setup/db/README.md` 절차 | 실행(디버깅) 시 DB 필수 |

## 2. 솔루션 설정 (vs2026-overlay.zip 덮어쓰기 + setup-dev.ps1)

| 항목 | 내용 | 안 하면 생기는 오류 |
|---|---|---|
| `global.json` | SDK 8.0.4xx 고정 (원본의 `global.jso_` 복원) | SDK 10으로 빌드되어 워크로드 이름·동작이 달라짐 |
| `nuget.config` | nuget.org + `.packages`(YiApp) + `.localfeed`(Assembly.* 1.0.0.1) | **NU1101** Assembly.ChatHub.Shared 등 패키지 없음 |
| `.localfeed` 생성 | `setup-dev.ps1`이 3개 라이브러리를 `dotnet pack` | 〃 |
| csproj 버전 고정 | `8.*`/`3.*` → 2026-06 시점 버전, SkiaSharp 3.119.2 | **NU1701** SkiaSharp.Views.WPF 호환성 경고, 빌드마다 다른 버전 |
| `CJ.SCADA.Build.slnf` | zip에 없는 Test 프로젝트 2개 제외 | "프로젝트 파일을 찾을 수 없음" |
| .slnx 변환 | VS가 물으면 **아니요** | VS2022에서 못 열게 됨 |

## 3. 실행(디버깅) 설정

| 항목 | 내용 |
|---|---|
| 시작 프로젝트 | 솔루션 우클릭 → 시작 프로젝트 구성 → 여러 개: **app-server.ftms**(8121), **DxfBlazorViewer.Client**(8122), **reverse-proxy.ftms**(8000), 프로필 모두 `http`. wasm-scada는 같은 8122라 함께 켜지 말 것 |
| 연결 문자열 | `app-server.ftms/appsettings.json` 의 DefaultConnection1/2 비밀번호, `Jwt:SecretKey` |
| DB 초기화 | `01_create_db_and_user.sql` → **app-server 1회 실행 후 종료** → `02_ftmsdb_timescale_setup.sql` |
| 도면 파일 | `.dxf/FTMS.v1.dxf` → `app-server.ftms/bin/Debug/assets/` (또는 환경변수 `FTMS_DXF_PATH`) |
| WASM 디버깅 | 도구 → 옵션 → 디버깅 → "ASP.NET에 대해 JavaScript 디버깅 사용" |
| 방화벽 | 첫 실행 시 허용 (TCP 6004~6006) |
| 시험 데이터 | 실행 중 `tcpclient.emul.ftms` 우클릭 → 디버그 → 새 인스턴스 시작 |

## 4. 대안: .NET 8 SDK를 설치할 수 없을 때
- `global.json` 삭제 → SDK 10으로 빌드, 워크로드는 `dotnet workload install wasm-tools-net8`
- 단, 결과물이 net8.0 이므로 **실행하려면 .NET 8 런타임(ASP.NET Core 8 + Desktop 8)** 은 따로 필요

## 5. 자주 나는 오류

| 메시지 | 조치 |
|---|---|
| 워크로드 wasm-tools-net8 필요 | 1-4 (SDK 10이면 `wasm-tools-net8`) |
| global.json 의 SDK 를 찾을 수 없음 | 1-3 설치, 또는 global.json 의 version 을 설치된 8.0.4xx 로 |
| NU1101 Assembly.* 없음 | 2 의 `.localfeed` (setup-dev.ps1) |
| NU1701 SkiaSharp.Views.WPF | csproj 3.119.2 고정 |
| 42P01 "ScanData"/"scan_data" 릴레이션 없음 | DB에 테이블이 이미 있는 상태에서 앱 실행 → 빈 DB로 다시 (3 의 순서) |
| 화면 통계가 비어 있음 | TimescaleDB 미설치 → 집계 함수가 조용히 0건 처리 |
| 포트 8122 사용 중 | wasm-scada 와 DxfBlazorViewer 동시 실행 금지 |
| .NET 8 지원 종료 경고 | .NET 8 지원은 2026-11-10 종료 → 장기 운영 시 .NET 10 전환 계획 |
