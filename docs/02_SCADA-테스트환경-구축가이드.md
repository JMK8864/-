# CJ.SCADA FTMS 테스트 환경 구축 가이드 (WSL Docker + nginx)

> 대상: Windows 개발 PC 1대에서 CJ.SCADA 솔루션(FTMS)을 빌드·실행·테스트
> 방식: **한 단계씩** 실행 → 확인 → 다음 단계. 여러 명령을 묶은 스크립트(.sh/.ps1)는 사용하지 않음
> 검증: 2026-10, Windows + VS2026 + .NET SDK 8.0.425 / 10.0.302 + WSL2(Ubuntu, 미러 모드) + Docker Engine 29.x 에서 0~13단계 실제 수행 완료
> 설정 파일: 저장소 `setup/` 폴더 (각 단계에 사용 파일 표시)

---

## 0. 전체 구성

```
 Windows (개발 PC)                                   WSL2 Ubuntu (미러 네트워크) + Docker Engine
 ┌──────────────────────────────────────┐           ┌──────────────────────────────────────────┐
 │ Visual Studio 2026 (F5 다중 시작)     │           │ ftms-nginx  (nginx, --network host)  :80 │
 │  ├ app-server.ftms   0.0.0.0:8121     │◄──────────┤   /api /chathub /health → 127.0.0.1:8121 │
 │  │   └ TCP 6004~6006 (도크 수신)      │◄──────────┤   그 외                 → 127.0.0.1:8122 │
 │  └ DxfBlazorViewer   0.0.0.0:8122     │           │                                          │
 │                                      │           │ ftms-db  (timescale/timescaledb:pg17)    │
 │ tcplinent.emul.console.ftms ─TCP 6004─┘           │   -p 5432:5432, 볼륨 ftms_db_data         │
 │ DBeaver ───────────── 127.0.0.1:5432 ────────────►│   authdb / ftmsdb / tapsuser            │
 │ 브라우저 ─────────── http://localhost/ ──────────►│                                          │
 └──────────────────────────────────────┘           └──────────────────────────────────────────┘
```

| 단계 | 내용 | 사용 파일 |
|---|---|---|
| 1 | Windows 필수 S/W 설치·확인 | — |
| 2 | 솔루션 빌드 (VS2026 호환 수정) | — |
| 3 | WSL2 / 네트워크 모드 확인 | `setup/wsl/.wslconfig` |
| 4 | WSL 에 Docker Engine 설치·확인 | — |
| 5 | Windows 포트 정리 (5432, 80) | — |
| 6 | DB 컨테이너(ftms-db) 실행 | — |
| 7 | 앱 계정·DB·확장 생성 (DBeaver) | `setup/db/01_*.sql`, `02_*.sql` |
| 8 | app-server 연결 설정 + 첫 실행(테이블 자동 생성) | `setup/app-config/app-server.appsettings.ConnectionStrings.json` |
| 9 | TimescaleDB 하이퍼테이블·집계 함수 | `setup/db/03_*.sql`, `04_verify.sql` |
| 10 | VS 다중 시작 + YARP 로 화면 확인 | — |
| 11 | 에뮬레이터로 데이터 송신 테스트 | `setup/db/04_verify.sql` |
| 12 | 리버스 프록시를 WSL Docker nginx 로 전환 | `setup/nginx/default.conf`, `setup/app-config/*.launchSettings.json` |
| 13 | WSL 자동 시작·유지 + 재부팅 테스트 | — |
| 14 | 매일 시작 순서 / 문제 해결 / 정리 | `setup/db/05_clear_test_data.sql` |

명령을 실행하는 곳은 각 블록 위에 **(PowerShell)**, **(관리자 PowerShell)**, **(WSL)**, **(DBeaver)** 로 표시합니다.

---

## 1단계. Windows 필수 S/W 설치·확인

| S/W | 용도 | 설치 |
|---|---|---|
| Visual Studio 2026 (또는 2022 17.8+) | 빌드·디버깅 | 워크로드 **ASP.NET 및 웹 개발**, **.NET 데스크톱 개발** |
| .NET 8 SDK (8.0.4xx) | 솔루션 대상 프레임워크 net8.0 | https://dotnet.microsoft.com/download/dotnet/8.0 → SDK x64 |
| wasm-tools 워크로드 | DxfBlazorViewer (`WasmBuildNative=true`, SkiaSharp WASM) | 아래 1-3 |
| Windows 10 SDK 10.0.19041 | CJ.FTMS.Viewer (`net8.0-windows10.0.19041`) | VS 설치 관리자 → 개별 구성 요소 |
| DBeaver Community | DB 접속·SQL 실행 | https://dbeaver.io/download |
| 브라우저 | 웹 UI | Chrome / Edge |

**1-1. SDK 확인 (PowerShell)**
```powershell
dotnet --list-sdks
```
✅ `8.0.4xx` 가 있어야 함 (10.0.x 가 같이 있어도 됨)

