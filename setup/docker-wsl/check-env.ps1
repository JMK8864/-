# FTMS 로컬 환경 점검 - Windows 쪽 (PowerShell)
#   사용: powershell -ExecutionPolicy Bypass -File .\check-env.ps1
#   Docker/DB 상세 점검은 WSL 에서 ./check-env.sh

$ErrorActionPreference = 'SilentlyContinue'
function Show($ok, $name, $detail) {
    if ($ok -eq $true) { Write-Host "[OK] $name  $detail" -ForegroundColor Green }
    elseif ($ok -eq $false) { Write-Host "[NG] $name  $detail" -ForegroundColor Red }
    else { Write-Host "[--] $name  $detail" -ForegroundColor Gray }
}
function Section($t) { Write-Host "`n===== $t =====" -ForegroundColor Cyan }

Section '1. WSL'
$list = ((wsl -l -v) -join "`n") -replace "`0", ''
Show ($list -match 'Running\s+2') 'WSL2 배포판 실행 중' ''
$list -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { Show $null '' $_.Trim() }
$dk = ((wsl -e docker info --format '{{.ServerVersion}}') -join '') -replace "`0", ''
Show ([bool]$dk) 'WSL 안 Docker 엔진' $dk

Section '2. .NET'
$sdks = (dotnet --list-sdks) -join '; '
Show ($sdks -match '8\.0\.4\d\d') '.NET SDK 8.0.4xx' $sdks
Show (((dotnet workload list) -join ' ') -match 'wasm-tools') 'wasm-tools 워크로드' ''

Section '3. 포트 (Windows 기준)'
foreach ($p in 80, 5432, 8121, 8122, 6004) {
    $c = Get-NetTCPConnection -State Listen -LocalPort $p | Select-Object -First 1
    if ($c) { Show $null "포트 $p" "$((Get-Process -Id $c.OwningProcess).ProcessName) / $($c.LocalAddress)" }
    else { Show $null "포트 $p" '리슨 없음' }
}
Write-Host '   (80/5432 = wslrelay 또는 WSL 프로세스, 8121/8122/6004 = app-server·웹(dotnet) 이며 주소가 0.0.0.0 이어야 nginx 가 접근 가능)' -ForegroundColor Gray

Section '4. 연결'
$db = Test-NetConnection localhost -Port 5432 -WarningAction SilentlyContinue
Show $db.TcpTestSucceeded 'DB localhost:5432 (app-server → DB)' ''
function Probe($u) {
    try { (Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 5).StatusCode }
    catch { if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'no response' } }
}
foreach ($u in 'http://localhost:8121/health', 'http://localhost/nginx-health', 'http://localhost/health', 'http://localhost/api/Dock/status', 'http://localhost/') {
    $r = Probe $u; Show ("$r" -eq '200') $u $r
}
Write-Host "`n점검 완료" -ForegroundColor Cyan
