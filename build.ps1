# Builds a self-contained BtNotifier.exe (no .NET needed on the target PC)
# and, if Inno Setup is installed, wraps it into dist\BtNotifier-Setup.exe
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Start clean so no stale files end up in the installer
Remove-Item publish, dist -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish BtNotifier.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Get-Item "publish\BtNotifier.exe"
Write-Host ("Built publish\BtNotifier.exe ({0:N1} MB)" -f ($exe.Length / 1MB))

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host ""
    Write-Host "The standalone exe works on its own."
    Write-Host "For a proper installer: winget install JRSoftware.InnoSetup, then run build.ps1 again."
    exit 0
}

& $iscc installer.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host ""
Write-Host "Done: dist\BtNotifier-Setup.exe"
