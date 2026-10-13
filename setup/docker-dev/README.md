# FTMS 로컬 실행·테스트 가이드 (Windows + VS2026 + WSL2 Docker)

빌드가 끝난 솔루션을 **내 PC에서 실행·테스트**하기 위한 절차입니다.
DB(TimescaleDB)와 리버스 프록시(nginx)는 Docker 로, 앱(app-server, 웹 화면)은 VS2026 에서 실행합니다.

```
 브라우저 http://localhost ──► [Docker] ftms-nginx :80
                                   ├─ /api, /chathub, /health ─► [Windows/VS] app-server.ftms   :8121
                                   └─ 그 외(화면)              ─► [Windows/VS] DxfBlazorViewer  :8122
 [Windows] tcpclient.emul ──TCP 6004──► app-server.ftms
 app-server.ftms ──localhost:5432──► [Docker] ftms-db (PostgreSQL 17 + TimescaleDB)
```

## 0. 준비물 한눈에 보기

| # | 항목 | 필수 | 설치 확인 명령 (PowerShell) | 정상 결과 |
|---|---|---|---|---|
| 1 | WSL2 | ✅ | `wsl -l -v` | 배포판 VERSION = 2 |
| 2 | Docker Desktop (WSL2 엔진) | ✅ | `docker version` / `docker compose version` | Server 버전 표시 |
| 3 | .NET SDK 8.0.4xx | ✅ (설치됨) | `dotnet --list-sdks` | 8.0.425 |
| 4 | wasm-tools 워크로드 | ✅ (빌드됨) | `dotnet workload list` | wasm-tools |
| 5 | 이 키트(docker-dev) | ✅ | — | — |
| 6 | 도면 파일 `FTMS.v1.dxf` | ✅ | 솔루션 `.dxf\FTMS.v1.dxf` | 파일 존재 |
| 7 | psql / pgAdmin | 선택 | — | 컨테이너 안 psql 로 대체 가능 |

전체 점검: 키트 폴더에서 `powershell -ExecutionPolicy Bypass -File .\check-env.ps1` (각 단계마다 다시 실행해 [OK] 확인)

## 1. WSL2

```powershell
wsl --install          # 관리자 PowerShell, 처음 한 번 → 재부팅
wsl --update
wsl --status           # 기본 버전: 2
wsl -l -v              # 설치된 배포판과 VERSION 확인
```
BIOS 가상화(VT-x/AMD-V)가 꺼져 있으면 오류 → BIOS 에서 켜기. `작업 관리자 → 성능 → CPU → 가상화: 사용` 확인.

## 2. Docker Desktop

1. https://www.docker.com/products/docker-desktop 에서 설치 (설치 시 "Use WSL 2 instead of Hyper-V" 체크)
2. 실행 → Settings → General → **Use the WSL 2 based engine** ✔
3. 확인
   ```powershell
   docker version                  # Client / Server 둘 다 표시되어야 함
   docker compose version
   docker run --rm hello-world     # "Hello from Docker!" 출력
   ```
> Docker Desktop 은 직원 250명 이상 또는 매출 1천만 달러 이상 기업은 유료 구독 대상입니다. 회사 정책을 확인하세요. (대안: 부록 A, WSL 안에 Docker Engine 직접 설치)

## 3. 키트 준비

1. `docker-dev.zip` 을 원하는 폴더(예: `D:\ftms-docker`)에 압축 해제
2. `.env` 수정
   ```ini
   POSTGRES_PASSWORD=관리자비밀번호
   FTMS_DB_PASSWORD=앱비밀번호          # app-server 연결 문자열에 동일하게 입력
   FTMS_DB_PORT=5432                    # Windows 에 PostgreSQL 이 이미 있으면 5433
   FTMS_HTTP_PORT=80                    # 80 사용 중이면 8080
   ```
3. 포트 확인: `netstat -ano | findstr ":80 :5432 :8121 :8122 :6004"` → 사용 중이면 `.env` 변경 또는 해당 프로그램 중지
4. `db\init\10-ftms-init.sh` 는 **LF 줄바꿈**이어야 함 (편집기로 열어 저장하면 CRLF 로 바뀔 수 있음 → VS Code 우하단 "LF" 확인)

