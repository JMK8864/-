# CJ.SCADA.Solution 개발 환경 구성 스크립트 (솔루션 루트에서 관리자 PowerShell로 실행)
#   1) .NET 8 SDK 확인  2) wasm-tools 워크로드  3) 로컬 NuGet 피드 생성  4) restore / build
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

Write-Host '== 1. .NET SDK 확인 (global.json: 8.0.414 이상 8.0.4xx)' -ForegroundColor Cyan
$sdks = & dotnet --list-sdks
$sdks | ForEach-Object { Write-Host "   $_" }
if (-not ($sdks | Where-Object { $_ -match '^8\.0\.4\d\d' })) {
    Write-Host '   .NET 8 SDK(8.0.4xx)가 없습니다. 설치: winget install Microsoft.DotNet.SDK.8' -ForegroundColor Yellow
    exit 1
}
Write-Host "   사용 SDK: $(& dotnet --version)"

Write-Host '== 2. wasm-tools 워크로드 (SDK 8 기준 이름)' -ForegroundColor Cyan
& dotnet workload install wasm-tools
if ($LASTEXITCODE -ne 0) { throw 'wasm-tools 설치 실패 (관리자 권한/인터넷 확인)' }

Write-Host '== 3. 로컬 NuGet 피드(.localfeed) 생성' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path '.localfeed' | Out-Null
$libs = @(
    'Assembies\Assembly.SR.Shared\Assembly.ChatHub.Shared.csproj',
    'Assembies\Assembly.JwtTokenGenerator\Assembly.JwtTokenGenerator.csproj',
    'Assembies\Assembly.LoginManager\Assembly.LoginManager.csproj'
)
foreach ($p in $libs) {
    & dotnet pack $p -c Release -o '.localfeed'
    if ($LASTEXITCODE -ne 0) { throw "pack 실패: $p" }
}
Get-ChildItem '.localfeed' -Filter *.nupkg | ForEach-Object { Write-Host "   $($_.Name)" }

Write-Host '== 4. restore / build' -ForegroundColor Cyan
& dotnet restore 'CJ.SCADA.Build.slnf'
if ($LASTEXITCODE -ne 0) { throw 'restore 실패' }
& dotnet build 'CJ.SCADA.Build.slnf' -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { throw 'build 실패' }

Write-Host '완료. 이제 Visual Studio 2026에서 CJ.SCADA.Build.slnf 를 여세요 (누락 프로젝트 제외).' -ForegroundColor Green
