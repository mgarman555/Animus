# Launch the FleetView dashboard + every agent group in fleet.json.
# Usage:
#   .\scripts\Start-Fleet.ps1                 launch the whole fleet
#   .\scripts\Start-Fleet.ps1 -Project animus launch just one project's agents
[CmdletBinding()]
param(
  [string]$Project
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$FleetPath = Join-Path $Root "fleet.json"
if (-not (Test-Path $FleetPath)) {
  Write-Error "fleet.json not found. Copy fleet.example.json to fleet.json and edit it."
  exit 1
}
$Fleet = Get-Content $FleetPath -Raw | ConvertFrom-Json
$port = if ($Fleet.port) { $Fleet.port } else { 4700 }
$base = "http://127.0.0.1:$port"

# 1. Install/refresh hooks in every project (idempotent).
node (Join-Path $Root "scripts/install-hooks.js") --all

# 2. Start the dashboard server if it isn't already answering.
$up = $false
try { Invoke-WebRequest "$base/api/state" -UseBasicParsing -TimeoutSec 2 | Out-Null; $up = $true } catch {}
if (-not $up) {
  Write-Host "[FleetView] starting server on $base" -ForegroundColor Green
  Start-Process node -ArgumentList (Join-Path $Root "server/server.js") -WindowStyle Hidden
  Start-Sleep -Seconds 1
}
Start-Process $base

# 3. Choose project(s).
$projects = if ($Project) {
  $Fleet.projects | Where-Object { $_.name -eq $Project }
} else { $Fleet.projects }
if (-not $projects) { Write-Error "No matching projects."; exit 1 }

$startAgent = Join-Path $Root "scripts/Start-Agent.ps1"
$haveWt = [bool](Get-Command wt.exe -ErrorAction SilentlyContinue)

foreach ($proj in $projects) {
  if (-not $proj.agents) { continue }
  if ($haveWt) {
    # One Windows Terminal tab per project; split a pane per agent (cap ~4/tab).
    $parts = @()
    $first = $true
    $count = 0
    foreach ($ag in $proj.agents) {
      $cmd = "powershell -NoExit -File `"$startAgent`" -Project $($proj.name) -Agent $($ag.name)"
      if ($first) {
        $tab = "new-tab --title `"$($proj.name)`""
        if ($proj.color) { $tab += " --tabColor `"$($proj.color)`"" }
        $parts += "$tab $cmd"
        $first = $false
      }
      elseif ($count % 4 -eq 0) {
        $parts += "new-tab --title `"$($proj.name)`" $cmd"
      }
      else {
        $parts += "split-pane -H $cmd"
      }
      $count++
    }
    $wtArgs = ($parts -join " ; ")
    Start-Process wt.exe -ArgumentList $wtArgs
  }
  else {
    # Fallback: one plain PowerShell window per agent.
    foreach ($ag in $proj.agents) {
      Start-Process powershell -ArgumentList "-NoExit", "-File", "`"$startAgent`"", "-Project", $proj.name, "-Agent", $ag.name
    }
  }
}
Write-Host "[FleetView] fleet launched — dashboard at $base" -ForegroundColor Green
