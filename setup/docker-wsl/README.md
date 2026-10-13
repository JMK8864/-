# FTMS 로컬 실행·테스트 가이드 — WSL Docker Engine (Docker Desktop 미사용)

DB(TimescaleDB)와 nginx 는 **WSL(Ubuntu) 안의 Docker Engine 컨테이너**로, 앱(app-server, 웹 화면)은 **Windows VS2026** 에서 실행합니다.

```
 Windows                                         WSL2 (Ubuntu) ─ Docker Engine
 ─────────────────────────────                   ─────────────────────────────────────
 브라우저 http://localhost ── localhost 전달 ──► ftms-nginx :80
                                                    │  (Windows 호스트 IP = WSL 기본 게이트웨이, start.sh 자동 감지)
 VS: app-server.ftms  0.0.0.0:8121  ◄───────────────┤ /api, /chathub, /health
 VS: DxfBlazorViewer  0.0.0.0:8122  ◄───────────────┘ 그 외(화면)
 VS: app-server ── localhost:5432 ── localhost 전달 ──► ftms-db (PostgreSQL 17 + TimescaleDB)
 VS: tcpclient.emul ── 127.0.0.1:6004 ──► app-server
```

## 0. 준비물과 확인 방법

| # | 항목 | 어디서 | 확인 명령 | 정상 |
|---|---|---|---|---|
| 1 | WSL2 + Ubuntu | Windows | `wsl -l -v` | Ubuntu, VERSION 2 |
| 2 | systemd 사용 | WSL | `ps -p 1 -o comm=` | systemd |
| 3 | Docker Engine + compose | WSL | `docker version`, `docker compose version` | Server 표시 |
| 4 | .NET SDK 8.0.4xx / wasm-tools | Windows | `dotnet --list-sdks`, `dotnet workload list` | 8.0.425 / wasm-tools |
| 5 | 이 키트 | WSL `~/ftms-docker` | `ls ~/ftms-docker` | start.sh 등 |
| 6 | `FTMS.v1.dxf` | 솔루션 `.dxf\` | — | — |

일괄 점검: WSL 에서 `~/ftms-docker/check-env.sh`, Windows 에서 `check-env.ps1`

> 이하 **[PS]** = Windows PowerShell, **[WSL]** = Ubuntu 터미널 (`wsl` 입력 또는 시작 메뉴 Ubuntu)

## 1. WSL2 + Ubuntu

```powershell
# [PS] 관리자
wsl --install -d Ubuntu-24.04      # 처음이면 재부팅 → Ubuntu 사용자 이름/비밀번호 설정
wsl --update
wsl -l -v                          # Ubuntu-24.04  Running  2
```
**systemd 켜기** (Docker 자동 시작용)
```bash
# [WSL]
sudo tee /etc/wsl.conf >/dev/null <<'CONF'
[boot]
systemd=true
CONF
```
```powershell
# [PS]
wsl --shutdown          # 몇 초 후 Ubuntu 다시 열기
```
```bash
# [WSL] 확인
ps -p 1 -o comm=        # systemd
```

## 2. Docker Engine 설치 (WSL 안)

```bash
# [WSL]
curl -fsSL https://get.docker.com | sudo sh          # docker-ce + compose 플러그인
sudo usermod -aG docker $USER                        # sudo 없이 docker 사용
sudo systemctl enable --now docker
exit                                                 # 터미널 닫고 다시 열기(그룹 적용)
```
확인
```bash
# [WSL]
docker version              # Client / Server 둘 다 표시
docker compose version
docker run --rm hello-world # "Hello from Docker!"
systemctl is-enabled docker # enabled
```
> 폐쇄망: 인터넷 되는 PC 에서 `docker save timescale/timescaledb:latest-pg17 nginx:stable-alpine -o ftms-images.tar` → 현장 WSL 에서 `docker load -i ftms-images.tar`
> (Docker 패키지 자체도 오프라인 설치 필요: https://docs.docker.com/engine/install/ubuntu/ 의 "Install from a package")

## 3. 키트 배치 (WSL 파일시스템에)

`/mnt/d/...`(Windows 드라이브)에서 직접 실행하면 느리고 권한·줄바꿈 문제가 생기므로 **WSL 홈으로 복사**합니다.
```bash
# [WSL]  (zip 을 D:\Download 에 받았다고 가정)
sudo apt-get install -y unzip
cd ~ && unzip /mnt/d/Download/docker-wsl.zip && mv docker-wsl ftms-docker
cd ~/ftms-docker
sed -i 's/\r$//' *.sh db/init/*.sh .env      # Windows 줄바꿈(CRLF) 제거
chmod +x *.sh db/init/*.sh
nano .env                                    # 비밀번호 수정 (Ctrl+O 저장, Ctrl+X 종료)
```
`.env` 주요 값
```ini
POSTGRES_PASSWORD=관리자비밀번호
FTMS_DB_PASSWORD=앱비밀번호         # app-server 연결 문자열과 동일
FTMS_DB_PORT=5432                   # Windows 에 PostgreSQL 설치돼 있으면 5433
FTMS_HTTP_PORT=80                   # 80 사용 중이면 8080
FTMS_WIN_HOST=auto                  # Windows IP 자동 감지 (NAT 모드). 다른 PC 의 앱이면 그 IP
```
포트 확인 [PS]: `netstat -ano | findstr ":80 :5432 :8121 :8122 :6004"`

## 4. DB 컨테이너 (계정·DB·확장 자동 생성)

```bash
# [WSL]
cd ~/ftms-docker
./start.sh ftms-db               # "app-server 에 연결 안 됨" 경고는 이 단계에선 정상
docker compose ps                # ftms-db ... (healthy)
docker logs ftms-db 2>&1 | grep ftms-init     # [ftms-init] done
docker exec ftms-db psql -U postgres -c "\du"              # tapsuser
docker exec ftms-db psql -U postgres -c "\l"               # authdb, ftmsdb (Owner tapsuser)
docker exec ftms-db psql -U postgres -d ftmsdb -c "\dx"    # timescaledb, pgcrypto
```
```powershell
# [PS] Windows 에서 DB 포트 접근 확인 (WSL localhost 전달)
Test-NetConnection localhost -Port 5432       # TcpTestSucceeded : True
```
> init 은 DB 볼륨이 비었을 때 1회만 실행. 처음부터 다시: `docker compose down -v` (데이터 삭제!)

## 5. app-server 설정 → 1회 실행 (테이블 자동 생성)

**5-1** `servers\ftms\app-server.ftms\appsettings.json`
```json
"ConnectionStrings": {
  "DefaultConnection1": "Host=localhost;Port=5432;Database=authdb;Username=tapsuser;Password=앱비밀번호",
  "DefaultConnection2": "Host=localhost;Port=5432;Database=ftmsdb;Username=tapsuser;Password=앱비밀번호"
},
"Jwt": { "SecretKey": "32자 이상 임의 문자열" }
```
**5-2** nginx(WSL)가 접근할 수 있게 `Properties\launchSettings.json` 의 `http` 프로필 `applicationUrl` 변경
- `app-server.ftms` : `http://localhost:8121` → **`http://0.0.0.0:8121`**
- `DxfBlazorViewer` : `http://localhost:8122` → **`http://0.0.0.0:8122`**

**5-3** Windows 방화벽 — WSL 에서 들어오는 8121/8122 허용 [PS 관리자]
```powershell
New-NetFirewallRule -DisplayName "FTMS from WSL 8121-8122" -Direction Inbound -Protocol TCP -LocalPort 8121,8122 -RemoteAddress 172.16.0.0/12,192.168.0.0/16 -Action Allow
New-NetFirewallRule -DisplayName "FTMS dock TCP 6004-6006" -Direction Inbound -Protocol TCP -LocalPort 6004-6006 -Action Allow -Profile Private
```
(WSL 대역 확인: [PS] `wsl hostname -I`)

**5-4** 도면: `.dxf\FTMS.v1.dxf` → `servers\ftms\app-server.ftms\bin\Debug\assets\FTMS.v1.dxf`

**5-5** VS2026 에서 `app-server.ftms` 만 F5 → 아래 로그 확인 후 종료
- `데이터베이스1 스키마가 성공적으로 확인/생성되었습니다.`
- `데이터베이스2 스키마가 성공적으로 확인/생성되었습니다.`
```bash
# [WSL] 확인
docker exec ftms-db psql -U postgres -d authdb -c "select user_name from asp_net_users"   # admin, manager ...
docker exec ftms-db psql -U postgres -d ftmsdb -c "\dt"                                  # settings, scan_data, process_counts ...
```
> 이 단계 전에 ftmsdb 에 테이블을 만들면 앱이 테이블을 만들지 않습니다(42P01). 순서: **4 → 5 → 6**

## 6. 시계열·집계 설정 (02 스크립트)

```bash
# [WSL]
docker exec ftms-db psql -U tapsuser -d ftmsdb -f /ftms-sql/02_ftmsdb_timescale_setup.sql
docker exec ftms-db psql -U postgres -d ftmsdb -c "select hypertable_name from timescaledb_information.hypertables"   # 7개
docker exec ftms-db psql -U tapsuser -d ftmsdb -c "select * from process_counts_aggregate_all(1000)"                   # 오류 없이 1행
```

## 7. VS2026 실행

1. 솔루션 → 시작 프로젝트 구성 → 여러 개: **app-server.ftms**(http), **DxfBlazorViewer.Client**(http) 시작 / reverse-proxy·wasm-scada 는 없음
2. F5 → `http://localhost:8121/health`, `http://localhost:8121/swagger` 확인

## 8. nginx (WSL)

```bash
# [WSL]  — VS 에서 app-server, 웹 화면이 실행 중인 상태에서
cd ~/ftms-docker
./start.sh                       # [start] app-server 연결 OK  가 나와야 함
./check-env.sh                   # 전체 [OK] 확인
```
| 브라우저(Windows) | 기대 결과 |
|---|---|
| `http://localhost/nginx-health` | ok |
| `http://localhost/health` | app-server JSON |
| `http://localhost/api/Dock/status` | JSON |
| `http://localhost/` | 로그인 화면 |

**PC 재부팅·WSL 재시작 후에는 Windows 호스트 IP 가 바뀔 수 있으므로 `./start.sh` 로 다시 시작**하세요 (`docker compose restart` 만 하면 예전 IP 사용 → 502).

## 9. 기능 테스트

1. `http://localhost/` 로그인 **admin / admin**
2. F12 → Network → `chathub?id=...` **101**
3. VS 에서 `tcpclient.emul.ftms` 우클릭 → 디버그 → 새 인스턴스 시작 (127.0.0.1:6004, 도크 B22)
4. `/viewer`, `/dashboard`, `/monitor` 에 B22 표시
5. [WSL] 데이터 확인
   ```bash
   docker exec ftms-db psql -U postgres -d ftmsdb -c "select count(*), max(created_at) from process_counts"
   docker exec ftms-db psql -U postgres -d ftmsdb -c "select count(*) from process_counts where is_aggregated=false"    # 0 근처 = 집계 정상
   docker exec ftms-db psql -U postgres -d ftmsdb -c "select * from process_count_per_minute_accum order by minute_bucket desc limit 5"
   docker exec ftms-db psql -U postgres -d ftmsdb -c "select occurred_at, event_code, event_desc from ftms_event_log order by occurred_at desc limit 5"
   ```

## 10. 일상 사용

| 작업 | 명령 |
|---|---|
| 시작 (Windows 에서 한 번에) | [PS] `wsl -d Ubuntu-24.04 -- bash -lc "~/ftms-docker/start.sh"` |
| 시작 (WSL) | `~/ftms-docker/start.sh` |
| 중지 | `docker compose stop` |
| 상태 / 점검 | `docker compose ps` / `./check-env.sh` |
| 로그 | `docker logs -f ftms-db`, `docker logs -f ftms-nginx`, `logs/ftms_*.log` |
| DB 접속 | `docker exec -it ftms-db psql -U tapsuser -d ftmsdb` |
| 백업 | `docker exec ftms-db pg_dump -U postgres -Fc ftmsdb > ~/ftmsdb.dump` |
| DB 초기화 | `docker compose down -v` → 4단계부터 |
| WSL 이 꺼졌는지 | [PS] `wsl -l -v` (Stopped 이면 Ubuntu 를 열거나 위 시작 명령) |

컨테이너는 `restart: unless-stopped` + systemd 로 WSL 이 켜지면 자동 시작되지만, **WSL 자체는 Windows 로그인 후 Ubuntu 를 한 번 열어야(또는 위 [PS] 시작 명령) 켜집니다.** 자동화하려면 작업 스케줄러에 로그온 시 `wsl -d Ubuntu-24.04 -- bash -lc "~/ftms-docker/start.sh"` 등록.

## 11. 문제 해결

| 증상 | 원인 / 조치 |
|---|---|
| `/usr/bin/env: 'bash\r'` | CRLF → `sed -i 's/\r$//' *.sh db/init/*.sh .env` |
| `permission denied ... docker.sock` | docker 그룹 미적용 → 터미널 재시작, `groups` 에 docker 확인 |
| `Cannot connect to the Docker daemon` | `sudo systemctl start docker`, systemd 미사용이면 1단계 wsl.conf |
| ftms-db 재시작 반복 / `[ftms-init]` 오류 | `docker logs ftms-db` → CRLF 또는 비밀번호 문제 → `docker compose down -v` 후 재시작 |
| Windows 에서 5432 연결 안 됨 | WSL localhost 전달 문제 → [PS] `wsl --shutdown` 후 재시작, `%UserProfile%\.wslconfig` 에 `localhostForwarding=false` 가 없는지 확인 |
| app-server `password authentication failed` | appsettings 비밀번호 ≠ `.env` FTMS_DB_PASSWORD |
| 42P01 relation does not exist | 5단계 전 테이블 생성됨 → `docker compose down -v` 후 4→5→6 |
| **nginx 502** | ① 5-2 0.0.0.0 리슨 누락 ② 5-3 방화벽 ③ IP 변경 → `./start.sh` 재실행. 확인: `curl http://$(ip route show default \| awk '{print $3}'):8121/health` |
| `FTMS_WIN_HOST_RESOLVED 없음` | `docker compose up` 을 직접 실행함 → `./start.sh` 사용 |
| `networkingMode=mirrored` 메시지 | `.wslconfig` 가 mirrored → `.env` 의 FTMS_WIN_HOST 에 Windows IP 직접 입력 (이 모드는 미검증) |
| 통계 비어 있음 | 6단계 누락 |
| 80 포트 사용 중 | `.env` FTMS_HTTP_PORT=8080 → `http://localhost:8080` |

## 검증 내역

- `start.sh`: 모의 docker 로 실행 시험 — Windows IP 자동 감지·수동 지정·mirrored 차단, `.env` 기록, CRLF 제거 확인
- `check-env.sh`: 모의 환경에서 실행 시험(오류 없이 전 항목 출력)
- DB init / 01 / 02 스크립트, nginx 템플릿: 앞서 PostgreSQL 16·nginx 1.24 로 시험
- **실제 WSL·Docker Engine·TimescaleDB 이미지, check-env.ps1 은 이 환경에서 실행하지 못함**
