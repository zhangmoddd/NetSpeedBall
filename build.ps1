param(
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"
$assets = Join-Path $root "assets"
$icon = Join-Path $assets "NetSpeedBall.ico"
$source = Join-Path $root "NetSpeedBall.cs"
$manifest = Join-Path $root "app.manifest"
$out = Join-Path $dist "NetSpeedBall.exe"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if ($Clean -and (Test-Path $dist)) {
    Remove-Item -LiteralPath $dist -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

if (-not (Test-Path $compiler)) {
    throw "Cannot find .NET Framework compiler: $compiler"
}

& (Join-Path $root "tools\GenerateIcon.ps1")

# A running instance keeps NetSpeedBall.exe locked; stop it automatically.
$running = Get-Process -Name "NetSpeedBall" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running NetSpeedBall instance..."
    Stop-Process -Name "NetSpeedBall" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}

$ErrorActionPreference = "Continue"
$compilerLog = Join-Path $dist "build-errors.txt"

& $compiler `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /codepage:65001 `
    /win32icon:$icon `
    /win32manifest:$manifest `
    /out:$out `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $source 2> $compilerLog

$ErrorActionPreference = "Stop"

if ($LASTEXITCODE -ne 0) {
    if (Test-Path $compilerLog) {
        Write-Host (Get-Content $compilerLog | Out-String)
    }
    throw "Compiler failed with exit code $LASTEXITCODE"
}

if ((Test-Path $compilerLog) -and ((Get-Item $compilerLog).Length -gt 0)) {
    Write-Host (Get-Content $compilerLog | Out-String)
}

Write-Host "Built $out"
