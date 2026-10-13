# CJ.FTMS.Viewer 'Json' 해결 원인 진단 자료 수집
# 사용: 솔루션 폴더(CJ.SCADA.sln 있는 곳)에서  powershell -ExecutionPolicy Bypass -File .\collect-viewer-diag.ps1
# 결과: viewer-diag.txt (이 파일을 전달)

$ErrorActionPreference = 'Continue'
$root = Get-Location
$proj = 'Clients\CJ.FTMS.Viewer\CJ.FTMS.Viewer.csproj'
$out  = Join-Path $root 'viewer-diag.txt'

function Section($title) { "`r`n==================== $title ====================" | Out-File $out -Append -Encoding utf8 }

"진단 시각: $(Get-Date -Format s)  폴더: $root" | Out-File $out -Encoding utf8

Section '1. SDK'
& dotnet --version 2>&1 | Out-File $out -Append -Encoding utf8
if (Test-Path global.json) { Get-Content global.json | Out-File $out -Append -Encoding utf8 } else { 'global.json 없음' | Out-File $out -Append -Encoding utf8 }

Section '2. CJ.FTMS.Viewer.csproj 전체'
Get-Content $proj | Out-File $out -Append -Encoding utf8

Section '3. Json 을 사용하는 두 파일 (해당 줄)'
Select-String -Path 'Clients\CJ.FTMS.Viewer\Model\partial\JsonFile.cs','Clients\CJ.FTMS.Viewer\Embedded.Resource\EmbeddedResourceLoader.cs' -Pattern 'Json\.|^using|namespace' |
    ForEach-Object { "$($_.Filename):$($_.LineNumber): $($_.Line)" } | Out-File $out -Append -Encoding utf8

Section '4. 솔루션 전체에서 Json 클래스 정의 검색'
Get-ChildItem -Recurse -Include *.cs -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern 'class\s+Json\b' |
    ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" } | Out-File $out -Append -Encoding utf8

Section '5. 상위 폴더의 Directory.Build.props / .targets / Directory.Packages.props'
& {
$d = Get-Item $root
while ($d -ne $null) {
    foreach ($n in 'Directory.Build.props','Directory.Build.targets','Directory.Packages.props') {
        $f = Join-Path $d.FullName $n
        if (Test-Path $f) { "--- $f"; Get-Content $f } 
    }
    $d = $d.Parent
}
} | Out-File $out -Append -Encoding utf8

Section '6. 프로젝트 참조'
& dotnet list $proj reference 2>&1 | Out-File $out -Append -Encoding utf8

Section '7. 실제 컴파일되는 파일 목록 중 Json 관련'
& dotnet msbuild $proj -getItem:Compile 2>&1 | Select-String -Pattern 'Json' | Out-File $out -Append -Encoding utf8

Section '8. 참조되는 어셈블리 중 프로젝트/로컬 DLL'
& dotnet msbuild $proj -getItem:ProjectReference -getItem:Reference 2>&1 |
    Select-String -Pattern '"Identity"|"FullPath"|HintPath' | Out-File $out -Append -Encoding utf8

Section '9. 빌드 (재빌드) 결과'
& dotnet build $proj --no-incremental -v:minimal 2>&1 | Out-File $out -Append -Encoding utf8

"`r`n완료: $out" | Write-Host