**1-2. 기본 SDK 확인 (PowerShell)**
```powershell
dotnet --version
```
→ 8.x 이면 1-3 에서 `wasm-tools`, 9.x/10.x 이면 `wasm-tools-net8` 사용

**1-3. wasm 워크로드 설치 (관리자 PowerShell)**
```powershell
dotnet workload install wasm-tools-net8
```
(1-2 결과가 8.x 이면 `dotnet workload install wasm-tools`)

**1-4. 확인 (PowerShell)**
```powershell
dotnet workload list
```
✅ `wasm-tools-net8` (또는 `wasm-tools`) 표시

> `winget` 이 없는 PC 가 많으므로 이 가이드는 웹 설치 파일을 기준으로 합니다.

---

## 2단계. 솔루션 빌드 (VS2026 호환 수정)

원본은 VS2022 에서 개발되었고 NuGet 버전에 와일드카드(`3.*`, `8.*`)가 많아, **지금 복원하면 새 버전이 받아져** 오류가 납니다. 아래 3가지를 수정합니다. (검증한 PC 에서 이 수정만으로 전체 빌드 성공)

**2-1. CJ.FTMS.Viewer — SkiaSharp.Views.WPF 버전 고정**
- 증상: `NU1701 ... 'SkiaSharp.Views.WPF 3.119.4' ... .NETFramework 사용하여 복원`
- 원인: 3.119.4 는 net8.0-windows 빌드가 없음
- 조치: VS → CJ.FTMS.Viewer → NuGet 패키지 관리 → `SkiaSharp.Views.WPF` 를 **3.119.2** 로 설치(버전 고정)

**2-2. CJ.FTMS.Viewer — `Json` 이름 오류**
- 증상: `CS0103 'Json' 이름이 현재 컨텍스트에 없습니다` (`Model/partial/JsonFile.cs`)
- 원인: `Json` 헬퍼 클래스가 이 프로젝트에 포함되어 있지 않음
- 조치: 해당 줄을 `System.Text.Json.JsonSerializer.Serialize(...)` / `JsonSerializer.Deserialize<T>(...)` 로 변경

**2-3. DxfBlazorViewer — MessagePack 취약성 경고**
- 증상: `MessagePack 3.1.4 ... GHSA-2f33-pr97-265q`
- 조치: NuGet 패키지 관리 → `MessagePack` 을 **3.1.7 이상**으로 업데이트

**2-4. 그대로 두어도 되는 경고**
- `SKCanvas.DrawText(string, float, float, SKPaint) 사용되지 않음` (CS0618) — SkiaSharp 3.x API 변경 경고. VS2022 에서도 같은 SkiaSharp 버전이면 동일하게 발생. 빌드·실행에 영향 없음

**2-5. 빌드 (VS)**
- 빌드 → 솔루션 다시 빌드
- ✅ 오류 0. (`Test\...` 프로젝트가 "언로드됨"으로 보이는 것은 zip 에 폴더가 없어서이며 무시)

> **새 PC 에서 복원이 실패할 때**: DxfBlazorViewer 는 `Assembly.ChatHub.Shared / Assembly.JwtTokenGenerator / Assembly.LoginManager 1.0.0.1` 을 **NuGet 패키지로** 참조합니다. 기존 PC 는 사용자 NuGet 캐시(`%UserProfile%\.nuget\packages`)에 있어 복원되었을 수 있습니다. 없으면 세 프로젝트를 Release 빌드(`GeneratePackageOnBuild=true`) → `bin\Release\*.nupkg` 를 한 폴더에 모아 VS → 도구 → 옵션 → NuGet 패키지 관리자 → 패키지 소스에 추가.

---

## 3단계. WSL2 / 네트워크 모드 확인

**3-1. WSL 상태 (PowerShell)**
```powershell
wsl -l -v
```
✅ `Ubuntu   Running(또는 Stopped)   2` — VERSION 이 **2** 여야 함
- WSL 이 없으면 (관리자 PowerShell) `wsl --install -d Ubuntu` → 재부팅 → Ubuntu 사용자 생성

**3-2. 네트워크 모드 (WSL)**
```bash
wslinfo --networking-mode
```
✅ `mirrored` — 이 가이드의 기본 전제 (WSL 의 127.0.0.1 = Windows 의 127.0.0.1)

다른 확인 방법:
```bash
ip route show default
```
- `default via 192.168.x.1 dev eth0 ... metric NN` (사내/공유기 주소) → 미러 모드
- `default via 172.x.x.1 dev eth0` → NAT 모드

