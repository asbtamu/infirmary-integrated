<#
  Builds update packages for the share that II:SIM and II Command update from at startup
  (II Library\Classes\Updater.cs). Output is a folder to copy, as-is, onto
  H:\Sim Techs\updates\ii -- e.g. write it to a USB drive with -Out E:\ii.

    tools\Publish-Update.ps1                         # both apps, into .\updates-out
    tools\Publish-Update.ps1 -Product sim -Out E:\ii

  Bump <Version> in the app's .csproj first: stations only take a build whose version is
  higher than their own. The version is read back from the built dll, so the manifest can
  never disagree with what the station will report.

  Copy the zip(s) onto the share BEFORE manifest.json (or copy the folder in one go): a
  station acts only on the manifest, so a manifest that arrives first points at a missing
  or half-copied zip -- harmless (hash mismatch, logged, retried next launch), but noisy.
#>
param(
    [ValidateSet('sim', 'command', 'all')] [string] $Product = 'all',
    [string] $Out = (Join-Path (Get-Location) 'updates-out')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$apps = @{
    sim     = @{ Project = 'II Simulator\II Simulator.csproj'; Dll = 'infirmary-integrated.dll';         Name = 'II-SIM' }
    command = @{ Project = 'II Command\II Command.csproj';     Dll = 'infirmary-integrated-command.dll'; Name = 'II-Command' }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force $Out | Out-Null

$manifestPath = Join-Path $Out 'manifest.json'
$manifest = @{}
if (Test-Path $manifestPath) {
    (Get-Content $manifestPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $manifest[$_.Name] = $_.Value }
}

foreach ($key in $(if ($Product -eq 'all') { 'sim', 'command' } else { $Product })) {
    $app = $apps[$key]
    $pub = Join-Path ([IO.Path]::GetTempPath()) "ii-publish-$key"
    if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }

    dotnet publish (Join-Path $root $app.Project) -c Release -r win-x64 --self-contained false -o $pub
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $key" }

    $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $pub $app.Dll)).Version.ToString(3)
    $zipName = "$($app.Name)_$version.zip"
    $zip = Join-Path $Out $zipName
    if (Test-Path $zip) { Remove-Item $zip -Force }

    [IO.Compression.ZipFile]::CreateFromDirectory($pub, $zip)
    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash

    $manifest[$key] = [ordered]@{ version = $version; package = $zipName; sha256 = $sha }
    Remove-Item $pub -Recurse -Force
    Write-Host "$key $version -> $zipName"
}

Copy-Item (Join-Path $PSScriptRoot 'Install-Station.ps1') $Out -Force   # Room PC setup runs from the share

# Written last and atomically: a station never sees a manifest for a package that isn't fully there
$tmp = "$manifestPath.tmp"
$manifest | ConvertTo-Json | Set-Content -Path $tmp -Encoding ascii
Move-Item $tmp $manifestPath -Force
Write-Host "Copy the contents of $Out to H:\Sim Techs\updates\ii"
