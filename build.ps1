<#
.SYNOPSIS
  Tests, publishes and packages SnapFloat.
.DESCRIPTION
  Produces in .\artifacts:
    publish\                               self-contained build (no .NET install needed)
    SnapFloat-<ver>-win-x64-portable.zip   portable copy of the above
    SnapFloat-Setup-<ver>.exe              per-user installer (Inno Setup; downloaded automatically from NuGet)
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build.ps1
#>
param(
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$SkipInstaller
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

if (-not $SkipTests) {
    Step "Running tests"
    dotnet test (Join-Path $root "tests\SnapFloat.Core.Tests") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Step "Publishing self-contained win-x64 build"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "src\SnapFloat\SnapFloat.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Step "Creating portable zip"
$zip = Join-Path $artifacts "SnapFloat-$Version-win-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Copy-Item (Join-Path $root "LICENSE") (Join-Path $publish "LICENSE.txt") -Force
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal

if (-not $SkipInstaller) {
    Step "Building installer"
    $innoVersion = "6.4.3"
    $innoDir = Join-Path $root "tools\.cache\innosetup-$innoVersion"
    $iscc = Join-Path $innoDir "tools\ISCC.exe"
    if (-not (Test-Path $iscc)) {
        New-Item -ItemType Directory -Force $innoDir | Out-Null
        $pkg = Join-Path $innoDir "pkg.zip"
        Invoke-WebRequest "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$innoVersion" -OutFile $pkg
        Expand-Archive $pkg -DestinationPath $innoDir -Force
        Remove-Item $pkg
    }
    & $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $root "installer\SnapFloat.iss")
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
}

Step "Done"
Get-ChildItem $artifacts -File | ForEach-Object { "{0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