## 4. DB 컨테이너 시작 (계정·DB·확장 자동 생성)

```powershell
cd D:\ftms-docker
docker compose up -d ftms-db
docker compose ps                      # ftms-db  ... (healthy)
docker logs ftms-db | Select-String ftms-init     # [ftms-init] done
```
최초 기동 시 자동으로: `tapsuser` 계정(비밀번호 = FTMS_DB_PASSWORD), `authdb`, `ftmsdb`, `timescaledb`·`pgcrypto` 확장.

확인
```powershell
docker exec ftms-db psql -U postgres -c "\du"                          # tapsuser
docker exec ftms-db psql -U postgres -c "\l"                           # authdb, ftmsdb (Owner tapsuser)
docker exec ftms-db psql -U postgres -d ftmsdb -c "\dx"                # timescaledb, pgcrypto
Test-NetConnection localhost -Port 5432                                # TcpTestSucceeded : True
```
> init 스크립트는 **데이터 볼륨이 비어 있을 때 한 번만** 실행됩니다. 처음부터 다시 하려면 `docker compose down -v` (DB 데이터 삭제!).

## 5. app-server 설정 → 1회 실행 (테이블 자동 생성)

### 5-1. `servers\ftms\app-server.ftms\appsettings.json`
```json
"ConnectionStrings": {
  "DefaultConnection1": "Host=localhost;Port=5432;Database=authdb;Username=tapsuser;Password=앱비밀번호",
  "DefaultConnection2": "Host=localhost;Port=5432;Database=ftmsdb;Username=tapsuser;Password=앱비밀번호"
},
"Jwt": { "SecretKey": "32자 이상 임의 문자열" }
```

### 5-2. Docker(nginx) 에서 접속 가능하도록 리슨 주소 변경
`Properties\launchSettings.json` 의 `http` 프로필 `applicationUrl`
- `servers\ftms\app-server.ftms` : `http://localhost:8121` → **`http://0.0.0.0:8121`**
- `servers\ftms\DxfBlazorViewer` : `http://localhost:8122` → **`http://0.0.0.0:8122`**

(localhost 로만 열려 있으면 컨테이너에서 접근이 막혀 nginx 가 502 를 냄)

### 5-3. 도면 파일
`.dxf\FTMS.v1.dxf` → `servers\ftms\app-server.ftms\bin\Debug\assets\FTMS.v1.dxf` 로 복사
(또는 app-server 디버그 프로필 환경변수 `FTMS_DXF_PATH` = dxf 파일 전체 경로)

### 5-4. 1회 실행
VS2026 → `app-server.ftms` 를 시작 프로젝트로 → F5 → 콘솔 로그에서 아래 확인 후 종료
- `데이터베이스1 스키마가 성공적으로 확인/생성되었습니다.`
- `데이터베이스2 스키마가 성공적으로 확인/생성되었습니다.`
- `기본 설정 데이터가 성공적으로 데이터베이스에 작성되었습니다.`

확인
```powershell
docker exec ftms-db psql -U postgres -d authdb -c "\dt"     # asp_net_users, asp_net_roles, refresh_tokens ...
docker exec ftms-db psql -U postgres -d ftmsdb -c "\dt"     # settings, scan_data, process_counts, ftms_event_list, event_data_store, collected_payload, rawdata_table ...
docker exec ftms-db psql -U postgres -d authdb -c "select user_name from asp_net_users"   # admin, manager, ... (기본 계정)
```
> 이 단계 전에 ftmsdb 에 테이블을 만들면 앱이 테이블을 생성하지 않습니다(42P01 오류). 반드시 **앱 1회 실행 → 6단계** 순서.

## 6. 시계열·집계 설정 (02 스크립트)

```powershell
docker exec ftms-db psql -U tapsuser -d ftmsdb -f /ftms-sql/02_ftmsdb_timescale_setup.sql
```
확인 (마지막에 자동 출력됨)
```powershell
docker exec ftms-db psql -U postgres -d ftmsdb -c "select hypertable_name from timescaledb_information.hypertables"   # 7개
docker exec ftms-db psql -U postgres -d ftmsdb -c "select proname from pg_proc where proname in ('process_counts_aggregate_all','get_dock_process_statistics_lastest','process_collected_payload_v4')"  # 3개
docker exec ftms-db psql -U tapsuser -d ftmsdb -c "select * from process_counts_aggregate_all(1000)"                   # 오류 없이 1행
```
여러 번 실행해도 안전합니다.

