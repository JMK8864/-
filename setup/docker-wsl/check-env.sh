#!/usr/bin/env bash
# FTMS 로컬 환경 점검 (WSL 안에서 실행): ./check-env.sh
cd "$(dirname "$0")"
G='\033[32m'; R='\033[31m'; Y='\033[90m'; C='\033[36m'; N='\033[0m'
ok(){ echo -e "${G}[OK]${N} $1  $2"; }; ng(){ echo -e "${R}[NG]${N} $1  $2"; }; info(){ echo -e "${Y}[--]${N} $1  $2"; }
chk(){ if [ "$1" = 0 ]; then ok "$2" "$3"; else ng "$2" "$3"; fi; }
sec(){ echo -e "\n${C}===== $1 =====${N}"; }
envv(){ grep -E "^$1=" .env 2>/dev/null | head -1 | cut -d= -f2- | tr -d '\r'; }
HTTP=$(envv FTMS_HTTP_PORT); HTTP=${HTTP:-80}; WIN=$(envv FTMS_WIN_HOST_RESOLVED)
API=$(envv FTMS_API_PORT); API=${API:-8121}; WEB=$(envv FTMS_WEB_PORT); WEB=${WEB:-8122}

sec "1. WSL / Docker Engine"
info "배포판" "$(. /etc/os-release 2>/dev/null; echo "$PRETTY_NAME")  커널 $(uname -r)"
[ "$(ps -p 1 -o comm= 2>/dev/null)" = systemd ]; chk $? "systemd 사용" "(PID 1 = $(ps -p 1 -o comm= 2>/dev/null))"
command -v docker >/dev/null; chk $? "docker 설치" "$(docker --version 2>/dev/null)"
docker info >/dev/null 2>&1; chk $? "docker 엔진 실행 / 권한" "$(docker info --format 'server {{.ServerVersion}}' 2>/dev/null)"
docker compose version >/dev/null 2>&1; chk $? "docker compose" "$(docker compose version --short 2>/dev/null)"
info "networkingMode" "$(wslinfo --networking-mode 2>/dev/null || echo 'nat(추정)')"
info "Windows 호스트 IP" "감지: $(ip route show default | awk '{print $3; exit}')  / .env 기록: ${WIN:-없음}"

sec "2. 컨테이너"
for n in ftms-db ftms-nginx; do
  st=$(docker inspect -f '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' $n 2>/dev/null)
  [[ "$st" == running* ]]; chk $? "$n" "${st:-없음}"
done

sec "3. DB"
q(){ docker exec ftms-db psql -U postgres -d "$1" -Atc "$2" 2>/dev/null; }
[ "$(q postgres "select 1 from pg_roles where rolname='tapsuser'")" = 1 ]; chk $? "계정 tapsuser"
d=$(q postgres "select string_agg(datname,',' order by datname) from pg_database where datname in ('authdb','ftmsdb')"); [ "$d" = "authdb,ftmsdb" ]; chk $? "DB authdb/ftmsdb" "$d"
e=$(q ftmsdb "select string_agg(extname||' '||extversion,', ') from pg_extension"); [[ "$e" == *timescaledb* && "$e" == *pgcrypto* ]]; chk $? "확장" "$e"
t=$(q ftmsdb "select count(*) from information_schema.tables where table_schema='public'"); [ "${t:-0}" -gt 0 ]; chk $? "ftmsdb 테이블 (app-server 1회 실행 후)" "${t:-0}"
a=$(q authdb "select count(*) from information_schema.tables where table_schema='public'"); [ "${a:-0}" -gt 0 ]; chk $? "authdb 테이블 (로그인/권한)" "${a:-0}"
h=$(q ftmsdb "select count(*) from timescaledb_information.hypertables"); [ "${h:-0}" -ge 7 ]; chk $? "하이퍼테이블 (02 스크립트 후 7)" "${h:-0}"
f=$(q ftmsdb "select count(*) from pg_proc where proname in ('process_counts_aggregate_all','get_dock_process_statistics_lastest','process_collected_payload_v4')"); [ "${f:-0}" -ge 3 ]; chk $? "DB 함수 3개" "${f:-0}"
info "process_counts 행 수" "$(q ftmsdb 'select count(*) from process_counts')"

sec "4. 네트워크"
code(){ curl -s -o /dev/null -m 4 -w '%{http_code}' "$1" 2>/dev/null; }
if [ -n "$WIN" ]; then
  c=$(code "http://$WIN:$API/health"); [ "$c" = 200 ]; chk $? "WSL → Windows app-server" "http://$WIN:$API/health → $c"
  c=$(code "http://$WIN:$WEB/");       [ "$c" = 200 ]; chk $? "WSL → Windows 웹 화면" "http://$WIN:$WEB/ → $c"
  c=$(docker exec ftms-nginx wget -qO- -T 4 "http://$WIN:$API/health" >/dev/null 2>&1 && echo 200 || echo fail); [ "$c" = 200 ]; chk $? "nginx 컨테이너 → app-server" "$c"
fi
for u in nginx-health health api/Dock/status ""; do
  c=$(code "http://localhost:$HTTP/$u"); [ "$c" = 200 ]; chk $? "http://localhost:$HTTP/$u" "$c"
done
echo