**3-3. 미러 모드가 아니면** `setup/wsl/.wslconfig` 를 `%UserProfile%\.wslconfig` 로 복사(이미 있으면 `[wsl2]` 아래 두 줄만 추가) 후 (PowerShell)
```powershell
wsl --shutdown
```
→ WSL 터미널 다시 열고 3-2 재확인.
(미러 모드를 쓸 수 없는 환경은 [부록 A. NAT 모드](#부록-a-nat-모드에서-nginx-구성) 참고)

---

## 4단계. WSL 에 Docker Engine 설치·확인

Docker Desktop 이 아니라 **WSL Ubuntu 안에 Docker Engine** 을 직접 설치합니다.

**4-1. 이미 설치되어 있는지 (WSL)**
```bash
docker version
```
✅ `Client` 와 `Server` 가 모두 표시되면 4-4 로. `command not found` 면 4-2 부터.

**4-2. 설치 (WSL, 한 줄씩)** — Docker 공식 apt 저장소 방식
```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" | sudo tee /etc/apt/sources.list.d/docker.list
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
```

**4-3. sudo 없이 docker 사용 (WSL)**
```bash
sudo usermod -aG docker $USER
```
→ WSL 터미널을 닫았다가 다시 열기

**4-4. 자동 시작 확인 (WSL)**
```bash
systemctl is-enabled docker
```
✅ `enabled` (아니면 `sudo systemctl enable --now docker`)
- `System has not been booted with systemd` 오류면 `/etc/wsl.conf` 에 아래를 넣고 (PowerShell) `wsl --shutdown` 후 재시도
  ```ini
  [boot]
  systemd=true
  ```

**4-5. 동작 확인 (WSL)**
```bash
docker run --rm hello-world
```
✅ `Hello from Docker!`

---

## 5단계. Windows 포트 정리 (5432, 80)

미러 모드에서는 **WSL 과 Windows 가 포트를 공유**하므로, Windows 에서 같은 포트를 쓰는 프로그램이 있으면 충돌합니다.

**5-1. 5432 (PowerShell)**
```powershell
netstat -ano | findstr "LISTENING" | findstr ":5432"
```
- 비어 있으면 5-3 으로.
- 결과가 있으면 PID 확인:
  ```powershell
  Get-Process -Id <PID>
  ```
  `postgres` 이면 Windows 에 설치된 PostgreSQL 입니다 → 5-2

**5-2. Windows PostgreSQL 서비스 중지·사용 안 함 (관리자 PowerShell)**
```powershell
Get-Service postgresql*
```
```powershell
Stop-Service postgresql-x64-11
```
```powershell
Set-Service postgresql-x64-11 -StartupType Disabled
```
(서비스 이름은 `Get-Service` 결과에 맞게) → 5-1 재실행해서 비었는지 확인

**5-3. 80 (PowerShell)**
```powershell
netstat -ano | findstr "LISTENING" | findstr ":80 "
```
- 비어 있음 → 12단계 nginx 를 80 으로 사용
- `PID 4`(System, http.sys) → 검증 PC 와 같은 상태. 실제 웹 서비스(IIS 등)가 돌지 않으면 **nginx 가 80 을 정상 사용**함(검증 완료). IIS 를 쓰고 있다면 12단계에서 nginx 를 8080 으로 사용
- 다른 PID → `Get-Process -Id <PID>` 로 확인

**5-4. 이전에 만든 컨테이너 정리 (WSL)**
```bash
docker ps -a
```
같은 포트(5432, 80)를 쓰는 예전 컨테이너가 있으면 이름을 확인하고 하나씩 삭제:
```bash
docker rm -f <컨테이너이름>
```

---

## 6단계. DB 컨테이너(ftms-db) 실행

**6-1. 실행 (WSL)** — `관리자비밀번호` 만 바꿔서 한 줄로 실행
```bash
docker run -d --name ftms-db -p 5432:5432 -e POSTGRES_PASSWORD='관리자비밀번호' -e TZ=Asia/Seoul -v ftms_db_data:/var/lib/postgresql/data --restart unless-stopped timescale/timescaledb:latest-pg17
```
| 옵션 | 의미 |
|---|---|
| `-p 5432:5432` | Windows 127.0.0.1:5432 로 접속 가능 |
| `POSTGRES_PASSWORD` | `postgres` 관리자 비밀번호 (**볼륨을 처음 만들 때만 적용**) |
| `-v ftms_db_data:...` | 데이터를 볼륨에 보관 → 컨테이너를 지워도 데이터 유지 |
| `--restart unless-stopped` | Docker 가 켜지면 자동 시작 |

**6-2. 확인 (WSL)**
```bash
docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
```
✅ `ftms-db   Up ...   0.0.0.0:5432->5432/tcp`

**6-3. Windows 에서 접속 확인 (PowerShell)**
```powershell
Test-NetConnection 127.0.0.1 -Port 5432
```
✅ `TcpTestSucceeded : True`
> `localhost` 로 테스트하면 IPv6 `::1` 경고가 나올 수 있습니다(WSL 포트 전달은 IPv4). 이 가이드는 DB 주소를 모두 **127.0.0.1** 로 씁니다.

---

## 7단계. 앱 계정·DB·확장 생성 (DBeaver)

**7-1. 관리자 연결 만들기 (DBeaver)**
1. 데이터베이스 → 새 데이터베이스 연결 → **PostgreSQL**
2. Main: Host **`127.0.0.1`**, Port `5432`, Database `postgres`, Username `postgres`, Password = 6-1 의 관리자 비밀번호
3. PostgreSQL 탭: ☑ **Show all databases**
4. Test Connection (처음이면 드라이버 Download) → Connected → 완료

**7-2. 계정·DB 생성 — `setup/db/01_create_user_and_db.sql`**
1. 7-1 연결 선택 → 파일 → 파일 열기 → `01_create_user_and_db.sql`
2. 툴바 트랜잭션 모드가 **Auto-Commit** 인지 확인
3. `'앱비밀번호'` 를 실제 값으로 변경 (영문/숫자/_ 권장 — 8단계에서 그대로 사용)
4. **한 줄씩** 커서 → `Ctrl+Enter` (CREATE DATABASE 는 트랜잭션 안에서 실행 불가)
5. ✅ 마지막 확인 쿼리: `authdb | tapsuser`, `ftmsdb | tapsuser`

**7-3. 확장 설치 — `setup/db/02_create_extensions.sql`**
1. Navigator 에서 연결 새로 고침(F5) → **ftmsdb** 선택 → 파일 열기 → `02_create_extensions.sql`
2. 편집기 상단 활성 DB 가 **ftmsdb** 인지 확인 (postgres 이면 클릭해서 변경)
3. `Alt+X`
4. ✅ `current_database = ftmsdb`, 목록에 `timescaledb`, `pgcrypto`

**7-4. 앱 계정 연결 만들기 (DBeaver)** — 9·11단계에서 사용
1. 새 연결 → PostgreSQL → Host `127.0.0.1`, Database **`ftmsdb`**, Username **`tapsuser`**, Password = 앱 비밀번호
2. ✅ Test Connection → Connected
3. SQL 편집기에서 권한 테스트:
   ```sql
   SELECT current_user, current_database();
   CREATE TABLE _perm_test(id int);
   DROP TABLE _perm_test;
   ```
   ✅ `tapsuser | ftmsdb`, 오류 없음

| 오류 | 해결 |
|---|---|
| `password authentication failed for user "postgres"` | 6-1 비밀번호와 다름. 볼륨이 예전부터 있었다면 처음 만들 때의 비밀번호가 유지됨 |
| `CREATE DATABASE cannot run inside a transaction block` | Auto-Commit 켜고 한 줄씩 실행 |
| `role "tapsuser" already exists` | 이미 생성됨. 비밀번호만 바꾸려면 `ALTER USER tapsuser WITH PASSWORD '새값';` |
| `permission denied to create extension` | tapsuser 로 실행함 → postgres 연결로 실행 |

---

## 8단계. app-server 연결 설정 + 첫 실행 (테이블 자동 생성)

app-server 는 기동 시 `EnsureCreated()` 로 authdb·ftmsdb 테이블을 만들고, 역할 7개와 기본 계정 7개(아이디=비밀번호=역할명 소문자, 예: `admin/admin`)를 넣습니다.
⚠ **app-server 는 DB 에 연결되어야 8121 포트를 엽니다** (DB 초기화가 `app.Run()` 보다 먼저 실행). ftms-db 가 꺼져 있으면 서버가 뜨지 않습니다.

**8-1. 연결 문자열** — `setup/app-config/app-server.appsettings.ConnectionStrings.json`
`servers\ftms\app-server.ftms\appsettings.json` 의 `"ConnectionStrings"` 부분을 교체:
```json
"ConnectionStrings": {
  "DefaultConnection1": "Host=127.0.0.1;Port=5432;Database=authdb;Username=tapsuser;Password=앱비밀번호",
  "DefaultConnection2": "Host=127.0.0.1;Port=5432;Database=ftmsdb;Username=tapsuser;Password=앱비밀번호"
},
```

**8-2. 빌드 (VS)**: app-server.ftms 우클릭 → 빌드

**8-3. 도면 파일 복사 (탐색기)**
- 원본: `CJ.SCADA.Solution\servers\dxf\FTMS.v1.dxf`
- 대상: `CJ.SCADA.Solution\servers\ftms\app-server.ftms\bin\Debug\net8.0\assets\FTMS.v1.dxf` (`assets` 폴더는 직접 생성)

**8-4. 실행 (VS)**: app-server.ftms 우클릭 → 시작 프로젝트로 설정 → 프로필 **http** → F5

**8-5. 확인**

로그 파일 위치: `servers\ftms\app-server.ftms\bin\Debug\logs\app-server.ftms-YYYYMMDD.txt` (실행 폴더의 한 단계 위)
(PowerShell, `app-server.ftms` 폴더에서)
```powershell
Select-String -Path .\bin\Debug\logs\app-server.ftms-*.txt -Pattern "Now listening|스키마가 성공적으로|ERR\]" | Select-Object -Last 10
```
✅ `데이터베이스1/2 스키마가 성공적으로 확인/생성되었습니다.`, `Now listening on: http://localhost:8121`, `[ERR]` 없음

(PowerShell)
```powershell
netstat -ano | findstr "LISTENING" | findstr ":8121"
```
✅ 8121 LISTENING

브라우저: `http://localhost:8121/api/status` → JSON, `http://localhost:8121/swagger` → Swagger

(DBeaver, tapsuser@ftmsdb)
```sql
SELECT table_name FROM information_schema.tables WHERE table_schema='public' ORDER BY 1;
```
✅ `process_counts`, `ftms_event_log`, `dock_message_events` 등

(DBeaver, tapsuser@authdb — 연결의 DB 만 authdb 로 바꾼 연결)
```sql
SELECT user_name FROM asp_net_users;
```
✅ `admin, manager, operator, engineer, worker, auditor, viewer`

| 증상 | 해결 |
|---|---|
| `28P01 password authentication failed` | 8-1 비밀번호 ≠ 7-2 비밀번호 |
| `Failed to connect to 127.0.0.1:5432` / 8121 이 안 열림 | ftms-db 꺼짐 → (WSL) `docker ps`, 꺼져 있으면 `docker start ftms-db` |
| 8121 사용 중 | 이전 실행 잔존 → 작업 관리자에서 `app-server.ftms.exe` 종료 |

---

## 9단계. TimescaleDB 하이퍼테이블·집계 함수

EF 가 만든 테이블은 일반 테이블입니다. `DataCollectorService` 가 1초마다 호출하는 `process_counts_aggregate_all()`, 통계 API 가 쓰는 `get_dock_process_statistics_lastest()` 와 집계 테이블은 **앱이 만들지 않으므로** 이 단계가 필요합니다.

**9-1.** VS 디버깅 중지 (`Shift+F5`)

**9-2. DBeaver 설정** (함수 본문이 빈 줄에서 잘리지 않게)
창 → 설정 → 편집기 → SQL 편집기 → SQL 처리 → **"빈 줄을 문장 구분자로 사용" = Never** → 적용

**9-3. 실행 — `setup/db/03_ftmsdb_timescale_setup.sql`**
1. 연결 **tapsuser / ftmsdb** 선택 → 파일 열기
2. 활성 DB = ftmsdb 확인 → `Alt+X` (전체 실행)
3. `NOTICE: ... already a hypertable, skipping` 은 정상

**9-4. 앱 재실행**: VS F5 (app-server 가 `collected_payload`, `rawdata_table`, `process_collected_payload_v4()` 를 추가 생성)

**9-5. 확인 — `setup/db/04_verify.sql` 의 [9단계] 쿼리**
✅ 하이퍼테이블 **9개**:
`collected_payload, dock_message_events, ftms_event_log, process_count_per_day_accum, process_count_per_day_kst, process_count_per_hour_accum, process_count_per_minute_accum, process_counts, rawdata_table`
✅ 함수 **3개**: `get_dock_process_statistics_lastest, process_collected_payload_v4, process_counts_aggregate_all`
✅ `SELECT * FROM process_counts_aggregate_all(10);` 오류 없음

(PowerShell, app-server.ftms 폴더) 재실행 후 새 오류 없는지:
```powershell
Select-String -Path .\bin\Debug\logs\app-server.ftms-*.txt -Pattern "ERR\]" | Select-Object -Last 10
```

| 오류 | 해결 |
|---|---|
| `unterminated dollar-quoted string` | 9-2 설정 누락 |
| `relation "process_counts" does not exist` | ftmsdb 가 아닌 DB 에서 실행 / 8단계 미완료 |
| `function create_hypertable does not exist` | 7-3 확장 미설치 |

---

## 10단계. VS 다중 시작 + YARP 로 화면 확인

DxfBlazorViewer 는 API 를 `/api/...`, `/chathub` **상대 주소**로 호출하므로 화면과 API 가 **같은 주소**여야 합니다. 먼저 솔루션의 YARP(reverse-proxy.ftms, 8000)로 확인한 뒤, 12단계에서 nginx 로 바꿉니다.
```
http://localhost:8000 (YARP) ─ /api,/chathub,/health → 8121 (app-server)
                             └ 그 외                → 8122 (DxfBlazorViewer)
```

**10-1. 다중 시작 (VS)**: 솔루션 우클릭 → 시작 프로젝트 구성 → **여러 시작 프로젝트**
| 프로젝트 | 작업 | 순서 |
|---|---|---|
| app-server.ftms | 시작 | 1 |
| DxfBlazorViewer.Client | 시작 | 2 |
| reverse-proxy.ftms | 시작 | 3 |
나머지는 없음. (VS2026: 시작 프로필 이름으로 저장 가능, 예 `FTMS-YARP`)

**10-2.** F5 → 브라우저가 `http://localhost:8000` 으로 열림 (첫 로딩 수 초)

**10-3. 확인**
(PowerShell)
```powershell
netstat -ano | findstr "LISTENING" | findstr ":8000 :8121 :8122"
```
✅ 3개 포트
- `http://localhost:8000/api/status` → JSON
- `http://localhost:8000` → **admin / admin** 로그인 → 도면 표시
- F12 → Console 빨간 오류 없음, Network 에서 `chathub/negotiate` 200

---

## 11단계. 에뮬레이터로 데이터 송신 테스트

**`tcplinent.emul.console.ftms`(콘솔) 사용** — 실행 즉시 127.0.0.1:6004 로 **60개 도크** 접속, 하트비트 1초, 처리수량 초당 100건 자동 송신.

> WPF 버전(`tcpclient.emul.ftms`)은 [전체 전송] 버튼을 누르기 전에는 하트비트를 보내지 않습니다. 서버는 `TcpServerFinal:TimeoutSeconds = 10` 초 무수신 시 연결을 끊으므로 **접속되지 않는 것처럼** 보입니다. WPF 를 쓰려면 실행 후 10초 안에 [전체 전송] 을 누르세요.

**11-1. 6004 확인 (PowerShell)**
```powershell
netstat -ano | findstr "LISTENING" | findstr ":6004"
```
✅ `0.0.0.0:6004`

**11-2. 실행 (VS)**: 10단계 실행 상태 유지 → `servers\ftms\tcplinent.emul.console.ftms` 우클릭 → 디버그 → **새 인스턴스 시작**

**11-3. 확인 (30초~1분 후)**
(PowerShell) 접속 수:
```powershell
(netstat -ano | findstr ":6004" | findstr "ESTABLISHED").Count
```
✅ 약 120 (60 연결 × 양쪽)

(DBeaver, tapsuser@ftmsdb) `setup/db/04_verify.sql` 의 [11단계] 쿼리
✅ `process_counts` count 증가, `done` 증가 / `pending` 작게 유지, 분 집계 행 생성

브라우저: 도크별 처리수량 숫자가 갱신됨 (SignalR)

**11-4.** 확인 후 콘솔 에뮬레이터 `Ctrl+C` 또는 창 닫기 (초당 100건 계속 적재됨)

---

## 12단계. 리버스 프록시를 WSL Docker nginx 로 전환

미러 모드에서는 nginx 컨테이너를 `--network host` 로 실행하면 컨테이너 안의 `127.0.0.1` 이 Windows 의 `127.0.0.1` 입니다. → 게이트웨이 IP·`--add-host`·방화벽 규칙이 필요 없습니다.

**12-1. YARP 제외 (VS)**: 디버깅 중지 → 시작 프로젝트 구성에서 `reverse-proxy.ftms` = **없음** (app-server, DxfBlazorViewer 는 시작 유지)

**12-2. 앱을 0.0.0.0 으로** — `setup/app-config/*.launchSettings.json`
- `servers\ftms\app-server.ftms\Properties\launchSettings.json` → http 프로필 `"applicationUrl": "http://0.0.0.0:8121"`
- `servers\ftms\DxfBlazorViewer\Properties\launchSettings.json` → http 프로필 `"applicationUrl": "http://0.0.0.0:8122"`
- (미러 모드에서는 localhost 바인딩도 동작하지만, NAT 모드·다른 PC 접속과 같은 설정으로 맞추기 위해 0.0.0.0 사용)
- F5 → Windows 방화벽 창이 뜨면 **허용**

(PowerShell)
```powershell
netstat -ano | findstr "LISTENING" | findstr ":8121 :8122"
```
✅ `0.0.0.0:8121`, `0.0.0.0:8122` **둘 다**. 8121 이 없으면 → ftms-db 가 꺼져 있는지 먼저 확인 (8단계 ⚠)

**12-3. WSL → Windows 앱 연결 확인 (WSL)**
```bash
curl -s -m 5 http://127.0.0.1:8121/api/status
```
✅ JSON

**12-4. nginx 설정 파일 (WSL)**
```bash
mkdir -p ~/ftms-nginx
```
```bash
nano ~/ftms-nginx/default.conf
```
`setup/nginx/default.conf` 내용 전체를 붙여 넣기 → `Ctrl+O` → `Enter` → `Ctrl+X`
(또는 Windows 에 받은 파일을 복사: `cp /mnt/d/경로/setup/nginx/default.conf ~/ftms-nginx/default.conf`)

확인:
```bash
grep -n "server 127\|listen" ~/ftms-nginx/default.conf
```
✅ `127.0.0.1:8121`, `127.0.0.1:8122`, `listen 80 default_server;`
(5-3 에서 IIS 가 80 을 쓰고 있었다면 `listen 8080` 으로 변경)

**12-5. nginx 컨테이너 실행 (WSL)**
같은 이름이 있는지 먼저:
```bash
docker ps -a --filter name=ftms-nginx
```
(있으면 `docker rm -f ftms-nginx`)
```bash
docker run -d --name ftms-nginx --network host -v ~/ftms-nginx/default.conf:/etc/nginx/conf.d/default.conf:ro --restart unless-stopped nginx:latest
```
- 이미 받아 둔 `nginx:latest` 사용. 버전을 고정하려면 `nginx:1.27-alpine` 등 태그 지정 (설정 경로 동일)
- `--network host` 이므로 `-p` 옵션 없음, `docker ps` 의 PORTS 칸이 비어 있는 것이 정상

**12-6. 확인 (WSL)**
```bash
docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Status}}"
```
```bash
docker exec ftms-nginx nginx -t
```
```bash
curl -s -m 5 http://127.0.0.1/api/status
```
✅ `ftms-nginx Up`, `syntax is ok / test is successful`, nginx 를 거친 JSON

**12-7. 최종 확인 (Windows 브라우저)**
| 주소 | 기대 결과 |
|---|---|
| `http://localhost/api/status` | JSON |
| `http://localhost/` | 화면, admin/admin 로그인, 도면 |
| F12 → Network → `chathub?id=...` | **101 Switching Protocols** |
| 11단계 에뮬레이터 실행 | 수량 갱신 |

(PowerShell) 응답 서버가 nginx 인지:
```powershell
curl.exe -sI http://localhost/ | findstr /i "Server"
```
✅ `Server: nginx/...` (`Microsoft-HTTPAPI/2.0` 이면 http.sys 가 응답한 것 → 5-3 참고, nginx 를 8080 으로)

**12-8. 설정을 바꿨을 때** — nginx 는 **시작할 때만** 설정을 읽습니다 (파일만 고치면 반영 안 됨)
```bash
docker exec ftms-nginx nginx -t
```
```bash
docker restart ftms-nginx
```

| 증상 | 해결 |
|---|---|
| 502 Bad Gateway | 8121/8122 미실행 → 12-2 확인. `/` 만 502 면 DxfBlazorViewer 미실행 |
| 컨테이너가 바로 종료 | `docker logs ftms-nginx` → `bind() to 0.0.0.0:80 failed` 면 80 사용 중 → 8080 |
| 화면은 되는데 수량이 안 바뀜 | chathub 가 101 이 아님 → `/chathub` location 확인 |

---

## 13단계. WSL 자동 시작·유지 + 재부팅 테스트

Docker Engine 은 **WSL VM 안**에서 돌기 때문에 **WSL 이 꺼지면 ftms-db·ftms-nginx 도 멈춥니다.**
- WSL 은 Windows 부팅 시 자동으로 켜지지 않음
- WSL 터미널을 모두 닫으면 잠시 뒤 자동 종료됨
- WSL 이 다시 켜지면 Docker 자동 시작 → 컨테이너는 `--restart unless-stopped` 로 자동 복구

**방법 1 (간단)**: 작업할 때 WSL 터미널을 하나 열어 두기 (닫지 말고 최소화)

**방법 2 (권장)**: 로그인 시 WSL 을 켜고 유지하는 작업 스케줄러 등록 (관리자 PowerShell, 한 줄씩)
```powershell
$a = New-ScheduledTaskAction -Execute "C:\Windows\System32\wsl.exe" -Argument "-d Ubuntu --exec sleep infinity"
```
```powershell
$t = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
```
```powershell
$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
```
```powershell
Register-ScheduledTask -TaskName "WSL-KeepAlive" -Action $a -Trigger $t -Settings $s
```
- 배포판 이름은 `wsl -l -v` 결과에 맞게 (`Ubuntu`)
- `-ExecutionTimeLimit 0` : 기본 72시간 강제 종료 해제
- 해제: `Unregister-ScheduledTask -TaskName "WSL-KeepAlive" -Confirm:$false`

즉시 시험 (관리자 PowerShell):
```powershell
Start-ScheduledTask -TaskName "WSL-KeepAlive"
```
```powershell
wsl -l -v
```
✅ Ubuntu `Running` — WSL 터미널을 모두 닫고 2~3분 뒤에도 `Running`

**재부팅 테스트**: 재부팅 → 로그인 → (WSL 터미널 열지 않고) PowerShell
```powershell
Test-NetConnection 127.0.0.1 -Port 5432
```
✅ `True` → VS F5 → `http://localhost/` 정상 (검증 PC 에서 재부팅 후 80 정상 동작 확인)

---

## 14단계. 매일 시작 순서 / 문제 해결 / 정리

### 14-1. 매일 시작 순서
1. (WSL-KeepAlive 미사용 시) WSL 터미널 열기
2. (WSL) `docker ps --format "table {{.Names}}\t{{.Status}}"` → **ftms-db, ftms-nginx 모두 Up**
3. (VS) 다중 시작 F5 — app-server.ftms + DxfBlazorViewer.Client
4. 브라우저 `http://localhost/` → admin/admin
5. (테스트 시) tcplinent.emul.console.ftms → 디버그 → 새 인스턴스 시작

### 14-2. 문제 해결 빠른 표
| 증상 | 확인 |
|---|---|
| app-server 가 8121 을 안 엶 | WSL 켜짐? ftms-db Up? (DB 연결 전에는 포트를 열지 않음) |
| `http://localhost/` 502 | (PowerShell) `netstat -ano \| findstr ":8121 :8122"` |
| nginx 설정 변경이 반영 안 됨 | `docker exec ftms-nginx nginx -t` → `docker restart ftms-nginx` |
| nginx 로그 | (WSL) `docker logs --tail 20 ftms-nginx` |
| DB 로그 | (WSL) `docker logs --tail 20 ftms-db` |
| app-server 오류 | (PowerShell, app-server.ftms 폴더) `Select-String -Path .\bin\Debug\logs\app-server.ftms-*.txt -Pattern "ERR\]" \| Select-Object -Last 10` |
| 로그의 `An error occurred using the connection to database` + `Failed to delete old events` | 그 시각에 DB(WSL)가 꺼져 있었음 → 13단계 |

### 14-3. 테스트 데이터 정리 — `setup/db/05_clear_test_data.sql`
app-server·에뮬레이터 중지 → DBeaver tapsuser@ftmsdb → `Alt+X` (테스트 환경 전용)

### 14-4. 컨테이너·데이터 완전 삭제 (처음부터 다시 할 때, WSL)
```bash
docker rm -f ftms-nginx
```
```bash
docker rm -f ftms-db
```
```bash
docker volume rm ftms_db_data
```
⚠ 마지막 명령은 DB 데이터를 영구 삭제 → 6단계부터 다시

---

## 부록 A. NAT 모드에서 nginx 구성

`wslinfo --networking-mode` 가 `nat` 이고 미러 모드로 바꿀 수 없는 경우.

1. (WSL) Windows 호스트 IP 확인 — `via` 뒤의 172.x.x.1 값 (= `<WINIP>`)
   ```bash
   ip route show default
   ```
2. (WSL) 연결 테스트
   ```bash
   curl -s -m 5 http://<WINIP>:8121/api/status
   ```
3. 시간 초과면 (관리자 PowerShell) 방화벽 허용
   ```powershell
   New-NetFirewallRule -DisplayName "FTMS from WSL 8121-8122" -Direction Inbound -Protocol TCP -LocalPort 8121,8122 -RemoteAddress LocalSubnet -Action Allow -Profile Any
   ```
4. `setup/nginx/default.nat.conf` 를 `~/ftms-nginx/default.conf` 로 복사 (upstream 이 `winhost:8121/8122`)
5. (WSL)
   ```bash
   docker run -d --name ftms-nginx -p 80:80 --add-host winhost:<WINIP> -v ~/ftms-nginx/default.conf:/etc/nginx/conf.d/default.conf:ro --restart unless-stopped nginx:latest
   ```
6. (WSL) nginx 를 거쳐 확인: `curl -s -m 5 http://127.0.0.1/api/status` → JSON
- ⚠ NAT 모드의 게이트웨이 IP 는 WSL 재시작 시 바뀔 수 있음 → 바뀌면 `docker rm -f ftms-nginx` 후 5번 재실행

## 부록 B. 다른 PC 에서 접속

미러 모드에서는 WSL 이 Windows IP 를 공유하므로 `http://<이 PC IP>/` 로 접속 가능합니다. Windows 방화벽 인바운드 허용이 필요합니다 (관리자 PowerShell):
```powershell
New-NetFirewallRule -DisplayName "FTMS nginx 80" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow -Profile Private,Domain
```
현장 도크 PC(또는 다른 PC 의 에뮬레이터)에서 TCP 로 보내려면 6004~6006 도 같은 방식으로 허용합니다.

## 부록 C. 포트·계정 요약

| 항목 | 값 |
|---|---|
| DB | 127.0.0.1:5432, 관리자 `postgres`, 앱 `tapsuser`, DB `authdb` / `ftmsdb` |
| app-server | 8121 (REST·SignalR), TCP 6004·6005·6006 (도크) |
| DxfBlazorViewer (개발 서버) | 8122 |
| YARP (10단계 확인용) | 8000 |
| nginx | 80 (IIS 사용 PC 는 8080) |
| 웹 로그인 | admin/admin 외 역할별 6개 (아이디=비밀번호) — **테스트 전용, 운영 전 삭제** |
| 로그 | `servers\ftms\app-server.ftms\bin\Debug\logs\` |