## 7. VS2026 실행 구성

1. 솔루션 우클릭 → **시작 프로젝트 구성** → 여러 개의 시작 프로젝트
   - `app-server.ftms` : 시작 (프로필 http)
   - `DxfBlazorViewer.Client` : 시작 (프로필 http)
   - `reverse-proxy.ftms` : **없음** (nginx 가 대신함) / `wasm-scada.ftms` : 없음 (같은 8122 포트)
2. F5
3. 처음 실행 시 Windows 방화벽 창 → **개인 네트워크 허용** (Docker → 8121/8122 접근, TCP 6004~6006)
   창이 안 떴거나 거부했다면 (관리자 PowerShell):
   ```powershell
   New-NetFirewallRule -DisplayName "FTMS dev 8121-8122" -Direction Inbound -Protocol TCP -LocalPort 8121,8122 -Action Allow -Profile Private
   New-NetFirewallRule -DisplayName "FTMS dev TCP 6004-6006" -Direction Inbound -Protocol TCP -LocalPort 6004-6006 -Action Allow -Profile Private
   ```
4. 확인: `http://localhost:8121/health` (JSON), `http://localhost:8121/swagger` (API 목록)

## 8. nginx 리버스 프록시

```powershell
docker compose up -d ftms-nginx
docker compose ps                          # ftms-nginx (healthy)
docker exec ftms-nginx nginx -t            # syntax is ok
docker exec ftms-nginx wget -qO- http://host.docker.internal:8121/health   # 컨테이너 → app-server 연결 확인
```
브라우저 확인
| 주소 | 기대 결과 |
|---|---|
| `http://localhost/nginx-health` | ok |
| `http://localhost/health` | app-server 상태 JSON |
| `http://localhost/api/Dock/status` | `[]` 또는 도크 상태 JSON |
| `http://localhost/` | FTMS 로그인 화면 |

설정 변경 후: `docker compose restart ftms-nginx` / 로그: `logs\ftms_access.log`, `logs\ftms_error.log`

## 9. 기능 테스트

1. `http://localhost/` 로그인: **admin / admin** (기본 계정, 테스트 후 비밀번호 변경 권장)
2. F12 → Network → `chathub?id=...` 상태 **101** (SignalR 정상)
3. 도크 에뮬레이터 실행: VS 에서 `tcpclient.emul.ftms` 우클릭 → 디버그 → 새 인스턴스 시작
   (기본 설정: 127.0.0.1:6004, 도크 B22 / Heartbeat 2초, 처리수량 초당 N건)
4. 화면 `/viewer`, `/dashboard`, `/monitor` 에 B22 상태·처리량 표시 확인
5. DB 로 데이터 흐름 확인
   ```powershell
   docker exec ftms-db psql -U postgres -d ftmsdb -c "select count(*) from collected_payload"                       # 수집 버퍼
   docker exec ftms-db psql -U postgres -d ftmsdb -c "select count(*), max(created_at) from process_counts"         # 원본
   docker exec ftms-db psql -U postgres -d ftmsdb -c "select count(*) from process_counts where is_aggregated=false"  # 0 근처 유지 = 집계 정상
   docker exec ftms-db psql -U postgres -d ftmsdb -c "select * from process_count_per_minute_accum order by minute_bucket desc limit 5"
   docker exec ftms-db psql -U postgres -d ftmsdb -c "select occurred_at, event_code, event_desc from ftms_event_log order by occurred_at desc limit 5"   # 알람
   ```

## 10. 일상 명령

| 작업 | 명령 |
|---|---|
| 전체 시작 / 중지 | `docker compose up -d` / `docker compose stop` |
| 상태 | `docker compose ps` |
| 로그 | `docker logs -f ftms-db`, `docker logs -f ftms-nginx` |
| DB 접속 | `docker exec -it ftms-db psql -U tapsuser -d ftmsdb` |
| DB 백업 | `docker exec ftms-db pg_dump -U postgres -Fc ftmsdb > ftmsdb.dump` |
| DB 완전 초기화 | `docker compose down -v` → 4단계부터 |

