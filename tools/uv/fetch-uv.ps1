# Downloads the pinned uv release for win-x64 and linux-x64 into tools/uv/<rid>/.
# Usage: powershell -ExecutionPolicy Bypass -File tools/uv/fetch-uv.ps1 [-Version 0.12.19]
# Without -Version it fetches the version in tools/uv/VERSION.
param([string]$Version = "")
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Version) { $Version = (Get-Content (Join-Path $here "VERSION") -TotalCount 1).Trim() }
$base = "https://github.com/astral-sh/uv/releases/download/$Version"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("uv-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $tmp | Out-Null
try {
    # Windows
    $zip = Join-Path $tmp "uv-win.zip"
    Invoke-WebRequest "$base/uv-x86_64-pc-windows-msvc.zip" -OutFile $zip
    Expand-Archive $zip -DestinationPath (Join-Path $tmp "win") -Force
    New-Item -ItemType Directory -Force (Join-Path $here "win-x64") | Out-Null
    Copy-Item (Join-Path $tmp "win\uv.exe") (Join-Path $here "win-x64\uv.exe") -Force

    # Linux
    $tgz = Join-Path $tmp "uv-linux.tar.gz"
    Invoke-WebRequest "$base/uv-x86_64-unknown-linux-gnu.tar.gz" -OutFile $tgz
    New-Item -ItemType Directory -Force (Join-Path $tmp "linux") | Out-Null
    tar -xzf $tgz -C (Join-Path $tmp "linux")
    New-Item -ItemType Directory -Force (Join-Path $here "linux-x64") | Out-Null
    Copy-Item (Join-Path $tmp "linux\uv-x86_64-unknown-linux-gnu\uv") (Join-Path $here "linux-x64\uv") -Force

    Set-Content -Path (Join-Path $here "VERSION") -Value $Version
    Write-Host "uv $Version downloaded."
}
finally {
    Remove-Item -Recurse -Force $tmp
}
