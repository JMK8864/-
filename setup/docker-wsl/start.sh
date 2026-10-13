#!/usr/bin/env bash
# FTMS 로컬 환경 시작 (WSL 안에서 실행)
#   ./start.sh              DB + nginx
#   ./start.sh ftms-db      DB 만
#   ./start.sh ftms-nginx   nginx 만 (Windows IP 가 바뀌었을 때도 이것으로 재적용)
set -euo pipefail
cd "$(dirname "$0")"

# Windows 에서 복사/편집하며 생긴 CRLF 제거 (CRLF 이면 DB init 스크립트가 실패함)
sed -i 's/\r$//' .env db/init/*.sh start.sh check-env.sh 2>/dev/null || true

setting=$(grep -E '^FTMS_WIN_HOST=' .env | head -1 | cut -d= -f2- | tr -d '[:space:]')
if [ -z "$setting" ] || [ "$setting" = "auto" ]; then
    mode=$(wslinfo --networking-mode 2>/dev/null || echo nat)
    if [ "$mode" = "mirrored" ]; then
        echo "[start] WSL networkingMode=mirrored : 자동 감지를 지원하지 않습니다. .env 의 FTMS_WIN_HOST 에 Windows IP 를 직접 입력하세요." >&2
        exit 1
    fi
    host=$(ip route show default | awk '{print $3; exit}')
else
    host="$setting"
fi
[ -n "$host" ] || { echo "[start] Windows 호스트 IP 를 찾지 못했습니다" >&2; exit 1; }

# .env 의 FTMS_WIN_HOST_RESOLVED 갱신 (docker compose 직접 실행 시에도 사용됨)
if grep -qE '^FTMS_WIN_HOST_RESOLVED=' .env; then
    sed -i "s|^FTMS_WIN_HOST_RESOLVED=.*|FTMS_WIN_HOST_RESOLVED=$host|" .env
else
    printf '\nFTMS_WIN_HOST_RESOLVED=%s\n' "$host" >> .env
fi
echo "[start] Windows 호스트(app-server/웹 화면) = $host"

api_port=$(grep -E '^FTMS_API_PORT=' .env | cut -d= -f2 | tr -d '[:space:]'); api_port=${api_port:-8121}
if command -v curl >/dev/null; then
    code=$(curl -s -o /dev/null -m 3 -w '%{http_code}' "http://$host:$api_port/health" || true)
    if [ "$code" = "200" ]; then echo "[start] app-server 연결 OK (http://$host:$api_port/health)"
    else echo "[start] 경고: app-server 에 연결 안 됨 (HTTP=$code). VS 에서 실행 중인지, 0.0.0.0 리슨/Windows 방화벽 확인 (DB 만 먼저 띄울 때는 정상)"; fi
fi

docker compose up -d "$@"
docker compose ps