## 11. 문제 해결

| 증상 | 원인 / 조치 |
|---|---|
| `docker` 명령 없음 / Server 정보 없음 | Docker Desktop 실행 안 됨 → 트레이 아이콘 확인, WSL2 엔진 설정 |
| ftms-db 가 계속 재시작 | `docker logs ftms-db` 확인. `\r: command not found` → init 스크립트 CRLF → LF 로 저장 후 `docker compose down -v` |
| `[ftms-init]` 로그 없음 | 볼륨에 이전 데이터 있음 → init 미실행. `docker compose down -v` 후 재시작 |
| app-server: `password authentication failed` | appsettings 비밀번호 ≠ `.env` FTMS_DB_PASSWORD |
| app-server: `Connection refused 5432` | ftms-db 미기동, 또는 FTMS_DB_PORT 변경 시 연결 문자열 Port 불일치 |
| 42P01 `relation ... does not exist` | 5단계(앱 1회 실행) 전에 테이블이 생김 → `docker compose down -v` 후 순서대로 |
| 화면 통계가 비어 있음 | 6단계(02 스크립트) 누락 → 집계 함수 없음 |
| nginx **502** | 5-2(0.0.0.0 리슨) 누락 또는 방화벽 → `docker exec ftms-nginx wget -qO- http://host.docker.internal:8121/health` 로 확인 |
| nginx 기본 환영 페이지 | 템플릿 미적용 → `docker exec ftms-nginx cat /etc/nginx/conf.d/default.conf` |
| 포트 80 사용 중 | IIS/World Wide Web 서비스 등 → `.env` FTMS_HTTP_PORT=8080 → `http://localhost:8080` |
| 실시간 갱신 안 됨 | chathub 가 101 이 아님 → nginx 로그 확인 |
| 에뮬레이터 연결 안 됨 | app-server 콘솔에 `Listening on 0.0.0.0:6004` 확인, 방화벽 6004 |
| `/viewer` 도면 안 나옴 | 5-3 DXF 파일 위치 확인 → `http://localhost:8121/api/dxf` |

## 부록 A. Docker Desktop 없이 WSL 안에 Docker Engine 설치

```bash
# WSL Ubuntu 셸에서
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER && newgrp docker
sudo service docker start          # systemd 사용 시: sudo systemctl enable --now docker
```
- 키트는 WSL 경로(예: `/mnt/d/ftms-docker`)에서 `docker compose up -d`
- 컨테이너 포트(80, 5432)는 WSL 이 Windows `localhost` 로 자동 전달
- 단, 컨테이너 → **Windows 의 app-server** 접근 시 `host.docker.internal` 이 Windows 를 가리키지 않음 →
  `.env` 의 `FTMS_API_UPSTREAM` / `FTMS_WEB_UPSTREAM` 을 Windows IP 로 변경
  (WSL 에서 `ip route show default | awk '{print $3}'` 값, 예: `172.28.80.1:8121`) + Windows 방화벽에서 WSL 대역 허용
  또는 `%UserProfile%\.wslconfig` 에 `[wsl2]` `networkingMode=mirrored` 설정 후 `wsl --shutdown`

## 검증 내역

- DB init(`10-ftms-init.sh` + `01_create_db_and_user.sql`): PostgreSQL 16 로 실행 시험 — 계정/DB 생성, 재실행(멱등), 특수문자(`'`, `"`, `$`, 공백) 비밀번호 확인. (timescaledb 확장 생성 줄은 확장이 없어 제외하고 시험)
- `02_ftmsdb_timescale_setup.sql`: TimescaleDB 함수 대체 상태로 생성·재실행·집계 결과 검증 (앞서 시험)
- nginx 템플릿: nginx 1.24 `nginx -t` 및 라우팅·WebSocket 헤더 시험 (앞서 시험)
- docker-compose.yml: YAML 문법 확인
- **Docker Desktop / 실제 TimescaleDB 이미지 / check-env.ps1 은 이 환경에서 실행하지 못함**
