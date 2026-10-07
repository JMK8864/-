# VS2026에서 원본 PC와 동일한 빌드 환경 맞추기

`vs2026-overlay.zip`을 **CJ.SCADA.Solution 폴더에 덮어쓰기**로 풀고 `setup-dev.ps1`을 관리자 PowerShell로 실행합니다.

## 포함 파일

| 파일 | 내용 |
|---|---|
| `global.json` | .NET SDK 8.0.414 이상 8.0.4xx로 고정 (원본 `global.jso_`의 의도 복원, `rollForward: latestPatch`) |
| `nuget.config` | 패키지 소스 고정: nuget.org + `.packages`(YiApp) + `.localfeed`(Assembly.* 1.0.0.1) |
| `CJ.SCADA.Build.slnf` | zip에 없는 Test 프로젝트 2개를 뺀 솔루션 필터 (22개 프로젝트) |
| `setup-dev.ps1` | SDK 확인 → `wasm-tools` 설치 → `.localfeed` 생성(dotnet pack) → restore/build |
| `**/*.csproj` (25개) | 와일드카드 버전(`8.*`, `3.*` …)을 **소스 최종 수정일(2026-06-18) 시점에 받아졌을 버전**으로 고정 (SkiaSharp만 3.119.2) |

## 고정한 패키지 버전

| 패키지 | 원래 | 고정 | 현재 최신 |
|---|---|---|---|
| EFCore.NamingConventions | `8.*` | 8.0.3 | 8.0.3 |
| IxMilia.Dxf | `0.*` | 0.8.4 | 0.8.4 |
| MessagePack | `3.*` | 3.1.7 | 3.1.11 |
| Microsoft.AspNetCore.Authentication.JwtBearer | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.AspNetCore.Components.Web | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.AspNetCore.Components.WebAssembly | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.AspNetCore.Components.WebAssembly.DevServer | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.AspNetCore.Http.Connections | `1.*` | 1.2.11 | 1.2.13 |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.AspNetCore.SignalR.Client | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.AspNetCore.SignalR.Core | `1.*` | 1.2.11 | 1.2.13 |
| Microsoft.Bcl.AsyncInterfaces | `8.*` | 8.0.0 | 8.0.0 |
| Microsoft.Data.SQLite.Core | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.EntityFrameworkCore | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.EntityFrameworkCore.Sqlite | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.EntityFrameworkCore.Tools | `8.*` | 8.0.28 | 8.0.31 |
| Microsoft.Extensions.Caching.Memory | `8.*` | 8.0.1 | 8.0.1 |
| Microsoft.Extensions.Configuration.Json | `8.*` | 8.0.1 | 8.0.1 |
| Microsoft.Extensions.Hosting | `8.*` | 8.0.1 | 8.0.1 |
| Microsoft.Extensions.Hosting.WindowsServices | `8.*` | 8.0.1 | 8.0.1 |
| Microsoft.Extensions.Http | `8.*` | 8.0.1 | 8.0.1 |
| Microsoft.Extensions.Logging.Console | `8.*` | 8.0.1 | 8.0.1 |
| Microsoft.IdentityModel.Tokens | `8.*` | 8.19.1 | 8.23.0 |
| Newtonsoft.Json | `13.*` | 13.0.4 | 13.0.4 |
| Npgsql | `8.*` | 8.0.9 | 8.0.9 |
| Npgsql.EntityFrameworkCore.PostgreSQL | `8.*` | 8.0.11 | 8.0.11 |
| Serilog.Extensions.Hosting | `8.*` | 8.0.0 | 8.0.0 |
| Serilog.Settings.Configuration | `8.*` | 8.0.4 | 8.0.4 |
| Serilog.Sinks.Async | `2.*` | 2.1.0 | 2.1.0 |
| Serilog.Sinks.Console | `6.*` | 6.1.1 | 6.1.1 |
| Serilog.Sinks.File | `6.*` | 6.0.0 | 6.0.0 |
| SkiaSharp | `3.*` | 3.119.2 | 3.119.4 |
| SkiaSharp.NativeAssets.WebAssembly | `3.*` | 3.119.2 | 3.119.4 |
| SkiaSharp.Views.Blazor | `3.*` | 3.119.2 | 3.119.4 |
| SkiaSharp.Views.WPF | `3.*` | 3.119.2 | 3.119.4 |
| Swashbuckle.AspNetCore | `6.*` | 6.9.0 | 6.9.0 |
| System.IO.Pipelines | `8.*` | 8.0.0 | 8.0.0 |
| System.IdentityModel.Tokens.Jwt | `8.*` | 8.19.1 | 8.23.0 |
| System.Management | `9.*` | 9.0.17 | 9.0.20 |
| System.Memory | `4.*` | 4.6.3 | 4.6.3 |
| Yarp.ReverseProxy | `2.*` | 2.3.0 | 2.3.0 |
| netDxf.netstandard | `3.*` | 3.0.1 | 3.0.1 |

- 기준일 2026-06-18: zip 안 소스 파일의 가장 최근 수정일. 원본 PC가 그 무렵 restore 했다면 `*`는 위 버전으로 풀렸을 것으로 추정.
- SkiaSharp는 3.119.4(2026-05-25 배포)가 net8.0-windows를 제공하지 않아 WPF에서 NU1701 경고 → 3.119.2로 고정.
- DxfBlazorViewer.Client는 원래부터 버전이 고정되어 있어 변경하지 않음.
- **정확히 같게** 하려면 원본 PC에서 `dotnet list CJ.SCADA.sln package --include-transitive > packages.txt` 결과(또는 각 프로젝트 `obj\project.assets.json`)를 받아 위 표와 비교.

## 검증 상태

csproj XML 문법만 확인함. 이 환경에서는 .NET SDK 다운로드가 차단되어 **실제 restore/build는 검증하지 못함** → `setup-dev.ps1` 실행 결과로 확인 필요.
