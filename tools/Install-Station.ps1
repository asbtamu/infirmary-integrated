<#
  One-time setup of a sim room PC, run there while signed in as the account that uses the room:

    powershell -ExecutionPolicy Bypass -File "H:\Sim Techs\updates\ii\Install-Station.ps1" -Station SIM1

  1. Installs II:SIM from the share's current package into C:\Infirmary Integrated\II-SIM
     (writable by signed-in users, so later builds install themselves at startup -- Updater.cs).
  2. Puts "II Room.cmd" in this account's Startup folder: at sign-in II:SIM opens fullscreen on
     the patient monitor and joins the station's session (credentials from stations.json), then
     follows II Command whenever that room is broadcasting.

  Re-run it to change the station, or after credentials change in stations.json.
#>
param(
    [Parameter(Mandatory)] [string] $Station,
    [string] $Share,
    [string] $Install = 'C:\Infirmary Integrated\II-SIM'
)
$ErrorActionPreference = 'Stop'
if (-not $Share) { $Share = Split-Path -Parent $MyInvocation.MyCommand.Path }   # PS 5.1: $PSScriptRoot is empty in param defaults

$stations = Get-Content (Join-Path $Share 'stations.json') -Raw | ConvertFrom-Json
$st = $stations.stations.PSObject.Properties | Where-Object { $_.Name -eq $Station } | Select-Object -First 1 -ExpandProperty Value
if (-not $st) { throw "No station '$Station' in stations.json. Known: $($stations.stations.PSObject.Properties.Name -join ', ')" }
$server = if ($stations.server) { $stations.server } else { 'http://server.infirmary-integrated.com/' }

# Launcher arguments go through cmd: refuse anything it would reinterpret
foreach ($v in $st.accession, $st.passwordAccess, $server) {
    if ($v -match '["%^&|<>]') { throw "stations.json value '$v' contains a character a launcher can't carry" }
}

$pkg = (Get-Content (Join-Path $Share 'manifest.json') -Raw | ConvertFrom-Json).sim
$zip = Join-Path $Share $pkg.package
if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $pkg.sha256) { throw "SHA-256 mismatch for $($pkg.package); re-copy it to the share" }

$exe = Join-Path $Install 'infirmary-integrated.exe'
Get-Process infirmary-integrated -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force

if (Test-Path $Install) { Remove-Item $Install -Recurse -Force }
New-Item -ItemType Directory -Force $Install | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $Install)

# Built-in Users may modify: the updater renames this folder when a newer build arrives
icacls (Split-Path $Install -Parent) /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null

$launcher = Join-Path ([Environment]::GetFolderPath('Startup')) 'II Room.cmd'
@(
    '@echo off'
    "rem Infirmary Integrated room launcher: $($st.name) ($Station), from Install-Station.ps1"
    "start `"`" `"$exe`" --receive --accession `"$($st.accession)`" --password `"$($st.passwordAccess)`" --server `"$server`" --monitor --fullscreen"
) | Set-Content -Path $launcher -Encoding ascii

Write-Host "Installed II:SIM $($pkg.version) to $Install"
Write-Host "Launcher: $launcher"
Write-Host "Sign out and back in (or run the launcher) to connect as $($st.name)."
