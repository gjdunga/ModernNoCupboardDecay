#requires -Version 5.1
<#
.SYNOPSIS
  Fetch the Oxide/Rust/Unity reference assemblies the build chain compiles against.

.DESCRIPTION
  Installs a Rust dedicated server (Steam app 258550 - free, anonymous) and
  overlays the latest Oxide.Rust release, leaving the assemblies under
  references\RustDedicated_Data\Managed.

  These are proprietary game files. They are NOT committed to the repository
  (see .gitignore); every developer / CI run fetches its own copy.

.PARAMETER ManagedOnly
  After installing, delete everything except RustDedicated_Data\Managed. Shrinks
  ~8GB to a few dozen MB. The full server is not needed to compile.

.PARAMETER Dir
  Install location (default: <repo>\references).

.EXAMPLE
  tools\fetch-references.ps1
.EXAMPLE
  tools\fetch-references.ps1 -ManagedOnly
#>
[CmdletBinding()]
param(
  [switch]$ManagedOnly,
  [string]$Dir
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$AppId       = 258550
$OxideUrl    = 'https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust.zip'
$SteamCmdUrl = 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Dir) { $Dir = Join-Path $repoRoot 'references' }
$steamDir   = Join-Path $repoRoot '.steamcmd'
$managedDir = Join-Path $Dir 'RustDedicated_Data\Managed'
$steamExe   = Join-Path $steamDir 'steamcmd.exe'

New-Item -ItemType Directory -Force -Path $steamDir | Out-Null
if (-not (Test-Path $steamExe)) {
  Write-Host "==> Installing SteamCMD into $steamDir"
  $zip = Join-Path $env:TEMP 'steamcmd.zip'
  Invoke-WebRequest -Uri $SteamCmdUrl -OutFile $zip
  Expand-Archive -Path $zip -DestinationPath $steamDir -Force
  Remove-Item $zip
}

Write-Host "==> Downloading / updating Rust dedicated server (app $AppId)"
Write-Host "    into $Dir (this is large on first run)"
New-Item -ItemType Directory -Force -Path $Dir | Out-Null
# force_install_dir must precede login per SteamCMD's argument ordering.
& $steamExe +force_install_dir $Dir +login anonymous +app_update $AppId validate +quit
if ($LASTEXITCODE -ne 0) { throw "SteamCMD exited with code $LASTEXITCODE" }

Write-Host "==> Overlaying latest Oxide.Rust"
$oxZip = Join-Path $env:TEMP 'Oxide.Rust.zip'
Invoke-WebRequest -Uri $OxideUrl -OutFile $oxZip
Expand-Archive -Path $oxZip -DestinationPath $Dir -Force
Remove-Item $oxZip

if (-not (Test-Path $managedDir)) {
  throw "Expected managed directory not found at $managedDir. The Steam download or Oxide overlay did not complete."
}

if ($ManagedOnly) {
  Write-Host "==> Pruning everything except RustDedicated_Data\Managed"
  Get-ChildItem -Force $Dir |
    Where-Object { $_.Name -ne 'RustDedicated_Data' } |
    Remove-Item -Recurse -Force
  Get-ChildItem -Force (Join-Path $Dir 'RustDedicated_Data') |
    Where-Object { $_.Name -ne 'Managed' } |
    Remove-Item -Recurse -Force
}

Write-Host ""
Write-Host "Done. Reference assemblies are in: $managedDir"
Write-Host "Build with:  dotnet build build\ModernNoCupboardDecay.csproj -c Release"
