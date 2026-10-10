# Builds a release zip: the app published for one platform (framework-dependent), with las, the workers,
# uv for that platform, the HartsyInference and internal workers and the example project. The CI release workflow runs it;
# it also runs locally (Windows PowerShell 5.1 or pwsh).
# Usage: powershell -ExecutionPolicy Bypass -File build/release.ps1 [-Rid win-x64|linux-x64] [-Version 0.1.0]
param(
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Rid = $(if ($IsLinux) { "linux-x64" } else { "win-x64" }),
    # Defaults to the <Version> in Directory.Build.props.
    [string]$Version = "",
    [string]$OutDir = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) { $OutDir = Join-Path $root "artifacts" }
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
}

# uv isn't in the repository; tools/uv/fetch-uv.ps1 (or .sh) downloads it.
$uvExe = if ($Rid -eq "win-x64") { "uv.exe" } else { "uv" }
if (-not (Test-Path (Join-Path $root "tools/uv/$Rid/$uvExe"))) {
    throw "uv for $Rid is missing. Run tools/uv/fetch-uv.ps1 (or tools/uv/fetch-uv.sh) first."
}

$name = "LocalAudiobookStudio-$Version-$Rid"
$stage = Join-Path $OutDir $name
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
if (Test-Path $zip) { Remove-Item -Force $zip }

dotnet publish (Join-Path $root "src/LocalAudiobookStudio.App/LocalAudiobookStudio.App.csproj") `
    -c Release -r $Rid --self-contained false -p:Version=$Version -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Only this platform's uv, no debug symbols (the Hartsy worker embeds its own).
$otherRid = if ($Rid -eq "win-x64") { "linux-x64" } else { "win-x64" }
Remove-Item -Recurse -Force (Join-Path $stage "tools/uv/$otherRid") -ErrorAction SilentlyContinue
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force
Copy-Item (Join-Path $root "LICENSE.txt"), (Join-Path $root "THIRD-PARTY-NOTICES.md"), (Join-Path $root "README.md") $stage

if ($Rid -eq "linux-x64") {
    # Git on Windows may not keep the executable bit; zip (Info-ZIP) stores it, so unzip restores it.
    chmod +x "$stage/LocalAudiobookStudio" "$stage/las" "$stage/tools/hartsy/las-hartsy-worker" "$stage/tools/internal-worker/las-internal-worker" "$stage/tools/uv/linux-x64/uv"
    Push-Location $OutDir
    try { zip -qry "$name.zip" $name; if ($LASTEXITCODE -ne 0) { throw "zip failed" } }
    finally { Pop-Location }
}
else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
}

# <zip>.sha256 in sha256sum format, the convention ToolInstaller.DownloadVerifiedAsync reads (url + ".sha256").
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n")

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Release: $zip ($size MB)"
