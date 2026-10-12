# FTMS 리버스 프록시 — Docker nginx

`reverse-proxy.ftms`(YARP) 를 대체합니다. 라우팅은 YARP 설정과 동일합니다.

```
브라우저 ──:80──► [Docker] ftms-nginx ──► app-server.ftms :8121   (/api/*, /chathub, /health)
                                     └──► web-server.ftms :8122   (그 외: Blazor 화면)
```

## 1. 파일 구성

```
docker-nginx/
 ├ docker-compose.yml               컨테이너 정의
 ├ .env                             포트·백엔드 주소 (여기만 수정)
 ├ nginx/
 │  ├ templates/default.conf.template   기본 구성 (기동 시 .env 값으로 치환 → conf.d/default.conf)
 │  ├ alt/default.conf.template         [선택] web-server 없이 nginx 가 화면 파일 직접 제공
 │  └ ftms_proxy_headers                공통 프록시 헤더(클라이언트 IP 전달)
 └ logs/                            접속/오류 로그 (자동 생성)
```

## 2. Docker 설치

### Linux (Ubuntu) — 운영 권장
```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER      # 재로그인 후 sudo 없이 docker 사용
docker --version && docker compose version
```

### Windows 10/11 — 개발/시험용
1. 관리자 PowerShell: `wsl --install` → 재부팅
2. Docker Desktop 설치 (https://www.docker.com/products/docker-desktop) → "Use WSL 2 based engine" 사용
3. `docker --version`, `docker compose version` 확인

> Windows **Server** 에서는 Docker Desktop 이 지원되지 않아 Linux 컨테이너(nginx)를 쓰기 어렵습니다. Windows Server 라면 YARP(기존) 또는 IIS 를 권장합니다.

### 폐쇄망(인터넷 없음)
```bash
# 인터넷 되는 PC
docker pull nginx:stable-alpine
docker save nginx:stable-alpine -o nginx-stable-alpine.tar
# 현장 서버로 복사 후
docker load -i nginx-stable-alpine.tar
```

## 3. 사전 준비

| 항목 | 확인 |
|---|---|
| app-server / web-server 실행 | 각각 8121, 8122 에서 동작 (`curl http://localhost:8121/health`) |
| **0.0.0.0 으로 리슨** | 컨테이너는 호스트를 다른 IP(도커 브리지)로 접근하므로 `localhost` 전용 리슨이면 연결 불가. app-server `appsettings.Production.json` = `http://*:8121`, web-server `appsettings.json` = `http://0.0.0.0:8122` (기본값 OK) |
| **80 포트 비우기** | YARP(`reverse-proxy.ftms`) 서비스 중지, IIS 사용 중이면 중지. 확인: Linux `sudo ss -ltnp | grep :80`, Windows `netstat -ano | findstr :80` |
| 방화벽 | 외부 → 80 허용, 외부 → 8121/8122 **차단**, 도커 내부망 → 8121/8122 허용 |

Linux(ufw) 예:
```bash
sudo ufw allow 80/tcp
sudo ufw allow from 172.16.0.0/12 to any port 8121,8122 proto tcp   # 도커 브리지 대역
sudo ufw deny 8121/tcp
sudo ufw deny 8122/tcp
sudo ufw allow 6004:6006/tcp                                         # 도크 TCP (nginx 와 무관)
```

## 4. 설정 (.env)

```ini
FTMS_HTTP_PORT=80
FTMS_API_UPSTREAM=host.docker.internal:8121   # 같은 서버면 그대로
FTMS_WEB_UPSTREAM=host.docker.internal:8122   # 다른 서버면 192.168.x.x:8122
```

## 5. 실행 / 운영 명령

```bash
cd docker-nginx
docker compose up -d                 # 시작 (재부팅 후 자동 시작: restart: unless-stopped)
docker compose ps                    # 상태 (healthy 확인)
docker compose logs -f               # 컨테이너 로그
tail -f logs/ftms_access.log logs/ftms_error.log

# 설정 변경 후 (템플릿은 기동 시 치환되므로 재시작)
docker compose restart
# 설정 문법 검사
docker exec ftms-nginx nginx -t

docker compose down                  # 중지/삭제
```

## 6. 동작 확인

```bash
curl http://서버IP/nginx-health      # ok            (nginx 자체)
curl http://서버IP/health            # app-server JSON
curl http://서버IP/api/Dock/status   # app-server JSON
curl -I http://서버IP/               # 200 (web-server 화면)
```
브라우저에서 `http://서버IP/` 접속 → 로그인 → 개발자도구(F12) Network 탭에서 `chathub?id=...` 가 **101 Switching Protocols** 이면 SignalR(WebSocket) 정상.

## 7. [선택] nginx 가 화면 파일을 직접 제공 (web-server.ftms 불필요)

1. 화면 빌드: `dotnet publish servers/ftms/DxfBlazorViewer/DxfBlazorViewer.Client.csproj -c Release -o out`
2. `out/wwwroot` 를 `docker-nginx/wwwroot` 로 복사
3. `docker-compose.yml` 수정
   - `./nginx/templates:/etc/nginx/templates:ro` → `./nginx/alt:/etc/nginx/templates:ro`
   - `# - ./wwwroot:/usr/share/nginx/ftms:ro` 주석 해제
4. `docker compose up -d --force-recreate`

`.wasm` MIME, `.dll/.dat` 1년 캐시, `index.html`·`blazor.boot.json` 캐시 안 함, `/viewer` 등 화면 주소 → `index.html` 처리 포함.

## 8. 문제 해결

| 증상 | 원인 / 조치 |
|---|---|
| **502 Bad Gateway** | 컨테이너에서 백엔드 접근 불가 → app-server 가 0.0.0.0 리슨인지, 방화벽이 도커 대역(172.16.0.0/12)을 허용하는지, `.env` 주소 확인. `docker exec ftms-nginx wget -qO- http://host.docker.internal:8121/health` |
| nginx 기본 환영 페이지 | 템플릿이 마운트되지 않음 → `docker exec ftms-nginx cat /etc/nginx/conf.d/default.conf` 확인 |
| 화면은 뜨는데 실시간 갱신 안 됨 | `/chathub` WebSocket 실패 → 앞단에 다른 프록시/방화벽이 Upgrade 헤더를 막는지 확인 |
| 업로드 413 | 500MB 초과 → 템플릿 `client_max_body_size` 조정 |
| 로그인 후 바로 로그아웃/토큰 오류 | Refresh 토큰이 클라이언트 IP 에 묶여 있음 → `ftms_proxy_headers` 의 X-Forwarded-For 가 빠지지 않았는지 확인 |
| 80 포트 사용 중 | YARP/IIS/다른 웹서버 중지 또는 `.env` 의 `FTMS_HTTP_PORT` 변경 |
| SELinux(RHEL 계열)에서 설정 파일 읽기 실패 | 볼륨에 `:ro,Z` 사용 |

## 9. 검증 내역

nginx 1.24 로 템플릿 치환(envsubst) → `nginx -t` 통과, 가짜 백엔드 2개로 라우팅 시험:
`/health`·`/api/*`·`/chathub` → API, `/viewer`·`/_framework/*`·`/apix` → Web, WebSocket 시 `Connection: upgrade` 전달, 그 외 keepalive, X-Real-IP/X-Forwarded-* 전달, 500MB 초과 413.
선택 구성(정적 제공)도 `/viewer`→index.html, `.wasm`=application/wasm, 캐시 헤더 확인.
실제 Docker 컨테이너와 실제 app-server 로는 시험하지 못함.
