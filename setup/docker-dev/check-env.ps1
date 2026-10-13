# FTMS 로컬 실행 환경 점검 (Windows PowerShell)
#   사용: docker-dev 폴더에서  powershell -ExecutionPolicy Bypass -File .\check-env.ps1
#   각 항목을 [OK] / [NG] / [--](정보) 로 표시합니다.

$ErrorActionPreference = 'SilentlyContinue'
$envFile = Join-Path $PSScriptRoot '.env'
$cfg = @{}
if (Test-Path $envFile) {
    Get-Content $envFile | Where-Object { $_ -match '^\s*([A-Z_]+)\s*=\s*(.*)$' } | ForEach-Object {
        $null = $_ -match '^\s*([A-Z_]+)\s*=\s*(.*)$'; $cfg[$Matches[1]] = $Matches[2].Trim()
    }
}
$httpPort = if ($cfg['FTMS_HTTP_PORT']) { $cfg['FTMS_HTTP_PORT'] } else { '80' }
$dbPort   = if ($cfg['FTMS_DB_PORT'])   { $cfg['FTMS_DB_PORT'] }   else { '5432' }

function Show($ok, $name, $detail) {
    if ($ok -eq $true) { Write-Host "[OK] $name  $detail" -ForegroundColor Green }
    elseif ($ok -eq $false) { Write-Host "[NG] $name  $detail" -ForegroundColor Red }
    else { Write-Host "[--] $name  $detail" -ForegroundColor Gray }
}
function Section($t) { Write-Host "`n===== $t =====" -ForegroundColor Cyan }

Section '1. WSL / Docker'
$wsl = (wsl -l -v 2>$null) -join ' '
$wsl = $wsl -replace "`0", ''
Show ($wsl -match '2') 'WSL2' ($wsl -replace '\s+', ' ')
$dv = docker version --format '{{.Server.Version}}' 2>$null
Show ([bool]$dv) 'Docker 엔진 실행' "server=$dv"
$dc = docker compose version --short 2>$null
Show ([bool]$dc) 'docker compose' $dc

Section '2. .NET'
$sdks = (dotnet --list-sdks) -join '; '
Show ($sdks -match '8\.0\.4\d\d') '.NET SDK 8.0.4xx' $sdks
$wl = (dotnet workload list) -join ' '
Show ($wl -match 'wasm-tools') 'wasm-tools 워크로드' ''

Section '3. 포트 사용 현황'
foreach ($p in @($httpPort, $dbPort, 8121, 8122, 6004, 6005, 6006)) {
    $c = Get-NetTCPConnection -State Listen -LocalPort $p | Select-Object -First 1
    if ($c) {
        $proc = (Get-Process -Id $c.OwningProcess).ProcessName
        Show $null "포트 $p" "사용 중: $proc (PID $($c.OwningProcess), 주소 $($c.LocalAddress))"
    } else { Show $null "포트 $p" '비어 있음' }
}

Section '4. 컨테이너'
foreach ($n in 'ftms-db','ftms-nginx') {
    $st = docker inspect -f '{{.State.Status}} / {{if .State.Health}}{{.State.Health.Status}}{{end}}' $n 2>$null
    Show ($st -match 'running') $n $st
}

Section '5. DB (ftms-db 컨테이너 안에서 확인)'
$q = {
    param($db, $sql)
    docker exec ftms-db psql -U postgres -d $db -Atc $sql 2>$null
}
$roles = & $q 'postgres' "select rolname from pg_roles where rolname='tapsuser'"
Show ($roles -eq 'tapsuser') '계정 tapsuser' ''
$dbs = (& $q 'postgres' "select string_agg(datname, ',') from pg_database where datname in ('authdb','ftmsdb')")
Show ($dbs -match 'authdb' -and $dbs -match 'ftmsdb') 'DB authdb / ftmsdb' $dbs
$ext = (& $q 'ftmsdb' "select string_agg(extname||' '||extversion, ', ') from pg_extension")
Show ($ext -match 'timescaledb' -and $ext -match 'pgcrypto') '확장 timescaledb / pgcrypto' $ext
$tbl = & $q 'ftmsdb' "select count(*) from information_schema.tables where table_schema='public'"
Show ([int]$tbl -gt 0) 'ftmsdb 테이블 수 (app-server 1회 실행 후 생성)' $tbl
$idn = & $q 'authdb' "select count(*) from information_schema.tables where table_schema='public'"
Show ([int]$idn -gt 0) 'authdb 테이블 수 (로그인/권한)' $idn
$hyp = & $q 'ftmsdb' "select count(*) from timescaledb_information.hypertables"
Show ([int]$hyp -ge 7) '하이퍼테이블 (02 스크립트 후 7개)' $hyp
$fn = (& $q 'ftmsdb' "select string_agg(proname, ', ') from pg_proc where proname in ('process_counts_aggregate_all','get_dock_process_statistics_lastest','process_collected_payload_v4')")
Show (($fn -split ',').Count -ge 3) 'DB 함수 3개' $fn
$pc = & $q 'ftmsdb' "select count(*) from process_counts"
Show $null 'process_counts 행 수 (에뮬레이터 실행 후 증가)' $pc

Section '6. HTTP'
function Probe($url) {
    try { $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5; return "$($r.StatusCode)" }
    catch { if ($_.Exception.Response) { return "$([int]$_.Exception.Response.StatusCode)" } else { return 'no response' } }
}
$base = if ($httpPort -eq '80') { 'http://localhost' } else { "http://localhost:$httpPort" }
foreach ($u in @("http://localhost:8121/health", "$base/nginx-health", "$base/health", "$base/api/Dock/status", "$base/")) {
    $code = Probe $u
    Show ($code -eq '200') $u $code
}

Write-Host "`n점검 완료" -ForegroundColor Cyan
