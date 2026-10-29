<#
.SYNOPSIS
  Install (or remove) the II mirror relay as a boot-time scheduled task on the campus relay PC.

.DESCRIPTION
  Run in an ADMIN PowerShell on the relay PC (Houston: 23-ENGM-8Q0XR33, 10.116.17.30), from a
  folder holding this script and mirror_relay.py (Install-Relay.cmd does this for you):

      Set-ExecutionPolicy -Scope Process Bypass
      .\install-mirror-relay.ps1

  Same pattern as OSCEtime's "OSCEtime Relay companion" task on this PC, and deliberately a
  separate task: an II fault must never touch camera privacy. Copies mirror_relay.py to
  C:\relay\ii, registers task "II Mirror Relay" (runs as SYSTEM at boot, restarts a minute after
  a crash, logs to C:\relay\logs\IIMirrorRelay.log), opens TCP 8480 to the sim floor's subnet
  only, then checks that a step can be posted and read back.

  Afterwards point II at it: "server": "http://10.116.17.30:8480/" in stations.json on the
  update share, then re-run Install-Station.ps1 on each room PC so its launcher carries the
  new address. II Command picks it up from stations.json via "+ Add Room".

.PARAMETER AllowFrom
  Who may reach port 8480. Default covers the control PCs and sim rooms seen so far
  (10.116.16.x and 10.116.17.x); widen it if a room PC on another subnet can't connect.

.PARAMETER Uninstall
  Stop and remove the task and firewall rule. C:\relay\ii is left in place.
#>
param(
    [int]$Port = 8480,
    [string]$AllowFrom = "10.116.16.0/23",
    [string]$InstallDir = "C:\relay\ii",
    [string]$LogDir = "C:\relay\logs",
    [switch]$Uninstall
)
$ErrorActionPreference = "Stop"
$name = "II Mirror Relay"
$rule = "II mirror relay ($Port)"

function Find-Python {
    # SYSTEM needs the interpreter's full path, not the per-user py launcher
    $exe = & py -3 -c "import sys; print(sys.executable)" 2>$null
    if (-not $exe -or -not (Test-Path $exe)) {
        $cmd = Get-Command python.exe -ErrorAction SilentlyContinue
        if ($cmd) { $exe = $cmd.Source }
    }
    if (-not $exe) { throw "Python 3 not found" }
    return $exe.Trim()
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this in an ADMIN PowerShell (right-click -> Run as administrator)."
}

if (Get-ScheduledTask $name -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask $name -Confirm:$false
}
Remove-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue
if ($Uninstall) { Write-Host "removed task '$name' and firewall rule; $InstallDir untouched"; exit 0 }

$src = Join-Path $PSScriptRoot "mirror_relay.py"
if (-not (Test-Path $src)) { throw "mirror_relay.py must sit next to this script" }
New-Item -ItemType Directory -Force -Path $InstallDir, $LogDir | Out-Null
Copy-Item $src $InstallDir -Force
$python = Find-Python

$action = New-ScheduledTaskAction -Execute $python -WorkingDirectory $InstallDir `
    -Argument "-I `"$InstallDir\mirror_relay.py`" --port $Port --log `"$LogDir\IIMirrorRelay.log`""
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $name -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) `
    -Settings $settings -User "SYSTEM" -RunLevel Highest `
    -Description "Infirmary Integrated mirror relay: II Command -> sim room monitors, HTTP :$Port" | Out-Null

New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $Port `
    -RemoteAddress $AllowFrom -Action Allow -Profile Domain,Private | Out-Null
Write-Host "firewall: allow inbound TCP $Port from $AllowFrom"

Start-ScheduledTask $name
Start-Sleep -Seconds 3
Write-Host ("{0}: {1}" -f $name, (Get-ScheduledTask $name).State)

# Write + read probe: a relay that can't store and return a step should fail here, not in class
try {
    $base = "http://127.0.0.1:$Port"
    $post = (Invoke-WebRequest -UseBasicParsing "$base/mirror_post.php?accession=INSTALLPROBE&key_access=A&key_edit=E&patient=probe&updated=2026/01/01 00:00:00").Content.Trim()
    $get = (Invoke-WebRequest -UseBasicParsing "$base/mirror_get.php?accession=INSTALLPROBE&accesshash=A").Content
    if ($post -notin "ENTRY_ADDED", "ENTRY_UPDATED" -or $get -notmatch "probe") { throw "post=$post get=$get" }
    Write-Host "probe: post and read back OK"
} catch {
    Write-Warning "probe failed: $_  (see $LogDir\IIMirrorRelay.log)"
}
Write-Host "Point II at http://<this PC>:$Port/  ·  manage: Task Scheduler > '$name' (Restart-ScheduledTask)"
